using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Memory;

/// <summary>
/// What kind of thing a memory is. The distinction matters: a confirmed preference is
/// treated as truth, a learned pattern or an assumption is only a hint.
/// </summary>
public static class MemoryKinds
{
    public const string Fact = "fact";
    public const string Preference = "preference";
    public const string Person = "person";
    public const string Project = "project";
    public const string Context = "context";      // temporary, usually with an expiry
    public const string Pattern = "pattern";      // learned behaviour, unconfirmed until the user says so
    public const string Assumption = "assumption"; // derived guess

    public static readonly string[] All = [Fact, Preference, Person, Project, Context, Pattern, Assumption];
}

/// <summary>Where a memory came from.</summary>
public static class MemorySources
{
    public const string UserExplicit = "user";      // the user told JARVIS directly
    public const string UserConfirmed = "confirmed"; // JARVIS proposed it and the user confirmed
    public const string Learned = "learned";        // inferred from behaviour
    public const string Derived = "derived";        // concluded by the AI from other information

    public static readonly string[] All = [UserExplicit, UserConfirmed, Learned, Derived];
}

public sealed record MemoryItem(
    string Id,
    string Kind,
    string Content,
    string? Subject,
    string Source,
    double Confidence,
    string? Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    int UseCount)
{
    /// <summary>Why and how this memory was stored.</summary>
    public MemoryProvenance? Provenance { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    /// <summary>Told or confirmed by the user (a fact) rather than inferred (a hint).</summary>
    public bool IsConfirmed => Source is MemorySources.UserExplicit or MemorySources.UserConfirmed;
}

/// <summary>
/// Where a memory came from: the surface ("chat", "voice", "dashboard", "learner", "ai"), the
/// conversation and request that produced it, the user's own words, and for inferences the reason.
/// </summary>
public sealed record MemoryProvenance(string Via, string? ConversationId = null, string? TurnId = null, string? Quote = null, string? Reason = null, string? Tool = null);

public sealed record NewMemory(
    string Content,
    string Kind = MemoryKinds.Fact,
    string? Subject = null,
    string Source = MemorySources.UserExplicit,
    double? Confidence = null,
    string? Tags = null,
    DateTimeOffset? ExpiresAt = null,
    MemoryProvenance? Provenance = null);

public sealed record MemoryUpdate(
    string? Content = null,
    string? Kind = null,
    string? Subject = null,
    string? Source = null,
    double? Confidence = null,
    string? Tags = null);

/// <summary>Local, searchable personal memory backed by SQLite FTS5.</summary>
public sealed partial class MemoryStore(JarvisDatabase db, IEventBus events)
{
    public MemoryItem Add(NewMemory m)
    {
        if (string.IsNullOrWhiteSpace(m.Content)) throw new ArgumentException("Memory content is empty.");
        if (!MemoryKinds.All.Contains(m.Kind)) throw new ArgumentException($"Unknown memory kind '{m.Kind}'.");
        if (!MemorySources.All.Contains(m.Source)) throw new ArgumentException($"Unknown memory source '{m.Source}'.");

        // Avoid storing the exact same statement twice; refresh it instead.
        var existing = FindExact(m.Content);
        if (existing is not null)
        {
            return Update(existing.Id, new MemoryUpdate(Kind: m.Kind, Subject: m.Subject ?? existing.Subject,
                Source: Stronger(existing.Source, m.Source), Confidence: Math.Max(existing.Confidence, DefaultConfidence(m))))!;
        }

        var id = Guid.NewGuid().ToString("n");
        var now = JarvisDatabase.Now();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO memories (id, kind, content, subject, source, confidence, tags, search_text, created_at, updated_at, expires_at, provenance, confirmed_at)
                VALUES ($id, $kind, $content, $subject, $source, $conf, $tags, $search, $now, $now, $exp, $prov, $confirmed);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", m.Kind);
            cmd.Parameters.AddWithValue("$content", m.Content.Trim());
            cmd.Parameters.AddWithValue("$subject", (object?)m.Subject?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", m.Source);
            cmd.Parameters.AddWithValue("$conf", DefaultConfidence(m));
            cmd.Parameters.AddWithValue("$tags", (object?)m.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$search", SearchText(m.Content, m.Subject, m.Tags));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$exp", m.ExpiresAt is { } e ? JarvisDatabase.Format(e) : DBNull.Value);
            cmd.Parameters.AddWithValue("$prov", m.Provenance is null ? DBNull.Value : JsonSerializer.Serialize(m.Provenance, ProvJson));
            cmd.Parameters.AddWithValue("$confirmed", m.Source == MemorySources.UserConfirmed ? now : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "added", id });
        return Get(id)!;
    }

    public MemoryItem? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public MemoryItem? Update(string id, MemoryUpdate u)
    {
        var current = Get(id);
        if (current is null) return null;
        var content = u.Content?.Trim() ?? current.Content;
        var kind = u.Kind ?? current.Kind;
        var source = u.Source ?? current.Source;
        if (!MemoryKinds.All.Contains(kind)) throw new ArgumentException($"Unknown memory kind '{kind}'.");
        if (!MemorySources.All.Contains(source)) throw new ArgumentException($"Unknown memory source '{source}'.");
        var subject = u.Subject ?? current.Subject;
        var tags = u.Tags ?? current.Tags;

        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE memories SET kind=$kind, content=$content, subject=$subject, source=$source, confidence=$conf,
                    tags=$tags, search_text=$search, updated_at=$now WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$conf", Math.Clamp(u.Confidence ?? current.Confidence, 0, 1));
            cmd.Parameters.AddWithValue("$tags", (object?)tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$search", SearchText(content, subject, tags));
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "updated", id });
        return Get(id);
    }

    /// <summary>The user confirmed an inferred memory: it becomes a fact JARVIS may rely on.</summary>
    public MemoryItem? Confirm(string id)
    {
        var current = Get(id);
        if (current is null) return null;
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE memories SET source = $s, confidence = 1.0, confirmed_at = $now, updated_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", current.Source == MemorySources.UserExplicit ? MemorySources.UserExplicit : MemorySources.UserConfirmed);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "confirmed", id });
        return Get(id);
    }

    public bool Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id; DELETE FROM memory_entities WHERE memory_id = $id; DELETE FROM embeddings WHERE owner_type = 'memory' AND owner_id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var deleted = cmd.ExecuteNonQuery() > 0;
        if (deleted) events.Publish(EventTypes.MemoryChanged, new { action = "deleted", id });
        return deleted;
    }

    /// <summary>Delete all memories, or all of one kind.</summary>
    public int Clear(string? kind = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memory_entities WHERE memory_id IN (SELECT id FROM memories WHERE ($kind IS NULL OR kind = $kind));
            DELETE FROM embeddings WHERE owner_type = 'memory' AND owner_id IN (SELECT id FROM memories WHERE ($kind IS NULL OR kind = $kind));
            DELETE FROM memories WHERE ($kind IS NULL OR kind = $kind);
            """;
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        var n = cmd.ExecuteNonQuery();
        events.Publish(EventTypes.MemoryChanged, new { action = "cleared", kind, count = n });
        return n;
    }

    public IReadOnlyList<MemoryItem> List(string? kind = null, int limit = 200, int offset = 0, bool? confirmed = null)
    {
        PurgeExpired();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM memories WHERE ($kind IS NULL OR kind = $kind)
              AND ($confirmed IS NULL OR ($confirmed = 1 AND source IN ('user','confirmed')) OR ($confirmed = 0 AND source NOT IN ('user','confirmed')))
            ORDER BY updated_at DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$confirmed", confirmed is null ? DBNull.Value : confirmed.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        return ReadAll(cmd);
    }

    /// <summary>Full-text search over content, subject and tags (Arabic-normalized, prefix matching).</summary>
    public IReadOnlyList<MemoryItem> Search(string query, int limit = 20, string? kind = null)
    {
        PurgeExpired();
        var tokens = Tokens(query);
        if (tokens.Count == 0) return List(kind, limit);

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns.Replace("id,", "m.id,")} FROM memories_fts f JOIN memories m ON m.rowid = f.rowid
            WHERE memories_fts MATCH $q AND ($kind IS NULL OR m.kind = $kind)
            ORDER BY bm25(memories_fts) LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$q", string.Join(" OR ", tokens.Select(t => $"\"{t}\"*")));
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        return ReadAll(cmd);
    }

    /// <summary>Record that memories were used to answer something (feeds relevance ranking).</summary>
    public void MarkUsed(IEnumerable<string> ids)
    {
        using var conn = db.Open();
        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE memories SET use_count = use_count + 1, last_used_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memories;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Memories by id, in the given order (missing ids are skipped).</summary>
    public IReadOnlyList<MemoryItem> GetMany(IEnumerable<string> ids)
    {
        var list = new List<MemoryItem>();
        foreach (var id in ids) if (Get(id) is { } m) list.Add(m);
        return list;
    }

    public (int Total, int Confirmed, int Inferred) Counts()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN source IN ('user','confirmed') THEN 1 ELSE 0 END), 0) FROM memories;";
        using var r = cmd.ExecuteReader();
        r.Read();
        var total = r.GetInt32(0);
        var confirmed = r.GetInt32(1);
        return (total, confirmed, total - confirmed);
    }

    private MemoryItem? FindExact(string content)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM memories WHERE content = $c LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", content.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private void PurgeExpired()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE expires_at IS NOT NULL AND expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    private static double DefaultConfidence(NewMemory m) => m.Confidence ?? m.Source switch
    {
        MemorySources.UserExplicit or MemorySources.UserConfirmed => 1.0,
        MemorySources.Learned => 0.5,
        _ => 0.4,
    };

    private static string Stronger(string a, string b)
    {
        static int Rank(string s) => s switch
        {
            MemorySources.UserExplicit => 3, MemorySources.UserConfirmed => 2, MemorySources.Learned => 1, _ => 0,
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    internal static string SearchText(string content, string? subject, string? tags) =>
        TextNormalizer.Normalize($"{subject} {content} {tags}");

    internal static List<string> Tokens(string query) =>
        WordRegex().Matches(TextNormalizer.Normalize(query))
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()
            .Take(12)
            .ToList();

    private static readonly HashSet<string> StopWords =
    [
        "the", "a", "an", "is", "are", "was", "of", "to", "in", "on", "for", "and", "or", "my", "me", "you", "your",
        "what", "who", "do", "does", "about", "that", "this", "it", "i", "we", "be", "with", "know", "remember",
        "انا", "انت", "ده", "دي", "في", "من", "علي", "عن", "ايه", "اللي", "هو", "هي", "يا", "و", "ان", "مع",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    private const string Columns =
        "id, kind, content, subject, source, confidence, tags, created_at, updated_at, expires_at, last_used_at, use_count, provenance, confirmed_at";

    private static readonly JsonSerializerOptions ProvJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static List<MemoryItem> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<MemoryItem>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static MemoryItem Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetDouble(5), r.IsDBNull(6) ? null : r.GetString(6),
        JarvisDatabase.Parse(r.GetString(7)), JarvisDatabase.Parse(r.GetString(8)),
        r.IsDBNull(9) ? null : JarvisDatabase.Parse(r.GetString(9)),
        r.IsDBNull(10) ? null : JarvisDatabase.Parse(r.GetString(10)),
        r.GetInt32(11))
    {
        Provenance = r.IsDBNull(12) ? null : TryProvenance(r.GetString(12)),
        ConfirmedAt = r.IsDBNull(13) ? null : JarvisDatabase.Parse(r.GetString(13)),
    };

    private static MemoryProvenance? TryProvenance(string json)
    {
        try { return JsonSerializer.Deserialize<MemoryProvenance>(json, ProvJson); }
        catch (JsonException) { return null; }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Memory;

/// <summary>Kinds of things JARVIS knows about.</summary>
public static class EntityTypes
{
    public const string Person = "person";
    public const string Organization = "organization";
    public const string Project = "project";
    public const string Place = "place";
    public const string File = "file";
    public const string Event = "event";
    public const string Topic = "topic";

    public static readonly string[] All = [Person, Organization, Project, Place, File, Event, Topic];
}

public sealed record Entity(string Id, string Type, string Name, IReadOnlyList<string> Aliases, string? Notes, string Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A directed relationship, e.g. (Ahmed) works_at (CityCrep).</summary>
public sealed record Relation(string Id, string FromId, string FromName, string Type, string ToId, string ToName, string Source, double Confidence, MemoryProvenance? Provenance, DateTimeOffset CreatedAt);

/// <summary>
/// People, organisations, projects and other things, and how they relate ("Ahmed works at CityCrep").
/// Memories link to the entities they mention, so "what do you know about CityCrep" can gather
/// everything connected to it.
/// </summary>
public sealed partial class EntityStore(JarvisDatabase db, IEventBus events)
{
    public static string Normalize(string name) => TextNormalizer.Normalize(name).Trim();

    /// <summary>Finds an entity by name or alias (any type unless given), or creates it.</summary>
    public Entity Upsert(string type, string name, string source = MemorySources.UserExplicit)
    {
        if (!EntityTypes.All.Contains(type)) throw new ArgumentException($"Unknown entity type '{type}'.");
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Entity name is empty.");
        var existing = Find(name, type) ?? (type == EntityTypes.Topic ? Find(name) : null);
        if (existing is not null)
        {
            // A topic that turns out to be a person/organisation gets the more specific type.
            if (existing.Type == EntityTypes.Topic && type != EntityTypes.Topic) SetType(existing.Id, type);
            return Get(existing.Id)!;
        }
        // Upgrade a topic of the same name instead of creating a duplicate.
        var topic = Find(name, EntityTypes.Topic);
        if (topic is not null)
        {
            SetType(topic.Id, type);
            return Get(topic.Id)!;
        }

        var id = Guid.NewGuid().ToString("n");
        var now = JarvisDatabase.Now();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO entities (id, type, name, norm, aliases, source, created_at, updated_at) VALUES ($id, $t, $n, $norm, '[]', $s, $now, $now);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$norm", Normalize(name));
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "entity", id });
        return Get(id)!;
    }

    public Entity? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM entities WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Exact match on the normalized name or any alias.</summary>
    public Entity? Find(string name, string? type = null)
    {
        var norm = Normalize(name);
        if (norm.Length == 0) return null;
        return List(type).FirstOrDefault(e => Normalize(e.Name) == norm || e.Aliases.Any(a => Normalize(a) == norm));
    }

    public IReadOnlyList<Entity> List(string? type = null, string? query = null, int limit = 500)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM entities WHERE ($t IS NULL OR type = $t) AND ($q IS NULL OR norm LIKE $q OR aliases LIKE $q) ORDER BY updated_at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$t", (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$q", string.IsNullOrWhiteSpace(query) ? DBNull.Value : $"%{Normalize(query)}%");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var r = cmd.ExecuteReader();
        var list = new List<Entity>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public Entity? Update(string id, string? name = null, string? type = null, IReadOnlyList<string>? aliases = null, string? notes = null)
    {
        var cur = Get(id);
        if (cur is null) return null;
        if (type is not null && !EntityTypes.All.Contains(type)) throw new ArgumentException($"Unknown entity type '{type}'.");
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE entities SET name=$n, norm=$norm, type=$t, aliases=$a, notes=$notes, updated_at=$now WHERE id=$id;";
            var n = name?.Trim() is { Length: > 0 } nn ? nn : cur.Name;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", n);
            cmd.Parameters.AddWithValue("$norm", Normalize(n));
            cmd.Parameters.AddWithValue("$t", type ?? cur.Type);
            cmd.Parameters.AddWithValue("$a", JsonSerializer.Serialize((aliases ?? cur.Aliases).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct()));
            cmd.Parameters.AddWithValue("$notes", (object?)(notes ?? cur.Notes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "entity", id });
        return Get(id);
    }

    public bool Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memory_entities WHERE entity_id = $id;
            DELETE FROM relations WHERE from_id = $id OR to_id = $id;
            DELETE FROM entities WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var n = cmd.ExecuteNonQuery();
        if (n > 0) events.Publish(EventTypes.MemoryChanged, new { action = "entity-deleted", id });
        return n > 0;
    }

    public void Link(string memoryId, string entityId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO memory_entities (memory_id, entity_id) VALUES ($m, $e);";
        cmd.Parameters.AddWithValue("$m", memoryId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Entity> EntitiesOf(string memoryId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols.Replace("id,", "e.id,")} FROM memory_entities l JOIN entities e ON e.id = l.entity_id WHERE l.memory_id = $m ORDER BY e.name;";
        cmd.Parameters.AddWithValue("$m", memoryId);
        using var r = cmd.ExecuteReader();
        var list = new List<Entity>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public IReadOnlyList<string> MemoryIdsOf(string entityId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT memory_id FROM memory_entities WHERE entity_id = $e;";
        cmd.Parameters.AddWithValue("$e", entityId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public Relation Relate(string fromId, string relation, string toId, string source, double? confidence = null, MemoryProvenance? provenance = null)
    {
        relation = RelationName(relation);
        if (fromId == toId) throw new ArgumentException("An entity can't relate to itself.");
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO relations (id, from_id, relation, to_id, source, confidence, provenance, created_at)
                VALUES ($id, $f, $r, $t, $s, $c, $p, $now)
                ON CONFLICT(from_id, relation, to_id) DO UPDATE SET
                    source = CASE WHEN excluded.source IN ('user','confirmed') THEN excluded.source ELSE relations.source END,
                    confidence = MAX(relations.confidence, excluded.confidence);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
            cmd.Parameters.AddWithValue("$f", fromId);
            cmd.Parameters.AddWithValue("$r", relation);
            cmd.Parameters.AddWithValue("$t", toId);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$c", confidence ?? (source is MemorySources.UserExplicit or MemorySources.UserConfirmed ? 1.0 : 0.5));
            cmd.Parameters.AddWithValue("$p", provenance is null ? DBNull.Value : JsonSerializer.Serialize(provenance, JsonOpts));
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.MemoryChanged, new { action = "relation", fromId, toId });
        return RelationsOf(fromId).First(r => r.ToId == toId && r.Type == relation);
    }

    public bool DeleteRelation(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM relations WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) events.Publish(EventTypes.MemoryChanged, new { action = "relation-deleted", id });
        return ok;
    }

    /// <summary>Relations where the entity is on either side.</summary>
    public IReadOnlyList<Relation> RelationsOf(string entityId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.from_id, f.name, r.relation, r.to_id, t.name, r.source, r.confidence, r.provenance, r.created_at
            FROM relations r JOIN entities f ON f.id = r.from_id JOIN entities t ON t.id = r.to_id
            WHERE r.from_id = $e OR r.to_id = $e ORDER BY r.created_at;
            """;
        cmd.Parameters.AddWithValue("$e", entityId);
        using var r = cmd.ExecuteReader();
        var list = new List<Relation>();
        while (r.Read())
        {
            list.Add(new Relation(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDouble(7),
                r.IsDBNull(8) ? null : JsonSerializer.Deserialize<MemoryProvenance>(r.GetString(8), JsonOpts), JarvisDatabase.Parse(r.GetString(9))));
        }
        return list;
    }

    /// <summary>Entities whose name (or alias) appears as whole words in the text.</summary>
    public IReadOnlyList<Entity> Mentioned(string text)
    {
        var norm = " " + string.Join(' ', WordRegex().Matches(Normalize(text)).Select(m => m.Value)) + " ";
        return List().Where(e => new[] { e.Name }.Concat(e.Aliases)
                .Select(n => " " + string.Join(' ', WordRegex().Matches(Normalize(n)).Select(m => m.Value)) + " ")
                .Any(n => n.Trim().Length >= 2 && norm.Contains(n, StringComparison.Ordinal)))
            .ToList();
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entities;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>"works at", "Works_At" → "works_at"; keeps relation names consistent.</summary>
    public static string RelationName(string relation)
    {
        var r = Regex.Replace(relation.Trim().ToLowerInvariant(), @"[\s\-]+", "_");
        if (r.Length is 0 or > 40) throw new ArgumentException("Invalid relation name.");
        return r;
    }

    private void SetType(string id, string type)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE entities SET type = $t, updated_at = $now WHERE id = $id AND NOT EXISTS (SELECT 1 FROM entities o WHERE o.type = $t AND o.norm = entities.norm AND o.id <> entities.id);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    private const string Cols = "id, type, name, aliases, notes, source, created_at, updated_at";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Entity Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? [] : JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [],
        r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
        JarvisDatabase.Parse(r.GetString(6)), JarvisDatabase.Parse(r.GetString(7)));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}

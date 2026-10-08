using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Files;

public sealed record IndexedFile
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Ext { get; init; }
    public required string Kind { get; init; }
    public long Size { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
    public string Status { get; init; } = "ok";
    public string? Note { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public int? Pages { get; init; }
    public string? Project { get; init; }
    public int TextChars { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record FileHit(IndexedFile File, string? Snippet, double Score, bool Semantic);

/// <summary>
/// The local file knowledge index: one row per file (metadata, extraction status) and its text split
/// into chunks with an Arabic-normalised full-text index; chunks can also be embedded for meaning-based
/// search. Nothing leaves the machine.
/// </summary>
public sealed partial class FileIndex(JarvisDatabase db, EntityStore entities, SemanticIndex semantic, IEventBus events)
{
    public const string ChunkOwner = "file_chunk";
    public const int ChunkChars = 1200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Unchanged since it was indexed (same size and modification time)?</summary>
    public bool IsCurrent(string path, long size, DateTimeOffset modified)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT size, modified_at FROM files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetInt64(0) == size && JarvisDatabase.Parse(r.GetString(1)).ToUnixTimeSeconds() == modified.ToUnixTimeSeconds();
    }

    public IndexedFile Upsert(FileInfo info, ExtractedDocument doc, string? project)
    {
        var existing = GetByPath(info.FullName);
        var id = existing?.Id ?? Guid.NewGuid().ToString("n");
        var chunks = Chunk(doc.Text);
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM file_chunks WHERE file_id = $id; DELETE FROM file_entities WHERE file_id = $id;";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files (id, path, name, ext, kind, size, modified_at, created_at, indexed_at, status, note, title, author, pages, project, text_chars, metadata, name_search)
                    VALUES ($id, $path, $name, $ext, $kind, $size, $mod, $created, $now, $status, $note, $title, $author, $pages, $project, $chars, $meta, $ns)
                    ON CONFLICT(path) DO UPDATE SET name=excluded.name, ext=excluded.ext, kind=excluded.kind, size=excluded.size, modified_at=excluded.modified_at,
                        indexed_at=excluded.indexed_at, status=excluded.status, note=excluded.note, title=excluded.title, author=excluded.author,
                        pages=excluded.pages, project=excluded.project, text_chars=excluded.text_chars, metadata=excluded.metadata, name_search=excluded.name_search;
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$path", info.FullName);
                cmd.Parameters.AddWithValue("$name", info.Name);
                cmd.Parameters.AddWithValue("$ext", info.Extension.ToLowerInvariant());
                cmd.Parameters.AddWithValue("$kind", FileKinds.Of(info.FullName));
                cmd.Parameters.AddWithValue("$size", info.Length);
                cmd.Parameters.AddWithValue("$mod", JarvisDatabase.Format(info.LastWriteTime));
                cmd.Parameters.AddWithValue("$created", JarvisDatabase.Format(info.CreationTime));
                cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
                cmd.Parameters.AddWithValue("$status", doc.Status);
                cmd.Parameters.AddWithValue("$note", (object?)doc.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$title", (object?)doc.Title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$author", (object?)doc.Author ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pages", (object?)doc.Pages ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$chars", doc.Text.Length);
                cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(doc.Metadata, Json));
                cmd.Parameters.AddWithValue("$ns", NameSearch(info.Name, doc.Title));
                cmd.ExecuteNonQuery();
            }
            for (var i = 0; i < chunks.Count; i++)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO file_chunks (file_id, ord, text, search_text) VALUES ($f, $o, $t, $s);";
                ins.Parameters.AddWithValue("$f", id);
                ins.Parameters.AddWithValue("$o", i);
                ins.Parameters.AddWithValue("$t", chunks[i]);
                ins.Parameters.AddWithValue("$s", TextNormalizer.Normalize($"{info.Name} {doc.Title} {chunks[i]}"));
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // People/organisations/projects mentioned in the title or the first pages.
        var head = $"{doc.Title} {Path.GetFileNameWithoutExtension(info.Name)} {doc.Text[..Math.Min(doc.Text.Length, 6000)]}";
        foreach (var e in entities.Mentioned(head)) Link(id, e.Id);
        foreach (var (chunkId, text) in ChunkIds(id)) semantic.Enqueue(ChunkOwner, chunkId.ToString(), text);
        return GetByPath(info.FullName)!;
    }

    public bool Remove(string path)
    {
        var f = GetByPath(path);
        if (f is null) return false;
        foreach (var (chunkId, _) in ChunkIds(f.Id)) semantic.Remove(ChunkOwner, chunkId.ToString());
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_chunks WHERE file_id = $id; DELETE FROM file_entities WHERE file_id = $id; DELETE FROM files WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", f.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Drops index entries for files that no longer exist under a root.</summary>
    public int Prune(string root)
    {
        var gone = PathsUnder(root).Where(p => !File.Exists(p)).ToList();
        foreach (var p in gone) Remove(p);
        return gone.Count;
    }

    public IReadOnlyList<string> PathsUnder(string root)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM files WHERE path LIKE $r;";
        cmd.Parameters.AddWithValue("$r", root.TrimEnd('\\', '/') + "%");
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IndexedFile? GetByPath(string path) => Query("WHERE path = $p", c => c.Parameters.AddWithValue("$p", path)).FirstOrDefault();
    public IndexedFile? Get(string id) => Query("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    /// <summary>Most recently modified files, optionally of a kind, under a folder, or about an entity.</summary>
    public IReadOnlyList<IndexedFile> Latest(int limit = 10, string? kind = null, string? folder = null, string? entityId = null) =>
        Query("""
            WHERE ($kind IS NULL OR kind = $kind) AND ($folder IS NULL OR path LIKE $folder)
              AND ($eid IS NULL OR id IN (SELECT file_id FROM file_entities WHERE entity_id = $eid))
            ORDER BY modified_at DESC LIMIT $limit
            """, c =>
        {
            c.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            c.Parameters.AddWithValue("$folder", folder is null ? DBNull.Value : folder.TrimEnd('\\', '/') + "%");
            c.Parameters.AddWithValue("$eid", (object?)entityId ?? DBNull.Value);
            c.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        });

    public IReadOnlyList<IndexedFile> AboutEntity(string entityId, int limit = 50) => Latest(limit, entityId: entityId);

    public IReadOnlyList<Entity> EntitiesOf(string fileId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM file_entities WHERE file_id = $f;";
        cmd.Parameters.AddWithValue("$f", fileId);
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids.Select(entities.Get).OfType<Entity>().ToList();
    }

    public void Link(string fileId, string entityId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO file_entities (file_id, entity_id) VALUES ($f, $e);";
        cmd.Parameters.AddWithValue("$f", fileId);
        cmd.Parameters.AddWithValue("$e", entityId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The indexed text of a file (all chunks), for summaries and comparisons.</summary>
    public string Text(string fileId) => string.Join("\n", ChunkIds(fileId).Select(c => c.Text));

    /// <summary>
    /// Keyword (names, titles and contents) and meaning-based search, ranked together. Results carry the
    /// best-matching passage.
    /// </summary>
    public async Task<IReadOnlyList<FileHit>> SearchAsync(string query, int limit, string? kind, CancellationToken ct)
    {
        var scores = new Dictionary<string, (double Score, string? Snippet, bool Semantic)>();
        var tokens = MemoryStore.Tokens(query);
        if (tokens.Count > 0)
        {
            var match = string.Join(" OR ", tokens.Select(t => $"\"{t}\"*"));
            using (var conn = db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT c.file_id, c.text, bm25(file_chunks_fts) AS rank FROM file_chunks_fts f JOIN file_chunks c ON c.id = f.rowid
                    WHERE file_chunks_fts MATCH $q ORDER BY rank LIMIT 200;
                    """;
                cmd.Parameters.AddWithValue("$q", match);
                using var r = cmd.ExecuteReader();
                var i = 0;
                while (r.Read())
                {
                    var id = r.GetString(0);
                    if (!scores.ContainsKey(id)) scores[id] = (1.0 / (i + 2) + 0.4, Snippet(r.GetString(1), tokens), false);
                    i++;
                }
            }
            // File names and titles that contain all the words rank high even without content.
            using (var conn = db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM files WHERE " + string.Join(" AND ", tokens.Select((_, k) => $"name_search LIKE $t{k}")) + " LIMIT 50;";
                for (var k = 0; k < tokens.Count; k++) cmd.Parameters.AddWithValue($"$t{k}", $"%{tokens[k]}%");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(0);
                    var cur = scores.GetValueOrDefault(id);
                    scores[id] = (Math.Max(cur.Score, 0.9), cur.Snippet, cur.Semantic);
                }
            }
        }
        // Files linked to people/organisations named in the query ("the CityCrep contract").
        foreach (var e in entities.Mentioned(query))
            foreach (var f in AboutEntity(e.Id, 50))
            {
                var cur = scores.GetValueOrDefault(f.Id);
                scores[f.Id] = (cur.Score + 0.25, cur.Snippet, cur.Semantic);
            }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            foreach (var (chunkId, sim) in await semantic.SearchAsync(ChunkOwner, query, 30, timeout.Token).ConfigureAwait(false))
            {
                if (sim < 0.45 || ChunkFile(long.Parse(chunkId)) is not { } c) continue;
                var cur = scores.GetValueOrDefault(c.FileId);
                if (sim > cur.Score) scores[c.FileId] = (sim + (cur.Score > 0 ? 0.1 : 0), cur.Snippet ?? Clip(c.Text, 240), true);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or AI.AiProviderException) { }

        return scores.Select(kv => (File: Get(kv.Key), kv.Value))
            .Where(x => x.File is not null && (kind is null || x.File.Kind == kind))
            .Select(x => new FileHit(x.File!, x.Value.Snippet, x.Value.Score, x.Value.Semantic))
            .OrderByDescending(h => h.Score).ThenByDescending(h => h.File.ModifiedAt)
            .Take(limit).ToList();
    }

    public (int Files, int WithText, long Chars, DateTimeOffset? LastIndexed) Stats()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN text_chars > 0 THEN 1 ELSE 0 END),0), COALESCE(SUM(text_chars),0), MAX(indexed_at) FROM files;";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : JarvisDatabase.Parse(r.GetString(3)));
    }

    public IReadOnlyDictionary<string, int> CountsByKind()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM files GROUP BY kind;";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, int>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    public int ClearAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_chunks; DELETE FROM file_entities; DELETE FROM embeddings WHERE owner_type = 'file_chunk'; DELETE FROM files;";
        var n = cmd.ExecuteNonQuery();
        events.Publish(EventTypes.FilesIndexChanged, new { action = "cleared" });
        return n;
    }

    /// <summary>Splits text on paragraph boundaries into ~1200-character chunks.</summary>
    internal static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;
        var sb = new StringBuilder();
        foreach (var para in ParaSplit().Split(text))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            if (sb.Length > 0 && sb.Length + p.Length > ChunkChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (p.Length > ChunkChars * 2)
            {
                for (var i = 0; i < p.Length; i += ChunkChars) chunks.Add(p.Substring(i, Math.Min(ChunkChars, p.Length - i)));
                continue;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks.Take(400).ToList();
    }

    private static string NameSearch(string name, string? title) =>
        TextNormalizer.Normalize($"{Path.GetFileNameWithoutExtension(name).Replace('_', ' ').Replace('-', ' ').Replace('.', ' ')} {title}");

    private static string? Snippet(string text, IReadOnlyList<string> tokens)
    {
        var norm = TextNormalizer.Normalize(text);
        var idx = tokens.Select(t => norm.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, Math.Min(idx - 80, text.Length - 1));
        return Clip(text[start..].ReplaceLineEndings(" "), 240);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s.Trim() : s[..max].Trim() + "…";

    private IEnumerable<(long Id, string Text)> ChunkIds(string fileId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text FROM file_chunks WHERE file_id = $f ORDER BY ord;";
        cmd.Parameters.AddWithValue("$f", fileId);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    private (string FileId, string Text)? ChunkFile(long chunkId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_id, text FROM file_chunks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", chunkId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    private List<IndexedFile> Query(string where, Action<SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, path, name, ext, kind, size, modified_at, created_at, indexed_at, status, note, title, author, pages, project, text_chars, metadata FROM files {where};";
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<IndexedFile>();
        while (r.Read())
        {
            list.Add(new IndexedFile
            {
                Id = r.GetString(0), Path = r.GetString(1), Name = r.GetString(2), Ext = r.GetString(3), Kind = r.GetString(4), Size = r.GetInt64(5),
                ModifiedAt = JarvisDatabase.Parse(r.GetString(6)), CreatedAt = r.IsDBNull(7) ? null : JarvisDatabase.Parse(r.GetString(7)),
                IndexedAt = JarvisDatabase.Parse(r.GetString(8)), Status = r.GetString(9), Note = r.IsDBNull(10) ? null : r.GetString(10),
                Title = r.IsDBNull(11) ? null : r.GetString(11), Author = r.IsDBNull(12) ? null : r.GetString(12), Pages = r.IsDBNull(13) ? null : r.GetInt32(13),
                Project = r.IsDBNull(14) ? null : r.GetString(14), TextChars = r.GetInt32(15),
                Metadata = r.IsDBNull(16) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(16), Json) ?? [],
            });
        }
        return list;
    }

    [GeneratedRegex(@"\n\s*\n|\r\n\s*\r\n|(?=--- (?:Slide|Sheet))")]
    private static partial Regex ParaSplit();
}

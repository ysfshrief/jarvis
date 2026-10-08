using Jarvis.Core.Persistence;

namespace Jarvis.Core.Agent;

public sealed record StoredMessage(long Id, string ConversationId, string Role, string Content, string? Lang, string? Source, string? Meta, DateTimeOffset CreatedAt);

public sealed record ConversationSummary(string Id, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

/// <summary>Persistent conversation history (the long-term record; the agent keeps a short window in memory).</summary>
public sealed class ConversationStore(JarvisDatabase db)
{
    public void EnsureConversation(string id, string? title = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $now, $now)
            ON CONFLICT(id) DO UPDATE SET updated_at = $now, title = COALESCE(conversations.title, $title);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    public void Append(string conversationId, string role, string content, string? lang, string? source, string? meta = null)
    {
        EnsureConversation(conversationId, role == "user" ? Title(content) : null);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (conversation_id, role, content, lang, source, meta, created_at)
            VALUES ($c, $role, $content, $lang, $source, $meta, $now);
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$lang", (object?)lang ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meta", (object?)meta ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StoredMessage> Messages(string conversationId, int limit = 200)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, conversation_id, role, content, lang, source, meta, created_at FROM (
                SELECT * FROM messages WHERE conversation_id = $c ORDER BY id DESC LIMIT $limit
            ) ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read())
        {
            list.Add(new StoredMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), JarvisDatabase.Parse(r.GetString(7))));
        }
        return list;
    }

    public IReadOnlyList<ConversationSummary> Recent(int limit = 30)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.title, c.created_at, c.updated_at, (SELECT COUNT(*) FROM messages m WHERE m.conversation_id = c.id)
            FROM conversations c ORDER BY c.updated_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationSummary>();
        while (r.Read())
        {
            list.Add(new ConversationSummary(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                JarvisDatabase.Parse(r.GetString(2)), JarvisDatabase.Parse(r.GetString(3)), r.GetInt32(4)));
        }
        return list;
    }

    public int DeleteOlderThan(DateTimeOffset cutoff)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE updated_at < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", JarvisDatabase.Format(cutoff));
        return cmd.ExecuteNonQuery();
    }

    public int DeleteAll()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations;";
        return cmd.ExecuteNonQuery();
    }

    private static string Title(string text)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length > 60 ? t[..57] + "..." : t;
    }
}

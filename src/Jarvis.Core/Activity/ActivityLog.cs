using Jarvis.Core.Events;
using Jarvis.Core.Persistence;

namespace Jarvis.Core.Activity;

public sealed record ActivityEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Kind,
    string? Tool,
    string Summary,
    string? Risk,
    string? Status,
    string? Details,
    string? ConversationId,
    long? DurationMs);

public static class ActivityKinds
{
    public const string Request = "request";
    public const string Tool = "tool";
    public const string Approval = "approval";
    public const string Ai = "ai";
    public const string System = "system";
    public const string Error = "error";
    public const string Notification = "notification";
    public const string Memory = "memory";
}

/// <summary>Audit trail of what JARVIS did, why, and with what result. Shown in the Activity view.</summary>
public sealed class ActivityLog(JarvisDatabase db, IEventBus events)
{
    public ActivityEntry Record(
        string kind,
        string summary,
        string? tool = null,
        string? risk = null,
        string? status = null,
        string? details = null,
        string? conversationId = null,
        long? durationMs = null)
    {
        var ts = DateTimeOffset.Now;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO activity (ts, kind, tool, summary, risk, status, details, conversation_id, duration_ms)
            VALUES ($ts, $kind, $tool, $summary, $risk, $status, $details, $conv, $dur);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", JarvisDatabase.Format(ts));
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$tool", (object?)tool ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", Truncate(summary, 1000));
        cmd.Parameters.AddWithValue("$risk", (object?)risk ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$details", (object?)Truncate(details, 8000) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conv", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dur", (object?)durationMs ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        var entry = new ActivityEntry(id, ts, kind, tool, summary, risk, status, details, conversationId, durationMs);
        events.Publish(EventTypes.Activity, entry);
        return entry;
    }

    public IReadOnlyList<ActivityEntry> Recent(int limit = 100, string? kind = null, string? status = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, kind, tool, summary, risk, status, details, conversation_id, duration_ms
            FROM activity
            WHERE ($kind IS NULL OR kind = $kind) AND ($status IS NULL OR status = $status)
            ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        var list = new List<ActivityEntry>();
        while (r.Read())
        {
            list.Add(new ActivityEntry(
                r.GetInt64(0), JarvisDatabase.Parse(r.GetString(1)), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetInt64(9)));
        }
        return list;
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..(max - 3)] + "...";
}

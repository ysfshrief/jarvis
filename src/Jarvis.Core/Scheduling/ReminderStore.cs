using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Scheduling;

public static class ReminderStatus
{
    public const string Pending = "pending";
    public const string Fired = "fired";
    public const string Cancelled = "cancelled";
}

public sealed record Reminder(string Id, string Text, DateTimeOffset DueAt, string Status, string? Lang, DateTimeOffset CreatedAt, DateTimeOffset? FiredAt);

public sealed class ReminderStore(JarvisDatabase db, IEventBus events)
{
    public Reminder Create(string text, DateTimeOffset dueAt, string? lang = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Reminder text is empty.");
        var id = Guid.NewGuid().ToString("n");
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO reminders (id, text, due_at, status, lang, created_at)
                VALUES ($id, $text, $due, 'pending', $lang, $now);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$text", text.Trim());
            cmd.Parameters.AddWithValue("$due", JarvisDatabase.Format(dueAt));
            cmd.Parameters.AddWithValue("$lang", (object?)lang ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.RemindersChanged, new { action = "created", id });
        return Get(id)!;
    }

    public Reminder? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM reminders WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<Reminder> List(bool includeDone = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM reminders WHERE ($all = 1 OR status = 'pending') ORDER BY due_at LIMIT 500;
            """;
        cmd.Parameters.AddWithValue("$all", includeDone ? 1 : 0);
        return ReadAll(cmd);
    }

    /// <summary>Pending reminders whose time has come. Compared as instants, not strings.</summary>
    public IReadOnlyList<Reminder> Due(DateTimeOffset now) =>
        List().Where(r => r.DueAt <= now).ToList();

    public DateTimeOffset? NextDue() => List().Select(r => (DateTimeOffset?)r.DueAt).Min();

    public bool MarkFired(string id) => SetStatus(id, ReminderStatus.Fired, fired: true);
    public bool Cancel(string id) => SetStatus(id, ReminderStatus.Cancelled, fired: false);

    private bool SetStatus(string id, string status, bool fired)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE reminders SET status=$s, fired_at=$f WHERE id=$id AND status='pending';";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$f", fired ? JarvisDatabase.Now() : DBNull.Value);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) events.Publish(EventTypes.RemindersChanged, new { action = status, id });
        return ok;
    }

    private const string Columns = "id, text, due_at, status, lang, created_at, fired_at";

    private static List<Reminder> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Reminder>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static Reminder Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), JarvisDatabase.Parse(r.GetString(2)), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), JarvisDatabase.Parse(r.GetString(5)),
        r.IsDBNull(6) ? null : JarvisDatabase.Parse(r.GetString(6)));
}

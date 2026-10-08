using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Tasks;

public static class TaskStates
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, InProgress, Waiting, Blocked, Completed, Cancelled];
    public static bool IsOpen(string state) => state is not (Completed or Cancelled);
}

public static class TaskPriorities
{
    public const string Low = "low", Normal = "normal", High = "high", Urgent = "urgent";
    public static readonly string[] All = [Low, Normal, High, Urgent];
}

public sealed record TaskItem(
    string Id,
    string Title,
    string? Notes,
    string State,
    string Priority,
    string? Project,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>Repeat rule (see Workflows.Recurrence); a completed recurring task schedules its next occurrence.</summary>
    public string? Recurrence { get; init; }
}

public sealed record NewTask(string Title, string? Notes = null, string Priority = "normal", string? Project = null, DateTimeOffset? DueAt = null, string? Recurrence = null);

public sealed record TaskUpdate(string? Title = null, string? Notes = null, string? State = null, string? Priority = null, string? Project = null, DateTimeOffset? DueAt = null, bool ClearDue = false);

public sealed class TaskStore(JarvisDatabase db, IEventBus events)
{
    public TaskItem Create(NewTask t)
    {
        if (string.IsNullOrWhiteSpace(t.Title)) throw new ArgumentException("Task title is empty.");
        if (!TaskPriorities.All.Contains(t.Priority)) throw new ArgumentException($"Unknown priority '{t.Priority}'.");
        if (t.Recurrence is not null && !Workflows.Recurrence.IsValid(t.Recurrence)) throw new ArgumentException($"Unknown repeat rule '{t.Recurrence}'.");
        var id = Guid.NewGuid().ToString("n");
        var now = JarvisDatabase.Now();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tasks (id, title, notes, state, priority, project, due_at, created_at, updated_at, recurrence)
                VALUES ($id, $title, $notes, 'pending', $prio, $project, $due, $now, $now, $rec);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$title", t.Title.Trim());
            cmd.Parameters.AddWithValue("$notes", (object?)t.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prio", t.Priority);
            cmd.Parameters.AddWithValue("$project", (object?)t.Project ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$due", t.DueAt is { } d ? JarvisDatabase.Format(d) : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$rec", (object?)t.Recurrence ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.TasksChanged, new { action = "created", id });
        return Get(id)!;
    }

    public TaskItem? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public TaskItem? Update(string id, TaskUpdate u)
    {
        var cur = Get(id);
        if (cur is null) return null;
        var state = u.State ?? cur.State;
        var priority = u.Priority ?? cur.Priority;
        if (!TaskStates.All.Contains(state)) throw new ArgumentException($"Unknown task state '{state}'.");
        if (!TaskPriorities.All.Contains(priority)) throw new ArgumentException($"Unknown priority '{priority}'.");
        var due = u.ClearDue ? null : u.DueAt ?? cur.DueAt;
        DateTimeOffset? completed = state == TaskStates.Completed ? cur.CompletedAt ?? DateTimeOffset.Now : null;

        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tasks SET title=$title, notes=$notes, state=$state, priority=$prio, project=$project,
                    due_at=$due, updated_at=$now, completed_at=$completed WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$title", (u.Title ?? cur.Title).Trim());
            cmd.Parameters.AddWithValue("$notes", (object?)(u.Notes ?? cur.Notes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$prio", priority);
            cmd.Parameters.AddWithValue("$project", (object?)(u.Project ?? cur.Project) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$due", due is { } d ? JarvisDatabase.Format(d) : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.Parameters.AddWithValue("$completed", completed is { } c ? JarvisDatabase.Format(c) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.TasksChanged, new { action = "updated", id });
        // Completing a recurring task schedules the next one (once).
        if (state == TaskStates.Completed && cur.State != TaskStates.Completed && cur.Recurrence is { } rule)
        {
            var anchor = cur.DueAt ?? DateTimeOffset.Now;
            var next = Workflows.Recurrence.Next(rule, anchor);
            while (next is { } n && n < DateTimeOffset.Now) next = Workflows.Recurrence.Next(rule, n); // skip missed occurrences
            if (next is not null)
                Create(new NewTask(cur.Title, cur.Notes, cur.Priority, cur.Project, cur.DueAt is null ? null : next, rule));
        }
        return Get(id);
    }

    public bool Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) events.Publish(EventTypes.TasksChanged, new { action = "deleted", id });
        return ok;
    }

    public IReadOnlyList<TaskItem> List(bool includeClosed = false, string? project = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM tasks
            WHERE ($closed = 1 OR state NOT IN ('completed','cancelled')) AND ($project IS NULL OR project = $project)
            ORDER BY CASE priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END,
                     COALESCE(due_at, '9999'), created_at;
            """;
        cmd.Parameters.AddWithValue("$closed", includeClosed ? 1 : 0);
        cmd.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<TaskItem>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>Find open tasks whose title contains the text (for "mark X as done").</summary>
    public IReadOnlyList<TaskItem> FindOpen(string titleFragment)
    {
        var needle = Language.TextNormalizer.Normalize(titleFragment);
        return List().Where(t => Language.TextNormalizer.Normalize(t.Title).Contains(needle)).ToList();
    }

    private const string Columns = "id, title, notes, state, priority, project, due_at, created_at, updated_at, completed_at, recurrence";

    private static TaskItem Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : JarvisDatabase.Parse(r.GetString(6)),
        JarvisDatabase.Parse(r.GetString(7)), JarvisDatabase.Parse(r.GetString(8)),
        r.IsDBNull(9) ? null : JarvisDatabase.Parse(r.GetString(9)))
    {
        Recurrence = r.IsDBNull(10) ? null : r.GetString(10),
    };
}

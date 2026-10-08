using System.Text.Json;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Workflows;

public static class WorkflowStatus
{
    public const string Active = "active";
    public const string Waiting = "waiting";   // everything that can move is waiting on someone else
    public const string Blocked = "blocked";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public static readonly string[] All = [Active, Waiting, Blocked, Completed, Cancelled];
}

public static class StepStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public static readonly string[] All = [Pending, InProgress, Waiting, Blocked, Done, Skipped];
    public static bool IsClosed(string s) => s is Done or Skipped;
}

/// <summary>An action a step can run through the normal tool pipeline (permissions, approvals, audit).</summary>
public sealed record StepAction(string Tool, JsonElement? Args);

public sealed record WorkflowStep
{
    public required string Id { get; init; }
    public required string WorkflowId { get; init; }
    public int Order { get; init; }
    public required string Title { get; init; }
    public string Status { get; init; } = StepStatus.Pending;
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public DateTimeOffset? DueAt { get; init; }
    /// <summary>Who or what this step is waiting on (an external dependency), e.g. "client signature".</summary>
    public string? WaitingFor { get; init; }
    public DateTimeOffset? FollowUpAt { get; init; }
    /// <summary>Business actions (sending, signing, paying…) are marked so JARVIS asks before acting on them.</summary>
    public bool RequiresApproval { get; init; }
    public StepAction? Action { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>All dependencies are done or skipped, and the step itself isn't closed.</summary>
    public bool Ready { get; init; }
}

public sealed record Workflow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Goal { get; init; }
    public string Template { get; init; } = "custom";
    public string Status { get; init; } = WorkflowStatus.Active;
    public string? EntityId { get; init; }
    public string? EntityName { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public string? Recurrence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];

    public int DoneCount => Steps.Count(s => StepStatus.IsClosed(s.Status));
    public double Progress => Steps.Count == 0 ? 0 : (double)DoneCount / Steps.Count;
    /// <summary>The first step that can be worked on now.</summary>
    public WorkflowStep? Next => Steps.FirstOrDefault(s => s.Ready && s.Status != StepStatus.Waiting) ?? Steps.FirstOrDefault(s => s.Ready);
    public bool IsOpen => Status is not (WorkflowStatus.Completed or WorkflowStatus.Cancelled);
}

public sealed record WorkflowEvent(long Id, string WorkflowId, string? StepId, DateTimeOffset Timestamp, string Kind, string Text);

public sealed record NewStep(string Title, IReadOnlyList<int>? DependsOnIndexes = null, DateTimeOffset? DueAt = null, string? WaitingFor = null,
    bool RequiresApproval = false, StepAction? Action = null, string? Notes = null);

public sealed record StepChange(string? Status = null, string? Notes = null, string? WaitingFor = null, DateTimeOffset? FollowUpAt = null,
    DateTimeOffset? DueAt = null, bool ClearFollowUp = false, string? Title = null);

/// <summary>
/// Long-running, multi-step goals ("track the CityCrep deal"): ordered steps with dependencies,
/// deadlines, waiting states with follow-ups, approval-gated business actions and a full history.
/// The workflow's status is derived from its steps, never set by hand except to cancel.
/// </summary>
public sealed class WorkflowStore(JarvisDatabase db, IEventBus events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Workflow Create(string title, string? goal, string template, IReadOnlyList<NewStep> steps, string? entityId = null, string? entityName = null,
        DateTimeOffset? dueAt = null, string? recurrence = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Workflow title is empty.");
        if (recurrence is not null && !Workflows.Recurrence.IsValid(recurrence)) throw new ArgumentException($"Unknown repeat rule '{recurrence}'.");
        var id = Guid.NewGuid().ToString("n");
        var now = JarvisDatabase.Now();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO workflows (id, title, goal, template, status, entity_id, entity_name, due_at, recurrence, created_at, updated_at)
                VALUES ($id, $t, $g, $tpl, 'active', $eid, $ename, $due, $rec, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$t", title.Trim());
            cmd.Parameters.AddWithValue("$g", (object?)goal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tpl", template);
            cmd.Parameters.AddWithValue("$eid", (object?)entityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ename", (object?)entityName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$due", dueAt is { } d ? JarvisDatabase.Format(d) : DBNull.Value);
            cmd.Parameters.AddWithValue("$rec", (object?)recurrence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        var ids = steps.Select(_ => Guid.NewGuid().ToString("n")).ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var deps = (s.DependsOnIndexes ?? (i > 0 ? [i - 1] : [])).Where(x => x >= 0 && x < i).Select(x => ids[x]).ToList();
            InsertStep(conn, tx, ids[i], id, i, s, deps, now);
        }
        tx.Commit();
        AddEvent(id, null, "created", $"Started tracking “{title.Trim()}” with {steps.Count} step(s).");
        Recompute(id);
        events.Publish(EventTypes.WorkflowsChanged, new { action = "created", id });
        return Get(id)!;
    }

    public WorkflowStep AddStep(string workflowId, NewStep step, IReadOnlyList<string>? dependsOn = null)
    {
        var wf = Get(workflowId) ?? throw new ArgumentException("Workflow not found.");
        var id = Guid.NewGuid().ToString("n");
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            var deps = dependsOn ?? (wf.Steps.Count > 0 ? [wf.Steps[^1].Id] : []);
            InsertStep(conn, tx, id, workflowId, wf.Steps.Count, step, deps, JarvisDatabase.Now());
            tx.Commit();
        }
        AddEvent(workflowId, id, "step_added", $"Added step “{step.Title}”.");
        Recompute(workflowId);
        events.Publish(EventTypes.WorkflowsChanged, new { action = "step_added", id = workflowId });
        return Get(workflowId)!.Steps.First(s => s.Id == id);
    }

    public Workflow? Get(string id)
    {
        using var conn = db.Open();
        Workflow? wf;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {WfCols} FROM workflows WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            wf = r.Read() ? ReadWf(r) : null;
        }
        return wf is null ? null : wf with { Steps = LoadSteps(conn, id) };
    }

    public IReadOnlyList<Workflow> List(bool includeClosed = false)
    {
        var list = new List<Workflow>();
        using var conn = db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {WfCols} FROM workflows WHERE ($all = 1 OR status NOT IN ('completed','cancelled')) ORDER BY updated_at DESC;";
            cmd.Parameters.AddWithValue("$all", includeClosed ? 1 : 0);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadWf(r));
        }
        return list.Select(w => w with { Steps = LoadSteps(conn, w.Id) }).ToList();
    }

    /// <summary>Best match by title or linked entity name (e.g. "CityCrep" finds "CityCrep deal").</summary>
    public Workflow? Find(string query, bool includeClosed = false)
    {
        var q = Language.TextNormalizer.Normalize(query).Trim();
        if (q.Length == 0) return null;
        var all = List(includeClosed);
        return all.FirstOrDefault(w => Language.TextNormalizer.Normalize(w.Title) == q)
            ?? all.FirstOrDefault(w => Language.TextNormalizer.Normalize(w.Title).Contains(q) || (w.EntityName is not null && Language.TextNormalizer.Normalize(w.EntityName).Contains(q)))
            ?? all.FirstOrDefault(w => q.Contains(Language.TextNormalizer.Normalize(w.Title)) || (w.EntityName is not null && q.Contains(Language.TextNormalizer.Normalize(w.EntityName))));
    }

    /// <summary>Finds a step by title fragment within a workflow.</summary>
    public static WorkflowStep? FindStep(Workflow wf, string query)
    {
        var q = Language.TextNormalizer.Normalize(query).Trim();
        if (int.TryParse(q, out var n) && n >= 1 && n <= wf.Steps.Count) return wf.Steps[n - 1];
        return wf.Steps.FirstOrDefault(s => Language.TextNormalizer.Normalize(s.Title) == q)
            ?? wf.Steps.FirstOrDefault(s => Language.TextNormalizer.Normalize(s.Title).Contains(q))
            ?? wf.Steps.FirstOrDefault(s => Words(q).Count(w => Language.TextNormalizer.Normalize(s.Title).Contains(w)) >= Math.Max(1, Words(q).Count * 2 / 3));
    }

    private static List<string> Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();

    public WorkflowStep? UpdateStep(string stepId, StepChange c, string? note = null)
    {
        using var conn = db.Open();
        WorkflowStep? cur;
        using (var get = conn.CreateCommand())
        {
            get.CommandText = $"SELECT {StepCols} FROM workflow_steps WHERE id = $id;";
            get.Parameters.AddWithValue("$id", stepId);
            using var r = get.ExecuteReader();
            cur = r.Read() ? ReadStep(r) : null;
        }
        if (cur is null) return null;
        if (c.Status is not null && !StepStatus.All.Contains(c.Status)) throw new ArgumentException($"Unknown step status '{c.Status}'.");
        var status = c.Status ?? cur.Status;
        var now = JarvisDatabase.Now();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE workflow_steps SET title=$title, status=$s, notes=$notes, waiting_for=$wait, follow_up_at=$fu, due_at=$due, updated_at=$now,
                    completed_at = CASE WHEN $s IN ('done','skipped') THEN COALESCE(completed_at, $now) ELSE NULL END,
                    notified = CASE WHEN $fuChanged = 1 THEN NULL ELSE notified END
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.Parameters.AddWithValue("$title", c.Title?.Trim() is { Length: > 0 } t ? t : cur.Title);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$notes", (object?)(c.Notes ?? cur.Notes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$wait", (object?)(status == StepStatus.Waiting ? c.WaitingFor ?? cur.WaitingFor : c.WaitingFor) ?? DBNull.Value);
            var followUp = c.ClearFollowUp ? null : c.FollowUpAt ?? (status == StepStatus.Waiting ? cur.FollowUpAt : null);
            cmd.Parameters.AddWithValue("$fu", followUp is { } f ? JarvisDatabase.Format(f) : DBNull.Value);
            cmd.Parameters.AddWithValue("$fuChanged", followUp != cur.FollowUpAt || c.DueAt is not null ? 1 : 0);
            cmd.Parameters.AddWithValue("$due", (c.DueAt ?? cur.DueAt) is { } d ? JarvisDatabase.Format(d) : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        var text = status != cur.Status
            ? status switch
            {
                StepStatus.Done => $"“{cur.Title}” done.",
                StepStatus.Skipped => $"“{cur.Title}” skipped.",
                StepStatus.Waiting => $"“{cur.Title}” is waiting{(c.WaitingFor ?? cur.WaitingFor) switch { { } w => $" for {w}", _ => "" }}.",
                StepStatus.Blocked => $"“{cur.Title}” is blocked.",
                StepStatus.InProgress => $"Started “{cur.Title}”.",
                _ => $"“{cur.Title}” reopened.",
            }
            : $"Updated “{cur.Title}”.";
        AddEvent(cur.WorkflowId, stepId, status != cur.Status ? "step_" + status : "step_updated", note is null ? text : $"{text} {note}");
        Recompute(cur.WorkflowId);
        events.Publish(EventTypes.WorkflowsChanged, new { action = "step", id = cur.WorkflowId, stepId });
        return Get(cur.WorkflowId)!.Steps.First(s => s.Id == stepId);
    }

    public bool Cancel(string id)
    {
        if (Get(id) is not { IsOpen: true }) return false;
        SetStatus(id, WorkflowStatus.Cancelled);
        AddEvent(id, null, "cancelled", "Stopped tracking.");
        events.Publish(EventTypes.WorkflowsChanged, new { action = "cancelled", id });
        return true;
    }

    public bool Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM workflow_steps WHERE workflow_id = $id; DELETE FROM workflow_events WHERE workflow_id = $id; DELETE FROM workflows WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        if (ok) events.Publish(EventTypes.WorkflowsChanged, new { action = "deleted", id });
        return ok;
    }

    public void Note(string workflowId, string text, string? stepId = null)
    {
        AddEvent(workflowId, stepId, "note", text);
        Touch(workflowId);
        events.Publish(EventTypes.WorkflowsChanged, new { action = "note", id = workflowId });
    }

    public IReadOnlyList<WorkflowEvent> History(string workflowId, int limit = 200)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, workflow_id, step_id, ts, kind, text FROM workflow_events WHERE workflow_id = $id ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$id", workflowId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowEvent>();
        while (r.Read())
            list.Add(new WorkflowEvent(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), JarvisDatabase.Parse(r.GetString(3)), r.GetString(4), r.GetString(5)));
        return list;
    }

    /// <summary>Records that a follow-up/deadline notification went out, so it isn't repeated.</summary>
    public void MarkNotified(string stepId, string what)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workflow_steps SET notified = COALESCE(notified, '') || $w || ';' WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", stepId);
        cmd.Parameters.AddWithValue("$w", what);
        cmd.ExecuteNonQuery();
    }

    public bool WasNotified(string stepId, string what)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT notified FROM workflow_steps WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", stepId);
        return cmd.ExecuteScalar() is string s && s.Split(';').Contains(what);
    }

    public void AddEvent(string workflowId, string? stepId, string kind, string text)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO workflow_events (workflow_id, step_id, ts, kind, text) VALUES ($w, $s, $now, $k, $t);";
        cmd.Parameters.AddWithValue("$w", workflowId);
        cmd.Parameters.AddWithValue("$s", (object?)stepId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Derives the workflow status from its steps; starts the next cycle of a recurring workflow.</summary>
    private void Recompute(string id)
    {
        var wf = Get(id);
        if (wf is null || wf.Status == WorkflowStatus.Cancelled) return;
        string status;
        if (wf.Steps.Count > 0 && wf.Steps.All(s => StepStatus.IsClosed(s.Status))) status = WorkflowStatus.Completed;
        else if (wf.Steps.Any(s => s.Status == StepStatus.Blocked)) status = WorkflowStatus.Blocked;
        else if (wf.Steps.Where(s => s.Ready).All(s => s.Status == StepStatus.Waiting) && wf.Steps.Any(s => s.Ready)) status = WorkflowStatus.Waiting;
        else status = WorkflowStatus.Active;
        if (status == wf.Status) { Touch(id); return; }
        SetStatus(id, status);
        if (status == WorkflowStatus.Completed)
        {
            AddEvent(id, null, "completed", "All steps are done.");
            if (wf.Recurrence is { } rule) StartNextCycle(wf, rule);
        }
    }

    private void StartNextCycle(Workflow wf, string rule)
    {
        var anchor = wf.DueAt ?? DateTimeOffset.Now;
        var next = Workflows.Recurrence.Next(rule, anchor);
        var offset = next - anchor;
        var steps = wf.Steps.Select(s => new NewStep(s.Title,
            s.DependsOn.Select(d => wf.Steps.ToList().FindIndex(x => x.Id == d)).Where(i => i >= 0).ToList(),
            s.DueAt is { } due && offset is { } o ? due + o : null, null, s.RequiresApproval, s.Action, null)).ToList();
        var created = Create(wf.Title, wf.Goal, wf.Template, steps, wf.EntityId, wf.EntityName, next, rule);
        AddEvent(wf.Id, null, "recurred", $"Next cycle created for {next:ddd d MMM}.");
        AddEvent(created.Id, null, "recurred", "Created automatically from the previous cycle.");
    }

    private void SetStatus(string id, string status)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workflows SET status = $s, updated_at = $now, completed_at = CASE WHEN $s = 'completed' THEN $now ELSE NULL END WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    private void Touch(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workflows SET updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    private static void InsertStep(SqliteConnection conn, SqliteTransaction tx, string id, string workflowId, int ord, NewStep s, IReadOnlyList<string> deps, string now)
    {
        if (string.IsNullOrWhiteSpace(s.Title)) throw new ArgumentException("Step title is empty.");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO workflow_steps (id, workflow_id, ord, title, status, depends_on, due_at, waiting_for, requires_approval, action, notes, created_at, updated_at)
            VALUES ($id, $wf, $ord, $t, $status, $deps, $due, $wait, $appr, $action, $notes, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$wf", workflowId);
        cmd.Parameters.AddWithValue("$ord", ord);
        cmd.Parameters.AddWithValue("$t", s.Title.Trim());
        cmd.Parameters.AddWithValue("$status", s.WaitingFor is null ? StepStatus.Pending : StepStatus.Waiting);
        cmd.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(deps));
        cmd.Parameters.AddWithValue("$due", s.DueAt is { } d ? JarvisDatabase.Format(d) : DBNull.Value);
        cmd.Parameters.AddWithValue("$wait", (object?)s.WaitingFor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$appr", s.RequiresApproval ? 1 : 0);
        cmd.Parameters.AddWithValue("$action", s.Action is null ? DBNull.Value : JsonSerializer.Serialize(s.Action, Json));
        cmd.Parameters.AddWithValue("$notes", (object?)s.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private static List<WorkflowStep> LoadSteps(SqliteConnection conn, string workflowId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {StepCols} FROM workflow_steps WHERE workflow_id = $id ORDER BY ord;";
        cmd.Parameters.AddWithValue("$id", workflowId);
        using var r = cmd.ExecuteReader();
        var steps = new List<WorkflowStep>();
        while (r.Read()) steps.Add(ReadStep(r));
        var closed = steps.Where(s => StepStatus.IsClosed(s.Status)).Select(s => s.Id).ToHashSet();
        return steps.Select(s => s with { Ready = !StepStatus.IsClosed(s.Status) && s.DependsOn.All(closed.Contains) }).ToList();
    }

    private const string WfCols = "id, title, goal, template, status, entity_id, entity_name, due_at, recurrence, created_at, updated_at, completed_at";
    private const string StepCols = "id, workflow_id, ord, title, status, depends_on, due_at, waiting_for, follow_up_at, requires_approval, action, notes, created_at, updated_at, completed_at";

    private static Workflow ReadWf(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Title = r.GetString(1), Goal = r.IsDBNull(2) ? null : r.GetString(2), Template = r.GetString(3), Status = r.GetString(4),
        EntityId = r.IsDBNull(5) ? null : r.GetString(5), EntityName = r.IsDBNull(6) ? null : r.GetString(6),
        DueAt = r.IsDBNull(7) ? null : JarvisDatabase.Parse(r.GetString(7)), Recurrence = r.IsDBNull(8) ? null : r.GetString(8),
        CreatedAt = JarvisDatabase.Parse(r.GetString(9)), UpdatedAt = JarvisDatabase.Parse(r.GetString(10)),
        CompletedAt = r.IsDBNull(11) ? null : JarvisDatabase.Parse(r.GetString(11)),
    };

    private static WorkflowStep ReadStep(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), WorkflowId = r.GetString(1), Order = r.GetInt32(2), Title = r.GetString(3), Status = r.GetString(4),
        DependsOn = r.IsDBNull(5) ? [] : JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [],
        DueAt = r.IsDBNull(6) ? null : JarvisDatabase.Parse(r.GetString(6)), WaitingFor = r.IsDBNull(7) ? null : r.GetString(7),
        FollowUpAt = r.IsDBNull(8) ? null : JarvisDatabase.Parse(r.GetString(8)), RequiresApproval = r.GetInt32(9) == 1,
        Action = r.IsDBNull(10) ? null : JsonSerializer.Deserialize<StepAction>(r.GetString(10), Json), Notes = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = JarvisDatabase.Parse(r.GetString(12)), UpdatedAt = JarvisDatabase.Parse(r.GetString(13)),
        CompletedAt = r.IsDBNull(14) ? null : JarvisDatabase.Parse(r.GetString(14)),
    };
}

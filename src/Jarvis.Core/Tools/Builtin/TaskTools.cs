using System.Globalization;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Tasks;

namespace Jarvis.Core.Tools.Builtin;

public sealed class TaskCreateTool(TaskStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "task_create",
        Category = "tasks",
        Description = "Create a task in the user's task list.",
        Parameters =
        [
            new("title", "string", "Short task title.", true),
            new("notes", "string", "Optional details."),
            new("priority", "string", "Priority.", false, TaskPriorities.All),
            new("project", "string", "Project the task belongs to."),
            new("due", "string", "Due date/time in ISO 8601, if any."),
            new("repeat", "string", "Repeat rule for recurring tasks: daily, weekdays, weekly, weekly:mon,thu, monthly, every:3d."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Add task: {args.GetString("title")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var due = DateTimeOffset.TryParse(args.GetString("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTimeOffset?)null;
        var prio = args.GetString("priority") ?? "normal";
        if (!TaskPriorities.All.Contains(prio)) prio = "normal";
        var title = args.RequireString("title");
        var repeat = args.GetString("repeat");
        if (repeat is null)
        {
            var (rule, rest) = Workflows.Recurrence.Extract(title);
            if (rule is not null && rest.Length > 0) { repeat = rule; title = rest; }
        }
        if (repeat is not null && !Workflows.Recurrence.IsValid(repeat)) repeat = null;
        // A recurring task without a date starts today at 9:00 so "next occurrence" has an anchor.
        if (repeat is not null && due is null) due = new DateTimeOffset(DateTime.Today.AddHours(9));
        var t = store.Create(new NewTask(title, args.GetString("notes"), prio, args.GetString("project"), due, repeat));
        var every = repeat is null ? "" : ctx.T($" (repeats {Workflows.Recurrence.Describe(repeat)})", $" (بتتكرر {Workflows.Recurrence.Describe(repeat, true)})");
        return Task.FromResult(ToolResult.Ok(ctx.T($"Added to your tasks: {t.Title}{every}.", $"ضفتها للمهام: {t.Title}{every}."), t));
    }
}

public sealed class TaskListTool(TaskStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "task_list",
        Category = "tasks",
        Description = "List the user's open tasks (or all tasks including completed).",
        Parameters = [new("include_closed", "boolean", "Include completed/cancelled tasks.")],
    };

    protected override string Describe(ToolArgs args) => "List tasks";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var list = store.List(args.GetBool("include_closed") ?? false);
        if (list.Count == 0)
            return Task.FromResult(ToolResult.Ok(ctx.T($"Your task list is clear{ctx.CommaSir}.", $"مفيش مهام مفتوحة{ctx.CommaSir}."), list));
        var lines = string.Join("\n", list.Take(10).Select(t => $"• {t.Title}" + (t.State != TaskStates.Pending ? $" ({t.State.Replace('_', ' ')})" : "")));
        var head = ctx.T($"You have {list.Count} open task{(list.Count == 1 ? "" : "s")}:", $"عندك {list.Count} مهام:");
        return Task.FromResult(ToolResult.Ok($"{head}\n{lines}", list));
    }
}

public sealed class TaskCompleteTool(TaskStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "task_complete",
        Category = "tasks",
        Description = "Mark a task as completed, by id or by (part of) its title.",
        Parameters = [new("id", "string", "Task id."), new("title", "string", "Part of the task title.")],
    };

    protected override string Describe(ToolArgs args) => $"Complete task: {args.GetString("title") ?? args.GetString("id")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var id = args.GetString("id");
        TaskItem? task = id is not null ? store.Get(id) : null;
        if (task is null && args.GetString("title") is { } title)
        {
            var matches = store.FindOpen(title);
            if (matches.Count > 1)
                return Task.FromResult(ToolResult.Fail(ctx.T(
                    $"Several tasks match \"{title}\": {string.Join(", ", matches.Select(m => m.Title))}. Which one?",
                    $"فيه كذا مهمة شبه \"{title}\": {string.Join("، ", matches.Select(m => m.Title))}. أنهي واحدة؟")));
            task = matches.FirstOrDefault();
        }
        if (task is null)
            return Task.FromResult(ToolResult.Fail(ctx.T("I couldn't find that task.", "ملقتش المهمة دي."), status: ToolStatus.NotFound));
        var done = store.Update(task.Id, new TaskUpdate(State: TaskStates.Completed))!;
        return Task.FromResult(ToolResult.Ok(ctx.T($"Marked done: {done.Title}.", $"خلصت: {done.Title}."), done));
    }
}

public sealed class TaskUpdateTool(TaskStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "task_update",
        Category = "tasks",
        Description = "Change a task's state, priority, title, notes or due date.",
        Parameters =
        [
            new("id", "string", "Task id.", true),
            new("state", "string", "New state.", false, TaskStates.All),
            new("priority", "string", "New priority.", false, TaskPriorities.All),
            new("title", "string", "New title."),
            new("notes", "string", "New notes."),
            new("due", "string", "New due date/time (ISO 8601)."),
        ],
    };

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var due = DateTimeOffset.TryParse(args.GetString("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTimeOffset?)null;
        var updated = store.Update(args.RequireString("id"), new TaskUpdate(args.GetString("title"), args.GetString("notes"),
            args.GetString("state"), args.GetString("priority"), DueAt: due));
        return Task.FromResult(updated is null
            ? ToolResult.Fail(ctx.T("I couldn't find that task.", "ملقتش المهمة دي."), status: ToolStatus.NotFound)
            : ToolResult.Ok(ctx.T($"Updated: {updated.Title}.", $"اتعدلت: {updated.Title}."), updated));
    }
}

public sealed class ReminderCreateTool(ReminderStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "reminder_create",
        Category = "tasks",
        Description = "Set a reminder that will notify the user at a specific time.",
        Parameters =
        [
            new("text", "string", "What to remind the user about.", true),
            new("due", "string", "When, as ISO 8601 local date-time (e.g. 2025-05-01T17:30:00).", false),
            new("in_minutes", "number", "Alternatively: minutes from now."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Reminder: {args.GetString("text")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        DateTimeOffset due;
        if (args.GetDouble("in_minutes") is { } minutes && minutes > 0)
            due = DateTimeOffset.Now.AddMinutes(minutes);
        else if (DateTimeOffset.TryParse(args.GetString("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            due = parsed;
        else
            throw new ToolArgumentException("Give either 'due' (ISO date-time) or 'in_minutes'.");

        if (due <= DateTimeOffset.Now.AddSeconds(-5))
            return Task.FromResult(ToolResult.Fail(ctx.T("That time has already passed.", "الوقت ده عدى خلاص.")));

        var r = store.Create(args.RequireString("text"), due, ctx.Lang == Language.Lang.Ar ? "ar" : "en");
        return Task.FromResult(ToolResult.Ok(ctx.T($"I'll remind you {When(due, ctx)}{ctx.CommaSir}.", $"هفكرك {When(due, ctx)}{ctx.CommaSir}."), r));
    }

    internal static string When(DateTimeOffset due, ToolContext ctx)
    {
        var now = DateTimeOffset.Now;
        var delta = due - now;
        var local = due.LocalDateTime;
        if (delta < TimeSpan.FromMinutes(90))
        {
            var mins = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return ctx.T($"in {mins} minute{(mins == 1 ? "" : "s")}", mins switch { 1 => "بعد دقيقة", 2 => "بعد دقيقتين", <= 10 => $"بعد {mins} دقايق", _ => $"بعد {mins} دقيقة" });
        }
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (local.Date == now.LocalDateTime.Date) return ctx.T($"at {time}", $"الساعة {local:h:mm}");
        if (local.Date == now.LocalDateTime.Date.AddDays(1)) return ctx.T($"tomorrow at {time}", $"بكرة الساعة {local:h:mm}");
        return ctx.T($"on {local.ToString("ddd d MMM", CultureInfo.InvariantCulture)} at {time}", $"يوم {local:d/M} الساعة {local:h:mm}");
    }
}

public sealed class ReminderListTool(ReminderStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "reminder_list",
        Category = "tasks",
        Description = "List upcoming reminders.",
    };

    protected override string Describe(ToolArgs args) => "List reminders";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var list = store.List();
        if (list.Count == 0) return Task.FromResult(ToolResult.Ok(ctx.T("No upcoming reminders.", "مفيش تذكيرات جاية."), list));
        var lines = string.Join("\n", list.Take(10).Select(r => $"• {r.Text} — {ReminderCreateTool.When(r.DueAt, ctx)}"));
        return Task.FromResult(ToolResult.Ok(lines, list));
    }
}

public sealed class ReminderCancelTool(ReminderStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "reminder_cancel",
        Category = "tasks",
        Description = "Cancel a pending reminder by id.",
        Parameters = [new("id", "string", "Reminder id.", true)],
    };

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx) =>
        Task.FromResult(store.Cancel(args.RequireString("id"))
            ? ToolResult.Ok(ctx.T("Reminder cancelled.", "اتلغى التذكير."))
            : ToolResult.Fail(ctx.T("No pending reminder with that id.", "مفيش تذكير بالرقم ده."), status: ToolStatus.NotFound));
}

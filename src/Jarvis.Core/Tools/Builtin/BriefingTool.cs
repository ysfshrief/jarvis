using System.Globalization;
using System.Text;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Notifications;
using Jarvis.Core.Permissions;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Tasks;

namespace Jarvis.Core.Tools.Builtin;

/// <summary>
/// "What's happening today?" / "Check my priorities": a chief-of-staff summary built only from what
/// JARVIS actually knows (reminders, tasks, approvals, queued work, held notifications). No AI needed.
/// </summary>
public sealed class BriefingTool(TaskStore tasks, ReminderStore reminders, ApprovalBroker approvals, OfflineQueue queue, NotificationCenter notifications, Workflows.WorkflowStore workflows, Inbox.InboxStore mail, Agenda.AgendaStore agenda) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "daily_briefing",
        Category = "tasks",
        Description = "Summarise the user's day: reminders due today, priority and overdue tasks, tracked workflows (deals, projects), approvals waiting, queued actions and held notifications.",
        Parameters = [new("focus", "string", "\"today\" for the whole day or \"priorities\" for tasks only.", false, ["today", "priorities"])],
    };

    protected override string Describe(ToolArgs args) => "Prepare a briefing";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var b = Build(DateTimeOffset.Now, args.GetString("focus") == "priorities");
        return Task.FromResult(ToolResult.Ok(Render(b, ctx), b));
    }

    public sealed record Briefing(
        IReadOnlyList<Reminder> RemindersToday,
        IReadOnlyList<TaskItem> Overdue,
        IReadOnlyList<TaskItem> Priority,
        IReadOnlyList<TaskItem> DueToday,
        int OpenTasks,
        int Approvals,
        int Queued,
        int HeldNotifications,
        bool PrioritiesOnly)
    {
        /// <summary>Tracked workflows that need attention (waiting, overdue or with a ready next step).</summary>
        public IReadOnlyList<Workflows.Workflow> Workflows { get; init; } = [];
        /// <summary>Unhandled mail that needs the user (from the last sync — reading the briefing doesn't fetch mail).</summary>
        public IReadOnlyList<Inbox.InboxMessage> UrgentMail { get; init; } = [];
        public int NeedsReply { get; init; }
        /// <summary>Calendar events for the rest of today.</summary>
        public IReadOnlyList<Agenda.AgendaEvent> Meetings { get; init; } = [];
    }

    public Briefing Build(DateTimeOffset now, bool prioritiesOnly)
    {
        var endOfDay = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        var open = tasks.List();
        var overdue = open.Where(t => t.DueAt is { } d && d < now).OrderBy(t => t.DueAt).ToList();
        var dueToday = open.Where(t => t.DueAt is { } d && d >= now && d < endOfDay).OrderBy(t => t.DueAt).ToList();
        var priority = open.Where(t => t.Priority is TaskPriorities.Urgent or TaskPriorities.High && !overdue.Contains(t))
            .OrderByDescending(t => t.Priority == TaskPriorities.Urgent).ThenBy(t => t.DueAt ?? DateTimeOffset.MaxValue).ToList();
        var todays = reminders.List().Where(r => r.DueAt < endOfDay).OrderBy(r => r.DueAt).ToList();
        int held;
        try { held = notifications.Recent(100, "held").Count; } catch { held = 0; }
        return new Briefing(todays, overdue, priority, dueToday, open.Count, approvals.Pending.Count, queue.List().Count, held, prioritiesOnly)
        {
            Workflows = workflows.List().Where(w => w.Steps.Any(st => st.Ready)).Take(5).ToList(),
            UrgentMail = mail.List(Inbox.MailCategories.Urgent, limit: 3),
            NeedsReply = mail.Counts()[Inbox.MailCategories.NeedsResponse],
            Meetings = agenda.Between(now, endOfDay),
        };
    }

    public static string Render(Briefing b, ToolContext ctx)
    {
        var ar = ctx.Lang == Language.Lang.Ar;
        var sb = new StringBuilder();
        string Time(DateTimeOffset d) => d.ToString(d.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);

        if (b.Approvals > 0)
            sb.AppendLine(ctx.T($"• {b.Approvals} action(s) waiting for your approval.", $"• فيه {b.Approvals} حاجة مستنية موافقتك."));
        if (b.Overdue.Count > 0)
            sb.AppendLine(ctx.T($"• Overdue: {List(b.Overdue, 3)}.", $"• متأخر: {List(b.Overdue, 3)}."));
        if (b.Priority.Count > 0)
            sb.AppendLine(ctx.T($"• Top priorities: {List(b.Priority, 3)}.", $"• أهم الأولويات: {List(b.Priority, 3)}."));
        if (b.DueToday.Count > 0)
            sb.AppendLine(ctx.T($"• Due today: {List(b.DueToday, 3)}.", $"• مطلوب النهارده: {List(b.DueToday, 3)}."));
        if (b.Meetings.Count > 0)
        {
            var items = string.Join(ar ? "، " : ", ", b.Meetings.Take(4).Select(e => e.AllDay ? e.Title : $"{e.Title} ({Time(e.Start)})"));
            sb.AppendLine(ctx.T($"• Calendar: {items}{(b.Meetings.Count > 4 ? $" and {b.Meetings.Count - 4} more" : "")}.",
                                $"• الأجندة: {items}{(b.Meetings.Count > 4 ? $" و{b.Meetings.Count - 4} كمان" : "")}."));
        }
        if (b.UrgentMail.Count > 0)
        {
            var items = string.Join(ar ? "، " : "; ", b.UrgentMail.Select(m => $"{m.Sender} — {m.Subject}"));
            sb.AppendLine(ctx.T($"• Urgent email: {items}.", $"• إيميل مستعجل: {items}."));
        }
        if (b.NeedsReply > 0)
            sb.AppendLine(ctx.T($"• {b.NeedsReply} email(s) waiting for your reply.", $"• {b.NeedsReply} إيميل مستني ردك."));
        foreach (var wf in b.Workflows)
            sb.AppendLine("• " + Workflows.WorkflowService.Summary(wf, ctx.Lang));
        if (!b.PrioritiesOnly)
        {
            if (b.RemindersToday.Count > 0)
            {
                var items = string.Join(ar ? "، " : ", ", b.RemindersToday.Take(3).Select(r => $"{r.Text} ({Time(r.DueAt)})"));
                sb.AppendLine(ctx.T($"• Reminders today: {items}{(b.RemindersToday.Count > 3 ? $" and {b.RemindersToday.Count - 3} more" : "")}.",
                                    $"• تذكيرات النهارده: {items}{(b.RemindersToday.Count > 3 ? $" و{b.RemindersToday.Count - 3} كمان" : "")}."));
            }
            if (b.Queued > 0)
                sb.AppendLine(ctx.T($"• {b.Queued} internet action(s) queued until we're back online.", $"• فيه {b.Queued} حاجة مستنية النت يرجع."));
            if (b.HeldNotifications > 0)
                sb.AppendLine(ctx.T($"• {b.HeldNotifications} notification(s) held while you were busy.", $"• فيه {b.HeldNotifications} إشعار مستني عشان كنت مشغول."));
        }

        if (sb.Length == 0)
        {
            return b.OpenTasks == 0
                ? ctx.T($"Your slate is clear{ctx.CommaSir}: no open tasks, reminders or approvals today.", $"مفيش حاجة ورا حضرتك{ctx.CommaSir}: لا مهام ولا تذكيرات ولا موافقات النهارده.")
                : ctx.T($"Nothing urgent{ctx.CommaSir}. You have {b.OpenTasks} open task(s), none overdue or high priority.",
                        $"مفيش حاجة مستعجلة{ctx.CommaSir}. عندك {b.OpenTasks} مهمة مفتوحة، ولا واحدة متأخرة أو مهمة أوي.");
        }
        var head = b.PrioritiesOnly
            ? ctx.T($"Here are your priorities{ctx.CommaSir}:", $"دي أولوياتك{ctx.CommaSir}:")
            : ctx.T($"Here's your day{ctx.CommaSir}:", $"ده يومك{ctx.CommaSir}:");
        return head + "\n" + sb.ToString().TrimEnd();
    }

    private static string List(IReadOnlyList<TaskItem> items, int max)
    {
        var shown = string.Join(", ", items.Take(max).Select(t => t.Title));
        return items.Count > max ? $"{shown} (+{items.Count - max})" : shown;
    }
}

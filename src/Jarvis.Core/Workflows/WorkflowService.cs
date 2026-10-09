using System.Text.Json;
using Jarvis.Core.Activity;
using Jarvis.Core.Agent;
using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Permissions;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Workflows;

/// <summary>A reusable shape for a common kind of goal.</summary>
public sealed record WorkflowTemplate(string Id, string Name, string Description, IReadOnlyList<NewStep> Steps, string EntityType);

public static class WorkflowTemplates
{
    public static readonly IReadOnlyList<WorkflowTemplate> All =
    [
        new("deal", "Deal", "Track a sales deal or partnership from first contact to kickoff.",
        [
            new("Qualify the opportunity and confirm the decision maker"),
            new("Prepare the proposal"),
            new("Send the proposal", RequiresApproval: true),
            new("Get the client's response", WaitingFor: "the client"),
            new("Negotiate terms"),
            new("Contract signed", WaitingFor: "the client's signature", RequiresApproval: true),
            new("Kickoff meeting"),
        ], EntityTypes.Organization),
        new("project", "Project", "Plan, build, review and deliver a piece of work.",
        [
            new("Define the scope and success criteria"),
            new("Plan milestones"),
            new("Build"),
            new("Review and test"),
            new("Deliver", RequiresApproval: true),
            new("Retrospective and follow-ups"),
        ], EntityTypes.Project),
        new("hiring", "Hiring", "Fill a role.",
        [
            new("Write the job description"),
            new("Publish the job post", RequiresApproval: true),
            new("Screen candidates"),
            new("Interviews"),
            new("Make the offer", RequiresApproval: true),
            new("Offer accepted", WaitingFor: "the candidate"),
        ], EntityTypes.Organization),
        new("follow_up", "Follow-up", "Wait for someone and chase them if they go quiet.",
        [
            new("Get a reply", WaitingFor: "a reply"),
        ], EntityTypes.Person),
        new("custom", "Custom", "Your own steps.", [], EntityTypes.Topic),
    ];

    public static WorkflowTemplate Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All.First(t => t.Id == "custom");
}

/// <summary>
/// Creates workflows (from templates or custom steps), links them to the people/organisations they're
/// about, runs step actions through the normal tool pipeline (so approvals and audit still apply),
/// and sends follow-up and deadline nudges.
/// </summary>
public sealed class WorkflowService(
    WorkflowStore store,
    KnowledgeService knowledge,
    NotificationCenter notifications,
    ActivityLog activity,
    ISettingsStore settings)
{
    public static readonly TimeSpan DefaultFollowUp = TimeSpan.FromDays(3);

    public WorkflowStore Store => store;

    public Workflow Start(string title, string template, string? about = null, string? goal = null, IReadOnlyList<NewStep>? customSteps = null,
        DateTimeOffset? dueAt = null, string? recurrence = null)
    {
        var tpl = WorkflowTemplates.Get(template);
        var steps = customSteps is { Count: > 0 } ? customSteps : tpl.Steps;
        if (steps.Count == 0) throw new ArgumentException("A custom workflow needs at least one step.");
        string? entityId = null, entityName = null;
        if (!string.IsNullOrWhiteSpace(about))
        {
            var e = knowledge.Entities.Find(about) ?? knowledge.Entities.Upsert(tpl.EntityType, about.Trim());
            knowledge.LinkExisting(e);
            entityId = e.Id;
            entityName = e.Name;
        }
        // Spread deadlines: a deal due in 30 days gets step due dates in order, the last on the deadline.
        if (dueAt is { } due && customSteps is null)
        {
            var span = due - DateTimeOffset.Now;
            if (span > TimeSpan.Zero)
                steps = steps.Select((s, i) => s with { DueAt = DateTimeOffset.Now + span * (i + 1) / steps.Count }).ToList();
        }
        var wf = store.Create(title, goal ?? tpl.Description, tpl.Id, steps, entityId, entityName, dueAt, recurrence);
        activity.Record(ActivityKinds.Tool, $"Tracking workflow: {wf.Title}", "workflow_create", status: "ok");
        return wf;
    }

    /// <summary>Marks a step waiting on someone, with a follow-up date (default three days).</summary>
    public WorkflowStep? Wait(string stepId, string? waitingFor, TimeSpan? followUpIn = null) =>
        store.UpdateStep(stepId, new StepChange(Status: StepStatus.Waiting, WaitingFor: waitingFor, FollowUpAt: DateTimeOffset.Now + (followUpIn ?? DefaultFollowUp)));

    /// <summary>
    /// Sends nudges for follow-ups that are due and step deadlines that are near or passed. Each nudge
    /// goes out once (until the date changes).
    /// </summary>
    public async Task<int> CheckDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var sent = 0;
        var ar = Jarvis.Core.Settings.LanguagePolicy.Default(settings.Current) == Jarvis.Core.Language.Lang.Ar;
        foreach (var wf in store.List())
        {
            foreach (var s in wf.Steps.Where(x => !StepStatus.IsClosed(x.Status)))
            {
                if (s.Status == StepStatus.Waiting && s.FollowUpAt is { } fu && fu <= now && !store.WasNotified(s.Id, "followup"))
                {
                    var waited = (int)Math.Max(1, (now - s.UpdatedAt).TotalDays);
                    var title = ar ? $"متابعة: {wf.Title}" : $"Follow up: {wf.Title}";
                    var body = ar
                        ? $"«{s.Title}» مستني {s.WaitingFor ?? "رد"} من {waited} يوم. تحب نتابع؟"
                        : $"“{s.Title}” has been waiting on {s.WaitingFor ?? "a reply"} for {waited} day(s). Time to follow up?";
                    await notifications.PostAsync(new Notification { Title = title, Body = body, Priority = NotificationPriority.High, Source = "workflow", GroupKey = $"wf-fu-{s.Id}" }, ct).ConfigureAwait(false);
                    store.MarkNotified(s.Id, "followup");
                    store.AddEvent(wf.Id, s.Id, "follow_up", $"Reminded you to follow up on “{s.Title}”.");
                    sent++;
                }
                if (s.DueAt is { } due)
                {
                    if (due <= now && !store.WasNotified(s.Id, "overdue"))
                    {
                        await notifications.PostAsync(new Notification
                        {
                            Title = ar ? $"متأخر: {s.Title}" : $"Overdue: {s.Title}",
                            Body = ar ? $"في «{wf.Title}» — كان المفروض {due:ddd d MMM HH:mm}." : $"In “{wf.Title}” — was due {due:ddd d MMM HH:mm}.",
                            Priority = NotificationPriority.High, Source = "workflow", GroupKey = $"wf-due-{s.Id}",
                        }, ct).ConfigureAwait(false);
                        store.MarkNotified(s.Id, "overdue");
                        store.AddEvent(wf.Id, s.Id, "overdue", $"“{s.Title}” is overdue.");
                        sent++;
                    }
                    else if (due > now && due - now <= TimeSpan.FromHours(24) && s.Ready && !store.WasNotified(s.Id, "duesoon"))
                    {
                        await notifications.PostAsync(new Notification
                        {
                            Title = ar ? $"قرب ميعاد: {s.Title}" : $"Due soon: {s.Title}",
                            Body = ar ? $"في «{wf.Title}» — {due:ddd HH:mm}." : $"In “{wf.Title}” — {due:ddd HH:mm}.",
                            Priority = NotificationPriority.Normal, Source = "workflow", GroupKey = $"wf-soon-{s.Id}",
                        }, ct).ConfigureAwait(false);
                        store.MarkNotified(s.Id, "duesoon");
                        sent++;
                    }
                }
            }
        }
        return sent;
    }

    /// <summary>One-line status used by the briefing, the assistant and the dashboard.</summary>
    public static string Summary(Workflow wf, Language.Lang lang)
    {
        var ar = lang == Language.Lang.Ar;
        var done = $"{wf.DoneCount}/{wf.Steps.Count}";
        if (wf.Status == WorkflowStatus.Completed) return ar ? $"{wf.Title}: خلص ({done})." : $"{wf.Title}: completed ({done}).";
        var waiting = wf.Steps.Where(s => s.Ready && s.Status == StepStatus.Waiting).ToList();
        var next = wf.Next;
        var parts = new List<string>();
        if (waiting.Count > 0)
            parts.Add(ar ? $"مستني {string.Join("، ", waiting.Select(w => w.WaitingFor ?? w.Title))}" : $"waiting on {string.Join(", ", waiting.Select(w => w.WaitingFor ?? w.Title))}");
        if (next is not null && next.Status != StepStatus.Waiting)
            parts.Add(ar ? $"الخطوة الجاية: {next.Title}" : $"next: {next.Title}");
        var overdue = wf.Steps.Count(s => !StepStatus.IsClosed(s.Status) && s.DueAt < DateTimeOffset.Now);
        if (overdue > 0) parts.Add(ar ? $"{overdue} متأخر" : $"{overdue} overdue");
        return $"{wf.Title} ({done}): {string.Join(ar ? "؛ " : "; ", parts)}";
    }

    public static StepAction? ParseAction(string? tool, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(tool)) return null;
        JsonElement? args = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { args = JsonDocument.Parse(argsJson).RootElement.Clone(); }
            catch (JsonException) { throw new ArgumentException("The step action's arguments aren't valid JSON."); }
        }
        return new StepAction(tool.Trim(), args);
    }
}

/// <summary>
/// Runs workflow step actions through the normal tool pipeline. Separate from <see cref="WorkflowService"/>
/// because tools themselves depend on the workflow service.
/// </summary>
public sealed class WorkflowRunner(WorkflowStore store, ToolExecutor executor, ApprovalBroker approvals, ISettingsStore settings)
{
    /// <summary>
    /// Runs a step's action as a tool call. Steps flagged as business actions always ask first, even
    /// when the tool itself is safe; the decision is recorded in the workflow history.
    /// </summary>
    public async Task<(bool Ok, string Message)> RunStepAsync(string stepId, ToolContext ctx)
    {
        var wf = store.List(true).FirstOrDefault(w => w.Steps.Any(s => s.Id == stepId));
        var step = wf?.Steps.First(s => s.Id == stepId);
        if (wf is null || step is null) return (false, ctx.T("I couldn't find that step.", "ملقتش الخطوة دي."));
        if (step.Action is null) return (false, ctx.T("That step is something you do; tell me when it's done.", "الخطوة دي حضرتك اللي بتعملها؛ قولي لما تخلص."));
        if (!step.Ready) return (false, ctx.T("Earlier steps aren't finished yet.", "لسه فيه خطوات قبلها مخلصتش."));

        if (step.RequiresApproval)
        {
            var now = DateTimeOffset.Now;
            var decision = await approvals.RequestAsync(new ApprovalRequest(
                Guid.NewGuid().ToString("n"), ctx.ConversationId, step.Action.Tool, RiskLevel.Sensitive,
                $"{wf.Title}: {step.Title}", "This workflow step is a business action, so it needs your OK.",
                step.Action.Args?.GetRawText() ?? "{}", now, now.AddSeconds(settings.Current.Permissions.ApprovalTimeoutSeconds)), ctx.CancellationToken).ConfigureAwait(false);
            if (decision != ApprovalOutcome.Approved)
            {
                store.AddEvent(wf.Id, step.Id, "approval", $"Running “{step.Title}” was {(decision == ApprovalOutcome.Denied ? "declined" : "not approved in time")}.");
                return (false, ctx.T("Not run — it wasn't approved.", "متنفذتش — مفيش موافقة."));
            }
            store.AddEvent(wf.Id, step.Id, "approval", $"You approved “{step.Title}”.");
        }

        var args = step.Action.Args is { } a ? ToolArgs.Parse(a.GetRawText()) : new ToolArgs();
        var (result, _) = await executor.ExecuteAsync(step.Action.Tool, args, ctx).ConfigureAwait(false);
        if (result.Success)
        {
            store.UpdateStep(step.Id, new StepChange(Status: StepStatus.Done), $"Done by JARVIS: {result.Message}");
            return (true, result.Message);
        }
        store.AddEvent(wf.Id, step.Id, "action_failed", $"“{step.Title}” failed: {result.Message}");
        return (false, result.Message);
    }

}

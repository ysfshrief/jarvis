using System.Globalization;
using Jarvis.Core.Workflows;

namespace Jarvis.Core.Tools.Builtin;

public sealed class WorkflowCreateTool(WorkflowService workflows) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "workflow_create",
        Category = "workflows",
        Description = "Start tracking a multi-step goal (a deal, project, hire or follow-up) with ordered steps, deadlines and follow-ups. Use a template, or give your own steps.",
        Parameters =
        [
            new("title", "string", "Short name, e.g. \"CityCrep deal\".", true),
            new("template", "string", "Template to use.", false, WorkflowTemplates.All.Select(t => t.Id).ToList()),
            new("about", "string", "The organisation, project or person it's about, e.g. CityCrep."),
            new("steps", "string", "Custom steps separated by \";\" (only for template=custom)."),
            new("due", "string", "Overall deadline, ISO 8601, if any."),
            new("repeat", "string", "Repeat rule if this recurs: daily, weekdays, weekly, weekly:mon,thu, monthly, every:3d."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Track: {args.GetString("title")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var template = args.GetString("template") ?? (args.GetString("steps") is null ? "deal" : "custom");
        var custom = args.GetString("steps")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => new NewStep(s)).ToList();
        var due = DateTimeOffset.TryParse(args.GetString("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTimeOffset?)null;
        var existing = workflows.Store.Find(args.RequireString("title"));
        if (existing is not null && existing.Title.Equals(args.RequireString("title"), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Ok(ctx.T($"I'm already tracking that. {WorkflowService.Summary(existing, ctx.Lang)}", $"أنا متابعها بالفعل. {WorkflowService.Summary(existing, ctx.Lang)}"), existing));
        Workflow wf;
        try
        {
            wf = workflows.Start(args.RequireString("title"), template, args.GetString("about"), null, custom, due, args.GetString("repeat"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
        var steps = string.Join("\n", wf.Steps.Select((s, i) => $"{i + 1}. {s.Title}{(s.RequiresApproval ? ctx.T(" (needs your OK)", " (محتاج موافقتك)") : "")}{(s.WaitingFor is null ? "" : ctx.T($" — waiting for {s.WaitingFor}", $" — مستني {s.WaitingFor}"))}"));
        var msg = ctx.T($"Tracking “{wf.Title}”{ctx.CommaSir}. Steps:\n{steps}\nI'll nudge you on deadlines and follow-ups.",
                        $"بقيت متابع «{wf.Title}»{ctx.CommaSir}. الخطوات:\n{steps}\nهفكرك بالمواعيد والمتابعات.");
        return Task.FromResult(ToolResult.Ok(msg, new { wf.Id, wf.Title, steps = wf.Steps.Select(s => s.Title) }));
    }
}

public sealed class WorkflowStatusTool(WorkflowStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "workflow_status",
        Category = "workflows",
        Description = "Show what JARVIS is tracking, or the status of one workflow (steps, what it's waiting on, deadlines, recent history).",
        Parameters = [new("name", "string", "Workflow name or what it's about; omit to list all.")],
    };

    protected override string Describe(ToolArgs args) => args.GetString("name") is { } n ? $"Workflow status: {n}" : "List workflows";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var all = store.List();
            if (all.Count == 0)
                return Task.FromResult(ToolResult.Ok(ctx.T("I'm not tracking anything yet. Try “track the CityCrep deal”.", "مش متابع حاجة لسه. جرب «تابع صفقة سيتي كريب».")));
            var lines = string.Join("\n", all.Select(w => "• " + WorkflowService.Summary(w, ctx.Lang)));
            return Task.FromResult(ToolResult.Ok(ctx.T($"I'm tracking {all.Count}:\n{lines}", $"أنا متابع {all.Count}:\n{lines}"), all.Select(w => new { w.Id, w.Title, w.Status, w.Progress })));
        }
        var wf = store.Find(name, includeClosed: true);
        if (wf is null)
            return Task.FromResult(ToolResult.Fail(ctx.T($"I'm not tracking anything called “{name}”.", $"مش متابع حاجة اسمها «{name}»."), status: ToolStatus.NotFound));
        var stepLines = string.Join("\n", wf.Steps.Select((s, i) => $"{Mark(s)} {i + 1}. {s.Title}{Extra(s, ctx)}"));
        var history = store.History(wf.Id, 3).Select(h => h.Text);
        var msg = $"{WorkflowService.Summary(wf, ctx.Lang)}\n{stepLines}" + (history.Any() ? "\n" + ctx.T("Latest: ", "آخر حاجة: ") + string.Join(" · ", history) : "");
        return Task.FromResult(ToolResult.Ok(msg, wf));
    }

    private static string Mark(WorkflowStep s) => s.Status switch
    {
        StepStatus.Done => "✓", StepStatus.Skipped => "–", StepStatus.Waiting => "⏳", StepStatus.Blocked => "⛔", StepStatus.InProgress => "▶", _ => s.Ready ? "○" : "·",
    };

    private static string Extra(WorkflowStep s, ToolContext ctx)
    {
        var parts = new List<string>();
        if (s.Status == StepStatus.Waiting && s.WaitingFor is not null) parts.Add(ctx.T($"waiting for {s.WaitingFor}", $"مستني {s.WaitingFor}"));
        if (s.FollowUpAt is { } f && !StepStatus.IsClosed(s.Status)) parts.Add(ctx.T($"follow up {f:ddd d MMM}", $"متابعة {f:d/M}"));
        if (s.DueAt is { } d && !StepStatus.IsClosed(s.Status)) parts.Add(ctx.T($"due {d:ddd d MMM}", $"مطلوب {d:d/M}"));
        if (s.RequiresApproval && !StepStatus.IsClosed(s.Status)) parts.Add(ctx.T("needs your OK", "محتاج موافقتك"));
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }
}

public sealed class WorkflowStepTool(WorkflowStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "workflow_update_step",
        Category = "workflows",
        Description = "Update a step of a tracked workflow: mark it done, skipped, in progress, blocked, or waiting on someone (with a follow-up), or add a note.",
        Parameters =
        [
            new("workflow", "string", "Workflow name or what it's about.", true),
            new("step", "string", "Step title (or part of it), or its number.", true),
            new("status", "string", "New status.", false, StepStatus.All),
            new("waiting_for", "string", "Who/what it waits on (with status=waiting)."),
            new("follow_up_days", "number", "Remind me to follow up after this many days (default 3 when waiting)."),
            new("note", "string", "A note to record in the history."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Update {args.GetString("workflow")}: {args.GetString("step")} → {args.GetString("status") ?? "note"}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var wf = store.Find(args.RequireString("workflow"));
        if (wf is null) return Task.FromResult(ToolResult.Fail(ctx.T($"I'm not tracking “{args.GetString("workflow")}”.", $"مش متابع «{args.GetString("workflow")}»."), status: ToolStatus.NotFound));
        var step = WorkflowStore.FindStep(wf, args.RequireString("step"));
        if (step is null)
            return Task.FromResult(ToolResult.Fail(ctx.T($"“{wf.Title}” has no step like “{args.GetString("step")}”. Steps: {string.Join("; ", wf.Steps.Select(s => s.Title))}",
                $"«{wf.Title}» مفيهاش خطوة زي «{args.GetString("step")}». الخطوات: {string.Join("؛ ", wf.Steps.Select(s => s.Title))}"), status: ToolStatus.NotFound));
        var status = args.GetString("status");
        if (status is not null && !StepStatus.All.Contains(status)) status = null;
        var followDays = args.GetDouble("follow_up_days");
        var followUp = status == StepStatus.Waiting || followDays is not null
            ? DateTimeOffset.Now.AddDays(followDays ?? WorkflowService.DefaultFollowUp.TotalDays)
            : (DateTimeOffset?)null;
        var updated = store.UpdateStep(step.Id, new StepChange(Status: status, WaitingFor: args.GetString("waiting_for"), FollowUpAt: followUp), args.GetString("note"));
        var wf2 = store.Get(wf.Id)!;
        var msg = ctx.T($"Updated “{updated!.Title}” in {wf.Title}. {WorkflowService.Summary(wf2, ctx.Lang)}",
                        $"حدثت «{updated!.Title}» في {wf.Title}. {WorkflowService.Summary(wf2, ctx.Lang)}");
        return Task.FromResult(ToolResult.Ok(msg, new { workflow = wf2.Title, step = updated.Title, updated.Status, wf2.Progress }));
    }
}

public sealed class WorkflowAddStepTool(WorkflowStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "workflow_add_step",
        Category = "workflows",
        Description = "Add a step to a tracked workflow (it follows the current last step).",
        Parameters =
        [
            new("workflow", "string", "Workflow name.", true),
            new("title", "string", "Step title.", true),
            new("due", "string", "Due date/time, ISO 8601."),
            new("needs_approval", "boolean", "True for business actions (sending, signing, paying, publishing)."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Add step to {args.GetString("workflow")}: {args.GetString("title")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var wf = store.Find(args.RequireString("workflow"));
        if (wf is null) return Task.FromResult(ToolResult.Fail(ctx.T("I'm not tracking that.", "مش متابع ده."), status: ToolStatus.NotFound));
        var due = DateTimeOffset.TryParse(args.GetString("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : (DateTimeOffset?)null;
        var step = store.AddStep(wf.Id, new NewStep(args.RequireString("title"), DueAt: due, RequiresApproval: args.GetBool("needs_approval") == true));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Added “{step.Title}” to {wf.Title}.", $"ضفت «{step.Title}» لـ {wf.Title}."), step));
    }
}

public sealed class WorkflowCancelTool(WorkflowStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "workflow_cancel",
        Category = "workflows",
        Risk = RiskLevel.Sensitive,
        Description = "Stop tracking a workflow (kept in history as cancelled).",
        Parameters = [new("workflow", "string", "Workflow name.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Stop tracking {args.GetString("workflow")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var wf = store.Find(args.RequireString("workflow"));
        if (wf is null || !store.Cancel(wf.Id)) return Task.FromResult(ToolResult.Fail(ctx.T("I'm not tracking that.", "مش متابع ده."), status: ToolStatus.NotFound));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Stopped tracking {wf.Title}.", $"وقفت متابعة {wf.Title}.")));
    }
}

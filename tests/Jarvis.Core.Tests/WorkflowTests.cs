using System.Text.Json;
using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Core.Workflows;

namespace Jarvis.Core.Tests;

public class RecurrenceTests
{
    private static readonly DateTimeOffset Mon = new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(3)); // a Monday

    [Theory]
    [InlineData("daily", "2026-10-06")]
    [InlineData("weekly", "2026-10-12")]
    [InlineData("weekly:thu", "2026-10-08")]
    [InlineData("weekly:mon,thu", "2026-10-08")]
    [InlineData("monthly", "2026-11-05")]
    [InlineData("every:3d", "2026-10-08")]
    [InlineData("every:2w", "2026-10-19")]
    [InlineData("weekdays", "2026-10-06")]
    public void Computes_the_next_occurrence(string rule, string expected) =>
        Assert.Equal(expected, Recurrence.Next(rule, Mon)!.Value.ToString("yyyy-MM-dd"));

    [Fact]
    public void Working_days_skip_the_egyptian_weekend() =>
        Assert.Equal(DayOfWeek.Sunday, Recurrence.Next("weekdays", new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero))!.Value.DayOfWeek); // Thu → Sun

    [Theory]
    [InlineData("water the plants every monday", "weekly:mon", "water the plants")]
    [InlineData("send the weekly report every week", "weekly", "send the weekly report")]
    [InlineData("check the bank daily", "daily", "check the bank")]
    [InlineData("backup the laptop every 3 days", "every:3d", "backup the laptop")]
    [InlineData("اراجع الحسابات كل شهر", "monthly", "اراجع الحسابات")]
    [InlineData("اكلم ماما كل جمعة", "weekly:fri", "اكلم ماما")]
    public void Extracts_repeat_phrases(string text, string rule, string rest)
    {
        var (r, remaining) = Recurrence.Extract(text);
        Assert.Equal(rule, r);
        Assert.Equal(rest, remaining);
    }
}

public class WorkflowTests
{
    [Fact]
    public async Task Tracking_a_deal_creates_ordered_steps_linked_to_the_client()
    {
        using var host = new TestHost();
        var r = await host.Say("track the CityCrep deal");
        Assert.True(r.Success, r.Reply);
        Assert.Contains("Send the proposal (needs your OK)", r.Reply);

        var wf = host.Get<WorkflowStore>().List().Single();
        Assert.Equal("CityCrep deal", wf.Title);
        Assert.Equal("deal", wf.Template);
        Assert.Equal(7, wf.Steps.Count);
        Assert.Equal("CityCrep", wf.EntityName);
        Assert.Equal(EntityTypes.Organization, host.Get<EntityStore>().Find("CityCrep")!.Type);
        // Only the first step can start; the rest wait for their predecessors.
        Assert.True(wf.Steps[0].Ready);
        Assert.All(wf.Steps.Skip(1), s => Assert.False(s.Ready));

        var again = await host.Say("track the CityCrep deal");
        Assert.Contains("already tracking", again.Reply);
        Assert.Single(host.Get<WorkflowStore>().List());

        var ar = await host.Say("تابع صفقة سيتي كريب");
        Assert.True(ar.Success);
        Assert.Equal("ar", ar.Lang);
    }

    [Fact]
    public async Task Steps_progress_wait_and_complete()
    {
        using var host = new TestHost();
        await host.Say("track the Atlas project");
        var store = host.Get<WorkflowStore>();
        var wf = store.List().Single();
        Assert.Equal("Atlas project", wf.Title);

        var r = await host.Say("define the scope is done for Atlas");
        Assert.True(r.Success, r.Reply);
        wf = store.Get(wf.Id)!;
        Assert.Equal(StepStatus.Done, wf.Steps[0].Status);
        Assert.True(wf.Steps[1].Ready);
        Assert.Equal(WorkflowStatus.Active, wf.Status);

        // Everything that can move is waiting on someone → the workflow is "waiting".
        store.UpdateStep(wf.Steps[1].Id, new StepChange(Status: StepStatus.Waiting, WaitingFor: "Mona's input", FollowUpAt: DateTimeOffset.Now.AddDays(2)));
        wf = store.Get(wf.Id)!;
        Assert.Equal(WorkflowStatus.Waiting, wf.Status);

        foreach (var s in wf.Steps.Skip(1)) store.UpdateStep(s.Id, new StepChange(Status: StepStatus.Done));
        wf = store.Get(wf.Id)!;
        Assert.Equal(WorkflowStatus.Completed, wf.Status);
        Assert.NotNull(wf.CompletedAt);
        Assert.Contains(store.History(wf.Id), h => h.Kind == "completed");

        var status = await host.Say("what's the status of the Atlas project");
        Assert.Contains("completed", status.Reply);
    }

    [Fact]
    public async Task Follow_ups_and_deadlines_nudge_once()
    {
        using var host = new TestHost();
        var service = host.Get<WorkflowService>();
        var wf = service.Start("Bank loan", "custom", null, null,
            [new NewStep("Submit documents", DueAt: DateTimeOffset.Now.AddHours(-1)), new NewStep("Bank decision", WaitingFor: "the bank")]);
        var store = host.Get<WorkflowStore>();
        store.UpdateStep(wf.Steps[0].Id, new StepChange(Status: StepStatus.Done));
        wf = store.Get(wf.Id)!;
        store.UpdateStep(wf.Steps[1].Id, new StepChange(Status: StepStatus.Waiting, WaitingFor: "the bank", FollowUpAt: DateTimeOffset.Now.AddMinutes(-5)));

        var sent = await service.CheckDueAsync(DateTimeOffset.Now, default);
        Assert.Equal(1, sent);
        var note = host.Get<NotificationCenter>().Recent(5).First();
        Assert.Contains("Follow up: Bank loan", note.Title);
        Assert.Contains("the bank", note.Body);
        Assert.Equal(0, await service.CheckDueAsync(DateTimeOffset.Now, default)); // not repeated

        var overdueWf = service.Start("Taxes", "custom", null, null, [new NewStep("File the return", DueAt: DateTimeOffset.Now.AddMinutes(-1))]);
        Assert.Equal(1, await service.CheckDueAsync(DateTimeOffset.Now, default));
        Assert.Contains(host.Get<NotificationCenter>().Recent(5), n => n.Title == "Overdue: File the return");
        Assert.Contains(store.History(overdueWf.Id), h => h.Kind == "overdue");
    }

    [Fact]
    public void Recurring_workflows_start_their_next_cycle()
    {
        using var host = new TestHost();
        var service = host.Get<WorkflowService>();
        var due = DateTimeOffset.Now.AddDays(1);
        var wf = service.Start("Weekly report", "custom", null, null, [new NewStep("Collect numbers"), new NewStep("Send report", RequiresApproval: true)], due, "weekly");
        var store = host.Get<WorkflowStore>();
        foreach (var s in wf.Steps) store.UpdateStep(s.Id, new StepChange(Status: StepStatus.Done));
        var open = store.List();
        var next = Assert.Single(open);
        Assert.NotEqual(wf.Id, next.Id);
        Assert.Equal(due.AddDays(7).Date, next.DueAt!.Value.Date);
        Assert.True(next.Steps[1].RequiresApproval);
        Assert.All(next.Steps, s => Assert.Equal(StepStatus.Pending, s.Status));
    }

    [Fact]
    public async Task Recurring_tasks_schedule_the_next_one_when_completed()
    {
        using var host = new TestHost();
        var r = await host.Say("add task water the plants every monday");
        Assert.Contains("repeats every Monday", r.Reply);
        var tasks = host.Get<TaskStore>();
        var t = tasks.List().Single();
        Assert.Equal("water the plants", t.Title);
        Assert.Equal("weekly:mon", t.Recurrence);

        tasks.Update(t.Id, new TaskUpdate(State: TaskStates.Completed));
        var next = tasks.List().Single();
        Assert.NotEqual(t.Id, next.Id);
        Assert.Equal(DayOfWeek.Monday, next.DueAt!.Value.DayOfWeek);
        Assert.True(next.DueAt > DateTimeOffset.Now);
    }

    [Fact]
    public async Task Business_steps_run_only_after_approval()
    {
        using var host = new TestHost();
        var service = host.Get<WorkflowService>();
        var action = WorkflowService.ParseAction("task_create", JsonSerializer.Serialize(new { title = "Invoice CityCrep" }));
        var wf = service.Start("Billing", "custom", null, null, [new NewStep("Raise the invoice", RequiresApproval: true, Action: action)]);
        var runner = host.Get<WorkflowRunner>();

        var denied = runner.RunStepAsync(wf.Steps[0].Id, host.Ctx());
        await host.AnswerNextApproval(false);
        var (ok, _) = await denied;
        Assert.False(ok);
        Assert.Empty(host.Get<TaskStore>().List());
        Assert.Equal(StepStatus.Pending, host.Get<WorkflowStore>().Get(wf.Id)!.Steps[0].Status);

        var approved = runner.RunStepAsync(wf.Steps[0].Id, host.Ctx());
        await host.AnswerNextApproval(true);
        var (ok2, _) = await approved;
        Assert.True(ok2);
        Assert.Contains(host.Get<TaskStore>().List(), t => t.Title == "Invoice CityCrep");
        var after = host.Get<WorkflowStore>().Get(wf.Id)!;
        Assert.Equal(WorkflowStatus.Completed, after.Status);
        Assert.Contains(host.Get<WorkflowStore>().History(wf.Id), h => h.Text.Contains("You approved"));
    }

    [Fact]
    public async Task Briefing_mentions_tracked_work()
    {
        using var host = new TestHost();
        await host.Say("track the CityCrep deal");
        var r = await host.Say("what's happening today");
        Assert.Contains("CityCrep deal (0/7)", r.Reply);
        Assert.Contains("next: Qualify the opportunity", r.Reply);
    }
}

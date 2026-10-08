using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Presence;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;

namespace Jarvis.Core.Tests;

public class MemoryTests
{
    [Fact]
    public void Add_search_update_delete()
    {
        using var host = new TestHost();
        var mem = host.Get<MemoryStore>();
        var a = mem.Add(new NewMemory("Ahmed is the CityCrep account manager", MemoryKinds.Person, "Ahmed"));
        mem.Add(new NewMemory("Prefers short answers in the morning", MemoryKinds.Preference));

        Assert.Equal(a.Id, Assert.Single(mem.Search("who manages citycrep")).Id);
        Assert.Equal(MemorySources.UserExplicit, a.Source);
        Assert.Equal(1.0, a.Confidence);

        var updated = mem.Update(a.Id, new MemoryUpdate(Content: "Ahmed manages the CityCrep and Nile accounts"));
        Assert.Single(mem.Search("nile"));
        Assert.Empty(mem.Search("manager"));
        Assert.NotNull(updated);

        Assert.True(mem.Delete(a.Id));
        Assert.Empty(mem.Search("ahmed"));
    }

    [Fact]
    public void Arabic_search_tolerates_spelling_variants()
    {
        using var host = new TestHost();
        var mem = host.Get<MemoryStore>();
        mem.Add(new NewMemory("أحمد بيحب القهوة سادة"));
        Assert.Single(mem.Search("احمد"));
        Assert.Single(mem.Search("القهوه"));
    }

    [Fact]
    public void Duplicates_are_merged_and_explicit_source_wins()
    {
        using var host = new TestHost();
        var mem = host.Get<MemoryStore>();
        mem.Add(new NewMemory("Works best after 10am", MemoryKinds.Pattern, Source: MemorySources.Learned));
        var again = mem.Add(new NewMemory("Works best after 10am", MemoryKinds.Preference, Source: MemorySources.UserExplicit));
        Assert.Equal(1, mem.Count());
        Assert.Equal(MemorySources.UserExplicit, again.Source);
        Assert.Equal(1.0, again.Confidence);
    }

    [Fact]
    public void Learned_patterns_have_lower_confidence_than_facts()
    {
        using var host = new TestHost();
        var m = host.Get<MemoryStore>().Add(new NewMemory("Usually opens VS Code at 9", MemoryKinds.Pattern, Source: MemorySources.Learned));
        Assert.True(m.Confidence < 1.0);
    }

    [Fact]
    public void Clear_by_kind()
    {
        using var host = new TestHost();
        var mem = host.Get<MemoryStore>();
        mem.Add(new NewMemory("a fact"));
        mem.Add(new NewMemory("a preference", MemoryKinds.Preference));
        Assert.Equal(1, mem.Clear(MemoryKinds.Fact));
        Assert.Equal(MemoryKinds.Preference, Assert.Single(mem.List()).Kind);
    }

    [Fact]
    public void Expired_context_disappears()
    {
        using var host = new TestHost();
        var mem = host.Get<MemoryStore>();
        mem.Add(new NewMemory("temporary", MemoryKinds.Context, ExpiresAt: DateTimeOffset.Now.AddSeconds(-1)));
        Assert.Empty(mem.List());
    }

    [Fact]
    public async Task Disallowed_memory_kinds_are_not_stored()
    {
        using var host = new TestHost(s => s.Memory.AllowedKinds = ["fact"]);
        var r = await host.Get<Agent.ToolExecutor>().ExecuteAsync("memory_remember",
            Tools.ToolArgs.From(new { content = "Sara is my sister", kind = "person" }), host.Ctx());
        Assert.False(r.Result.Success);
        Assert.Equal(0, host.Get<MemoryStore>().Count());
    }
}

public class TaskAndReminderTests
{
    [Fact]
    public void Task_lifecycle()
    {
        using var host = new TestHost();
        var tasks = host.Get<TaskStore>();
        var t = tasks.Create(new NewTask("Follow up with CityCrep", Priority: "high"));
        Assert.Equal(TaskStates.Pending, t.State);
        tasks.Update(t.Id, new TaskUpdate(State: TaskStates.Waiting));
        Assert.Equal(TaskStates.Waiting, tasks.Get(t.Id)!.State);
        var done = tasks.Update(t.Id, new TaskUpdate(State: TaskStates.Completed))!;
        Assert.NotNull(done.CompletedAt);
        Assert.Empty(tasks.List());
        Assert.Single(tasks.List(includeClosed: true));
        Assert.Throws<ArgumentException>(() => tasks.Update(t.Id, new TaskUpdate(State: "flying")));
    }

    [Fact]
    public async Task Due_reminders_fire_once_as_notifications()
    {
        using var host = new TestHost();
        var reminders = host.Get<ReminderStore>();
        reminders.Create("call Ahmed", DateTimeOffset.Now.AddSeconds(-1));
        reminders.Create("later", DateTimeOffset.Now.AddHours(1));
        var dispatcher = host.Get<ReminderDispatcher>();

        Assert.Equal(1, await dispatcher.FireDueAsync(DateTimeOffset.Now));
        Assert.Equal(0, await dispatcher.FireDueAsync(DateTimeOffset.Now));
        Assert.Contains(host.Get<NotificationCenter>().Recent(), n => n.Title == "Reminder: call Ahmed");
        Assert.Single(reminders.List());
    }
}

public class NotificationPolicyTests
{
    private static readonly NotificationSettings Defaults = new();
    private static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Notification N(NotificationPriority p) => new() { Title = "t", Priority = p };

    [Fact]
    public void Normal_notifications_are_delivered_when_active() =>
        Assert.True(NotificationCenter.Decide(N(NotificationPriority.Normal), new PresenceSnapshot { State = UserState.Active }, Defaults, Noon).DeliverNow);

    [Fact]
    public void Low_priority_goes_to_the_digest() =>
        Assert.False(NotificationCenter.Decide(N(NotificationPriority.Low), new PresenceSnapshot { State = UserState.Active }, Defaults, Noon).DeliverNow);

    [Fact]
    public void Meetings_hold_normal_and_silence_high()
    {
        var meeting = new PresenceSnapshot { State = UserState.InMeeting, InMeeting = true };
        Assert.False(NotificationCenter.Decide(N(NotificationPriority.Normal), meeting, Defaults, Noon).DeliverNow);
        var high = NotificationCenter.Decide(N(NotificationPriority.High), meeting, Defaults, Noon);
        Assert.True(high.DeliverNow);
        Assert.False(high.Mode.HasFlag(DeliveryMode.Spoken));
    }

    [Fact]
    public void Critical_always_gets_through() =>
        Assert.True(NotificationCenter.Decide(N(NotificationPriority.Critical), new PresenceSnapshot { InMeeting = true, State = UserState.InMeeting },
            new NotificationSettings { QuietHoursStart = "00:00", QuietHoursEnd = "23:59" }, Noon).DeliverNow);

    [Fact]
    public void Quiet_hours_wrap_midnight()
    {
        var s = new NotificationSettings { QuietHoursStart = "22:00", QuietHoursEnd = "07:00" };
        Assert.True(NotificationCenter.InQuietHours(s, new DateTimeOffset(DateTime.Today.AddHours(23))));
        Assert.True(NotificationCenter.InQuietHours(s, new DateTimeOffset(DateTime.Today.AddHours(6))));
        Assert.False(NotificationCenter.InQuietHours(s, new DateTimeOffset(DateTime.Today.AddHours(12))));
    }

    [Fact]
    public async Task Duplicates_are_suppressed_and_held_items_flush_as_one_digest()
    {
        using var host = new TestHost();
        var center = host.Get<NotificationCenter>();
        var first = await center.PostAsync(new Notification { Title = "Build failed", Source = "ci", Priority = NotificationPriority.Low });
        var dup = await center.PostAsync(new Notification { Title = "Build failed", Source = "ci", Priority = NotificationPriority.Low });
        Assert.Equal(NotificationStatus.Held, first.Status);
        Assert.Equal(NotificationStatus.Suppressed, dup.Status);
        Assert.Equal(1, await center.FlushHeldAsync());
        Assert.Empty(center.Recent(status: NotificationStatus.Held));
    }
}

public class BriefingTests
{
    [Fact]
    public async Task Briefing_reports_only_what_exists()
    {
        using var host = new TestHost();
        var empty = await host.Say("what's happening today");
        Assert.True(empty.Success);
        Assert.Contains("clear", empty.Reply);

        var tasks = host.Get<Jarvis.Core.Tasks.TaskStore>();
        tasks.Create(new("Sign the CityCrep contract", Priority: "urgent"));
        tasks.Create(new("File the report", DueAt: DateTimeOffset.Now.AddHours(-2)));
        tasks.Create(new("Water the plants", Priority: "low"));
        host.Get<Jarvis.Core.Scheduling.ReminderStore>().Create("call Ahmed", DateTimeOffset.Now.AddMinutes(30));

        var day = await host.Say("brief me");
        Assert.Contains("Sign the CityCrep contract", day.Reply);
        Assert.Contains("Overdue: File the report", day.Reply);
        Assert.Contains("call Ahmed", day.Reply);
        Assert.DoesNotContain("Water the plants", day.Reply);

        var prio = await host.Say("أولوياتي إيه");
        Assert.Contains("Sign the CityCrep contract", prio.Reply);
        Assert.DoesNotContain("call Ahmed", prio.Reply);
        Assert.Equal("ar", prio.Lang);
    }
}

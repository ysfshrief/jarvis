using Jarvis.Core.Scheduling;

namespace Jarvis.Core.Tests;

public sealed class TimerAndNameTests
{
    [Fact]
    public async Task A_short_timer_is_set_without_ai_and_wakes_the_scheduler_right_away()
    {
        using var host = new TestHost();
        var store = host.Get<ReminderStore>();
        var waiting = store.WaitForNewAsync(TimeSpan.FromSeconds(15), default); // the scheduler, between its regular checks

        var r = await host.Say("set a timer for 3 seconds");

        Assert.True(r.Success, r.Reply);
        Assert.Equal("deterministic", r.Route);
        Assert.Contains("in 3 seconds", r.Reply); // not "in 1 minute"
        var timer = Assert.Single(store.List());
        Assert.Equal("Timer done (3 seconds)", timer.Text);
        Assert.True(await Task.WhenAny(waiting, Task.Delay(2000)) == waiting, "the scheduler wasn't woken");

        Assert.Equal(1, await host.Get<ReminderDispatcher>().FireDueAsync(timer.DueAt.AddMilliseconds(1)));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Whats_my_name_answers_from_settings_without_ai()
    {
        using var host = new TestHost();
        var unknown = await host.Say("whats my name");
        Assert.Equal("deterministic", unknown.Route);
        Assert.Contains("Settings → General", unknown.Reply);

        host.Settings.Update(s => s.General.UserName = "Joe");
        Assert.Contains("You're Joe", (await host.Say("whats my name")).Reply);
    }
}

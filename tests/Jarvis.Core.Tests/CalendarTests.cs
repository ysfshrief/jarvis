using System.Net;
using System.Net.Sockets;
using System.Text;
using Jarvis.Core.Agenda;
using Jarvis.Core.Agent;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Tools;
using Jarvis.Core.Workflows;

namespace Jarvis.Core.Tests;

public class DatePhraseTests
{
    // Thursday 8 Oct 2026, 10:00 local.
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 8, 10, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 8, 10, 0, 0)));

    [Theory]
    [InlineData("meeting with ahmed tomorrow at 3pm", "2026-10-09 15:00", "meeting with ahmed")]
    [InlineData("dentist on sunday at 5pm", "2026-10-11 17:00", "dentist")]
    [InlineData("review next thursday 10:30", "2026-10-15 10:30", "review")]
    [InlineData("call mona at 3", "2026-10-08 15:00", "call mona")]
    [InlineData("standup 14 oct at 9am", "2026-10-14 09:00", "standup")]
    [InlineData("lunch at noon", "2026-10-08 12:00", "lunch")]
    [InlineData("اجتماع مع ساره بكره الساعه 11", "2026-10-09 11:00", "اجتماع مع ساره")]
    [InlineData("دكتور يوم الاحد الساعه 5 العصر", "2026-10-11 17:00", "دكتور")]
    [InlineData("مكالمه بعد بكره الساعه ٣", "2026-10-10 15:00", "مكالمه")]
    public void Finds_day_time_and_title(string text, string expected, string rest)
    {
        var p = DatePhrases.Parse(TextNormalizer.Normalize(text), Now);
        Assert.Equal(expected, p.Start(Now)!.Value.ToString("yyyy-MM-dd HH:mm"));
        Assert.Equal(rest, p.Rest);
    }

    [Fact]
    public void Finds_durations()
    {
        Assert.Equal(TimeSpan.FromHours(2), DatePhrases.Parse("workshop tomorrow at 10am for 2 hours", Now).Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), DatePhrases.Parse("sync at 4pm for 30 minutes", Now).Duration);
        Assert.Equal(TimeSpan.FromHours(1), DatePhrases.Parse("review at 4pm for an hour", Now).Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), DatePhrases.Parse(TextNormalizer.Normalize("مكالمه بكره الساعه 2 لمده نص ساعه"), Now).Duration);
    }

    [Fact]
    public void Ranges_for_agenda_questions()
    {
        var (from, to, label) = DatePhrases.Range("tomorrow", Now);
        Assert.Equal(new DateTime(2026, 10, 9), from.Date);
        Assert.Equal(TimeSpan.FromDays(1), to - from);
        Assert.Equal("week", DatePhrases.Range("this week", Now).Label);
        Assert.Equal("today", DatePhrases.Range("today", Now).Label);
    }
}

/// <summary>Serves an ICS file over real HTTP, like Google Calendar's secret address.</summary>
public sealed class IcsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }
    public string Ics { get; set; } = "";

    public IcsServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/private-abc123/basic.ics";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                var bytes = Encoding.UTF8.GetBytes(c.Request.Url!.AbsolutePath.EndsWith(".ics") ? Ics : "<html>not a calendar</html>");
                c.Response.ContentType = "text/calendar";
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}

public sealed class CalendarTests : IDisposable
{
    private readonly IcsServer _server = new();

    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Event(string uid, DateTime startUtc, int minutes, string summary, string? rrule = null, string? attendees = null, string? location = null) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{startUtc.AddMinutes(minutes):yyyyMMdd'T'HHmmss'Z'}\r\nSUMMARY:{summary}\r\n" +
        (rrule is null ? "" : $"RRULE:{rrule}\r\n") + (location is null ? "" : $"LOCATION:{location}\r\n") + (attendees ?? "") + "END:VEVENT\r\n";

    [Fact]
    public async Task Subscribes_to_an_ics_feed_expands_recurrence_and_keeps_the_address_secret()
    {
        using var host = new TestHost();
        var tomorrow = DateTime.UtcNow.Date.AddDays(1).AddHours(9);
        _server.Ics = Ics(
            Event("standup", tomorrow, 15, "Standup", rrule: "FREQ=DAILY;COUNT=5"),
            Event("citycrep", tomorrow.AddHours(4), 60, "CityCrep proposal review", location: "Zoom",
                attendees: "ATTENDEE;CN=Ahmed Hassan:mailto:ahmed@citycrep.com\r\nORGANIZER;CN=Mona:mailto:mona@me.com\r\n"));
        var calendar = host.Get<CalendarService>();
        host.Get<EntityStore>().Upsert(EntityTypes.Organization, "CityCrep");

        var cal = await calendar.SubscribeAsync("Work", _server.Url, default);
        Assert.Equal("ok", cal.Status);
        Assert.Contains(cal.UrlSecret, host.Get<Jarvis.Core.Security.ISecretStore>().Names());
        Assert.DoesNotContain("private-abc123", System.Text.Json.JsonSerializer.Serialize(host.Settings.Current));

        var week = calendar.Store.Between(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
        Assert.Equal(5, week.Count(e => e.Title == "Standup"));
        var review = week.Single(e => e.Title == "CityCrep proposal review");
        Assert.Equal("Zoom", review.Location);
        Assert.Contains(host.Get<EntityStore>().Find("CityCrep")!.Id, calendar.Store.EntityIdsOf(review.Id));

        // A refresh replaces events rather than duplicating them.
        await calendar.SyncAsync(default);
        Assert.Equal(5, calendar.Store.Between(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)).Count(e => e.Title == "Standup"));
    }

    [Fact]
    public async Task Bad_addresses_are_refused_without_saving_anything()
    {
        using var host = new TestHost();
        var calendar = host.Get<CalendarService>();
        await Assert.ThrowsAsync<ArgumentException>(() => calendar.SubscribeAsync("x", "ftp://example.com/cal.ics", default));
        await Assert.ThrowsAsync<CalendarException>(() => calendar.SubscribeAsync("x", _server.Url.Replace(".ics", ".html"), default));
        Assert.Empty(calendar.Store.Calendars());
    }

    [Fact]
    public async Task Agenda_next_meeting_and_adding_events_by_voice_style_commands()
    {
        using var host = new TestHost();
        var first = await host.Say("what's on my calendar today");
        Assert.Contains("empty", first.Reply);

        var add = await host.Say("schedule a meeting with Ahmed tomorrow at 3pm for 30 minutes");
        Assert.True(add.Success, add.Reply);
        Assert.Equal("deterministic", add.Route);
        var e = host.Get<AgendaStore>().Between(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(3)).Single();
        Assert.Equal("Meeting with Ahmed", e.Title);
        Assert.Equal(DateTime.Today.AddDays(1).AddHours(15), e.Start.LocalDateTime);
        Assert.Equal(TimeSpan.FromMinutes(30), e.End - e.Start);

        var tomorrow = await host.Say("do I have meetings tomorrow?");
        Assert.Contains("15:00", tomorrow.Reply);
        Assert.Contains("Meeting with Ahmed", tomorrow.Reply);
        var next = await host.Say("what's my next meeting");
        Assert.Contains("Meeting with Ahmed", next.Reply);

        var arabic = await host.Say("حط اجتماع مع سارة بعد بكرة الساعة 11");
        Assert.True(arabic.Success, arabic.Reply);
        Assert.Contains(host.Get<AgendaStore>().Between(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(3)), x => x.Title == "اجتماع مع سارة" && x.Start.Hour == 11);

        var noTime = await host.Say("schedule a meeting with the board");
        Assert.False(noTime.Success);
    }

    [Fact]
    public async Task Meeting_prep_brings_together_people_work_and_mail()
    {
        using var host = new TestHost();
        var tomorrow = DateTime.UtcNow.Date.AddDays(1).AddHours(11);
        _server.Ics = Ics(Event("m1", tomorrow, 60, "CityCrep pricing call",
            attendees: "ATTENDEE;CN=Ahmed Hassan:mailto:ahmed@citycrep.com\r\n"));
        var k = host.Get<KnowledgeService>();
        k.Relate("Ahmed Hassan", EntityTypes.Person, "works_at", "CityCrep", EntityTypes.Organization, MemorySources.UserExplicit, null);
        host.Get<WorkflowService>().Start("CityCrep deal", "deal", about: "CityCrep");
        await host.Get<CalendarService>().SubscribeAsync("Work", _server.Url, default);

        var prep = await host.Say("prepare me for my CityCrep meeting");
        Assert.True(prep.Success, prep.Reply);
        Assert.Contains("CityCrep pricing call", prep.Reply);
        Assert.Contains("Ahmed Hassan", prep.Reply);
        Assert.Contains("works at CityCrep", prep.Reply);
        Assert.Contains("CityCrep deal", prep.Reply);

        var missing = await host.Say("prepare me for my Atlantis meeting");
        Assert.False(missing.Success);
        Assert.Contains("can't find", missing.Reply);
    }

    [Fact]
    public async Task Reminds_once_before_a_meeting_and_shows_it_in_the_briefing()
    {
        using var host = new TestHost();
        var calendar = host.Get<CalendarService>();
        var now = DateTimeOffset.Now;
        calendar.AddLocal("Budget review", now.AddMinutes(6), now.AddMinutes(36), "Room 2", [new Attendee("Mona", null)], "you");
        calendar.AddLocal("Later thing", now.AddHours(3), now.AddHours(4), null, [], "you");

        Assert.Equal(1, await calendar.RemindAsync(now, default));
        Assert.Equal(0, await calendar.RemindAsync(now.AddMinutes(1), default)); // once only
        var n = host.Get<Jarvis.Core.Notifications.NotificationCenter>().Recent(10).Single(x => x.Source == "calendar");
        Assert.Contains("Budget review", n.Title);
        Assert.Contains("Room 2", n.Body);

        if (now.AddHours(4).Date == now.Date)
        {
            var brief = await host.Say("what's happening today?");
            Assert.Contains("Budget review", brief.Reply);
        }
    }

    public void Dispose() => _server.Dispose();
}

using System.Globalization;
using Jarvis.Core.Agenda;
using Jarvis.Core.Language;

namespace Jarvis.Core.Tools.Builtin;

internal static class AgendaText
{
    public static string Line(AgendaEvent e, ToolContext ctx) =>
        e.AllDay
            ? $"• {ctx.T("all day", "طول اليوم")} — {e.Title}"
            : $"• {e.Start:HH:mm}–{e.End:HH:mm} {e.Title}{(e.Location is { Length: > 0 } l ? $" ({l})" : "")}";

    public static object Data(AgendaEvent e) => new
    {
        id = e.Id, title = e.Title, start = e.Start, end = e.End, allDay = e.AllDay, location = e.Location,
        people = e.People.ToList(), source = e.Source,
    };

    public static string Day(DateTimeOffset d, ToolContext ctx)
    {
        var today = DateTimeOffset.Now.Date;
        return d.Date == today ? ctx.T("today", "النهارده") : d.Date == today.AddDays(1) ? ctx.T("tomorrow", "بكره") : d.ToString("dddd d MMM", ctx.Lang == Lang.Ar ? new CultureInfo("ar-EG") : CultureInfo.InvariantCulture);
    }
}

public sealed class CalendarAgendaTool(CalendarService calendar) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "calendar_agenda",
        Category = "calendar",
        Description = "List calendar events for a day or range: 'today', 'tomorrow', 'friday', '14 Oct', 'this week'.",
        Parameters = [new("when", "string", "Day or range in plain words (default today).")],
    };

    protected override string Describe(ToolArgs args) => $"Calendar for {args.GetString("when") ?? "today"}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var now = DateTimeOffset.Now;
        var (from, to, label) = DatePhrases.Range(TextNormalizer.Normalize(args.GetString("when") ?? "today"), now);
        var events = calendar.Store.Between(from, to);
        var any = calendar.Store.Calendars().Any();
        if (events.Count == 0)
            return Task.FromResult(ToolResult.Ok(any
                ? ctx.T($"Nothing on your calendar {Label(label, from, ctx)}.", $"مفيش حاجة في الأجندة {Label(label, from, ctx)}.")
                : ctx.T("Your calendar is empty — add events here, or subscribe to your Google/Outlook calendar in Settings → Accounts.",
                        "الأجندة فاضية — ضيف مواعيد هنا، أو اربط جوجل/أوتلوك كاليندر من الإعدادات ← الحسابات."), new { events = Array.Empty<object>() }));
        var lines = label == "week"
            ? string.Join("\n", events.GroupBy(e => e.Start.Date).Select(g => $"{AgendaText.Day(g.First().Start, ctx)}:\n" + string.Join("\n", g.Select(e => AgendaText.Line(e, ctx)))))
            : string.Join("\n", events.Select(e => AgendaText.Line(e, ctx)));
        return Task.FromResult(ToolResult.Ok(ctx.T($"{Label(label, from, ctx, cap: true)} ({events.Count}):\n{lines}", $"{Label(label, from, ctx, cap: true)} ({events.Count}):\n{lines}"),
            new { events = events.Select(AgendaText.Data) }));
    }

    private static string Label(string label, DateTimeOffset from, ToolContext ctx, bool cap = false) => label switch
    {
        "week" => ctx.T(cap ? "This week" : "this week", "الأسبوع ده"),
        _ => cap ? Capitalize(AgendaText.Day(from, ctx)) : AgendaText.Day(from, ctx),
    };

    private static string Capitalize(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpper(s[0]) + s[1..] : s;
}

public sealed class CalendarNextTool(CalendarService calendar) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "calendar_next",
        Category = "calendar",
        Description = "The next event on the calendar.",
    };

    protected override string Describe(ToolArgs args) => "Next calendar event";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var e = calendar.Next(DateTimeOffset.Now);
        if (e is null) return Task.FromResult(ToolResult.Ok(ctx.T("Nothing coming up on your calendar.", "مفيش حاجة جاية في الأجندة.")));
        var mins = (int)Math.Round((e.Start - DateTimeOffset.Now).TotalMinutes);
        var when = mins <= 0 ? ctx.T("now", "دلوقتي") : mins < 60 ? ctx.T($"in {mins} min", $"كمان {mins} دقيقة") : $"{AgendaText.Day(e.Start, ctx)} {e.Start:HH:mm}";
        var who = e.People.Take(4).ToList();
        return Task.FromResult(ToolResult.Ok(
            ctx.T($"Next: “{e.Title}” {when}{(e.Location is { Length: > 0 } l ? $" — {l}" : "")}{(who.Count > 0 ? $", with {string.Join(", ", who)}" : "")}.",
                  $"الجاي: «{e.Title}» {when}{(e.Location is { Length: > 0 } l2 ? $" — {l2}" : "")}{(who.Count > 0 ? $"، مع {string.Join("، ", who)}" : "")}."),
            AgendaText.Data(e)));
    }
}

public sealed class CalendarAddTool(CalendarService calendar) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "calendar_add",
        Category = "calendar",
        Description = "Add an event to JARVIS's calendar on this PC (it does not invite anyone or change your online calendar). Give the time in words ('tomorrow at 3pm for an hour') or as ISO start.",
        Parameters =
        [
            new("title", "string", "What it is, e.g. 'Call with Ahmed'.", true),
            new("when", "string", "When, in words: 'tomorrow at 3pm', 'Sunday 10:30 for 2 hours', 'بكره الساعة 3'."),
            new("start", "string", "ISO start time instead of 'when'."),
            new("duration_minutes", "integer", "Length (default 60, or what 'when' says)."),
            new("location", "string", "Place or meeting link."),
            new("attendees", "string", "People, comma-separated (names or emails) — for your reference."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Add “{args.GetString("title")}” to the calendar";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var now = DateTimeOffset.Now;
        var title = args.RequireString("title").Trim();
        DateTimeOffset? start = null;
        TimeSpan? duration = null;
        if (args.GetString("start") is { Length: > 0 } iso && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var s)) start = s;
        if (start is null)
        {
            // The time may be in "when" or still inside the title ("Dentist tomorrow at 5").
            var phrase = DatePhrases.Parse(TextNormalizer.Normalize(args.GetString("when") ?? ""), now);
            if (phrase.Start(now) is null)
            {
                var inTitle = DatePhrases.Parse(TextNormalizer.Normalize(title), now);
                if (inTitle.Start(now) is not null && inTitle.Rest.Length > 0) { phrase = inTitle; title = Restore(title, inTitle.Rest); }
            }
            start = phrase.Start(now);
            duration = phrase.Duration;
        }
        if (start is null)
            return Task.FromResult(ToolResult.Fail(ctx.T($"When should I put “{title}”? Say e.g. “tomorrow at 3pm”.", $"أحط «{title}» امتى؟ قول مثلاً «بكره الساعة 3»."), status: ToolStatus.NotFound));
        if (args.GetInt("duration_minutes") is { } dm and > 0) duration = TimeSpan.FromMinutes(dm);
        var attendees = (args.GetString("attendees") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Contains('@') ? new Attendee(null, a) : new Attendee(a, null)).ToList();
        AgendaEvent e;
        try { e = calendar.AddLocal(title, start.Value, start.Value + (duration ?? TimeSpan.FromHours(1)), args.GetString("location"), attendees, "jarvis"); }
        catch (ArgumentException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
        var note = attendees.Count > 0 ? ctx.T(" Nobody was invited — it's on your calendar only.", " محدش اتبعتله دعوة — ده في أجندتك بس.") : "";
        return Task.FromResult(ToolResult.Ok(
            ctx.T($"Added “{e.Title}” {AgendaText.Day(e.Start, ctx)} {e.Start:HH:mm}–{e.End:HH:mm}.{note}", $"ضفت «{e.Title}» {AgendaText.Day(e.Start, ctx)} {e.Start:HH:mm}–{e.End:HH:mm}.{note}"),
            AgendaText.Data(e)));
    }

    /// <summary>Keeps the user's own capitalisation for the part of the title that remains.</summary>
    private static string Restore(string original, string normalizedRest)
    {
        var words = normalizedRest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var kept = original.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => words.Contains(TextNormalizer.Normalize(w)));
        var s = string.Join(' ', kept).Trim();
        return s.Length > 0 ? s : original;
    }
}

public sealed class CalendarDeleteTool(CalendarService calendar) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "calendar_delete",
        Category = "calendar",
        Risk = RiskLevel.Sensitive,
        Description = "Remove an event from JARVIS's own calendar (subscribed calendars are read-only).",
        Parameters = [new("event", "string", "Event id.", true)],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx) =>
        new(RiskLevel.Sensitive, calendar.Store.Get(args.RequireString("event")) is { } e ? $"Remove “{e.Title}” ({e.Start:ddd d MMM HH:mm}) from the calendar" : "Remove a calendar event");

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var e = calendar.Store.Get(args.RequireString("event"));
        if (e is null) return Task.FromResult(ToolResult.Fail(ctx.T("I can't find that event.", "ملقتش الميعاد ده."), status: ToolStatus.NotFound));
        if (e.Source == "ics") return Task.FromResult(ToolResult.Fail(ctx.T("That event comes from a subscribed calendar; change it there.", "الميعاد ده من كاليندر مربوط؛ غيره من هناك."), status: ToolStatus.Denied));
        calendar.Store.DeleteEvent(e.Id);
        return Task.FromResult(ToolResult.Ok(ctx.T($"Removed “{e.Title}”.", $"شلت «{e.Title}».")));
    }
}

public sealed class MeetingPrepTool(CalendarService calendar) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "meeting_prep",
        Category = "calendar",
        ReadsUntrustedContent = true,
        Description = "Prepare the user for a meeting: who's coming and what JARVIS knows about them, related tracked work, open tasks, recent email and documents. Give the meeting's name/company, or nothing for the next meeting.",
        Parameters = [new("meeting", "string", "Which meeting (title, person or company).")],
    };

    protected override string Describe(ToolArgs args) => $"Prepare for {args.GetString("meeting") ?? "the next meeting"}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var q = args.GetString("meeting") ?? "";
        var e = q.Length > 0 ? calendar.Find(TextNormalizer.Normalize(q), DateTimeOffset.Now) : calendar.Next(DateTimeOffset.Now);
        if (e is null)
            return Task.FromResult(ToolResult.Fail(ctx.T($"I can't find {(q.Length > 0 ? $"a “{q}” meeting" : "an upcoming meeting")} on your calendar.", $"ملقتش {(q.Length > 0 ? $"اجتماع «{q}»" : "اجتماع جاي")} في الأجندة."), status: ToolStatus.NotFound));
        var brief = calendar.Prepare(e);
        return Task.FromResult(ToolResult.Ok(CalendarService.Render(brief, ctx), new
        {
            meeting = AgendaText.Data(e),
            people = brief.People.Select(p => new { name = p.Person, known = p.Entity?.Name, facts = p.Facts }),
            workflows = brief.Workflows.Select(w => w.Title),
            tasks = brief.OpenTasks.Select(t => t.Title),
            recentEmail = brief.RecentMail.Select(m => new { from = m.Sender, m.Subject, m.ReceivedAt }),
            documents = brief.Files.Select(f => f.Name),
            notes = e.Description is { Length: > 0 } d ? new { untrustedContent = d.Length > 1500 ? d[..1500] : d } : null,
        }));
    }
}

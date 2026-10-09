using System.Text;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Jarvis.Core.Activity;
using Jarvis.Core.Files;
using Jarvis.Core.Inbox;
using Jarvis.Core.Memory;
using Jarvis.Core.Security;
using Jarvis.Core.Workflows;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agenda;

public sealed class CalendarException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything JARVIS knows that's relevant to a meeting, gathered from real data.</summary>
public sealed record MeetingBrief(
    AgendaEvent Event,
    IReadOnlyList<(string Person, Entity? Entity, IReadOnlyList<string> Facts)> People,
    IReadOnlyList<InboxMessage> RecentMail,
    IReadOnlyList<Workflow> Workflows,
    IReadOnlyList<Tasks.TaskItem> OpenTasks,
    IReadOnlyList<IndexedFile> Files,
    AgendaEvent? LastTime);

/// <summary>
/// Calendars: read-only ICS subscriptions (the private "secret address" Google Calendar, Outlook and
/// iCloud offer — stored as a secret) and JARVIS's own local calendar. Events are linked to the people
/// and organisations JARVIS knows, so it can prepare you for a meeting from what it really has.
/// </summary>
public sealed class CalendarService(
    AgendaStore store,
    HttpClient http,
    ISecretStore secrets,
    EntityStore entities,
    KnowledgeService knowledge,
    InboxStore mail,
    WorkflowStore workflows,
    FileIndex files,
    ActivityLog activity,
    Notifications.NotificationCenter notifications,
    Settings.ISettingsStore settings,
    ILogger<CalendarService> logger)
{
    public static readonly TimeSpan Past = TimeSpan.FromDays(30);
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(120);
    private readonly SemaphoreSlim _sync = new(1, 1);

    public AgendaStore Store => store;

    public async Task<AgendaCalendar> SubscribeAsync(string name, string url, CancellationToken ct)
    {
        var uri = NormalizeUrl(url);
        var text = await FetchAsync(uri, ct).ConfigureAwait(false);
        var events = Parse(text, "pending", DateTimeOffset.Now);
        var cal = store.AddCalendar(string.IsNullOrWhiteSpace(name) ? uri.Host : name, CalendarKinds.Ics);
        secrets.Set(cal.UrlSecret, uri.AbsoluteUri);
        Link(store.ReplaceEvents(cal.Id, events.Select(e => e with { CalendarId = cal.Id }).ToList()) > 0 ? cal.Id : null);
        store.SetStatus(cal.Id, "ok", null, synced: true);
        activity.Record(ActivityKinds.System, $"Calendar subscribed: {cal.Name} ({events.Count} events)", status: "ok");
        return store.Calendar(cal.Id)!;
    }

    public bool Remove(string id)
    {
        var c = store.Calendar(id);
        if (c is null) return false;
        secrets.Remove(c.UrlSecret);
        return store.RemoveCalendar(id);
    }

    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var total = 0;
            foreach (var c in store.Calendars().Where(c => c.Enabled && c.Kind == CalendarKinds.Ics))
            {
                try
                {
                    var url = secrets.Get(c.UrlSecret) ?? throw new CalendarException("The calendar address is missing; add it again.");
                    var events = Parse(await FetchAsync(new Uri(url), ct).ConfigureAwait(false), c.Id, DateTimeOffset.Now);
                    total += store.ReplaceEvents(c.Id, events);
                    Link(c.Id);
                    store.SetStatus(c.Id, "ok", null, synced: true);
                }
                catch (Exception ex) when (ex is CalendarException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Calendar sync failed for {Calendar}", c.Name);
                    store.SetStatus(c.Id, "error", ex.Message);
                }
            }
            return total;
        }
        finally { _sync.Release(); }
    }

    /// <summary>Adds an event to JARVIS's own calendar.</summary>
    public AgendaEvent AddLocal(string title, DateTimeOffset start, DateTimeOffset end, string? location, IReadOnlyList<Attendee> attendees, string source)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("The event needs a title.");
        if (end <= start) throw new ArgumentException("The event must end after it starts.");
        var cal = store.Local();
        var e = store.AddEvent(new AgendaEvent
        {
            Id = Guid.NewGuid().ToString("n"), CalendarId = cal.Id, Uid = Guid.NewGuid().ToString("n"), Title = title.Trim(), Start = start, End = end,
            Location = location, Attendees = attendees, Source = source,
        });
        LinkEvent(e);
        return e;
    }

    /// <summary>Reminds you once about each meeting starting within the reminder window.</summary>
    public async Task<int> RemindAsync(DateTimeOffset now, CancellationToken ct)
    {
        var minutes = settings.Current.Calendar.ReminderMinutes;
        if (minutes <= 0) return 0;
        var sent = 0;
        foreach (var e in store.Between(now, now.AddMinutes(minutes)).Where(e => !e.AllDay && !e.Reminded && e.Start >= now))
        {
            var ar = Jarvis.Core.Settings.LanguagePolicy.Default(settings.Current) == Jarvis.Core.Language.Lang.Ar;
            var inMin = Math.Max(1, (int)Math.Round((e.Start - now).TotalMinutes));
            var who = e.People.Take(3).ToList();
            await notifications.PostAsync(new Notifications.Notification
            {
                Title = ar ? $"كمان {inMin} دقيقة: {e.Title}" : $"In {inMin} min: {e.Title}",
                Body = string.Join(" · ", new[] { e.Location, who.Count > 0 ? (ar ? "مع " : "with ") + string.Join(", ", who) : null }.OfType<string>()),
                Priority = Notifications.NotificationPriority.High,
                Source = "calendar",
                GroupKey = $"cal-{e.Id}",
            }, ct).ConfigureAwait(false);
            store.MarkReminded(e.Id);
            sent++;
        }
        return sent;
    }

    public AgendaEvent? Next(DateTimeOffset now) =>
        store.Between(now, now.Add(Ahead), 50).FirstOrDefault(e => !e.AllDay && e.Start >= now.AddMinutes(-5));

    /// <summary>Finds the meeting a phrase refers to ("CityCrep", "the board meeting"): soonest upcoming match, else the latest past one.</summary>
    public AgendaEvent? Find(string query, DateTimeOffset now)
    {
        query = System.Text.RegularExpressions.Regex.Replace(query, @"\b(?:my|the|our|next|meeting|call|with)\b|اجتماع|ميتنج|مع", " ").Trim();
        if (query.Length == 0) return Next(now);
        var upcoming = store.Search(query, now.AddHours(-1), now.Add(Ahead), 5);
        if (upcoming.Count > 0) return upcoming[0];
        if (entities.Find(query) is { } entity && store.AboutEntity(entity.Id, 10).Where(e => e.End >= now).OrderBy(e => e.Start).FirstOrDefault() is { } byEntity) return byEntity;
        return store.Search(query, now.Subtract(Past), now, 50).LastOrDefault();
    }

    public MeetingBrief Prepare(AgendaEvent e)
    {
        var linked = store.EntityIdsOf(e.Id).Select(entities.Get).OfType<Entity>().ToList();
        var people = new List<(string, Entity?, IReadOnlyList<string>)>();
        foreach (var a in e.Attendees.Append(e.Organizer).OfType<Attendee>().DistinctBy(a => a.Email ?? a.Name))
        {
            var label = a.Name ?? a.Email ?? "";
            var entity = PersonOf(a) ?? OrgOf(a.Email);
            var facts = entity is null ? [] : Facts(entity);
            people.Add((label, entity, facts));
        }
        foreach (var en in linked.Where(l => people.All(p => p.Item2?.Id != l.Id)))
            people.Add((en.Name, en, Facts(en)));

        var emails = e.Attendees.Append(e.Organizer).OfType<Attendee>().Select(a => a.Email).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recentMail = emails.SelectMany(addr => mail.FromSender(addr, 5))
            .Concat(linked.SelectMany(en => mail.AboutEntity(en.Id, 5)))
            .DistinctBy(m => m.Id).OrderByDescending(m => m.ReceivedAt).Take(5).ToList();
        var entityIds = people.Select(p => p.Item2?.Id).OfType<string>().ToHashSet();
        var wfs = workflows.List().Where(w => w.EntityId is { } id && entityIds.Contains(id)).ToList();
        var tasks = people.Select(p => p.Item2).OfType<Entity>().SelectMany(en => knowledge.Profile(en).Tasks).DistinctBy(t => t.Id).Take(6).ToList();
        var docs = entityIds.SelectMany(id => files.AboutEntity(id, 5)).DistinctBy(f => f.Id).OrderByDescending(f => f.ModifiedAt).Take(4).ToList();
        var last = store.Search(e.Title, e.Start.Subtract(Past), e.Start.AddMinutes(-1), 50).LastOrDefault(x => x.Id != e.Id);
        return new MeetingBrief(e, people, recentMail, wfs, tasks, docs, last);
    }

    public static string Render(MeetingBrief b, Tools.ToolContext ctx)
    {
        var e = b.Event;
        var sb = new StringBuilder();
        var when = e.AllDay ? e.Start.ToString("ddd d MMM") : $"{e.Start:ddd d MMM HH:mm}–{e.End:HH:mm}";
        sb.AppendLine(ctx.T($"“{e.Title}” — {when}{(e.Location is { Length: > 0 } l ? $", {l}" : "")}.", $"«{e.Title}» — {when}{(e.Location is { Length: > 0 } l2 ? $"، {l2}" : "")}."));
        if (b.People.Count > 0)
        {
            sb.AppendLine(ctx.T("Who:", "مين:"));
            foreach (var (person, entity, facts) in b.People.Take(8))
                sb.AppendLine($"• {person}{(entity is not null && entity.Name != person ? $" ({entity.Name})" : "")}{(facts.Count > 0 ? " — " + string.Join("; ", facts.Take(3)) : "")}");
        }
        if (b.Workflows.Count > 0)
        {
            sb.AppendLine(ctx.T("What you're tracking:", "اللي بتتابعه:"));
            foreach (var w in b.Workflows) sb.AppendLine("• " + WorkflowService.Summary(w, ctx.Lang));
        }
        if (b.OpenTasks.Count > 0)
            sb.AppendLine(ctx.T($"Open tasks: {string.Join(", ", b.OpenTasks.Select(t => t.Title))}.", $"مهام مفتوحة: {string.Join("، ", b.OpenTasks.Select(t => t.Title))}."));
        if (b.RecentMail.Count > 0)
        {
            sb.AppendLine(ctx.T("Recent email:", "آخر إيميلات:"));
            foreach (var m in b.RecentMail) sb.AppendLine($"• {m.Sender} — {m.Subject} ({m.ReceivedAt:d MMM})");
        }
        if (b.Files.Count > 0)
            sb.AppendLine(ctx.T($"Documents: {string.Join(", ", b.Files.Select(f => f.Name))}.", $"ملفات: {string.Join("، ", b.Files.Select(f => f.Name))}."));
        if (b.LastTime is { } last)
            sb.AppendLine(ctx.T($"Last time: {last.Start:ddd d MMM}.", $"آخر مرة: {last.Start:ddd d MMM}."));
        if (b.People.Count == 0 && b.Workflows.Count == 0 && b.RecentMail.Count == 0 && b.Files.Count == 0)
            sb.AppendLine(ctx.T("I don't have anything else on this meeting yet — no linked people, mail, files or tracked work.", "معنديش حاجة تانية عن الاجتماع ده لسه — لا ناس ولا إيميلات ولا ملفات ولا متابعات."));
        if (e.Description is { Length: > 0 } d)
            sb.AppendLine(ctx.T("Invite notes: ", "ملاحظات الدعوة: ") + (d.Length > 400 ? d[..397] + "…" : d).ReplaceLineEndings(" "));
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----

    private IReadOnlyList<string> Facts(Entity e)
    {
        var p = knowledge.Profile(e);
        return p.Relations.Select(r => $"{r.FromName} {r.Type.Replace('_', ' ')} {r.ToName}")
            .Concat(p.Memories.Where(m => m.IsConfirmed).Select(m => m.Content))
            .Distinct().Take(4).ToList();
    }

    /// <summary>"Ahmed Hassan" &lt;ahmed@citycrep.com&gt; → the known person "Ahmed" if he works at CityCrep.</summary>
    private Entity? PersonOf(Attendee a)
    {
        if (a.Name is not { Length: > 0 } name) return null;
        if (entities.Find(name, EntityTypes.Person) is { } exact) return exact;
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (OrgOf(a.Email) is not { } org || entities.Find(first, EntityTypes.Person) is not { } candidate) return null;
        return knowledge.Profile(candidate).Relations.Any(r => r.ToId == org.Id || r.FromId == org.Id) ? candidate : null;
    }

    private Entity? OrgOf(string? email)
    {
        var parts = (email?.Split('@').LastOrDefault() ?? "").Split('.');
        return parts.Length >= 2 ? entities.Find(parts[^2], EntityTypes.Organization) : null;
    }

    private void Link(string? calendarId)
    {
        if (calendarId is null) return;
        foreach (var e in store.Between(DateTimeOffset.Now.Subtract(Past), DateTimeOffset.Now.Add(Ahead), 2000).Where(e => e.CalendarId == calendarId))
            LinkEvent(e);
    }

    private void LinkEvent(AgendaEvent e)
    {
        foreach (var en in entities.Mentioned($"{e.Title} {string.Join(' ', e.People)}")) store.LinkEntity(e.Id, en.Id);
        foreach (var a in e.Attendees.Append(e.Organizer).OfType<Attendee>())
            if (OrgOf(a.Email) is { } org) store.LinkEntity(e.Id, org.Id);
    }

    internal static Uri NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("That isn't a calendar address (it should start with https:// or webcal://).");
        return uri;
    }

    private async Task<string> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new CalendarException($"The calendar server answered {(int)resp.StatusCode}.");
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new CalendarException("That address doesn't return a calendar (iCal/ICS).");
        return text;
    }

    /// <summary>Expands an iCalendar feed (recurrences included) into events within the window JARVIS keeps.</summary>
    internal static IReadOnlyList<AgendaEvent> Parse(string ics, string calendarId, DateTimeOffset now)
    {
        Ical.Net.Calendar? cal;
        try { cal = Ical.Net.Calendar.Load(ics); }
        catch (Exception ex) { throw new CalendarException("The calendar file couldn't be read.", ex); }
        if (cal is null) throw new CalendarException("The calendar file is empty.");
        var from = now.Subtract(Past).UtcDateTime;
        var to = now.Add(Ahead).UtcDateTime;
        var list = new List<AgendaEvent>();
        foreach (var o in cal.GetOccurrences(new CalDateTime(from, "UTC")).TakeWhile(o => o.Period.StartTime.AsUtc < to))
        {
            if (o.Source is not CalendarEvent ev || string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var start = new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero);
            var end = o.Period.EffectiveEndTime is { } en ? new DateTimeOffset(en.AsUtc, TimeSpan.Zero) : start.AddHours(ev.IsAllDay ? 24 : 1);
            if (ev.IsAllDay)
            {
                // All-day dates are calendar days, not instants: keep them on the local day.
                start = new DateTimeOffset(o.Period.StartTime.Value.Date, TimeZoneInfo.Local.GetUtcOffset(o.Period.StartTime.Value.Date));
                end = start.AddDays(Math.Max(1, (int)Math.Round((end - new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero)).TotalDays)));
            }
            list.Add(new AgendaEvent
            {
                Id = Guid.NewGuid().ToString("n"), CalendarId = calendarId, Uid = ev.Uid ?? Guid.NewGuid().ToString("n"),
                Title = string.IsNullOrWhiteSpace(ev.Summary) ? "(no title)" : ev.Summary.Trim(), Start = start, End = end, AllDay = ev.IsAllDay,
                Location = string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location.Trim(), Description = ev.Description?.Trim(),
                Organizer = ev.Organizer is null ? null : new Attendee(ev.Organizer.CommonName, Email(ev.Organizer.Value)),
                Attendees = ev.Attendees.Select(a => new Attendee(a.CommonName, Email(a.Value))).ToList(), Source = "ics",
            });
        }
        return list;
    }

    private static string? Email(Uri? u) => u is null ? null : u.Scheme == "mailto" ? u.UserInfo.Length > 0 ? $"{u.UserInfo}@{u.Host}" : u.OriginalString[7..] : u.OriginalString;
}

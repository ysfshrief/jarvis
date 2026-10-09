using System.Text.Json;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Agenda;

public static class CalendarKinds
{
    /// <summary>A read-only iCalendar (ICS) subscription, e.g. Google Calendar's secret address.</summary>
    public const string Ics = "ics";
    /// <summary>JARVIS's own calendar on this PC.</summary>
    public const string Local = "local";
}

public sealed record AgendaCalendar
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? Color { get; init; }
    public bool Enabled { get; init; } = true;
    public string Status { get; init; } = "new";
    public string? StatusMessage { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public string UrlSecret => $"calendar.{Id}.url";
}

public sealed record Attendee(string? Name, string? Email);

public sealed record AgendaEvent
{
    public required string Id { get; init; }
    public required string CalendarId { get; init; }
    public required string Uid { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public bool AllDay { get; init; }
    public string? Location { get; init; }
    public string? Description { get; init; }
    public Attendee? Organizer { get; init; }
    public IReadOnlyList<Attendee> Attendees { get; init; } = [];
    /// <summary>"ics", "you" or "jarvis".</summary>
    public string Source { get; init; } = "ics";
    public bool Reminded { get; init; }

    public bool IsMeeting => Attendees.Count > 0 || Organizer is not null;
    public IEnumerable<string> People => Attendees.Append(Organizer).OfType<Attendee>().Select(a => a.Name ?? a.Email ?? "").Where(n => n.Length > 0).Distinct();
}

/// <summary>Calendars and their (expanded) events, stored locally.</summary>
public sealed class AgendaStore(JarvisDatabase db, IEventBus events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgendaCalendar AddCalendar(string name, string kind, string? color = null)
    {
        var c = new AgendaCalendar { Id = Guid.NewGuid().ToString("n"), Name = name.Trim(), Kind = kind, Color = color, CreatedAt = DateTimeOffset.Now, Status = kind == CalendarKinds.Local ? "ok" : "new" };
        Exec("INSERT INTO calendars(id, name, kind, color, enabled, status, created_at) VALUES ($id, $n, $k, $c, 1, $s, $t);", cmd =>
        {
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$c", (object?)color ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", c.Status);
            cmd.Parameters.AddWithValue("$t", JarvisDatabase.Format(c.CreatedAt));
        });
        Changed();
        return c;
    }

    public IReadOnlyList<AgendaCalendar> Calendars()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, kind, color, enabled, status, status_message, last_sync, created_at FROM calendars ORDER BY created_at;";
        using var r = cmd.ExecuteReader();
        var list = new List<AgendaCalendar>();
        while (r.Read())
            list.Add(new AgendaCalendar
            {
                Id = r.GetString(0), Name = r.GetString(1), Kind = r.GetString(2), Color = r.IsDBNull(3) ? null : r.GetString(3), Enabled = r.GetInt64(4) == 1,
                Status = r.GetString(5), StatusMessage = r.IsDBNull(6) ? null : r.GetString(6), LastSync = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
                CreatedAt = DateTimeOffset.Parse(r.GetString(8)),
            });
        return list;
    }

    public AgendaCalendar? Calendar(string id) => Calendars().FirstOrDefault(c => c.Id == id);

    /// <summary>JARVIS's own calendar, created on first use.</summary>
    public AgendaCalendar Local() => Calendars().FirstOrDefault(c => c.Kind == CalendarKinds.Local) ?? AddCalendar("JARVIS", CalendarKinds.Local, "#3ee6ff");

    public void SetStatus(string id, string status, string? message, bool synced = false)
    {
        Exec($"UPDATE calendars SET status = $s, status_message = $m{(synced ? ", last_sync = $t" : "")} WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", status);
            c.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
            if (synced) c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Changed();
    }

    public bool RemoveCalendar(string id)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = "DELETE FROM event_entities WHERE event_id IN (SELECT id FROM events WHERE calendar_id = $id); DELETE FROM events WHERE calendar_id = $id;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM calendars WHERE id = $id;";
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        Changed();
        return n > 0;
    }

    /// <summary>Replaces a subscription's events with a fresh copy, keeping "already reminded" marks.</summary>
    public int ReplaceEvents(string calendarId, IReadOnlyList<AgendaEvent> fresh)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var reminded = new HashSet<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT uid || '|' || start_at FROM events WHERE calendar_id = $c AND reminded = 1;";
            cmd.Parameters.AddWithValue("$c", calendarId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) reminded.Add(r.GetString(0));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM event_entities WHERE event_id IN (SELECT id FROM events WHERE calendar_id = $c); DELETE FROM events WHERE calendar_id = $c;";
            cmd.Parameters.AddWithValue("$c", calendarId);
            cmd.ExecuteNonQuery();
        }
        foreach (var e in fresh)
            Insert(conn, tx, e with { CalendarId = calendarId, Reminded = reminded.Contains($"{e.Uid}|{JarvisDatabase.Format(e.Start)}") });
        tx.Commit();
        Changed();
        return fresh.Count;
    }

    public AgendaEvent AddEvent(AgendaEvent e)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Insert(conn, tx, e);
        tx.Commit();
        Changed();
        return e;
    }

    public bool DeleteEvent(string id)
    {
        var n = Exec("DELETE FROM event_entities WHERE event_id = $id; DELETE FROM events WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id));
        Changed();
        return n > 0;
    }

    public AgendaEvent? Get(string id) => Query("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id), 1).FirstOrDefault();

    /// <summary>Events overlapping [from, to), earliest first.</summary>
    public IReadOnlyList<AgendaEvent> Between(DateTimeOffset from, DateTimeOffset to, int limit = 200) =>
        Query("WHERE end_at > $f AND start_at < $t AND calendar_id IN (SELECT id FROM calendars WHERE enabled = 1)", c =>
        {
            c.Parameters.AddWithValue("$f", JarvisDatabase.Format(from.ToUniversalTime()));
            c.Parameters.AddWithValue("$t", JarvisDatabase.Format(to.ToUniversalTime()));
        }, limit, ascending: true);

    public IReadOnlyList<AgendaEvent> Search(string query, DateTimeOffset from, DateTimeOffset to, int limit = 20)
    {
        var words = TextNormalizer.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        if (words.Count == 0) return [];
        return Between(from, to, 500).Where(e =>
        {
            var hay = Haystack(e);
            return words.All(hay.Contains);
        }).Take(limit).ToList();
    }

    public void MarkReminded(string id) => Exec("UPDATE events SET reminded = 1 WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id));

    public void LinkEntity(string eventId, string entityId) =>
        Exec("INSERT OR IGNORE INTO event_entities(event_id, entity_id) VALUES ($e, $n);", c => { c.Parameters.AddWithValue("$e", eventId); c.Parameters.AddWithValue("$n", entityId); });

    public IReadOnlyList<string> EntityIdsOf(string eventId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM event_entities WHERE event_id = $e;";
        cmd.Parameters.AddWithValue("$e", eventId);
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    public IReadOnlyList<AgendaEvent> AboutEntity(string entityId, int limit = 20) =>
        Query("WHERE id IN (SELECT event_id FROM event_entities WHERE entity_id = $e)", c => c.Parameters.AddWithValue("$e", entityId), limit, ascending: false);

    // ---- helpers ----

    private static void Insert(SqliteConnection conn, SqliteTransaction tx, AgendaEvent e)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO events(id, calendar_id, uid, title, start_at, end_at, all_day, location, description, organizer, attendees, source, reminded, updated_at, search_text)
            VALUES ($id, $cal, $uid, $title, $s, $e, $ad, $loc, $desc, $org, $att, $src, $rem, $t, $search);
            """;
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$cal", e.CalendarId);
        cmd.Parameters.AddWithValue("$uid", e.Uid);
        cmd.Parameters.AddWithValue("$title", e.Title);
        cmd.Parameters.AddWithValue("$s", JarvisDatabase.Format(e.Start.ToUniversalTime()));
        cmd.Parameters.AddWithValue("$e", JarvisDatabase.Format(e.End.ToUniversalTime()));
        cmd.Parameters.AddWithValue("$ad", e.AllDay ? 1 : 0);
        cmd.Parameters.AddWithValue("$loc", (object?)e.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", (object?)(e.Description is { Length: > 4000 } d ? d[..4000] : e.Description) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$org", e.Organizer is null ? DBNull.Value : JsonSerializer.Serialize(e.Organizer, Json));
        cmd.Parameters.AddWithValue("$att", JsonSerializer.Serialize(e.Attendees, Json));
        cmd.Parameters.AddWithValue("$src", e.Source);
        cmd.Parameters.AddWithValue("$rem", e.Reminded ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        cmd.Parameters.AddWithValue("$search", Haystack(e));
        cmd.ExecuteNonQuery();
    }

    private static string Haystack(AgendaEvent e) =>
        TextNormalizer.Normalize($"{e.Title} {e.Location} {string.Join(' ', e.People)} {string.Join(' ', e.Attendees.Select(a => a.Email))}");

    private IReadOnlyList<AgendaEvent> Query(string where, Action<SqliteCommand> bind, int limit, bool ascending = true)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, calendar_id, uid, title, start_at, end_at, all_day, location, description, organizer, attendees, source, reminded
            FROM events {where} ORDER BY start_at {(ascending ? "ASC" : "DESC")} LIMIT {Math.Clamp(limit, 1, 2000)};
            """;
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<AgendaEvent>();
        while (r.Read())
            list.Add(new AgendaEvent
            {
                Id = r.GetString(0), CalendarId = r.GetString(1), Uid = r.GetString(2), Title = r.GetString(3),
                Start = DateTimeOffset.Parse(r.GetString(4)).ToLocalTime(), End = DateTimeOffset.Parse(r.GetString(5)).ToLocalTime(), AllDay = r.GetInt64(6) == 1,
                Location = r.IsDBNull(7) ? null : r.GetString(7), Description = r.IsDBNull(8) ? null : r.GetString(8),
                Organizer = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<Attendee>(r.GetString(9), Json),
                Attendees = r.IsDBNull(10) ? [] : JsonSerializer.Deserialize<List<Attendee>>(r.GetString(10), Json) ?? [],
                Source = r.GetString(11), Reminded = r.GetInt64(12) == 1,
            });
        return list;
    }

    private int Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return cmd.ExecuteNonQuery();
    }

    private void Changed() => events.Publish(EventTypes.CalendarChanged, new { });
}

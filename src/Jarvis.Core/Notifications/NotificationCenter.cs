using System.Globalization;
using System.Text.Json.Serialization;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Jarvis.Core.Presence;
using Jarvis.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<NotificationPriority>))]
public enum NotificationPriority { Low, Normal, High, Critical }

public static class NotificationStatus
{
    public const string Delivered = "delivered";
    public const string Held = "held";
    public const string Suppressed = "suppressed";
    public const string Read = "read";
}

public sealed record Notification
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string Title { get; init; }
    public string? Body { get; init; }
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;
    public string Source { get; init; } = "jarvis";
    /// <summary>Notifications with the same group key within a short window are treated as duplicates.</summary>
    public string? GroupKey { get; init; }
    /// <summary>Spoken text, if different from the title (e.g. already phrased for the user).</summary>
    public string? Speech { get; init; }
    public string? Lang { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Status { get; init; } = NotificationStatus.Delivered;
}

/// <summary>A delivery channel: Windows toast, voice, the dashboard, a paired phone later.</summary>
public interface INotificationSink
{
    string Name { get; }
    Task DeliverAsync(Notification notification, DeliveryMode mode, CancellationToken ct);
}

[Flags]
public enum DeliveryMode { None = 0, Visual = 1, Spoken = 2 }

public sealed record DeliveryDecision(bool DeliverNow, DeliveryMode Mode, string Reason);

/// <summary>
/// Notification intelligence: dedupe, decide whether now is a good moment to interrupt
/// (based on priority, presence and quiet hours), hold the rest, and summarize held items
/// when the user becomes available again. JARVIS itself must never become the noise.
/// </summary>
public sealed class NotificationCenter
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(5);

    private readonly JarvisDatabase _db;
    private readonly IEventBus _events;
    private readonly ISettingsStore _settings;
    private readonly PresenceTracker _presence;
    private readonly IEnumerable<INotificationSink> _sinks;
    private readonly ILogger<NotificationCenter> _logger;
    private readonly Dictionary<string, DateTimeOffset> _recent = new();
    private readonly object _gate = new();

    public NotificationCenter(JarvisDatabase db, IEventBus events, ISettingsStore settings, PresenceTracker presence,
        IEnumerable<INotificationSink> sinks, ILogger<NotificationCenter> logger)
    {
        _db = db;
        _events = events;
        _settings = settings;
        _presence = presence;
        _sinks = sinks;
        _logger = logger;
        _presence.StateChanged += OnPresenceChanged;
    }

    public async Task<Notification> PostAsync(Notification n, CancellationToken ct = default)
    {
        var key = n.GroupKey ?? $"{n.Source}:{n.Title}";
        lock (_gate)
        {
            if (_recent.TryGetValue(key, out var last) && n.Timestamp - last < DedupeWindow && n.Priority < NotificationPriority.Critical)
            {
                var dup = n with { Status = NotificationStatus.Suppressed };
                Save(dup);
                return dup;
            }
            _recent[key] = n.Timestamp;
        }

        var decision = Decide(n, _presence.Current, _settings.Current.Notifications, DateTimeOffset.Now);
        var stored = n with { Status = decision.DeliverNow ? NotificationStatus.Delivered : NotificationStatus.Held };
        Save(stored);
        _events.Publish(EventTypes.Notification, new { notification = stored, decision });

        if (decision.DeliverNow) await DeliverAsync(stored, decision.Mode, ct).ConfigureAwait(false);
        return stored;
    }

    /// <summary>The interruption policy. Pure function so it can be tested exhaustively.</summary>
    public static DeliveryDecision Decide(Notification n, PresenceSnapshot presence, NotificationSettings s, DateTimeOffset now)
    {
        var spoken = s.SpeakImportant ? DeliveryMode.Spoken : DeliveryMode.None;
        var visual = s.ToastsEnabled ? DeliveryMode.Visual : DeliveryMode.None;

        if (n.Priority == NotificationPriority.Critical)
            return new(true, visual | (presence.InMeeting ? DeliveryMode.None : spoken), "Critical: always delivered.");

        if (InQuietHours(s, now))
            return new(false, DeliveryMode.None, "Quiet hours.");

        if (presence.InMeeting && s.HoldDuringMeetings)
        {
            return n.Priority == NotificationPriority.High
                ? new(true, visual, "In a meeting: high priority shown silently.")
                : new(false, DeliveryMode.None, "In a meeting: held for later.");
        }

        if (presence.IsFullscreen && s.HoldDuringFullscreen && n.Priority < NotificationPriority.High)
            return new(false, DeliveryMode.None, "Fullscreen app: held for later.");

        if (n.Priority == NotificationPriority.Low)
            return new(false, DeliveryMode.None, "Low priority: added to the digest.");

        var mode = visual;
        if (n.Priority == NotificationPriority.High && presence.State is UserState.Active or UserState.Idle or UserState.Unknown)
            mode |= spoken;
        return new(true, mode, "Delivered.");
    }

    public static bool InQuietHours(NotificationSettings s, DateTimeOffset now)
    {
        if (!TimeOnly.TryParseExact(s.QuietHoursStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(s.QuietHoursEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return false;
        var t = TimeOnly.FromDateTime(now.LocalDateTime);
        return start <= end ? t >= start && t < end : t >= start || t < end;
    }

    public IReadOnlyList<Notification> Recent(int limit = 50, string? status = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, title, body, priority, source, group_key, status FROM notifications
            WHERE ($status IS NULL OR status = $status) ORDER BY ts DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        var list = new List<Notification>();
        while (r.Read())
        {
            list.Add(new Notification
            {
                Id = r.GetString(0), Timestamp = JarvisDatabase.Parse(r.GetString(1)), Title = r.GetString(2),
                Body = r.IsDBNull(3) ? null : r.GetString(3),
                Priority = Enum.TryParse<NotificationPriority>(r.GetString(4), out var p) ? p : NotificationPriority.Normal,
                Source = r.IsDBNull(5) ? "jarvis" : r.GetString(5), GroupKey = r.IsDBNull(6) ? null : r.GetString(6),
                Status = r.GetString(7),
            });
        }
        return list;
    }

    public void MarkRead(string id) => SetStatus(id, NotificationStatus.Read);

    /// <summary>Deliver everything that was held, as one summary instead of a burst.</summary>
    public async Task<int> FlushHeldAsync(CancellationToken ct = default)
    {
        var held = Recent(100, NotificationStatus.Held).OrderBy(n => n.Timestamp).ToList();
        if (held.Count == 0) return 0;
        foreach (var n in held) SetStatus(n.Id, NotificationStatus.Delivered);

        var lang = held[^1].Lang;
        var title = lang == "ar"
            ? $"وصلك {held.Count} تنبيه وانت مشغول"
            : $"{held.Count} notification{(held.Count == 1 ? "" : "s")} while you were busy";
        var body = string.Join("\n", held.Take(6).Select(n => "• " + n.Title)) + (held.Count > 6 ? "\n…" : "");
        var digest = new Notification { Title = title, Body = body, Priority = NotificationPriority.Normal, Source = "digest", Lang = lang };
        _events.Publish(EventTypes.NotificationDigest, new { digest, items = held });
        await DeliverAsync(digest, _settings.Current.Notifications.ToastsEnabled ? DeliveryMode.Visual : DeliveryMode.None, ct).ConfigureAwait(false);
        return held.Count;
    }

    private void OnPresenceChanged(PresenceSnapshot prev, PresenceSnapshot next)
    {
        var wasBusy = prev.State is UserState.InMeeting or UserState.Fullscreen or UserState.Away;
        var nowFree = next.State is UserState.Active;
        if (wasBusy && nowFree)
            _ = FlushHeldAsync().ContinueWith(t => _logger.LogWarning(t.Exception, "Digest delivery failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task DeliverAsync(Notification n, DeliveryMode mode, CancellationToken ct)
    {
        foreach (var sink in _sinks)
        {
            try { await sink.DeliverAsync(n, mode, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Notification sink {Sink} failed", sink.Name); }
        }
    }

    private void Save(Notification n)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notifications (id, ts, title, body, priority, source, group_key, status, delivered_at)
            VALUES ($id, $ts, $title, $body, $prio, $source, $group, $status, $delivered)
            ON CONFLICT(id) DO UPDATE SET status = $status;
            """;
        cmd.Parameters.AddWithValue("$id", n.Id);
        cmd.Parameters.AddWithValue("$ts", JarvisDatabase.Format(n.Timestamp));
        cmd.Parameters.AddWithValue("$title", n.Title);
        cmd.Parameters.AddWithValue("$body", (object?)n.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prio", n.Priority.ToString());
        cmd.Parameters.AddWithValue("$source", n.Source);
        cmd.Parameters.AddWithValue("$group", (object?)n.GroupKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", n.Status);
        cmd.Parameters.AddWithValue("$delivered", n.Status == NotificationStatus.Delivered ? JarvisDatabase.Now() : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SetStatus(string id, string status)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notifications SET status = $s WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.ExecuteNonQuery();
    }
}

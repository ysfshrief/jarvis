using Jarvis.Core.Activity;
using Jarvis.Core.Notifications;

namespace Jarvis.Core.Scheduling;

/// <summary>Turns due reminders into notifications. Called by the runtime's scheduler loop.</summary>
public sealed class ReminderDispatcher(ReminderStore reminders, NotificationCenter notifications, ActivityLog activity, Settings.ISettingsStore settings)
{
    public async Task<int> FireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var fired = 0;
        foreach (var r in reminders.Due(now))
        {
            // Mark first so a crash during delivery can't fire the same reminder twice.
            if (!reminders.MarkFired(r.Id)) continue;
            var late = now - r.DueAt > TimeSpan.FromMinutes(2);
            var ar = r.Lang == "ar";
            var title = ar ? $"تذكير: {r.Text}" : $"Reminder: {r.Text}";
            var body = late
                ? (ar ? $"كان المفروض {r.DueAt.LocalDateTime:h:mm} (الجهاز كان مقفول)" : $"Was due at {r.DueAt.LocalDateTime:h:mm tt} (JARVIS wasn't running)")
                : null;
            var g = settings.Current.General;
            var speech = ar
                ? (string.IsNullOrWhiteSpace(g.HonorificAr) ? $"بفكرك: {r.Text}" : $"{g.HonorificAr}، بفكرك: {r.Text}")
                : (string.IsNullOrWhiteSpace(g.Honorific) ? $"A reminder: {r.Text}" : $"{g.Honorific}, a reminder: {r.Text}");
            await notifications.PostAsync(new Notification
            {
                Title = title, Body = body, Speech = speech, Priority = NotificationPriority.High,
                Source = "reminder", GroupKey = $"reminder:{r.Id}", Lang = r.Lang,
            }, ct).ConfigureAwait(false);
            activity.Record(ActivityKinds.Notification, title, status: "fired");
            fired++;
        }
        return fired;
    }
}

using Jarvis.Core.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Notifications;
using Jarvis.Core.Presence;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Settings;

namespace Jarvis.Runtime;

/// <summary>Fires due reminders. Wakes at least every 15 seconds, or sooner when one is close.</summary>
public sealed class SchedulerService(ReminderDispatcher dispatcher, ReminderStore reminders, ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.FireDueAsync(DateTimeOffset.Now, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder dispatch failed");
            }

            var next = reminders.NextDue();
            var delay = TimeSpan.FromSeconds(15);
            if (next is { } n && n - DateTimeOffset.Now < delay)
                delay = n - DateTimeOffset.Now < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : n - DateTimeOffset.Now;
            await Task.Delay(delay, stoppingToken);
        }
    }
}

/// <summary>Samples presence every two seconds (cheap Win32 calls; no camera, no recording).</summary>
public sealed class PresenceService(PresenceTracker tracker, ILogger<PresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!tracker.IsSupported) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { tracker.Refresh(); }
            catch (Exception ex) { logger.LogDebug(ex, "Presence sample failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

/// <summary>Tracks internet connectivity; when it returns, tells the user about queued work.</summary>
public sealed class ConnectivityService(
    ConnectivityMonitor monitor, OfflineQueue queue, NotificationCenter notifications, ISettingsStore settings,
    ILogger<ConnectivityService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        monitor.Changed += online =>
        {
            if (!online) return;
            var pending = queue.List();
            if (pending.Count == 0) return;
            _ = notifications.PostAsync(new Notification
            {
                Title = $"Back online. {pending.Count} queued action{(pending.Count == 1 ? " is" : "s are")} waiting for your OK.",
                Body = string.Join("\n", pending.Take(5).Select(p => "• " + p.Summary)),
                Priority = NotificationPriority.Normal,
                Source = "connectivity",
                GroupKey = "queue-ready",
            });
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await monitor.CheckAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Connectivity check failed"); }
            var interval = monitor.IsOnline ? settings.Current.Runtime.ConnectivityProbeSeconds : 10;
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }
}

/// <summary>Daily housekeeping: drop conversation history older than the retention setting.</summary>
public sealed class RetentionService(ConversationStore conversations, ISettingsStore settings, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var days = settings.Current.Memory.ConversationRetentionDays;
            if (days > 0)
            {
                var removed = conversations.DeleteOlderThan(DateTimeOffset.Now.AddDays(-days));
                if (removed > 0) logger.LogInformation("Removed {Count} conversations older than {Days} days", removed, days);
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}

/// <summary>Probes AI providers at startup so the first request doesn't pay the discovery cost.</summary>
public sealed class ProviderWarmupService(ProviderRegistry providers, ILogger<ProviderWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        foreach (var status in await providers.CheckAllAsync(stoppingToken))
            logger.LogInformation("AI provider {Provider}: {Available} — {Message}", status.ProviderId, status.Available ? "available" : "unavailable", status.Message);
    }
}

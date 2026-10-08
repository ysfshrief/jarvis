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

/// <summary>
/// Keeps semantic memory search up to date: embeds new/edited memories as they arrive and backfills
/// older ones whenever an embedding model becomes available. Does nothing without a model.
/// </summary>
public sealed class KnowledgeIndexService(Jarvis.Core.Memory.KnowledgeService knowledge, ILogger<KnowledgeIndexService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        var nextBackfill = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now >= nextBackfill)
                {
                    var n = await knowledge.BackfillAsync(stoppingToken);
                    if (n > 0) logger.LogInformation("Indexed {Count} memories for semantic search", n);
                    nextBackfill = DateTimeOffset.Now.AddMinutes(10);
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromMinutes(10));
                try { await knowledge.Semantic.WaitForWorkAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }
                while (await knowledge.Semantic.DrainAsync(stoppingToken) > 0) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Semantic indexing skipped");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}

/// <summary>Runs the opt-in pattern learner a few minutes after start and then every six hours.</summary>
public sealed class PatternLearnerService(Jarvis.Core.Memory.PatternLearner learner, ISettingsStore settings, ILogger<PatternLearnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Current.Memory.LearnPatterns)
            {
                try
                {
                    var r = learner.Run(DateTimeOffset.Now);
                    if (r.Proposed + r.Updated > 0) logger.LogInformation("Pattern learner proposed {New} and updated {Updated} patterns", r.Proposed, r.Updated);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Pattern learner failed"); }
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}

/// <summary>Checks tracked workflows once a minute for follow-ups and deadlines.</summary>
public sealed class WorkflowMonitorService(Jarvis.Core.Workflows.WorkflowService workflows, ILogger<WorkflowMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await workflows.CheckDueAsync(DateTimeOffset.Now, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Workflow check failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

/// <summary>
/// Runs the file knowledge index when the user enabled it: a gentle scan shortly after start and every
/// six hours, plus live updates from file-system notifications. Stops when it's turned off.
/// </summary>
public sealed class FileIndexService(Jarvis.Core.Files.FileIndexer indexer, ISettingsStore settings, ILogger<FileIndexService> logger) : BackgroundService
{
    private volatile bool _rescan = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = (Enabled: false, Roots: "");
        settings.Changed += s =>
        {
            var now = (s.Files.IndexEnabled, string.Join("|", s.Files.IndexRoots));
            if (now != last) _rescan = true;
        };
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        var nextScan = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = settings.Current.Files;
                var current = (s.IndexEnabled, string.Join("|", s.IndexRoots));
                if (_rescan || current != last)
                {
                    _rescan = false;
                    last = current;
                    indexer.Watch();
                    nextScan = s.IndexEnabled ? DateTimeOffset.Now : DateTimeOffset.MaxValue;
                }
                if (s.IndexEnabled && DateTimeOffset.Now >= nextScan)
                {
                    await indexer.ScanAsync(stoppingToken);
                    nextScan = DateTimeOffset.Now.AddHours(6);
                }
                if (s.IndexEnabled) await indexer.ProcessChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "File indexing failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

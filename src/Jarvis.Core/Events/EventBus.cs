using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Events;

/// <summary>A single thing that happened inside JARVIS. Fanned out to the UI over WebSocket.</summary>
public sealed record JarvisEvent(string Type, object? Data, DateTimeOffset Timestamp);

public interface IEventBus
{
    void Publish(string type, object? data = null);
    IDisposable Subscribe(Action<JarvisEvent> handler);
}

/// <summary>
/// In-process publish/subscribe. Deliberately simple: handlers run synchronously on the
/// publisher's thread and must be fast (the WebSocket hub just enqueues into a channel).
/// A failing handler never breaks the publisher.
/// </summary>
public sealed class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly object _gate = new();
    private Action<JarvisEvent>[] _handlers = [];

    public void Publish(string type, object? data = null)
    {
        var evt = new JarvisEvent(type, data, DateTimeOffset.Now);
        foreach (var handler in Volatile.Read(ref _handlers))
        {
            try { handler(evt); }
            catch (Exception ex) { logger.LogWarning(ex, "Event handler failed for {Type}", type); }
        }
    }

    public IDisposable Subscribe(Action<JarvisEvent> handler)
    {
        lock (_gate) _handlers = [.. _handlers, handler];
        return new Unsubscriber(() =>
        {
            lock (_gate) _handlers = _handlers.Where(h => h != handler).ToArray();
        });
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}

/// <summary>Well-known event type names (the contract with the UI and future companion apps).</summary>
public static class EventTypes
{
    public const string TurnStarted = "agent.turn.started";
    public const string TurnCompleted = "agent.turn.completed";
    /// <summary>Visible progress of a turn: understanding → analyzing → selecting_tool → executing → completed/failed.</summary>
    public const string TurnPhase = "agent.turn.phase";
    /// <summary>Reply text as the model writes it (streaming).</summary>
    public const string TurnDelta = "agent.turn.delta";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolved = "approval.resolved";
    public const string Activity = "activity";
    public const string Notification = "notification";
    public const string NotificationDigest = "notification.digest";
    public const string PresenceChanged = "presence.changed";
    public const string ConnectivityChanged = "connectivity.changed";
    public const string VoiceState = "voice.state";
    public const string VoiceTranscript = "voice.transcript";
    public const string VoiceModelProgress = "voice.model.progress";
    public const string RuntimeState = "runtime.state";
    public const string ModelPull = "ai.model.pull";
    public const string AiStatusChanged = "ai.status";
    public const string SettingsChanged = "settings.changed";
    public const string MemoryChanged = "memory.changed";
    public const string TasksChanged = "tasks.changed";
    public const string WorkflowsChanged = "workflows.changed";
    /// <summary>File index progress and changes.</summary>
    public const string FilesIndexChanged = "files.index";
    /// <summary>JARVIS's browser opened, navigated or closed.</summary>
    public const string BrowserChanged = "browser.changed";
    /// <summary>Mail accounts, messages or drafts changed.</summary>
    public const string InboxChanged = "inbox.changed";
    /// <summary>Calendars or events changed.</summary>
    public const string CalendarChanged = "calendar.changed";
    /// <summary>A meeting recording started, progressed, stopped or its notes are ready.</summary>
    public const string MeetingChanged = "meeting.changed";
    /// <summary>The camera was just used (the UI flashes an indicator).</summary>
    public const string CameraUsed = "camera.used";
    public const string RemindersChanged = "reminders.changed";
    public const string QueueChanged = "queue.changed";
    public const string UiShow = "ui.show";
    /// <summary>Ask the desktop shell to open the command console.</summary>
    public const string UiConsole = "ui.console";
}

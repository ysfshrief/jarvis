using System.Text.Json.Serialization;
using Jarvis.Core.Events;

namespace Jarvis.Core.Presence;

[JsonConverter(typeof(JsonStringEnumConverter<UserState>))]
public enum UserState
{
    Unknown,
    /// <summary>Using the computer.</summary>
    Active,
    /// <summary>No input for a few minutes.</summary>
    Idle,
    /// <summary>No input for a long time or the session is locked.</summary>
    Away,
    /// <summary>A meeting/call appears to be in progress.</summary>
    InMeeting,
    /// <summary>A fullscreen app (presentation, game, video) is in front.</summary>
    Fullscreen,
}

/// <summary>What JARVIS currently knows about the user's computer context.</summary>
public sealed record PresenceSnapshot
{
    public UserState State { get; init; } = UserState.Unknown;
    public string? ActiveProcess { get; init; }
    public string? ActiveWindowTitle { get; init; }
    public int IdleSeconds { get; init; }
    public bool IsFullscreen { get; init; }
    public bool MicrophoneInUse { get; init; }
    public bool InMeeting { get; init; }
    public string? MeetingApp { get; init; }
    public bool SessionLocked { get; init; }
    /// <summary>A coarse activity label for the adaptive UI: coding, communication, meeting, browsing, other.</summary>
    public string Activity { get; init; } = "other";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public static PresenceSnapshot Unknown => new();
}

/// <summary>Platform-specific source of presence signals.</summary>
public interface IPresenceProvider
{
    bool IsSupported { get; }
    PresenceSnapshot Sample();
}

public sealed class NullPresenceProvider : IPresenceProvider
{
    public bool IsSupported => false;
    public PresenceSnapshot Sample() => PresenceSnapshot.Unknown;
}

/// <summary>Holds the latest presence snapshot and announces meaningful changes.</summary>
public sealed class PresenceTracker(IPresenceProvider provider, IEventBus events)
{
    private PresenceSnapshot _current = PresenceSnapshot.Unknown;

    public PresenceSnapshot Current => Volatile.Read(ref _current);
    public bool IsSupported => provider.IsSupported;

    public event Action<PresenceSnapshot, PresenceSnapshot>? StateChanged;

    /// <summary>Take a fresh sample. Returns true when something user-visible changed.</summary>
    public bool Refresh()
    {
        var next = provider.Sample();
        var prev = Interlocked.Exchange(ref _current, next);
        var changed = prev.State != next.State || prev.ActiveProcess != next.ActiveProcess ||
                      prev.ActiveWindowTitle != next.ActiveWindowTitle || prev.InMeeting != next.InMeeting ||
                      prev.MicrophoneInUse != next.MicrophoneInUse;
        if (changed) events.Publish(EventTypes.PresenceChanged, next);
        if (prev.State != next.State) StateChanged?.Invoke(prev, next);
        return changed;
    }

    /// <summary>Classifies window/process into a coarse activity used by the adaptive dashboard.</summary>
    public static string ClassifyActivity(string? process, string? title, bool inMeeting)
    {
        if (inMeeting) return "meeting";
        var p = (process ?? "").ToLowerInvariant();
        var t = (title ?? "").ToLowerInvariant();
        string[] coding = ["code", "devenv", "rider", "idea64", "pycharm64", "webstorm64", "windowsterminal", "powershell", "pwsh", "cmd", "wt", "sublime_text", "notepad++", "cursor", "android studio", "studio64"];
        string[] comms = ["outlook", "olk", "thunderbird", "slack", "discord", "telegram", "whatsapp", "teams", "ms-teams", "signal"];
        string[] browsers = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];
        if (coding.Any(c => p == c || p.StartsWith(c))) return "coding";
        if (comms.Any(c => p.Contains(c))) return "communication";
        if (browsers.Contains(p))
        {
            if (t.Contains("gmail") || t.Contains("outlook") || t.Contains("whatsapp") || t.Contains("slack") || t.Contains("messenger") || t.Contains("linkedin"))
                return "communication";
            if (t.Contains("github") || t.Contains("stack overflow") || t.Contains("localhost"))
                return "coding";
            return "browsing";
        }
        return "other";
    }
}

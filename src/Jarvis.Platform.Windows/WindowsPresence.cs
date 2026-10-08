using Jarvis.Core.Presence;
using Microsoft.Win32;

namespace Jarvis.Platform.Windows;

/// <summary>
/// Reads presence signals from Windows: foreground app, idle time, fullscreen/presentation
/// state (the same signal Windows uses to suppress its own notifications), microphone use
/// (privacy consent store), and whether the session is locked. No camera, no recording.
/// </summary>
public sealed class WindowsPresenceProvider : IPresenceProvider
{
    private static readonly string[] MeetingProcesses =
        ["ms-teams", "teams", "zoom", "webex", "webexmta", "slack", "discord", "skype", "lync", "gotomeeting", "ringcentral"];

    private static readonly string[] MeetingTitles =
        ["meet.google.com", "google meet", "zoom meeting", "| microsoft teams", "meeting", "webex", "اجتماع"];

    public bool IsSupported => true;

    public PresenceSnapshot Sample()
    {
        var fg = WindowManager.Foreground();
        var idle = IdleSeconds();
        var locked = IsSessionLocked() || fg?.ProcessName is "LockApp";
        var micApps = MicrophoneUsers();
        var micInUse = micApps.Count > 0;

        Native.SHQueryUserNotificationState(out var quns);
        var fullscreen = quns is Native.QUNS_RUNNING_D3D_FULL_SCREEN or Native.QUNS_PRESENTATION_MODE or Native.QUNS_BUSY
                         || (fg is not null && WindowManager.IsFullscreen(fg.Handle));

        var meetingApp = micApps.FirstOrDefault(a => MeetingProcesses.Any(m => a.Contains(m, StringComparison.OrdinalIgnoreCase)));
        var titleSuggestsMeeting = fg is not null && MeetingTitles.Any(t => fg.Title.Contains(t, StringComparison.OrdinalIgnoreCase));
        var browserMeeting = micInUse && titleSuggestsMeeting;
        var inMeeting = meetingApp is not null || browserMeeting ||
                        (micInUse && fg is not null && MeetingProcesses.Contains(fg.ProcessName.ToLowerInvariant()));

        var state = locked ? UserState.Away
            : inMeeting ? UserState.InMeeting
            : fullscreen ? UserState.Fullscreen
            : idle > 900 ? UserState.Away
            : idle > 180 ? UserState.Idle
            : UserState.Active;

        return new PresenceSnapshot
        {
            State = state,
            ActiveProcess = fg?.ProcessName,
            ActiveWindowTitle = fg?.Title,
            IdleSeconds = idle,
            IsFullscreen = fullscreen,
            MicrophoneInUse = micInUse,
            InMeeting = inMeeting,
            MeetingApp = meetingApp ?? (browserMeeting ? fg?.ProcessName : null),
            SessionLocked = locked,
            Activity = PresenceTracker.ClassifyActivity(fg?.ProcessName, fg?.Title, inMeeting),
        };
    }

    private static int IdleSeconds()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return 0;
        var idleMs = (uint)Environment.TickCount - info.dwTime;
        return (int)(idleMs / 1000);
    }

    private static bool IsSessionLocked()
    {
        // When the secure desktop is active, the input desktop can't be opened by a normal process.
        var h = Native.OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (h == 0) return true;
        Native.CloseDesktop(h);
        return false;
    }

    /// <summary>
    /// Apps currently using the microphone, from Windows' privacy consent store
    /// (an entry whose LastUsedTimeStop is 0 is in use right now). Excludes JARVIS itself.
    /// </summary>
    internal static List<string> MicrophoneUsers()
    {
        var users = new List<string>();
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var mic = Registry.CurrentUser.OpenSubKey(root);
            if (mic is null) return users;
            Collect(mic, users);
            using var nonPackaged = mic.OpenSubKey("NonPackaged");
            if (nonPackaged is not null) Collect(nonPackaged, users);
        }
        catch { /* access denied or missing on this Windows edition */ }
        return users.Where(u => !u.Contains("jarvis", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void Collect(RegistryKey parent, List<string> users)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var k = parent.OpenSubKey(name);
            if (k?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start > 0)
                users.Add(Path.GetFileNameWithoutExtension(name.Replace('#', '\\')));
        }
    }
}

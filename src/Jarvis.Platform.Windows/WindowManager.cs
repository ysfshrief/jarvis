using System.Diagnostics;

namespace Jarvis.Platform.Windows;

public sealed record WindowInfo(nint Handle, string Title, int ProcessId, string ProcessName, bool Minimized, bool Maximized);

/// <summary>Enumerates and controls top-level application windows.</summary>
public static class WindowManager
{
    /// <summary>Visible, titled, top-level app windows (what you'd see in Alt+Tab).</summary>
    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;
            if (Native.GetWindow(hWnd, Native.GW_OWNER) != 0) return true;
            if ((Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE) & (nint)Native.WS_EX_TOOLWINDOW) != 0) return true;
            var title = Native.WindowText(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            Native.GetWindowThreadProcessId(hWnd, out var pid);
            if (!names.TryGetValue(pid, out var pname))
            {
                try { using var p = Process.GetProcessById((int)pid); pname = p.ProcessName; }
                catch { pname = "?"; }
                names[pid] = pname;
            }
            if (pname is "TextInputHost" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost") return true;
            result.Add(new WindowInfo(hWnd, title, (int)pid, pname, Native.IsIconic(hWnd), Native.IsZoomed(hWnd)));
            return true;
        }, 0);
        return result;
    }

    public static WindowInfo? Foreground()
    {
        var h = Native.GetForegroundWindow();
        if (h == 0) return null;
        Native.GetWindowThreadProcessId(h, out var pid);
        string pname;
        try { using var p = Process.GetProcessById((int)pid); pname = p.ProcessName; }
        catch { pname = "?"; }
        return new WindowInfo(h, Native.WindowText(h), (int)pid, pname, Native.IsIconic(h), Native.IsZoomed(h));
    }

    /// <summary>Best match by process name or title (normalized, partial).</summary>
    public static List<WindowInfo> Find(string query)
    {
        var q = Norm(query);
        var all = List();
        var exactProc = all.Where(w => Norm(w.ProcessName) == q).ToList();
        if (exactProc.Count > 0) return exactProc;
        return all.Where(w => Norm(w.Title).Contains(q) || Norm(w.ProcessName).Contains(q)).ToList();
    }

    public static bool Focus(nint hWnd)
    {
        if (Native.IsIconic(hWnd)) Native.ShowWindow(hWnd, Native.SW_RESTORE);
        // Windows restricts focus stealing; a synthetic Alt press is the documented-ish workaround.
        Native.keybd_event(0x12, 0, 0, 0);
        Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, 0);
        Native.BringWindowToTop(hWnd);
        var ok = Native.SetForegroundWindow(hWnd);
        Thread.Sleep(100);
        return ok || Native.GetForegroundWindow() == hWnd;
    }

    public static void Show(nint hWnd, int command) => Native.ShowWindow(hWnd, command);

    public static void Close(nint hWnd) => Native.PostMessage(hWnd, Native.WM_CLOSE, 0, 0);

    public static bool IsFullscreen(nint hWnd)
    {
        if (hWnd == 0 || !Native.GetWindowRect(hWnd, out var r)) return false;
        var mon = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        var title = Native.WindowText(hWnd);
        if (title is "Program Manager" or "") return false; // the desktop itself
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    internal static string Norm(string s) => Core.Language.TextNormalizer.Normalize(s).Replace(" ", "");
}

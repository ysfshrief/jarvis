using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Jarvis.Core;
using Jarvis.Core.Tools;
using Jarvis.Core.Tools.Builtin;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Jarvis.Platform.Windows;

public sealed class VolumeTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "volume",
        Category = "media",
        Description = "Change or read the system volume: up, down, set to a level (0-100), mute, unmute, or get.",
        Parameters =
        [
            new("action", "string", "What to do.", true, ["up", "down", "set", "mute", "unmute", "get"]),
            new("level", "integer", "Target level 0-100 for 'set', or step size for up/down (default 10)."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Volume {args.GetString("action")} {args.GetInt("level")}".Trim();

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (COMException) { return Task.FromResult(ToolResult.Fail(ctx.T("No audio output device is available.", "مفيش جهاز صوت متوصل."))); }
        using (device)
        {
            var vol = device.AudioEndpointVolume;
            var action = args.RequireString("action");
            var level = args.GetInt("level");
            switch (action)
            {
                case "up": vol.MasterVolumeLevelScalar = Math.Min(1f, vol.MasterVolumeLevelScalar + (level ?? 10) / 100f); vol.Mute = false; break;
                case "down": vol.MasterVolumeLevelScalar = Math.Max(0f, vol.MasterVolumeLevelScalar - (level ?? 10) / 100f); break;
                case "set":
                    if (level is null) throw new ToolArgumentException("'set' needs a level from 0 to 100.");
                    vol.MasterVolumeLevelScalar = Math.Clamp(level.Value, 0, 100) / 100f;
                    vol.Mute = level.Value == 0;
                    break;
                case "mute": vol.Mute = true; break;
                case "unmute": vol.Mute = false; break;
                case "get": break;
                default: throw new ToolArgumentException($"Unknown action '{action}'.");
            }
            var pct = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
            var muted = vol.Mute;
            var msg = muted
                ? ctx.T("Muted.", "الصوت مكتوم.")
                : ctx.T($"Volume is at {pct}%.", $"الصوت على {pct}%.");
            return Task.FromResult(ToolResult.Ok(msg, new { level = pct, muted, device = device.FriendlyName }));
        }
    }
}

public sealed class MediaControlTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "media_control",
        Category = "media",
        Description = "Control whatever media is playing (Spotify, YouTube, etc.): play/pause, next, previous, stop.",
        Parameters = [new("action", "string", "Media key to press.", true, ["play_pause", "next", "previous", "stop"])],
    };

    protected override string Describe(ToolArgs args) => $"Media {args.GetString("action")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var action = args.RequireString("action");
        byte vk = action switch
        {
            "play_pause" => 0xB3,
            "next" => 0xB0,
            "previous" => 0xB1,
            "stop" => 0xB2,
            _ => throw new ToolArgumentException($"Unknown action '{action}'."),
        };
        Native.keybd_event(vk, 0, Native.KEYEVENTF_EXTENDEDKEY, 0);
        Native.keybd_event(vk, 0, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, 0);
        var msg = action switch
        {
            "next" => ctx.T("Next track.", "اللي بعده."),
            "previous" => ctx.T("Previous track.", "اللي قبله."),
            "stop" => ctx.T("Stopped.", "وقفت."),
            _ => ctx.T("Done.", "تمام."),
        };
        return Task.FromResult(ToolResult.Ok(msg, new { action }));
    }
}

public sealed class ScreenshotTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "screenshot",
        Category = "screen",
        CapturesScreen = true,
        Description = "Capture the whole screen (all monitors) to a PNG in Pictures\\JARVIS and return its path.",
    };

    protected override string Describe(ToolArgs args) => "Take a screenshot";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var path = Capture();
        var info = new FileInfo(path);
        return Task.FromResult(ToolResult.Ok(ctx.T($"Screenshot saved to Pictures\\JARVIS{ctx.CommaSir}.", $"السكرين شوت اتحفظ في Pictures\\JARVIS{ctx.CommaSir}."),
            new { path, sizeBytes = info.Length }));
    }

    public static string Capture()
    {
        var x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen is available to capture.");
        Directory.CreateDirectory(JarvisPaths.ScreenshotsDir);
        var path = Path.Combine(JarvisPaths.ScreenshotsDir, $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}

public sealed class LockScreenTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "lock_screen",
        Category = "system",
        Description = "Lock the Windows session (like Win+L).",
    };

    protected override string Describe(ToolArgs args) => "Lock the computer";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx) =>
        Task.FromResult(Native.LockWorkStation()
            ? ToolResult.Ok(ctx.T("Locked.", "قفلت الشاشة."))
            : ToolResult.Fail(ctx.T("Windows didn't allow locking the session.", "الويندوز مسمحش بقفل الشاشة.")));
}

public sealed class SystemPowerTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "system_power",
        Category = "system",
        Risk = RiskLevel.Critical,
        Description = "Shut down, restart or sleep the computer. Shutdown/restart wait 30 seconds and can be cancelled with action 'cancel'. Always requires approval.",
        Parameters = [new("action", "string", "Power action.", true, ["shutdown", "restart", "sleep", "cancel"])],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var action = args.RequireString("action");
        return action == "cancel"
            ? new(RiskLevel.Safe, "Cancel a pending shutdown/restart")
            : new(RiskLevel.Critical, action switch { "shutdown" => "Shut down the computer", "restart" => "Restart the computer", _ => "Put the computer to sleep" },
                "Unsaved work in open apps may be lost.");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var action = args.RequireString("action");
        switch (action)
        {
            case "shutdown":
                Run("shutdown.exe", "/s /t 30 /c \"JARVIS: shutting down in 30 seconds\"");
                return Task.FromResult(ToolResult.Ok(ctx.T("Shutting down in 30 seconds. Say \"cancel shutdown\" to stop it.", "الجهاز هيقفل بعد 30 ثانية. قول \"الغي القفل\" لو عايز توقفه.")));
            case "restart":
                Run("shutdown.exe", "/r /t 30 /c \"JARVIS: restarting in 30 seconds\"");
                return Task.FromResult(ToolResult.Ok(ctx.T("Restarting in 30 seconds.", "الجهاز هيعمل ريستارت بعد 30 ثانية.")));
            case "sleep":
                _ = Task.Run(async () => { await Task.Delay(1500); Native.SetSuspendState(false, false, false); });
                return Task.FromResult(ToolResult.Ok(ctx.T("Going to sleep.", "الجهاز هينام.")));
            case "cancel":
                var code = Run("shutdown.exe", "/a");
                return Task.FromResult(code == 0
                    ? ToolResult.Ok(ctx.T("Cancelled the pending shutdown.", "لغيت القفل."))
                    : ToolResult.Fail(ctx.T("There was no pending shutdown to cancel.", "مكانش فيه قفل مستني.")));
            default:
                throw new ToolArgumentException($"Unknown action '{action}'.");
        }
    }

    private static int Run(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(5000);
        return p.HasExited ? p.ExitCode : -1;
    }
}

public sealed class ClipboardReadTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "clipboard_read",
        Category = "system",
        Risk = RiskLevel.Sensitive,
        Description = "Read the text currently on the clipboard (may contain private data; needs approval).",
    };

    protected override string Describe(ToolArgs args) => "Read the clipboard";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var text = Clipboard.GetText();
        return Task.FromResult(text is null
            ? ToolResult.Ok(ctx.T("The clipboard has no text.", "مفيش نص في الكليبورد."), new { text = (string?)null })
            : ToolResult.Ok(ctx.T($"The clipboard has {text.Length} characters of text.", $"الكليبورد فيه {text.Length} حرف."), new { text }));
    }
}

public sealed class ClipboardWriteTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "clipboard_write",
        Category = "system",
        Description = "Put text on the clipboard so the user can paste it.",
        Parameters = [new("text", "string", "Text to copy.", true)],
    };

    protected override string Describe(ToolArgs args) => "Copy text to the clipboard";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var text = args.GetString("text") ?? "";
        return Task.FromResult(Clipboard.SetText(text)
            ? ToolResult.Ok(ctx.T("Copied to the clipboard.", "اتنسخ."))
            : ToolResult.Fail(ctx.T("Another app is holding the clipboard; try again.", "فيه برنامج تاني ماسك الكليبورد، جرب تاني.")));
    }
}

public sealed class TypeTextTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "keyboard_type",
        Category = "input",
        Risk = RiskLevel.Sensitive,
        Description = "Type text into whatever window currently has focus, as if from the keyboard. Needs approval.",
        Parameters = [new("text", "string", "Text to type.", true)],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var fg = WindowManager.Foreground();
        var text = args.RequireString("text");
        var preview = text.Length > 60 ? text[..57] + "..." : text;
        return new(RiskLevel.Sensitive, $"Type \"{preview}\" into {fg?.ProcessName ?? "the active window"}", "Keystrokes go to whatever app is focused.");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var text = args.RequireString("text");
        var inputs = new List<Native.INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                inputs.Add(Key(0x0D, false));
                inputs.Add(Key(0x0D, true));
                continue;
            }
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } });
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } });
        }
        var sent = Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
        return Task.FromResult(sent == inputs.Count
            ? ToolResult.Ok(ctx.T("Typed.", "كتبت."), new { characters = text.Length })
            : ToolResult.Fail(ctx.T("Windows blocked the keystrokes (the focused app may be running as administrator).", "الويندوز منع الكتابة (ممكن البرنامج شغال كأدمن).")));
    }

    internal static Native.INPUT Key(ushort vk, bool up) =>
        new() { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } } };
}

public sealed class SendKeysTool : ToolBase
{
    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["insert"] = 0x2D, ["printscreen"] = 0x2C,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "keyboard_shortcut",
        Category = "input",
        Risk = RiskLevel.Sensitive,
        Description = "Press a key or shortcut in the focused window, e.g. 'ctrl+s', 'alt+tab', 'win+d', 'enter', 'f5'. Needs approval.",
        Parameters = [new("keys", "string", "Shortcut like 'ctrl+shift+t'.", true)],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var fg = WindowManager.Foreground();
        var keys = args.RequireString("keys");
        return new(RiskLevel.Sensitive, $"Press {keys} in {fg?.ProcessName ?? "the active window"}", "Keystrokes go to whatever app is focused.");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var parts = args.RequireString("keys").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var codes = new List<ushort>();
        foreach (var p in parts)
        {
            if (Keys.TryGetValue(p, out var vk)) codes.Add(vk);
            else if (p.Length == 1) codes.Add((ushort)(Native.VkKeyScanW(char.ToLowerInvariant(p[0])) & 0xFF));
            else throw new ToolArgumentException($"Unknown key '{p}'.");
        }
        var inputs = codes.Select(c => TypeTextTool.Key(c, false)).Concat(codes.AsEnumerable().Reverse().Select(c => TypeTextTool.Key(c, true))).ToArray();
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        return Task.FromResult(sent == inputs.Length
            ? ToolResult.Ok(ctx.T("Done.", "تمام."), new { keys = args.GetString("keys") })
            : ToolResult.Fail(ctx.T("Windows blocked the key press.", "الويندوز منع الضغطة.")));
    }
}

public sealed class WindowsSystemInfoTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "system_info",
        Category = "system",
        Description = "Get the computer's status: Windows version, uptime, CPU load, memory, battery, disk space.",
    };

    protected override string Describe(ToolArgs args) => "Read system status";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var baseInfo = SystemInfoTool.Collect();
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref mem);
        var cpu = await CpuLoadAsync(ctx.CancellationToken).ConfigureAwait(false);
        Native.GetSystemPowerStatus(out var power);
        var hasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;
        var battery = hasBattery && power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : (int?)null;
        var charging = power.ACLineStatus == 1;
        var os = WindowsVersion() ?? baseInfo.Os;
        var memPct = (int)mem.dwMemoryLoad;
        var totalGb = mem.ullTotalPhys / 1073741824.0;

        var batteryEn = battery is { } b ? $" Battery {b}%{(charging ? ", charging" : "")}." : "";
        var batteryAr = battery is { } b2 ? $" البطارية {b2}%{(charging ? " وبتشحن" : "")}." : "";
        var msg = ctx.T(
            $"{os}. CPU {cpu}%, memory {memPct}% of {totalGb:0} GB.{batteryEn} Free on {baseInfo.SystemDrive}: {baseInfo.SystemDriveFreeGb:0} GB. Up {SystemInfoTool.Fmt(baseInfo.Uptime)}.",
            $"{os}. البروسيسور {cpu}%، الرامات {memPct}% من {totalGb:0} جيجا.{batteryAr} المساحة الفاضية على {baseInfo.SystemDrive}: {baseInfo.SystemDriveFreeGb:0} جيجا. شغال من {SystemInfoTool.Fmt(baseInfo.Uptime)}.");
        return ToolResult.Ok(msg, new
        {
            os, baseInfo.Machine, baseInfo.User, baseInfo.Cpus, cpuPercent = cpu, memoryPercent = memPct, totalMemoryGb = Math.Round(totalGb, 1),
            batteryPercent = battery, charging, uptime = SystemInfoTool.Fmt(baseInfo.Uptime), baseInfo.Drives,
        });
    }

    private static async Task<int> CpuLoadAsync(CancellationToken ct)
    {
        Native.GetSystemTimes(out var idle1, out var k1, out var u1);
        await Task.Delay(400, ct).ConfigureAwait(false);
        Native.GetSystemTimes(out var idle2, out var k2, out var u2);
        var total = (k2 - k1) + (u2 - u1);
        return total <= 0 ? 0 : (int)Math.Round(100.0 * (total - (idle2 - idle1)) / total);
    }

    private static string? WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            var build = key?.GetValue("CurrentBuildNumber") as string;
            if (name is null) return null;
            // Windows 11 still reports "Windows 10" in ProductName; the build number tells the truth.
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {display}".Trim();
        }
        catch { return null; }
    }
}

/// <summary>Moves deleted files to the Windows Recycle Bin so deletions can be undone.</summary>
public sealed class RecycleBinTrash : IFileTrash
{
    public string Name => "Recycle Bin";

    public void Trash(string fullPath)
    {
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = fullPath + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
        };
        var rc = Native.SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Windows couldn't move the item to the Recycle Bin (code {rc}).");
    }
}

/// <summary>Plain Win32 clipboard access (no UI thread required).</summary>
internal static class Clipboard
{
    public static string? GetText()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(0))
            {
                try
                {
                    var h = Native.GetClipboardData(Native.CF_UNICODETEXT);
                    if (h == 0) return null;
                    var p = Native.GlobalLock(h);
                    try { return Marshal.PtrToStringUni(p); }
                    finally { Native.GlobalUnlock(h); }
                }
                finally { Native.CloseClipboard(); }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(0))
            {
                try
                {
                    Native.EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)bytes);
                    var p = Native.GlobalLock(h);
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0);
                    Native.GlobalUnlock(h);
                    return Native.SetClipboardData(Native.CF_UNICODETEXT, h) != 0;
                }
                finally { Native.CloseClipboard(); }
            }
            Thread.Sleep(50);
        }
        return false;
    }
}

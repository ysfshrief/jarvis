using System.Diagnostics;
using System.Text.Json;
using Jarvis.Core;
using Jarvis.Core.Language;
using Microsoft.Extensions.Logging;

namespace Jarvis.Platform.Windows;

public sealed record AppEntry(string Name, string AppId);

public enum LaunchKind { App, Executable, Uri, Folder, Website }

public sealed record LaunchTarget(LaunchKind Kind, string Target, string DisplayName, string? ProcessHint);

/// <summary>
/// Knows how to start things on this PC: Start-menu apps (classic and Store, via Get-StartApps),
/// common executables, settings pages, folders and websites, with English and Egyptian Arabic names.
/// </summary>
public sealed partial class AppCatalog(JarvisPaths paths, ILogger<AppCatalog> logger)
{
    private readonly string _cachePath = Path.Combine(paths.DataDir, "apps-cache.json");
    private List<AppEntry> _apps = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Spoken/written names (normalized) → canonical target. Checked before the Start menu.</summary>
    private static readonly Dictionary<string, LaunchTarget> Aliases = BuildAliases();

    public IReadOnlyList<AppEntry> Apps => _apps;

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && DateTimeOffset.Now - _loadedAt < TimeSpan.FromHours(6) && _apps.Count > 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _apps.Count == 0 && File.Exists(_cachePath))
            {
                try { _apps = JsonSerializer.Deserialize<List<AppEntry>>(await File.ReadAllTextAsync(_cachePath, ct)) ?? []; }
                catch { _apps = []; }
            }
            var fresh = await QueryStartAppsAsync(ct).ConfigureAwait(false);
            if (fresh.Count > 0)
            {
                _apps = fresh;
                _loadedAt = DateTimeOffset.Now;
                await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(fresh), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't refresh the installed apps list");
        }
        finally { _gate.Release(); }
    }

    public LaunchTarget? Resolve(string name)
    {
        var raw = name.Trim().Trim('"', '\'');
        var key = Key(raw);
        if (key.Length == 0) return null;

        if (Aliases.TryGetValue(key, out var alias))
        {
            // Prefer the real Start-menu entry for the alias's display name when it exists (e.g. Store apps).
            if (alias.Kind == LaunchKind.App)
            {
                var app = FindApp(Key(alias.Target));
                if (app is not null) return new LaunchTarget(LaunchKind.App, app.AppId, app.Name, alias.ProcessHint);
                return null;
            }
            return alias;
        }

        if (Core.Tools.Builtin.FilePolicy.KnownFolder(raw) is { } folder)
            return new LaunchTarget(LaunchKind.Folder, folder, Path.GetFileName(folder), "explorer");

        if (LooksLikeUrl(raw))
            return new LaunchTarget(LaunchKind.Website, raw.Contains("://") ? raw : "https://" + raw, raw, null);

        var found = FindApp(key);
        if (found is not null) return new LaunchTarget(LaunchKind.App, found.AppId, found.Name, null);

        if (Directory.Exists(raw)) return new LaunchTarget(LaunchKind.Folder, raw, Path.GetFileName(raw), "explorer");
        if (File.Exists(raw)) return new LaunchTarget(LaunchKind.Executable, raw, Path.GetFileNameWithoutExtension(raw), Path.GetFileNameWithoutExtension(raw));
        return null;
    }

    /// <summary>Close candidates for an unknown name, for a helpful "did you mean" reply.</summary>
    public IReadOnlyList<string> Suggestions(string name, int max = 3)
    {
        var key = Key(name);
        return _apps.Select(a => (a.Name, Score: Distance(Key(a.Name), key)))
            .Where(x => x.Score <= Math.Max(3, key.Length / 2))
            .OrderBy(x => x.Score).Take(max).Select(x => x.Name).ToList();
    }

    private AppEntry? FindApp(string key)
    {
        var apps = _apps;
        if (apps.Count == 0) return null;
        var exact = apps.FirstOrDefault(a => Key(a.Name) == key);
        if (exact is not null) return exact;
        var starts = apps.Where(a => Key(a.Name).StartsWith(key)).OrderBy(a => a.Name.Length).FirstOrDefault();
        if (starts is not null) return starts;
        if (key.Length >= 3)
        {
            var contains = apps.Where(a => Key(a.Name).Contains(key)).OrderBy(a => a.Name.Length).FirstOrDefault();
            if (contains is not null) return contains;
        }
        // Tolerate small typos and speech-recognition slips ("calculater").
        var best = apps.Select(a => (a, d: Distance(Key(a.Name), key))).OrderBy(x => x.d).FirstOrDefault();
        return best.a is not null && best.d <= Math.Max(1, key.Length / 5) ? best.a : null;
    }

    public static ProcessStartInfo StartInfo(LaunchTarget t) => t.Kind switch
    {
        LaunchKind.App when t.Target.Contains('\\') && t.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) =>
            new ProcessStartInfo(t.Target) { UseShellExecute = true },
        LaunchKind.App => new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{t.Target}") { UseShellExecute = false },
        LaunchKind.Folder => new ProcessStartInfo("explorer.exe", $"\"{t.Target}\"") { UseShellExecute = false },
        _ => new ProcessStartInfo(t.Target) { UseShellExecute = true },
    };

    private static async Task<List<AppEntry>> QueryStartAppsAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var list = new List<AppEntry>();
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            var n = e.TryGetProperty("Name", out var nn) ? nn.GetString() : null;
            var id = e.TryGetProperty("AppID", out var ii) ? ii.GetString() : null;
            if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(id)) list.Add(new AppEntry(n, id));
        }
        return list;
    }

    /// <summary>Comparison key: normalized, Arabic article removed, letters/digits only.</summary>
    internal static string Key(string s)
    {
        var n = ArabicArticle().Replace(TextNormalizer.Normalize(s), "");
        return new string(n.Where(c => char.IsLetterOrDigit(c) || c is '+' or '#').ToArray());
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|\s)ال")]
    private static partial System.Text.RegularExpressions.Regex ArabicArticle();

    private static bool LooksLikeUrl(string s) =>
        s.StartsWith("http://") || s.StartsWith("https://") ||
        (!s.Contains(' ') && s.Contains('.') && s.LastIndexOf('.') < s.Length - 2 && !s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    internal static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static Dictionary<string, LaunchTarget> BuildAliases()
    {
        var d = new Dictionary<string, LaunchTarget>();
        void Exe(string exe, string display, string? proc, params string[] names)
        {
            foreach (var n in names.Append(display)) d[Key(n)] = new LaunchTarget(LaunchKind.Executable, exe, display, proc);
        }
        void App(string startMenuName, string? proc, params string[] names)
        {
            foreach (var n in names.Append(startMenuName)) d[Key(n)] = new LaunchTarget(LaunchKind.App, startMenuName, startMenuName, proc);
        }
        void Uri(string uri, string display, params string[] names)
        {
            foreach (var n in names.Append(display)) d[Key(n)] = new LaunchTarget(LaunchKind.Uri, uri, display, null);
        }
        void Site(string url, string display, params string[] names)
        {
            foreach (var n in names.Append(display)) d[Key(n)] = new LaunchTarget(LaunchKind.Website, url, display, null);
        }

        Exe("calc.exe", "Calculator", "CalculatorApp", "calc", "calculator app", "الاله الحاسبه", "الحاسبه", "اله حاسبه", "كالكيوليتر", "الكالكيوليتر");
        Exe("notepad.exe", "Notepad", "notepad", "note pad", "النوت باد", "نوت باد", "المفكره");
        Exe("mspaint.exe", "Paint", "mspaint", "ms paint", "الرسام", "بينت");
        Exe("explorer.exe", "File Explorer", "explorer", "explorer", "files", "my files", "this pc", "مستكشف الملفات", "الملفات", "اكسبلورر", "ماي كمبيوتر");
        Exe("taskmgr.exe", "Task Manager", "Taskmgr", "task manager", "مدير المهام", "تاسك مانجر");
        Exe("cmd.exe", "Command Prompt", "cmd", "cmd", "command prompt", "الكوماند", "سي ام دي");
        Exe("powershell.exe", "PowerShell", "powershell", "power shell", "باورشيل");
        Exe("wt.exe", "Windows Terminal", "WindowsTerminal", "terminal", "the terminal", "الترمنال", "تيرمينال");
        Exe("control.exe", "Control Panel", "control", "control panel", "لوحه التحكم", "كنترول بانل");
        Exe("snippingtool.exe", "Snipping Tool", "SnippingTool", "snipping tool", "اداه القص");
        Exe("magnify.exe", "Magnifier", "Magnify", "magnifier", "المكبر");
        Exe("osk.exe", "On-Screen Keyboard", "osk", "on screen keyboard", "الكيبورد اللي علي الشاشه");

        App("Visual Studio Code", "Code", "vs code", "vscode", "code", "visual studio code", "فيجوال ستوديو كود", "في اس كود", "الفي اس كود", "فيس كود");
        App("Visual Studio 2022", "devenv", "visual studio", "فيجوال ستوديو");
        App("Google Chrome", "chrome", "chrome", "google chrome", "كروم", "جوجل كروم");
        App("Microsoft Edge", "msedge", "edge", "microsoft edge", "ايدج", "المتصفح", "the browser", "browser");
        App("Firefox", "firefox", "firefox", "mozilla firefox", "فايرفوكس");
        App("Word", "WINWORD", "word", "microsoft word", "ms word", "الوورد", "وورد");
        App("Excel", "EXCEL", "excel", "microsoft excel", "الاكسل", "اكسل");
        App("PowerPoint", "POWERPNT", "powerpoint", "power point", "microsoft powerpoint", "الباوربوينت", "باوربوينت", "البوربوينت");
        App("Outlook", "OUTLOOK", "outlook", "outlook (new)", "الاوتلوك", "اوتلوك");
        App("Microsoft Teams", "ms-teams", "teams", "microsoft teams", "التيمز", "تيمز");
        App("Spotify", "Spotify", "spotify", "سبوتيفاي");
        App("WhatsApp", "WhatsApp", "whatsapp", "whats app", "الواتساب", "واتساب", "الواتس", "واتس");
        App("Telegram", "Telegram", "telegram", "telegram desktop", "التليجرام", "تليجرام");
        App("Discord", "Discord", "discord", "الديسكورد", "ديسكورد");
        App("Slack", "slack", "slack", "سلاك");
        App("Zoom", "Zoom", "zoom", "زووم");
        App("OBS Studio", "obs64", "obs", "او بي اس");
        App("Microsoft Store", null, "store", "microsoft store", "المتجر", "الستور");
        App("Photos", "Photos", "photos app", "الصور ابلكيشن");
        App("Camera", "WindowsCamera", "camera", "الكاميرا", "كاميرا");
        App("Clock", null, "clock", "alarms", "المنبه", "الساعه ابلكيشن");
        App("Notion", "Notion", "notion", "نوشن");
        App("Postman", "Postman", "postman", "بوستمان");
        App("Docker Desktop", "Docker Desktop", "docker", "دوكر");
        App("Android Studio", "studio64", "android studio", "اندرويد ستوديو");

        Uri("ms-settings:", "Settings", "settings", "windows settings", "الاعدادات", "اعدادات", "السيتنجز", "سيتنجز");
        Uri("ms-settings:bluetooth", "Bluetooth settings", "bluetooth", "bluetooth settings", "البلوتوث", "بلوتوث");
        Uri("ms-settings:network-wifi", "Wi-Fi settings", "wifi", "wi-fi", "wifi settings", "الواي فاي", "واي فاي", "النت");
        Uri("ms-settings:display", "Display settings", "display settings", "اعدادات الشاشه");
        Uri("ms-settings:sound", "Sound settings", "sound settings", "اعدادات الصوت");
        Uri("ms-settings:windowsupdate", "Windows Update", "windows update", "updates", "التحديثات", "ابديت");
        Uri("ms-settings:batterysaver", "Battery settings", "battery settings", "اعدادات البطاريه");
        Uri("ms-settings:notifications", "Notification settings", "notification settings", "اعدادات الاشعارات");
        Uri("ms-settings:appsfeatures", "Installed apps", "installed apps", "apps and features", "البرامج المتسطبه");
        Uri("ms-settings:privacy-microphone", "Microphone privacy settings", "microphone settings", "اعدادات المايك");

        Site("https://www.youtube.com", "YouTube", "youtube", "you tube", "اليوتيوب", "يوتيوب");
        Site("https://www.google.com", "Google", "google", "جوجل");
        Site("https://mail.google.com", "Gmail", "gmail", "جيميل", "الجيميل");
        Site("https://github.com", "GitHub", "github", "git hub", "جيت هاب");
        Site("https://www.facebook.com", "Facebook", "facebook", "فيسبوك", "الفيسبوك", "الفيس");
        Site("https://www.instagram.com", "Instagram", "instagram", "انستجرام", "الانستا", "انستا");
        Site("https://www.linkedin.com", "LinkedIn", "linkedin", "linked in", "لينكدان", "اللينكد ان");
        Site("https://x.com", "X", "twitter", "x.com", "تويتر");
        Site("https://web.whatsapp.com", "WhatsApp Web", "whatsapp web", "واتساب ويب");
        Site("https://www.netflix.com", "Netflix", "netflix", "نتفليكس");
        Site("https://calendar.google.com", "Google Calendar", "google calendar", "calendar", "الكالندر", "التقويم");
        Site("https://drive.google.com", "Google Drive", "google drive", "drive", "جوجل درايف", "الدرايف");
        return d;
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Jarvis.Core;
using Forms = System.Windows.Forms;

namespace Jarvis.Desktop;

public static class Program
{
    /// <summary>
    /// JARVIS.exe [--tray]
    ///   --tray   start quietly (orb and tray icon only), used when the runtime launches the interface.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = new JarvisPaths();
        using var mutex = new Mutex(true, "Local\\JARVIS.Desktop", out var first);
        if (!first)
        {
            // Already running: ask it (through the runtime) to show the dashboard.
            _ = NotifyShowAsync(paths).Wait(TimeSpan.FromSeconds(3));
            return 0;
        }

        var app = new App(paths, startInTray: args.Contains("--tray"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(paths, e.ExceptionObject?.ToString());
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log(paths, e.Exception.ToString());
            // Exit non-zero so the runtime's watchdog restarts the interface.
            e.Handled = true;
            app.Shutdown(1);
        };
        return app.Run();
    }

    private static async Task NotifyShowAsync(JarvisPaths paths)
    {
        using var core = new CoreClient(paths);
        if (await core.EnsureRuntimeAsync(TimeSpan.FromSeconds(2)))
        {
            try { await core.PostAsync("/ui/show"); } catch { }
        }
    }

    internal static void Log(JarvisPaths paths, string? message)
    {
        try { File.AppendAllText(Path.Combine(paths.LogsDir, "desktop.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }
}

/// <summary>Wires together the tray icon, orb, command console, dashboard, hotkeys and the runtime connection.</summary>
public sealed class App : Application
{
    private readonly JarvisPaths _paths;
    private readonly bool _startInTray;
    private readonly CoreClient _core;
    private Forms.NotifyIcon? _tray;
    private OrbWindow? _orb;
    private QuickBarWindow? _quick;
    private ConsoleWindow? _console;
    private DashboardWindow? _dashboard;
    private Hotkeys? _hotkeys;
    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _orbItem;
    private Forms.ToolStripMenuItem? _askItem;
    private Forms.ToolStripMenuItem? _talkItem;
    private string _shortcutsKey = "";
    private bool _paused;
    private bool _connected;
    private int _pendingApprovals;
    private string _voiceState = "Idle";
    private string? _turnPhase;
    private DateTime _errorUntil;
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };

    public App(JarvisPaths paths, bool startInTray)
    {
        _paths = paths;
        _startInTray = startInTray;
        _core = new CoreClient(paths);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); UpdateOrb(); };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _quick = new QuickBarWindow(_core);
        _quick.OpenDashboardRequested += () => { _quick.Hide(); _ = ShowDashboard(); };
        _console = new ConsoleWindow(_core, _paths);
        _console.DashboardRequested += () => _ = ShowDashboard("assistant");
        _dashboard = new DashboardWindow(_core, _paths);

        CreateTray();
        CreateOrb();
        _hotkeys = new Hotkeys();
        ApplyShortcuts("Ctrl+Alt+J", "Ctrl+Alt+Space", "");

        _core.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => OnConnectionChanged(connected));
        _core.EventReceived += (type, data) => Dispatcher.BeginInvoke(() => OnEvent(type, data));
        _ = Task.Run(_core.RunEventLoopAsync);

        if (!_startInTray) _ = ShowDashboard();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => _ = ShowDashboard());
        _askItem = new Forms.ToolStripMenuItem("Command console", null, (_, _) => ToggleConsole());
        menu.Items.Add(_askItem);
        _talkItem = new Forms.ToolStripMenuItem("Push to talk", null, (_, _) => PushToTalk());
        menu.Items.Add(_talkItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _orbItem = new Forms.ToolStripMenuItem("Show orb", null, (_, _) => ToggleOrb()) { Checked = true };
        menu.Items.Add(_orbItem);
        _pauseItem = new Forms.ToolStripMenuItem("Pause JARVIS", null, async (_, _) => await TogglePauseAsync());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Open logs folder", null, (_, _) => Process.Start(new ProcessStartInfo(_paths.LogsDir) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Close interface (JARVIS keeps running)", null, (_, _) => ExitInterface());
        menu.Items.Add("Shut down JARVIS", null, async (_, _) => await ShutdownJarvisAsync());

        _tray = new Forms.NotifyIcon
        {
            Text = "JARVIS",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleConsole(); };
        _tray.MouseDoubleClick += (_, _) => _ = ShowDashboard();
    }

    private System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "jarvis.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path);
            return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void CreateOrb()
    {
        _orb = new OrbWindow();
        var prefs = DesktopPrefs.Load(_paths);
        var area = SystemParameters.WorkArea;
        _orb.Left = prefs.OrbLeft is { } l && l >= SystemParameters.VirtualScreenLeft && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 ? l : area.Right - _orb.Width - 16;
        _orb.Top = prefs.OrbTop is { } t && t >= SystemParameters.VirtualScreenTop && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40 ? t : area.Bottom - _orb.Height - 16;
        _orb.Clicked += ToggleConsole;
        _orb.DoubleClicked += () => _ = ShowDashboard();
        _orb.MenuRequested += p => _tray?.ContextMenuStrip?.Show((int)p.X, (int)p.Y);
        _orb.Moved += () =>
        {
            var p = DesktopPrefs.Load(_paths);
            p.OrbLeft = _orb.Left;
            p.OrbTop = _orb.Top;
            p.Save(_paths);
        };
        _orb.SetState("offline");
        _orb.Show();
    }

    /// <summary>(Re)registers the global shortcuts from Settings → Shortcuts and says so if one is taken.</summary>
    private void ApplyShortcuts(string console, string talk, string dashboard)
    {
        var key = $"{console}|{talk}|{dashboard}";
        if (_hotkeys is null || key == _shortcutsKey) return;
        _shortcutsKey = key;
        _hotkeys.UnregisterAll();
        var problems = new System.Collections.Generic.List<string>();
        void Bind(string combo, Action action, string name)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            if (!Hotkeys.TryParse(combo, out var mods, out var vk)) { problems.Add($"{name}: \"{combo}\" isn't a valid shortcut"); return; }
            if (!_hotkeys.Register(mods, vk, action)) problems.Add($"{name}: {combo} is already used by another app");
        }
        Bind(console, ToggleConsole, "Command console");
        Bind(talk, PushToTalk, "Push to talk");
        Bind(dashboard, () => _ = ShowDashboard(), "Dashboard");
        Program.Log(_paths, $"Shortcuts: console={console} talk={talk} dashboard={dashboard}{(problems.Count == 0 ? " registered" : "")}");
        if (_askItem is not null) _askItem.Text = string.IsNullOrWhiteSpace(console) ? "Command console" : $"Command console   {console}";
        if (_talkItem is not null) _talkItem.Text = string.IsNullOrWhiteSpace(talk) ? "Push to talk" : $"Push to talk   {talk}";
        foreach (var p in problems) Program.Log(_paths, p);
        if (problems.Count > 0)
            _tray?.ShowBalloonTip(6000, "JARVIS shortcuts", string.Join("\n", problems) + "\nChange them in Settings → Shortcuts.", Forms.ToolTipIcon.Warning);
    }

    private async Task ShowDashboard(string page = "overview")
    {
        _quick?.Hide();
        _console?.Hide();
        await _dashboard!.ShowPageAsync(page);
    }

    private async void ToggleConsole()
    {
        if (_console is { IsVisible: true }) { _console.Hide(); return; }
        if (_quick is { IsVisible: true }) { _quick.Hide(); return; }
        try
        {
            if (_console is not null && await _console.ShowConsoleAsync()) return;
        }
        catch (Exception ex)
        {
            Program.Log(_paths, "Command console failed: " + ex);
            _console?.Hide();
        }
        // No WebView2: the native quick bar does the same job, more plainly.
        if (_quick is not null && _orb is not null) _quick.ShowNear(new Rect(_orb.Left, _orb.Top, _orb.Width, _orb.Height));
    }

    // async void (a hotkey handler): every failure must be caught here, or it would take the whole shell down.
    private async void PushToTalk()
    {
        try
        {
            if (_console is not null && (_console.IsVisible || await _console.ShowConsoleAsync()))
            {
                await _core.PostAsync("/voice/listen");
                return;
            }
            if (_quick is null || _orb is null) return;
            if (!_quick.IsVisible) _quick.ShowNear(new Rect(_orb.Left, _orb.Top, _orb.Width, _orb.Height));
            await _quick.ListenAsync();
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(4000, "JARVIS", ex.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private void ToggleOrb()
    {
        if (_orb is null || _orbItem is null) return;
        if (_orb.IsVisible) _orb.Hide(); else _orb.Show();
        _orbItem.Checked = _orb.IsVisible;
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            await _core.PostAsync(_paused ? "/runtime/resume" : "/runtime/pause");
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(4000, "JARVIS", ex.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        _connected = connected;
        if (_tray is not null) _tray.Text = connected ? "JARVIS" : "JARVIS (reconnecting…)";
        UpdateOrb();
        if (connected) _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var s = await _core.GetAsync("/status");
            await Dispatcher.InvokeAsync(() =>
            {
                _paused = s.Bool("paused");
                _pendingApprovals = s.Int("pendingApprovals");
                _voiceState = s?["voice"]?.Str("state") ?? "Idle";
                _orb?.SetRecording(s?["recording"]?.Str("title"));
                ApplyStatus();
            });
            await ApplySettingsAsync();
        }
        catch { }
    }

    /// <summary>Honours Settings: show orb, orb size/colour/motion, and global shortcuts.</summary>
    private async Task ApplySettingsAsync()
    {
        try
        {
            var settings = await _core.GetAsync("/settings");
            var show = settings?["general"]?["showOrb"]?.GetValue<bool>() ?? true;
            var appearance = settings?["appearance"];
            var shortcuts = settings?["shortcuts"];
            await Dispatcher.InvokeAsync(() =>
            {
                if (_orb is null) return;
                _orb.Configure(appearance?["orbSize"]?.GetValue<int>() ?? 72, appearance.Str("accent") ?? "cyan", appearance.Str("motion") ?? "full");
                if (show && !_orb.IsVisible) _orb.Show();
                if (!show && _orb.IsVisible) _orb.Hide();
                if (_orbItem is not null) _orbItem.Checked = _orb.IsVisible;
                if (shortcuts is not null)
                    ApplyShortcuts(shortcuts.Str("commandConsole") ?? "", shortcuts.Str("pushToTalk") ?? "", shortcuts.Str("dashboard") ?? "");
            });
        }
        catch { }
    }

    private void ApplyStatus()
    {
        if (_pauseItem is not null) _pauseItem.Text = _paused ? "Resume JARVIS" : "Pause JARVIS";
        _orb?.SetAttention(_pendingApprovals > 0);
        UpdateOrb();
    }

    /// <summary>
    /// Same priority as the dashboard: offline > error (briefly) > warning (approval) > speaking >
    /// listening > executing > thinking > idle.
    /// </summary>
    private void UpdateOrb()
    {
        if (_orb is null) return;
        string state;
        if (!_connected || _paused) state = "offline";
        else if (DateTime.UtcNow < _errorUntil) state = "error";
        else if (_pendingApprovals > 0) state = "warning";
        else if (_voiceState == "Speaking") state = "speaking";
        else if (_voiceState == "Listening") state = "listening";
        else if (_voiceState == "Transcribing") state = "thinking";
        else if (_turnPhase == "executing") state = "executing";
        else if (_turnPhase is not null) state = "thinking";
        else state = "idle";
        if (_orb.State != state) _orb.SetState(state);
    }

    private void OnEvent(string type, JsonNode? data)
    {
        switch (type)
        {
            case "hello":
                _paused = data.Bool("paused");
                _pendingApprovals = data.Int("pendingApprovals");
                _voiceState = data?["voice"]?.Str("state") ?? "Idle";
                ApplyStatus();
                break;
            case "voice.state":
                _voiceState = data.Str("state") ?? "Idle";
                UpdateOrb();
                break;
            case "meeting.changed":
                if (data?["recording"] is not null) _orb?.SetRecording(data.Bool("recording") ? data.Str("title") ?? "a meeting" : null);
                break;
            case "agent.turn.started":
                _turnPhase = "understanding";
                UpdateOrb();
                break;
            case "agent.turn.phase":
                var phase = data.Str("phase");
                _turnPhase = phase is "completed" or "failed" ? null : phase;
                UpdateOrb();
                break;
            case "agent.turn.completed":
                _turnPhase = null;
                if (!data.Bool("success"))
                {
                    _errorUntil = DateTime.UtcNow.AddSeconds(4); // same as the dashboard orb
                    _errorTimer.Stop();
                    _errorTimer.Start();
                }
                UpdateOrb();
                break;
            case "runtime.state":
                if (data.Bool("stopping"))
                {
                    _core.ShutdownRequested = true;
                    ExitInterface();
                    return;
                }
                _paused = data.Bool("paused");
                ApplyStatus();
                break;
            case "approval.requested":
                _pendingApprovals++;
                ApplyStatus();
                // Approvals need a human: bring them in front of the user unless the dashboard is already up.
                if (_dashboard?.IsActive != true && _console?.IsVisible != true && _quick?.IsVisible != true) ToggleConsole();
                else _ = _quick?.RefreshApprovalsAsync();
                break;
            case "approval.resolved":
                _pendingApprovals = Math.Max(0, _pendingApprovals - 1);
                ApplyStatus();
                _ = _quick?.RefreshApprovalsAsync();
                break;
            case "ui.show":
                _ = ShowDashboard();
                break;
            case "ui.console":
                if (_console?.IsVisible != true) ToggleConsole();
                break;
            case "settings.changed":
                _ = ApplySettingsAsync();
                break;
        }
    }

    private async Task ShutdownJarvisAsync()
    {
        var answer = MessageBox.Show("Shut down JARVIS completely? Reminders and voice will stop until you start it again.",
            "JARVIS", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _core.ShutdownRequested = true;
        try { await _core.PostAsync("/runtime/shutdown"); } catch { }
        ExitInterface();
    }

    private void ExitInterface()
    {
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_dashboard is not null) _dashboard.AllowClose = true;
        _core.ShutdownRequested = true;
        _core.Dispose();
        Shutdown(0);
    }
}

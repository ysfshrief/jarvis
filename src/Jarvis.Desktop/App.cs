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

/// <summary>Wires together the tray icon, orb, quick bar, dashboard, hotkeys and the runtime connection.</summary>
public sealed class App : Application
{
    private readonly JarvisPaths _paths;
    private readonly bool _startInTray;
    private readonly CoreClient _core;
    private Forms.NotifyIcon? _tray;
    private OrbWindow? _orb;
    private QuickBarWindow? _quick;
    private DashboardWindow? _dashboard;
    private Hotkeys? _hotkeys;
    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _orbItem;
    private bool _paused;
    private int _pendingApprovals;

    public App(JarvisPaths paths, bool startInTray)
    {
        _paths = paths;
        _startInTray = startInTray;
        _core = new CoreClient(paths);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _quick = new QuickBarWindow(_core);
        _quick.OpenDashboardRequested += () => { _quick.Hide(); _ = ShowDashboard(); };
        _dashboard = new DashboardWindow(_core, _paths);

        CreateTray();
        CreateOrb();
        RegisterHotkeys();

        _core.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => OnConnectionChanged(connected));
        _core.EventReceived += (type, data) => Dispatcher.BeginInvoke(() => OnEvent(type, data));
        _ = Task.Run(_core.RunEventLoopAsync);

        if (!_startInTray) _ = ShowDashboard();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => _ = ShowDashboard());
        menu.Items.Add("Ask JARVIS…   Ctrl+Alt+J", null, (_, _) => ToggleQuickBar());
        menu.Items.Add("Push to talk   Ctrl+Alt+Space", null, (_, _) => PushToTalk());
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
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleQuickBar(); };
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
        _orb.Left = prefs.OrbLeft is { } l && l >= SystemParameters.VirtualScreenLeft && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 ? l : area.Right - _orb.Width - 24;
        _orb.Top = prefs.OrbTop is { } t && t >= SystemParameters.VirtualScreenTop && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40 ? t : area.Bottom - _orb.Height - 24;
        _orb.Clicked += ToggleQuickBar;
        _orb.DoubleClicked += () => _ = ShowDashboard();
        _orb.MenuRequested += p => _tray?.ContextMenuStrip?.Show((int)p.X, (int)p.Y);
        _orb.Moved += () =>
        {
            var p = DesktopPrefs.Load(_paths);
            p.OrbLeft = _orb.Left;
            p.OrbTop = _orb.Top;
            p.Save(_paths);
        };
        _orb.SetState("Disconnected");
        _orb.Show();
    }

    private void RegisterHotkeys()
    {
        _hotkeys = new Hotkeys();
        if (!_hotkeys.Register(Hotkeys.ModControl | Hotkeys.ModAlt, 0x4A /* J */, ToggleQuickBar))
            Program.Log(_paths, "Ctrl+Alt+J is taken by another app");
        if (!_hotkeys.Register(Hotkeys.ModControl | Hotkeys.ModAlt, 0x20 /* Space */, PushToTalk))
            Program.Log(_paths, "Ctrl+Alt+Space is taken by another app");
    }

    private async Task ShowDashboard(string page = "overview")
    {
        _quick?.Hide();
        await _dashboard!.ShowPageAsync(page);
    }

    private void ToggleQuickBar()
    {
        if (_quick is null || _orb is null) return;
        if (_quick.IsVisible) { _quick.Hide(); return; }
        _quick.ShowNear(new Rect(_orb.Left, _orb.Top, _orb.Width, _orb.Height));
    }

    private void PushToTalk()
    {
        if (_quick is null || _orb is null) return;
        if (!_quick.IsVisible) _quick.ShowNear(new Rect(_orb.Left, _orb.Top, _orb.Width, _orb.Height));
        _ = _quick.ListenAsync();
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
        if (_tray is not null) _tray.Text = connected ? "JARVIS" : "JARVIS (reconnecting…)";
        if (!connected)
        {
            _orb?.SetState("Disconnected");
            return;
        }
        _ = RefreshStatusAsync();
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
                ApplyStatus(s?["voice"]?.Str("state") ?? "Idle");
            });
        }
        catch { }
    }

    private void ApplyStatus(string voiceState)
    {
        if (_pauseItem is not null) _pauseItem.Text = _paused ? "Resume JARVIS" : "Pause JARVIS";
        _orb?.SetState(_paused ? "Paused" : voiceState is "Unavailable" ? "Idle" : voiceState);
        _orb?.SetAttention(_pendingApprovals > 0);
    }

    private void OnEvent(string type, JsonNode? data)
    {
        switch (type)
        {
            case "hello":
                _paused = data.Bool("paused");
                _pendingApprovals = data.Int("pendingApprovals");
                ApplyStatus(data?["voice"]?.Str("state") ?? "Idle");
                break;
            case "voice.state":
                if (!_paused) _orb?.SetState(data.Str("state") is "Unavailable" ? "Idle" : data.Str("state") ?? "Idle");
                break;
            case "agent.turn.started":
                if (!_paused && _orb?.State is "Idle" or "WakeListening") _orb?.SetState("Thinking");
                break;
            case "agent.turn.completed":
                if (_orb?.State == "Thinking") _orb.SetState("Idle");
                break;
            case "runtime.state":
                if (data.Bool("stopping"))
                {
                    _core.ShutdownRequested = true;
                    ExitInterface();
                    return;
                }
                _paused = data.Bool("paused");
                ApplyStatus("Idle");
                break;
            case "approval.requested":
                _pendingApprovals++;
                _orb?.SetAttention(true);
                // Approvals need a human: bring them in front of the user unless the dashboard is already up.
                if (_dashboard?.IsActive != true && _orb is not null && _quick is not null)
                    _quick.ShowNear(new Rect(_orb.Left, _orb.Top, _orb.Width, _orb.Height));
                else _ = _quick?.RefreshApprovalsAsync();
                break;
            case "approval.resolved":
                _pendingApprovals = Math.Max(0, _pendingApprovals - 1);
                _orb?.SetAttention(_pendingApprovals > 0);
                _ = _quick?.RefreshApprovalsAsync();
                break;
            case "ui.show":
                _ = ShowDashboard();
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

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Jarvis.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Jarvis.Desktop;

/// <summary>One WebView2 environment (and profile) shared by every JARVIS window.</summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> EnvironmentAsync(JarvisPaths paths) =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(paths.DataDir, "webview"));

    /// <summary>Common hardening: no new windows, no navigation away from the local runtime.</summary>
    public static void Harden(CoreWebView2 web, Func<int?> port)
    {
        web.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        web.Settings.IsStatusBarEnabled = false;
        web.Settings.AreBrowserAcceleratorKeysEnabled = true;
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };
        web.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"http://127.0.0.1:{port()}/", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:"))
            {
                e.Cancel = true;
                if (e.Uri.StartsWith("http")) Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
        };
    }
}

/// <summary>
/// The cinematic command console (Ctrl+Alt+J): the dashboard's console page in a borderless,
/// always-on-top window with rounded corners. Esc or clicking elsewhere hides it.
/// </summary>
public sealed class ConsoleWindow : Window
{
    private readonly CoreClient _core;
    private readonly JarvisPaths _paths;
    private readonly WebView2 _web;
    private bool _initialized;
    private string? _loadedFor;

    /// <summary>The page asked to open the dashboard.</summary>
    public event Action? DashboardRequested;

    public ConsoleWindow(CoreClient core, JarvisPaths paths)
    {
        _core = core;
        _paths = paths;
        Title = "JARVIS console";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 780;
        Height = 560;
        Background = new SolidColorBrush(Color.FromRgb(0x04, 0x0B, 0x14));
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x04, 0x0B, 0x14) };
        Content = _web;
        Deactivated += (_, _) => Hide();
        SourceInitialized += (_, _) => RoundCorners();
    }

    /// <summary>True once WebView2 is known to work; false means the caller should use the native quick bar.</summary>
    public bool Available { get; private set; } = true;

    public async Task<bool> ShowConsoleAsync()
    {
        if (!Available) return false;
        if (_core.Info is null && !await _core.EnsureRuntimeAsync(TimeSpan.FromSeconds(10))) return false;
        try
        {
            if (!_initialized)
            {
                var env = await WebViewHost.EnvironmentAsync(_paths);
                await _web.EnsureCoreWebView2Async(env);
                WebViewHost.Harden(_web.CoreWebView2, () => _core.Info?.Port);
                _web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    var msg = e.TryGetWebMessageAsString();
                    if (msg == "hide") Hide();
                    else if (msg == "dashboard") { Hide(); DashboardRequested?.Invoke(); }
                };
                _initialized = true;
            }
            // Reload only when the runtime restarted (new port/token); otherwise just show it again.
            var url = _core.DashboardUrl("console");
            if (_loadedFor != url)
            {
                _web.CoreWebView2.Navigate(url);
                _loadedFor = url;
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Available = false;
            return false;
        }

        PlaceOnActiveScreen();
        Show();
        Activate();
        _web.Focus();
        try { await _web.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('focus'))"); } catch { }
        return true;
    }

    /// <summary>Upper third of the screen the mouse is on — where the user is looking.</summary>
    private void PlaceOnActiveScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        var left = screen.Left * scaleX;
        var top = screen.Top * scaleY;
        var width = screen.Width * scaleX;
        var height = screen.Height * scaleY;
        Width = Math.Min(780, width - 32);
        Height = Math.Min(560, height - 32);
        Left = left + (width - Width) / 2;
        Top = top + Math.Max(16, height * 0.16);
    }

    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND (Windows 11; ignored on Windows 10)
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            var border = 0x00FFD03F; // COLORREF 0x00BBGGRR: cyan accent border
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch { /* cosmetic */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

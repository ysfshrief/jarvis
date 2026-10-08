using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Jarvis.Desktop;

/// <summary>
/// The full dashboard, hosted in WebView2 (Microsoft Edge engine, preinstalled on Windows 10/11).
/// Closing the window only hides it; JARVIS keeps running in the background.
/// </summary>
public sealed class DashboardWindow : Window
{
    private readonly CoreClient _core;
    private readonly JarvisPaths _paths;
    private readonly WebView2 _web;
    private readonly TextBlock _status;
    private bool _initialized;
    private bool _webViewFailed;

    public bool AllowClose { get; set; }

    public DashboardWindow(CoreClient core, JarvisPaths paths)
    {
        _core = core;
        _paths = paths;
        Title = "JARVIS";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1280, area.Width * 0.92);
        Height = Math.Min(820, area.Height * 0.92);
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x02, 0x06, 0x0C));
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/jarvis.ico")); } catch { }

        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x02, 0x06, 0x0C) };
        _status = new TextBlock
        {
            Text = "Connecting to JARVIS…", Foreground = Brushes.LightGray, FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        SourceInitialized += (_, _) =>
        {
            // Dark title bar to match the HUD (Windows 10 20H1+ / 11).
            try
            {
                var dark = 1;
                DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            }
            catch { }
        };
        var grid = new Grid();
        grid.Children.Add(_status);
        grid.Children.Add(_web);
        Content = grid;
    }

    public async Task ShowPageAsync(string page = "overview")
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (_core.Info is null && !await _core.EnsureRuntimeAsync(TimeSpan.FromSeconds(20)))
        {
            _status.Text = "The JARVIS runtime isn't running and couldn't be started. Check the logs in %LOCALAPPDATA%\\JARVIS\\logs.";
            _web.Visibility = Visibility.Collapsed;
            return;
        }

        if (_webViewFailed)
        {
            OpenInBrowser(page);
            return;
        }

        try
        {
            if (!_initialized)
            {
                var env = await WebViewHost.EnvironmentAsync(_paths);
                await _web.EnsureCoreWebView2Async(env);
                WebViewHost.Harden(_web.CoreWebView2, () => _core.Info?.Port);
                _initialized = true;
            }
            _web.Visibility = Visibility.Visible;
            _web.CoreWebView2.Navigate(_core.DashboardUrl(page));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _webViewFailed = true;
            _status.Text = "Microsoft Edge WebView2 is not installed, so the dashboard opened in your browser instead.";
            _web.Visibility = Visibility.Collapsed;
            OpenInBrowser(page);
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OpenInBrowser(string page) =>
        Process.Start(new ProcessStartInfo(_core.DashboardUrl(page)) { UseShellExecute = true });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

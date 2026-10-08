using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jarvis.Desktop;

/// <summary>
/// A compact command bar near the orb (Ctrl+Alt+J). Type a request in English or Arabic, see the
/// answer, approve or deny pending actions, or hold the mic button to speak.
/// </summary>
public sealed class QuickBarWindow : Window
{
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x10, 0x1A, 0x2B));
    private static readonly Brush Border2 = new SolidColorBrush(Color.FromRgb(0x2A, 0x3F, 0x60));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xF8));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8E, 0xA2, 0xBF));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x38, 0xC6, 0xF4));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    private readonly CoreClient _core;
    private readonly TextBox _input;
    private readonly TextBlock _reply;
    private readonly StackPanel _approvals;
    private readonly Button _mic;
    private bool _busy;

    public event Action? OpenDashboardRequested;

    public QuickBarWindow(CoreClient core)
    {
        _core = core;
        Title = "JARVIS";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 520;
        SizeToContent = SizeToContent.Height;

        _input = new TextBox
        {
            FontSize = 16, Padding = new Thickness(10, 8, 10, 8), Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x13, 0x20)),
            Foreground = Text, CaretBrush = Accent, BorderBrush = Border2, BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Segoe UI Arabic"),
        };
        _input.TextChanged += (_, _) => _input.FlowDirection = ContainsArabic(_input.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Hide(); }
        };

        _mic = MakeButton("🎤", "Push to talk (Ctrl+Alt+Space)");
        _mic.Click += async (_, _) => await ListenAsync();
        var send = MakeButton("➤", "Send (Enter)");
        send.Click += async (_, _) => await SendAsync();
        var open = MakeButton("⧉", "Open dashboard");
        open.Click += (_, _) => OpenDashboardRequested?.Invoke();

        var inputRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(send, Dock.Right);
        DockPanel.SetDock(_mic, Dock.Right);
        inputRow.Children.Add(open);
        inputRow.Children.Add(send);
        inputRow.Children.Add(_mic);
        inputRow.Children.Add(_input);

        _reply = new TextBlock
        {
            Foreground = Text, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 2, 0),
            Visibility = Visibility.Collapsed, FontFamily = _input.FontFamily,
        };
        _approvals = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var hint = new TextBlock
        {
            Text = "English or عربي · Esc to close · Ctrl+Alt+J to toggle",
            Foreground = Muted, FontSize = 11, Margin = new Thickness(2, 8, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(inputRow);
        stack.Children.Add(_reply);
        stack.Children.Add(_approvals);
        stack.Children.Add(hint);

        Content = new Border
        {
            Background = Panel, BorderBrush = Border2, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12), Child = stack, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
        };

        Deactivated += (_, _) => { if (!_busy && _approvals.Children.Count == 0) Hide(); };
    }

    public void ShowNear(Rect anchor)
    {
        var area = SystemParameters.WorkArea;
        Show();
        UpdateLayout();
        var left = anchor.Left - Width + anchor.Width;
        var top = anchor.Top - ActualHeight - 4;
        if (top < area.Top) top = anchor.Bottom + 4;
        Left = Math.Clamp(left, area.Left, area.Right - Width);
        Top = Math.Clamp(top, area.Top, area.Bottom - ActualHeight);
        Activate();
        _input.Focus();
        _ = RefreshApprovalsAsync();
    }

    public async Task RefreshApprovalsAsync()
    {
        try
        {
            var list = (await _core.GetAsync("/approvals")).Arr();
            Dispatcher.Invoke(() =>
            {
                _approvals.Children.Clear();
                foreach (var a in list) _approvals.Children.Add(ApprovalView(a!));
            });
        }
        catch { /* runtime not reachable; nothing to show */ }
    }

    private UIElement ApprovalView(JsonNode a)
    {
        var id = a.Str("id")!;
        var risk = a.Str("risk") ?? "Sensitive";
        var title = new TextBlock { Text = $"Approval needed · {risk}", Foreground = risk == "Critical" ? Bad : Warn, FontWeight = FontWeights.SemiBold };
        var summary = new TextBlock { Text = a.Str("summary"), Foreground = Text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 4, 0, 4) };
        var approve = MakeButton("Approve", "Run this action");
        var deny = MakeButton("Deny", "Don't run it");
        approve.Background = Accent;
        approve.Foreground = Brushes.Black;
        approve.Click += async (_, _) => await DecideAsync(id, true);
        deny.Click += async (_, _) => await DecideAsync(id, false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(approve);
        buttons.Children.Add(deny);
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(summary);
        stack.Children.Add(buttons);
        return new Border
        {
            BorderBrush = risk == "Critical" ? Bad : Warn, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 6, 6),
            Margin = new Thickness(0, 6, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x15, 0x22, 0x38)), Child = stack,
        };
    }

    private async Task DecideAsync(string id, bool approve)
    {
        try { await _core.PostAsync($"/approvals/{id}", new { approve }); }
        catch (Exception ex) { ShowReply(ex.Message, error: true); }
        await RefreshApprovalsAsync();
    }

    private async Task SendAsync()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _busy) return;
        _busy = true;
        _input.IsEnabled = false;
        ShowReply("…", error: false);
        try
        {
            var r = await _core.PostAsync("/chat", new { text, source = "text" });
            ShowReply(r.Str("reply") ?? "", error: !r.Bool("success"));
            _input.Clear();
        }
        catch (Exception ex)
        {
            ShowReply($"I couldn't reach the JARVIS runtime: {ex.Message}", error: true);
        }
        finally
        {
            _busy = false;
            _input.IsEnabled = true;
            _input.Focus();
        }
    }

    public async Task ListenAsync()
    {
        if (_busy) return;
        _busy = true;
        _mic.Background = Bad;
        ShowReply("Listening… speak now.", error: false);
        try
        {
            var r = await _core.PostAsync("/voice/listen");
            var result = r?["result"];
            ShowReply(result is null ? "I didn't catch anything." : result.Str("reply") ?? "", error: false);
        }
        catch (Exception ex)
        {
            ShowReply(ex.Message, error: true);
        }
        finally
        {
            _busy = false;
            _mic.ClearValue(BackgroundProperty);
            _mic.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x22, 0x38));
        }
    }

    private void ShowReply(string text, bool error)
    {
        _reply.Text = text;
        _reply.Foreground = error ? Bad : Text;
        _reply.FlowDirection = ContainsArabic(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _reply.Visibility = Visibility.Visible;
    }

    private static Button MakeButton(string content, string tooltip) => new()
    {
        Content = content, ToolTip = tooltip, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), MinWidth = 38,
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x22, 0x38)), Foreground = Text, BorderBrush = Border2, Cursor = Cursors.Hand,
    };

    private static bool ContainsArabic(string s) => s.Any(ch => ch is >= '\u0600' and <= '\u06FF');
}

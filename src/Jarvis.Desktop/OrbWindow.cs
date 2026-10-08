using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Jarvis.Desktop;

/// <summary>
/// The floating orb: JARVIS's always-visible presence. Its colour and motion show the state
/// (idle, listening, thinking, speaking, paused, needs attention). Click for the quick bar,
/// double-click for the dashboard, drag to move, right-click for the menu.
/// </summary>
public sealed class OrbWindow : Window
{
    private const double OrbSize = 64;
    private readonly Ellipse _glow;
    private readonly Ellipse _ring;
    private readonly Ellipse _core;
    private readonly Path _arc;
    private readonly RotateTransform _arcRotation = new();
    private readonly ScaleTransform _glowScale = new(1, 1);
    private readonly Ellipse _badge;
    private Point _downAt;
    private bool _dragging;
    private string _state = "Idle";
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer;

    public event Action? Clicked;
    public event Action? DoubleClicked;
    public event Action<Point>? MenuRequested;
    public event Action? Moved;

    public OrbWindow()
    {
        Title = "JARVIS";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = OrbSize + 32;
        Height = OrbSize + 32;
        ShowActivated = false;

        var root = new Grid { Width = Width, Height = Height };

        _glow = new Ellipse
        {
            Width = OrbSize + 26, Height = OrbSize + 26,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _glowScale,
            Effect = new BlurEffect { Radius = 14 },
            IsHitTestVisible = false,
        };
        _ring = new Ellipse { Width = OrbSize, Height = OrbSize, StrokeThickness = 2.5 };
        _core = new Ellipse { Width = OrbSize - 16, Height = OrbSize - 16 };
        _arc = new Path
        {
            Width = OrbSize + 8, Height = OrbSize + 8,
            StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 4,36 A 32,32 0 0 1 36,4"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _arcRotation,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Ellipse
        {
            Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
            Stroke = Brushes.White, StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 14, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };

        root.Children.Add(_glow);
        root.Children.Add(_ring);
        root.Children.Add(_core);
        root.Children.Add(_arc);
        root.Children.Add(_badge);
        // A transparent hit target so clicks register everywhere on the orb.
        root.Children.Add(new Ellipse { Width = OrbSize, Height = OrbSize, Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.Hand });
        Content = root;
        ToolTip = "JARVIS — click to ask, double-click for the dashboard";

        // A single click waits for the double-click interval so a double-click doesn't also open the quick bar.
        _clickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); Clicked?.Invoke(); };

        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseMove += OnMove;
        MouseRightButtonUp += (_, e) => MenuRequested?.Invoke(PointToScreen(e.GetPosition(this)));
        SetState("Idle");
    }

    public string State => _state;

    public void SetAttention(bool on) => _badge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    public void SetState(string state)
    {
        _state = state;
        var color = state switch
        {
            "Listening" or "WakeListening" => Color.FromRgb(0x22, 0xD3, 0xEE),
            "Transcribing" or "Thinking" => Color.FromRgb(0xA7, 0x8B, 0xFA),
            "Speaking" => Color.FromRgb(0x34, 0xD3, 0x99),
            "Paused" or "Unavailable" or "Disconnected" => Color.FromRgb(0x64, 0x74, 0x8B),
            _ => Color.FromRgb(0x38, 0xC6, 0xF4),
        };

        _glow.Fill = new RadialGradientBrush(Color.FromArgb(150, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
        _ring.Stroke = new SolidColorBrush(Lighten(color, 0.35));
        _core.Fill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.42),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xE8, 0xFB, 0xFF), 0.0),
                new GradientStop(Color.FromRgb(0xE8, 0xFB, 0xFF), 0.18),
                new GradientStop(color, 0.5),
                new GradientStop(Color.FromRgb(0x06, 0x20, 0x33), 1.0),
            },
        };
        _arc.Stroke = new SolidColorBrush(Lighten(color, 0.5));

        // Motion per state.
        _glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _glow.BeginAnimation(OpacityProperty, null);
        _arcRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _arc.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case "Thinking":
            case "Transcribing":
                _arc.Visibility = Visibility.Visible;
                _arcRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
                break;
            case "Listening":
                Pulse(0.9, 1.15, 0.45);
                break;
            case "Speaking":
                Pulse(0.95, 1.12, 0.25);
                break;
            case "Paused":
            case "Unavailable":
            case "Disconnected":
                _glow.Opacity = 0.35;
                break;
            default:
                Pulse(0.94, 1.04, 2.0); // calm breathing
                break;
        }
    }

    private void Pulse(double from, double to, double seconds)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        _glow.Opacity = 1;
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            DoubleClicked?.Invoke();
            return;
        }
        _downAt = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragging && (Math.Abs(p.X - _downAt.X) > 4 || Math.Abs(p.Y - _downAt.Y) > 4))
        {
            _dragging = true;
            ReleaseMouseCapture();
            DragMove();
            Moved?.Invoke();
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!_dragging && e.ClickCount == 1) _clickTimer.Start();
        _dragging = false;
    }
}

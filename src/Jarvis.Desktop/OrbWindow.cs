using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Jarvis.Desktop;

/// <summary>
/// The floating orb: JARVIS's always-visible presence, drawn as concentric HUD rings around a
/// luminous core. States: idle, listening, thinking, speaking, executing, warning, error, offline.
/// Click for the command console, double-click for the dashboard, drag to move, right-click for the
/// menu. Animations are WPF render transforms (composited on the GPU) and stop entirely when the
/// user turns motion off.
/// </summary>
public sealed class OrbWindow : Window
{
    private readonly Grid _root;
    private readonly Ellipse _glow = new() { IsHitTestVisible = false };
    private readonly Ellipse _ticks = new() { IsHitTestVisible = false, StrokeDashCap = PenLineCap.Round };
    private readonly Ellipse _segments = new() { IsHitTestVisible = false };
    private readonly Ellipse _inner = new() { IsHitTestVisible = false };
    private readonly Path _arc = new() { IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Ellipse _core = new() { IsHitTestVisible = false };
    private readonly Canvas _wave = new() { IsHitTestVisible = false };
    private readonly Ellipse _badge;
    private readonly Ellipse _rec;
    private readonly Ellipse _hit = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.Hand };
    private readonly RotateTransform _ticksRot = new();
    private readonly RotateTransform _segRot = new();
    private readonly RotateTransform _arcRot = new();
    private readonly ScaleTransform _glowScale = new(1, 1);
    private readonly List<ScaleTransform> _bars = [];
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer;
    private Point _downAt;
    private bool _dragging;
    private string _state = "idle";
    private double _size = 72;
    private Color _accent = Color.FromRgb(0x3F, 0xD0, 0xFF);
    private string _motion = "full";

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
        ShowActivated = false;

        _badge = new Ellipse
        {
            Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40)),
            Stroke = Brushes.White, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        // Recording indicator: a red dot that is always visible while a meeting is being recorded.
        _rec = new Ellipse
        {
            Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x4E)),
            Stroke = Brushes.White, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x3B, 0x4E), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        };
        _root = new Grid();
        foreach (var e in new UIElement[] { _glow, _ticks, _segments, _arc, _inner, _wave, _core, _badge, _rec, _hit }) _root.Children.Add(e);
        Content = _root;
        ToolTip = "JARVIS — click for the command console, double-click for the dashboard";

        // A single click waits for the double-click interval so a double-click doesn't also open the console.
        _clickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); Clicked?.Invoke(); };
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseMove += OnMove;
        MouseRightButtonUp += (_, e) => MenuRequested?.Invoke(PointToScreen(e.GetPosition(this)));

        Layout(_size);
        SetState("idle");
    }

    public string State => _state;

    public void SetAttention(bool on) => _badge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    public bool Recording { get; private set; }

    /// <summary>Shows (and pulses) the red recording dot; the tooltip says what is being recorded.</summary>
    public void SetRecording(string? title)
    {
        Recording = title is not null;
        _rec.Visibility = Recording ? Visibility.Visible : Visibility.Collapsed;
        _rec.BeginAnimation(OpacityProperty, Recording
            ? new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
        ToolTip = Recording
            ? $"JARVIS — recording “{title}”. Open the dashboard or say “stop recording” to stop."
            : "JARVIS — click for the command console, double-click for the dashboard";
    }

    /// <summary>Applies Settings → Appearance (size, energy colour, motion).</summary>
    public void Configure(double size, string accent, string motion)
    {
        _accent = accent switch
        {
            "amber" => Color.FromRgb(0xFF, 0xBA, 0x52),
            "violet" => Color.FromRgb(0xAA, 0x8C, 0xFF),
            "green" => Color.FromRgb(0x46, 0xE8, 0xAA),
            _ => Color.FromRgb(0x3F, 0xD0, 0xFF),
        };
        _motion = motion;
        if (Math.Abs(size - _size) > 0.5)
        {
            // Keep the orb's centre where the user put it.
            var cx = Left + Width / 2;
            var cy = Top + Height / 2;
            Layout(size);
            Left = cx - Width / 2;
            Top = cy - Height / 2;
        }
        SetState(_state);
    }

    private void Layout(double size)
    {
        _size = size;
        var pad = Math.Round(size * 0.28);
        Width = Height = size + pad * 2;
        _root.Width = _root.Height = Width;

        void Ring(Shape s, double d, double thickness, RotateTransform? rot = null)
        {
            s.Width = s.Height = d;
            s.StrokeThickness = thickness;
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.VerticalAlignment = VerticalAlignment.Center;
            if (rot is not null) { s.RenderTransformOrigin = new Point(0.5, 0.5); s.RenderTransform = rot; }
        }

        _glow.Width = _glow.Height = Width;
        _glow.RenderTransformOrigin = new Point(0.5, 0.5);
        _glow.RenderTransform = _glowScale;
        Ring(_ticks, size * 1.0, Math.Max(1.2, size / 50), _ticksRot);
        _ticks.StrokeDashArray = new DoubleCollection { 0.4, 3.2 };
        Ring(_segments, size * 0.88, Math.Max(1.8, size / 32), _segRot);
        _segments.StrokeDashArray = new DoubleCollection { 9, 2.4, 2.6, 2.4, 1, 2.4 };
        Ring(_inner, size * 0.7, 1);
        Ring(_core, size * 0.46, 1);

        var r = size * 0.4;
        _arc.Width = _arc.Height = r * 2 + 6;
        _arc.HorizontalAlignment = HorizontalAlignment.Center;
        _arc.VerticalAlignment = VerticalAlignment.Center;
        _arc.Stretch = Stretch.None;
        _arc.StrokeThickness = Math.Max(2, size / 26);
        var c = r + 3;
        _arc.Data = new PathGeometry([new PathFigure(new Point(c, 3), [new ArcSegment(new Point(c + r * Math.Sin(1.1), c - r * Math.Cos(1.1)), new Size(r, r), 0, false, SweepDirection.Clockwise, true)], false)]);
        _arc.RenderTransformOrigin = new Point(0.5, 0.5);
        _arc.RenderTransform = _arcRot;

        // Waveform: short radial bars between the core and the inner ring.
        _wave.Children.Clear();
        _bars.Clear();
        _wave.Width = _wave.Height = Width;
        const int count = 16;
        for (var i = 0; i < count; i++)
        {
            var bar = new Rectangle { Width = Math.Max(1.6, size / 40), Height = size * 0.1, RadiusX = 1, RadiusY = 1 };
            // Scale and rotate around the bar's base so it grows outwards from the core.
            var scale = new ScaleTransform(1, 0.3, bar.Width / 2, bar.Height);
            bar.RenderTransform = new TransformGroup { Children = { scale, new RotateTransform(360.0 / count * i, bar.Width / 2, bar.Height) } };
            var angle = 2 * Math.PI * i / count;
            var baseR = size * 0.25;
            Canvas.SetLeft(bar, Width / 2 + baseR * Math.Sin(angle) - bar.Width / 2);
            Canvas.SetTop(bar, Height / 2 - baseR * Math.Cos(angle) - bar.Height);
            _bars.Add(scale);
            _wave.Children.Add(bar);
        }

        _hit.Width = _hit.Height = size;
        _badge.Margin = new Thickness(0, pad * 0.6, pad * 0.6, 0);
        _rec.Margin = new Thickness(pad * 0.6, pad * 0.6, 0, 0);
    }

    /// <summary>Accepts the eight orb states (and the runtime's voice-state names).</summary>
    public void SetState(string state)
    {
        state = state.ToLowerInvariant() switch
        {
            "paused" or "disconnected" or "unavailable" => "offline",
            "wakelistening" => "idle",
            "transcribing" => "thinking",
            var s => s,
        };
        _state = state;
        var color = state switch
        {
            "listening" => Color.FromRgb(0x22, 0xD3, 0xEE),
            "thinking" => Color.FromRgb(0x8C, 0xDC, 0xFF),
            "speaking" => Color.FromRgb(0x5E, 0xEA, 0xD4),
            "warning" => Color.FromRgb(0xFF, 0xB0, 0x40),
            "error" => Color.FromRgb(0xFF, 0x56, 0x68),
            "offline" => Color.FromRgb(0x64, 0x74, 0x8B),
            _ => _accent,
        };

        _glow.Fill = new RadialGradientBrush(
            [new GradientStop(Color.FromArgb(120, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(40, color.R, color.G, color.B), 0.45), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)]);
        _ticks.Stroke = new SolidColorBrush(Color.FromArgb(130, color.R, color.G, color.B));
        _segments.Stroke = new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B));
        _inner.Stroke = new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B));
        _arc.Stroke = new SolidColorBrush(Lighten(color, 0.3));
        _arc.Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 };
        _core.Fill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.42),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xF2, 0xFD, 0xFF), 0.0),
                new GradientStop(Color.FromRgb(0xD8, 0xF7, 0xFF), 0.2),
                new GradientStop(color, 0.55),
                new GradientStop(Color.FromRgb(0x03, 0x15, 0x22), 1.0),
            },
        };
        _core.Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        foreach (UIElement b in _wave.Children) ((Rectangle)b).Fill = new SolidColorBrush(color);

        // Motion per state.
        Stop(_glowScale, ScaleTransform.ScaleXProperty); Stop(_glowScale, ScaleTransform.ScaleYProperty);
        Stop(_ticksRot, RotateTransform.AngleProperty); Stop(_segRot, RotateTransform.AngleProperty); Stop(_arcRot, RotateTransform.AngleProperty);
        _glow.BeginAnimation(OpacityProperty, null);
        foreach (var s in _bars) Stop(s, ScaleTransform.ScaleYProperty);
        _arc.Visibility = state is "thinking" or "executing" ? Visibility.Visible : Visibility.Collapsed;
        _wave.Visibility = state is "listening" or "speaking" ? Visibility.Visible : Visibility.Collapsed;
        _glow.Opacity = state == "offline" ? 0.3 : 1;

        if (_motion == "off") return;
        var ambient = _motion == "full" && state != "offline";
        if (ambient)
        {
            Spin(_ticksRot, 90, false);
            Spin(_segRot, state is "thinking" ? 6 : state is "executing" ? 10 : 38, true);
        }
        switch (state)
        {
            case "thinking": Spin(_arcRot, 1.1, false); break;
            case "executing": Spin(_arcRot, 2.2, false); break;
            case "listening": Pulse(0.92, 1.08, 0.6); Bars(0.45); break;
            case "speaking": Pulse(0.95, 1.08, 0.3); Bars(0.22); break;
            case "warning": case "error": Pulse(0.9, 1.08, 0.45); break;
            case "offline": break;
            default: if (ambient) Pulse(0.96, 1.03, 2.2); break;
        }
    }

    private static void Stop(Animatable a, DependencyProperty p) => a.BeginAnimation(p, null);

    private static void Spin(RotateTransform r, double seconds, bool reverse) =>
        r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(reverse ? 360 : 0, reverse ? 0 : 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });

    private void Pulse(double from, double to, double seconds)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Bars(double seconds)
    {
        for (var i = 0; i < _bars.Count; i++)
        {
            var a = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(seconds * ((i * 7) % _bars.Count) / _bars.Count),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
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

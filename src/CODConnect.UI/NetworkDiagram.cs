using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CODConnect.UI;

public sealed record FriendVisual(string Name, bool Attached, bool Ready, string? Detail = null, bool Relay = false, bool Lost = false);

public sealed record NetworkVisualState(bool LocalReady, bool RemoteReady, bool TunnelConnected,
    bool LocalAttached = false, bool RemoteAttached = false, IReadOnlyList<FriendVisual>? Friends = null,
    string? LocalDetail = null, string? Internet = null, bool Relayed = false)
{
    public bool LanReady => Friends is { Count: > 1 } friends
        ? LocalReady && TunnelConnected && friends.Any(f => f.Attached) && friends.Where(f => f.Attached).All(f => f.Ready)
        : LocalReady && RemoteReady && TunnelConnected;

    public static NetworkVisualState FromScreen(Screen screen) => new(false, false,
        screen == Screen.Connected, screen != Screen.Home, screen == Screen.Connected);

    public bool Equals(NetworkVisualState? other)
        => other is not null && LocalReady == other.LocalReady && RemoteReady == other.RemoteReady
           && TunnelConnected == other.TunnelConnected && LocalAttached == other.LocalAttached
           && RemoteAttached == other.RemoteAttached && LocalDetail == other.LocalDetail
           && Internet == other.Internet && Relayed == other.Relayed
           && (Friends ?? []).SequenceEqual(other.Friends ?? []);

    public override int GetHashCode() => HashCode.Combine(LocalReady, RemoteReady, TunnelConnected, Friends?.Count ?? 0);
}

public sealed class NetworkDiagram : FrameworkElement
{
    private NetworkVisualState _state = NetworkVisualState.FromScreen(Screen.Home);
    private readonly List<DrawingGroup> _glows = [];
    private Window? _window;
    private bool _reduceMotion;

    public NetworkVisualState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; InvalidateVisual(); }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { _reduceMotion = value; UpdateMotion(); }
    }

    internal bool HasAnimatedGlow => _glows.Any(group => group.HasAnimatedProperties);
    internal int GlowLayerCount => _glows.Count;
    internal double GlowOpacity => _glows.FirstOrDefault()?.Opacity ?? 0;

    public NetworkDiagram()
    {
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += WindowStateChanged;
            SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
            UpdateMotion();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= WindowStateChanged;
            _window = null;
            SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
            StopMotion();
        };
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    private void WindowStateChanged(object? sender, EventArgs e) => UpdateMotion();
    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(UpdateMotion);
    }

    private void StopMotion()
    {
        foreach (var glow in _glows) { glow.BeginAnimation(DrawingGroup.OpacityProperty, null); glow.Opacity = .8; }
    }

    private void UpdateMotion()
    {
        StopMotion();
        if (!IsLoaded || !IsVisible || _window?.WindowState == WindowState.Minimized ||
            ReduceMotion || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        foreach (var glow in _glows)
            glow.BeginAnimation(DrawingGroup.OpacityProperty, new DoubleAnimation(.55, .95, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DiagramPeer(this);
    private sealed class DiagramPeer(NetworkDiagram owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(NetworkDiagram);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetNameCore() => owner.State.Friends is { Count: > 1 } friends
            ? $"Network with {friends.Count + 1} players. {friends.Count(f => f.Ready) + (owner.State.LocalReady ? 1 : 0)} consoles ready. " + (owner.State.LanReady ? "LAN ready." : "Waiting for every console.")
            : $"Network. Your side {(owner.State.LocalReady ? "ready" : "not confirmed")}. Friend’s side {(owner.State.RemoteReady ? "ready" : "not confirmed")}. " +
            (owner.State.LanReady ? "LAN ready." : owner.State.TunnelConnected ? "PC tunnel connected; console readiness not confirmed on both sides." : "Waiting for the PC tunnel.")
            + (owner.State.Relayed ? " Connected through the relay." : "")
            + (owner.State.Internet is { } internet ? $" Console Internet: {DescribeInternet(internet)}." : "");
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }
    private static readonly Brush Accent = Brush("#73D9E5"), Blue = Brush("#65C8FF"), Text = Brush("#EAE9EF"), Muted = Brush("#A1A0AB"), Warn = Brush("#E6B58C");

    private enum Glyph { Console, Pc, Globe }
    private static readonly Point Center = new(340, 280);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        StopMotion(); _glows.Clear();
        var scale = Math.Min(ActualWidth / 680, ActualHeight / 530);
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 680 * scale) / 2, (ActualHeight - 530 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (State.Friends is { Count: > 1 } friends)
        {
            RenderRing(dc, friends);
            dc.Pop(); dc.Pop();
            UpdateMotion();
            return;
        }

        dc.DrawEllipse(null, new Pen(Brush("#29292F"), 1), Center, 180, 180);
        dc.DrawEllipse(null, new Pen(Brush("#34343B"), 9), Center, 136, 136);
        dc.DrawEllipse(null, new Pen(Brush("#202025"), 2), Center, 136, 136);
        dc.DrawEllipse(null, new Pen(Brush("#34343B"), 1), Center, 121, 121);
        Path(dc, "M 120,129 L 120,256", "#484850", 1.5, true);
        Path(dc, "M 560,129 L 560,256", "#484850", 1.5, true);
        Path(dc, "M 144,280 L 204,280", State.LocalAttached ? "#4E6970" : "#414149", 5);
        Path(dc, "M 476,280 L 536,280", State.RemoteAttached ? "#4E6970" : "#414149", 5);
        if (!State.LanReady)
            Path(dc, "M 214,334 A 136,136 0 0 0 466,334", State.TunnelConnected ? "#73D9E5" : "#414149", 2, !State.TunnelConnected);
        foreach (var x in new[] { 204d, 476d })
            dc.DrawRoundedRectangle(Brush((x == 204 ? State.LocalAttached : State.RemoteAttached) ? "#78A1A9" : "#62626B"), null, new Rect(x - 5, 261, 10, 38), 4, 4);
        dc.DrawEllipse(Brush("#202025"), new Pen(Brush("#3C3C44"), 1), Center, 71, 71);

        if (State.LocalReady) DrawSideGlow(dc, true);
        if (State.RemoteReady) DrawSideGlow(dc, false);
        if (State.LanReady) DrawCoreGlow(dc);

        dc.DrawGeometry(State.LanReady ? Blue : Accent, null, Geometry.Parse("M 318,269 L 329,247 L 340,247 L 329,269 Z"));
        dc.DrawGeometry(Brush(State.LanReady ? "#439DDD" : "#527F88"), null, Geometry.Parse("M 333,247 L 344,247 L 355,269 L 344,269 Z"));
        Label(dc, "CODCONNECT", 340, 286, Text, 12);
        Label(dc, State.LanReady ? "LAN ready" : "Virtual LAN", 340, 308, State.LanReady ? Blue : Muted, 11);
        if (State.Relayed) RelayMarker(dc, new Point(340, 416), State.TunnelConnected);
        if (State.Internet is { } internet) InternetNode(dc, internet);
        Node(dc, 120, 105, Glyph.Console, State.LocalReady, State.LocalReady);
        Node(dc, 560, 105, Glyph.Console, State.RemoteReady, State.RemoteReady);
        Node(dc, 120, 280, Glyph.Pc, State.LocalAttached || State.LocalReady, State.LocalReady);
        Node(dc, 560, 280, Glyph.Pc, State.RemoteAttached || State.RemoteReady, State.RemoteReady);
        var friend = State.Friends is { Count: 1 } one ? Short(one[0].Name) : null;
        Label(dc, "Your console", 120, 57, Text);
        Label(dc, friend is null ? "Friend’s console" : $"{friend}’s console", 560, 57, Text);
        Label(dc, "This PC", 120, 325, Text);
        Label(dc, friend is null ? "Friend’s PC" : $"{friend}’s PC", 560, 325, Text);
        dc.Pop(); dc.Pop();
        UpdateMotion();
    }

    private static string Short(string name) => name.Length <= 12 ? name : name[..11] + "…";

    private void RenderRing(DrawingContext dc, IReadOnlyList<FriendVisual> friends)
    {
        const double orbit = 196;
        dc.DrawEllipse(null, new Pen(Brush("#29292F"), 1), Center, 150, 150);
        dc.DrawEllipse(null, new Pen(Brush("#34343B"), 7), Center, 118, 118);
        dc.DrawEllipse(null, new Pen(Brush("#202025"), 2), Center, 118, 118);

        var players = new List<(string Name, bool Attached, bool Ready, string? Detail, bool Relay, bool Lost)> { ("You", State.LocalAttached, State.LocalReady, State.LocalDetail, false, false) };
        players.AddRange(friends.Select(f => (Short(f.Name), f.Attached, f.Ready, f.Detail, f.Relay && f.Attached && !f.Lost, f.Lost)));
        var positions = players.Select((_, i) =>
        {
            var angle = Math.PI + i * 2 * Math.PI / players.Count;
            return new Point(Center.X + orbit * Math.Cos(angle), Center.Y + orbit * Math.Sin(angle));
        }).ToList();

        var spokes = players.Select((p, i) => SpokeGeometry(positions[i], orbit, p.Relay)).ToList();
        for (var i = 0; i < players.Count; i++)
        {
            var pen = new Pen(players[i].Lost ? Warn : Brush(players[i].Attached ? "#4E6970" : "#414149"), players[i].Lost ? 2.5 : 4)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (players[i].Lost) pen.DashStyle = new DashStyle(new[] { 2d, 3d }, 0);
            dc.DrawGeometry(null, pen, spokes[i].Geometry);
        }

        dc.DrawEllipse(Brush("#202025"), new Pen(Brush("#3C3C44"), 1), Center, 71, 71);
        for (var i = 0; i < players.Count; i++)
        {
            if (!players[i].Ready)
            {
                continue;
            }

            var group = new DrawingGroup();
            using (var glow = group.Open())
            {
                GlowStroke(glow, spokes[i].Geometry, 2);
            }

            _glows.Add(group); dc.DrawDrawing(group);
        }

        if (State.LanReady) DrawCoreGlow(dc);
        dc.DrawGeometry(State.LanReady ? Blue : Accent, null, Geometry.Parse("M 318,269 L 329,247 L 340,247 L 329,269 Z"));
        dc.DrawGeometry(Brush(State.LanReady ? "#439DDD" : "#527F88"), null, Geometry.Parse("M 333,247 L 344,247 L 355,269 L 344,269 Z"));
        Label(dc, "CODCONNECT", 340, 286, Text, 12);
        Label(dc, State.LanReady ? "LAN ready" : "Virtual LAN", 340, 308, State.LanReady ? Blue : Muted, 11);

        for (var i = 0; i < players.Count; i++)
        {
            if (spokes[i].Relay is { } relay) RelayMarker(dc, relay, players[i].Ready);
        }

        for (var i = 0; i < players.Count; i++)
        {
            var (x, y) = (positions[i].X, positions[i].Y);
            Node(dc, x, y, Glyph.Console, players[i].Attached || players[i].Ready, players[i].Ready, players[i].Lost);
            if (i == 0 && State.Internet is { } internet) InternetBadge(dc, x, y, internet == "Ready");
            var above = y < Center.Y - 40;
            var top = above ? y - (players[i].Detail is null ? 50 : 66) : y + 30;
            Label(dc, players[i].Name, x, top, Text);
            if (players[i].Detail is { } detail)
                Label(dc, detail, x, top + 18, players[i].Lost ? Warn : players[i].Ready ? Accent : Muted, 11);
        }
    }

    private static (Geometry Geometry, Point? Relay) SpokeGeometry(Point player, double orbit, bool relay)
    {
        var (from, to) = Spoke(player, 122, orbit - 30);
        if (!relay) return (new LineGeometry(from, to), null);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var bend = new Point((from.X + to.X) / 2 - dy / length * 30, (from.Y + to.Y) / 2 + dx / length * 30);
        var path = new PathGeometry([new PathFigure(from, [new PolyLineSegment([bend, to], true)], false)]);
        return (path, bend);
    }

    private void RelayMarker(DrawingContext dc, Point at, bool live)
    {
        var stroke = live ? Blue : Accent;
        dc.DrawEllipse(Brush(live ? "#172C3B" : "#1E2F33"), new Pen(stroke, 1.2), at, 15, 15);
        var (x, y) = (at.X, at.Y);
        dc.DrawGeometry(null, new Pen(stroke, 1.3) { LineJoin = PenLineJoin.Round }, Geometry.Parse(FormattableString.Invariant(
            $"M {x - 7},{y + 4.5} L {x + 6.5},{y + 4.5} A 3.6,3.6 0 0 0 {x + 7},{y - 2.5} A 5.2,5.2 0 0 0 {x - 2.6},{y - 3.8} A 4.2,4.2 0 0 0 {x - 7},{y + 4.5} Z")));
        Label(dc, "Relay", x, y + 17, stroke, 10);
    }

    private void InternetNode(DrawingContext dc, string state)
    {
        var ready = state == "Ready";
        var active = ready || state == "Starting";
        Path(dc, "M 120,352 L 120,410", active ? "#4E6970" : "#414149", 2, !ready);
        if (ready)
        {
            var group = new DrawingGroup();
            using (var glow = group.Open()) GlowStroke(glow, Geometry.Parse("M 120,352 L 120,410"), 2);
            _glows.Add(group); dc.DrawDrawing(group);
        }

        Node(dc, 120, 434, Glyph.Globe, active, ready);
        Label(dc, "Internet", 120, 464, Text);
        Label(dc, DescribeInternet(state), 120, 482, ready ? Accent : state == "Unavailable" ? Warn : Muted, 11);
    }

    private static void InternetBadge(DrawingContext dc, double x, double y, bool ready)
    {
        var stroke = ready ? Blue : Brush("#6A6A73");
        var at = new Point(x - 18, y - 18);
        dc.DrawEllipse(Brush(ready ? "#172C3B" : "#25252B"), new Pen(stroke, 1), at, 7, 7);
        var pen = new Pen(stroke, 0.9);
        dc.DrawEllipse(null, pen, at, 3.8, 3.8);
        dc.DrawEllipse(null, pen, at, 1.6, 3.8);
        dc.DrawLine(pen, new Point(at.X - 3.8, at.Y), new Point(at.X + 3.8, at.Y));
    }

    internal static string DescribeInternet(string state) => state switch
    {
        "Ready" => "Through this PC",
        "Starting" => "Setting up…",
        "Waiting" => "Waiting for the console",
        "Unavailable" => "Unavailable",
        _ => "Off",
    };

    private static (Point From, Point To) Spoke(Point player, double inner, double outer)
    {
        var dx = player.X - Center.X;
        var dy = player.Y - Center.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return (new Point(Center.X + dx / length * inner, Center.Y + dy / length * inner),
                new Point(Center.X + dx / length * outer, Center.Y + dy / length * outer));
    }

    private void DrawSideGlow(DrawingContext dc, bool left)
    {
        var group = new DrawingGroup();
        using (var glow = group.Open())
        {
            foreach (var radius in new[] { 180d, 136d, 121d })
            {
                var path = new PathGeometry([new PathFigure(new Point(340, 280 - radius),
                    [new ArcSegment(new Point(340, 280 + radius), new Size(radius, radius), 0, false,
                        left ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true)], false)]);
                GlowStroke(glow, path, radius == 136 ? 2.4 : 1.1, false);
            }
            GlowStroke(glow, Geometry.Parse(left ? "M 120,129 L 120,256 M 144,280 L 204,280" : "M 560,129 L 560,256 M 476,280 L 536,280"), 2);
        }
        _glows.Add(group); dc.DrawDrawing(group);
    }

    private void DrawCoreGlow(DrawingContext dc)
    {
        var group = new DrawingGroup();
        using (var glow = group.Open())
        {
            var fill = new RadialGradientBrush(Color.FromArgb(50, 63, 163, 236), Colors.Transparent);
            glow.DrawEllipse(fill, null, Center, 99, 99);
            GlowStroke(glow, new EllipseGeometry(Center, 71, 71), 1.5);
            GlowStroke(glow, new EllipseGeometry(Center, 136, 136), 1.2);
        }
        _glows.Add(group); dc.DrawDrawing(group);
    }

    private static void GlowStroke(DrawingContext dc, Geometry geometry, double width, bool roundEnds = true)
    {
        foreach (var (spread, alpha) in new[] { (20d, .025), (12d, .05), (6d, .11), (0d, .85) })
        {
            dc.PushOpacity(alpha);
            dc.DrawGeometry(null, new Pen(Blue, width + spread) { StartLineCap = roundEnds ? PenLineCap.Round : PenLineCap.Flat, EndLineCap = roundEnds ? PenLineCap.Round : PenLineCap.Flat }, geometry);
            dc.Pop();
        }
    }

    private void Label(DrawingContext dc, string value, double x, double y, Brush brush, double size = 13)
    {
        var scale = Math.Min(ActualWidth / 680, ActualHeight / 530);
        size = Math.Max(size, 11 / scale);
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(x - text.Width / 2, y));
    }

    private static void Path(DrawingContext dc, string data, string color, double width, bool dashed = false)
    {
        var pen = new Pen(Brush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) pen.DashStyle = new DashStyle(new[] { 3d, 5d }, 0);
        dc.DrawGeometry(null, pen, Geometry.Parse(data));
    }

    private static void Node(DrawingContext dc, double x, double y, Glyph glyph, bool attached, bool ready, bool lost = false)
    {
        var foreground = lost ? Warn : ready ? Blue : attached ? Accent : Muted;
        dc.DrawRoundedRectangle(Brush(lost ? "#2B2520" : ready ? "#223544" : attached ? "#273439" : "#25252B"),
            new Pen(lost ? Warn : ready ? Blue : Brush(attached ? "#587F86" : "#494950"), 1), new Rect(x - 24, y - 24, 48, 48), 7, 7);
        var pen = new Pen(foreground, 1.4);
        if (glyph == Glyph.Globe)
        {
            dc.DrawEllipse(null, pen, new Point(x, y), 10, 10);
            dc.DrawEllipse(null, pen, new Point(x, y), 4.5, 10);
            dc.DrawLine(pen, new Point(x - 10, y), new Point(x + 10, y));
        }
        else if (glyph == Glyph.Console)
        {
            dc.DrawRoundedRectangle(null, pen, new Rect(x - 7, y - 12, 14, 24), 2, 2);
            dc.DrawLine(pen, new Point(x - 3, y - 7), new Point(x + 3, y - 7));
            dc.DrawEllipse(foreground, null, new Point(x, y + 6), 1.5, 1.5);
        }
        else
        {
            dc.DrawRoundedRectangle(null, pen, new Rect(x - 11, y - 9, 22, 15), 2, 2);
            dc.DrawLine(pen, new Point(x, y + 6), new Point(x, y + 11));
            dc.DrawLine(pen, new Point(x - 7, y + 11), new Point(x + 7, y + 11));
        }
        if (ready && !lost)
        {
            dc.DrawEllipse(Brush("#172C3B"), new Pen(Blue, 1), new Point(x + 18, y - 18), 6, 6);
            dc.DrawGeometry(null, new Pen(Blue, 1.2), Geometry.Parse(FormattableString.Invariant($"M {x + 15},{y - 18} L {x + 17},{y - 16} {x + 21},{y - 20}")));
        }
    }
}

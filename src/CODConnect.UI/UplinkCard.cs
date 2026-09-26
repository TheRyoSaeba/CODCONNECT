using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace CODConnect.UI;

public sealed class UplinkCard : Border
{
    private static readonly Brush Accent = Frozen("#73D9E5"), Muted = Frozen("#A1A0AB"), Text = Frozen("#EAE9EF");
    private readonly Drawing _drawing = new();
    private readonly Canvas _packets = new() { Width = 440, Height = 120, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24) };

    public int Stage { get; private set; }

    public UplinkCard()
    {
        Width = 440; Height = 196;
        CornerRadius = new CornerRadius(14);
        Background = Frozen("#18181C"); BorderBrush = Frozen("#323238"); BorderThickness = new Thickness(1);
        Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Opacity = .6, Color = Colors.Black };
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        RenderTransform = new TranslateTransform();
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        var grid = new Grid();
        grid.Children.Add(_drawing);
        grid.Children.Add(_packets);
        grid.Children.Add(_status);
        Child = grid;
    }

    public void Show(int stage, bool motion)
    {
        if (stage == Stage && Visibility == Visibility.Visible) return;
        Stage = stage;
        _drawing.Stage = stage;
        _drawing.InvalidateVisual();
        _status.Text = stage >= 3 ? "Online through this PC" : "Connected to this PC";
        _status.Foreground = stage >= 3 ? Accent : Muted;
        AutomationProperties.SetName(this, "Console Internet: " + _status.Text);

        _packets.Children.Clear();
        if (stage >= 3)
        {
            foreach (var (from, to) in new[] { (106d, 188d), (246d, 328d) })
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Color = Color.FromRgb(115, 217, 229), Opacity = 1 } };
                Canvas.SetTop(dot, 59);
                var shift = new TranslateTransform(motion ? from : (from + to) / 2, 0);
                dot.RenderTransform = shift;
                if (motion)
                    shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
                _packets.Children.Add(dot);
            }
        }

        if (Visibility == Visibility.Visible) return;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        if (!motion) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
        RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
    }

    public void Hide(bool motion)
    {
        if (Visibility != Visibility.Visible) return;
        Stage = 0;
        if (!motion)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => { if (Stage == 0) { Visibility = Visibility.Collapsed; BeginAnimation(OpacityProperty, null); } };
        BeginAnimation(OpacityProperty, fade);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush;
    }

    private sealed class Drawing : FrameworkElement
    {
        public int Stage { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            var online = Stage >= 3;
            foreach (var (from, to, lit) in new[] { (106d, 194d, true), (246d, 334d, online) })
            {
                var pen = new Pen(Frozen(lit ? "#4E6970" : "#414149"), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (!lit) pen.DashStyle = new DashStyle(new[] { 3d, 5d }, 0);
                dc.DrawLine(pen, new Point(from, 62), new Point(to, 62));
                if (lit) NetworkDiagram.GlowStroke(dc, new LineGeometry(new Point(from, 62), new Point(to, 62)), 2);
            }

            NetworkDiagram.Node(dc, 80, 62, NetworkDiagram.Glyph.Console, true, true);
            NetworkDiagram.Node(dc, 220, 62, NetworkDiagram.Glyph.Pc, true, true);
            NetworkDiagram.Node(dc, 360, 62, NetworkDiagram.Glyph.Globe, true, online);
            foreach (var (label, x) in new[] { ("Your console", 80d), ("This PC", 220d), ("Internet", 360d) })
            {
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(x - text.Width / 2, 94));
            }
        }
    }
}

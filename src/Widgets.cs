using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Islet
{
    public static class Ui
    {
        public static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static readonly Brush White = Frozen(Color.FromRgb(245, 245, 247));
        public static readonly Brush Secondary = Frozen(Color.FromArgb(170, 255, 255, 255));
        public static readonly Brush Tertiary = Frozen(Color.FromArgb(110, 255, 255, 255));
        public static readonly Brush Track = Frozen(Color.FromArgb(60, 255, 255, 255));
        public static readonly Brush Hover = Frozen(Color.FromArgb(40, 255, 255, 255));
        public static readonly Brush Chip = Frozen(Color.FromArgb(30, 255, 255, 255));
        public static readonly Brush ArtPlaceholder = CreatePlaceholder();

        public static readonly Color Orange = Color.FromRgb(255, 159, 10);
        public static readonly Color Green = Color.FromRgb(48, 209, 88);
        public static readonly Color Red = Color.FromRgb(255, 69, 58);
        public static readonly Color Blue = Color.FromRgb(10, 132, 255);
        public static readonly Color Neutral = Color.FromRgb(235, 235, 240);

        public const string GlyphPrevious = "";
        public const string GlyphNext = "";
        public const string GlyphPlay = "";
        public const string GlyphPause = "";
        public const string GlyphStop = "";
        public const string GlyphHome = "";
        public const string GlyphMusic = "";
        public const string GlyphTimer = "";
        public const string GlyphStopwatch = "";
        public const string GlyphAdd = "";
        public const string GlyphCancel = "";
        public const string GlyphReset = "";
        public const string GlyphBolt = "";
        public const string GlyphBluetooth = "";
        public const string GlyphHeadphones = "";
        public const string GlyphMute = "";
        public const string GlyphVolume = "";

        // Speaker glyph that follows the level (Volume0..Volume3), or the muted one.
        public static string VolumeGlyph(double level, bool muted)
        {
            if (muted) return GlyphMute;
            return level <= 0.001 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        }
        public const string GlyphMic = "";
        public const string GlyphCamera = "";

        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Brush CreatePlaceholder()
        {
            var b = new LinearGradientBrush(Color.FromRgb(70, 70, 78), Color.FromRgb(34, 34, 40), 45);
            b.Freeze();
            return b;
        }

        public static TextBlock Label(double size, FontWeight weight, Brush foreground)
        {
            return new TextBlock
            {
                FontFamily = TextFont,
                FontSize = size,
                FontWeight = weight,
                Foreground = foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // Digits that do not jiggle as they change.
        public static TextBlock Digits(double size, FontWeight weight, Brush foreground)
        {
            var t = Label(size, weight, foreground);
            t.FontFamily = DisplayFont;
            System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        public static TextBlock Glyph(string glyph, double size, Brush foreground)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = size,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        public static Border ArtTile(double size, double radius)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = ArtPlaceholder,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        public static void SetArt(Border tile, BitmapSource art)
        {
            tile.Background = art == null
                ? ArtPlaceholder
                : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }

        public static Border IconButton(string glyph, double glyphSize, double box, Action click, out TextBlock glyphBlock)
        {
            glyphBlock = Glyph(glyph, glyphSize, White);
            return Pressable(glyphBlock, box, box, box / 2, Brushes.Transparent, click);
        }

        public static Border ChipButton(string text, Action click)
        {
            var label = Label(13, FontWeights.SemiBold, White);
            label.Text = text;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            return Pressable(label, double.NaN, 30, 15, Chip, click, new Thickness(12, 0, 12, 0));
        }

        public static Border Pressable(UIElement content, double width, double height, double radius, Brush rest, Action click)
        {
            return Pressable(content, width, height, radius, rest, click, new Thickness(0));
        }

        public static Border Pressable(UIElement content, double width, double height, double radius, Brush rest, Action click, Thickness padding)
        {
            var button = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = rest, // non-null so the whole button is hit-testable
                Child = content,
                Padding = padding,
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 4, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var scale = new ScaleTransform(1, 1);
            button.RenderTransform = scale;
            button.MouseEnter += (s, e) => button.Background = Hover;
            button.MouseLeave += (s, e) => { button.Background = rest; scale.ScaleX = scale.ScaleY = 1; };
            button.MouseLeftButtonDown += (s, e) => { scale.ScaleX = scale.ScaleY = 0.9; e.Handled = true; };
            button.MouseLeftButtonUp += (s, e) =>
            {
                scale.ScaleX = scale.ScaleY = 1;
                e.Handled = true;
                click();
            };
            return button;
        }

        public static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }
    }

    // Frequency bars. Rise fast, fall slowly, rest as small dots.
    public sealed class Visualizer : StackPanel
    {
        private readonly Rectangle[] bars;
        private readonly double[] heights;
        private readonly double minHeight, maxHeight;

        public Visualizer(int count, double barWidth, double maxHeight)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            Height = maxHeight;
            this.maxHeight = maxHeight;
            minHeight = barWidth;
            bars = new Rectangle[count];
            heights = new double[count];
            for (int i = 0; i < count; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = barWidth,
                    Height = minHeight,
                    RadiusX = barWidth / 2,
                    RadiusY = barWidth / 2,
                    Fill = Ui.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(i == 0 ? 0 : barWidth * 0.75, 0, 0, 0),
                };
                heights[i] = minHeight;
                Children.Add(bars[i]);
            }
        }

        public int Count { get { return bars.Length; } }

        public Brush Accent
        {
            set { foreach (var bar in bars) bar.Fill = value; }
        }

        // Returns true while any bar is above its resting height.
        public bool Update(double[] levels)
        {
            bool moving = false;
            for (int i = 0; i < bars.Length; i++)
            {
                double level = levels != null && i < levels.Length ? levels[i] : 0;
                double target = minHeight + (maxHeight - minHeight) * level;
                heights[i] += (target - heights[i]) * (target > heights[i] ? 0.6 : 0.18);
                if (Math.Abs(bars[i].Height - heights[i]) > 0.1) bars[i].Height = heights[i];
                if (heights[i] > minHeight + 0.3) moving = true;
            }
            return moving;
        }
    }

    // Horizontal 0..1 slider: drag, click anywhere on the track, or use the wheel.
    // Changed fires only for user input, so external updates never echo back.
    public sealed class VolumeSlider : Grid
    {
        private const double Thumb = 14;
        private readonly Border fill;
        private readonly Ellipse thumb;
        private double value;
        private bool dragging;

        public VolumeSlider(double width)
        {
            Width = width;
            Height = 24;
            Background = Brushes.Transparent; // the whole strip is grabbable
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Track, VerticalAlignment = VerticalAlignment.Center });
            fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            thumb = new Ellipse { Width = Thumb, Height = Thumb, Fill = Ui.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(fill);
            Children.Add(thumb);

            MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                CaptureMouse();
                SetFromPointer(e.GetPosition(this).X);
                e.Handled = true;
            };
            MouseMove += (s, e) => { if (dragging) SetFromPointer(e.GetPosition(this).X); };
            MouseLeftButtonUp += (s, e) =>
            {
                ReleaseMouseCapture();
                e.Handled = true;
            };
            LostMouseCapture += (s, e) => dragging = false;
            MouseWheel += (s, e) =>
            {
                Set(value + (e.Delta > 0 ? 0.02 : -0.02), true);
                e.Handled = true;
            };
            Apply(0);
        }

        public event Action<double> Changed;

        public bool IsDragging { get { return dragging; } }

        // External updates are ignored mid-drag so the thumb never jumps.
        public double Value
        {
            get { return value; }
            set { if (!dragging) Apply(Math.Max(0, Math.Min(1, value))); }
        }

        public Brush Accent { set { fill.Background = value; } }

        public void Nudge(double delta)
        {
            Set(value + delta, true);
        }

        private void SetFromPointer(double x)
        {
            Set((x - Thumb / 2) / (Width - Thumb), true);
        }

        private void Set(double v, bool fromUser)
        {
            v = Math.Max(0, Math.Min(1, v));
            Apply(v);
            var handler = Changed;
            if (fromUser && handler != null) handler(v);
        }

        private void Apply(double v)
        {
            value = v;
            double travel = Width - Thumb;
            fill.Width = Thumb / 2 + travel * v;
            thumb.Margin = new Thickness(travel * v, 0, 0, 0);
        }
    }

    // Rounded battery outline whose fill follows the level.
    public sealed class BatteryIcon : Grid
    {
        private readonly Border fill;
        private readonly double inner;

        public BatteryIcon(double width, double height)
        {
            Width = width + 3;
            Height = height;
            VerticalAlignment = VerticalAlignment.Center;
            inner = width - 4;
            var body = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(height * 0.3),
                BorderBrush = Ui.Secondary,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            fill = new Border { CornerRadius = new CornerRadius(height * 0.18), HorizontalAlignment = HorizontalAlignment.Left };
            body.Child = fill;
            var nub = new Border
            {
                Width = 2,
                Height = height * 0.4,
                CornerRadius = new CornerRadius(0, 1, 1, 0),
                Background = Ui.Secondary,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(body);
            Children.Add(nub);
        }

        public void Set(int percent, Color color)
        {
            fill.Width = Math.Max(1.5, inner * Math.Max(0, Math.Min(100, percent)) / 100.0);
            fill.Background = Ui.Frozen(color);
        }
    }

    // Circular progress (timer bubble), drawn as an arc.
    public sealed class ProgressRing : Grid
    {
        private readonly Path arc;
        private readonly double size, thickness;

        public ProgressRing(double size, double thickness, Color color)
        {
            this.size = size;
            this.thickness = thickness;
            Width = size;
            Height = size;
            Children.Add(new Ellipse { Stroke = Ui.Frozen(Color.FromArgb(60, color.R, color.G, color.B)), StrokeThickness = thickness });
            arc = new Path { Stroke = Ui.Frozen(color), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            Children.Add(arc);
        }

        public void Set(double fraction)
        {
            fraction = Math.Max(0, Math.Min(0.9999, fraction));
            double r = (size - thickness) / 2, c = size / 2;
            double angle = fraction * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = new Point(c, c - r) };
            figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            arc.Data = geometry;
        }
    }
}

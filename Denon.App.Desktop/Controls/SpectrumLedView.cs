using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Denon.App.Desktop.Controls;

/// <summary>Band levels and peak caps shared by the LED view and its bloom layer.</summary>
public sealed class SpectrumState(int bandCount)
{
    public double[] Levels { get; } = new double[bandCount];

    public double[] Peaks { get; } = new double[bandCount];

    public int BandCount => Levels.Length;
}

/// <summary>
/// Segmented LED spectrum (31 bands, 25 Hz–16 kHz). Bars snap to whole LED segments, colored by height
/// (deep blue → cyan → ice → amber → red), over dim "unlit" segments, with floating peak caps, a glass-floor
/// reflection and frequency labels at the true band centers. With <see cref="GlowOnly"/> it draws only the
/// lit segments, for a blurred bloom layer on top.
/// </summary>
public sealed class SpectrumLedView : FrameworkElement
{
    private const double LabelHeight = 12;
    private const double ReflectionHeight = 10;
    private const double MinFrequency = 25.0;
    private const double MaxFrequency = 16000.0;


    private static readonly Typeface LabelTypeface = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static MeterPalette P => MeterPalette.Current;

    private static IReadOnlyList<(double Offset, Color Color)> Palette => P.LedHeat;

    private static Color ScreenColor => P.LedScreen;

    private Size _laidOutFor;
    private double _ppd = 1;
    private double _pitch;
    private double _gap;
    private int _rows;
    private double _baseline;
    private double[] _barX = [];
    private double _barWidth;
    private Brush? _barBrush;
    private Brush? _ghostBrush;
    private Brush? _grilleBrush;
    private Brush? _reflectionMask;
    private Pen? _baselinePen;
    private Brush[] _peakBrushes = [];
    private (FormattedText Text, double X)[] _labels = [];

    public bool GlowOnly { get; set; }

    public SpectrumState? State { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (State is not { } state || ActualWidth < 40 || ActualHeight < 30)
        {
            return;
        }

        EnsureLayout(new Size(ActualWidth, ActualHeight), state.BandCount);
        if (_rows < 3 || _barBrush is null)
        {
            return;
        }

        if (GlowOnly)
        {
            DrawBars(dc, state, includePeaks: true);
            return;
        }

        // Unlit segments, the lit bars and peak caps.
        for (var i = 0; i < state.BandCount; i++)
        {
            dc.DrawRectangle(_ghostBrush, null, new Rect(_barX[i], 0, _barWidth, _baseline));
        }

        DrawBars(dc, state, includePeaks: true);

        // Glass-floor reflection under the baseline.
        var mirrorAxis = _baseline + (_gap / 2) + 1;
        dc.PushClip(new RectangleGeometry(new Rect(0, _baseline + 1, ActualWidth, ReflectionHeight)));
        dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, 2 * mirrorAxis));
        dc.PushOpacityMask(_reflectionMask);
        DrawBars(dc, state, includePeaks: false);
        dc.Pop();
        dc.Pop();
        dc.Pop();

        // LED grille: a fine gap at the bottom of every segment (screen-colored), over bars and reflection.
        dc.DrawRectangle(_grilleBrush, null, new Rect(0, 0, ActualWidth, _baseline + 1 + ReflectionHeight));
        dc.DrawLine(_baselinePen, new Point(0, _baseline + 0.5), new Point(ActualWidth, _baseline + 0.5));

        foreach (var (text, x) in _labels)
        {
            dc.DrawText(text, new Point(x, ActualHeight - LabelHeight + 1));
        }
    }

    private void DrawBars(DrawingContext dc, SpectrumState state, bool includePeaks)
    {
        for (var i = 0; i < state.BandCount; i++)
        {
            var lit = (int)Math.Round(Math.Clamp(state.Levels[i], 0.0, 1.0) * _rows);
            if (lit > 0)
            {
                var top = _baseline - (lit * _pitch);
                dc.DrawRectangle(_barBrush, null, new Rect(_barX[i], top, _barWidth, lit * _pitch));
            }

            if (!includePeaks)
            {
                continue;
            }

            var peakRow = (int)Math.Round(Math.Clamp(state.Peaks[i], 0.0, 1.0) * _rows);
            if (peakRow > lit && peakRow >= 2)
            {
                var row = Math.Min(peakRow, _rows);
                dc.DrawRectangle(_peakBrushes[row - 1], null, new Rect(_barX[i], _baseline - (row * _pitch), _barWidth, _pitch));
            }
        }
    }

    private void EnsureLayout(Size size, int bands)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (size == _laidOutFor && Math.Abs(ppd - _ppd) < 0.001 && _barX.Length == bands)
        {
            return;
        }

        _laidOutFor = size;
        _ppd = ppd;

        // Segment pitch and gap in whole device pixels, so every LED edge is crisp at any DPI.
        var pitchPx = Math.Max(4, Math.Round(4.5 * ppd));
        var gapPx = Math.Max(1, Math.Round(1.1 * ppd));
        _pitch = pitchPx / ppd;
        _gap = gapPx / ppd;

        var barsHeight = size.Height - LabelHeight - ReflectionHeight - 2;
        _rows = Math.Max(0, (int)Math.Floor(barsHeight / _pitch));
        _baseline = _rows * _pitch;

        var slot = size.Width / bands;
        _barWidth = Snap(Math.Max(2, slot * 0.72), ppd);
        _barX = new double[bands];
        for (var i = 0; i < bands; i++)
        {
            _barX[i] = Snap((i * slot) + ((slot - _barWidth) / 2), ppd);
        }

        // Color by height: one absolute gradient over the full bar area.
        var gradient = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, _baseline),
            EndPoint = new Point(0, 0),
        };
        foreach (var (offset, color) in Palette)
        {
            gradient.GradientStops.Add(new GradientStop(color, offset));
        }

        gradient.Freeze();
        _barBrush = gradient;

        var ghost = gradient.Clone();
        ghost.Opacity = 0.075;
        ghost.Freeze();
        _ghostBrush = ghost;

        _peakBrushes = new Brush[Math.Max(1, _rows)];
        for (var r = 1; r <= _rows; r++)
        {
            var c = PaletteAt((double)r / _rows);
            var hot = Color.FromRgb((byte)(c.R + ((255 - c.R) * 0.55)), (byte)(c.G + ((255 - c.G) * 0.55)), (byte)(c.B + ((255 - c.B) * 0.55)));
            var b = new SolidColorBrush(hot);
            b.Freeze();
            _peakBrushes[r - 1] = b;
        }

        // Grille tile: transparent segment, screen-colored gap at its bottom.
        var tile = new DrawingGroup();
        using (var tdc = tile.Open())
        {
            tdc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, 4, _pitch));
            tdc.DrawRectangle(new SolidColorBrush(ScreenColor), null, new Rect(0, _pitch - _gap, 4, _gap));
        }

        tile.Freeze();
        var grille = new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, _pitch),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        grille.Freeze();
        _grilleBrush = grille;

        // Reflection fades out quickly below the floor (mask is in mirrored coordinates).
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, _baseline),
            EndPoint = new Point(0, _baseline - ReflectionHeight),
            GradientStops =
            {
                new GradientStop(WithAlpha(P.Shade, 105), 0),
                new GradientStop(WithAlpha(P.Shade, 0), 1),
            },
        };
        mask.Freeze();
        _reflectionMask = mask;

        var floor = new LinearGradientBrush(WithAlpha(P.LedFloorGlow, 0), WithAlpha(P.LedFloorGlow, 0), 0)
        {
            GradientStops =
            {
                new GradientStop(WithAlpha(P.LedFloorGlow, 0), 0),
                new GradientStop(WithAlpha(P.LedFloorGlow, 70), 0.5),
                new GradientStop(WithAlpha(P.LedFloorGlow, 0), 1),
            },
        };
        floor.Freeze();
        _baselinePen = new Pen(floor, 1);
        _baselinePen.Freeze();

        BuildLabels(size.Width, bands, ppd);
    }

    private void BuildLabels(double width, int bands, double ppd)
    {
        var ratio = Math.Pow(MaxFrequency / MinFrequency, 1.0 / bands);
        var slot = width / bands;
        var brush = new SolidColorBrush(P.LedLabel);
        brush.Freeze();

        (double Hz, string Text)[] marks = [(31.5, "31"), (63, "63"), (125, "125"), (250, "250"), (500, "500"), (1000, "1k"), (2000, "2k"), (4000, "4k"), (8000, "8k"), (16000, "16k")];
        var labels = new List<(FormattedText, double)>();
        foreach (var (hz, label) in marks)
        {
            // Band i is centered at 25 Hz × ratio^(i + 0.5).
            var bandPos = (Math.Log(hz / MinFrequency) / Math.Log(ratio)) - 0.5;
            var centerX = (bandPos + 0.5) * slot;
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 8, brush, ppd);
            var x = Math.Clamp(centerX - (text.Width / 2), 0, width - text.Width);
            labels.Add((text, Snap(x, ppd)));
        }

        _labels = [.. labels];
    }

    private static Color PaletteAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (var i = 1; i < Palette.Count; i++)
        {
            if (t <= Palette[i].Offset)
            {
                var (o0, c0) = Palette[i - 1];
                var (o1, c1) = Palette[i];
                var f = (t - o0) / (o1 - o0);
                return Color.FromRgb(
                    (byte)(c0.R + ((c1.R - c0.R) * f)),
                    (byte)(c0.G + ((c1.G - c0.G) * f)),
                    (byte)(c0.B + ((c1.B - c0.B) * f)));
            }
        }

        return Palette[^1].Color;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static double Snap(double v, double ppd) => Math.Round(v * ppd) / ppd;
}

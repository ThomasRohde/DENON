using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Denon.App.Desktop.Services;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Denon.App.Desktop.Controls;

/// <summary>
/// One backlit analog VU dial, drawn in a fixed 500×132 design space and scaled uniformly to fit.
/// Layers (bottom → top): face, lamp glow (follows the level), scale print, legend, readouts,
/// needle shadow, needle, hood, glass. Only the needle angle, lamp opacity, peak lamp and (at 12 Hz)
/// the readouts change per frame, so a frame costs two transform updates.
/// </summary>
public sealed class VuDial : FrameworkElement
{
    private const double DesignHeight = 132;
    private const double ScaleRadius = 196;
    private const double MinAngle = -50;
    private const double MaxAngle = 50;
    private const double CornerRadius = 11;
    private const double MinDesignWidth = 500;
    private const double MaxDesignWidth = 760;
    private const double PivotY = 238;

    // Skin fonts, read at each draw: a skin switch redraws the dials (Restyle).
    private static FontFamily ScaleFont => ThemeResources.Font("DisplayFont");
    private static FontFamily ReadoutFont => ThemeResources.Font("MonoFont");
    private static Typeface ScaleTypeface => new(ScaleFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static Typeface LightTypeface => new(ScaleFont, FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
    private static Typeface LegendTypeface => new(ScaleFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static Typeface ReadoutTypeface => new(ReadoutFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static Color Red => P.RedZone;
    private static Color RedInk => P.RedZoneInk;

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(VuDial),
        new FrameworkPropertyMetadata(MeterPalette.Current.DefaultAccent, (d, _) => ((VuDial)d).RebuildStatic()));

    public static readonly DependencyProperty ChannelProperty = DependencyProperty.Register(
        nameof(Channel), typeof(string), typeof(VuDial),
        new FrameworkPropertyMetadata("L", (d, _) => ((VuDial)d).RebuildStatic()));

    public static readonly DependencyProperty ChannelNameProperty = DependencyProperty.Register(
        nameof(ChannelName), typeof(string), typeof(VuDial),
        new FrameworkPropertyMetadata("LEFT", (d, _) => ((VuDial)d).RebuildStatic()));

    private readonly ContainerVisual _root = new();
    private readonly DrawingVisual _face = new();
    private readonly DrawingVisual _lamp = new();
    private readonly DrawingVisual _ink = new();
    private readonly DrawingVisual _legend = new();
    private readonly DrawingVisual _readouts = new();
    private readonly DrawingVisual _peakLamp = new();
    private readonly DrawingVisual _needleShadow = new();
    private readonly DrawingVisual _needle = new();
    private readonly DrawingVisual _glass = new();
    private readonly RotateTransform _needleRotation = new(MinAngle, 250, PivotY);
    private readonly RotateTransform _shadowRotation = new(MinAngle, 250, PivotY);
    private double _w = 500;

    private string _sourceText = "IDLE";
    private Color _sourceColor = MeterPalette.Current.IdleSource;
    private double _level;
    private double _peakLevel;
    private long _peakLevelUntil;
    private long _peakLampUntil;
    private long _nextReadoutAt;
    private string _shownLevelText = string.Empty;
    private string _shownPeakText = string.Empty;
    private bool _built;

    public VuDial()
    {
        _root.Children.Add(_face);
        _root.Children.Add(_lamp);
        _root.Children.Add(_ink);
        _root.Children.Add(_legend);
        _root.Children.Add(_readouts);
        _root.Children.Add(_peakLamp);
        _root.Children.Add(_needleShadow);
        _root.Children.Add(_needle);
        _root.Children.Add(_glass);
        _root.Clip = new RectangleGeometry(new Rect(0, 0, _w, DesignHeight), CornerRadius, CornerRadius);

        _needle.Transform = _needleRotation;
        var shadow = new TransformGroup();
        shadow.Children.Add(_shadowRotation);
        shadow.Children.Add(new TranslateTransform(2.6, 2.2));
        _needleShadow.Transform = shadow;
        _peakLamp.Opacity = 0;
        _lamp.Opacity = 0.35;

        AddVisualChild(_root);
        Loaded += (_, _) => RebuildStatic();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RebuildStatic();
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Big channel letter on the face ("L" / "R").</summary>
    public string Channel
    {
        get => (string)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public string ChannelName
    {
        get => (string)GetValue(ChannelNameProperty);
        set => SetValue(ChannelNameProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _root;

    /// <summary>Meter level 0..1 (40 dB range) for this frame.</summary>
    public void SetLevel(double level)
    {
        _level = Math.Clamp(level, 0.0, 1.0);
        var deflection = VuMeterScale.LevelToDeflection(_level);
        var angle = MinAngle + (deflection * (MaxAngle - MinAngle));
        _needleRotation.Angle = angle;
        _shadowRotation.Angle = angle;

        // The lamp breathes with the program: a dim pilot light at rest, full glow at 0 VU.
        _lamp.Opacity = 0.34 + (0.66 * Math.Min(1.0, deflection / 0.72));

        var now = Environment.TickCount64;
        if (VuMeterScale.LevelToVu(_level) >= 1.0 && _level > 0.003)
        {
            _peakLampUntil = now + 260;
        }

        _peakLamp.Opacity = now <= _peakLampUntil ? 1.0 : 0.0;

        if (_level >= _peakLevel || now > _peakLevelUntil)
        {
            _peakLevel = _level;
            _peakLevelUntil = now + 1500;
        }

        if (now >= _nextReadoutAt)
        {
            _nextReadoutAt = now + 80; // ~12 Hz: readable numbers, no flicker.
            DrawReadouts();
        }
    }

    /// <summary>Source print under "VU" (DENON / SIM / PC / MUTED / IDLE).</summary>
    public void SetSource(string text, Color color)
    {
        if (text == _sourceText && color == _sourceColor)
        {
            return;
        }

        _sourceText = text;
        _sourceColor = color;
        DrawLegend();
    }

    /// <summary>Pivot of the needle: centered, below the window (hidden by the hood).</summary>
    private Point Pivot => new(_w / 2, PivotY);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 500 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? DesignHeight : availableSize.Height;
        var (s, designW) = FitDesign(w, h);
        return new Size(designW * s, DesignHeight * s);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (s, designW) = FitDesign(finalSize.Width, finalSize.Height);

        // The face widens to fill the window (the scale stays centered), so wide windows show no dead bars.
        if (Math.Abs(designW - _w) > 0.5)
        {
            _w = designW;
            _needleRotation.CenterX = _shadowRotation.CenterX = _w / 2;
            _root.Clip = new RectangleGeometry(new Rect(0, 0, _w, DesignHeight), CornerRadius, CornerRadius);
            RebuildStatic();
        }

        var x = (finalSize.Width - (_w * s)) / 2;
        var y = (finalSize.Height - (DesignHeight * s)) / 2;
        _root.Transform = new MatrixTransform(s, 0, 0, s, x, y);
        return finalSize;
    }

    /// <summary>Scale by height; the design width follows the available aspect within limits.</summary>
    private static (double Scale, double DesignWidth) FitDesign(double width, double height)
    {
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            return (1, 500);
        }

        var s = height / DesignHeight;
        var designW = width / s;
        if (designW < MinDesignWidth)
        {
            s = width / MinDesignWidth;
            designW = MinDesignWidth;
        }

        return (s, Math.Min(designW, MaxDesignWidth));
    }

    /// <summary>Redraws every layer with the current skin's fonts and meter materials.</summary>
    public void Restyle() => RebuildStatic();

    // ---------------------------------------------------------------- static layers

    private void RebuildStatic()
    {
        if (!IsLoaded)
        {
            return;
        }

        _built = true;
        DrawFace();
        DrawLamp();
        DrawInk();
        DrawLegend();
        DrawNeedle();
        DrawGlass();
        DrawPeakLamp();
        _shownLevelText = _shownPeakText = string.Empty;
        DrawReadouts();
    }

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private static MeterPalette P => MeterPalette.Current;

    private Color Ink => Blend(P.PaperInk, Accent, 0.10);

    private void DrawFace()
    {
        using var dc = _face.RenderOpen();
        var face = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(P.FaceTop, 0.0),
                new GradientStop(Blend(P.FaceMiddle, Accent, 0.05 * P.FaceAccentTint), 0.55),
                new GradientStop(Blend(P.FaceBottom, Accent, 0.16 * P.FaceAccentTint), 1.0),
            },
        };
        face.Freeze();
        dc.DrawRoundedRectangle(face, null, new Rect(0, 0, _w, DesignHeight), CornerRadius, CornerRadius);

        // Fine engine-turned texture: faint concentric rings around the pivot, like a printed meter card.
        var ring = new Pen(new SolidColorBrush(WithAlpha(P.Glint, 9)), 0.6);
        ring.Freeze();
        for (var r = 60.0; r < 330; r += 7)
        {
            dc.DrawEllipse(null, ring, Pivot, r, r);
        }
    }

    private void DrawLamp()
    {
        using var dc = _lamp.RenderOpen();
        var lamp = P.Backlight ?? Accent;
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(_w / 2, DesignHeight + 6),
            GradientOrigin = new Point(_w / 2, DesignHeight + 6),
            RadiusX = _w * 0.52,
            RadiusY = DesignHeight * 1.05,
            GradientStops =
            {
                new GradientStop(WithAlpha(lamp, P.BacklightAlpha), 0.0),
                new GradientStop(WithAlpha(lamp, (byte)(P.BacklightAlpha * 46 / 120)), 0.45),
                new GradientStop(WithAlpha(lamp, 0), 1.0),
            },
        };
        glow.Freeze();
        dc.DrawRectangle(glow, null, new Rect(0, 0, _w, DesignHeight));
    }

    private void DrawInk()
    {
        using var dc = _ink.RenderOpen();
        var ink = Ink;
        var inkBrush = Frozen(new SolidColorBrush(ink));
        var dimInk = Frozen(new SolidColorBrush(WithAlpha(ink, 92)));
        var redBrush = Frozen(new SolidColorBrush(RedInk));
        var ppd = PixelsPerDip;

        // Red zone band (0..+3 VU), a little thicker toward +3.
        var zeroAngle = AngleFor(VuMeterScale.VuToDeflection(0));
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            g.BeginFigure(PointAt(zeroAngle, ScaleRadius), true, true);
            g.ArcTo(PointAt(MaxAngle, ScaleRadius), new Size(ScaleRadius, ScaleRadius), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(PointAt(MaxAngle, ScaleRadius + 7.5), true, false);
            g.ArcTo(PointAt(zeroAngle, ScaleRadius + 4), new Size(ScaleRadius + 6, ScaleRadius + 6), 0, false, SweepDirection.Counterclockwise, true, false);
        }

        band.Freeze();
        var redFill = new LinearGradientBrush(WithAlpha(Red, 150), Red, new Point(0, 0.5), new Point(1, 0.5));
        redFill.Freeze();
        dc.DrawGeometry(redFill, null, band);

        // Main scale line from -20 VU to 0 VU, plus a hairline continuation under the red band.
        var minus20 = AngleFor(VuMeterScale.VuToDeflection(-20));
        dc.DrawGeometry(null, Frozen(new Pen(inkBrush, 1.3)), Arc(minus20, zeroAngle, ScaleRadius));
        dc.DrawGeometry(null, Frozen(new Pen(redBrush, 1.0)), Arc(zeroAngle, MaxAngle, ScaleRadius));

        // VU ticks: major (labelled) and minor.
        (double Vu, bool Major, string Label)[] marks =
        [
            (-20, true, "20"), (-10, true, "10"), (-7, true, "7"), (-6, false, ""), (-5, true, "5"), (-4, false, ""),
            (-3, true, "3"), (-2, true, "2"), (-1, true, "1"), (0, true, "0"), (0.5, false, ""), (1, true, "1"),
            (1.5, false, ""), (2, true, "2"), (2.5, false, ""), (3, true, "3"),
        ];

        foreach (var (vu, major, label) in marks)
        {
            var a = AngleFor(VuMeterScale.VuToDeflection(vu));
            var isRed = vu >= 0;
            var pen = Frozen(new Pen(isRed ? redBrush : inkBrush, major ? 1.6 : 1.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            dc.DrawLine(pen, PointAt(a, ScaleRadius - 1.5), PointAt(a, ScaleRadius + (major ? 10 : 5.5)));

            if (major)
            {
                var text = Text(label, ScaleTypeface, vu == 0 ? 12.5 : 11, isRed ? redBrush : inkBrush, ppd);
                var p = PointAt(a, ScaleRadius + 20.5);
                dc.DrawText(text, new Point(p.X - (text.Width / 2), p.Y - (text.Height / 2)));
            }
        }

        // Inner percent-modulation scale (0..100 % = rest..0 VU), dim.
        const double innerR = ScaleRadius - 15;
        var hundred = VuMeterScale.VuToDeflection(0);
        dc.DrawGeometry(null, Frozen(new Pen(dimInk, 0.8)), Arc(MinAngle, AngleFor(hundred), innerR));
        foreach (var pct in new[] { 0, 20, 40, 60, 80, 100 })
        {
            var a = AngleFor(hundred * pct / 100.0);
            dc.DrawLine(Frozen(new Pen(dimInk, 0.9)), PointAt(a, innerR), PointAt(a, innerR - 4));
            if (pct is 20 or 40 or 60 or 80 or 100)
            {
                var t = Text(pct.ToString(CultureInfo.InvariantCulture), ScaleTypeface, 7.5, dimInk, ppd);
                var p = PointAt(a, innerR - 10);
                dc.DrawText(t, new Point(p.X - (t.Width / 2), p.Y - (t.Height / 2)));
            }
        }

        var pctSign = Text("%", ScaleTypeface, 7.5, dimInk, ppd);
        var ps = PointAt(AngleFor(hundred) + 7, innerR - 11);
        dc.DrawText(pctSign, new Point(ps.X - (pctSign.Width / 2), ps.Y - (pctSign.Height / 2)));

        // Channel letter and name, top corner (outer side of the pair).
        var accentBrush = Frozen(new SolidColorBrush(Accent));
        var letter = Text(Channel, LightTypeface, 30, accentBrush, ppd);
        var name = Text($"{ChannelName} CHANNEL", ScaleTypeface, 7, Frozen(new SolidColorBrush(WithAlpha(ink, 120))), ppd);
        name.SetFontWeight(FontWeights.Bold);
        var isRight = Channel == "R";
        var lx = isRight ? _w - 18 - letter.Width : 18;
        dc.DrawText(letter, new Point(lx, 4));
        dc.DrawText(name, new Point(isRight ? _w - 18 - name.Width : 19, 42));

        // Unlit peak lamp and its caption (the lit lamp is its own layer).
        var lampCenter = PeakLampCenter;
        var unlit = new RadialGradientBrush(P.PeakLampUnlitCenter, P.PeakLampUnlitEdge);
        unlit.Freeze();
        dc.DrawEllipse(unlit, Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(P.Glint, 70))), 0.6)), lampCenter, 4.8, 4.8);
        var caption = Text("PEAK", ScaleTypeface, 7, Frozen(new SolidColorBrush(WithAlpha(ink, 110))), ppd);
        caption.SetFontWeight(FontWeights.Bold);
        dc.DrawText(caption, new Point(isRight ? lampCenter.X + 9 : lampCenter.X - 9 - caption.Width, lampCenter.Y - (caption.Height / 2)));
    }

    private void DrawLegend()
    {
        if (!_built)
        {
            return;
        }

        using var dc = _legend.RenderOpen();
        var ppd = PixelsPerDip;

        // "VU" with a soft halo (the text drawn twice: a wide translucent pass, then the crisp one).
        var vuHalo = Text("VU", LegendTypeface, 25, Frozen(new SolidColorBrush(WithAlpha(Accent, 60))), ppd);
        var vu = Text("VU", LegendTypeface, 24, Frozen(new SolidColorBrush(Accent)), ppd);
        var cx = _w / 2;
        dc.DrawText(vuHalo, new Point(cx - (vuHalo.Width / 2), 77.5));
        dc.DrawText(vu, new Point(cx - (vu.Width / 2), 78));

        // Source print as a small lit tag under "VU".
        var tag = Text(_sourceText, ScaleTypeface, 7.5, Frozen(new SolidColorBrush(_sourceColor)), ppd);
        tag.SetFontWeight(FontWeights.Bold);
        var tagRect = new Rect(cx - (tag.Width / 2) - 6, 106.5, tag.Width + 12, tag.Height + 2);
        dc.DrawRoundedRectangle(
            Frozen(new SolidColorBrush(WithAlpha(_sourceColor, 30))),
            Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(_sourceColor, 90))), 0.7)),
            tagRect, 4, 4);
        dc.DrawText(tag, new Point(cx - (tag.Width / 2), 107.5));
    }

    private void DrawReadouts()
    {
        if (!_built)
        {
            return;
        }

        var levelText = VuMeterScale.FormatDbfs(_level);
        var peakText = VuMeterScale.FormatDbfs(_peakLevel);
        if (levelText == _shownLevelText && peakText == _shownPeakText)
        {
            return;
        }

        _shownLevelText = levelText;
        _shownPeakText = peakText;
        var ppd = PixelsPerDip;
        var ink = Ink;
        var isRight = Channel == "R";

        using var dc = _readouts.RenderOpen();
        var value = Text(levelText, ReadoutTypeface, 15, Frozen(new SolidColorBrush(ink)), ppd);
        var sub = Text($"dBFS · PK {peakText}", ReadoutTypeface, 7.5, Frozen(new SolidColorBrush(WithAlpha(ink, 125))), ppd);

        // Lower outer corner, below the channel letter's side; right-aligned on the right dial.
        dc.DrawText(value, new Point(isRight ? _w - 18 - value.Width : 18, 90));
        dc.DrawText(sub, new Point(isRight ? _w - 18 - sub.Width : 18.5, 110));
    }

    /// <summary>Peak lamp: inner top corner, so the pair reads L … PEAK | PEAK … R.</summary>
    private Point PeakLampCenter => new(Channel == "R" ? 30 : _w - 30, 22);

    private void DrawPeakLamp()
    {
        var center = PeakLampCenter;
        using var lit = _peakLamp.RenderOpen();
        var halo = new RadialGradientBrush(WithAlpha(Red, 150), WithAlpha(Red, 0));
        halo.Freeze();
        lit.DrawEllipse(halo, null, center, 15, 15);
        var core = new RadialGradientBrush(P.PeakLampCore, Red)
        {
            GradientOrigin = new Point(0.38, 0.35),
        };
        core.Freeze();
        lit.DrawEllipse(core, null, center, 4.6, 4.6);
    }

    private void DrawNeedle()
    {
        var tip = ScaleRadius + 9;
        const double baseR = 96;

        var needle = NeedleGeometry(baseR, tip, 3.4, 0.9);
        var glow = NeedleGeometry(baseR, tip + 2, 8.5, 3.2);

        using (var dc = _needle.RenderOpen())
        {
            if (P.NeedleGlowAlpha > 0)
            {
                dc.DrawGeometry(Frozen(new SolidColorBrush(WithAlpha(Accent, P.NeedleGlowAlpha))), null, glow);
            }

            var body = new LinearGradientBrush(P.NeedleTip, Blend(P.NeedleBase, Accent, 0.25), new Point(0.5, 0), new Point(0.5, 1));
            body.Freeze();
            dc.DrawGeometry(body, null, needle);
        }

        using (var dc = _needleShadow.RenderOpen())
        {
            dc.DrawGeometry(Frozen(new SolidColorBrush(WithAlpha(P.Shade, 120))), null, NeedleGeometry(baseR, tip, 4.2, 1.6));
        }
    }

    private void DrawGlass()
    {
        using var dc = _glass.RenderOpen();

        // Hood over the pivot: a dark arched cowl with a fine lit edge.
        const double hoodR = 116;
        var hood = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = Pivot,
            GradientOrigin = Pivot,
            RadiusX = hoodR,
            RadiusY = hoodR,
            GradientStops =
            {
                new GradientStop(P.HoodCenter, 0.0),
                new GradientStop(P.HoodMiddle, 0.93),
                new GradientStop(P.HoodEdge, 1.0),
            },
        };
        hood.Freeze();
        dc.DrawEllipse(hood, null, Pivot, hoodR, hoodR);
        dc.DrawGeometry(null, Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(Accent, 70))), 0.8)), Arc(-31, 31, hoodR));

        // Bezel depth: darker inner edge along the top.
        var bezel = new LinearGradientBrush(WithAlpha(P.Shade, 150), WithAlpha(P.Shade, 0), new Point(0, 0), new Point(0, 0.14));
        bezel.Freeze();
        dc.DrawRectangle(bezel, null, new Rect(0, 0, _w, DesignHeight));

        // Vignette toward the corners.
        var vignette = new RadialGradientBrush
        {
            RadiusX = 0.72,
            RadiusY = 1.05,
            GradientStops =
            {
                new GradientStop(WithAlpha(P.Shade, 0), 0.55),
                new GradientStop(WithAlpha(P.Shade, 110), 1.0),
            },
        };
        vignette.Freeze();
        dc.DrawRectangle(vignette, null, new Rect(0, 0, _w, DesignHeight));

        // Glass reflection: a soft curved sheen across the upper third.
        var sheen = new StreamGeometry();
        using (var g = sheen.Open())
        {
            g.BeginFigure(new Point(0, 0), true, true);
            g.LineTo(new Point(_w, 0), false, false);
            g.LineTo(new Point(_w, 30), false, false);
            g.QuadraticBezierTo(new Point(_w * 0.5, 62), new Point(0, 44), false, false);
        }

        sheen.Freeze();
        var sheenBrush = new LinearGradientBrush(WithAlpha(P.Glint, 24), WithAlpha(P.Glint, 2), new Point(0, 0), new Point(0.15, 1));
        sheenBrush.Freeze();
        dc.DrawGeometry(sheenBrush, null, sheen);

        // Crisp glass edge.
        dc.DrawRoundedRectangle(null, Frozen(new Pen(Frozen(new SolidColorBrush(WithAlpha(P.Glint, 26))), 1)),
            new Rect(0.5, 0.5, _w - 1, DesignHeight - 1), CornerRadius - 0.5, CornerRadius - 0.5);
    }

    // ---------------------------------------------------------------- geometry helpers

    private static double AngleFor(double deflection) => MinAngle + (deflection * (MaxAngle - MinAngle));

    private Point PointAt(double angleDeg, double radius)
    {
        var a = angleDeg * Math.PI / 180.0;
        return new Point(Pivot.X + (radius * Math.Sin(a)), Pivot.Y - (radius * Math.Cos(a)));
    }

    private Geometry Arc(double fromDeg, double toDeg, double radius)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(fromDeg, radius), false, false);
            ctx.ArcTo(PointAt(toDeg, radius), new Size(radius, radius), 0, Math.Abs(toDeg - fromDeg) > 180, SweepDirection.Clockwise, true, false);
        }

        g.Freeze();
        return g;
    }

    /// <summary>Tapered needle pointing straight up (angle 0); the rotate transform turns it.</summary>
    private Geometry NeedleGeometry(double baseRadius, double tipRadius, double baseWidth, double tipWidth)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(Pivot.X - (baseWidth / 2), Pivot.Y - baseRadius), true, true);
            ctx.LineTo(new Point(Pivot.X - (tipWidth / 2), Pivot.Y - tipRadius), true, false);
            ctx.LineTo(new Point(Pivot.X + (tipWidth / 2), Pivot.Y - tipRadius), true, false);
            ctx.LineTo(new Point(Pivot.X + (baseWidth / 2), Pivot.Y - baseRadius), true, false);
        }

        g.Freeze();
        return g;
    }

    private static FormattedText Text(string text, Typeface typeface, double size, Brush brush, double ppd) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, ppd);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)),
        (byte)(a.G + ((b.G - a.G) * t)),
        (byte)(a.B + ((b.B - a.B) * t)));
}

using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Denon.App.Desktop.Controls;

/// <summary>
/// Material colors of the drawn meters (VU dials and the LED spectrum): dial face, printed ink, needle, glass and
/// LEDs. These describe physical parts, so they are not theme tokens. Each skin has two sets, like two models of a
/// real meter: dark faces for dark themes and light faces (cream dials, a light LED screen) for light themes
/// (docs/theming.md). Alpha values for shading stay in the drawing code; <see cref="Shade"/> and
/// <see cref="Glint"/> are their bases.
/// </summary>
public sealed record MeterPalette
{
    /// <summary>The Default skin's meters: dark faces, amber and cyan channels, a cyan-to-red LED screen.</summary>
    public static MeterPalette Default { get; } = new();

    /// <summary>
    /// The Hi-Fi skin's meters: amber-backlit dials (both channels amber, warm print) and a fluorescent-display
    /// spectrum (teal-green segments, amber and red at the top).
    /// </summary>
    public static MeterPalette HiFi { get; } = new()
    {
        DefaultAccent = Color.FromRgb(255, 170, 64),
        LeftAccent = Color.FromRgb(255, 170, 64),
        RightAccent = Color.FromRgb(255, 170, 64),
        FaceTop = Color.FromRgb(38, 23, 9),
        FaceMiddle = Color.FromRgb(29, 17, 6),
        FaceBottom = Color.FromRgb(21, 12, 4),
        PaperInk = Color.FromRgb(255, 222, 168),
        NeedleTip = Color.FromRgb(255, 246, 228),
        NeedleBase = Color.FromRgb(255, 232, 196),
        HoodCenter = Color.FromRgb(8, 5, 2),
        HoodMiddle = Color.FromRgb(18, 11, 4),
        HoodEdge = Color.FromRgb(44, 28, 12),
        LedScreen = Color.FromRgb(3, 9, 8),
        LedScreenEdge = Color.FromRgb(14, 34, 30),
        LedLabel = Color.FromRgb(64, 150, 128),
        LedFloorGlow = Color.FromRgb(64, 240, 200),
        LedHeat =
        [
            (0.00, Color.FromRgb(12, 118, 98)),
            (0.40, Color.FromRgb(34, 200, 168)),
            (0.64, Color.FromRgb(96, 248, 212)),
            (0.78, Color.FromRgb(160, 255, 228)),
            (0.90, Color.FromRgb(255, 186, 72)),
            (1.00, Color.FromRgb(255, 92, 60)),
        ],
    };

    /// <summary>
    /// The Default skin's meters in light themes: cream dials with black print and needles, a warm lamp, and a light
    /// LED screen with deep blue to red segments.
    /// </summary>
    public static MeterPalette DefaultLight { get; } = new()
    {
        DefaultAccent = Color.FromRgb(160, 72, 8),
        LeftAccent = Color.FromRgb(160, 72, 8),
        RightAccent = Color.FromRgb(14, 116, 144),
        SourceDenon = Color.FromRgb(4, 120, 87),
        SourcePc = Color.FromRgb(160, 72, 8),
        SourceMuted = Color.FromRgb(185, 28, 28),
        SourceSimulated = Color.FromRgb(71, 85, 105),
        SourceIdle = Color.FromRgb(87, 83, 78),
        IdleSource = Color.FromRgb(87, 83, 78),
        FaceTop = Color.FromRgb(251, 247, 236),
        FaceMiddle = Color.FromRgb(245, 238, 220),
        FaceBottom = Color.FromRgb(236, 226, 202),
        PaperInk = Color.FromRgb(36, 31, 26),
        RedZone = Color.FromRgb(220, 38, 38),
        RedZoneInk = Color.FromRgb(185, 28, 28),
        PeakLampUnlitCenter = Color.FromRgb(150, 70, 66),
        PeakLampUnlitEdge = Color.FromRgb(110, 46, 44),
        NeedleTip = Color.FromRgb(24, 22, 20),
        NeedleBase = Color.FromRgb(58, 50, 42),
        NeedleGlowAlpha = 0,
        FaceAccentTint = 0,
        HoodCenter = Color.FromRgb(28, 25, 22),
        HoodMiddle = Color.FromRgb(42, 37, 32),
        HoodEdge = Color.FromRgb(78, 68, 58),
        Backlight = Color.FromRgb(255, 206, 128),
        BacklightAlpha = 70,
        Shade = Color.FromRgb(120, 96, 64),
        LedScreen = Color.FromRgb(232, 236, 241),
        LedScreenEdge = Color.FromRgb(196, 204, 214),
        LedLabel = Color.FromRgb(84, 96, 112),
        LedFloorGlow = Color.FromRgb(3, 105, 161),
        LedGhostOpacity = 0.11,
        LedBloom = 0.18,
        PeakMix = Colors.Black,
        PeakMixAmount = 0.3,
        LedHeat =
        [
            (0.00, Color.FromRgb(30, 64, 175)),
            (0.40, Color.FromRgb(3, 105, 161)),
            (0.64, Color.FromRgb(8, 145, 178)),
            (0.78, Color.FromRgb(217, 119, 6)),
            (0.90, Color.FromRgb(234, 88, 12)),
            (1.00, Color.FromRgb(220, 38, 38)),
        ],
    };

    /// <summary>
    /// The Hi-Fi skin's meters in light themes: ivory dials with an amber lamp and brown print, and a pale LCD-style
    /// spectrum with deep teal segments.
    /// </summary>
    public static MeterPalette HiFiLight { get; } = DefaultLight with
    {
        DefaultAccent = Color.FromRgb(161, 84, 6),
        LeftAccent = Color.FromRgb(161, 84, 6),
        RightAccent = Color.FromRgb(161, 84, 6),
        FaceTop = Color.FromRgb(253, 244, 222),
        FaceMiddle = Color.FromRgb(248, 234, 202),
        FaceBottom = Color.FromRgb(240, 220, 178),
        PaperInk = Color.FromRgb(58, 38, 18),
        Backlight = Color.FromRgb(255, 178, 80),
        BacklightAlpha = 80,
        LedScreen = Color.FromRgb(224, 232, 226),
        LedScreenEdge = Color.FromRgb(184, 200, 190),
        LedLabel = Color.FromRgb(56, 98, 86),
        LedFloorGlow = Color.FromRgb(13, 110, 92),
        LedHeat =
        [
            (0.00, Color.FromRgb(10, 92, 78)),
            (0.40, Color.FromRgb(13, 118, 100)),
            (0.64, Color.FromRgb(15, 140, 118)),
            (0.78, Color.FromRgb(22, 160, 135)),
            (0.90, Color.FromRgb(217, 119, 6)),
            (1.00, Color.FromRgb(220, 38, 38)),
        ],
    };

    /// <summary>The palette the meters use now: the skin's set for the theme (<c>ThemeManager</c> sets it).</summary>
    public static MeterPalette Current { get; set; } = Default;

    // VU dial
    public Color DefaultAccent { get; init; } = Color.FromRgb(245, 158, 11);
    public Color LeftAccent { get; init; } = Color.FromRgb(245, 158, 11);
    public Color RightAccent { get; init; } = Color.FromRgb(34, 211, 238);

    // The source printed under "VU" (what the needles show).
    public Color SourceDenon { get; init; } = Color.FromRgb(52, 211, 153);
    public Color SourcePc { get; init; } = Color.FromRgb(251, 191, 36);
    public Color SourceMuted { get; init; } = Color.FromRgb(248, 113, 113);
    public Color SourceSimulated { get; init; } = Color.FromRgb(142, 159, 179);
    public Color SourceIdle { get; init; } = Color.FromRgb(100, 116, 139);
    public Color IdleSource { get; init; } = Color.FromRgb(100, 116, 139);
    public Color FaceTop { get; init; } = Color.FromRgb(13, 16, 23);
    public Color FaceMiddle { get; init; } = Color.FromRgb(10, 12, 17);
    public Color FaceBottom { get; init; } = Color.FromRgb(8, 9, 13);
    public Color PaperInk { get; init; } = Color.FromRgb(241, 236, 226);
    public Color RedZone { get; init; } = Color.FromRgb(239, 68, 68);
    public Color RedZoneInk { get; init; } = Color.FromRgb(252, 128, 128);
    public Color PeakLampCore { get; init; } = Color.FromRgb(255, 214, 214);
    public Color PeakLampUnlitCenter { get; init; } = Color.FromRgb(74, 28, 30);
    public Color PeakLampUnlitEdge { get; init; } = Color.FromRgb(36, 14, 16);
    public Color NeedleTip { get; init; } = Color.FromRgb(255, 250, 240);
    public Color NeedleBase { get; init; } = Color.FromRgb(255, 244, 222);
    public Color HoodCenter { get; init; } = Color.FromRgb(6, 7, 10);
    public Color HoodMiddle { get; init; } = Color.FromRgb(13, 15, 21);
    public Color HoodEdge { get; init; } = Color.FromRgb(28, 32, 43);

    /// <summary>The lamp behind the dial; null lights it in the channel's accent.</summary>
    public Color? Backlight { get; init; }

    /// <summary>Lamp strength at full glow (alpha at the lamp's center).</summary>
    public byte BacklightAlpha { get; init; } = 120;

    /// <summary>How much the face takes the channel's accent (1 = a tinted lamp; 0 = both dials the same paper).</summary>
    public double FaceAccentTint { get; init; } = 1;

    /// <summary>Glow around the needle (alpha of the accent); 0 for a plain printed needle.</summary>
    public byte NeedleGlowAlpha { get; init; } = 70;

    /// <summary>Base of shadows, bezel and vignette (used with alpha).</summary>
    public Color Shade { get; init; } = Colors.Black;

    /// <summary>Base of glass sheen, rings and edges (used with alpha).</summary>
    public Color Glint { get; init; } = Colors.White;

    // LED spectrum
    public Color LedScreen { get; init; } = Color.FromRgb(5, 7, 11);
    public Color LedScreenEdge { get; init; } = Color.FromRgb(13, 22, 34);
    public Color LedLabel { get; init; } = Color.FromRgb(91, 107, 130);
    public Color LedFloorGlow { get; init; } = Color.FromRgb(34, 211, 238);

    /// <summary>How much the unlit segments show.</summary>
    public double LedGhostOpacity { get; init; } = 0.075;

    /// <summary>Opacity of the blurred bloom over the lit segments.</summary>
    public double LedBloom { get; init; } = 0.5;

    /// <summary>Peak caps are the segment color mixed toward this color (lighter on a dark screen, darker on a light one).</summary>
    public Color PeakMix { get; init; } = Colors.White;

    public double PeakMixAmount { get; init; } = 0.55;

    /// <summary>LED color by bar height (0 = bottom, 1 = top).</summary>
    public IReadOnlyList<(double Offset, Color Color)> LedHeat { get; init; } =
    [
        (0.00, Color.FromRgb(3, 105, 161)),
        (0.40, Color.FromRgb(6, 182, 212)),
        (0.64, Color.FromRgb(103, 232, 249)),
        (0.78, Color.FromRgb(251, 191, 36)),
        (0.90, Color.FromRgb(249, 115, 22)),
        (1.00, Color.FromRgb(239, 68, 68)),
    ];
}

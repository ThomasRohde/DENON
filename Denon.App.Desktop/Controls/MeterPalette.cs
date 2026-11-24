using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Denon.App.Desktop.Controls;

/// <summary>
/// Material colors of the drawn meters (VU dials and the LED spectrum): dial face, printed ink, needle, glass and
/// LEDs. These describe physical parts, so they are not theme tokens: the dials look the same in every theme, like
/// the display of a real receiver. Each skin has its palette (docs/theming.md).
/// Alpha values for shading stay in the drawing code; <see cref="Shade"/> and <see cref="Glint"/> are their bases.
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

    /// <summary>The palette the meters use now (the skin's; <c>ThemeManager.ApplySkin</c> sets it).</summary>
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

    /// <summary>Base of shadows, bezel and vignette (used with alpha).</summary>
    public Color Shade { get; init; } = Colors.Black;

    /// <summary>Base of glass sheen, rings and edges (used with alpha).</summary>
    public Color Glint { get; init; } = Colors.White;

    // LED spectrum
    public Color LedScreen { get; init; } = Color.FromRgb(5, 7, 11);
    public Color LedScreenEdge { get; init; } = Color.FromRgb(13, 22, 34);
    public Color LedLabel { get; init; } = Color.FromRgb(91, 107, 130);
    public Color LedFloorGlow { get; init; } = Color.FromRgb(34, 211, 238);

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

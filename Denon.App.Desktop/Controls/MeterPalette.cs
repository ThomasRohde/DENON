using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Denon.App.Desktop.Controls;

/// <summary>
/// Material colors of the drawn meters (VU dials and the LED spectrum): dial face, printed ink, needle, glass and
/// LEDs. These describe physical parts, so they are not app theme tokens; a future skin can supply another palette.
/// Alpha values for shading stay in the drawing code; <see cref="Shade"/> and <see cref="Glint"/> are their bases.
/// </summary>
public sealed record MeterPalette
{
    /// <summary>The palette the meters use now.</summary>
    public static MeterPalette Current { get; set; } = new();

    // VU dial
    public Color DefaultAccent { get; init; } = Color.FromRgb(245, 158, 11);
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

using System.Windows.Media;
using Denon.App.Desktop.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Keeps content colors (Help topic badges, scene accent badges, station monograms, plugin badges) readable in
/// both Dark and Light themes using WCAG 2 relative luminance contrast math.
/// </summary>
public static class BadgeContrastHelper
{
    public const double DefaultTextContrastFloor = 4.5;

    /// <summary>WCAG 2 relative luminance in [0, 1] for an sRGB color.</summary>
    public static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
    }

    /// <summary>WCAG 2 contrast ratio in [1, 21] between two opaque colors.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Picks whichever of <paramref name="optionA"/> or <paramref name="optionB"/> gives higher contrast on <paramref name="background"/>.</summary>
    public static Color PickForeground(Color background, Color optionA, Color optionB) =>
        ContrastRatio(background, optionA) >= ContrastRatio(background, optionB) ? optionA : optionB;

    /// <summary>
    /// The better of the two theme inks on <paramref name="background"/>; when neither reaches
    /// <see cref="DefaultTextContrastFloor"/> (a midtone badge such as #777777), pure black or white instead.
    /// </summary>
    public static Color PickReadableForeground(Color background, Color themeInkA, Color themeInkB)
    {
        var chosen = PickForeground(background, themeInkA, themeInkB);
        return ContrastRatio(background, chosen) >= DefaultTextContrastFloor
            ? chosen
            : PickForeground(background, Colors.Black, Colors.White);
    }

    /// <summary>
    /// Adjusts <paramref name="accent"/> along its hue (darkening on light surfaces, lightening on dark surfaces)
    /// just enough to reach <paramref name="minContrast"/> against <paramref name="surface"/>.
    /// Returns <paramref name="accent"/> unchanged when it already meets the contrast floor.
    /// </summary>
    public static Color EnsureReadableOnSurface(Color accent, Color surface, double minContrast = DefaultTextContrastFloor)
    {
        if (surface.A == 0 || ContrastRatio(accent, surface) >= minContrast)
        {
            return accent;
        }

        var target = RelativeLuminance(surface) > 0.5 ? Colors.Black : Colors.White;
        var lo = 0.0;
        var hi = 1.0;
        var best = target;

        for (var i = 0; i < 16; i++)
        {
            var mid = (lo + hi) * 0.5;
            var candidate = Blend(accent, target, mid);
            if (ContrastRatio(candidate, surface) >= minContrast)
            {
                best = candidate;
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return best;
    }

    /// <summary>Frozen foreground brush (theme's light or dark ink) with highest contrast on a solid content-colored badge.</summary>
    public static Brush PickThemeForegroundBrush(Color background)
    {
        var optA = ThemeResources.Color("TextPrimary");
        var optB = ThemeResources.Color("TextOnAccent");
        if (optA.A == 0 || optB.A == 0)
        {
            optA = Colors.Black;
            optB = Colors.White;
        }

        var brush = new SolidColorBrush(PickReadableForeground(background, optA, optB));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Frozen accent brush readable on the current theme's badge surfaces (<c>Ink02</c> / <c>Ink03</c>).
    /// Keeps bright content colors untouched on Dark surfaces and deepens them on Light surfaces.
    /// </summary>
    public static Brush CreateReadableAccentBrush(Color accent, double minContrast = DefaultTextContrastFloor)
    {
        var sidebar = ThemeResources.Color("Ink02");
        var panel = ThemeResources.Color("Ink03");
        if (sidebar.A == 0 || panel.A == 0)
        {
            var raw = new SolidColorBrush(accent);
            raw.Freeze();
            return raw;
        }

        // On Light, Ink02 is slightly darker than Ink03 so meeting contrast on Ink02 also meets Ink03 & Ink04.
        // On Dark, Ink03 is slightly lighter than Ink02 so meeting contrast on Ink03 also meets Ink02 & Ink01.
        var stricterSurface = RelativeLuminance(sidebar) > 0.5 ? sidebar : panel;
        var readable = EnsureReadableOnSurface(accent, stricterSurface, minContrast);
        var brush = new SolidColorBrush(readable);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double t)
    {
        var r = (byte)Math.Clamp(Math.Round(from.R + ((to.R - from.R) * t)), 0, 255);
        var g = (byte)Math.Clamp(Math.Round(from.G + ((to.G - from.G) * t)), 0, 255);
        var b = (byte)Math.Clamp(Math.Round(from.B + ((to.B - from.B) * t)), 0, 255);
        return Color.FromRgb(r, g, b);
    }
}

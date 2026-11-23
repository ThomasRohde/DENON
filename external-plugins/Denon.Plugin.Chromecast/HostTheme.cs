using System.Windows;
using System.Windows.Media;

namespace Denon.Plugin.Chromecast;

/// <summary>
/// Colors, styles, font sizes and corner radii from the Denon Desktop theme (the host's Themes/*.xaml), so this
/// plugin follows the app's look. Neutral values when there is no host (unit tests).
/// </summary>
internal static class HostTheme
{
    public static Brush Brush(string token) =>
        Application.Current?.TryFindResource(token + "Brush") as Brush ?? Brushes.Transparent;

    /// <summary>A style from the host theme (for example <c>PrimaryButton</c>); null without a host.</summary>
    public static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;

    /// <summary>A size from the host type scale (for example <c>FontSize06</c>); the system font size without a host.</summary>
    public static double FontSize(string key) =>
        Application.Current?.TryFindResource(key) is double size ? size : SystemFonts.MessageFontSize;

    /// <summary>A corner radius from the host radius scale (for example <c>Radius04</c>); square without a host.</summary>
    public static CornerRadius Radius(string key) =>
        Application.Current?.TryFindResource(key) is CornerRadius radius ? radius : default;
}

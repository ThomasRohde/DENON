using System.Windows;
using System.Windows.Media;

namespace Denon.Plugin.AppleTv;

/// <summary>
/// Colors from the Denon Desktop theme tokens (the host's Themes/Colors.*.xaml and Themes/Brushes.xaml), so this
/// plugin follows the app's look. Transparent when there is no host (unit tests).
/// </summary>
internal static class HostTheme
{
    public static Color Color(string token) =>
        Application.Current?.TryFindResource(token + "Color") is Color color ? color : Colors.Transparent;

    public static Brush Brush(string token) =>
        Application.Current?.TryFindResource(token + "Brush") as Brush ?? Brushes.Transparent;

    /// <summary>A style from the host theme (for example <c>PrimaryButton</c>); null without a host.</summary>
    public static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;

    /// <summary>"#AARRGGBB" for XAML templates parsed at run time.</summary>
    public static string Hex(string token)
    {
        var c = Color(token);
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}

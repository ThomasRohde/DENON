using System.Windows;
using System.Windows.Media;

namespace Denon.Plugin.PhilipsHue;

/// <summary>
/// Brushes from the Denon Desktop theme (Themes/Brushes.xaml in the host), for code in this built-in plugin.
/// Transparent when there is no host application (unit tests).
/// </summary>
internal static class HostTheme
{
    public static Brush Brush(string token) =>
        Application.Current?.TryFindResource(token + "Brush") as Brush ?? Brushes.Transparent;
}

using System.Windows;
using System.Windows.Media;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Brushes from the Denon Desktop theme (Themes/Brushes.xaml in the host), for code in this built-in plugin.
/// Transparent when there is no host application (unit tests).
/// </summary>
internal static class HostTheme
{
    public static Brush Brush(string token) =>
        Application.Current?.TryFindResource(token + "Brush") as Brush ?? Brushes.Transparent;

    /// <summary>
    /// Binds a brush property to a theme token live (<c>SetResourceReference</c>): the element follows a theme switch.
    /// Use this instead of assigning <see cref="Brush"/> to an element.
    /// </summary>
    public static void Bind(FrameworkElement element, DependencyProperty property, string token) =>
        element.SetResourceReference(property, token + "Brush");
}

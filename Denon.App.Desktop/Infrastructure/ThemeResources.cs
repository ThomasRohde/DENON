using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Denon.App.Desktop.Infrastructure;

/// <summary>
/// Theme colors for code (view models, converters, drawn controls). The keys are the tokens in
/// <c>Themes/Colors.Dark.xaml</c> and <c>Themes/Brushes.xaml</c>. Code reads them at use time, so a future theme
/// switch only has to swap the dictionaries. Without an application (unit tests) the result is transparent.
/// </summary>
public static class ThemeResources
{
    /// <summary>The frozen brush for a token, for example <c>Brush("AccentEmerald")</c> → <c>AccentEmeraldBrush</c>.</summary>
    public static Brush Brush(string token) =>
        Application.Current?.TryFindResource(token + "Brush") as Brush ?? Brushes.Transparent;

    /// <summary>The color for a token, for example <c>Color("AccentEmerald")</c> → <c>AccentEmeraldColor</c>.</summary>
    public static Color Color(string token) =>
        Application.Current?.TryFindResource(token + "Color") is Color color ? color : Colors.Transparent;

    /// <summary>The token color as a WinForms color (tray menu).</summary>
    public static System.Drawing.Color DrawingColor(string token)
    {
        var c = Color(token);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }
}

using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Application = System.Windows.Application;
using Denon.App.Desktop.Controls;

namespace Denon.App.Desktop.Infrastructure.Theming;

/// <summary>
/// The app's theme stack (palette, brushes, skin, meter materials) as CSS custom properties, for the Insights page in
/// the browser. Read from the live resources, so the page follows a theme or skin switch. Call on the UI thread.
/// </summary>
public static class ThemeCss
{
    /// <summary>Changes when the theme or the skin changes; the page reloads its theme CSS then.</summary>
    public static string Stamp => $"{ThemeManager.Current.Id}-{ThemeManager.CurrentSkin.Id}";

    public static string Build()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Application.Current is { } app)
        {
            Collect(app.Resources, vars, new HashSet<string>(StringComparer.Ordinal));
        }

        var meters = MeterPalette.Current;
        vars["--meter-left-accent"] = Css(meters.LeftAccent);
        vars["--meter-right-accent"] = Css(meters.RightAccent);
        vars["--meter-led-screen"] = Css(meters.LedScreen);
        vars["--meter-led-screen-edge"] = Css(meters.LedScreenEdge);
        vars["--meter-led-label"] = Css(meters.LedLabel);
        vars["--meter-led-floor-glow"] = Css(meters.LedFloorGlow);
        vars["--meter-led-ghost-opacity"] = Num(meters.LedGhostOpacity);
        vars["--meter-led-bloom"] = Num(meters.LedBloom);
        vars["--meter-heat-count"] = meters.LedHeat.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < meters.LedHeat.Count; i++)
        {
            vars[$"--meter-heat-{i}"] = $"{Num(meters.LedHeat[i].Offset)} {Css(meters.LedHeat[i].Color)}";
        }

        vars["--meter-led-heat"] = "linear-gradient(to right, " +
                                   string.Join(", ", meters.LedHeat.Select(h => $"{Css(h.Color)} {Num(h.Offset * 100)}%")) + ")";

        var sb = new StringBuilder();
        sb.Append("/* Denon Desktop theme: ").Append(ThemeManager.Current.Name).Append(", skin ")
          .Append(ThemeManager.CurrentSkin.Name).AppendLine(". Generated from the app's live resources. */");
        sb.AppendLine(":root {");
        sb.Append("  color-scheme: ").Append(ThemeManager.Current.IsDark ? "dark" : "light").AppendLine(";");
        sb.Append("  --theme-dark: ").Append(ThemeManager.Current.IsDark ? "1" : "0").AppendLine(";");
        foreach (var (name, value) in vars.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(name).Append(": ").Append(value).AppendLine(";");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void Collect(ResourceDictionary dictionary, Dictionary<string, string> vars, HashSet<string> colorNames)
    {
        // Merged dictionaries first, then the dictionary's own entries: later ones win, as in WPF lookup.
        foreach (var merged in dictionary.MergedDictionaries)
        {
            Collect(merged, vars, colorNames);
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                continue;
            }

            switch (entry.Value)
            {
                case Color color when key.EndsWith("Color", StringComparison.Ordinal):
                    vars[$"--{Kebab(key[..^5])}"] = Css(color);
                    colorNames.Add($"--{Kebab(key[..^5])}");
                    break;

                case SolidColorBrush brush when key.EndsWith("Brush", StringComparison.Ordinal):
                    // Role brushes (CardBgBrush, ChassisBgBrush …); palette brushes repeat their color's name, and a
                    // skin may set a role brush again (the later one wins).
                    var brushName = $"--{Kebab(key[..^5])}";
                    if (!colorNames.Contains(brushName))
                    {
                        vars[brushName] = Css(brush.Color);
                    }
                    break;

                case LinearGradientBrush gradient:
                    vars[$"--{Kebab(key)}"] = Gradient(gradient);
                    break;

                case double size when key.StartsWith("FontSize", StringComparison.Ordinal) || key.StartsWith("Radius", StringComparison.Ordinal):
                    vars[$"--app-{Kebab(key)}"] = $"{Num(size)}px";
                    break;

                case CornerRadius radius when key.StartsWith("Radius", StringComparison.Ordinal):
                    vars[$"--app-{Kebab(key)}"] = $"{Num(radius.TopLeft)}px";
                    break;

                case FontFamily family when key is "DisplayFont" or "UiFont" or "MonoFont":
                    vars[key switch { "DisplayFont" => "--font-display", "UiFont" => "--font-ui", _ => "--font-mono" }] =
                        Family(family, key == "MonoFont");
                    break;
            }
        }
    }

    /// <summary>"pack://application:,,,/Themes/Fonts/#Share Tech Mono, Consolas" → "Share Tech Mono", Consolas, ….</summary>
    private static string Family(FontFamily family, bool mono)
    {
        var names = (family.Source ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Contains('#') ? n[(n.LastIndexOf('#') + 1)..] : n)
            .Where(n => n.Length > 0)
            .Select(n => $"\"{n.Replace("\"", string.Empty)}\"");
        return string.Join(", ", names.Append(mono ? "ui-monospace, monospace" : "system-ui, sans-serif"));
    }

    private static string Gradient(LinearGradientBrush brush)
    {
        var dx = brush.EndPoint.X - brush.StartPoint.X;
        var dy = brush.EndPoint.Y - brush.StartPoint.Y;
        var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        var stops = brush.GradientStops.Select(s => $"{Css(s.Color)} {Num(s.Offset * 100)}%");
        return $"linear-gradient({Num(angle)}deg, {string.Join(", ", stops)})";
    }

    private static string Css(Color c) =>
        c.A == 255
            ? $"#{c.R:x2}{c.G:x2}{c.B:x2}"
            : $"rgb({c.R} {c.G} {c.B} / {Num(c.A / 255.0)})";

    private static string Num(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    private static string Kebab(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && (char.IsUpper(ch) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])) ||
                          char.IsDigit(ch) && char.IsLetter(name[i - 1])))
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}

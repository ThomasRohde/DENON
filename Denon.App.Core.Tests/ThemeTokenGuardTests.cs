using System.Text.RegularExpressions;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Keeps every UI color in the theme tokens (Denon.App.Desktop/Themes), so a theme can change the whole look.
/// Scans the app and the built-in plugins as source files.
/// </summary>
public sealed partial class ThemeTokenGuardTests
{
    private static readonly string[] SourceDirs =
        ["Denon.App.Desktop", "Denon.Plugin.HomeAssistant", "Denon.Plugin.PhilipsHue", "Denon.Plugin.SmartTv"];

    /// <summary>
    /// C# files that may hold literal colors, and why. Content colors are data, not theme: a theme must not change
    /// the color a user gave a scene, the color of a Hue light, or a plugin's identity color.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedCodeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Denon.App.Desktop/Controls/MeterPalette.cs"] = "meter material colors (a future skin supplies another palette)",
        ["Denon.App.Desktop/ViewModels/MainViewModel.Help.cs"] = "help section identity colors (content)",
        ["Denon.App.Desktop/Models/HelpGuideModels.cs"] = "fallback for a help section color (content)",
        ["Denon.App.Desktop/ViewModels/MainViewModel.ScenesAcoustics.cs"] = "default scene accent colors (user data)",
        ["Denon.App.Desktop/ViewModels/ScenePresetEditor.cs"] = "scene accent color the user picks (user data)",
        ["Denon.App.Desktop/ViewModels/StationPresetEditor.cs"] = "station color swatches the user picks (user data)",
        ["Denon.App.Desktop/ViewModels/MainViewModel.Plugins.cs"] = "fallback for a plugin's identity color (content)",
        ["Denon.App.Desktop/Services/AlbumArtPaletteService.cs"] = "fallback album-art colors for Hue sync (content)",
        ["Denon.Plugin.HomeAssistant/HomeAssistantPlugin.cs"] = "plugin identity color in the manifest",
        ["Denon.Plugin.SmartTv/SmartTvPlugin.cs"] = "plugin identity color in the manifest",
        ["Denon.Plugin.PhilipsHue/PhilipsHuePlugin.cs"] = "light colors sent to Hue lamps, plugin identity color",
        ["Denon.Plugin.PhilipsHue/Models/HueModels.cs"] = "mood preset light colors and light color conversion",
        ["Denon.Plugin.PhilipsHue/ViewModels/HueDashboardViewModel.cs"] = "light and album-art preview colors (content)",
    };

    private static readonly string Root = FindRepoRoot();

    [Fact]
    public void Xaml_has_no_hex_colors_outside_the_theme_color_files()
    {
        var offenders = XamlFiles()
            .Where(f => !Path.GetFileName(f).StartsWith("Colors.", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => Lines(f).Where(l => XamlHexColor().IsMatch(l.Text)).Select(l => $"{Rel(f)}:{l.Number}: {l.Text.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Use a theme token ({StaticResource …Brush}) instead of a hex color:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Code_has_no_literal_colors_outside_the_allowed_content_files()
    {
        var offenders = CodeFiles()
            .Where(f => !AllowedCodeColors.ContainsKey(Rel(f)))
            .SelectMany(f => Lines(f)
                .Where(l => CodeHexColor().IsMatch(l.Text) || CodeColorLiteral().IsMatch(l.Text))
                .Select(l => $"{Rel(f)}:{l.Number}: {l.Text.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use ThemeResources.Brush/Color (app) or HostTheme.Brush (built-in plugins) instead of a literal color, " +
            "or add the file to AllowedCodeColors with the reason:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_resource_key_used_in_xaml_is_defined()
    {
        var files = XamlFiles().ToList();
        var defined = files.SelectMany(f => XKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var missing = files
            .SelectMany(f => ResourceReference().Matches(File.ReadAllText(f)).Select(m => (File: Rel(f), Key: m.Groups[1].Value)))
            .Where(r => !defined.Contains(r.Key))
            .Select(r => $"{r.File}: {r.Key}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Undefined resource keys (the app throws when it loads the view):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_token_read_from_code_exists()
    {
        var themes = Path.Combine(Root, "Denon.App.Desktop", "Themes");
        var brushes = XKey().Matches(File.ReadAllText(Path.Combine(themes, "Brushes.xaml"))).Select(m => m.Groups[1].Value).ToHashSet();
        var colors = XKey().Matches(File.ReadAllText(Path.Combine(themes, "Colors.Dark.xaml"))).Select(m => m.Groups[1].Value).ToHashSet();

        var missing = CodeFiles()
            .SelectMany(f => TokenRead().Matches(File.ReadAllText(f)).Select(m => (File: Rel(f), Kind: m.Groups[1].Value, Token: m.Groups[2].Value)))
            .Where(r => r.Kind == "Brush" ? !brushes.Contains(r.Token + "Brush") : !colors.Contains(r.Token + "Color"))
            .Select(r => $"{r.File}: {r.Kind}(\"{r.Token}\")")
            .ToList();

        Assert.True(missing.Count == 0, "Unknown theme tokens (they would draw transparent):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_color_token_has_a_brush_and_every_brush_has_a_color()
    {
        var themes = Path.Combine(Root, "Denon.App.Desktop", "Themes");
        var colors = XKey().Matches(File.ReadAllText(Path.Combine(themes, "Colors.Dark.xaml")))
            .Select(m => m.Groups[1].Value).Where(k => k.EndsWith("Color", StringComparison.Ordinal))
            .Select(k => k[..^"Color".Length]).ToHashSet();
        var brushText = File.ReadAllText(Path.Combine(themes, "Brushes.xaml"));
        var solidBrushes = SolidBrush().Matches(brushText).Select(m => (Key: m.Groups[1].Value[..^"Brush".Length], Color: m.Groups[2].Value[..^"Color".Length])).ToList();

        Assert.Empty(colors.Except(solidBrushes.Select(b => b.Key)));
        Assert.All(solidBrushes, b => Assert.Contains(b.Color, colors));
    }

    private static IEnumerable<string> XamlFiles() => SourceFiles("*.xaml");

    private static IEnumerable<string> CodeFiles() => SourceFiles("*.cs");

    private static IEnumerable<string> SourceFiles(string pattern) =>
        SourceDirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), pattern, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static IEnumerable<(int Number, string Text)> Lines(string file) =>
        File.ReadLines(file).Select((text, i) => (i + 1, text)).Where(l => !l.text.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Rel(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DenonDesktop.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (DenonDesktop.slnx) not found.");
    }

    [GeneratedRegex("""(=\s*"|>)\s*#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\s*("|<)""")]
    private static partial Regex XamlHexColor();

    [GeneratedRegex("\"#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?\"")]
    private static partial Regex CodeHexColor();

    // Literal RGB channels only: Color.FromArgb(28, c.R, c.G, c.B) (alpha on a token color) is fine.
    [GeneratedRegex(@"Color\.FromRgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)|Color\.FromArgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)")]
    private static partial Regex CodeColorLiteral();

    [GeneratedRegex("x:Key=\"([^\"{}]+)\"")]
    private static partial Regex XKey();

    [GeneratedRegex(@"\{(?:Static|Dynamic)Resource\s+([A-Za-z_][\w.]*)\s*\}")]
    private static partial Regex ResourceReference();

    [GeneratedRegex("""(?:ThemeResources|HostTheme)\.(Brush|Color|DrawingColor)\("(\w+)"\)""")]
    private static partial Regex TokenRead();

    [GeneratedRegex(@"<SolidColorBrush[^>]*x:Key=""(\w+Brush)""[^>]*Color=""\{StaticResource (\w+Color)\}""")]
    private static partial Regex SolidBrush();
}

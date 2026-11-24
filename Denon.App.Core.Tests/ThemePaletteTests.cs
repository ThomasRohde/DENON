using System.Globalization;
using System.Text.RegularExpressions;
using Denon.App.Desktop.Infrastructure.Theming;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The palette contract (docs/theming.md): every palette has the same tokens, text keeps its contrast on every
/// surface, and views refer to theme brushes live, so a theme switch reaches them.
/// </summary>
public sealed partial class ThemePaletteTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Themes = Path.Combine(Root, "Denon.App.Desktop", "Themes");

    /// <summary>The ramp steps text sits on: the page, sidebar, panels, cards and controls.</summary>
    private static readonly string[] Surfaces = ["Ink01", "Ink02", "Ink03", "Ink04", "Ink05", "Ink06", "Ink07", "Ink08"];

    public static TheoryData<string> Palettes()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Themes, "Palettes"), "*.xaml"))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Fact]
    public void Every_built_in_theme_has_a_palette_file_and_every_palette_a_theme()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Themes, "Palettes"), "*.xaml").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var registered = ThemeManager.Themes.Select(t => Path.GetFileNameWithoutExtension(t.PaletteUri.AbsolutePath)).ToHashSet();
        Assert.Equal(files.Order(), registered.Order());
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Palette_defines_exactly_the_tokens_of_the_dark_palette(string palette)
    {
        var dark = Load("Dark").Keys.ToHashSet();
        var keys = Load(palette).Keys.ToHashSet();
        Assert.True(dark.SetEquals(keys),
            $"{palette}: missing {string.Join(", ", dark.Except(keys))}; extra {string.Join(", ", keys.Except(dark))}");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Text_keeps_its_contrast_on_every_surface(string palette)
    {
        var colors = Load(palette);
        var floors = new Dictionary<string, double>
        {
            ["TextPrimary"] = 7, ["TextStrong"] = 7,
            ["TextSoft"] = 4.5, ["TextSecondary"] = 4.5, ["TextMuted"] = 4.5,
            ["TextFaint"] = 3,
            // Accents used as text and icons.
            ["AccentCyan"] = 4, ["AccentCyanBright"] = 4, ["AccentEmerald"] = 4, ["AccentAmber"] = 4, ["AccentCrimson"] = 4,
            // Accent text colors.
            ["AccentCyanText"] = 4.5, ["AccentEmeraldText"] = 4.5, ["AccentAmberText"] = 4.5, ["AccentCrimsonText"] = 4.5,
            ["AccentBlueText"] = 4.5, ["AccentIndigoText"] = 4.5, ["AccentVioletText"] = 4.5, ["AccentPinkText"] = 4.5, ["AccentRoseText"] = 4.5,
        };

        var failures = (from floor in floors
                        from surface in Surfaces
                        let ratio = Contrast(colors[floor.Key], colors[surface])
                        where ratio < floor.Value
                        select $"{floor.Key} on {surface}: {ratio:0.00} < {floor.Value}").ToList();

        Assert.True(failures.Count == 0, $"{palette}:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Tinted_chips_and_accent_buttons_stay_readable(string palette)
    {
        var c = Load(palette);
        (string Text, string Background, double Floor)[] pairs =
        [
            ("AccentCyanText", "AccentCyanSubtleBg", 4.5), ("AccentEmeraldText", "AccentEmeraldSubtleBg", 4.5),
            ("AccentAmberText", "AccentAmberSubtleBg", 4.5), ("AccentCrimsonText", "AccentCrimsonBg", 4.5),
            ("AccentVioletText", "AccentVioletSubtleBg", 4.5),
            ("TextOnAccent", "AccentCyan", 4.5), ("TextOnAccent", "AccentSkyDeep", 4.5), ("AccentCyanInk", "AccentCyan", 4.5),
        ];

        var failures = pairs.Select(p => (p, Ratio: Contrast(c[p.Text], c[p.Background])))
            .Where(x => x.Ratio < x.p.Floor)
            .Select(x => $"{x.p.Text} on {x.p.Background}: {x.Ratio:0.00} < {x.p.Floor}")
            .ToList();
        Assert.True(failures.Count == 0, $"{palette}:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Views_refer_to_theme_brushes_live()
    {
        var themeKeys = XKey().Matches(File.ReadAllText(Path.Combine(Themes, "Brushes.xaml"))).Select(m => m.Groups[1].Value)
            .Concat(Load("Dark").Keys.Select(k => k + "Color"))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = XamlFiles()
            .Where(f => Path.GetFileName(f) != "Brushes.xaml" && Path.GetFileName(Path.GetDirectoryName(f)) != "Palettes")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (Line: line, Number: i + 1))
                .SelectMany(l => StaticReference().Matches(l.Line).Where(m => themeKeys.Contains(m.Groups[1].Value))
                    .Select(m => $"{Path.GetRelativePath(Root, f)}:{l.Number}: {m.Value}")))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use {DynamicResource …} for theme brushes and colors, so a theme switch reaches the view:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Text_takes_its_color_from_its_control()
    {
        // A Foreground in the implicit TextBlock style beats the inherited color, so text in a button would ignore
        // the button's Foreground (dark text on the blue primary button in Light, 2.0.0).
        var styles = File.ReadAllText(Path.Combine(Themes, "Styles.xaml"));
        var implicitTextStyle = ImplicitTextBlockStyle().Match(styles);
        Assert.True(implicitTextStyle.Success, "Styles.xaml has no implicit TextBlock style");
        Assert.DoesNotContain("Property=\"Foreground\"", implicitTextStyle.Value, StringComparison.Ordinal);

        // Instead every window sets the default text color, and text inherits it.
        var windows = XamlFiles().Where(f => File.ReadAllText(f).TrimStart().StartsWith("<Window", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(windows);
        Assert.All(windows, f =>
        {
            var text = File.ReadAllText(f);
            var root = text[..text.IndexOf('>', StringComparison.Ordinal)];
            Assert.True(root.Contains("Foreground=\"{DynamicResource TextPrimaryBrush}\"", StringComparison.Ordinal),
                $"{Path.GetFileName(f)}: the window sets no Foreground, so its text would be black");
        });
    }

    [Fact]
    public void Unknown_theme_setting_gives_dark()
    {
        Assert.Equal("dark", ThemeManager.Resolve("no-such-theme").Id);
        Assert.Equal("light", ThemeManager.Resolve("Light").Id);
    }

    /// <summary>Token (without "Color") → "#RRGGBB" or "#AARRGGBB".</summary>
    private static Dictionary<string, string> Load(string palette) =>
        PaletteColor().Matches(File.ReadAllText(Path.Combine(Themes, "Palettes", palette + ".xaml")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>WCAG 2 contrast ratio of two opaque colors (alpha is ignored: text tokens are opaque).</summary>
    private static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = hex[^6..];
        double Channel(int i)
        {
            var c = int.Parse(rgb.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static IEnumerable<string> XamlFiles() =>
        new[] { "Denon.App.Desktop", "Denon.Plugin.HomeAssistant", "Denon.Plugin.PhilipsHue", "Denon.Plugin.SmartTv" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.xaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

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

    [GeneratedRegex("""<Color x:Key="(\w+)Color">(#[0-9A-Fa-f]{6,8})</Color>""")]
    private static partial Regex PaletteColor();

    [GeneratedRegex("x:Key=\"([^\"{}]+)\"")]
    private static partial Regex XKey();

    [GeneratedRegex(@"\{StaticResource\s+([A-Za-z_][\w.]*)\s*\}")]
    private static partial Regex StaticReference();

    [GeneratedRegex(@"<Style TargetType=""TextBlock"">.*?</Style>", RegexOptions.Singleline)]
    private static partial Regex ImplicitTextBlockStyle();
}

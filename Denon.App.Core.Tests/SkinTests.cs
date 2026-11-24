using System.Globalization;
using System.Text.RegularExpressions;
using Denon.App.Desktop.Infrastructure.Theming;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The skin contract (docs/theming.md): every skin defines the same tokens, and views take type, radii, fonts,
/// icons, spacing and sizes from the skin live, so a skin switch reaches every page.
/// </summary>
public sealed partial class SkinTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string SkinDir = Path.Combine(Root, "Denon.App.Desktop", "Themes", "Skins");

    public static TheoryData<string> SkinFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(SkinDir, "*.xaml"))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Fact]
    public void Every_built_in_skin_has_a_file_and_every_file_a_skin()
    {
        var files = Directory.EnumerateFiles(SkinDir, "*.xaml").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var registered = ThemeManager.Skins.Select(s => Path.GetFileNameWithoutExtension(s.TokensUri.AbsolutePath)).ToHashSet();
        Assert.Equal(files.Order(), registered.Order());
        Assert.Equal(ThemeManager.Skins.Count, ThemeManager.Skins.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(SkinFiles))]
    public void Skin_defines_exactly_the_tokens_of_the_default_skin(string skin)
    {
        var defaults = Keys("Default");
        var keys = Keys(skin);
        Assert.True(defaults.SetEquals(keys),
            $"{skin}: missing {string.Join(", ", defaults.Except(keys))}; extra {string.Join(", ", keys.Except(defaults))}");
    }

    [Theory]
    [MemberData(nameof(SkinFiles))]
    public void Type_scale_rises_and_stays_readable(string skin)
    {
        var sizes = Enumerable.Range(0, 15).Select(i => Number(skin, $"FontSize{i:00}")).ToList();
        Assert.True(sizes[0] >= 8.5, $"{skin}: FontSize00 is {sizes[0]}; text below 8.5 px is not readable");
        for (var i = 1; i < sizes.Count; i++)
        {
            Assert.True(sizes[i] >= sizes[i - 1], $"{skin}: FontSize{i:00} ({sizes[i]}) is smaller than FontSize{i - 1:00} ({sizes[i - 1]})");
        }

        Assert.InRange(Number(skin, "SpaceScale"), 0.5, 2);
        Assert.InRange(Number(skin, "SizeScale"), 0.5, 2);
    }

    [Fact]
    public void Compact_is_smaller_and_large_is_larger_than_default()
    {
        foreach (var key in Enumerable.Range(0, 15).Select(i => $"FontSize{i:00}").Append("SpaceScale").Append("SizeScale"))
        {
            Assert.True(Number("Compact", key) <= Number("Default", key), $"Compact {key}");
            Assert.True(Number("Large", key) >= Number("Default", key), $"Large {key}");
        }
    }

    [Fact]
    public void Views_take_skin_tokens_live()
    {
        var offenders = XamlFiles()
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) != "Skins")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (Line: line, Number: i + 1))
                .SelectMany(l => StaticSkinReference().Matches(l.Line).Select(m => $"{Rel(f)}:{l.Number}: {m.Value}")))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use {DynamicResource …} for skin tokens (type, radii, fonts, icons), so a skin switch reaches the view:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Views_write_spacing_and_sizes_with_the_skin_scales()
    {
        var offenders = XamlFiles()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (Line: line, Number: i + 1))
                .SelectMany(l => LiteralSpacing().Matches(l.Line).Concat(LiteralSize().Matches(l.Line))
                    .Where(IsScalable)
                    .Where(_ => !IsWindowAttribute(f, l.Number))
                    .Select(m => $"{Rel(f)}:{l.Number}: {m.Value}")))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Write margins and padding as {skin:Space …} and sizes as {skin:Size …} (Default-skin pixels), so a skin " +
            "scales them:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData(12, 1, 12)]
    [InlineData(4.5, 1, 4.5)]
    [InlineData(12, 0.75, 9)]
    [InlineData(10, 1.25, 12.5)]
    [InlineData(7, 1.25, 9)]
    [InlineData(-5, 1.2, -6)]
    public void Scale_rounds_to_half_pixels_and_keeps_default_exact(double value, double scale, double expected) =>
        Assert.Equal(expected, SkinMetrics.Scale(value, scale));

    [Fact]
    public void Unknown_skin_setting_gives_default()
    {
        Assert.Equal(ThemeManager.DefaultSkinId, ThemeManager.ResolveSkin("no-such-skin").Id);
        Assert.Equal("hifi", ThemeManager.ResolveSkin("HiFi").Id);
    }

    /// <summary>A literal is fine when it is zero (spacing) or a hairline of up to 2 px (sizes).</summary>
    private static bool IsScalable(Match m)
    {
        var numbers = m.Groups["v"].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList();
        return m.Groups["space"].Success ? numbers.Any(n => n != 0) : numbers.Any(n => n > 2);
    }

    /// <summary>Attributes of a Window element (window size stays as the user or the layout sets it).</summary>
    private static bool IsWindowAttribute(string file, int lineNumber)
    {
        var lines = File.ReadLines(file).Take(lineNumber).ToList();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var text = lines[i].TrimStart();
            if (text.StartsWith('<'))
            {
                return text.StartsWith("<Window", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static HashSet<string> Keys(string skin) =>
        XKey().Matches(File.ReadAllText(Path.Combine(SkinDir, skin + ".xaml"))).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static double Number(string skin, string key)
    {
        var m = Regex.Match(File.ReadAllText(Path.Combine(SkinDir, skin + ".xaml")), $"x:Key=\"{key}\">([^<]+)<");
        Assert.True(m.Success, $"{skin}: {key} not found");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> XamlFiles() =>
        new[] { "Denon.App.Desktop", "Denon.Plugin.HomeAssistant", "Denon.Plugin.PhilipsHue", "Denon.Plugin.SmartTv" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.xaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

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

    [GeneratedRegex("x:Key=\"([^\"{}]+)\"")]
    private static partial Regex XKey();

    [GeneratedRegex(@"\{StaticResource\s+(FontSize\d\d|Radius\d\d|DisplayFont|UiFont|MonoFont|Icon\w+)\s*\}")]
    private static partial Regex StaticSkinReference();

    // Margin="0,0,0,12", Padding="8" and the same as setters.
    [GeneratedRegex("""(?:(?<=\s)(?:Margin|Padding)="|Property="(?:Margin|Padding)"\s+Value=")(?<v>[-0-9., ]+)"(?<space>)""")]
    private static partial Regex LiteralSpacing();

    // Width="32", MinHeight="40" and the same as setters.
    [GeneratedRegex("""(?:(?<=\s)(?:Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight)="|Property="(?:Width|Height|MinWidth|MinHeight|MaxWidth|MaxHeight)"\s+Value=")(?<v>[0-9.]+)(?=")""")]
    private static partial Regex LiteralSize();
}

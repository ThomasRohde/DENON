using System.Text.RegularExpressions;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Keyboard and screen-reader basics in the app's XAML (#57): a visible focus ring on the shared control styles, a
/// name on every symbol-only button, and light text on the dark scrim in both themes.
/// </summary>
public sealed partial class AccessibilityGuardTests
{
    private static readonly string Root = FindRepoRoot();
    private static string Desktop(string path) => Path.Combine(Root, "Denon.App.Desktop", path);

    [Fact]
    public void Shared_control_styles_show_a_keyboard_focus_ring()
    {
        var styles = File.ReadAllText(Desktop("Themes/Styles.xaml"));
        var missing = StyleStart().Matches(styles)
            .Where(m => !m.Value.Contains("BasedOn", StringComparison.Ordinal))
            .Where(m => !StyleBody(styles, m.Index).Contains("Property=\"FocusVisualStyle\"", StringComparison.Ordinal))
            .Select(m => m.Value)
            .ToList();

        Assert.True(missing.Count == 0, "Add a FocusVisualStyle setter (FocusRing) to:\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("TrayFlyoutWindow.xaml")]
    [InlineData("Views/ControlDeckView.xaml")]
    [InlineData("Views/YouTubeMusicView.xaml")]
    public void Symbol_only_buttons_have_a_screen_reader_name(string file)
    {
        var text = File.ReadAllText(Desktop(file));
        var unnamed = SymbolButton().Matches(text)
            .Where(m => !m.Value.Contains("AutomationProperties.Name", StringComparison.Ordinal))
            .Select(m => $"{file}:{text[..m.Index].Count(c => c == '\n') + 1}: {m.Groups["content"].Value}")
            .ToList();

        Assert.True(unnamed.Count == 0, "A button that shows only a symbol needs AutomationProperties.Name:\n" + string.Join("\n", unnamed));
    }

    [Fact]
    public void Text_on_the_scrim_stays_light_in_both_themes()
    {
        foreach (var palette in new[] { "Dark", "Light" })
        {
            Assert.Contains("x:Key=\"TextOnScrimColor\">#F1F5F9<", File.ReadAllText(Desktop($"Themes/Palettes/{palette}.xaml")));
        }

        var youTube = File.ReadAllText(Desktop("Views/YouTubeMusicView.xaml"));
        Assert.Matches("""Text="\{Binding DurationDisplay\}"[^>]*Foreground="\{DynamicResource TextOnScrimBrush\}""", youTube);
    }

    /// <summary>The style's text up to its closing tag (styles do not nest in Styles.xaml).</summary>
    private static string StyleBody(string text, int start)
    {
        var end = text.IndexOf("</Style>", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

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

    // Interactive control styles: buttons, rail and toggle items, sliders, lists, combo boxes, expanders.
    [GeneratedRegex("""<Style (x:Key="[^"]+" )?TargetType="(Button|RadioButton|CheckBox|Slider|ComboBox|ListBoxItem|Expander)"[^>]*>""")]
    private static partial Regex StyleStart();

    // <Button ... Content="↑" ...>: content with no letters (arrows, ✕, ▶, +1, -5, + Q is not matched: it has a letter).
    [GeneratedRegex("""<Button\b(?=[^>]*\bContent="(?<content>[^"A-Za-z{]+)")[^>]*>""")]
    private static partial Regex SymbolButton();
}

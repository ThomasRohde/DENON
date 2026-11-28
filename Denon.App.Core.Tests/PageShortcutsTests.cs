using System.Text.RegularExpressions;
using Denon.App.Desktop.CommandPalette;
using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>Ctrl+1–9 and Ctrl+0 open the nav rail pages in rail order, Brain included (#76).</summary>
public sealed class PageShortcutsTests
{
    [Fact]
    public void The_keys_follow_the_rail_from_ctrl_1_to_ctrl_0()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 0], PageShortcuts.All.Select(shortcut => shortcut.Digit));
        Assert.Equal(CommandPalettePages.All.Select(page => page.Section), PageShortcuts.All.Select(shortcut => shortcut.Page.Section));
    }

    [Theory]
    [InlineData(1, "Control")]
    [InlineData(3, "Stations")]
    [InlineData(5, "Acoustics")]
    [InlineData(7, "Mcp")] // Brain has a key now.
    [InlineData(8, "Settings")]
    [InlineData(0, CommandPalettePages.HelpSection)]
    public void A_digit_opens_its_page(int digit, string section) =>
        Assert.Equal(section, PageShortcuts.ForDigit(digit)?.Section);

    [Fact]
    public void The_rail_in_MainWindow_has_the_same_pages_in_the_same_order()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Denon.App.Desktop", "MainWindow.xaml"));
        var railSections = Regex.Matches(xaml, "GroupName=\"NavRail\"[^>]*?ConverterParameter=(?<section>\\w+)\\}")
            .Select(match => match.Groups["section"].Value);

        Assert.Equal(CommandPalettePages.All.Select(page => page.Section), railSections);
    }

    [Fact]
    public void Every_page_key_is_reserved_for_in_app_shortcuts()
    {
        foreach (var shortcut in PageShortcuts.All)
        {
            Assert.Contains(shortcut.Gesture, GlobalHotkeyService.ReservedNavigationGestures);
            Assert.Contains("reserved", GlobalHotkeyService.GetReservedWarning(shortcut.Gesture, isGlobal: false));
        }

        // The page keys plus Ctrl+R, Ctrl+K and F1.
        Assert.Equal(PageShortcuts.All.Count + 3, GlobalHotkeyService.ReservedNavigationGestures.Count);
    }

    [Fact]
    public void Help_lists_every_key_in_rail_order()
    {
        Assert.Equal("Ctrl + 1 .. 0", PageShortcuts.KeyRange());
        Assert.Equal(
            "1 Deck, 2 Music, 3 Stations, 4 Cast, 5 Scenes, 6 Plugins, 7 Brain, 8 Settings, 9 Health, 0 Help",
            PageShortcuts.Summary());
    }

    [Fact]
    public void The_palette_shows_the_key_of_each_page()
    {
        var brain = CommandPalettePages.Entries().Single(entry => entry.Key == "page:Mcp");

        Assert.Equal("Open this page · Ctrl+7", brain.Subtitle);
    }

    [Fact]
    public void A_rail_with_more_pages_than_digit_keys_is_refused()
    {
        var pages = Enumerable.Range(0, 11)
            .Select(i => new CommandPalettePage($"Page{i}", $"P{i}", $"Page {i}", "IconNavHelp", []))
            .ToList();

        Assert.Throws<InvalidOperationException>(() => PageShortcuts.Create(pages));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DenonDesktop.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("DenonDesktop.slnx not found above the test folder.");
    }
}

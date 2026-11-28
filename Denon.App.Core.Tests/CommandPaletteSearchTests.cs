using Denon.App.Desktop.CommandPalette;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The Ctrl+K command palette finds and ranks pages, scenes, stations, inputs, sound modes and plugin actions (#76).</summary>
public sealed class CommandPaletteSearchTests
{
    private static readonly CommandPaletteEntry[] Sample =
    [
        .. CommandPalettePages.Entries(),
        new("scene:movie", CommandPaletteCategory.Scene, "Movie Night", "Smart Scene", ["cinema", "film"]),
        new("scene:morning", CommandPaletteCategory.Scene, "Morning Radio", "Smart Scene"),
        new("station:dr-p3", CommandPaletteCategory.Station, "DR P3", "Saved station", ["p3"]),
        new("station:jazz", CommandPaletteCategory.Station, "Jazz FM", "Saved station"),
        new("input:SAT/CBL", CommandPaletteCategory.Input, "CBL/SAT", "Input", ["SAT/CBL"]),
        new("input:GAME", CommandPaletteCategory.Input, "Game", "Input", ["GAME"]),
        new("input:TV", CommandPaletteCategory.Input, "TV Audio", "Input", ["TV"]),
        new("soundmode:MOVIE", CommandPaletteCategory.SoundMode, "Movie", "Sound mode", ["MOVIE"]),
        new("soundmode:STEREO", CommandPaletteCategory.SoundMode, "Stereo", "Sound mode", ["STEREO"]),
        new("plugin:hue", CommandPaletteCategory.Plugin, "Philips Hue", "Plugin"),
        new("plugin:hue:all-off", CommandPaletteCategory.PluginAction, "All lights off", "Philips Hue"),
        new("plugin:tv:power-on", CommandPaletteCategory.PluginAction, "Turn on TV", "Smart TV"),
    ];

    private static List<string> Keys(string query, int max = CommandPaletteSearch.DefaultMaxResults) =>
        CommandPaletteSearch.Search(Sample, query, max).Select(m => m.Entry.Key).ToList();

    [Fact]
    public void Empty_query_returns_the_rows_in_their_given_order()
    {
        var keys = Keys("   ");

        Assert.Equal(Sample.Select(e => e.Key), keys);
    }

    [Fact]
    public void Empty_query_honours_the_result_limit()
    {
        Assert.Equal(3, Keys(string.Empty, max: 3).Count);
        Assert.Empty(Keys("movie", max: 0));
    }

    [Fact]
    public void Every_query_word_must_match()
    {
        Assert.Empty(Keys("movie zebra"));
        Assert.Empty(Keys("xyzzy"));
    }

    [Fact]
    public void Exact_title_ranks_above_a_title_prefix()
    {
        var keys = Keys("movie");

        Assert.Equal("soundmode:MOVIE", keys[0]);
        Assert.Equal("scene:movie", keys[1]);
    }

    [Fact]
    public void Title_prefix_ranks_above_a_later_word_and_text_inside()
    {
        var keys = Keys("mo");

        // Three titles start with "mo". Equal scores: scenes before sound modes, the shorter title first.
        // "Cast & Remote" only has "mo" inside a word, so it comes after them.
        Assert.Equal(["scene:movie", "scene:morning", "soundmode:MOVIE"], keys.Take(3));
        Assert.Contains("page:CastRemote", keys.Skip(3));
    }

    [Fact]
    public void Word_prefix_finds_a_later_title_word()
    {
        var keys = Keys("night");

        Assert.Equal("scene:movie", keys[0]);
    }

    [Fact]
    public void Initials_find_a_title()
    {
        var keys = Keys("mn");

        Assert.Equal("scene:movie", keys[0]);
    }

    [Fact]
    public void Letters_in_order_find_a_title_but_must_start_a_word()
    {
        Assert.Equal("plugin:hue:all-off", Keys("alof")[0]);
        Assert.Equal("input:SAT/CBL", Keys("cblst")[0]);

        // "movienight" has o, v and n in order, but no title word starts with "o" there.
        Assert.Empty(Keys("ovn"));
    }

    [Fact]
    public void Subtitle_and_keywords_find_a_row_below_title_matches()
    {
        var keys = Keys("cinema");

        // The Music page lists "cinema" as a keyword; so does the scene. No title contains it.
        Assert.Contains("scene:movie", keys);
        Assert.Contains("page:YouTube", keys);
        Assert.All(CommandPaletteSearch.Search(Sample, "cinema"), m => Assert.True(m.Score < 50));
    }

    [Fact]
    public void Plugin_actions_are_found_by_the_plugin_name()
    {
        var keys = Keys("hue off");

        Assert.Equal("plugin:hue:all-off", keys.Single());
    }

    [Fact]
    public void Category_words_narrow_the_results()
    {
        var keys = Keys("input game");

        Assert.Equal("input:GAME", keys[0]);
        Assert.All(keys, key => Assert.StartsWith("input:", key));
    }

    [Fact]
    public void Rail_labels_find_their_page()
    {
        Assert.Equal("page:Mcp", Keys("brain")[0]);
        Assert.Equal("page:Control", Keys("deck")[0]);
        Assert.Equal("page:Diagnostics", Keys("health")[0]);
    }

    [Fact]
    public void Case_accents_and_punctuation_do_not_matter()
    {
        Assert.Equal("input:SAT/CBL", Keys("cbl sat")[0]);
        Assert.Equal("input:SAT/CBL", Keys("CBL-SAT")[0]);
        Assert.Equal("scene:movie", Keys("MÓVIE NIGHT")[0]);
        Assert.Equal("page:Acoustics", Keys("scenes & acoustics")[0]);
    }

    [Fact]
    public void Equal_scores_keep_the_category_order()
    {
        // "Stations & Streams" (page) and "Stereo" (sound mode) both start with "st".
        var keys = Keys("st");

        Assert.True(keys.IndexOf("page:Stations") < keys.IndexOf("soundmode:STEREO"));
    }

    [Theory]
    [InlineData("Scènes & Acoustics", "scenes acoustics")]
    [InlineData("  HDMI-1 ", "hdmi 1")]
    [InlineData("CBL/SAT", "cbl sat")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_lowers_strips_accents_and_collapses_punctuation(string? text, string expected)
    {
        Assert.Equal(expected, CommandPaletteSearch.Normalize(text));
    }

    [Fact]
    public void The_pages_follow_the_nav_rail_order()
    {
        Assert.Equal(
            ["Control", "YouTube", "Stations", "CastRemote", "Acoustics", "Plugins", "Mcp", "Settings", "Diagnostics", "Help"],
            CommandPalettePages.All.Select(p => p.Section));
        Assert.Equal(
            ["Deck", "Music", "Stations", "Cast", "Scenes", "Plugins", "Brain", "Settings", "Health", "Help"],
            CommandPalettePages.All.Select(p => p.RailLabel));
    }
}

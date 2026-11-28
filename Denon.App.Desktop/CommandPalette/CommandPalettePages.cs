namespace Denon.App.Desktop.CommandPalette;

/// <summary>One page of the left nav rail.</summary>
/// <param name="Section">The MainViewModel.ActiveSection value, for example "Control".</param>
/// <param name="RailLabel">The short label under the rail icon, for example "Deck".</param>
/// <param name="Title">The full page name (the rail tooltip), for example "Control Deck".</param>
/// <param name="IconKey">The Themes/Icons.xaml geometry of the rail icon.</param>
/// <param name="Keywords">More words that find the page.</param>
public sealed record CommandPalettePage(
    string Section,
    string RailLabel,
    string Title,
    string IconKey,
    IReadOnlyList<string> Keywords);

/// <summary>
/// The pages of the left nav rail, top to bottom. No WPF: the palette and its tests read it.
/// </summary>
/// <remarks>
/// TODO(#76): MainWindow's Ctrl+1–0 shortcuts and the reserved gestures should read this list too, so
/// the keys follow the rail order. That waits until the desktop shell PR has merged.
/// </remarks>
public static class CommandPalettePages
{
    public const string HelpSection = "Help";

    public static IReadOnlyList<CommandPalettePage> All { get; } =
    [
        new("Control", "Deck", "Control Deck", "IconNavControlDeck",
            ["deck", "home", "now playing", "volume", "power", "transport", "zone 2"]),
        new("YouTube", "Music", "YouTube & Music", "IconNavYouTube",
            ["music", "youtube", "queue", "jukebox", "local media", "cinema"]),
        new("Stations", "Stations", "Stations & Streams", "IconRadio",
            ["stations", "radio", "streams", "presets", "favorites", "heos"]),
        new("CastRemote", "Cast", "Cast & Remote", "IconNavCast",
            ["cast", "chromecast", "remote", "trackpad", "tv", "phone remote"]),
        new("Acoustics", "Scenes", "Scenes & Acoustics", "IconNavScenes",
            ["scenes", "smart scenes", "acoustics", "audyssey", "multeq", "dynamic eq", "trims", "bass", "treble"]),
        new("Plugins", "Plugins", "Plugins", "IconNavPlugins",
            ["plugins", "hue", "home assistant", "smart tv", "devices"]),
        new("Mcp", "Brain", "MCP Server & Room Brain", "IconNavMcp",
            ["brain", "mcp", "room brain", "ai", "assistant", "intelligence"]),
        new("Settings", "Settings", "Settings", "IconNavSettings",
            ["settings", "preferences", "shortcuts", "theme", "appearance", "automations", "receiver address"]),
        new("Diagnostics", "Health", "Health & Support", "IconNavHealth",
            ["health", "support", "diagnostics", "logs", "console", "telnet"]),
        new(HelpSection, "Help", "Help & Guide", "IconNavHelp",
            ["help", "guide", "documentation", "f1", "how to"]),
    ];

    /// <summary>The palette rows for the pages, in rail order.</summary>
    public static IEnumerable<CommandPaletteEntry> Entries() =>
        All.Select(page => new CommandPaletteEntry(
            $"page:{page.Section}",
            CommandPaletteCategory.Page,
            page.Title,
            "Open this page",
            [page.RailLabel, .. page.Keywords],
            page));
}

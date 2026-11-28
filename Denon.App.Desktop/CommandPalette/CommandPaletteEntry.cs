namespace Denon.App.Desktop.CommandPalette;

/// <summary>The kind of thing a command palette result opens or runs. The order is the tie-break order.</summary>
public enum CommandPaletteCategory
{
    Page,
    Scene,
    Station,
    Input,
    SoundMode,
    Plugin,
    PluginAction,
    Tool,
}

/// <summary>
/// One row the command palette can find. Plain data: no WPF types, so the search can be unit-tested.
/// </summary>
/// <param name="Key">Unique key, for example "page:Control" or "input:GAME".</param>
/// <param name="Category">What the row is.</param>
/// <param name="Title">The main text, for example "Movie Night".</param>
/// <param name="Subtitle">The second line, for example "Smart Scene".</param>
/// <param name="Keywords">More words that find the row but are not shown.</param>
/// <param name="Payload">The object the palette runs (a scene, a station, a section name). The search ignores it.</param>
public sealed record CommandPaletteEntry(
    string Key,
    CommandPaletteCategory Category,
    string Title,
    string Subtitle = "",
    IReadOnlyList<string>? Keywords = null,
    object? Payload = null)
{
    public IReadOnlyList<string> SearchKeywords => Keywords ?? [];
}

/// <summary>A search hit and its score. A higher score is a better match.</summary>
public sealed record CommandPaletteMatch(CommandPaletteEntry Entry, int Score);

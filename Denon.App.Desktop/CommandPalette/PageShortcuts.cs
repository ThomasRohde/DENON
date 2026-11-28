namespace Denon.App.Desktop.CommandPalette;

/// <summary>One fixed page key: Ctrl plus a digit opens a page of the left nav rail.</summary>
/// <param name="Digit">The digit key, 1–9 then 0.</param>
/// <param name="Page">The rail page the key opens.</param>
public sealed record PageShortcut(int Digit, CommandPalettePage Page)
{
    /// <summary>The gesture as the shortcut checker writes it, for example "Ctrl+1".</summary>
    public string Gesture => $"Ctrl+{Digit}";
}

/// <summary>
/// Ctrl+1–9 and Ctrl+0 open the rail pages in rail order, top to bottom (#76). The keys come from
/// <see cref="CommandPalettePages.All"/>, so MainWindow, the reserved gestures and Help always agree with the rail.
/// No WPF: the tests read it.
/// </summary>
public static class PageShortcuts
{
    /// <summary>The page keys, in rail order. The first page is Ctrl+1 and the tenth is Ctrl+0.</summary>
    public static IReadOnlyList<PageShortcut> All { get; } = Create(CommandPalettePages.All);

    /// <summary>The page for a digit key (0–9), or null when no page has that key.</summary>
    public static CommandPalettePage? ForDigit(int digit) =>
        All.FirstOrDefault(shortcut => shortcut.Digit == digit)?.Page;

    /// <summary>The keys for Help and Settings, for example "1 Deck, 2 Music, … 0 Help".</summary>
    public static string Summary() =>
        string.Join(", ", All.Select(shortcut => $"{shortcut.Digit} {shortcut.Page.RailLabel}"));

    /// <summary>The range of keys for Help, for example "Ctrl + 1 .. 0".</summary>
    public static string KeyRange() =>
        All.Count == 0 ? string.Empty : $"Ctrl + {All[0].Digit} .. {All[^1].Digit}";

    internal static IReadOnlyList<PageShortcut> Create(IReadOnlyList<CommandPalettePage> pages)
    {
        // Ten digit keys: a rail with more pages would leave a page without a key, so fail at start-up and in the tests.
        if (pages.Count > 10)
        {
            throw new InvalidOperationException($"The nav rail has {pages.Count} pages, but there are only 10 digit keys.");
        }

        return pages.Select((page, index) => new PageShortcut((index + 1) % 10, page)).ToArray();
    }
}

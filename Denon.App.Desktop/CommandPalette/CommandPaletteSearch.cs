using System.Globalization;
using System.Text;

namespace Denon.App.Desktop.CommandPalette;

/// <summary>
/// Finds and ranks command palette rows. No WPF: the rules are unit-tested.
/// </summary>
/// <remarks>
/// Each word of the query must match the row: its title, its subtitle, a keyword or its category name.
/// A match at the start of the title ranks highest, then the start of a title word, the title initials
/// ("mn" finds "Movie Night"), text inside the title, the letters in order ("hdm1" finds "HDMI 1"),
/// and last the subtitle and keywords. Case and accents do not matter. Equal scores keep the category
/// order (pages first), then the shorter title, then A to Z.
/// </remarks>
public static class CommandPaletteSearch
{
    public const int DefaultMaxResults = 50;

    private const int ExactTitle = 100;
    private const int TitlePrefix = 80;
    private const int WordPrefix = 70;
    private const int Initials = 60;
    private const int TitleContains = 50;
    private const int OtherWordPrefix = 40;
    private const int OtherContains = 25;
    private const int SubsequenceBase = 20;
    private const int WholeQueryExactBonus = 100;
    private const int WholeQueryPrefixBonus = 40;

    /// <summary>Words that find every row of a category, for example "input" or "radio".</summary>
    public static IReadOnlyList<string> CategoryWords(CommandPaletteCategory category) => category switch
    {
        CommandPaletteCategory.Page => ["page", "go", "open"],
        CommandPaletteCategory.Scene => ["scene", "smart scene"],
        CommandPaletteCategory.Station => ["station", "radio", "stream"],
        CommandPaletteCategory.Input => ["input", "source"],
        CommandPaletteCategory.SoundMode => ["sound mode", "surround"],
        CommandPaletteCategory.Plugin => ["plugin", "open"],
        CommandPaletteCategory.PluginAction => ["plugin", "action", "run"],
        _ => [],
    };

    /// <summary>
    /// Returns the rows that match <paramref name="query"/>, best first. An empty query returns the rows
    /// in their given order, so the caller decides what shows before the user types.
    /// </summary>
    public static IReadOnlyList<CommandPaletteMatch> Search(
        IEnumerable<CommandPaletteEntry> entries,
        string? query,
        int maxResults = DefaultMaxResults)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maxResults <= 0)
        {
            return [];
        }

        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return entries.Take(maxResults).Select(entry => new CommandPaletteMatch(entry, 0)).ToList();
        }

        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(CommandPaletteMatch Match, int Order)>();
        var order = 0;
        foreach (var entry in entries)
        {
            var score = Score(entry, normalizedQuery, tokens);
            if (score > 0)
            {
                scored.Add((new CommandPaletteMatch(entry, score), order));
            }

            order++;
        }

        return scored
            .OrderByDescending(item => item.Match.Score)
            .ThenBy(item => item.Match.Entry.Category)
            .ThenBy(item => item.Match.Entry.Title.Length)
            .ThenBy(item => item.Match.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Order)
            .Take(maxResults)
            .Select(item => item.Match)
            .ToList();
    }

    /// <summary>
    /// The score of one row, or 0 when a query word does not match it. <paramref name="normalizedQuery"/>
    /// and <paramref name="tokens"/> come from <see cref="Normalize"/>.
    /// </summary>
    public static int Score(CommandPaletteEntry entry, string normalizedQuery, IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var title = Normalize(entry.Title);
        var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(titleWords.Select(word => word[0]));
        var others = entry.SearchKeywords
            .Prepend(entry.Subtitle)
            .Concat(CategoryWords(entry.Category))
            .Select(Normalize)
            .Where(text => text.Length > 0)
            .ToList();

        var total = 0;
        foreach (var token in tokens)
        {
            var best = ScoreTitle(token, title, titleWords, initials);
            if (best < OtherWordPrefix)
            {
                best = Math.Max(best, ScoreOthers(token, others));
            }

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        if (title == normalizedQuery)
        {
            total += WholeQueryExactBonus;
        }
        else if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            total += WholeQueryPrefixBonus;
        }

        return total;
    }

    /// <summary>
    /// Lower case, no accents, and every run of punctuation or spaces as one space:
    /// "Scènes &amp; Acoustics" becomes "scenes acoustics", "HDMI-1" becomes "hdmi 1".
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var ch in decomposed)
        {
            var kind = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (kind is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static int ScoreTitle(string token, string title, string[] titleWords, string initials)
    {
        if (title.Length == 0)
        {
            return 0;
        }

        if (title == token)
        {
            return ExactTitle;
        }

        if (title.StartsWith(token, StringComparison.Ordinal))
        {
            return TitlePrefix;
        }

        if (titleWords.Any(word => word.StartsWith(token, StringComparison.Ordinal)))
        {
            return WordPrefix;
        }

        if (token.Length >= 2 && initials.StartsWith(token, StringComparison.Ordinal))
        {
            return Initials;
        }

        if (title.Contains(token, StringComparison.Ordinal))
        {
            return TitleContains;
        }

        return token.Length >= 2 ? ScoreSubsequence(token, titleWords) : 0;
    }

    private static int ScoreOthers(string token, IReadOnlyList<string> others)
    {
        var best = 0;
        foreach (var text in others)
        {
            if (text == token
                || text.StartsWith(token, StringComparison.Ordinal)
                || text.Split(' ').Any(word => word.StartsWith(token, StringComparison.Ordinal)))
            {
                return OtherWordPrefix;
            }

            if (token.Length >= 2 && text.Contains(token, StringComparison.Ordinal))
            {
                best = OtherContains;
            }
        }

        return best;
    }

    /// <summary>
    /// The token's letters appear in the title in order, and the first letter starts a title word:
    /// "hdmi1" finds "HDMI 1", "lvrm" finds "Living Room". Letters next to each other score a little
    /// more. Always below a plain "contains" match.
    /// </summary>
    private static int ScoreSubsequence(string token, string[] titleWords)
    {
        var text = string.Concat(titleWords);
        var best = 0;
        var wordStart = 0;
        foreach (var word in titleWords)
        {
            if (word[0] == token[0])
            {
                best = Math.Max(best, ScoreSubsequenceFrom(token, text, wordStart));
            }

            wordStart += word.Length;
        }

        return best;
    }

    private static int ScoreSubsequenceFrom(string token, string text, int start)
    {
        var position = start;
        var adjacent = 0;
        var previous = start - 2;
        foreach (var ch in token)
        {
            var found = text.IndexOf(ch, position);
            if (found < 0)
            {
                return 0;
            }

            if (found == previous + 1)
            {
                adjacent++;
            }

            previous = found;
            position = found + 1;
        }

        var bonus = adjacent * 10 / token.Length;
        return Math.Min(SubsequenceBase + bonus, TitleContains - 1);
    }
}

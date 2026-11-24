using System.Globalization;
using System.Text;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Matches TV channel names ("DR1", "TV 2 Charlie", "TV3+", "TV2 ØST") to logo files in the open tv-logos collection
/// ("dr1-dk.png", "tv2-charlie-dk.png", "tv3-plus-dk.png", "tv2-ost-dk.png"). The TV sends no logo and its DVB IDs
/// are network-private, so the name is the key: exact after normalizing first, then a few safe word rules.
/// </summary>
public static class ChannelLogoMatcher
{
    /// <summary>A logo file: its folder in the collection (for example "countries/nordic/denmark") and file name.</summary>
    public sealed record LogoFile(string Folder, string FileName)
    {
        public string Path => $"{Folder}/{FileName}";
    }

    /// <summary>Picture-quality and accessibility words that do not change which channel it is.</summary>
    private static readonly HashSet<string> DropWords = new(StringComparer.Ordinal)
    {
        "hd", "uhd", "fhd", "sd", "synstolkning", "tegnsprog", "syntolkning", "tegnspraak",
    };

    /// <summary>Words a logo name may add to or leave out of the channel name ("CNN" → "cnn-international").</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "channel", "international", "internacional", "tv", "world", "europe", "english", "int",
    };

    /// <summary>Brand words a logo name may put in front ("Kosmopol" → "tv2-kosmopol").</summary>
    private static readonly HashSet<string> BrandPrefixes = new(StringComparer.Ordinal) { "tv2", "tv", "kanal" };

    /// <summary>Style variants of a logo; the plain file is used.</summary>
    private static readonly HashSet<string> VariantWords = new(StringComparer.Ordinal)
    {
        "icon", "light", "mono", "dark", "hz", "alt", "white", "black",
    };

    private static readonly Dictionary<string, string> Translations = new(StringComparer.Ordinal)
    {
        ["engelsk"] = "english",
        ["dansk"] = "danish",
    };

    /// <summary>
    /// The logo for <paramref name="channelName"/>, or null. <paramref name="catalog"/> is in preference order (the
    /// country's own folder first): the first folder with a match wins.
    /// </summary>
    public static LogoFile? Match(string channelName, IReadOnlyList<LogoFile> catalog)
    {
        var words = Words(channelName);
        if (words.Count == 0)
        {
            return null;
        }

        var key = string.Concat(words);
        var keyNoQuality = string.Concat(words.Where(w => !DropWords.Contains(w)));
        var entries = catalog
            .Select(f => (File: f, Stem: StemWords(f.FileName)))
            .Where(e => e.Stem is { Count: > 0 })
            .ToList();

        foreach (var folder in catalog.Select(f => f.Folder).Distinct(StringComparer.Ordinal))
        {
            var inFolder = entries.Where(e => e.File.Folder == folder).ToList();

            // 1. Exact, with or without "HD" and the like on either side.
            foreach (var (file, stem) in inFolder)
            {
                var stemKey = string.Concat(stem!);
                var stemNoQuality = string.Concat(stem!.Where(w => !DropWords.Contains(w)));
                if (stemKey == key || stemKey == keyNoQuality || (stemNoQuality.Length > 0 && stemNoQuality == keyNoQuality))
                {
                    return file;
                }
            }

            // 2. Safe word rules, only when exactly one logo in the folder fits.
            var fits = inFolder.Where(e => FitsByWords(keyNoQuality, e.Stem!)).Select(e => e.File).ToList();
            if (fits.Count == 1)
            {
                return fits[0];
            }
        }

        return null;
    }

    private static bool FitsByWords(string key, IReadOnlyList<string> stem)
    {
        if (key.Length < 3)
        {
            return false;
        }

        var stemKey = string.Concat(stem.Where(w => !DropWords.Contains(w)));

        // "CNN" → "cnn-international", "Discovery" → "discovery-channel".
        for (var i = 1; i < stem.Count; i++)
        {
            if (string.Concat(stem.Take(i)) == key && stem.Skip(i).All(GenericWords.Contains))
            {
                return true;
            }
        }

        // "Al Jazeera English" → "aljazeera": the logo leaves out a generic word.
        if (key.StartsWith(stemKey, StringComparison.Ordinal) && stemKey.Length >= 3 &&
            GenericWords.Contains(key[stemKey.Length..]))
        {
            return true;
        }

        // "Kosmopol" → "tv2-kosmopol".
        return stem.Count >= 2 && BrandPrefixes.Contains(stem[0]) && string.Concat(stem.Skip(1)) == key && key.Length >= 5;
    }

    /// <summary>The words of a channel name: lower case, letters folded to ASCII (æ → ae, ø → o), "+" as "plus".</summary>
    public static List<string> Words(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var text = name.ToLowerInvariant()
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("+", " plus ", StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal);

        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : ' ');
        }

        return folded.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Translations.TryGetValue(w, out var t) ? t : w)
            .ToList();
    }

    /// <summary>
    /// The words of a logo file name without the extension and the country code ("tv3-plus-dk.png" → tv3, plus), or
    /// null for a style variant ("-icon", "-light", "-hz").
    /// </summary>
    public static List<string>? StemWords(string fileName)
    {
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var words = fileName[..^4].ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && (words[^1].Length == 2 || words[^1] == "int"))
        {
            words.RemoveAt(words.Count - 1); // Country code.
        }

        return words.Count == 0 || words.Any(VariantWords.Contains) ? null : words;
    }
}

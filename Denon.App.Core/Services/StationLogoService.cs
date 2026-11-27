using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Denon.App.Core.Infrastructure;

namespace Denon.App.Core.Services;

/// <summary>A station in TuneIn's public directory (one search result).</summary>
public sealed record TuneInStation(string Name, string ImageUrl, string GuideId);

/// <summary>
/// Logos for the saved stations. The receiver gives no picture for a station that this app starts from a stream
/// address (HEOS reports "Url Stream" with an empty image), so the logo comes from TuneIn's public directory: one
/// search by the station's name, once per station. The logo's address is kept in <c>station-logos.json</c>; later
/// starts ask nothing. Only the station's name goes to TuneIn.
/// </summary>
public sealed class StationLogoService
{
    private const string SearchUrl = "https://opml.radiotime.com/Search.ashx";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppLogStore _logStore;
    private readonly HttpClient _httpClient;
    private readonly string _cacheFile;
    private readonly object _sync = new();
    private readonly Dictionary<string, StoredLogo> _logos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> _lookups = new(StringComparer.Ordinal);
    private bool _loaded;

    public StationLogoService(AppLogStore logStore)
        : this(logStore, handler: null, cacheFile: null)
    {
    }

    internal StationLogoService(AppLogStore logStore, HttpMessageHandler? handler, string? cacheFile)
    {
        _logStore = logStore;
        _cacheFile = cacheFile ?? Path.Combine(AppDataPaths.RootDirectory, "station-logos.json");
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop/2.0");
    }

    /// <summary>A lookup found a logo for this station name. Raised on a thread-pool thread.</summary>
    public event Action<string>? LogoFound;

    /// <summary>The logo of a station from the file on this PC, or null. Never asks the network.</summary>
    public string? TryGet(string? stationName)
    {
        var key = Normalize(stationName);
        if (key.Length == 0)
        {
            return null;
        }

        lock (_sync)
        {
            EnsureLoaded();
            return _logos.TryGetValue(key, out var logo) ? logo.LogoUrl : null;
        }
    }

    /// <summary>
    /// The logo of a station: from the file, or from one search in TuneIn's directory. A name is searched at most
    /// once per start of the app; a station that TuneIn does not know stays without a logo.
    /// </summary>
    public Task<string?> EnsureAsync(string? stationName, CancellationToken cancellationToken = default)
    {
        var key = Normalize(stationName);
        if (key.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        lock (_sync)
        {
            EnsureLoaded();
            if (_logos.TryGetValue(key, out var known))
            {
                return Task.FromResult<string?>(known.LogoUrl);
            }

            if (!_lookups.TryGetValue(key, out var lookup))
            {
                lookup = LookUpAsync(stationName!.Trim(), key, cancellationToken);
                _lookups[key] = lookup;
            }

            return lookup;
        }
    }

    private async Task<string?> LookUpAsync(string stationName, string key, CancellationToken cancellationToken)
    {
        try
        {
            // The search text has no brackets: "Radio Paradise (Main)" is "Radio Paradise" in the directory.
            var query = StripBrackets(stationName);
            var url = $"{SearchUrl}?query={Uri.EscapeDataString(query)}&types=station&render=json";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = PickMatch(stationName, ParseSearch(json));
            if (match is null)
            {
                _logStore.Info($"Station logo: TuneIn has no station that matches \"{stationName}\".");
                return null;
            }

            var logoUrl = await LargestThatLoadsAsync(match.ImageUrl, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _logos[key] = new StoredLogo(stationName, logoUrl, $"TuneIn {match.GuideId} ({match.Name})");
                Save();
            }

            _logStore.Info($"Station logo: {stationName} uses the logo of \"{match.Name}\" from TuneIn ({match.GuideId}).");
            LogoFound?.Invoke(stationName);
            return logoUrl;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _logStore.Warning($"Station logo: the search for \"{stationName}\" failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>The 300 px logo when the directory has it, else the 145 px logo the search gave.</summary>
    private async Task<string> LargestThatLoadsAsync(string imageUrl, CancellationToken cancellationToken)
    {
        var larger = LargerLogo(imageUrl);
        if (larger == imageUrl)
        {
            return imageUrl;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, larger);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? larger : imageUrl;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return imageUrl;
        }
    }

    /// <summary>TuneIn names its sizes by one letter: "logoq.png" is 145 px, "logod.png" is 300 px.</summary>
    internal static string LargerLogo(string imageUrl)
    {
        const string small = "/images/logoq.";
        var at = imageUrl.IndexOf(small, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? imageUrl : string.Concat(imageUrl.AsSpan(0, at), "/images/logod.", imageUrl.AsSpan(at + small.Length));
    }

    /// <summary>The stations with a picture in a TuneIn search answer (also those inside a group).</summary>
    internal static IReadOnlyList<TuneInStation> ParseSearch(string json)
    {
        var stations = new List<TuneInStation>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("body", out var body))
        {
            Collect(body, stations);
        }

        return stations;

        static void Collect(JsonElement outlines, List<TuneInStation> into)
        {
            if (outlines.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var outline in outlines.EnumerateArray())
            {
                if (outline.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Text(outline, "item") == "station" && Text(outline, "text") is { Length: > 0 } name &&
                    Text(outline, "image") is { Length: > 0 } image &&
                    (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    into.Add(new TuneInStation(name, image, Text(outline, "guide_id") ?? string.Empty));
                }

                if (outline.TryGetProperty("children", out var children))
                {
                    Collect(children, into);
                }
            }
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }

    /// <summary>
    /// The search result that is this station: the same name first; else a result whose words are all in the
    /// station's name, or the other way (2 words or more: "SomaFM Groove Salad" and "Groove Salad"). Else null:
    /// no logo is better than the logo of another station.
    /// </summary>
    internal static TuneInStation? PickMatch(string stationName, IReadOnlyList<TuneInStation> results)
    {
        var wanted = Normalize(stationName);
        if (wanted.Length == 0)
        {
            return null;
        }

        var same = results.FirstOrDefault(r => Normalize(r.Name) == wanted);
        if (same is not null)
        {
            return same;
        }

        var wantedWords = wanted.Split(' ');
        foreach (var result in results)
        {
            var words = Normalize(result.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shared = Math.Min(words.Length, wantedWords.Length);
            if (shared >= 2 && (words.All(wantedWords.Contains) || wantedWords.All(words.Contains)))
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// A name for comparing: lower case, no text in brackets, Nordic and accented letters as plain letters
    /// ("København" and "Kobenhavn" are the same), only letters and digits, single spaces.
    /// </summary>
    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = StripBrackets(name).ToLowerInvariant()
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("å", "a", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StripBrackets(string name)
    {
        var builder = new StringBuilder(name.Length);
        var depth = 0;
        foreach (var c in name)
        {
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(_cacheFile) &&
                JsonSerializer.Deserialize<LogoFile>(File.ReadAllText(_cacheFile), JsonOptions) is { Logos: { } logos })
            {
                foreach (var (key, logo) in logos)
                {
                    if (!string.IsNullOrWhiteSpace(logo.LogoUrl))
                    {
                        _logos[key] = logo;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logStore.Warning($"Station logos: could not read {_cacheFile}: {exception.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new LogoFile(1, new SortedDictionary<string, StoredLogo>(_logos, StringComparer.Ordinal)), JsonOptions));
            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logStore.Warning($"Station logos: could not write {_cacheFile}: {exception.Message}");
        }
    }

    private sealed record StoredLogo(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("logoUrl")] string LogoUrl,
        [property: JsonPropertyName("source")] string Source);

    private sealed record LogoFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("logos")] IDictionary<string, StoredLogo> Logos);
}

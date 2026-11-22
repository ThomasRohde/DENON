using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class RadioBrowserService
{
    private static readonly string[] MirrorHosts =
    [
        "https://de1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://at1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
    ];

    private readonly HttpClient _httpClient;
    private readonly AppLogStore _logStore;

    public RadioBrowserService(AppLogStore logStore)
    {
        _logStore = logStore;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop/2.0");
    }

    public async Task<IReadOnlyList<RadioBrowserStation>> SearchStationsAsync(
        string? nameQuery,
        string? tag,
        string? country,
        bool hiResOnly,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var fetchLimit = hiResOnly ? Math.Clamp(limit * 3, 30, 120) : Math.Clamp(limit, 5, 60);
        var queryParams = new List<string>
        {
            "hidebroken=true",
            "order=votes",
            "reverse=true",
            $"limit={fetchLimit}",
        };

        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            queryParams.Add($"name={Uri.EscapeDataString(nameQuery.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            queryParams.Add($"tag={Uri.EscapeDataString(tag.Trim().ToLowerInvariant())}");
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            queryParams.Add($"country={Uri.EscapeDataString(country.Trim())}");
        }

        var path = $"/json/stations/search?{string.Join('&', queryParams)}";
        Exception? lastError = null;

        foreach (var host in MirrorHosts)
        {
            try
            {
                var items = await _httpClient.GetFromJsonAsync<List<RadioBrowserDto>>(host + path, cancellationToken) ?? [];
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var results = new List<RadioBrowserStation>();

                foreach (var dto in items)
                {
                    var url = string.IsNullOrWhiteSpace(dto.UrlResolved) ? dto.Url : dto.UrlResolved;
                    if (string.IsNullOrWhiteSpace(url) ||
                        (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                         !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (!seenUrls.Add(url.Trim()))
                    {
                        continue;
                    }

                    var title = string.IsNullOrWhiteSpace(dto.Name) ? "Internet Radio" : dto.Name.Trim();
                    results.Add(new RadioBrowserStation(
                        StationUuid: dto.StationUuid ?? Guid.NewGuid().ToString("N"),
                        Name: title,
                        UrlResolved: url.Trim(),
                        Favicon: dto.Favicon?.Trim() ?? string.Empty,
                        Country: dto.Country?.Trim() ?? string.Empty,
                        Tags: dto.Tags?.Trim() ?? string.Empty,
                        Codec: dto.Codec?.Trim() ?? string.Empty,
                        Bitrate: dto.Bitrate,
                        Votes: dto.Votes));
                }

                if (hiResOnly)
                {
                    results = FilterAndSortHiRes(results, limit);
                }

                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        _logStore.Warning($"Radio Browser lookup failed across mirrors: {lastError?.Message}");
        throw new InvalidOperationException($"Radio Browser API unreachable: {lastError?.Message}", lastError);
    }

    /// <summary>
    /// Honors the exact "FLAC / 256k+" union: includes FLAC streams even when their reported bitrate is 0/unknown.
    /// </summary>
    internal static List<RadioBrowserStation> FilterAndSortHiRes(IEnumerable<RadioBrowserStation> stations, int limit) =>
        stations
            .Where(static s => IsHiResStation(s.Codec, s.Bitrate))
            .OrderByDescending(static s => s.Codec.Equals("FLAC", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static s => s.Bitrate)
            .ThenByDescending(static s => s.Votes)
            .Take(Math.Clamp(limit, 5, 60))
            .ToList();

    internal static bool IsHiResStation(string? codec, int bitrate) =>
        string.Equals(codec?.Trim(), "FLAC", StringComparison.OrdinalIgnoreCase) || bitrate >= 256;

    private sealed class RadioBrowserDto
    {
        [JsonPropertyName("stationuuid")]
        public string? StationUuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("url_resolved")]
        public string? UrlResolved { get; set; }

        [JsonPropertyName("favicon")]
        public string? Favicon { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("tags")]
        public string? Tags { get; set; }

        [JsonPropertyName("codec")]
        public string? Codec { get; set; }

        [JsonPropertyName("bitrate")]
        public int Bitrate { get; set; }

        [JsonPropertyName("votes")]
        public int Votes { get; set; }
    }
}

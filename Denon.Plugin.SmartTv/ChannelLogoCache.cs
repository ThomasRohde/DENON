using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Channel logos for the now-playing hero and Insights, from the open tv-logos collection on GitHub
/// (https://github.com/tv-logo/tv-logos). For privacy the logos of the whole channel list are fetched in one go (a
/// server never sees which channel plays), saved in the plugin data folder and used from there. The folder lists are
/// read again after a week. Logos are trademarks of their owners and only shown on this PC.
/// </summary>
public sealed class ChannelLogoCache
{
    public const string FolderName = "channel-logos";
    private const string Repository = "tv-logo/tv-logos";
    private const string Branch = "main";
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailedRetry = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly Func<HttpClient> _httpFactory;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, string> _logoByChannel = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastEnsuredKey;
    private DateTimeOffset _lastEnsuredUtc;

    public ChannelLogoCache(string pluginDataDirectory, Func<HttpClient> httpFactory, Action<string> info, Action<string> warn)
    {
        _directory = Path.Combine(pluginDataDirectory, FolderName);
        _httpFactory = httpFactory;
        _info = info;
        _warn = warn;
        LoadIndex();
    }

    public string Directory => _directory;

    /// <summary>The cached logo file for a channel, or null (not matched, not downloaded yet, or turned off).</summary>
    public string? TryGetLogoPath(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        string? file;
        lock (_sync)
        {
            _logoByChannel.TryGetValue(channelName.Trim(), out file);
        }

        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        var path = Path.Combine(_directory, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Matches every channel to a logo and downloads the missing ones. Runs at most once per channel list and
    /// overrides, unless the folder lists are older than a week. Never throws for network trouble (it logs it).
    /// </summary>
    public async Task EnsureAsync(IReadOnlyList<string> channelNames, IReadOnlyDictionary<string, string> overrides, CancellationToken ct)
    {
        var names = channelNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        var ensureKey = string.Join("\n", names.Order(StringComparer.OrdinalIgnoreCase)) + "\n--\n" +
                        string.Join("\n", overrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => $"{o.Key}={o.Value}"));

        await _gate.WaitAsync(ct);
        try
        {
            var index = ReadIndex() ?? new LogoIndex();
            var folders = FoldersForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
            var catalogFresh = index.ListedAtUtc is { } listed &&
                               DateTimeOffset.UtcNow - listed < CatalogMaxAge &&
                               index.Folders.SequenceEqual(folders);
            if (catalogFresh && ensureKey == _lastEnsuredKey && DateTimeOffset.UtcNow - _lastEnsuredUtc < FailedRetry)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(_directory);
            using var http = _httpFactory();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DenonDesktop-SmartTv/1.0");

            if (!catalogFresh)
            {
                var catalog = await ListFoldersAsync(http, folders, ct);
                if (catalog.Count > 0)
                {
                    index.Catalog = catalog.Select(f => new IndexFile { Folder = f.Folder, Name = f.FileName }).ToList();
                    index.Folders = folders.ToList();
                    index.ListedAtUtc = DateTimeOffset.UtcNow;
                }
                else if (index.Catalog.Count == 0)
                {
                    _warn("Channel logos: could not read the tv-logos folder lists; trying again later.");
                    return;
                }
            }

            var catalogFiles = index.Catalog.Select(f => new ChannelLogoMatcher.LogoFile(f.Folder, f.Name)).ToList();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int matched = 0, downloaded = 0, failed = 0, pinned = 0;
            var misses = new List<string>();

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                if (overrides.TryGetValue(name, out var pin) && !string.IsNullOrWhiteSpace(pin))
                {
                    pinned++;
                    if (await PinAsync(http, pin.Trim(), ct) is { } pinnedFile)
                    {
                        map[name] = pinnedFile;
                    }

                    continue;
                }

                if (ChannelLogoMatcher.Match(name, catalogFiles) is not { } logo)
                {
                    misses.Add(name);
                    continue;
                }

                matched++;
                var local = LocalName(logo);
                if (!File.Exists(Path.Combine(_directory, local)))
                {
                    var url = $"https://raw.githubusercontent.com/{Repository}/{Branch}/{Uri.EscapeDataString(logo.Folder).Replace("%2F", "/", StringComparison.Ordinal)}/{Uri.EscapeDataString(logo.FileName)}";
                    if (await DownloadAsync(http, url, local, ct))
                    {
                        downloaded++;
                    }
                    else
                    {
                        failed++;
                        continue;
                    }
                }

                map[name] = local;
            }

            index.Channels = map;
            WriteIndex(index);
            lock (_sync)
            {
                _logoByChannel = map;
            }

            _lastEnsuredKey = failed == 0 ? ensureKey : null;
            _lastEnsuredUtc = DateTimeOffset.UtcNow;
            _info($"Channel logos: {matched} of {names.Count - pinned} channels matched in tv-logos ({string.Join(", ", folders)}), " +
                  $"{downloaded} downloaded, {failed} failed, {pinned} pinned." +
                  (misses.Count > 0 ? $" No logo: {string.Join(", ", misses.Take(20))}{(misses.Count > 20 ? ", …" : "")}." : ""));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warn($"Channel logos: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The tv-logos folders to search, best first: the country's own folder, "international", then the countries whose
    /// channels its TV lists most often carry.
    /// </summary>
    public static IReadOnlyList<string> FoldersForRegion(string isoRegion)
    {
        static string Folder(string name) => name switch
        {
            "denmark" or "sweden" or "norway" or "finland" or "iceland" => $"countries/nordic/{name}",
            _ => $"countries/{name}",
        };

        var region = isoRegion.ToUpperInvariant();
        string own;
        try
        {
            own = region switch
            {
                "DK" => "denmark",
                "SE" => "sweden",
                "NO" => "norway",
                "FI" => "finland",
                "IS" => "iceland",
                "GB" => "united-kingdom",
                "US" => "united-states",
                _ => new RegionInfo(region).EnglishName.ToLowerInvariant().Replace(' ', '-'),
            };
        }
        catch (ArgumentException)
        {
            own = "united-kingdom";
        }

        var neighbours = region switch
        {
            "DK" => new[] { "sweden", "norway", "germany", "united-kingdom" },
            "SE" => ["norway", "denmark", "finland", "united-kingdom"],
            "NO" => ["sweden", "denmark", "united-kingdom"],
            "FI" => ["sweden", "united-kingdom"],
            "DE" => ["austria", "switzerland", "united-kingdom"],
            "AT" => ["germany", "switzerland", "united-kingdom"],
            "CH" => ["germany", "austria", "france", "italy"],
            _ => ["united-kingdom", "united-states"],
        };

        return new[] { Folder(own), "countries/international" }
            .Concat(neighbours.Select(Folder))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<ChannelLogoMatcher.LogoFile>> ListFoldersAsync(HttpClient http, IReadOnlyList<string> folders, CancellationToken ct)
    {
        var files = new List<ChannelLogoMatcher.LogoFile>();
        foreach (var folder in folders)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/contents/{folder}?ref={Branch}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _warn($"Channel logos: listing {folder} gave HTTP {(int)response.StatusCode}.");
                    continue;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "file" &&
                        item.TryGetProperty("name", out var name) && name.GetString() is { } fileName &&
                        fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(new ChannelLogoMatcher.LogoFile(folder, fileName));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _warn($"Channel logos: listing {folder} failed: {ex.Message}");
            }
        }

        return files;
    }

    /// <summary>A pinned logo: an https address (downloaded once) or a local image (copied in); "none" shows no logo.</summary>
    private async Task<string?> PinAsync(HttpClient http, string pin, CancellationToken ct)
    {
        if (pin.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin)))[..12].ToLowerInvariant();
        if (Uri.TryCreate(pin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            var local = $"pinned-{hash}{SafeExtension(uri.AbsolutePath)}";
            return File.Exists(Path.Combine(_directory, local)) || await DownloadAsync(http, uri.AbsoluteUri, local, ct) ? local : null;
        }

        if (File.Exists(pin))
        {
            var local = $"pinned-{hash}{SafeExtension(pin)}";
            File.Copy(pin, Path.Combine(_directory, local), overwrite: true);
            return local;
        }

        _warn($"Channel logos: the pinned logo \"{pin}\" is not an https address or an existing file.");
        return null;
    }

    private async Task<bool> DownloadAsync(HttpClient http, string url, string localName, CancellationToken ct)
    {
        var target = Path.Combine(_directory, localName);
        var temp = target + ".part";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _warn($"Channel logos: {url} gave HTTP {(int)response.StatusCode}.");
                return false;
            }

            if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            {
                _warn($"Channel logos: {url} is larger than 4 MB; skipped.");
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is 0 or > 4 * 1024 * 1024 || !LooksLikeImage(bytes))
            {
                _warn($"Channel logos: {url} is not a PNG, JPEG or WebP image; skipped.");
                return false;
            }

            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _warn($"Channel logos: downloading {url} failed: {ex.Message}");
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            return false;
        }
    }

    private static bool LooksLikeImage(byte[] b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
        (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P');

    private static string SafeExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" ? ext : ".png";
    }

    /// <summary>The cached file name: the logo's own name, which carries its country ("dr1-dk.png").</summary>
    private static string LocalName(ChannelLogoMatcher.LogoFile logo) => logo.FileName.ToLowerInvariant();

    private void LoadIndex()
    {
        if (ReadIndex() is { } index)
        {
            _logoByChannel = new Dictionary<string, string>(index.Channels, StringComparer.OrdinalIgnoreCase);
        }
    }

    private LogoIndex? ReadIndex()
    {
        try
        {
            var path = Path.Combine(_directory, "index.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<LogoIndex>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _warn($"Channel logos: the cache index could not be read ({ex.Message}); it is built again.");
            return null;
        }
    }

    private void WriteIndex(LogoIndex index)
    {
        var path = Path.Combine(_directory, "index.json");
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(index, Json));
        File.Move(path + ".part", path, overwrite: true);
    }

    private sealed class LogoIndex
    {
        public string Source { get; set; } = $"https://github.com/{Repository}";

        public DateTimeOffset? ListedAtUtc { get; set; }

        public List<string> Folders { get; set; } = [];

        public List<IndexFile> Catalog { get; set; } = [];

        /// <summary>Channel name → cached file name.</summary>
        public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class IndexFile
    {
        public string Folder { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}

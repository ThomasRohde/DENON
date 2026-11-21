using System.Diagnostics;
using Denon.App.Core.Models;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Denon.App.Core.Services;

public sealed class YouTubeMediaService
{
    private readonly YoutubeClient _client;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private readonly Dictionary<string, (StreamManifest Manifest, DateTimeOffset ExpiresAt)> _manifestCache = new(StringComparer.Ordinal);

    public YouTubeMediaService(AppSettingsStore settingsStore, AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _logStore = logStore;
        _client = new YoutubeClient();
    }

    public async Task<YouTubeSearchResponse> SearchOrResolveAsync(
        string queryOrUrl,
        YouTubeSearchMode mode,
        int maxResults = 16,
        CancellationToken cancellationToken = default)
    {
        var input = (queryOrUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return new YouTubeSearchResponse([], [], "Enter a song, artist, YouTube URL, or playlist link.");
        }

        // Only parse direct Video/Playlist IDs when the user actually entered a YouTube URL.
        // Plain single-word queries like "Slopcore" can otherwise be accepted by PlaylistId.TryParse!
        var isUrl = LooksLikeYouTubeUrl(input);
        var parsedVideoId = isUrl ? VideoId.TryParse(input) : null;
        var parsedPlaylistId = isUrl ? PlaylistId.TryParse(input) : null;

        if (parsedPlaylistId is { } playlistId && (parsedVideoId is null || input.Contains("/playlist", StringComparison.OrdinalIgnoreCase)))
        {
            var tracks = await LoadPlaylistTracksAsync(playlistId.Value, Math.Max(maxResults, 40), cancellationToken);
            return new YouTubeSearchResponse(
                tracks,
                [],
                tracks.Count > 0
                    ? $"Loaded {tracks.Count} tracks from YouTube playlist."
                    : "Playlist was empty or private.");
        }

        // 2. Direct Video / YouTube Music Track URL
        if (parsedVideoId is { } videoId)
        {
            var video = await _client.Videos.GetAsync(videoId, cancellationToken);
            var item = new YouTubeTrackItem(
                VideoId: video.Id.Value,
                Title: video.Title,
                Author: video.Author.ChannelTitle,
                Duration: video.Duration,
                ThumbnailUrl: SelectBestThumbnail(video.Thumbnails, video.Id.Value),
                WatchUrl: video.Url);

            // If the URL also carried a playlist (e.g. watch?v=XXX&list=YYY), load the rest too
            if (parsedPlaylistId is { } radioListId)
            {
                try
                {
                    var listTracks = await LoadPlaylistTracksAsync(radioListId.Value, 25, cancellationToken);
                    var combined = new List<YouTubeTrackItem> { item };
                    foreach (var t in listTracks)
                    {
                        if (!string.Equals(t.VideoId, item.VideoId, StringComparison.Ordinal))
                        {
                            combined.Add(t);
                        }
                    }

                    return new YouTubeSearchResponse(combined, [], $"Resolved track + {combined.Count - 1} playlist tracks.");
                }
                catch
                {
                    // Mix/radio playlists may not always be enumerable; return single video cleanly
                }
            }

            return new YouTubeSearchResponse([item], [], $"Resolved YouTube track: {item.Title}");
        }

        // 3. Search Playlists mode
        if (mode == YouTubeSearchMode.Playlists)
        {
            var playlists = new List<YouTubePlaylistSummary>();
            await foreach (var pl in _client.Search.GetPlaylistsAsync(input, cancellationToken))
            {
                playlists.Add(new YouTubePlaylistSummary(
                    PlaylistId: pl.Id.Value,
                    Title: pl.Title,
                    Author: pl.Author?.ChannelTitle ?? "YouTube Playlist",
                    ThumbnailUrl: pl.Thumbnails.OrderByDescending(static t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty,
                    PlaylistUrl: pl.Url));

                if (playlists.Count >= maxResults)
                {
                    break;
                }
            }

            return new YouTubeSearchResponse(
                [],
                playlists,
                playlists.Count > 0
                    ? $"Found {playlists.Count} YouTube playlists for \"{input}\"."
                    : $"No playlists found for \"{input}\".");
        }

        // 4. Search Music or Videos
        var effectiveQuery = mode == YouTubeSearchMode.Music &&
                             !input.Contains("audio", StringComparison.OrdinalIgnoreCase) &&
                             !input.Contains("music", StringComparison.OrdinalIgnoreCase) &&
                             !input.Contains("live", StringComparison.OrdinalIgnoreCase) &&
                             !input.Contains("remix", StringComparison.OrdinalIgnoreCase)
            ? $"{input} audio"
            : input;

        var results = new List<YouTubeTrackItem>();
        await foreach (var video in _client.Search.GetVideosAsync(effectiveQuery, cancellationToken))
        {
            results.Add(new YouTubeTrackItem(
                VideoId: video.Id.Value,
                Title: video.Title,
                Author: video.Author.ChannelTitle,
                Duration: video.Duration,
                ThumbnailUrl: SelectBestThumbnail(video.Thumbnails, video.Id.Value),
                WatchUrl: video.Url));

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        var modeLabel = mode == YouTubeSearchMode.Music ? "YouTube Music" : "YouTube";
        return new YouTubeSearchResponse(
            results,
            [],
            results.Count > 0
                ? $"Found {results.Count} {modeLabel} results for \"{input}\"."
                : $"No results found for \"{input}\".");
    }

    public async Task<IReadOnlyList<YouTubeTrackItem>> LoadPlaylistTracksAsync(
        string playlistIdOrUrl,
        int maxTracks = 40,
        CancellationToken cancellationToken = default)
    {
        var tracks = new List<YouTubeTrackItem>();
        await foreach (var video in _client.Playlists.GetVideosAsync(playlistIdOrUrl, cancellationToken))
        {
            tracks.Add(new YouTubeTrackItem(
                VideoId: video.Id.Value,
                Title: video.Title,
                Author: video.Author.ChannelTitle,
                Duration: video.Duration,
                ThumbnailUrl: SelectBestThumbnail(video.Thumbnails, video.Id.Value),
                WatchUrl: video.Url));

            if (tracks.Count >= maxTracks)
            {
                break;
            }
        }

        return tracks;
    }

    public async Task<IStreamInfo> ResolveAudioStreamInfoAsync(
        string videoId,
        bool preferMp4Direct,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetCachedManifestAsync(videoId, cancellationToken);
        var audioOnly = manifest.GetAudioOnlyStreams().ToList();

        if (audioOnly.Count == 0)
        {
            var muxed = manifest.GetMuxedStreams().OrderByDescending(static s => s.Bitrate).FirstOrDefault();
            return muxed ?? throw new InvalidOperationException("No playable audio streams were found for this YouTube video.");
        }

        if (preferMp4Direct)
        {
            var mp4 = audioOnly
                .Where(static s => s.Container == Container.Mp4)
                .OrderByDescending(static s => s.Bitrate)
                .FirstOrDefault();

            if (mp4 is not null)
            {
                return mp4;
            }
        }

        return audioOnly.OrderByDescending(static s => s.Bitrate).First();
    }

    public ValueTask<Stream> OpenAudioStreamAsync(IStreamInfo streamInfo, CancellationToken cancellationToken = default) =>
        _client.Videos.Streams.GetAsync(streamInfo, cancellationToken);

    public string? FindFfmpegPath()
    {
        var configured = _settingsStore.Current.Media.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidatePaths =
        [
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Skip invalid PATH entry
            }
        }

        return null;
    }

    public static bool IsYouTubeUrl(string? input)
    {
        if (!LooksLikeYouTubeUrl(input))
        {
            return false;
        }

        var trimmed = input!.Trim();
        return VideoId.TryParse(trimmed) is not null || PlaylistId.TryParse(trimmed) is not null;
    }

    private static bool LooksLikeYouTubeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.Contains(' '))
        {
            return false;
        }

        var normalized = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<StreamManifest> GetCachedManifestAsync(string videoId, CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_manifestCache.TryGetValue(videoId, out var cached) && cached.ExpiresAt > now)
            {
                return cached.Manifest;
            }

            // Prune expired entries
            foreach (var key in _manifestCache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
            {
                _manifestCache.Remove(key);
            }
        }
        finally
        {
            _manifestGate.Release();
        }

        _logStore.Info($"Resolving YouTube audio stream manifest for {videoId}...");
        var manifest = await _client.Videos.Streams.GetManifestAsync(videoId, cancellationToken);

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            _manifestCache[videoId] = (manifest, DateTimeOffset.UtcNow.AddMinutes(12));
        }
        finally
        {
            _manifestGate.Release();
        }

        return manifest;
    }

    private static string SelectBestThumbnail(IReadOnlyList<Thumbnail> thumbnails, string videoId)
    {
        var medium = thumbnails
            .Where(static t => t.Resolution.Width is >= 300 and <= 640)
            .OrderByDescending(static t => t.Resolution.Area)
            .FirstOrDefault();

        if (medium is not null)
        {
            return medium.Url;
        }

        var best = thumbnails.OrderByDescending(static t => t.Resolution.Area).FirstOrDefault();
        return best?.Url ?? $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
    }
}

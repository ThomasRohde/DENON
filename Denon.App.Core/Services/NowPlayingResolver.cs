using System.Globalization;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Services;

public sealed record ResolvedNowPlaying(
    string Title,
    string Detail,
    string ImageUrl,
    string PlayStateLabel,
    bool IsExternalLiveInput,
    CastNowPlaying? Cast = null,
    bool CanPause = true,
    LiveTvShowing? LiveTv = null);

public sealed record SmartTvNowShowing(
    string ModelName,
    string Showing,
    bool IsScreenOff,
    LiveTvShowing? LiveTv = null);

/// <summary>
/// The channel and program Live TV shows, from the Smart TV plugin's status details: the channel, its TV and DVB IDs,
/// the current and next program from the TV's guide, and the cached channel logo (a file in the plugin data folder).
/// </summary>
public sealed record LiveTvShowing(
    string ChannelName,
    string? ChannelNumber,
    string? ChannelId,
    string? SignalChannelId,
    string? ProgramName,
    DateTimeOffset? ProgramStart,
    DateTimeOffset? ProgramEnd,
    string? NextProgramName,
    DateTimeOffset? NextProgramStart,
    string? LogoPath)
{
    /// <summary>The logo's file name in the plugin's channel-logos folder ("dr1-dk.png"), or null.</summary>
    public string? LogoFileName => LogoPath is null ? null : Path.GetFileName(LogoPath);

    /// <summary>"DR1 • Ch 1" (or the name alone).</summary>
    public string ChannelLabel => string.IsNullOrWhiteSpace(ChannelNumber) ? ChannelName : $"{ChannelName} • Ch {ChannelNumber}";

    /// <summary>"16:25–17:15" in local time, or null without a program.</summary>
    public string? ProgramTimes => ProgramStart is { } s && ProgramEnd is { } e
        ? $"{s.ToLocalTime():HH:mm}–{e.ToLocalTime():HH:mm}"
        : null;
}

/// <summary>
/// What a streamer plays, from its plugin's status text: a Chromecast ("chromecast" plugin) or an
/// Apple TV ("apple-tv" plugin). <see cref="PluginId"/> is the plugin that takes play/pause and artwork requests.
/// <see cref="CanPause"/> is false when the streamer explicitly reports that pause is not supported for the
/// current program (for example a live stream on Apple TV).
/// </summary>
public sealed record CastNowPlaying(
    string DeviceName,
    string App,
    string? Title,
    bool IsPlaying,
    string PluginId = NowPlayingResolver.ChromecastPluginId,
    bool CanPause = true)
{
    public bool IsAppleTv => PluginId == NowPlayingResolver.AppleTvPluginId;
}

public static class NowPlayingResolver
{
    public const string ChromecastPluginId = "chromecast";

    /// <summary>The HomePod plugin (separate download). HomePods are not on the receiver: the hero never shows them.</summary>
    public const string HomePodPluginId = "homepod";
    public const string AppleTvPluginId = "apple-tv";

    public static ResolvedNowPlaying Resolve(
        ReceiverStatus status,
        string? activeStationName,
        IReadOnlyList<PluginRecordSnapshot>? plugins,
        bool isPcCasting = false,
        string? pcCastDetail = null,
        LocalAudioTrackItem? activeLocalTrack = null,
        YouTubeTrackItem? activeYouTubeTrack = null)
    {
        var soundMode = string.IsNullOrWhiteSpace(status.SoundMode) ? "—" : status.SoundMode.Trim();

        if (!status.IsPoweredOn || !status.IsMainZoneOn)
        {
            return new ResolvedNowPlaying(
                Title: "Receiver in Standby",
                Detail: "Press Power or select any station to wake the receiver.",
                ImageUrl: string.Empty,
                PlayStateLabel: "Standby",
                IsExternalLiveInput: false);
        }

        var friendlyInput = DenonCatalog.GetFriendlyInputName(status.InputSource);
        var isNet = DenonCatalog.InputMatches(status.InputSource, "NET");
        var isPlayingOrPaused =
            string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.PlayState, "pause", StringComparison.OrdinalIgnoreCase);

        if (!isNet)
        {
            // TV Audio (ARC / eARC / Optical): enrich with live Smart TV app, channel, or labeled HDMI input!
            if (IsTvAudioInput(status.InputSource))
            {
                var tvInfo = TryExtractSmartTvNowShowing(plugins);

                // A Chromecast or Apple TV plays and the TV shows an HDMI input (or there is no TV plugin to ask):
                // show its program, not "DVD Player (HDMI 1)". Live TV and TV apps still win.
                if (TryExtractStreamerNowPlaying(plugins) is { } cast &&
                    (tvInfo is null ? cast.IsPlaying : tvInfo.Showing.Contains("HDMI", StringComparison.OrdinalIgnoreCase)))
                {
                    var kindLabel = cast.IsAppleTv ? "Apple TV" : "Cast";
                    var castDetail = new List<string> { cast.Title is null ? kindLabel : $"{kindLabel} • {cast.App}", cast.DeviceName };
                    if (tvInfo is not null)
                    {
                        castDetail.Add($"TV {tvInfo.Showing}");
                    }

                    if (soundMode != "—")
                    {
                        castDetail.Add(soundMode);
                    }

                    return new ResolvedNowPlaying(
                        Title: cast.Title ?? cast.App,
                        Detail: string.Join(" • ", castDetail),
                        ImageUrl: string.Empty,
                        PlayStateLabel: cast.IsPlaying ? (cast.IsAppleTv ? "Playing" : "Casting") : "Paused",
                        IsExternalLiveInput: true,
                        Cast: cast,
                        CanPause: cast.CanPause);
                }

                if (tvInfo is { LiveTv: { } live })
                {
                    // Live TV with the channel (and the program, from the TV's guide): the program is the title.
                    var liveParts = new List<string> { live.ChannelLabel };
                    if (live.ProgramTimes is { } times)
                    {
                        liveParts.Add(times);
                    }

                    if (tvInfo.IsScreenOff)
                    {
                        liveParts.Add("Screen Off");
                    }

                    if (soundMode != "—")
                    {
                        liveParts.Add(soundMode);
                    }

                    return new ResolvedNowPlaying(
                        Title: live.ProgramName ?? live.ChannelName,
                        Detail: string.Join(" • ", liveParts),
                        ImageUrl: live.LogoPath is { } logo ? new Uri(logo).AbsoluteUri : string.Empty,
                        PlayStateLabel: tvInfo.IsScreenOff ? "Screen Off" : "Live TV",
                        IsExternalLiveInput: true,
                        LiveTv: live);
                }

                if (tvInfo is not null)
                {
                    var detailParts = new List<string> { "TV Audio" };
                    if (!string.IsNullOrWhiteSpace(tvInfo.ModelName))
                    {
                        detailParts.Add(tvInfo.ModelName);
                    }

                    if (tvInfo.IsScreenOff)
                    {
                        detailParts.Add("Screen Off");
                    }

                    if (soundMode != "—")
                    {
                        detailParts.Add(soundMode);
                    }

                    return new ResolvedNowPlaying(
                        Title: tvInfo.Showing,
                        Detail: string.Join(" • ", detailParts),
                        ImageUrl: string.Empty,
                        PlayStateLabel: tvInfo.IsScreenOff ? "Screen Off" : "Live TV",
                        IsExternalLiveInput: true);
                }
            }

            // An Apple TV plugged straight into the receiver (for example "Media Player"): show its program
            // unless the input carries its own track metadata (Bluetooth / USB).
            var hasInputMetadata = isPlayingOrPaused && status.NowPlaying.HasRealMetadata;
            if (!IsTvAudioInput(status.InputSource) && !hasInputMetadata && TryExtractAppleTvNowPlaying(plugins) is { } appleTv)
            {
                var atvDetail = new List<string> { appleTv.Title is null ? "Apple TV" : $"Apple TV • {appleTv.App}", appleTv.DeviceName, friendlyInput };
                if (soundMode != "—")
                {
                    atvDetail.Add(soundMode);
                }

                return new ResolvedNowPlaying(
                    Title: appleTv.Title ?? appleTv.App,
                    Detail: string.Join(" • ", atvDetail),
                    ImageUrl: string.Empty,
                    PlayStateLabel: appleTv.IsPlaying ? "Playing" : "Paused",
                    IsExternalLiveInput: true,
                    Cast: appleTv,
                    CanPause: appleTv.CanPause);
            }

            // Bluetooth / USB / external AVR inputs that still carry live HEOS/AVRCP track metadata
            if (hasInputMetadata)
            {
                var extTitle = FirstNonEmpty(status.NowPlaying.Song, status.NowPlaying.Station, friendlyInput);
                var extDetail = BuildMetadataDetail(status.NowPlaying, activeStationName: null, serviceLabel: friendlyInput, fallback: $"External Input • {soundMode}");
                return new ResolvedNowPlaying(
                    Title: extTitle,
                    Detail: extDetail,
                    ImageUrl: status.NowPlaying.ImageUrl ?? string.Empty,
                    PlayStateLabel: ToTitleCase(status.PlayState),
                    IsExternalLiveInput: false);
            }

            return new ResolvedNowPlaying(
                Title: friendlyInput,
                Detail: $"External Input • Sound Mode: {soundMode}",
                ImageUrl: string.Empty,
                PlayStateLabel: "Active",
                IsExternalLiveInput: true);
        }

        // Network / HEOS input: only show local desktop overlays when HEOS isn't actively playing an external phone/HEOS service
        var externalHeosPlaying =
            string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase) &&
            status.NowPlaying.HasRealMetadata;
        var isExternalNamedService = status.NowPlaying.IsNamedServiceSource;

        if (isPcCasting && !externalHeosPlaying)
        {
            return new ResolvedNowPlaying(
                Title: "Windows PC Audio (Live Cast)",
                Detail: string.IsNullOrWhiteSpace(pcCastDetail) ? "Live PC Audio Relay" : pcCastDetail,
                ImageUrl: string.Empty,
                PlayStateLabel: "Live Cast",
                IsExternalLiveInput: false);
        }

        if (activeLocalTrack is not null && !isExternalNamedService)
        {
            var localTrackOverridden =
                externalHeosPlaying &&
                !string.IsNullOrWhiteSpace(status.NowPlaying.Song) &&
                !status.NowPlaying.Song.Contains(activeLocalTrack.Title, StringComparison.OrdinalIgnoreCase) &&
                !activeLocalTrack.Title.Contains(status.NowPlaying.Song, StringComparison.OrdinalIgnoreCase);

            if (!localTrackOverridden)
            {
                return new ResolvedNowPlaying(
                    Title: activeLocalTrack.Title,
                    Detail: $"{activeLocalTrack.Artist} • Local {activeLocalTrack.CodecBadge} ({activeLocalTrack.SizeDisplay})",
                    ImageUrl: string.Empty,
                    PlayStateLabel: ToTitleCase(status.PlayState),
                    IsExternalLiveInput: false);
            }
        }

        if (activeYouTubeTrack is not null && !externalHeosPlaying)
        {
            return new ResolvedNowPlaying(
                Title: activeYouTubeTrack.Title,
                Detail: $"{activeYouTubeTrack.Author} • YouTube Music • {activeYouTubeTrack.DurationDisplay}",
                ImageUrl: activeYouTubeTrack.ThumbnailUrl ?? string.Empty,
                PlayStateLabel: ToTitleCase(status.PlayState),
                IsExternalLiveInput: false);
        }

        // Native HEOS / Phone app / Spotify Connect / AirPlay / TuneIn / Tidal / Internet Radio
        var isStopped = string.Equals(status.PlayState, "stop", StringComparison.OrdinalIgnoreCase);
        if (isStopped && (status.NowPlaying.IsPlaceholderUrlStream || !status.NowPlaying.HasRealMetadata))
        {
            return new ResolvedNowPlaying(
                Title: FirstNonEmpty(activeStationName, "HEOS Music"),
                Detail: "Ready • Play a radio station, YouTube track, or cast from your phone",
                ImageUrl: string.Empty,
                PlayStateLabel: "Stopped",
                IsExternalLiveInput: false);
        }

        var sourceName = DenonCatalog.GetHeosSourceName(status.NowPlaying.SourceId);
        if (string.Equals(sourceName, "Network Stream", StringComparison.OrdinalIgnoreCase))
        {
            sourceName = null;
        }

        var songTitle = status.NowPlaying.IsPlaceholderUrlStream ? string.Empty : status.NowPlaying.Song;
        var resolvedTitle = FirstNonEmpty(songTitle, status.NowPlaying.Station, activeStationName, sourceName, "HEOS Stream");
        var resolvedDetail = BuildMetadataDetail(
            status.NowPlaying,
            activeStationName,
            serviceLabel: sourceName,
            fallback: "HEOS Network Audio Stream",
            excludeTitle: resolvedTitle);

        return new ResolvedNowPlaying(
            Title: resolvedTitle,
            Detail: resolvedDetail,
            ImageUrl: status.NowPlaying.ImageUrl ?? string.Empty,
            PlayStateLabel: ToTitleCase(status.PlayState),
            IsExternalLiveInput: false);
    }

    public static bool IsTvAudioInput(string? inputSource)
    {
        if (string.IsNullOrWhiteSpace(inputSource))
        {
            return false;
        }

        var normalized = inputSource.Trim().ToUpperInvariant();
        return normalized is "TV" or "TV AUDIO" or "ARC" or "EARC";
    }

    public static SmartTvNowShowing? TryExtractSmartTvNowShowing(IReadOnlyList<PluginRecordSnapshot>? plugins)
    {
        if (plugins is null || plugins.Count == 0)
        {
            return null;
        }

        var tv = plugins.FirstOrDefault(static p =>
            p.Id.Equals("smart-tv", StringComparison.OrdinalIgnoreCase) &&
            p.IsEnabled &&
            p.IsStarted &&
            p.Health == PluginHealthState.Connected);

        if (tv is null || string.IsNullOrWhiteSpace(tv.StatusText))
        {
            return null;
        }

        // Format from SmartTvPlugin:
        // "LIVE • 55UR73006LA • DVD Player (HDMI 1)"
        // "LIVE • 55UR73006LA • Live TV (Ch 1 • DR1) • Screen Off"
        // "Demo • HDMI 2 • Denon AVR (eARC) • HDMI ARC / eARC"
        var raw = tv.StatusText.Trim();
        var isScreenOff = raw.Contains("Screen Off", StringComparison.OrdinalIgnoreCase);
        var parts = raw.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("LIVE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = parts[1];
        var showingParts = parts
            .Skip(2)
            .Where(static p => !p.Equals("Screen Off", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var showing = string.Join(" • ", showingParts);
        if (string.IsNullOrWhiteSpace(showing) ||
            showing.Equals("TV Standby / Offline", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SmartTvNowShowing(model, showing, isScreenOff, TryReadLiveTv(tv.StatusDetails));
    }

    /// <summary>
    /// The Live TV channel and program from the Smart TV plugin's status details (kind "live_tv"), or null. A logo path
    /// counts only inside <paramref name="smartTvDataDirectory"/> (default: the plugin's data folder).
    /// </summary>
    public static LiveTvShowing? TryReadLiveTv(IReadOnlyDictionary<string, string>? details, string? smartTvDataDirectory = null)
    {
        if (details is null || !details.TryGetValue("kind", out var kind) || kind != "live_tv" ||
            Get("channelName") is not { } channelName)
        {
            return null;
        }

        var logo = Get("channelLogo") is { } path &&
                   Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                   IsFileInside(uri, smartTvDataDirectory ?? AppDataPaths.GetPluginDataDirectory("smart-tv")) &&
                   File.Exists(uri.LocalPath)
            ? uri.LocalPath
            : null;

        return new LiveTvShowing(
            channelName,
            Get("channelNumber"),
            Get("channelId"),
            Get("signalChannelId"),
            Get("programName"),
            Time("programStart"),
            Time("programEnd"),
            Get("nextProgramName"),
            Time("nextProgramStart"),
            logo);

        string? Get(string key) => details.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        DateTimeOffset? Time(string key) =>
            Get(key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    /// <summary>
    /// Reads the Chromecast plugin status: "CASTING • &lt;device&gt; • &lt;app&gt;[ • &lt;title&gt;]" or "CAST PAUSED • …".
    /// Null when the plugin is missing, stopped, or nothing is cast.
    /// </summary>
    public static CastNowPlaying? TryExtractCastNowPlaying(IReadOnlyList<PluginRecordSnapshot>? plugins) =>
        TryParseStreamerStatus(plugins, ChromecastPluginId, "CASTING", "CAST PAUSED");

    /// <summary>
    /// Reads the Apple TV plugin status: "APPLE TV • &lt;device&gt; • &lt;app&gt;[ • &lt;title&gt;]",
    /// "APPLE TV LIVE • …" (playing, pause unsupported), or "APPLE TV PAUSED • …".
    /// Null when the plugin is missing, stopped, or the Apple TV shows nothing.
    /// </summary>
    public static CastNowPlaying? TryExtractAppleTvNowPlaying(IReadOnlyList<PluginRecordSnapshot>? plugins) =>
        TryParseStreamerStatus(plugins, AppleTvPluginId, "APPLE TV", "APPLE TV PAUSED", noPausePlayingTag: "APPLE TV LIVE");

    /// <summary>The Chromecast or Apple TV program for the hero: one that plays wins over a paused one; Chromecast first on a tie.</summary>
    public static CastNowPlaying? TryExtractStreamerNowPlaying(IReadOnlyList<PluginRecordSnapshot>? plugins)
    {
        var cast = TryExtractCastNowPlaying(plugins);
        var appleTv = TryExtractAppleTvNowPlaying(plugins);
        return cast is { IsPlaying: true } ? cast
            : appleTv is { IsPlaying: true } ? appleTv
            : cast ?? appleTv;
    }

    private static CastNowPlaying? TryParseStreamerStatus(
        IReadOnlyList<PluginRecordSnapshot>? plugins,
        string pluginId,
        string playingTag,
        string pausedTag,
        string? noPausePlayingTag = null)
    {
        var plugin = plugins?.FirstOrDefault(p =>
            p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled && p.IsStarted);
        if (plugin is null || string.IsNullOrWhiteSpace(plugin.StatusText))
        {
            return null;
        }

        var parts = plugin.StatusText.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        bool isPlaying;
        bool canPause;
        if (parts[0] == playingTag)
        {
            isPlaying = true;
            canPause = true;
        }
        else if (noPausePlayingTag is not null && parts[0] == noPausePlayingTag)
        {
            isPlaying = true;
            canPause = false;
        }
        else if (parts[0] == pausedTag)
        {
            isPlaying = false;
            canPause = true;
        }
        else
        {
            return null;
        }

        var title = parts.Length > 3 ? string.Join(" • ", parts.Skip(3)) : null;
        return new CastNowPlaying(
            parts[1],
            parts[2],
            string.IsNullOrWhiteSpace(title) ? null : title,
            isPlaying,
            pluginId,
            canPause);
    }

    /// <summary>
    /// Reads <c>"canPause"</c> from a streamer plugin's <c>now-playing</c> JSON data, or null when omitted/invalid.
    /// </summary>
    public static bool? TryReadCastCanPause(string? nowPlayingDataJson)
    {
        if (string.IsNullOrWhiteSpace(nowPlayingDataJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(nowPlayingDataJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("canPause", out var value) &&
                value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// The artwork URL from a streamer plugin's <c>now-playing</c> action data (<c>"imageUrl"</c>).
    /// Absolute http(s) URLs, and local files inside <paramref name="pluginDataDirectory"/> (the Apple TV plugin
    /// saves the artwork it fetches there); null otherwise.
    /// </summary>
    public static string? TryReadCastImageUrl(string? nowPlayingDataJson, string? pluginDataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(nowPlayingDataJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(nowPlayingDataJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("imageUrl", out var image) &&
                image.ValueKind == System.Text.Json.JsonValueKind.String &&
                Uri.TryCreate(image.GetString(), UriKind.Absolute, out var uri) &&
                (uri.Scheme is "http" or "https" || IsFileInside(uri, pluginDataDirectory)))
            {
                return uri.AbsoluteUri;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    private static bool IsFileInside(Uri uri, string? directory)
    {
        if (!uri.IsFile || uri.IsUnc || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(uri.LocalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMetadataDetail(
        NowPlayingMedia nowPlaying,
        string? activeStationName,
        string? serviceLabel,
        string fallback,
        string? excludeTitle = null)
    {
        var pieces = new List<string>();

        void AddIfDistinct(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var trimmed = candidate.Trim();
            if (string.Equals(trimmed, "Url Stream", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(excludeTitle) &&
                string.Equals(trimmed, excludeTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!pieces.Any(p => p.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                pieces.Add(trimmed);
            }
        }

        AddIfDistinct(nowPlaying.Artist);
        AddIfDistinct(nowPlaying.Album);
        AddIfDistinct(nowPlaying.Station);
        AddIfDistinct(activeStationName);
        AddIfDistinct(serviceLabel);

        if (pieces.Count == 0 && !string.IsNullOrWhiteSpace(nowPlaying.Type))
        {
            pieces.Add(ToTitleCase(nowPlaying.Type));
        }

        return pieces.Count == 0 ? fallback : string.Join(" • ", pieces);
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static string ToTitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var trimmed = value.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }
}

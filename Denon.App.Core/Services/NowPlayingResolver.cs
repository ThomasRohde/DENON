using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Services;

public sealed record ResolvedNowPlaying(
    string Title,
    string Detail,
    string ImageUrl,
    string PlayStateLabel,
    bool IsExternalLiveInput);

public sealed record SmartTvNowShowing(
    string ModelName,
    string Showing,
    bool IsScreenOff);

public static class NowPlayingResolver
{
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

            // Bluetooth / USB / external AVR inputs that still carry live HEOS/AVRCP track metadata
            if (isPlayingOrPaused && status.NowPlaying.HasRealMetadata)
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
        var isExternalNamedService =
            status.NowPlaying.SourceId is int sid && sid != 1029 && sid != 0;

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

        return new SmartTvNowShowing(model, showing, isScreenOff);
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

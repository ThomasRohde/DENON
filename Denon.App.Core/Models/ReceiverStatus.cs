using System.Globalization;

namespace Denon.App.Core.Models;

public sealed record ReceiverPlayerInfo(
    string Name,
    int PlayerId,
    string Model,
    string Version,
    string IpAddress,
    string Network,
    int LineOut,
    string Serial);

public sealed record NowPlayingMedia(
    string Type,
    string Song,
    string Album,
    string Artist,
    string ImageUrl,
    string AlbumId,
    string MediaId,
    int? QueueId,
    int? SourceId,
    string Station = "")
{
    public bool IsPlaceholderUrlStream =>
        string.Equals(Song?.Trim(), "Url Stream", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(Artist) || string.Equals(Artist.Trim(), "Url Stream", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrWhiteSpace(Station);

    /// <summary>
    /// A named HEOS service (Spotify, TuneIn, Local Music/DLNA…) plays, not a URL stream from this app.
    /// HEOS reports some app relay streams (for example a local FLAC) as sid 1024 "Local Music" with the
    /// "Url Stream" placeholder metadata, so a placeholder never counts as another service.
    /// </summary>
    public bool IsNamedServiceSource =>
        SourceId is int sid && sid != 1029 && sid != 0 && !IsPlaceholderUrlStream;

    public bool HasRealMetadata =>
        !IsPlaceholderUrlStream &&
        (!string.IsNullOrWhiteSpace(Song) ||
         !string.IsNullOrWhiteSpace(Station) ||
         !string.IsNullOrWhiteSpace(Artist));
}

public sealed record ReceiverStatus(
    ReceiverPlayerInfo Player,
    string PlayState,
    int Volume,
    bool IsMuted,
    NowPlayingMedia NowPlaying,
    DateTimeOffset RetrievedAt,
    bool IsPoweredOn = false,
    bool IsMainZoneOn = false,
    string InputSource = "",
    string SoundMode = "",
    string EcoMode = "",
    int? SleepTimerMinutes = null,
    bool IsZone2On = false,
    int MaxVolume = 98,
    double VolumeExact = 0,
    string? ActiveStationAlias = null,
    bool IsAvrConfirmed = true,
    int? Zone2Volume = null,
    string Zone2Source = "",
    bool IsZone2Muted = false);

public sealed record AvrDeviceStatus(
    bool IsPoweredOn,
    bool IsMainZoneOn,
    int Volume,
    bool IsMuted,
    string InputSource,
    string SoundMode = "",
    string EcoMode = "",
    int? SleepTimerMinutes = null,
    bool IsZone2On = false,
    int MaxVolume = 98,
    double VolumeExact = 0,
    int? Zone2Volume = null,
    string Zone2Source = "",
    bool IsZone2Muted = false);

public sealed record DiscoveredReceiver(
    string IpAddress,
    string Name,
    string Model,
    string Version,
    int Port = 1255);

public sealed record HeosFavoriteItem(
    string Name,
    string MediaId,
    string ImageUrl,
    int PresetIndex);

public sealed record StreamProbeResult(
    bool IsReachable,
    int? StatusCode,
    string? ContentType,
    string? StationName,
    string? BitrateKbps,
    string? ResolvedUrl,
    string Message);

public sealed record AvrInputOption(string Code, string DisplayName, string ShortLabel);

public sealed record AvrSoundModeOption(string Code, string DisplayName);

public static class DenonCatalog
{
    public static IReadOnlyList<AvrInputOption> InputSources { get; } =
    [
        new("NET", "HEOS Music", "HEOS"),
        new("TV", "TV Audio", "TV"),
        new("BD", "Blu-ray", "BD"),
        new("GAME", "Game", "GAME"),
        new("MPLAY", "Media Player", "MPLAY"),
        new("SAT/CBL", "CBL / SAT", "SAT"),
        new("BT", "Bluetooth", "BT"),
        new("CD", "CD", "CD"),
        new("AUX1", "AUX 1", "AUX1"),
        new("PHONO", "Phono", "PHONO"),
        new("TUNER", "FM / AM", "TUNER"),
    ];

    public static IReadOnlyList<AvrSoundModeOption> SoundModes { get; } =
    [
        new("AUTO", "Auto"),
        new("STEREO", "Stereo"),
        new("DIRECT", "Direct"),
        new("PURE DIRECT", "Pure Direct"),
        new("MOVIE", "Movie"),
        new("MUSIC", "Music"),
        new("GAME", "Game"),
        new("MCH STEREO", "Multi-Ch Stereo"),
    ];

    /// <summary>
    /// The sources Zone 2 can play, for the pickers. SOURCE follows the main zone's input. The receiver takes the
    /// same codes as for the main zone, with "Z2" in front (Z2TV).
    /// </summary>
    public static IReadOnlyList<AvrInputOption> Zone2Sources { get; } =
        [new("SOURCE", "Main zone source", "MAIN"), .. InputSources];

    /// <summary>
    /// Every source code a "Z2" line can carry: the picker's codes and the other inputs Denon models report. A Z2
    /// line with any other text (Z2MUON, Z2CS ST, Z2PSBAS 50, Z245) is not a source.
    /// </summary>
    private static readonly HashSet<string> Zone2SourceCodes = new(
        Zone2Sources.Select(static s => s.Code).Concat(
        [
            "DVD", "AUX2", "AUX3", "AUX4", "AUX5", "AUX6", "AUX7", "USB", "USB/IPOD", "IPD", "IPOD", "SERVER", "IRADIO",
            "FAVORITES", "PANDORA", "SIRIUSXM", "SPOTIFY", "LASTFM", "FLICKR", "HDRADIO", "SAT", "CBL/SAT", "DVR",
            "V.AUX", "8K", "HEOS", "NETHOME", "IRP", "FVP", "NAPSTER", "TIDAL", "DEEZER", "AMAZON", "TUNEIN",
        ]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a code Zone 2 can play (see <see cref="Zone2SourceCodes"/>).</summary>
    public static bool IsZone2SourceCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Zone2SourceCodes.Contains(code.Trim());

    /// <summary>The name for a Zone 2 source code; SOURCE is the main zone's source.</summary>
    public static string GetFriendlyZone2SourceName(string? code) =>
        string.Equals(code?.Trim(), "SOURCE", StringComparison.OrdinalIgnoreCase)
            ? "Main zone source"
            : GetFriendlyInputName(code);

    public static string GetFriendlyInputName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "Unknown";
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized is "IRADIO" or "HEOS" or "ONLINE MUSIC")
        {
            return "HEOS Music";
        }

        foreach (var option in InputSources)
        {
            if (string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option.DisplayName;
            }
        }

        return code.Trim();
    }

    public static bool InputMatches(string? currentInput, string optionCode)
    {
        if (string.IsNullOrWhiteSpace(currentInput))
        {
            return false;
        }

        var normalizedCurrent = currentInput.Trim().ToUpperInvariant();
        var normalizedCode = optionCode.Trim().ToUpperInvariant();

        if (normalizedCode == "NET" && normalizedCurrent is
            "NET" or "HEOS" or "IRADIO" or "ONLINE MUSIC" or "SERVER" or "PANDORA" or "SPOTIFY" or
            "AIRPLAY" or "TIDAL" or "DEEZER" or "AMAZON" or "TUNEIN" or "SOUNDCLOUD" or "NAPSTER" or
            "RHAPSODY" or "SIRIUSXM" or "FAVORITES" or "NET/USB" or "USB/IPOD" or "USB")
        {
            return true;
        }

        return string.Equals(normalizedCurrent, normalizedCode, StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetHeosSourceName(int? sourceId) => sourceId switch
    {
        1 => "Pandora",
        2 => "Rhapsody",
        3 => "TuneIn",
        4 => "Spotify",
        5 => "Deezer",
        6 => "Napster",
        7 => "iHeartRadio",
        8 => "SiriusXM",
        9 => "SoundCloud",
        10 => "Tidal",
        13 => "Amazon Music",
        1024 => "Local Music",
        1025 => "HEOS Playlists",
        1026 => "HEOS History",
        1027 => "HEOS Aux",
        1028 => "HEOS Favorites",
        1029 => "Network Stream",
        _ => null,
    };
}

public static class DenonVolumeMath
{
    public const int AbsoluteZeroDbPoint = 80;
    public const int MaxAbsoluteVolume = 98;

    public static double ToDecibels(double absoluteVolume) => Math.Round(absoluteVolume - AbsoluteZeroDbPoint, 1);

    public static string FormatDecibels(double absoluteVolume)
    {
        if (absoluteVolume <= 0.01)
        {
            return "--- dB";
        }

        var db = ToDecibels(absoluteVolume);
        var sign = db > 0 ? "+" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{db:0.0} dB");
    }

    public static string FormatAbsolute(double absoluteVolume) =>
        Math.Abs(absoluteVolume - Math.Round(absoluteVolume)) < 0.05
            ? ((int)Math.Round(absoluteVolume)).ToString(CultureInfo.InvariantCulture)
            : absoluteVolume.ToString("0.0", CultureInfo.InvariantCulture);
}

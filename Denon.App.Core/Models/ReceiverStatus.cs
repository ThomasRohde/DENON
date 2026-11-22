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
    bool IsAvrConfirmed = true);

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
    double VolumeExact = 0);

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

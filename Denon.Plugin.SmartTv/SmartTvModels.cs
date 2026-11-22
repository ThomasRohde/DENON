namespace Denon.Plugin.SmartTv;

public sealed class SmartTvConfig
{
    public string TvName { get; set; } = "Living Room OLED TV";

    public string TvIp { get; set; } = string.Empty;

    public string MacAddress { get; set; } = string.Empty;

    public string ClientKey { get; set; } = string.Empty;

    public bool DemoMode { get; set; }

    public bool WakeWithDenonPowerOn { get; set; }

    public bool TurnOffWithDenonStandby { get; set; }

    public bool ShowSceneToastOnTv { get; set; } = true;

    public bool SwitchToArcOnDenonPowerOn { get; set; }

    public bool MuteTvSpeakersOnDenonPowerOn { get; set; }

    public bool ScreenOffOnMusicScene { get; set; }

    public bool AutoRefreshPreview { get; set; }

    public string MovieNightInputAppId { get; set; } = "com.webos.app.hdmi1";

    public string LastModelName { get; set; } = string.Empty;

    public string LastWebOsVersion { get; set; } = string.Empty;
}

public sealed record LgTvAppInfo(
    string Id,
    string Title,
    string Vendor,
    bool IsSystemApp,
    bool IsActive,
    string BadgeText);

public sealed record LgTvInputInfo(
    string Id,
    string AppId,
    string Label,
    bool IsConnected,
    bool IsActive,
    string SubLabel);

public sealed record LgTvDiscoveredDevice(
    string FriendlyName,
    string IpAddress,
    string MacAddress,
    string ModelName,
    string Udn);

public sealed record LgTvScreenshotResult(
    bool Success,
    string? ImageBase64,
    string MimeType,
    int Width,
    int Height,
    int ByteLength,
    string? ImageUri = null,
    string? ErrorMessage = null);

public sealed record LgTvLiveState
{
    public bool IsConnected { get; init; }

    public bool IsDemo { get; init; }

    public bool IsPointerConnected { get; init; }

    public string TransportLabel { get; init; } = "OFFLINE";

    public string ModelName { get; init; } = string.Empty;

    public string WebOsVersion { get; init; } = string.Empty;

    public string PowerState { get; init; } = "Unknown";

    public bool IsScreenOff { get; init; }

    public string ForegroundAppId { get; init; } = string.Empty;

    public string ForegroundAppTitle { get; init; } = string.Empty;

    public string CurrentChannelNumber { get; init; } = string.Empty;

    public string CurrentChannelName { get; init; } = string.Empty;

    public int Volume { get; init; }

    public bool IsMuted { get; init; }

    public string SoundOutput { get; init; } = string.Empty;

    public string SoundOutputLabel { get; init; } = "—";

    public IReadOnlyList<LgTvInputInfo> Inputs { get; init; } = [];

    public IReadOnlyList<LgTvAppInfo> Apps { get; init; } = [];

    public string? LastError { get; init; }

    public string NowShowingSummary
    {
        get
        {
            if (!IsConnected && !IsDemo)
            {
                return "TV Standby / Offline";
            }

            if (ForegroundAppId.Contains("livetv", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(CurrentChannelName) || !string.IsNullOrWhiteSpace(CurrentChannelNumber)))
            {
                var ch = string.IsNullOrWhiteSpace(CurrentChannelNumber)
                    ? CurrentChannelName
                    : $"Ch {CurrentChannelNumber} • {CurrentChannelName}".TrimEnd(' ', '•');
                return $"Live TV ({ch})";
            }

            if (!string.IsNullOrWhiteSpace(ForegroundAppTitle))
            {
                return ForegroundAppTitle;
            }

            return string.IsNullOrWhiteSpace(ForegroundAppId) ? "Home / Active" : ForegroundAppId;
        }
    }

    public static string FormatSoundOutput(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        "external_arc" => "HDMI ARC / eARC",
        "tv_speaker" => "TV Speakers",
        "external_optical" => "Optical Out",
        "bt_soundbar" or "external_speaker" or "bluetooth" => "Bluetooth",
        "tv_external_speaker" => "TV + Optical",
        "tv_speaker_headphone" => "TV + Headphone",
        "headphone" or "lineout" => "Headphone / Line Out",
        "wisa_speaker" => "WiSA Speaker",
        "" => "—",
        _ => raw!,
    };
}

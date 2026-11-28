namespace Denon.Plugin.SmartTv;

public sealed class SmartTvConfig
{
    public string TvName { get; set; } = "Living Room Smart TV";

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

    /// <summary>
    /// Downloads the logos of the TV's channels from the open tv-logos collection on GitHub (once, for the whole
    /// channel list, then from the cache) and shows them in the now-playing hero and Insights.
    /// </summary>
    public bool ChannelLogos { get; set; } = true;

    /// <summary>
    /// The Chromecast plugged into this TV (its id, name or IP), which wakes it over HDMI-CEC when Wake-on-LAN gets
    /// no answer. Chosen on the Smart TV page. Empty is "Automatic": the only Cast device known, else the only one
    /// online.
    /// </summary>
    public string CecWakeCastDevice { get; set; } = string.Empty;

    /// <summary>
    /// Pins a logo per channel name: an https address or a local image file. The value "none" shows no logo.
    /// </summary>
    public Dictionary<string, string> ChannelLogoOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A Cast device the Chromecast plugin knows: its id, its name, and whether it is online now.</summary>
internal sealed record CastDeviceChoice(string Id, string Name, bool IsConnected);

/// <summary>
/// One entry of the "Chromecast on this TV" list on the Smart TV page. An empty id is "Automatic". The entry stays
/// the same object while the list reloads, and only its label changes, so the box keeps the chosen entry.
/// </summary>
public sealed class CastDeviceOption(string id, string label) : System.ComponentModel.INotifyPropertyChanged
{
    private string _label = label;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = id;

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
            {
                return;
            }

            _label = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Label)));
        }
    }
}

/// <summary>A program from the TV's guide (DVB EIT), in real time (any day shift in the TV's guide removed).</summary>
public sealed record LgTvProgram(string Name, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A channel from the TV's channel list.</summary>
public sealed record LgTvChannel(string Id, string Number, string Name, bool IsRadio);

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

    public bool IsOledModel => ModelName.Contains("OLED", StringComparison.OrdinalIgnoreCase);

    public string WebOsVersion { get; init; } = string.Empty;

    public string PowerState { get; init; } = "Unknown";

    public bool IsScreenOff { get; init; }

    public string ForegroundAppId { get; init; } = string.Empty;

    public string ForegroundAppTitle { get; init; } = string.Empty;

    public string CurrentChannelNumber { get; init; } = string.Empty;

    public string CurrentChannelName { get; init; } = string.Empty;

    /// <summary>The TV's channel ID, for example "3_18_1_1_2190_101_65024" (source, physical channel, DVB IDs).</summary>
    public string CurrentChannelId { get; init; } = string.Empty;

    /// <summary>The DVB IDs as the TV writes them: "&lt;TSID&gt;_&lt;SID&gt;_&lt;ONID&gt;", for example "2190_101_65024".</summary>
    public string SignalChannelId { get; init; } = string.Empty;

    /// <summary>"Cable Digital TV", "Terrestrial Digital TV", "Satellite Digital TV" and so on.</summary>
    public string ChannelTypeName { get; init; } = string.Empty;

    public LgTvProgram? CurrentProgram { get; init; }

    public LgTvProgram? NextProgram { get; init; }

    public bool IsLiveTv => ForegroundAppId.Contains("livetv", StringComparison.OrdinalIgnoreCase);

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

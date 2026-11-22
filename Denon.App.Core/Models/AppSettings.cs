namespace Denon.App.Core.Models;

public sealed class AppSettings
{
    public ReceiverSettings Receiver { get; set; } = ReceiverSettings.CreateDefault();

    public UiSettings Ui { get; set; } = new();

    public MediaRelaySettings Media { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public PluginSettings Plugins { get; set; } = new();

    public McpServerSettings Mcp { get; set; } = new();

    public TelemetrySettings Telemetry { get; set; } = new();

    public List<AutomationRule> Automations { get; set; } = AutomationRule.CreateDefaults();

    public AppSettings Clone() =>
        new()
        {
            Receiver = Receiver.Clone(),
            Ui = Ui.Clone(),
            Media = Media.Clone(),
            Shortcuts = Shortcuts.Clone(),
            Plugins = Plugins.Clone(),
            Mcp = Mcp.Clone(),
            Telemetry = Telemetry.Clone(),
            Automations = Automations.Select(static a => a.Clone()).ToList(),
        };

    public static AppSettings CreateDefault() => new();
}

public sealed class PluginSettings
{
    public List<string> DisabledPluginIds { get; set; } = [];

    public string SelectedPluginId { get; set; } = "philips-hue";

    public PluginSettings Clone() =>
        new()
        {
            DisabledPluginIds = [.. DisabledPluginIds],
            SelectedPluginId = SelectedPluginId,
        };
}

public sealed class MediaRelaySettings
{
    public int RelayPort { get; set; } = 18925;

    public string FfmpegPath { get; set; } = string.Empty;

    public bool PreferFfmpegMp3Transcode { get; set; } = true;

    public int CinemaSyncOffsetMs { get; set; } = 1800;

    public MediaRelaySettings Clone() =>
        new()
        {
            RelayPort = RelayPort,
            FfmpegPath = FfmpegPath,
            PreferFfmpegMp3Transcode = PreferFfmpegMp3Transcode,
            CinemaSyncOffsetMs = CinemaSyncOffsetMs,
        };
}

public sealed class ReceiverSettings
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 1255;

    public string PlayerName { get; set; } = string.Empty;

    public int DefaultLowVolume { get; set; } = 20;

    public int PollIntervalSeconds { get; set; } = 5;

    public List<int> VolumePresets { get; set; } = [15, 28, 42, 55];

    public List<StationPreset> Stations { get; set; } = CreateDefaultStations();

    public List<ScenePreset> Scenes { get; set; } = ScenePreset.CreateDefaultScenes();

    public ReceiverSettings Clone() =>
        new()
        {
            Host = Host,
            Port = Port,
            PlayerName = PlayerName,
            DefaultLowVolume = DefaultLowVolume,
            PollIntervalSeconds = PollIntervalSeconds,
            VolumePresets = [.. VolumePresets],
            Stations = Stations.Select(static station => station.Clone()).ToList(),
            Scenes = Scenes.Select(static scene => scene.Clone()).ToList(),
        };

    public static ReceiverSettings CreateDefault() => new();

    public static List<StationPreset> CreateDefaultStations() =>
    [
        new StationPreset("radio-paradise", "Radio Paradise (Main)", "http://stream.radioparadise.com/aac-320"),
        new StationPreset("somafm-groove", "SomaFM Groove Salad", "http://ice2.somafm.com/groovesalad-256-mp3"),
        new StationPreset("somafm-secret", "SomaFM Secret Agent", "http://ice2.somafm.com/secretagent-128-mp3"),
        new StationPreset("somafm-drone", "SomaFM Drone Zone", "http://ice2.somafm.com/dronezone-256-mp3"),
        new StationPreset("nightride-fm", "Nightride FM (Synthwave)", "https://stream.nightride.fm/nightride.mp3"),
        new StationPreset("dr-p8-jazz", "DR P8 Jazz", "http://live-icy.dr.dk/A/A22L.mp3"),
    ];
}

public sealed class UiSettings
{
    public bool StartMinimizedToTray { get; set; } = false;

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool DisplayVolumeInDb { get; set; } = false;

    public bool LaunchAtWindowsStartup { get; set; } = false;

    public UiSettings Clone() =>
        new()
        {
            StartMinimizedToTray = StartMinimizedToTray,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
            DisplayVolumeInDb = DisplayVolumeInDb,
            LaunchAtWindowsStartup = LaunchAtWindowsStartup,
        };
}

public sealed class StationPreset
{
    public StationPreset()
    {
    }

    public StationPreset(string alias, string displayName, string url)
    {
        Alias = alias;
        DisplayName = displayName;
        Url = url;
    }

    public string Alias { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public StationPreset Clone() => new(Alias, DisplayName, Url);
}

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; } = true;

    public int RetentionDays { get; set; } = 90;

    public int MediaDwellSeconds { get; set; } = 15;

    public int VolumeSettleMs { get; set; } = 3000;

    public int ActiveProbeIntervalSeconds { get; set; } = 15;

    public int StandbyProbeIntervalSeconds { get; set; } = 60;

    public int MaxTotalSizeMb { get; set; } = 50;

    public TelemetrySettings Clone() =>
        new()
        {
            Enabled = Enabled,
            RetentionDays = RetentionDays,
            MediaDwellSeconds = MediaDwellSeconds,
            VolumeSettleMs = VolumeSettleMs,
            ActiveProbeIntervalSeconds = ActiveProbeIntervalSeconds,
            StandbyProbeIntervalSeconds = StandbyProbeIntervalSeconds,
            MaxTotalSizeMb = MaxTotalSizeMb,
        };
}

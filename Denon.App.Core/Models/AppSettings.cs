namespace Denon.App.Core.Models;

public sealed class AppSettings
{
    public ReceiverSettings Receiver { get; set; } = ReceiverSettings.CreateDefault();

    public UiSettings Ui { get; set; } = new();

    public MediaRelaySettings Media { get; set; } = new();

    public AppSettings Clone() =>
        new()
        {
            Receiver = Receiver.Clone(),
            Ui = Ui.Clone(),
            Media = Media.Clone(),
        };

    public static AppSettings CreateDefault() => new();
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
    public string Host { get; set; } = "192.168.1.3";

    public int Port { get; set; } = 1255;

    public string PlayerName { get; set; } = "Denon AVR-X2400H";

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
        new StationPreset("dr-p1", "DR P1", "http://live-icy.dr.dk/A/A03L.mp3"),
        new StationPreset("dr-p2", "DR P2", "http://live-icy.dr.dk/A/A04L.mp3"),
        new StationPreset("dr-p3", "DR P3", "http://live-icy.dr.dk/A/A05L.mp3"),
        new StationPreset("dr-p4-kbh", "DR P4 København", "http://live-icy.dr.dk/A/A08L.mp3"),
        new StationPreset("dr-p5-kbh", "DR P5 København", "http://live-icy.dr.dk/A/A28L.mp3"),
        new StationPreset("dr-p6", "DR P6 Beat", "http://live-icy.dr.dk/A/A29L.mp3"),
        new StationPreset("dr-p8", "DR P8 Jazz", "http://live-icy.dr.dk/A/A22L.mp3"),
    ];
}

public sealed class UiSettings
{
    public bool StartMinimizedToTray { get; set; } = false;

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public bool DisplayVolumeInDb { get; set; } = true;

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

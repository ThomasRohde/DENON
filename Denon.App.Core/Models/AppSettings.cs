namespace Denon.App.Core.Models;

public sealed class AppSettings
{
    public ReceiverSettings Receiver { get; set; } = ReceiverSettings.CreateDefault();

    public UiSettings Ui { get; set; } = new();

    public AppSettings Clone() =>
        new()
        {
            Receiver = Receiver.Clone(),
            Ui = Ui.Clone(),
        };

    public static AppSettings CreateDefault() => new();
}

public sealed class ReceiverSettings
{
    public string Host { get; set; } = "192.168.1.3";

    public int Port { get; set; } = 1255;

    public string PlayerName { get; set; } = "Denon AVR-X2400H";

    public int DefaultLowVolume { get; set; } = 10;

    public int PollIntervalSeconds { get; set; } = 5;

    public List<StationPreset> Stations { get; set; } = CreateDefaultStations();

    public ReceiverSettings Clone() =>
        new()
        {
            Host = Host,
            Port = Port,
            PlayerName = PlayerName,
            DefaultLowVolume = DefaultLowVolume,
            PollIntervalSeconds = PollIntervalSeconds,
            Stations = Stations.Select(static station => station.Clone()).ToList(),
        };

    public static ReceiverSettings CreateDefault() => new();

    public static List<StationPreset> CreateDefaultStations() =>
    [
        new StationPreset("dr-p1", "DR P1", "http://live-icy.dr.dk/A/A03L.mp3"),
        new StationPreset("dr-p2", "DR P2", "http://live-icy.dr.dk/A/A04L.mp3"),
        new StationPreset("dr-p3", "DR P3", "http://live-icy.dr.dk/A/A05L.mp3"),
        new StationPreset("dr-p4-kbh", "DR P4 Kobenhavn", "http://live-icy.dr.dk/A/A08L.mp3"),
        new StationPreset("dr-p5-kbh", "DR P5 Kobenhavn", "http://live-icy.dr.dk/A/A28L.mp3"),
        new StationPreset("dr-p6", "DR P6 Beat", "http://live-icy.dr.dk/A/A29L.mp3"),
        new StationPreset("dr-p8", "DR P8 Jazz", "http://live-icy.dr.dk/A/A22L.mp3"),
    ];
}

public sealed class UiSettings
{
    public bool StartMinimizedToTray { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowNotifications { get; set; } = true;

    public UiSettings Clone() =>
        new()
        {
            StartMinimizedToTray = StartMinimizedToTray,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
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

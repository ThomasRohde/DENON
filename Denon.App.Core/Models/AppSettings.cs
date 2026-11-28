namespace Denon.App.Core.Models;

public sealed class AppSettings
{
    public ReceiverSettings Receiver { get; set; } = ReceiverSettings.CreateDefault();

    public UiSettings Ui { get; set; } = new();

    public MediaRelaySettings Media { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public PluginSettings Plugins { get; set; } = new();

    public McpServerSettings Mcp { get; set; } = new();

    public RemoteAccessSettings Remote { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();

    public TelemetrySettings Telemetry { get; set; } = new();

    public VolumeMemorySettings VolumeMemory { get; set; } = new();

    public List<AutomationGraph> AutomationGraphs { get; set; } = AutomationGraph.CreateDefaults();

    public AppSettings Clone() =>
        new()
        {
            Receiver = Receiver.Clone(),
            Ui = Ui.Clone(),
            Media = Media.Clone(),
            Shortcuts = Shortcuts.Clone(),
            Plugins = Plugins.Clone(),
            Mcp = Mcp.Clone(),
            Remote = Remote.Clone(),
            Security = Security.Clone(),
            Telemetry = Telemetry.Clone(),
            VolumeMemory = VolumeMemory.Clone(),
            AutomationGraphs = AutomationGraphs.Select(static a => a.Clone()).ToList(),
        };

    public static AppSettings CreateDefault() => new();
}

/// <summary>
/// Volume memory: the app learns the listening volume of each source (a TV channel, a streaming app, an input) and
/// sets it when the source changes. The learned levels are in volume-memory.json, not here.
/// </summary>
public sealed class VolumeMemorySettings
{
    /// <summary>Set the learned volume when the source changes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Only turn the volume down, never up.</summary>
    public bool OnlyLower { get; set; }

    /// <summary>Smaller differences (in volume steps, 1 dB each) are left alone.</summary>
    public int MinDifference { get; set; } = 3;

    public VolumeMemorySettings Clone() => new() { Enabled = Enabled, OnlyLower = OnlyLower, MinDifference = MinDifference };
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

    /// <summary>Check GitHub once a day for a newer Denon Desktop (Settings → Desktop &amp; Tray Behavior).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check succeeded; the next automatic check is a day later, across restarts.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Theme: <c>system</c> (follow the Windows app mode), <c>dark</c> or <c>light</c> (docs/theming.md).</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Skin: <c>default</c>, <c>hifi</c>, <c>compact</c> or <c>large</c>; applied at start (docs/theming.md).</summary>
    public string Skin { get; set; } = "default";

    /// <summary>
    /// The main window's last size and position (saved when it hides or the app exits). Null until then: the
    /// window then fits itself to the monitor and the skin.
    /// </summary>
    public WindowPlacementSettings? Window { get; set; }

    /// <summary>The install the splash screen was last shown for (version and exe time); a new install shows it again.</summary>
    public string? SplashShownFor { get; set; }

    /// <summary>Opt-in "ROOM" mode: listen via a PC microphone / Line-In for TV/ARC, HDMI &amp; Chromecast audio meters (off by default).</summary>
    public bool AllowRoomMicMeter { get; set; } = false;

    /// <summary>Preferred Windows capture device ID for Room Mic meters (empty = system default microphone).</summary>
    public string MeterMicDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Copies the fields the Settings editor owns (tray, notification, dB, startup, updates and Room Mic choices) from
    /// <paramref name="editor"/>. Theme, skin, window placement and the splash marker stay: the app and the
    /// Appearance chips set them, and Save &amp; Sync must not overwrite them with an older copy.
    /// </summary>
    public void ApplyEditorFields(UiSettings editor)
    {
        StartMinimizedToTray = editor.StartMinimizedToTray;
        MinimizeToTray = editor.MinimizeToTray;
        CloseToTray = editor.CloseToTray;
        ShowNotifications = editor.ShowNotifications;
        DisplayVolumeInDb = editor.DisplayVolumeInDb;
        LaunchAtWindowsStartup = editor.LaunchAtWindowsStartup;
        CheckForUpdates = editor.CheckForUpdates;
        AllowRoomMicMeter = editor.AllowRoomMicMeter;
        MeterMicDeviceId = editor.MeterMicDeviceId ?? string.Empty;
    }

    public UiSettings Clone() =>
        new()
        {
            StartMinimizedToTray = StartMinimizedToTray,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
            DisplayVolumeInDb = DisplayVolumeInDb,
            LaunchAtWindowsStartup = LaunchAtWindowsStartup,
            CheckForUpdates = CheckForUpdates,
            LastUpdateCheck = LastUpdateCheck,
            Theme = Theme,
            Skin = Skin,
            Window = Window?.Clone(),
            SplashShownFor = SplashShownFor,
            AllowRoomMicMeter = AllowRoomMicMeter,
            MeterMicDeviceId = MeterMicDeviceId,
        };
}

/// <summary>The main window's normal (not maximized) bounds in device-independent pixels, and its state.</summary>
public sealed class WindowPlacementSettings
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>
    /// True once the user moved or resized the window: from then on it keeps their size. Until then it grows to
    /// fit when the deck grows (for example when the receiver connects after the first start).
    /// </summary>
    public bool UserSized { get; set; }

    /// <summary>The window's DPI scale when saved (1 = 96 DPI), to find its monitor again.</summary>
    public double Scale { get; set; } = 1;

    public WindowPlacementSettings Clone() => (WindowPlacementSettings)MemberwiseClone();
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

public sealed class RemoteAccessSettings
{
    public bool Enabled { get; set; } = true;

    public bool RequirePairing { get; set; } = true;

    public string OwnerToken { get; set; } = string.Empty;

    public string GuestToken { get; set; } = string.Empty;

    /// <summary>Works only for POST /api/hooks/{name}, so an iPhone Shortcut need not hold the owner token.</summary>
    public string WebhookToken { get; set; } = string.Empty;

    public RemoteAccessSettings Clone() =>
        new()
        {
            Enabled = Enabled,
            RequirePairing = RequirePairing,
            OwnerToken = OwnerToken,
            GuestToken = GuestToken,
            WebhookToken = WebhookToken,
        };

    public static string GeneratePairingToken(string prefix = "own")
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        return $"{prefix}_{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}

/// <summary>Network rules shared by the Phone Remote server (:18925) and the MCP server (:18926).</summary>
public sealed class SecuritySettings
{
    /// <summary>
    /// Answer only this PC and the local network: loopback, private (10/8, 172.16/12, 192.168/16), link-local, and
    /// IPv6 unique-local addresses. Others get 403. Off by default.
    /// </summary>
    public bool LanOnly { get; set; }

    public SecuritySettings Clone() => new() { LanOnly = LanOnly };
}

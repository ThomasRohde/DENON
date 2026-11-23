namespace Denon.Plugin.Chromecast;

internal sealed class ChromecastConfig
{
    public List<CastDeviceConfig> Devices { get; set; } = [];

    /// <summary>Run mDNS discovery at start and then on this interval.</summary>
    public bool AutoDiscover { get; set; } = true;

    public int DiscoveryIntervalMinutes { get; set; } = 10;

    /// <summary>Master switch for all receiver automations.</summary>
    public bool AutomationsEnabled { get; set; } = true;
}

internal sealed class CastDeviceConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8009;

    public string Model { get; set; } = string.Empty;

    /// <summary>"mdns" or "manual".</summary>
    public string Source { get; set; } = "mdns";

    public bool Enabled { get; set; } = true;

    public CastAutomationConfig Automation { get; set; } = new();
}

/// <summary>
/// What the plugin does on the Denon receiver when this Cast device starts or stops a session.
/// All fields are off or empty by default.
/// </summary>
internal sealed class CastAutomationConfig
{
    /// <summary>Power on the receiver when an app starts on this device.</summary>
    public bool PowerOnReceiver { get; set; }

    /// <summary>AVR input code of the HDMI port this device uses, for example "MPLAY" or "SAT/CBL".</summary>
    public string ReceiverInput { get; set; } = string.Empty;

    /// <summary>AVR sound mode code to select, for example "MOVIE".</summary>
    public string SoundMode { get; set; } = string.Empty;

    /// <summary>Smart Scene id or name to run when an app starts.</summary>
    public string SceneOnStart { get; set; } = string.Empty;

    /// <summary>Power off the receiver after this many idle minutes (0 = never).
    /// Only when the receiver is still on <see cref="ReceiverInput"/>.</summary>
    public int PowerOffAfterIdleMinutes { get; set; }

    /// <summary>Pause playback on this device when the receiver powers off.</summary>
    public bool PauseWhenReceiverPowersOff { get; set; }

    /// <summary>App names that never trigger automations.</summary>
    public List<string> IgnoredApps { get; set; } = [];

    public bool HasStartActions =>
        PowerOnReceiver ||
        !string.IsNullOrWhiteSpace(ReceiverInput) ||
        !string.IsNullOrWhiteSpace(SoundMode) ||
        !string.IsNullOrWhiteSpace(SceneOnStart);
}

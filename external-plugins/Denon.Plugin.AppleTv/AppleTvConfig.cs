using System.Text.Json.Serialization;
using Denon.Plugin.AppleTv.Protocol.Companion;

namespace Denon.Plugin.AppleTv;

internal sealed class AppleTvConfig
{
    public List<AppleTvDeviceConfig> Devices { get; set; } = [];

    /// <summary>Run DNS-SD discovery at start and then on this interval.</summary>
    public bool AutoDiscover { get; set; } = true;

    public int DiscoveryIntervalMinutes { get; set; } = 10;

    /// <summary>Master switch for all receiver automations.</summary>
    public bool AutomationsEnabled { get; set; } = true;
}

internal sealed class AppleTvDeviceConfig
{
    /// <summary>Stable id: the AirPlay device id (MAC-like) from discovery.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int CompanionPort { get; set; } = 49153;

    public int AirPlayPort { get; set; } = 7000;

    public string Model { get; set; } = string.Empty;

    public string OsVersion { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>pyatv-compatible HAP credentials "ltpk:ltsk:atvId:clientId" (holds a private key). Empty = not paired.</summary>
    public string Credentials { get; set; } = string.Empty;

    /// <summary>How Denon Desktop presents itself to this Apple TV (stable per pairing).</summary>
    public CompanionIdentity? Identity { get; set; }

    public DateTimeOffset? PairedUtc { get; set; }

    public AppleTvAutomationConfig Automation { get; set; } = new();

    [JsonIgnore]
    public bool IsPaired => !string.IsNullOrWhiteSpace(Credentials) && Identity is not null;
}

/// <summary>What the plugin does on the Denon receiver when this Apple TV wakes or sleeps. All off by default.</summary>
internal sealed class AppleTvAutomationConfig
{
    /// <summary>Power on the receiver when the Apple TV wakes.</summary>
    public bool PowerOnReceiver { get; set; }

    /// <summary>AVR input code of the HDMI port this Apple TV uses (for example "TV" via ARC, "MPLAY", "SAT/CBL").</summary>
    public string ReceiverInput { get; set; } = string.Empty;

    /// <summary>AVR sound mode code to select, for example "MOVIE".</summary>
    public string SoundMode { get; set; } = string.Empty;

    /// <summary>Smart Scene id or name to run when the Apple TV wakes.</summary>
    public string SceneOnWake { get; set; } = string.Empty;

    /// <summary>Power off the receiver this many minutes after the Apple TV sleeps (0 = never).
    /// Only when the receiver is still on <see cref="ReceiverInput"/>.</summary>
    public int PowerOffAfterSleepMinutes { get; set; }

    /// <summary>Pause the Apple TV when the receiver powers off.</summary>
    public bool PauseWhenReceiverPowersOff { get; set; }

    [JsonIgnore]
    public bool HasWakeActions =>
        PowerOnReceiver ||
        !string.IsNullOrWhiteSpace(ReceiverInput) ||
        !string.IsNullOrWhiteSpace(SoundMode) ||
        !string.IsNullOrWhiteSpace(SceneOnWake);
}

namespace Denon.App.Core.Models;

public sealed class ScenePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "Custom Scene";

    public string Description { get; set; } = string.Empty;

    public string Badge { get; set; } = "SCN";

    public string AccentHex { get; set; } = "#00D4FF";

    public bool PowerOn { get; set; } = true;

    public bool Unmute { get; set; } = true;

    public string? InputSource { get; set; }

    public string? SoundMode { get; set; }

    public int? TargetVolume { get; set; }

    public bool SmoothVolumeRamp { get; set; } = true;

    public string? EcoMode { get; set; }

    public int? SleepTimerMinutes { get; set; }

    public string? MultEq { get; set; }

    public bool? DynamicEq { get; set; }

    public string? DynamicVolume { get; set; }

    public string? Restorer { get; set; }

    public string? Dimmer { get; set; }

    public int? SubwooferTrimDb { get; set; }

    public int? DialogTrimDb { get; set; }

    public string? StreamUrl { get; set; }

    /// <summary>Zone 2 power: true turns it on (then source, volume and mute apply), false turns it off. Null: no change.</summary>
    public bool? Zone2On { get; set; }

    /// <summary>Zone 2 source code (TV, NET, SOURCE for the main zone's source). Null: no change.</summary>
    public string? Zone2Source { get; set; }

    /// <summary>Zone 2 volume on the receiver's 0–98 scale. Null: no change.</summary>
    public int? Zone2Volume { get; set; }

    /// <summary>Zone 2 mute. False unmutes after the volume is set. Null: no change.</summary>
    public bool? Zone2Muted { get; set; }

    /// <summary>
    /// HEOS players (names or ids) to group with this app's player, which leads and plays the scene's source.
    /// Empty: no change.
    /// </summary>
    public List<string> HeosGroupMembers { get; set; } = [];

    /// <summary>True ends the HEOS group this app's player is in (when <see cref="HeosGroupMembers"/> is empty).</summary>
    public bool HeosUngroup { get; set; }

    public List<ScenePluginActionStep> PluginActions { get; set; } = [];

    public ScenePreset Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Badge = Badge,
            AccentHex = AccentHex,
            PowerOn = PowerOn,
            Unmute = Unmute,
            InputSource = InputSource,
            SoundMode = SoundMode,
            TargetVolume = TargetVolume,
            SmoothVolumeRamp = SmoothVolumeRamp,
            EcoMode = EcoMode,
            SleepTimerMinutes = SleepTimerMinutes,
            MultEq = MultEq,
            DynamicEq = DynamicEq,
            DynamicVolume = DynamicVolume,
            Restorer = Restorer,
            Dimmer = Dimmer,
            SubwooferTrimDb = SubwooferTrimDb,
            DialogTrimDb = DialogTrimDb,
            StreamUrl = StreamUrl,
            Zone2On = Zone2On,
            Zone2Source = Zone2Source,
            Zone2Volume = Zone2Volume,
            Zone2Muted = Zone2Muted,
            HeosGroupMembers = [.. HeosGroupMembers ?? []], // null from a hand-edited settings file
            HeosUngroup = HeosUngroup,
            PluginActions = PluginActions.Select(static s => s.Clone()).ToList(),
        };

    public static List<ScenePreset> CreateDefaultScenes() =>
    [
        new ScenePreset
        {
            Id = "movie-night",
            Name = "Movie Night",
            Description = "TV/Cinema surround, Dynamic EQ on, front display blackout, smooth ramp to -32 dB.",
            Badge = "CINE",
            AccentHex = "#F59E0B",
            PowerOn = true,
            Unmute = true,
            InputSource = "TV",
            SoundMode = "MOVIE",
            TargetVolume = 48,
            SmoothVolumeRamp = true,
            EcoMode = "OFF",
            DynamicEq = true,
            DynamicVolume = "OFF",
            Dimmer = "OFF",
            SubwooferTrimDb = 0,
            DialogTrimDb = 1,
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "philips-hue",
                    ActionId = "hue.movie_dim",
                    DisplayName = "Philips Hue: Cinema Dim (15%)",
                },
            ],
        },
        new ScenePreset
        {
            Id = "late-night",
            Name = "Late Night",
            Description = "Heavy Dynamic Volume, -6 dB subwoofer trim, +3 dB dialog boost, dark panel & 60m sleep.",
            Badge = "NITE",
            AccentHex = "#A78BFA",
            PowerOn = true,
            Unmute = true,
            TargetVolume = 24,
            SmoothVolumeRamp = true,
            EcoMode = "ON",
            SleepTimerMinutes = 60,
            DynamicEq = true,
            DynamicVolume = "HEV",
            Dimmer = "DAR",
            SubwooferTrimDb = -6,
            DialogTrimDb = 3,
            PluginActions =
            [
                new ScenePluginActionStep
                {
                    PluginId = "philips-hue",
                    ActionId = "hue.candlelight",
                    DisplayName = "Philips Hue: Late Night Candlelight",
                },
            ],
        },
        new ScenePreset
        {
            Id = "pure-audiophile",
            Name = "Pure Hi-Fi",
            Description = "Uncolored Pure Direct playback on HEOS with ECO off and dimmed front panel.",
            Badge = "HIFI",
            AccentHex = "#00D4FF",
            PowerOn = true,
            Unmute = true,
            InputSource = "NET",
            SoundMode = "PURE DIRECT",
            EcoMode = "OFF",
            DynamicEq = false,
            DynamicVolume = "OFF",
            Dimmer = "DIM",
        },
        new ScenePreset
        {
            Id = "morning-coffee",
            Name = "Morning Jazz",
            Description = "Gentle volume ramp to 26, Stereo + Restorer Medium, and launches DR P8 Jazz.",
            Badge = "JAZZ",
            AccentHex = "#10B981",
            PowerOn = true,
            Unmute = true,
            InputSource = "NET",
            SoundMode = "STEREO",
            TargetVolume = 26,
            SmoothVolumeRamp = true,
            EcoMode = "AUTO",
            DynamicVolume = "LIT",
            Restorer = "MED",
            Dimmer = "BRI",
            StreamUrl = "http://live-icy.dr.dk/A/A22L.mp3",
        },
        new ScenePreset
        {
            Id = "party-room",
            Name = "Party Multi-Ch",
            Description = "All-channel stereo, +3 dB bass punch, Dynamic EQ on, full panel brightness.",
            Badge = "PRTY",
            AccentHex = "#EC4899",
            PowerOn = true,
            Unmute = true,
            SoundMode = "MCH STEREO",
            TargetVolume = 54,
            SmoothVolumeRamp = true,
            EcoMode = "OFF",
            DynamicEq = true,
            DynamicVolume = "OFF",
            Restorer = "MED",
            Dimmer = "BRI",
            SubwooferTrimDb = 3,
        },
    ];
}

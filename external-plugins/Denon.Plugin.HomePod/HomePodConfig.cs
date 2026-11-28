namespace Denon.Plugin.HomePod;

/// <summary>Plugin settings, saved as config.json in the plugin data folder.</summary>
internal sealed class HomePodConfig
{
    /// <summary>Look for HomePods on start and every 10 minutes.</summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>Pause the HomePods that play when the Denon receiver turns on (off by default).</summary>
    public bool PauseWhenReceiverPowersOn { get; set; }

    public List<HomePodSpeakerConfig> Speakers { get; set; } = [];
}

/// <summary>
/// One speaker: a single HomePod, or a stereo pair (two HomePods that play as one). The plugin connects to the
/// pair's leader only; it reports what plays on both.
/// </summary>
internal sealed class HomePodSpeakerConfig
{
    /// <summary>The leader's AirPlay device id (stable across IP changes).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"Køkken", or "HomePod Left + Right" for a stereo pair.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The leader's IP address.</summary>
    public string Host { get; set; } = string.Empty;

    public int AirPlayPort { get; set; } = 7000;

    public string ModelName { get; set; } = "HomePod";

    public string? OsVersion { get; set; }

    /// <summary>The names of the HomePods in this speaker (two for a stereo pair), for display.</summary>
    public List<string> Members { get; set; } = [];

    /// <summary>The device ids of the HomePods in this speaker: discovery matches and merges speakers by these.</summary>
    public List<string> MemberIds { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastSeenUtc { get; set; }

    public bool IsStereoPair => Members.Count > 1;
}

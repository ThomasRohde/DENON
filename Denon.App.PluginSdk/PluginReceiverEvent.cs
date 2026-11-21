namespace Denon.App.PluginSdk;

public enum PluginReceiverEventType
{
    PowerChanged = 0,
    InputChanged = 1,
    PlaybackChanged = 2,
    SoundModeChanged = 3,
    SceneActivated = 4,
    VolumeChanged = 5,
}

public sealed record PluginReceiverSnapshot(
    bool IsConnected,
    bool IsPoweredOn,
    int Volume,
    bool IsMuted,
    string InputSource,
    string SoundMode,
    string PlayState,
    string NowPlayingTitle,
    string ModelName)
{
    public static PluginReceiverSnapshot Empty { get; } = new(
        IsConnected: false,
        IsPoweredOn: false,
        Volume: 0,
        IsMuted: false,
        InputSource: string.Empty,
        SoundMode: string.Empty,
        PlayState: "Stopped",
        NowPlayingTitle: string.Empty,
        ModelName: string.Empty);
}

public sealed record PluginReceiverEvent(
    PluginReceiverEventType EventType,
    PluginReceiverSnapshot Snapshot,
    string? SceneId = null,
    string? SceneName = null,
    DateTimeOffset Timestamp = default)
{
    public DateTimeOffset OccurredAt => Timestamp == default ? DateTimeOffset.Now : Timestamp;
}

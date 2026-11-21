namespace Denon.App.Core.Models;

public enum YouTubeSearchMode
{
    Music,
    Videos,
    Playlists,
}

public enum YouTubeRepeatMode
{
    Off,
    All,
    One,
}

public sealed record YouTubeTrackItem(
    string VideoId,
    string Title,
    string Author,
    TimeSpan? Duration,
    string ThumbnailUrl,
    string WatchUrl,
    string? QueueEntryId = null)
{
    public string EntryId => string.IsNullOrWhiteSpace(QueueEntryId) ? VideoId : QueueEntryId;

    public string DurationDisplay => Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : "LIVE";

    public YouTubeTrackItem WithNewQueueEntryId() =>
        this with { QueueEntryId = Guid.NewGuid().ToString("N")[..10] };
}

public sealed record YouTubePlaylistSummary(
    string PlaylistId,
    string Title,
    string Author,
    string ThumbnailUrl,
    string PlaylistUrl);

public sealed record YouTubeSearchResponse(
    IReadOnlyList<YouTubeTrackItem> Tracks,
    IReadOnlyList<YouTubePlaylistSummary> Playlists,
    string SummaryText);

public sealed record RelayStatusSnapshot(
    bool IsRunning,
    string? EndpointBaseUrl,
    string TranscodeMode,
    bool HasFfmpeg,
    string? FfmpegPath,
    string StreamState,
    string? ActiveTrackTitle,
    long BytesStreamed,
    string? LastError)
{
    public static RelayStatusSnapshot Idle { get; } = new(
        IsRunning: false,
        EndpointBaseUrl: null,
        TranscodeMode: "Standby",
        HasFfmpeg: false,
        FfmpegPath: null,
        StreamState: "Idle",
        ActiveTrackTitle: null,
        BytesStreamed: 0,
        LastError: null);
}

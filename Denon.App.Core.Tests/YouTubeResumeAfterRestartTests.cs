using Denon.App.Core.Heos;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Media;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// A YouTube track that was casting when the app closed (an update, a crash) is cast again after the restart when the
/// receiver still reports the app's relay stream, so the title, cover and synced meters come back.
/// </summary>
public sealed class YouTubeResumeAfterRestartTests : IDisposable
{
    private static readonly ReceiverPlayerInfo Player = new("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-yt-resume-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _disposables = [];

    private string QueueDir => Path.Combine(_tempDir, "queues");

    private static ReceiverStateSnapshot Snapshot(
        bool connected = true, bool on = true, string input = "NET", string playState = "play",
        string song = "Url Stream", bool avrConfirmed = true) => new()
    {
        IsConnected = connected,
        Status = new ReceiverStatus(
            Player, playState, 40, false,
            new NowPlayingMedia("song", song, "Url Stream", song == "Url Stream" ? "Url Stream" : "Artist", "", "1", "1", 1, 1024),
            DateTimeOffset.Now,
            IsPoweredOn: on, IsMainZoneOn: on, InputSource: input, SoundMode: "STEREO", IsAvrConfirmed: avrConfirmed),
    };

    [Fact]
    public void The_relay_stream_still_playing_after_a_restart_resumes_the_track() =>
        Assert.True(YouTubeQueueService.ShouldResumeAfterRestart(Snapshot(), lastSourceWasStation: false));

    [Fact]
    public void A_HEOS_speaker_without_the_AVR_protocol_resumes_too() =>
        Assert.True(YouTubeQueueService.ShouldResumeAfterRestart(Snapshot(on: false, input: "", avrConfirmed: false), false));

    [Theory]
    [InlineData(false, true, "NET", "play", "Url Stream")] // not connected
    [InlineData(true, false, "NET", "play", "Url Stream")] // standby
    [InlineData(true, true, "TV", "play", "Url Stream")] // another input
    [InlineData(true, true, "NET", "stop", "Url Stream")] // HEOS gave up on the dead stream
    [InlineData(true, true, "NET", "pause", "Url Stream")]
    [InlineData(true, true, "NET", "play", "Money")] // a real title: another app or service plays
    public void Anything_else_does_not_resume(bool connected, bool on, string input, string playState, string song) =>
        Assert.False(YouTubeQueueService.ShouldResumeAfterRestart(Snapshot(connected, on, input, playState, song), false));

    [Fact]
    public void A_station_the_app_started_last_resumes_as_radio_not_as_YouTube() =>
        Assert.False(YouTubeQueueService.ShouldResumeAfterRestart(Snapshot(), lastSourceWasStation: true));

    [Fact]
    public void The_saved_playing_entry_waits_to_be_cast_again_after_a_restart()
    {
        var store = Track(new MediaQueueStore(QueueDir, Log()));
        store.SaveYouTube(new SavedYouTubeQueue(
            [Saved("abcdefghijk", "One", "e1"), Saved("ABCDEFGHIJK", "Two", "e2")],
            [],
            IsShuffleEnabled: false,
            RepeatMode: YouTubeRepeatMode.Off,
            PlayingEntryId: "e2"));
        store.Flush();

        var queue = CreateQueue();

        Assert.Equal("e2", queue.PendingResumeEntryId);
        Assert.Equal(1, queue.Snapshot.CurrentIndex);
        Assert.False(queue.IsYouTubePlaybackActive); // nothing plays until the receiver confirms its stream
    }

    [Fact]
    public void Another_cast_started_first_cancels_the_resume()
    {
        var store = Track(new MediaQueueStore(QueueDir, Log()));
        store.SaveYouTube(new SavedYouTubeQueue(
            [Saved("abcdefghijk", "One", "e1")], [], false, YouTubeRepeatMode.Off, PlayingEntryId: "e1"));
        store.Flush();

        var queue = CreateQueue();
        Assert.Equal("e1", queue.PendingResumeEntryId);

        // The Jukebox and PC Cast stop the YouTube session before they cast their own relay stream.
        queue.StopSession();

        Assert.Null(queue.PendingResumeEntryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gone")] // the entry is not in the queue
    public void No_playing_entry_means_nothing_to_resume(string? playingEntryId)
    {
        var store = Track(new MediaQueueStore(QueueDir, Log()));
        store.SaveYouTube(new SavedYouTubeQueue(
            [Saved("abcdefghijk", "One", "e1")], [], false, YouTubeRepeatMode.Off, playingEntryId));
        store.Flush();

        Assert.Null(CreateQueue().PendingResumeEntryId);
    }

    private static SavedYouTubeTrack Saved(string videoId, string title, string entryId) =>
        new(videoId, title, "Author", 200, $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg", entryId);

    private AppLogStore Log() => new(Path.Combine(_tempDir, $"app-{Guid.NewGuid():N}.log"));

    private YouTubeQueueService CreateQueue()
    {
        var log = Log();
        var name = Guid.NewGuid().ToString("N")[..6];
        var settings = new AppSettingsStore(log, Path.Combine(_tempDir, $"settings-{name}.json"));
        var telemetry = new EventTelemetryLog(settings, log, new RoomEventBus(log), Path.Combine(_tempDir, $"events-{name}"));
        var stateStore = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), settings, stateStore, telemetry, log);
        var relay = Track(new LocalAudioRelayServer(new YouTubeMediaService(settings, log), settings, log));
        var store = Track(new MediaQueueStore(QueueDir, log));
        return Track(new YouTubeQueueService(relay, controller, stateStore, log, store));
    }

    private T Track<T>(T item)
        where T : IDisposable
    {
        _disposables.Add(item);
        return item;
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_disposables))
        {
            item.Dispose();
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}

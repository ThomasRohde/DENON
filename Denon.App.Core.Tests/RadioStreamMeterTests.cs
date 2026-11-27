using Denon.App.Core.Models;
using Denon.App.Core.Services.Metering;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// Synced meters and Hue Music Sync for internet radio that the app starts on HEOS: a rolling timeline for the
/// endless stream, the clock that says which media time the speakers play, and when the stream counts as playing.
/// </summary>
public sealed class RadioStreamMeterTests
{
    private static readonly ReceiverPlayerInfo Player = new("Denon AVR-X2400H", 101, "Denon AVR-X2400H", "3.1", "192.168.1.3", "wired", 1, "SER");

    private static void AppendFrames(MeterTimeline timeline, int count, int startValue = 0)
    {
        var bands = new double[MeterTimeline.BandCount];
        for (var i = 0; i < count; i++)
        {
            timeline.Append(startValue + i, 0, bands);
        }
    }

    [Fact]
    public void A_rolling_timeline_never_fills_and_keeps_the_newest_frames()
    {
        var timeline = new MeterTimeline("radio:x", hopSeconds: 1, maxFrames: 1, rolling: true);
        Assert.Equal(8192, timeline.MaxFrames); // at least two whole chunks

        AppendFrames(timeline, 20_000);
        Assert.False(timeline.IsFull);
        Assert.Equal(20_000, timeline.FrameCount);

        // The newest frame is there; the oldest ones rolled out.
        Assert.True(timeline.TryGetAt(19_999, out var newest, out _));
        Assert.Equal(19_999, newest);
        Assert.False(timeline.TryGetAt(0, out _, out _));
        Assert.True(timeline.TryGetAt(20_000 - 8192 + 4096, out var kept, out _));
        Assert.Equal(20_000 - 8192 + 4096, kept);

        // A live stream has no end: past the newest frame there is nothing, even when it is marked complete.
        timeline.MarkComplete();
        Assert.False(timeline.TryGetAt(25_000, out _, out _));
    }

    [Fact]
    public void A_track_timeline_still_stops_at_its_cap()
    {
        var timeline = new MeterTimeline("local:x", hopSeconds: 1, maxFrames: 10);
        AppendFrames(timeline, 15);
        Assert.True(timeline.IsFull);
        Assert.Equal(10, timeline.FrameCount);
        Assert.True(timeline.TryGetAt(0, out _, out _));
    }

    [Fact]
    public void The_radio_clock_starts_when_heos_plays_the_new_stream()
    {
        var clock = RadioPlaybackClock.Commanded(nowMs: 10_000);
        Assert.Null(clock.PositionSeconds(10_100));

        clock.OnPlayState("play", nowMs: 10_100); // the old stream's state, right after the command
        Assert.False(clock.HasStarted);

        clock.OnPlayState("stop", nowMs: 10_500);
        clock.OnPlayState("play", nowMs: 12_000); // HEOS buffered and plays the new stream
        Assert.True(clock.HasStarted);
        Assert.Equal(3.0, clock.PositionSeconds(15_000));

        clock.OnPlayState("play", nowMs: 20_000); // later reads do not move the start
        Assert.Equal(8.0, clock.PositionSeconds(20_000));
    }

    [Fact]
    public void Without_a_play_event_the_radio_clock_assumes_the_heos_buffer()
    {
        var clock = RadioPlaybackClock.Commanded(nowMs: 0);
        Assert.Null(clock.PositionSeconds(5_000));
        var assumed = RadioPlaybackClock.AssumedStartDelay.TotalSeconds;
        Assert.Equal(10 - assumed, clock.PositionSeconds(10_000));

        var resumed = RadioPlaybackClock.PlayingSince(nowMs: 1_000);
        Assert.True(resumed.HasStarted);
        Assert.Equal(1.5, resumed.PositionSeconds(2_500));
    }

    private static ReceiverStateSnapshot Snapshot(bool on = true, string input = "NET", string playState = "play", int? sourceId = 1024, string song = "Url Stream") => new()
    {
        IsConnected = true,
        Status = new ReceiverStatus(
            Player, playState, 40, false,
            new NowPlayingMedia("song", song, "", song == "Url Stream" ? "Url Stream" : "Artist", "", "", "", null, sourceId),
            DateTimeOffset.Now,
            IsPoweredOn: on, IsMainZoneOn: on, InputSource: input, SoundMode: "STEREO"),
    };

    [Fact]
    public void Radio_counts_as_playing_only_on_heos_with_the_apps_stream()
    {
        Assert.True(AppStreamMeterService.IsRadioPlaying(Snapshot()));
        Assert.True(AppStreamMeterService.IsRadioPlaying(Snapshot(sourceId: 1029)));
        Assert.False(AppStreamMeterService.IsRadioPlaying(Snapshot(on: false)));
        Assert.False(AppStreamMeterService.IsRadioPlaying(Snapshot(input: "TV")));
        Assert.False(AppStreamMeterService.IsRadioPlaying(Snapshot(playState: "pause")));
        // Spotify, TuneIn or a DLNA track on HEOS is another source, not the app's stream.
        Assert.False(AppStreamMeterService.IsRadioPlaying(Snapshot(sourceId: 4, song: "Real Song")));
        Assert.False(AppStreamMeterService.IsRadioPlaying(ReceiverStateSnapshot.Empty));
    }
}

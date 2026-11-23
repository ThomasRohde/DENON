using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class AudioMeterSyncTests
{
    private static AudioMeterSource Route(
        bool awake = true,
        string input = "TV",
        bool heosPlaying = false,
        bool muted = false,
        bool pcCasting = false,
        bool pcSignal = false) =>
        AudioMeterSourceRouter.Resolve(new AudioMeterInputs(awake, input, heosPlaying, muted, pcCasting, pcSignal));

    [Fact]
    public void Laptop_podcast_is_not_shown_as_denon_audio_while_denon_is_on_tv()
    {
        Assert.Equal(AudioMeterSource.Simulated, Route(input: "TV", pcSignal: true));
    }

    [Fact]
    public void Muted_denon_rests_even_when_the_pc_plays()
    {
        Assert.Equal(AudioMeterSource.Muted, Route(input: "TV", muted: true, pcSignal: true));
    }

    [Fact]
    public void Pc_cast_to_the_denon_is_real_denon_audio()
    {
        Assert.Equal(AudioMeterSource.Denon, Route(input: "NET", pcCasting: true, pcSignal: true));
    }

    [Fact]
    public void Heos_streaming_without_pc_cast_is_simulated()
    {
        Assert.Equal(AudioMeterSource.Simulated, Route(input: "NET", heosPlaying: true, pcSignal: true));
    }

    [Theory]
    [InlineData(false, "TV", false)]  // Denon off
    [InlineData(true, "NET", false)]  // Denon idle on HEOS (paused/stopped)
    public void Idle_or_off_denon_falls_back_to_pc_audio(bool awake, string input, bool heosPlaying)
    {
        Assert.Equal(AudioMeterSource.Pc, Route(awake: awake, input: input, heosPlaying: heosPlaying, pcSignal: true));
        Assert.Equal(AudioMeterSource.Off, Route(awake: awake, input: input, heosPlaying: heosPlaying, pcSignal: false));
    }

    [Fact]
    public void Simulation_fades_out_at_very_low_volume()
    {
        Assert.Equal(0, AudioMeterSourceRouter.SimulationScale(0, 0.30, 0.66));
        Assert.True(AudioMeterSourceRouter.SimulationScale(5, 0.30, 0.66) < AudioMeterSourceRouter.SimulationScale(40, 0.30, 0.66) / 2);
        Assert.Equal(0.30 + (40 / 98.0) * 0.66, AudioMeterSourceRouter.SimulationScale(40, 0.30, 0.66), 6);
    }

    [Fact]
    public void History_returns_the_frame_from_the_heos_delay_ago()
    {
        var history = new AudioFrameHistory(capacity: 256);
        var bands = new double[31];
        for (var t = 0; t <= 5000; t += 20)
        {
            bands[0] = t / 5000.0;
            history.Add(10_000 + t, left: t / 5000.0, right: 0.5, isLive: true, bands);
        }

        // "Now" is 15 000; with a 1 800 ms HEOS delay the speakers play the frame from 13 200.
        var output = new double[31];
        Assert.True(history.TryGetAt(15_000 - 1_800, out var left, out var right, output));
        Assert.Equal(3200 / 5000.0, left, 6);
        Assert.Equal(0.5, right, 6);
        Assert.Equal(3200 / 5000.0, output[0], 6);
    }

    [Fact]
    public void History_reports_no_signal_for_silence_gaps_and_overwritten_frames()
    {
        var history = new AudioFrameHistory(capacity: 4) { MaxGapMs = 400 };
        history.Add(1_000, 0.7, 0.7, isLive: true);
        history.Add(1_020, 0, 0, isLive: false);

        Assert.False(history.TryGetAt(1_030, out _, out _));   // Newest frame before target is silent.
        Assert.True(history.TryGetAt(1_010, out var l, out _)); // Before the silence: live.
        Assert.Equal(0.7, l, 6);
        Assert.False(history.TryGetAt(2_000, out _, out _));    // Gap > 400 ms: no signal then.

        for (var t = 3_000; t < 3_100; t += 20)
        {
            history.Add(t, 0.1, 0.1, isLive: true);
        }

        Assert.False(history.TryGetAt(1_010, out _, out _));    // Overwritten by the ring.
    }
}

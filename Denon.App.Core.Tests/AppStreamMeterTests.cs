using System.Diagnostics;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class AppStreamMeterTests
{
    private const int Rate = AppStreamMeterService.SampleRate;

    private static float[] Sine(double seconds, double hz, float amplitude, bool leftOnly = false)
    {
        var frames = (int)(seconds * Rate);
        var pcm = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
            pcm[i * 2] = v;
            pcm[(i * 2) + 1] = leftOnly ? 0f : v;
        }

        return pcm;
    }

    private static MeterTimeline NewTimeline(int maxFrames = 100_000) =>
        new("test", AppStreamMeterService.HopSeconds, maxFrames);

    [Fact]
    public void Timeline_returns_the_frame_at_the_media_position()
    {
        var timeline = new MeterTimeline("t", hopSeconds: 0.1, maxFrames: 100);
        var bands = new double[31];
        for (var i = 0; i < 10; i++)
        {
            bands[0] = i / 10.0;
            timeline.Append(i / 10.0, 1 - (i / 10.0), bands);
        }

        var output = new double[31];
        Assert.True(timeline.TryGetAt(0.55, out var left, out var right, output));
        Assert.Equal(0.5, left, 5);
        Assert.Equal(0.5, right, 5);
        Assert.Equal(0.5, output[0], 5);

        Assert.False(timeline.TryGetAt(-1, out _, out _));      // Clock not started.
        Assert.False(timeline.TryGetAt(1.5, out _, out _));     // Not analyzed yet: use the simulation.
    }

    [Fact]
    public void Timeline_returns_silence_after_the_end_only_when_the_whole_track_was_analyzed()
    {
        var done = new MeterTimeline("t", 0.1, 100);
        done.Append(0.8, 0.8, new double[31]);
        done.MarkComplete();
        Assert.True(done.TryGetAt(5, out var l, out _));
        Assert.Equal(0, l);

        var failed = new MeterTimeline("t", 0.1, 100);
        failed.Append(0.8, 0.8, new double[31]);
        failed.MarkComplete(failed: true);
        Assert.False(failed.TryGetAt(5, out _, out _));

        var capped = new MeterTimeline("t", 0.1, maxFrames: 1);
        Assert.True(capped.Append(0.8, 0.8, new double[31]));
        Assert.False(capped.Append(0.8, 0.8, new double[31]));
        capped.MarkComplete();
        Assert.False(capped.TryGetAt(5, out _, out _)); // Past the cap the track still plays: no fake silence.
    }

    [Fact]
    public void Analyzer_builds_one_frame_per_hop_with_the_live_meter_scale()
    {
        var timeline = NewTimeline();
        var analyzer = new PcmMeterAnalyzer(timeline, Rate, AppStreamMeterService.HopFrames);

        analyzer.Process(Sine(1.0, 1000, 0.5f));
        analyzer.Flush();

        Assert.Equal(Rate / AppStreamMeterService.HopFrames, timeline.FrameCount);

        // Same scale as the live PC meter: peak/RMS blend over 40 dB.
        var expected = AudioMeterMath.ToNormalizedLevel(0.5, 0.5 / Math.Sqrt(2));
        var bands = new double[31];
        Assert.True(timeline.TryGetAt(0.5, out var left, out var right, bands));
        Assert.Equal(expected, left, 2);
        Assert.Equal(expected, right, 2);

        // 1 kHz lands in the band around 1 kHz (band 17 of 31, 25 Hz → 16 kHz).
        var loudest = Array.IndexOf(bands, bands.Max());
        Assert.InRange(loudest, 16, 18);
        Assert.True(bands[3] < bands[loudest] / 3, "Low bands must stay low for a 1 kHz tone.");
    }

    [Fact]
    public void Analyzer_keeps_channels_apart_and_writes_silence_as_zero()
    {
        var timeline = NewTimeline();
        var analyzer = new PcmMeterAnalyzer(timeline, Rate, AppStreamMeterService.HopFrames);

        analyzer.Process(Sine(0.5, 440, 0.5f, leftOnly: true));
        analyzer.Process(new float[Rate]); // 0.5 s of silence
        analyzer.Flush();

        Assert.True(timeline.TryGetAt(0.2, out var left, out var right));
        Assert.True(left > 0.5);
        Assert.Equal(0, right);

        var bands = new double[31];
        Assert.True(timeline.TryGetAt(0.8, out left, out right, bands));
        Assert.Equal(0, left);
        Assert.Equal(0, right);
        Assert.All(bands, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Playback_clock_stops_during_pause_and_subtracts_the_heos_delay()
    {
        var clock = new PlaybackClock();
        var t0 = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.False(clock.IsStarted);

        clock.Restart(t0);
        Assert.Equal(8, clock.GetPositionSeconds(t0.AddSeconds(10), bufferDelayMs: 2000), 6);
        Assert.Equal(0, clock.GetPositionSeconds(t0.AddSeconds(1), bufferDelayMs: 2000), 6); // Never negative.

        clock.Pause(t0.AddSeconds(10));
        Assert.Equal(8, clock.GetPositionSeconds(t0.AddSeconds(60), 2000), 6);

        clock.Resume(t0.AddSeconds(60));
        Assert.Equal(13, clock.GetPositionSeconds(t0.AddSeconds(65), 2000), 6);

        Assert.Equal(29, clock.GetPositionSeconds(t0.AddSeconds(500), 0, duration: TimeSpan.FromSeconds(30)), 6);

        clock.Reset();
        Assert.False(clock.IsStarted);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("bytes=0-", true)]
    [InlineData("bytes=0-1023", true)]
    [InlineData("bytes=1048576-", false)]
    [InlineData("bytes=-128", false)]
    public void Only_a_read_from_the_start_restarts_the_jukebox_clock(string? range, bool expected)
    {
        Assert.Equal(expected, LocalAudioRelayServer.IsReadFromStart(range));
    }

    [Fact]
    public async Task Ffmpeg_decode_fills_the_timeline_from_a_file()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return; // FFmpeg is optional for the app; CI may not have it.
        }

        var dir = Path.Combine(Path.GetTempPath(), "denon-meter-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 2 s of a 440 Hz tone, then 1 s of silence.
            var pcm = Sine(2.0, 440, 0.5f).Concat(new float[Rate * 2]).ToArray();
            var wav = Path.Combine(dir, "tone.wav");
            WriteFloatWav(wav, pcm);

            var timeline = NewTimeline();
            var ok = await AppStreamMeterService.DecodeToTimelineAsync(ffmpeg, wav, openInput: null, timeline, CancellationToken.None);

            Assert.True(ok);
            Assert.InRange(timeline.AnalyzedSeconds, 2.9, 3.05);
            Assert.True(timeline.TryGetAt(1.0, out var left, out _));
            Assert.True(left > 0.5);
            Assert.True(timeline.TryGetAt(2.5, out left, out _));
            Assert.Equal(0, left);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void WriteFloatWav(string path, float[] interleavedStereo)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var dataBytes = interleavedStereo.Length * 4;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)3); // IEEE float
        w.Write((short)2);
        w.Write(Rate);
        w.Write(Rate * 8);
        w.Write((short)8);
        w.Write((short)32);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in interleavedStereo)
        {
            w.Write(s);
        }
    }

    private static string? FindFfmpeg()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "ffmpeg.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}

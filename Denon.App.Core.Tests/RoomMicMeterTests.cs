using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Denon.App.Desktop.Controls;
using Denon.App.Desktop.Infrastructure.Theming;
using Denon.App.Desktop.Services;
using Denon.App.PluginSdk;
using NAudio.Wave;
using Xunit;
using Color = System.Windows.Media.Color;

namespace Denon.App.Core.Tests;

public sealed class RoomMicMeterTests
{
    // ── Smart privacy-first Room Mic gating ──────────────────────────────────────────────────────────

    [Fact]
    public void Room_mic_gate_stays_closed_when_disabled()
    {
        var inputs = new RoomMicGateInputs(
            RoomMicEnabled: false,
            MetersVisible: true,
            HueMusicSyncActive: true,
            IsReceiverConnected: true,
            IsReceiverAwake: true,
            IsReceiverMuted: false,
            InputSource: "TV",
            IsHeosPlaying: false,
            IsPcCasting: false,
            HasAppStreamFrame: false,
            HasPcLoopbackSignal: false);

        Assert.False(RoomMicGate.ShouldCapture(in inputs));
    }

    [Theory]
    [InlineData("TV", false)]
    [InlineData("MPLAY", false)]
    [InlineData("BD", false)]
    [InlineData("GAME", false)]
    [InlineData("NET", true)] // e.g., Spotify / TuneIn on HEOS without an app-tapped stream
    public void Room_mic_gate_opens_only_for_untappable_sources_when_enabled_and_visible(string input, bool heosPlaying)
    {
        var inputs = new RoomMicGateInputs(
            RoomMicEnabled: true,
            MetersVisible: true,
            HueMusicSyncActive: false,
            IsReceiverConnected: true,
            IsReceiverAwake: true,
            IsReceiverMuted: false,
            InputSource: input,
            IsHeosPlaying: heosPlaying,
            IsPcCasting: false,
            HasAppStreamFrame: false,
            HasPcLoopbackSignal: false);

        Assert.True(RoomMicGate.ShouldCapture(in inputs));
    }

    [Fact]
    public void Room_mic_gate_closes_immediately_on_standby_mute_disconnect_or_idle_net()
    {
        var baseOpen = new RoomMicGateInputs(
            RoomMicEnabled: true,
            MetersVisible: true,
            HueMusicSyncActive: false,
            IsReceiverConnected: true,
            IsReceiverAwake: true,
            IsReceiverMuted: false,
            InputSource: "TV",
            IsHeosPlaying: false,
            IsPcCasting: false,
            HasAppStreamFrame: false,
            HasPcLoopbackSignal: false);

        Assert.True(RoomMicGate.ShouldCapture(in baseOpen));
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { IsReceiverConnected = false }));
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { IsReceiverAwake = false }));
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { IsReceiverMuted = true }));
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { InputSource = "NET", IsHeosPlaying = false }));
    }

    [Fact]
    public void Room_mic_gate_closes_when_meters_hidden_unless_hue_sync_is_active_and_receiver_is_awake_unmuted()
    {
        var hiddenNoHue = new RoomMicGateInputs(
            RoomMicEnabled: true,
            MetersVisible: false,
            HueMusicSyncActive: false,
            IsReceiverConnected: true,
            IsReceiverAwake: true,
            IsReceiverMuted: false,
            InputSource: "TV",
            IsHeosPlaying: false,
            IsPcCasting: false,
            HasAppStreamFrame: false,
            HasPcLoopbackSignal: false);

        Assert.False(RoomMicGate.ShouldCapture(in hiddenNoHue));
        Assert.True(RoomMicGate.ShouldCapture(hiddenNoHue with { HueMusicSyncActive = true }));
        // Even with Hue Music Sync active, mic must release if receiver goes muted or standby!
        Assert.False(RoomMicGate.ShouldCapture(hiddenNoHue with { HueMusicSyncActive = true, IsReceiverMuted = true }));
        Assert.False(RoomMicGate.ShouldCapture(hiddenNoHue with { HueMusicSyncActive = true, IsReceiverAwake = false }));
    }

    [Fact]
    public void Room_mic_gate_closes_when_pc_loopback_or_digital_app_stream_already_provides_audio()
    {
        var baseOpen = new RoomMicGateInputs(
            RoomMicEnabled: true,
            MetersVisible: true,
            HueMusicSyncActive: false,
            IsReceiverConnected: true,
            IsReceiverAwake: true,
            IsReceiverMuted: false,
            InputSource: "TV",
            IsHeosPlaying: false,
            IsPcCasting: false,
            HasAppStreamFrame: false,
            HasPcLoopbackSignal: false);

        // PC loopback already active -> keep mic closed
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { HasPcLoopbackSignal = true }));
        // PC Cast active on NET -> keep mic closed
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { InputSource = "NET", IsPcCasting = true }));
        // Analyzed YouTube / Jukebox playing on NET -> keep mic closed
        Assert.False(RoomMicGate.ShouldCapture(baseOpen with { InputSource = "NET", IsHeosPlaying = true, HasAppStreamFrame = true }));
    }

    [Fact]
    public void LiveAudioAnalyzerService_evaluates_room_mic_gate_from_live_state_and_settings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "denon-room-mic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logStore = new AppLogStore();
            var settingsStore = new AppSettingsStore(logStore, Path.Combine(tempDir, "settings.json"));
            var stateStore = new ReceiverStateStore();
            var telemetry = new EventTelemetryLog(settingsStore, logStore, Path.Combine(tempDir, "events"));
            var controller = new ReceiverController(
                new HeosClient(),
                new DenonAvrClient(),
                settingsStore,
                stateStore,
                telemetry,
                logStore);
            var pluginHost = new PluginHostService([], settingsStore, stateStore, controller, telemetry, logStore);

            using var service = new LiveAudioAnalyzerService(pluginHost, logStore, stateStore, settingsStore);

            Assert.False(service.IsRoomMicEnabled);
            Assert.False(service.IsRoomMicActive);
            Assert.False(service.EvaluateRoomMicShouldCapture());

            // Receiver awake on TV, meters visible, but opt-in is still OFF -> closed
            stateStore.UpdateStatus(CreateStatus(poweredOn: true, mainZoneOn: true, input: "TV", muted: false));
            service.SetVuCaptureRequested(true);
            Assert.False(service.EvaluateRoomMicShouldCapture());

            // Enable Room Mic -> gate opens
            service.SetRoomMicEnabled(true);
            Assert.True(service.EvaluateRoomMicShouldCapture());

            // Mute receiver -> gate closes immediately
            stateStore.UpdateStatus(CreateStatus(poweredOn: true, mainZoneOn: true, input: "TV", muted: true));
            Assert.False(service.EvaluateRoomMicShouldCapture());

            // Unmute -> opens again
            stateStore.UpdateStatus(CreateStatus(poweredOn: true, mainZoneOn: true, input: "TV", muted: false));
            Assert.True(service.EvaluateRoomMicShouldCapture());

            // Hide meters (and no Hue sync) -> gate closes
            service.SetVuCaptureRequested(false);
            Assert.False(service.EvaluateRoomMicShouldCapture());

            // Show meters again, then standby -> gate closes
            service.SetVuCaptureRequested(true);
            Assert.True(service.EvaluateRoomMicShouldCapture());
            stateStore.UpdateStatus(CreateStatus(poweredOn: false, mainZoneOn: false, input: "TV", muted: false));
            Assert.False(service.EvaluateRoomMicShouldCapture());

            // Wake up, then disconnect -> gate closes
            stateStore.UpdateStatus(CreateStatus(poweredOn: true, mainZoneOn: true, input: "TV", muted: false));
            Assert.True(service.EvaluateRoomMicShouldCapture());
            stateStore.UpdateError("Network unreachable");
            Assert.False(service.EvaluateRoomMicShouldCapture());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Studio-grade Room Mic DSP (Noise Gate, Adaptive Floor, AGC, Formats, Mono vs Stereo) ─────────

    [Fact]
    public void Quiet_room_noise_rests_cleanly_at_zero_and_minus_20_vu()
    {
        var dsp = new RoomMicDspProcessor();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var bands = new double[31];
        var rng = new Random(42);
        PluginAudioFrame last = default;

        // Feed 1.2 seconds of low-level ambient room hiss (~0.0005 RMS, ~-66 dBFS)
        for (var block = 0; block < 60; block++)
        {
            var buf = GenerateNoiseFloat(48000, 1, 960, amplitude: 0.0008f, rng);
            last = dsp.ProcessBuffer(buf, buf.Length, format, block * 20L, bands);
        }

        Assert.False(dsp.IsGateOpen);
        Assert.Equal(0.0, last.LeftLevel);
        Assert.Equal(0.0, last.RightLevel);
        Assert.False(last.IsLiveSignal);
        Assert.Equal(0.0, VuMeterScale.LevelToDeflection(last.LeftLevel));
        Assert.All(bands, b => Assert.Equal(0.0, b));
    }

    [Fact]
    public void Adaptive_noise_floor_adapts_to_steady_ambient_fan_noise_and_closes_gate()
    {
        var dsp = new RoomMicDspProcessor();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var bands = new double[31];
        var rng = new Random(1337);
        PluginAudioFrame last = default;

        // Constant ambient fan/HVAC noise at ~0.0045 peak (~0.0026 RMS, initially above 0.0008 default floor)
        for (var block = 0; block < 600; block++) // 12 seconds: a rise of more than ~8 dB needs a full 8 s window
        {
            var buf = GenerateNoiseFloat(48000, 1, 960, amplitude: 0.0045f, rng);
            last = dsp.ProcessBuffer(buf, buf.Length, format, block * 20L, bands);
        }

        Assert.True(dsp.NoiseFloorRms > 0.0018, $"Expected adaptive floor to rise toward fan noise, got {dsp.NoiseFloorRms:F5}");
        Assert.False(dsp.IsGateOpen);
        Assert.Equal(0.0, last.LeftLevel);
        Assert.Equal(0.0, last.RightLevel);
    }

    [Fact]
    public void Quiet_program_audio_with_natural_dips_keeps_the_gate_open_for_30_seconds()
    {
        var dsp = new RoomMicDspProcessor();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var bands = new double[31];
        var phase = 0.0;
        PluginAudioFrame last = default;

        // Quiet TV level (0.015) with a 120 ms pause every 1.5 s, as between words or notes. The floor must not
        // learn the program as noise (it did with a rising average: the gate closed after ~9 s).
        for (var block = 0; block < 1500; block++)
        {
            var amp = block % 75 < 6 ? 0.0003f : 0.015f;
            var buf = GenerateSineFloat(48000, 2, 960, freqHz: 440.0, leftAmp: amp, rightAmp: amp, ref phase);
            last = dsp.ProcessBuffer(buf, buf.Length, format, block * 20L, bands);
        }

        Assert.True(dsp.IsGateOpen, $"Gate closed on program audio; floor {dsp.NoiseFloorRms:F5}");
        Assert.True(last.IsLiveSignal);
        Assert.InRange(last.LeftLevel, 0.4, 1.0);
    }

    [Fact]
    public void Agc_brings_both_quiet_and_loud_denon_volumes_into_the_vu_sweet_spot_with_fast_attack_and_slow_release()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var bands = new double[31];

        // 1. Quiet evening TV/music level (0.015 amplitude, ~-36 dBFS) -> AGC boosts cleanly into VU sweet spot
        var quietDsp = new RoomMicDspProcessor();
        PluginAudioFrame quietFrame = default;
        var phase = 0.0;
        for (var block = 0; block < 250; block++) // 5 seconds so slow release converges
        {
            var buf = GenerateSineFloat(48000, 2, 960, freqHz: 440.0, leftAmp: 0.015f, rightAmp: 0.015f, ref phase);
            quietFrame = quietDsp.ProcessBuffer(buf, buf.Length, format, block * 20L, bands);
        }

        Assert.True(quietFrame.IsLiveSignal);
        Assert.InRange(quietFrame.LeftLevel, 0.55, 0.98);
        Assert.InRange(quietFrame.RightLevel, 0.55, 0.98);
        Assert.True(quietDsp.AgcGain > 10.0, $"Expected AGC boost for quiet source, got {quietDsp.AgcGain:F2}");

        // 2. Sudden loud jump (0.75 amplitude) -> fast attack pulls gain down rapidly within ~160 ms
        var gainBeforeLoud = quietDsp.AgcGain;
        PluginAudioFrame loudFrame = default;
        for (var block = 0; block < 10; block++) // 200 ms
        {
            var buf = GenerateSineFloat(48000, 2, 960, freqHz: 440.0, leftAmp: 0.75f, rightAmp: 0.75f, ref phase);
            loudFrame = quietDsp.ProcessBuffer(buf, buf.Length, format, 5000 + (block * 20L), bands);
        }

        var gainAfterFastAttack = quietDsp.AgcGain;
        Assert.True(gainAfterFastAttack < gainBeforeLoud * 0.25,
            $"Expected fast attack attenuation: before={gainBeforeLoud:F2}, after 200ms={gainAfterFastAttack:F2}");
        Assert.InRange(loudFrame.LeftLevel, 0.55, 0.99);

        // 3. Drop back to quiet (0.015) for 200 ms -> slow release moves much more gradually than fast attack
        for (var block = 0; block < 10; block++) // 200 ms
        {
            var buf = GenerateSineFloat(48000, 2, 960, freqHz: 440.0, leftAmp: 0.015f, rightAmp: 0.015f, ref phase);
            _ = quietDsp.ProcessBuffer(buf, buf.Length, format, 5200 + (block * 20L), bands);
        }

        var gainAfterShortRelease = quietDsp.AgcGain;
        var attackDelta = Math.Abs(gainBeforeLoud - gainAfterFastAttack);
        var releaseDelta = Math.Abs(gainAfterShortRelease - gainAfterFastAttack);
        Assert.True(releaseDelta < attackDelta * 0.35,
            $"Expected slow release vs fast attack: attackDelta={attackDelta:F2}, releaseDelta={releaseDelta:F2}");
    }

    [Fact]
    public void Room_mic_dsp_supports_both_32bit_float_and_16bit_pcm_capture_formats()
    {
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var pcm16Format = new WaveFormat(44100, 16, 1);

        var floatDsp = new RoomMicDspProcessor();
        var pcmDsp = new RoomMicDspProcessor();
        var floatBands = new double[31];
        var pcmBands = new double[31];

        double phaseF = 0, phaseP = 0;
        PluginAudioFrame floatFrame = default, pcmFrame = default;
        for (var block = 0; block < 25; block++)
        {
            var fBuf = GenerateSineFloat(48000, 1, 960, 1000.0, 0.08f, 0.08f, ref phaseF);
            floatFrame = floatDsp.ProcessBuffer(fBuf, fBuf.Length, floatFormat, block * 20L, floatBands);

            var pBuf = GenerateSinePcm16(44100, 1, 882, 1000.0, 0.08f, 0.08f, ref phaseP);
            pcmFrame = pcmDsp.ProcessBuffer(pBuf, pBuf.Length, pcm16Format, block * 20L, pcmBands);
        }

        Assert.True(floatFrame.IsLiveSignal);
        Assert.True(pcmFrame.IsLiveSignal);
        Assert.True(floatFrame.LeftLevel > 0.5);
        Assert.True(pcmFrame.LeftLevel > 0.5);
        Assert.True(floatBands.Max() > 0.35);
        Assert.True(pcmBands.Max() > 0.35);
    }

    [Fact]
    public void Mono_mic_has_subtle_organic_lr_separation_while_stereo_preserves_true_lr()
    {
        // Mono 1-channel mic: subtle organic L/R separation
        var monoDsp = new RoomMicDspProcessor();
        var monoFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var bands = new double[31];
        var phase = 0.0;
        var maxMonoDiff = 0.0;

        for (var block = 0; block < 40; block++)
        {
            var freq = block % 2 == 0 ? 140.0 : 2400.0;
            var buf = GenerateSineFloat(48000, 1, 960, freq, 0.08f, 0.08f, ref phase);
            var frame = monoDsp.ProcessBuffer(buf, buf.Length, monoFormat, block * 20L, bands);
            var diff = Math.Abs(frame.LeftLevel - frame.RightLevel);
            if (diff > maxMonoDiff) maxMonoDiff = diff;
        }

        Assert.True(maxMonoDiff > 0.005, $"Expected subtle organic L/R spread on mono mic, got maxDiff={maxMonoDiff:F4}");
        Assert.True(maxMonoDiff < 0.18, $"Expected mono L/R spread to stay subtle, got maxDiff={maxMonoDiff:F4}");

        // Stereo 2-channel mic / Line-In: real L/R separation preserved (strong Left, near-silent Right)
        var stereoDsp = new RoomMicDspProcessor();
        var stereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        phase = 0.0;
        PluginAudioFrame stereoFrame = default;
        for (var block = 0; block < 25; block++)
        {
            var buf = GenerateSineFloat(48000, 2, 960, 440.0, leftAmp: 0.25f, rightAmp: 0.002f, ref phase);
            stereoFrame = stereoDsp.ProcessBuffer(buf, buf.Length, stereoFormat, block * 20L, bands);
        }

        Assert.True(stereoFrame.LeftLevel > 0.65, $"Expected strong left channel, got {stereoFrame.LeftLevel:F3}");
        Assert.True(stereoFrame.RightLevel < 0.25, $"Expected quiet right channel preserved, got {stereoFrame.RightLevel:F3}");
        Assert.True(stereoFrame.LeftLevel - stereoFrame.RightLevel > 0.45);
    }

    // ── PC Cast non-default RTA & Hue Music Sync routing ─────────────────────────────────────────────

    [Fact]
    public void Pc_cast_pipeline_measures_center_channel_dialogue_on_a_5_1_endpoint()
    {
        // 5.1 float capture with sound only on the center channel (2): the cast folds it into L/R, so must the meter.
        var pipeline = new PcCastAudioMeterPipeline(historyCapacity: 64);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 6);
        var phase = 0.0;
        PluginAudioFrame frame = default;
        for (var block = 0; block < 10; block++)
        {
            var buf = new byte[960 * 6 * 4];
            for (var i = 0; i < 960; i++)
            {
                var sample = (float)(0.3 * Math.Sin(phase));
                phase += 2 * Math.PI * 440.0 / 48000;
                BitConverter.GetBytes(sample).CopyTo(buf, ((i * 6) + 2) * 4);
            }

            frame = pipeline.ProcessCaptureBuffer(buf, buf.Length, format, timestampMs: block * 20L);
        }

        Assert.True(frame.IsLiveSignal);
        Assert.True(frame.LeftLevel > 0.5 && frame.RightLevel > 0.5);
    }

    [Fact]
    public void Pc_cast_pipeline_computes_delayed_31_band_rta_and_plugin_frame_for_non_default_device()
    {
        var pipeline = new PcCastAudioMeterPipeline(historyCapacity: 512);
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var phase = 0.0;

        // Simulate 2.4 s of 1 kHz audio captured from a non-default Windows output device
        for (var t = 0; t <= 2400; t += 20)
        {
            var buf = GenerateSineFloat(48000, 2, 960, 1000.0, 0.35f, 0.25f, ref phase);
            pipeline.ProcessCaptureBuffer(buf, buf.Length, floatFormat, timestampMs: 10_000 + t);
        }

        // Read at 1800 ms HEOS delay (target tick = 10_600)
        var outBands = new double[31];
        Assert.True(pipeline.TryGetDelayedFrameAt(nowTickMs: 12_400, delayMs: 1800, out var left, out var right, outBands));
        Assert.True(left > right && left > 0.5);

        // 1 kHz sits in the upper-mid bands (~band 16..18 out of 31 log-spaced 25 Hz..16 kHz)
        var peakBand = Array.IndexOf(outBands, outBands.Max());
        Assert.InRange(peakBand, 14, 20);
        Assert.True(outBands[peakBand] > 0.4);

        // Also verify full delayed PluginAudioFrame for Hue Music Sync
        Assert.True(pipeline.TryGetDelayedAudioFrameAt(nowTickMs: 12_400, delayMs: 1800, out var delayedFrame));
        Assert.True(delayedFrame.IsLiveSignal);
        Assert.True(delayedFrame.MidEnergy > delayedFrame.BassEnergy);

        // Also verify 16-bit PCM capture format (120 Hz bass) works identically
        pipeline.Reset();
        var pcm16Format = new WaveFormat(44100, 16, 2);
        phase = 0.0;
        for (var t = 0; t <= 600; t += 20)
        {
            var buf = GenerateSinePcm16(44100, 2, 882, 120.0, 0.4f, 0.4f, ref phase);
            pipeline.ProcessCaptureBuffer(buf, buf.Length, pcm16Format, timestampMs: 20_000 + t);
        }

        Array.Clear(outBands);
        Assert.True(pipeline.TryGetDelayedAudioFrameAt(nowTickMs: 20_600, delayMs: 200, out var bassFrame, outBands));
        Assert.True(bassFrame.BassEnergy > bassFrame.TrebleEnergy);
        var bassPeakBand = Array.IndexOf(outBands, outBands.Max());
        Assert.InRange(bassPeakBand, 0, 10);
        Assert.True(outBands[..9].Average() > outBands[20..].Average() * 3.0);
    }

    [Fact]
    public void Audio_meter_router_never_falls_back_to_sim_during_non_default_pc_cast()
    {
        var pipeline = new PcCastAudioMeterPipeline();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var phase = 0.0;
        for (var t = 0; t <= 400; t += 20)
        {
            var buf = GenerateSineFloat(48000, 2, 960, 880.0, 0.30f, 0.30f, ref phase);
            pipeline.ProcessCaptureBuffer(buf, buf.Length, format, 1000 + t);
        }

        // Non-default PC Cast: HasPcSignal (default loopback) is FALSE, IsPcCasting is TRUE
        var inputs = new AudioMeterInputs(
            IsDenonAwake: true,
            InputSource: "NET",
            IsHeosPlaying: true,
            IsMuted: false,
            IsPcCasting: true,
            HasPcSignal: false,
            HasAppStreamFrame: false,
            IsRoomMicActive: false);

        var bands = new double[31];
        var source = AudioMeterRouter.ResolveSpectrum(
            in inputs,
            tryCopyAppStreamBands: _ => false,
            tryCopyPcCastDelayedBands: dest => pipeline.TryGetDelayedFrameAt(1400, 200, out _, out _, dest),
            tryCopyRoomMicBands: _ => false,
            tryCopyPcLoopbackBands: _ => false,
            bandsDestination: bands);

        Assert.Equal(AudioMeterSource.Denon, source);
        Assert.True(bands.Max() > 0.35);

        // Even during a pause/silence in non-default PC Cast, stays DENON (cleared to 0) — never SIM!
        var silentSource = AudioMeterRouter.ResolveSpectrum(
            in inputs,
            tryCopyAppStreamBands: _ => false,
            tryCopyPcCastDelayedBands: _ => false,
            tryCopyRoomMicBands: _ => false,
            tryCopyPcLoopbackBands: _ => false,
            bandsDestination: bands);

        Assert.Equal(AudioMeterSource.Denon, silentSource);
        Assert.All(bands, b => Assert.Equal(0.0, b));
    }

    [Fact]
    public void Hue_music_sync_router_respects_heos_delay_room_mic_app_stream_and_mute()
    {
        var rawLoopback = new PluginAudioFrame(0.95, 0.95, 0.90, 0.80, 0.70, 0.85, IsBeat: true, IsLiveSignal: true, TimestampMs: 5000);
        var delayedCast = new PluginAudioFrame(0.42, 0.38, 0.55, 0.30, 0.20, 0.40, IsBeat: false, IsLiveSignal: true, TimestampMs: 3200);
        var appStream = new PluginAudioFrame(0.60, 0.62, 0.71, 0.48, 0.33, 0.66, IsBeat: true, IsLiveSignal: true, TimestampMs: 5000);
        var roomMic = new PluginAudioFrame(0.51, 0.49, 0.62, 0.41, 0.25, 0.50, IsBeat: true, IsLiveSignal: true, TimestampMs: 5000);

        // 1. PC Cast -> uses HEOS-delayed cast frame (NOT raw un-delayed PC loopback!)
        var castCtx = new AudioMeterRouteContext(
            new AudioMeterInputs(true, "NET", true, false, IsPcCasting: true, HasPcSignal: true),
            delayedCast, HasPcCastDelayedFrame: true,
            default, HasAppStreamFrame: false,
            roomMic, rawLoopback);
        var (castSrc, castFrame) = AudioMeterRouter.ResolvePluginAudioFrame(in castCtx, 5000);
        Assert.Equal(AudioMeterSource.Denon, castSrc);
        Assert.Equal(delayedCast.LeftLevel, castFrame.LeftLevel, 6);
        Assert.Equal(delayedCast.BassEnergy, castFrame.BassEnergy, 6);

        // 2. YouTube / Jukebox AppStream -> uses synced AppStream frame
        var streamCtx = new AudioMeterRouteContext(
            new AudioMeterInputs(true, "NET", true, false, IsPcCasting: false, HasPcSignal: true, HasAppStreamFrame: true),
            default, HasPcCastDelayedFrame: false,
            appStream, HasAppStreamFrame: true,
            roomMic, rawLoopback);
        var (streamSrc, streamFrame) = AudioMeterRouter.ResolvePluginAudioFrame(in streamCtx, 5000);
        Assert.Equal(AudioMeterSource.Denon, streamSrc);
        Assert.Equal(appStream.BassEnergy, streamFrame.BassEnergy, 6);

        // 3. TV / HDMI with Room Mic active -> uses Room Mic frame
        var roomCtx = new AudioMeterRouteContext(
            new AudioMeterInputs(true, "TV", false, false, IsPcCasting: false, HasPcSignal: false, HasAppStreamFrame: false, IsRoomMicActive: true),
            default, false, default, false, roomMic, rawLoopback);
        var (roomSrc, roomFrame) = AudioMeterRouter.ResolvePluginAudioFrame(in roomCtx, 5000);
        Assert.Equal(AudioMeterSource.Room, roomSrc);
        Assert.Equal(roomMic.BassEnergy, roomFrame.BassEnergy, 6);

        // 4. Muted Denon -> sends silence even when raw PC loopback and Room Mic are live
        var mutedCtx = roomCtx with
        {
            MeterInputs = roomCtx.MeterInputs with { IsMuted = true },
        };
        var (mutedSrc, mutedFrame) = AudioMeterRouter.ResolvePluginAudioFrame(in mutedCtx, 5000);
        Assert.Equal(AudioMeterSource.Muted, mutedSrc);
        Assert.False(mutedFrame.IsLiveSignal);
        Assert.Equal(0.0, mutedFrame.BassEnergy);

        // 5. Denon off/idle -> falls back to PC loopback so Hue still syncs to PC audio
        var pcCtx = new AudioMeterRouteContext(
            new AudioMeterInputs(false, "TV", false, false, IsPcCasting: false, HasPcSignal: true),
            default, false, default, false, default, rawLoopback);
        var (pcSrc, pcFrame) = AudioMeterRouter.ResolvePluginAudioFrame(in pcCtx, 5000);
        Assert.Equal(AudioMeterSource.Pc, pcSrc);
        Assert.Equal(rawLoopback.BassEnergy, pcFrame.BassEnergy, 6);
    }

    // ── Room Badge, RTA Label & MeterPalette Contrast (Dark + Light, all skins) ──────────────────────

    [Fact]
    public void Room_source_badges_and_palette_contrast_meet_standards_across_all_skins_and_themes()
    {
        Assert.Equal("ROOM", AudioMeterSourceRouter.BadgeText(AudioMeterSource.Room));
        Assert.Equal("ROOM MIC FFT", AudioMeterSourceRouter.RtaLabel(AudioMeterSource.Room));

        foreach (var skin in ThemeManager.Skins)
        {
            foreach (var palette in new[] { skin.Meters, skin.LightMeters })
            {
                var ratio = Contrast(palette.SourceRoom, palette.FaceMiddle);
                Assert.True(ratio >= 3.0, $"Skin '{skin.Id}' SourceRoom contrast too low: {ratio:0.00} < 3.0");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static ReceiverStatus CreateStatus(bool poweredOn, bool mainZoneOn, string input, bool muted, string playState = "stop") =>
        new(
            new ReceiverPlayerInfo("Test Denon", 1, "AVR", "1.0", "127.0.0.1", "wired", 0, "S1"),
            playState,
            40,
            muted,
            new NowPlayingMedia("station", "", "", "", "", "", "", null, null),
            DateTimeOffset.Now,
            IsPoweredOn: poweredOn,
            IsMainZoneOn: mainZoneOn,
            InputSource: input);

    private static byte[] GenerateNoiseFloat(int sampleRate, int channels, int frames, float amplitude, Random rng)
    {
        _ = sampleRate;
        var bytes = new byte[frames * channels * 4];
        var idx = 0;
        for (var f = 0; f < frames; f++)
        {
            var s = (float)(((rng.NextDouble() * 2.0) - 1.0) * amplitude);
            for (var c = 0; c < channels; c++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(idx, 4), s);
                idx += 4;
            }
        }

        return bytes;
    }

    private static byte[] GenerateSineFloat(int sampleRate, int channels, int frames, double freqHz, float leftAmp, float rightAmp, ref double phase)
    {
        var bytes = new byte[frames * channels * 4];
        var step = 2.0 * Math.PI * freqHz / sampleRate;
        var idx = 0;
        for (var f = 0; f < frames; f++)
        {
            var s = Math.Sin(phase);
            phase += step;
            var l = (float)(s * leftAmp);
            var r = (float)(s * rightAmp);

            BitConverter.TryWriteBytes(bytes.AsSpan(idx, 4), l);
            idx += 4;
            if (channels > 1)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(idx, 4), r);
                idx += 4;
            }
        }

        return bytes;
    }

    private static byte[] GenerateSinePcm16(int sampleRate, int channels, int frames, double freqHz, float leftAmp, float rightAmp, ref double phase)
    {
        var bytes = new byte[frames * channels * 2];
        var step = 2.0 * Math.PI * freqHz / sampleRate;
        var idx = 0;
        for (var f = 0; f < frames; f++)
        {
            var s = Math.Sin(phase);
            phase += step;
            var l = (short)Math.Clamp(s * leftAmp * 32767.0, -32768.0, 32767.0);
            var r = (short)Math.Clamp(s * rightAmp * 32767.0, -32768.0, 32767.0);

            BitConverter.TryWriteBytes(bytes.AsSpan(idx, 2), l);
            idx += 2;
            if (channels > 1)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(idx, 2), r);
                idx += 2;
            }
        }

        return bytes;
    }

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}

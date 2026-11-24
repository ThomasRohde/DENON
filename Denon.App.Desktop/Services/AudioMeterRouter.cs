using Denon.App.Core.Models;
using Denon.App.Core.Services.Metering;
using Denon.App.PluginSdk;

namespace Denon.App.Desktop.Services;

/// <summary>Snapshot of receiver, meter visibility, plugin sync, and PC loopback state used to gate the Room Mic.</summary>
public readonly record struct RoomMicGateInputs(
    bool RoomMicEnabled,
    bool MetersVisible,
    bool HueMusicSyncActive,
    bool IsReceiverConnected,
    bool IsReceiverAwake,
    bool IsReceiverMuted,
    string InputSource,
    bool IsHeosPlaying,
    bool IsPcCasting,
    bool HasAppStreamFrame,
    bool HasPcLoopbackSignal);

/// <summary>
/// Privacy-first gating policy for the opt-in Room Mic: opens the Windows microphone ONLY when the user enabled
/// Room mode, a consumer (visible meters or active Hue Music Sync) needs it, the Denon is connected/awake/unmuted
/// on a source the app cannot tap digitally, and PC loopback is not already providing live audio.
/// </summary>
public static class RoomMicGate
{
    public static bool ShouldCapture(in RoomMicGateInputs inputs)
    {
        if (!inputs.RoomMicEnabled)
        {
            return false;
        }

        if (!inputs.IsReceiverConnected || !inputs.IsReceiverAwake || inputs.IsReceiverMuted)
        {
            return false;
        }

        if (!inputs.MetersVisible && !inputs.HueMusicSyncActive)
        {
            return false;
        }

        if (inputs.HasPcLoopbackSignal)
        {
            return false;
        }

        var isNet = string.Equals(inputs.InputSource?.Trim(), "NET", StringComparison.OrdinalIgnoreCase);
        var hasDenonAudio = !isNet || inputs.IsHeosPlaying || inputs.IsPcCasting;
        if (!hasDenonAudio)
        {
            return false;
        }

        // If the app already taps the digital stream (PC Cast or analyzed YouTube/jukebox), keep the mic closed.
        var isAppTapped = isNet && (inputs.IsPcCasting || (inputs.IsHeosPlaying && inputs.HasAppStreamFrame));
        return !isAppTapped;
    }
}

/// <summary>Inputs for routing synced audio frames to audio-reactive plugins (e.g. Philips Hue Music Sync) and RTA consumers.</summary>
public readonly record struct AudioMeterRouteContext(
    AudioMeterInputs MeterInputs,
    PluginAudioFrame PcCastDelayedFrame,
    bool HasPcCastDelayedFrame,
    PluginAudioFrame AppStreamFrame,
    bool HasAppStreamFrame,
    PluginAudioFrame RoomMicFrame,
    PluginAudioFrame PcLoopbackFrame);

/// <summary>
/// Central routing engine for Hue Music Sync (<see cref="PluginAudioFrame"/>) and 31-band RTA spectrum selection.
/// Ensures PC Cast (both default and non-default output devices) uses HEOS-delayed frames, YouTube/jukebox uses
/// synced <see cref="AppStreamMeterService"/> frames, Room Mic feeds untappable Denon sources when active, and
/// muted receivers send silence instead of unrelated PC audio.
/// </summary>
public sealed class AudioMeterRouter
{
    private readonly AudioSpectrumAnalyzer _appStreamBeatAnalyzer = new();

    public static AudioMeterInputs BuildInputs(
        ReceiverStateSnapshot receiverSnapshot,
        bool isPcCasting,
        bool hasPcSignal,
        bool hasAppStreamFrame,
        bool isRoomMicActive)
    {
        var status = receiverSnapshot.Status;
        var isAwake = receiverSnapshot.IsConnected && status is { IsPoweredOn: true, IsMainZoneOn: true };
        var isHeosPlaying = status is not null &&
                            string.Equals(status.PlayState?.Trim(), "play", StringComparison.OrdinalIgnoreCase);

        return new AudioMeterInputs(
            IsDenonAwake: isAwake,
            InputSource: status?.InputSource ?? string.Empty,
            IsHeosPlaying: isHeosPlaying,
            IsMuted: status?.IsMuted ?? false,
            IsPcCasting: isPcCasting,
            HasPcSignal: hasPcSignal,
            HasAppStreamFrame: hasAppStreamFrame,
            IsRoomMicActive: isRoomMicActive);
    }

    public static RoomMicGateInputs BuildRoomMicGateInputs(
        bool roomMicEnabled,
        bool metersVisible,
        bool hueMusicSyncActive,
        ReceiverStateSnapshot receiverSnapshot,
        bool isPcCasting,
        bool hasAppStreamFrame,
        bool hasPcLoopbackSignal)
    {
        var status = receiverSnapshot.Status;
        var isAwake = status is { IsPoweredOn: true, IsMainZoneOn: true };
        var isHeosPlaying = status is not null &&
                            string.Equals(status.PlayState?.Trim(), "play", StringComparison.OrdinalIgnoreCase);

        return new RoomMicGateInputs(
            RoomMicEnabled: roomMicEnabled,
            MetersVisible: metersVisible,
            HueMusicSyncActive: hueMusicSyncActive,
            IsReceiverConnected: receiverSnapshot.IsConnected,
            IsReceiverAwake: isAwake,
            IsReceiverMuted: status?.IsMuted ?? false,
            InputSource: status?.InputSource ?? string.Empty,
            IsHeosPlaying: isHeosPlaying,
            IsPcCasting: isPcCasting,
            HasAppStreamFrame: hasAppStreamFrame,
            HasPcLoopbackSignal: hasPcLoopbackSignal);
    }

    /// <summary>Converts a synced 31-band <see cref="AppStreamMeterService"/> sample into a beat-detected <see cref="PluginAudioFrame"/>.</summary>
    public PluginAudioFrame BuildAppStreamPluginFrame(
        double left,
        double right,
        ReadOnlySpan<double> bands31,
        long timestampMs) =>
        _appStreamBeatAnalyzer.AnalyzeFromBands31(left, right, bands31, isLive: left > 0.003 || right > 0.003, timestampMs);

    public void ResetAppStreamBeatState() => _appStreamBeatAnalyzer.Reset();

    /// <summary>
    /// Selects the exact <see cref="PluginAudioFrame"/> to publish to audio-reactive plugins (such as Philips Hue Music Sync),
    /// following the same routing & HEOS delay rules as the VU meters and 31-band RTA.
    /// </summary>
    public static (AudioMeterSource Source, PluginAudioFrame Frame) ResolvePluginAudioFrame(
        in AudioMeterRouteContext context,
        long timestampMs)
    {
        var source = AudioMeterSourceRouter.Resolve(context.MeterInputs);
        var silent = new PluginAudioFrame(0, 0, 0, 0, 0, 0, false, false, timestampMs);

        switch (source)
        {
            case AudioMeterSource.Denon when context.HasAppStreamFrame:
                return (source, context.AppStreamFrame with { TimestampMs = timestampMs });

            case AudioMeterSource.Denon when context.MeterInputs.IsPcCasting:
                return context.HasPcCastDelayedFrame
                    ? (source, context.PcCastDelayedFrame with { TimestampMs = timestampMs })
                    : (source, silent);

            case AudioMeterSource.Room:
                return context.RoomMicFrame.IsLiveSignal
                    ? (source, context.RoomMicFrame with { TimestampMs = timestampMs })
                    : (source, silent);

            case AudioMeterSource.Pc:
                return context.PcLoopbackFrame.IsLiveSignal
                    ? (source, context.PcLoopbackFrame with { TimestampMs = timestampMs })
                    : (source, silent);

            default:
                // Muted, Off, or Simulated (untappable Denon source with Room Mic off): never leak unrelated PC audio
                return (source, silent);
        }
    }

    /// <summary>
    /// Resolves the 31-band RTA spectrum into <paramref name="bandsDestination"/> for any meter source,
    /// including delayed PC Cast FFT from both default and non-default output devices (no SIM fallback).
    /// </summary>
    public static AudioMeterSource ResolveSpectrum(
        in AudioMeterInputs inputs,
        Func<double[], bool> tryCopyAppStreamBands,
        Func<double[], bool> tryCopyPcCastDelayedBands,
        Func<double[], bool> tryCopyRoomMicBands,
        Func<double[], bool> tryCopyPcLoopbackBands,
        double[] bandsDestination)
    {
        if (bandsDestination.Length < AudioMeterMath.BandCount)
        {
            return AudioMeterSource.Off;
        }

        var source = AudioMeterSourceRouter.Resolve(inputs);
        switch (source)
        {
            case AudioMeterSource.Denon when inputs.HasAppStreamFrame && tryCopyAppStreamBands(bandsDestination):
                return AudioMeterSource.Denon;

            case AudioMeterSource.Denon when inputs.IsPcCasting:
                if (!tryCopyPcCastDelayedBands(bandsDestination))
                {
                    Array.Clear(bandsDestination, 0, AudioMeterMath.BandCount);
                }

                // Always stays DENON (synced silence during cast pause) — never falls back to SIM, even on non-default devices!
                return AudioMeterSource.Denon;

            case AudioMeterSource.Room:
                if (!tryCopyRoomMicBands(bandsDestination))
                {
                    Array.Clear(bandsDestination, 0, AudioMeterMath.BandCount);
                }

                return AudioMeterSource.Room;

            case AudioMeterSource.Pc:
                if (tryCopyPcLoopbackBands(bandsDestination))
                {
                    return AudioMeterSource.Pc;
                }

                Array.Clear(bandsDestination, 0, AudioMeterMath.BandCount);
                return AudioMeterSource.Off;

            case AudioMeterSource.Simulated:
                return AudioMeterSource.Simulated;

            default:
                Array.Clear(bandsDestination, 0, AudioMeterMath.BandCount);
                return source;
        }
    }
}

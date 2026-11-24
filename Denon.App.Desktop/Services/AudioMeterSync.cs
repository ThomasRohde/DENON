using Denon.App.PluginSdk;

namespace Denon.App.Desktop.Services;

public sealed record AudioCaptureDeviceOption(string DeviceId, string FriendlyName, bool IsDefault);

/// <summary>Where the VU meters and the 31-band RTA get their signal from.</summary>
public enum AudioMeterSource
{
    /// <summary>Nothing plays. Meters rest.</summary>
    Off,

    /// <summary>
    /// Real audio that this app streams to the Denon (PC Cast, YouTube / YouTube Music, local jukebox),
    /// lined up with the speakers by the measured HEOS delay.
    /// </summary>
    Denon,

    /// <summary>The Denon plays a source the app cannot hear (TV/ARC, HDMI, Spotify…). Volume-scaled simulation.</summary>
    Simulated,

    /// <summary>The Denon is on a source, but muted. Meters rest.</summary>
    Muted,

    /// <summary>The Denon is off or idle. Meters show the PC's own audio output (not the Denon).</summary>
    Pc,

    /// <summary>Opt-in PC microphone / Line-In listening to the room while the Denon plays an untappable source (TV/ARC, HDMI, Chromecast…).</summary>
    Room,
}

/// <summary>Snapshot of what the router needs, taken once per frame.</summary>
public readonly record struct AudioMeterInputs(
    bool IsDenonAwake,
    string InputSource,
    bool IsHeosPlaying,
    bool IsMuted,
    bool IsPcCasting,
    bool HasPcSignal,
    bool HasAppStreamFrame = false,
    bool IsRoomMicActive = false);

/// <summary>
/// Picks the meter source from what the <b>Denon</b> plays, not from what the PC plays.
/// A podcast on the laptop must not show as Denon audio while the Denon is on TV.
/// </summary>
public static class AudioMeterSourceRouter
{
    public static AudioMeterSource Resolve(in AudioMeterInputs inputs)
    {
        if (inputs.IsDenonAwake)
        {
            var isNet = string.Equals(inputs.InputSource?.Trim(), "NET", StringComparison.OrdinalIgnoreCase);

            // On NET the HEOS play state is reliable. On TV/HDMI/analog inputs HEOS reports its own
            // (unrelated) player state, so an awake Denon on such an input counts as "has audio".
            var hasAudio = !isNet || inputs.IsHeosPlaying || inputs.IsPcCasting;
            if (hasAudio)
            {
                if (inputs.IsMuted)
                {
                    return AudioMeterSource.Muted;
                }

                // DENON: the app streams this audio (PC Cast), or it streams a YouTube / jukebox track that is
                // analyzed at the current playback position.
                var isAppStream = inputs.IsPcCasting || (inputs.IsHeosPlaying && inputs.HasAppStreamFrame);
                if (isNet && isAppStream)
                {
                    return AudioMeterSource.Denon;
                }

                // Opt-in Room Mic listens to TV/ARC, HDMI, Chromecast, Spotify, etc. when enabled & active;
                // otherwise fall back to volume-scaled simulation.
                return inputs.IsRoomMicActive ? AudioMeterSource.Room : AudioMeterSource.Simulated;
            }
        }

        return inputs.HasPcSignal ? AudioMeterSource.Pc : AudioMeterSource.Off;
    }

    /// <summary>Short badge text for the VU meter.</summary>
    public static string BadgeText(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "DENON",
        AudioMeterSource.Room => "ROOM",
        AudioMeterSource.Simulated => "SIM",
        AudioMeterSource.Muted => "MUTED",
        AudioMeterSource.Pc => "PC",
        _ => "IDLE",
    };

    /// <summary>Longer label for the RTA header.</summary>
    public static string RtaLabel(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "DENON • SYNCED FFT",
        AudioMeterSource.Room => "ROOM MIC FFT",
        AudioMeterSource.Simulated => "SIMULATED (VOL)",
        AudioMeterSource.Muted => "DENON MUTED",
        AudioMeterSource.Pc => "PC AUDIO (NOT DENON)",
        _ => "STANDBY",
    };

    /// <summary>
    /// Scale for the simulation: follows the Denon volume (0–98) and fades to zero below volume 15,
    /// so a turned-down receiver does not look loud.
    /// </summary>
    public static double SimulationScale(int liveVolume, double floor, double span)
    {
        var scale = Math.Clamp(floor + (liveVolume / 98.0) * span, 0.0, 1.0);
        return scale * Math.Clamp(liveVolume / 15.0, 0.0, 1.0);
    }
}

/// <summary>
/// Classic VU scale law for the analog dials. Meter levels are 0..1 over 40 dB (-40 dBFS = 0, 0 dBFS = 1).
/// 0 VU sits at <see cref="ZeroVuDbfs"/>; needle deflection is proportional to amplitude (not dB), with
/// +3 VU at full scale, so -20..-3 spreads over the left half and 0..+3 crowds into the red zone, like a real VU.
/// </summary>
public static class VuMeterScale
{
    /// <summary>Program level that reads 0 VU.</summary>
    public const double ZeroVuDbfs = -7.2;

    /// <summary>Highest deflection: a touch past +3 VU, against the pin.</summary>
    public const double MaxDeflection = 1.04;

    public static double LevelToDbfs(double level) => (Math.Clamp(level, 0.0, 1.0) * 40.0) - 40.0;

    public static double LevelToVu(double level) => LevelToDbfs(level) - ZeroVuDbfs;

    /// <summary>0 at rest, 1 at +3 VU.</summary>
    public static double VuToDeflection(double vu) => Math.Clamp(Math.Pow(10.0, (vu - 3.0) / 20.0), 0.0, MaxDeflection);

    /// <summary>Needle deflection for a meter level; silence rests at 0.</summary>
    public static double LevelToDeflection(double level) => level <= 0.003 ? 0.0 : VuToDeflection(LevelToVu(level));

    /// <summary>"−7.4" style readout in dBFS (true minus sign); "−∞" for silence.</summary>
    public static string FormatDbfs(double level) => level <= 0.003
        ? "−∞"
        : LevelToDbfs(level).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('-', '−');
}

/// <summary>
/// Thread-safe ring of recent meter frames (stereo level + optional 31 RTA bands) with tick timestamps.
/// Lets the UI show the frame from <c>delayMs</c> ago, so meters for audio streamed to the Denon line up
/// with what the speakers play after the HEOS buffer.
/// </summary>
public sealed class AudioFrameHistory
{
    public const int BandCount = 31;

    private readonly object _sync = new();
    private readonly long[] _ticks;
    private readonly double[] _left;
    private readonly double[] _right;
    private readonly bool[] _live;
    private readonly PluginAudioFrame[] _frames;
    private readonly double[] _bands;
    private int _next;
    private int _count;

    public AudioFrameHistory(int capacity = 1024)
    {
        _ticks = new long[capacity];
        _left = new double[capacity];
        _right = new double[capacity];
        _live = new bool[capacity];
        _frames = new PluginAudioFrame[capacity];
        _bands = new double[capacity * BandCount];
    }

    /// <summary>Max age of the found frame versus the target time; older means "no signal then".</summary>
    public long MaxGapMs { get; init; } = 400;

    public void Add(long tickMs, double left, double right, bool isLive, ReadOnlySpan<double> bands = default) =>
        Add(new PluginAudioFrame(left, right, 0, 0, 0, 0, false, isLive, tickMs), bands);

    public void Add(in PluginAudioFrame frame, ReadOnlySpan<double> bands = default)
    {
        lock (_sync)
        {
            var i = _next;
            _ticks[i] = frame.TimestampMs;
            _left[i] = frame.LeftLevel;
            _right[i] = frame.RightLevel;
            _live[i] = frame.IsLiveSignal;
            _frames[i] = frame;
            var dest = _bands.AsSpan(i * BandCount, BandCount);
            if (bands.Length >= BandCount)
            {
                bands[..BandCount].CopyTo(dest);
            }
            else
            {
                dest.Clear();
            }

            _next = (i + 1) % _ticks.Length;
            _count = Math.Min(_count + 1, _ticks.Length);
        }
    }

    /// <summary>
    /// Finds the newest frame at or before <paramref name="targetTickMs"/>. Returns false when there is none,
    /// it is older than <see cref="MaxGapMs"/>, or it was silent. Copies the bands when a buffer is given.
    /// </summary>
    public bool TryGetAt(long targetTickMs, out double left, out double right, double[]? bandsDestination = null)
    {
        if (!TryGetFrameAt(targetTickMs, out var frame, bandsDestination))
        {
            left = 0;
            right = 0;
            return false;
        }

        left = frame.LeftLevel;
        right = frame.RightLevel;
        return true;
    }

    /// <summary>
    /// Finds the newest full <see cref="PluginAudioFrame"/> at or before <paramref name="targetTickMs"/>.
    /// Returns false when there is none, it is older than <see cref="MaxGapMs"/>, or it was silent.
    /// </summary>
    public bool TryGetFrameAt(long targetTickMs, out PluginAudioFrame frame, double[]? bandsDestination = null)
    {
        frame = default;
        lock (_sync)
        {
            for (var n = 1; n <= _count; n++)
            {
                var i = (_next - n + _ticks.Length) % _ticks.Length;
                if (_ticks[i] > targetTickMs)
                {
                    continue;
                }

                if (targetTickMs - _ticks[i] > MaxGapMs || !_live[i])
                {
                    return false;
                }

                frame = _frames[i];
                if (bandsDestination is { Length: >= BandCount })
                {
                    _bands.AsSpan(i * BandCount, BandCount).CopyTo(bandsDestination);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a live frame with a beat lies in (<paramref name="afterTickMs"/>, <paramref name="upToTickMs"/>].
    /// A consumer that samples at its own rate moves its cursor each time, so every beat is delivered once:
    /// none lost between samples, none repeated.
    /// </summary>
    public bool TryGetBeatBetween(long afterTickMs, long upToTickMs, out double intensity)
    {
        intensity = 0;
        var found = false;
        lock (_sync)
        {
            for (var n = 1; n <= _count; n++)
            {
                var i = (_next - n + _ticks.Length) % _ticks.Length;
                if (_ticks[i] > upToTickMs)
                {
                    continue;
                }

                if (_ticks[i] <= afterTickMs)
                {
                    break;
                }

                if (_live[i] && _frames[i].IsBeat)
                {
                    found = true;
                    intensity = Math.Max(intensity, _frames[i].BeatIntensity);
                }
            }
        }

        return found;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _next = 0;
            _count = 0;
        }
    }
}

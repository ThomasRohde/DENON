namespace Denon.App.Desktop.Services;

/// <summary>Where the VU meters and the 31-band RTA get their signal from.</summary>
public enum AudioMeterSource
{
    /// <summary>Nothing plays. Meters rest.</summary>
    Off,

    /// <summary>Real audio that this app streams to the Denon (PC Cast), delayed by the HEOS buffer so it is in sync.</summary>
    Denon,

    /// <summary>The Denon plays a source the app cannot hear (TV/ARC, HDMI, Spotify…). Volume-scaled simulation.</summary>
    Simulated,

    /// <summary>The Denon is on a source, but muted. Meters rest.</summary>
    Muted,

    /// <summary>The Denon is off or idle. Meters show the PC's own audio output (not the Denon).</summary>
    Pc,
}

/// <summary>Snapshot of what the router needs, taken once per frame.</summary>
public readonly record struct AudioMeterInputs(
    bool IsDenonAwake,
    string InputSource,
    bool IsHeosPlaying,
    bool IsMuted,
    bool IsPcCasting,
    bool HasPcSignal);

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

                return isNet && inputs.IsPcCasting ? AudioMeterSource.Denon : AudioMeterSource.Simulated;
            }
        }

        return inputs.HasPcSignal ? AudioMeterSource.Pc : AudioMeterSource.Off;
    }

    /// <summary>Short badge text for the VU meter.</summary>
    public static string BadgeText(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "DENON",
        AudioMeterSource.Simulated => "SIM",
        AudioMeterSource.Muted => "MUTED",
        AudioMeterSource.Pc => "PC",
        _ => "IDLE",
    };

    /// <summary>Longer label for the RTA header.</summary>
    public static string RtaLabel(AudioMeterSource source) => source switch
    {
        AudioMeterSource.Denon => "DENON • SYNCED FFT",
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
    private readonly double[] _bands;
    private int _next;
    private int _count;

    public AudioFrameHistory(int capacity = 1024)
    {
        _ticks = new long[capacity];
        _left = new double[capacity];
        _right = new double[capacity];
        _live = new bool[capacity];
        _bands = new double[capacity * BandCount];
    }

    /// <summary>Max age of the found frame versus the target time; older means "no signal then".</summary>
    public long MaxGapMs { get; init; } = 400;

    public void Add(long tickMs, double left, double right, bool isLive, ReadOnlySpan<double> bands = default)
    {
        lock (_sync)
        {
            var i = _next;
            _ticks[i] = tickMs;
            _left[i] = left;
            _right[i] = right;
            _live[i] = isLive;
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
        left = 0;
        right = 0;
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

                left = _left[i];
                right = _right[i];
                if (bandsDestination is { Length: >= BandCount })
                {
                    _bands.AsSpan(i * BandCount, BandCount).CopyTo(bandsDestination);
                }

                return true;
            }
        }

        return false;
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

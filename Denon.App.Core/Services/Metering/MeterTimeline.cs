namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Meter frames (stereo level + 31 RTA bands) for one track, indexed by media time.
/// One writer (the analyzer) appends; the UI reads the frame at the playback position.
/// A rolling timeline (a live radio stream, which never ends) keeps only the last <see cref="MaxFrames"/>
/// frames: the oldest chunk goes when a new one is needed, and times before it are no longer analyzed.
/// </summary>
public sealed class MeterTimeline
{
    public const int BandCount = AudioMeterMath.BandCount;
    private const int Stride = 2 + BandCount;
    private const int ChunkFrames = 4096;

    private readonly object _sync = new();
    private readonly List<float[]> _chunks = [];
    private int _count;
    private int _first; // the absolute index of the first kept frame (a multiple of ChunkFrames)
    private volatile bool _isComplete;

    public MeterTimeline(string key, double hopSeconds, int maxFrames, bool rolling = false)
    {
        if (hopSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hopSeconds));
        }

        Key = key;
        HopSeconds = hopSeconds;
        Rolling = rolling;
        // A rolling timeline drops whole chunks, so it keeps at least two.
        MaxFrames = rolling
            ? Math.Max(2, (Math.Max(1, maxFrames) + ChunkFrames - 1) / ChunkFrames) * ChunkFrames
            : Math.Max(1, maxFrames);
    }

    /// <summary>Keeps only the last <see cref="MaxFrames"/> frames instead of stopping there.</summary>
    public bool Rolling { get; }

    /// <summary>Which track this is (for example <c>yt:&lt;videoId&gt;</c> or <c>local:&lt;path&gt;</c>).</summary>
    public string Key { get; }

    public double HopSeconds { get; }

    public int MaxFrames { get; }

    public int FrameCount
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public double AnalyzedSeconds => FrameCount * HopSeconds;

    /// <summary>The decoder reached the end of the track (or the frame cap).</summary>
    public bool IsComplete => _isComplete;

    /// <summary>The decoder stopped with an error. Frames before that point stay valid.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>True when no more frames fit (<see cref="MaxFrames"/>).</summary>
    public bool IsFull
    {
        get
        {
            lock (_sync)
            {
                return !Rolling && _count >= MaxFrames;
            }
        }
    }

    public bool Append(double left, double right, ReadOnlySpan<double> bands)
    {
        lock (_sync)
        {
            if (_count - _first >= MaxFrames)
            {
                if (!Rolling)
                {
                    return false;
                }

                _chunks.RemoveAt(0);
                _first += ChunkFrames;
            }

            var chunkIndex = (_count - _first) / ChunkFrames;
            if (chunkIndex >= _chunks.Count)
            {
                _chunks.Add(new float[ChunkFrames * Stride]);
            }

            var offset = (_count % ChunkFrames) * Stride;
            var chunk = _chunks[chunkIndex];
            chunk[offset] = (float)left;
            chunk[offset + 1] = (float)right;
            for (var b = 0; b < BandCount; b++)
            {
                chunk[offset + 2 + b] = b < bands.Length ? (float)bands[b] : 0f;
            }

            _count++;
            return true;
        }
    }

    public void MarkComplete(bool failed = false)
    {
        IsFailed = failed;
        _isComplete = true;
    }

    /// <summary>
    /// The frame at <paramref name="positionSeconds"/>. Returns false when that time is not analyzed yet
    /// (the caller falls back to the simulation). After the end of a complete track it returns silence,
    /// except when the analysis stopped early (failed or capped): then it returns false.
    /// </summary>
    public bool TryGetAt(double positionSeconds, out double left, out double right, double[]? bandsDestination = null)
    {
        left = 0;
        right = 0;
        if (double.IsNaN(positionSeconds) || positionSeconds < 0)
        {
            return false;
        }

        var index = (long)Math.Floor(positionSeconds / HopSeconds);
        lock (_sync)
        {
            if (index < _first)
            {
                return false; // rolled out of a rolling timeline
            }

            if (index >= _count)
            {
                if (_isComplete && !IsFailed && !Rolling && _count < MaxFrames && _count > 0)
                {
                    // The track ended: synced silence until the next track starts.
                    if (bandsDestination is { Length: >= BandCount })
                    {
                        Array.Clear(bandsDestination, 0, BandCount);
                    }

                    return true;
                }

                return false;
            }

            var chunk = _chunks[(int)((index - _first) / ChunkFrames)];
            var offset = (int)(index % ChunkFrames) * Stride;
            left = chunk[offset];
            right = chunk[offset + 1];
            if (bandsDestination is { Length: >= BandCount })
            {
                for (var b = 0; b < BandCount; b++)
                {
                    bandsDestination[b] = chunk[offset + 2 + b];
                }
            }

            return true;
        }
    }
}

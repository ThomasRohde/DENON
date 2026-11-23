namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Media-time clock for a relay stream: runs while the Denon plays, stops while it is paused.
/// The position is what the speakers play now: elapsed time minus the HEOS buffer delay.
/// </summary>
public sealed class PlaybackClock
{
    private readonly object _sync = new();
    private DateTimeOffset? _runningSinceUtc;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private bool _paused;

    /// <summary>Starts from zero at <paramref name="startedAtUtc"/>.</summary>
    public void Restart(DateTimeOffset startedAtUtc)
    {
        lock (_sync)
        {
            _accumulated = TimeSpan.Zero;
            _runningSinceUtc = startedAtUtc;
            _paused = false;
        }
    }

    /// <summary>Stops and clears the clock (no track).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _accumulated = TimeSpan.Zero;
            _runningSinceUtc = null;
            _paused = false;
        }
    }

    public void Pause(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            if (_paused)
            {
                return;
            }

            if (_runningSinceUtc is { } started)
            {
                _accumulated += nowUtc - started;
            }

            _runningSinceUtc = null;
            _paused = true;
        }
    }

    public void Resume(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            if (!_paused)
            {
                return;
            }

            _runningSinceUtc = nowUtc;
            _paused = false;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _paused;
            }
        }
    }

    /// <summary>True after <see cref="Restart"/> and before <see cref="Reset"/>.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_sync)
            {
                return _paused || _runningSinceUtc is not null;
            }
        }
    }

    /// <summary>Media position the speakers play now, in seconds (never negative).</summary>
    public double GetPositionSeconds(DateTimeOffset nowUtc, int bufferDelayMs, TimeSpan? duration = null)
    {
        lock (_sync)
        {
            var elapsed = _accumulated;
            if (!_paused && _runningSinceUtc is { } started)
            {
                elapsed += nowUtc - started;
            }

            var seconds = Math.Max(0.0, elapsed.TotalSeconds - (Math.Clamp(bufferDelayMs, 0, 8000) / 1000.0));
            if (duration is { TotalSeconds: > 1 } d)
            {
                seconds = Math.Min(seconds, Math.Max(0, d.TotalSeconds - 1));
            }

            return seconds;
        }
    }
}

namespace Denon.App.Core.Services.Metering;

/// <summary>
/// Where a live radio stream is at the Denon speakers, for its meter timeline. HEOS and the app's own decoder each
/// open the stream; both get the server's buffered burst first, so media time 0 is about the same audio on both.
/// The speakers play media time 0 when HEOS reports "play" for the new stream. Until then the position is unknown;
/// when no "play" comes (a missed event), the clock starts <see cref="AssumedStartDelay"/> after the command.
/// </summary>
public sealed class RadioPlaybackClock
{
    /// <summary>A "play" this soon after the command is the state of the stream before it.</summary>
    public static readonly TimeSpan StaleGuard = TimeSpan.FromMilliseconds(300);

    /// <summary>How long to wait for "play" before <see cref="AssumedStartDelay"/> is used.</summary>
    public static readonly TimeSpan PlayWait = TimeSpan.FromSeconds(6);

    /// <summary>About how long HEOS buffers an internet radio stream before the speakers play it.</summary>
    public static readonly TimeSpan AssumedStartDelay = TimeSpan.FromSeconds(2.5);

    private const long NotStarted = long.MinValue;
    private readonly long _commandMs;
    private long _playStartMs; // set on the receiver-state thread, read by the meters: always through Interlocked

    private RadioPlaybackClock(long commandMs, long playStartMs)
    {
        _commandMs = commandMs;
        _playStartMs = playStartMs;
    }

    /// <summary>The app just told HEOS to play the stream.</summary>
    public static RadioPlaybackClock Commanded(long nowMs) => new(nowMs, NotStarted);

    /// <summary>HEOS plays the stream now (it resumed after a pause, which reopens the live stream).</summary>
    public static RadioPlaybackClock PlayingSince(long nowMs) => new(nowMs, nowMs);

    public bool HasStarted => Interlocked.Read(ref _playStartMs) != NotStarted;

    /// <summary>The receiver's play state changed (or was read again).</summary>
    public void OnPlayState(string? playState, long nowMs)
    {
        if (string.Equals(playState, "play", StringComparison.OrdinalIgnoreCase) &&
            nowMs - _commandMs >= StaleGuard.TotalMilliseconds)
        {
            Interlocked.CompareExchange(ref _playStartMs, nowMs, NotStarted);
        }
    }

    /// <summary>The media time the speakers play now, in seconds; null while that is not known yet.</summary>
    public double? PositionSeconds(long nowMs)
    {
        var start = Interlocked.Read(ref _playStartMs);
        if (start == NotStarted)
        {
            if (nowMs - _commandMs < PlayWait.TotalMilliseconds)
            {
                return null;
            }

            start = _commandMs + (long)AssumedStartDelay.TotalMilliseconds;
        }

        return Math.Max(0, nowMs - start) / 1000.0;
    }
}

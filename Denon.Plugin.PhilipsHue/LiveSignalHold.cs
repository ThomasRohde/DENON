namespace Denon.Plugin.PhilipsHue;

/// <summary>
/// Keeps the Hue panel's "live" badge on through short gaps (pauses in speech, a quiet bar), so it does not blink to
/// WAITING FOR AUDIO many times a minute. Only the badge waits: the lights follow every frame as before.
/// </summary>
public sealed class LiveSignalHold
{
    /// <summary>How long the badge stays live after the last live frame.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(2);

    private long _lastLiveMs = long.MinValue;

    /// <summary>True when this frame is live, or the last live frame was less than <see cref="HoldTime"/> ago.</summary>
    public bool Update(bool isLiveSignal, long nowMs)
    {
        if (isLiveSignal)
        {
            _lastLiveMs = nowMs;
            return true;
        }

        return _lastLiveMs != long.MinValue && nowMs - _lastLiveMs < HoldTime.TotalMilliseconds;
    }
}

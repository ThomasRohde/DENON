namespace Denon.App.Core.Services;

/// <summary>
/// Keeps the app's volume target while the receiver catches up. After the app sends a volume (a ±1/±5 step, a quick
/// level, the slider), status updates can still carry the old value for a moment. Without this guard such an update
/// put the old value back into the target, and the next step counted from it: −5 then +5 from 41 ended at 46, not 41.
/// The guard holds the sent value until the receiver reports it, or for <see cref="HoldTime"/> at most (a command that
/// failed or a remote that changed the volume meanwhile then wins).
/// </summary>
public sealed class VolumeCommandGuard(TimeProvider? clock = null)
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(4);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int? _sent;
    private DateTimeOffset _holdUntil;

    /// <summary>The app sent (or is about to send) this volume to the receiver.</summary>
    public void Sent(int level)
    {
        _sent = level;
        _holdUntil = _clock.GetUtcNow() + HoldTime;
    }

    /// <summary>
    /// True when a status update with <paramref name="reportedLevel"/> must not replace the target yet: a sent value is
    /// pending, the receiver does not report it yet, and the hold time has not passed. Clears the pending value once
    /// the receiver reports it or the hold time is over.
    /// </summary>
    public bool ShouldHold(int reportedLevel)
    {
        if (_sent is not { } sent)
        {
            return false;
        }

        if (reportedLevel == sent || _clock.GetUtcNow() >= _holdUntil)
        {
            _sent = null;
            return false;
        }

        return true;
    }
}

namespace Denon.App.Desktop;

/// <summary>A button of the Cinema window's full-screen HUD that controls the receiver (#79).</summary>
public enum CinemaHudCommand
{
    PlayPause,
    VolumeDown,
    VolumeUp,
    Mute,
}

/// <summary>The receiver state the HUD shows: the volume as the receiver's 0–98 number, mute and play state.</summary>
/// <param name="Volume">The master volume, 0–98.</param>
/// <param name="IsMuted">The receiver is muted.</param>
/// <param name="IsPlaying">Something plays (the button then pauses).</param>
/// <param name="Note">A short note next to the volume, for example that the safe volume stopped Vol +.</param>
public sealed record CinemaHudState(int Volume, bool IsMuted, bool IsPlaying, string Note = "");

/// <summary>The volume rule of the HUD's Vol − and Vol + buttons. No WPF: the tests read it.</summary>
public static class CinemaHudVolume
{
    /// <summary>One press moves the volume this many of the receiver's 0–98 steps (as Ctrl+Up / Ctrl+Down).</summary>
    public const int Step = 2;

    /// <summary>
    /// The volume after one press. Vol − goes down to 0. Vol + stops at the safe volume (the same limit as Volume
    /// Memory and the automations) and at the receiver's own maximum; above the safe volume it does not go up.
    /// </summary>
    /// <param name="current">The volume now, 0–98.</param>
    /// <param name="delta">The change: negative for Vol −, positive for Vol +.</param>
    /// <param name="maxVolume">The receiver's maximum volume.</param>
    /// <param name="safeVolume">The safe volume from the settings.</param>
    /// <returns>The new volume, and true when the safe volume (or the maximum) stopped a Vol + press.</returns>
    public static (int Target, bool Capped) Next(int current, int delta, int maxVolume, int safeVolume)
    {
        var max = Math.Clamp(maxVolume, 0, 98);
        current = Math.Clamp(current, 0, max);
        if (delta <= 0)
        {
            return (Math.Max(0, current + delta), false);
        }

        var ceiling = Math.Min(max, Math.Clamp(safeVolume, 15, 98));
        if (current >= ceiling)
        {
            return (current, true);
        }

        var target = Math.Min(current + delta, ceiling);
        return (target, current + delta > ceiling);
    }

    /// <summary>The HUD note when Vol + stopped: the safe volume, or the receiver's maximum when that is lower.</summary>
    public static string CapNote(int maxVolume, int safeVolume)
    {
        var safe = Math.Clamp(safeVolume, 15, 98);
        var max = Math.Clamp(maxVolume, 0, 98);
        return max < safe ? $"Max {max}" : $"Safe volume {safe}";
    }
}

using Denon.App.Core.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>The volume target survives stale receiver updates (test finding F-01: −5 then +5 ended 5 dB louder).</summary>
public sealed class VolumeCommandGuardTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_stale_update_does_not_replace_the_sent_volume()
    {
        var clock = new TestClock();
        var guard = new VolumeCommandGuard(clock);

        // At 41 the user clicks −5: the app sends 36. The receiver still reports 41 for a moment.
        guard.Sent(36);
        Assert.True(guard.ShouldHold(41));

        // The receiver reports 36: the update applies again, and the guard is clear.
        Assert.False(guard.ShouldHold(36));
        Assert.False(guard.ShouldHold(41));
    }

    [Fact]
    public void The_hold_ends_after_the_hold_time()
    {
        var clock = new TestClock();
        var guard = new VolumeCommandGuard(clock);

        guard.Sent(36);
        clock.Now += VolumeCommandGuard.HoldTime - TimeSpan.FromMilliseconds(1);
        Assert.True(guard.ShouldHold(41));

        // The command failed, or a remote set another volume: after the hold the receiver's value wins.
        clock.Now += TimeSpan.FromMilliseconds(1);
        Assert.False(guard.ShouldHold(41));
        Assert.False(guard.ShouldHold(41));
    }

    [Fact]
    public void Without_a_sent_volume_updates_always_apply()
    {
        Assert.False(new VolumeCommandGuard(new TestClock()).ShouldHold(41));
    }

    [Fact]
    public void A_newer_send_replaces_the_older_one()
    {
        var guard = new VolumeCommandGuard(new TestClock());
        guard.Sent(36);
        guard.Sent(41); // −5 then +5 before the receiver answered.

        Assert.True(guard.ShouldHold(36)); // The receiver's answer to the first step is stale now.
        Assert.False(guard.ShouldHold(41));
    }
}

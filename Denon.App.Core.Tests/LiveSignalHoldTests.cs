using Denon.Plugin.PhilipsHue;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class LiveSignalHoldTests
{
    [Fact]
    public void The_live_badge_holds_through_a_short_pause_and_drops_after_the_hold()
    {
        var hold = new LiveSignalHold();
        Assert.False(hold.Update(false, 0)); // nothing live yet

        Assert.True(hold.Update(true, 1_000));
        Assert.True(hold.Update(false, 1_900)); // a pause in speech
        Assert.True(hold.Update(false, 2_999));
        Assert.False(hold.Update(false, 3_000)); // two seconds without a live frame

        Assert.True(hold.Update(true, 3_100)); // live again at once
    }
}

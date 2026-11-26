using Denon.App.Desktop.ViewModels;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>A save that shortcut collisions refuse says which shortcuts and why (Stations page Save Changes, 2.10.5).</summary>
public sealed class ShortcutCollisionReportTests
{
    [Fact]
    public void Names_each_blocking_shortcut_with_its_gesture_and_reason()
    {
        var mute = new ShortcutItemEditor("global.mute", "Mute", isGlobal: true, "Ctrl+Alt+M");
        mute.SetValidationResult(true, false, "Collision: already assigned to \"Refresh\".", "CONFLICT");
        var refresh = new ShortcutItemEditor("app.refresh", "Refresh", isGlobal: true, "Ctrl+Alt+M");
        refresh.SetValidationResult(true, false, "Collision: already assigned to \"Mute\".", "CONFLICT");

        var text = ShortcutCollisionReport.Describe([mute, refresh]);

        Assert.StartsWith("Settings not saved:", text);
        Assert.Contains("\"Mute\" (Ctrl+Alt+M): Collision: already assigned to \"Refresh\".", text);
        Assert.Contains("\"Refresh\" (Ctrl+Alt+M): Collision: already assigned to \"Mute\".", text);
    }

    [Fact]
    public void Leaves_out_shortcuts_that_are_fine_or_only_overlap()
    {
        var ok = new ShortcutItemEditor("global.play", "Play / Pause", isGlobal: true, "Ctrl+Alt+P");
        ok.SetValidationResult(false, false, null, "OK");
        var overlap = new ShortcutItemEditor("app.next", "Next", isGlobal: false, "Ctrl+Alt+N");
        overlap.SetValidationResult(false, true, "Overlaps with \"Next track\" in the other scope.", "OVERLAP");
        var taken = new ShortcutItemEditor("global.volup", "Volume up", isGlobal: true, "Ctrl+Alt+Up");
        taken.SetValidationResult(true, false, null, "OS TAKEN");

        var text = ShortcutCollisionReport.Describe([ok, overlap, taken]);

        Assert.DoesNotContain("Play / Pause", text);
        Assert.DoesNotContain("Next", text);
        Assert.Contains("\"Volume up\" (Ctrl+Alt+Up): OS TAKEN", text);
    }

    [Fact]
    public void Without_a_blocking_shortcut_it_does_not_claim_a_collision()
    {
        var ok = new ShortcutItemEditor("global.play", "Play / Pause", isGlobal: true, "Ctrl+Alt+P");
        ok.SetValidationResult(false, false, null, "OK");

        var text = ShortcutCollisionReport.Describe([ok]);

        Assert.StartsWith("Settings not saved:", text);
        Assert.DoesNotContain("collision", text, StringComparison.OrdinalIgnoreCase);
    }
}

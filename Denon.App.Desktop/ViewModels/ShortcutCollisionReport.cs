namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Says why Save &amp; Sync refused to save: the shortcuts whose last validation found a blocking collision. The same
/// text goes to the log, the status line and the Stations page, so a refused save is never silent.
/// </summary>
public static class ShortcutCollisionReport
{
    public static string Describe(IEnumerable<ShortcutItemEditor> editors)
    {
        var blocking = editors
            .Where(static editor => editor.HasCollision)
            .Select(static editor =>
            {
                var gesture = string.IsNullOrWhiteSpace(editor.Gesture) ? string.Empty : $" ({editor.Gesture})";
                var reason = string.IsNullOrWhiteSpace(editor.CollisionMessage) ? editor.StatusLabel : editor.CollisionMessage;
                return $"\"{editor.Label}\"{gesture}: {reason}";
            })
            .ToList();

        return blocking.Count == 0
            ? "Settings not saved: the shortcut check in Settings did not pass. Check the shortcuts there."
            : $"Settings not saved: resolve the shortcut collision(s) in Settings first. {string.Join(" ", blocking)}";
    }
}

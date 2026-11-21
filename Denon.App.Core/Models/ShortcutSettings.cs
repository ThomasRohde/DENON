namespace Denon.App.Core.Models;

public sealed class ShortcutBinding
{
    public ShortcutBinding()
    {
    }

    public ShortcutBinding(string actionId, string label, bool isGlobal, string gesture)
    {
        ActionId = actionId;
        Label = label;
        IsGlobal = isGlobal;
        Gesture = gesture;
    }

    public string ActionId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool IsGlobal { get; set; }

    public string Gesture { get; set; } = string.Empty;

    public ShortcutBinding Clone() => new(ActionId, Label, IsGlobal, Gesture);
}

public static class ShortcutActionIds
{
    public const string GlobalShowApp = "global.show_app";
    public const string GlobalPlayPause = "global.play_pause";
    public const string GlobalMute = "global.mute";
    public const string GlobalVolumeUp = "global.volume_up";
    public const string GlobalVolumeDown = "global.volume_down";
    public const string GlobalNextTrack = "global.next_track";
    public const string GlobalPreviousTrack = "global.prev_track";

    public const string AppPlayPause = "app.play_pause";
    public const string AppMute = "app.mute";
    public const string AppVolumeUp = "app.volume_up";
    public const string AppVolumeDown = "app.volume_down";
    public const string AppRefresh = "app.refresh";
}

public sealed class ShortcutSettings
{
    public List<ShortcutBinding> Bindings { get; set; } = CreateDefaults();

    public ShortcutSettings Clone() =>
        new()
        {
            Bindings = Bindings.Select(static b => b.Clone()).ToList(),
        };

    public string GetGesture(string actionId) =>
        Bindings.FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.Gesture ?? string.Empty;

    public static List<ShortcutBinding> CreateDefaults() =>
    [
        // Global Windows Hotkeys (work anywhere, even when minimized to tray)
        new(ShortcutActionIds.GlobalShowApp, "Open / Toggle Main Window", true, "Ctrl+Alt+D"),
        new(ShortcutActionIds.GlobalPlayPause, "Global Play / Pause", true, "Ctrl+Alt+P"),
        new(ShortcutActionIds.GlobalMute, "Global Mute Toggle", true, "Ctrl+Alt+M"),
        new(ShortcutActionIds.GlobalVolumeUp, "Global Volume Up (+2)", true, "Ctrl+Alt+Up"),
        new(ShortcutActionIds.GlobalVolumeDown, "Global Volume Down (-2)", true, "Ctrl+Alt+Down"),
        new(ShortcutActionIds.GlobalNextTrack, "Global Next Track", true, "Ctrl+Alt+Right"),
        new(ShortcutActionIds.GlobalPreviousTrack, "Global Previous Track", true, "Ctrl+Alt+Left"),

        // In-App Shortcuts (active when Denon Desktop window is focused)
        new(ShortcutActionIds.AppPlayPause, "Play / Pause Playback", false, "Space"),
        new(ShortcutActionIds.AppMute, "Toggle Receiver Mute", false, "M"),
        new(ShortcutActionIds.AppVolumeUp, "Volume Up (+2)", false, "Ctrl+Up"),
        new(ShortcutActionIds.AppVolumeDown, "Volume Down (-2)", false, "Ctrl+Down"),
        new(ShortcutActionIds.AppRefresh, "Refresh Receiver Telemetry", false, "F5"),
    ];
}

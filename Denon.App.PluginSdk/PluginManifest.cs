namespace Denon.App.PluginSdk;

[Flags]
public enum PluginCapabilities
{
    None = 0,
    DashboardUi = 1 << 0,
    ReceiverEvents = 1 << 1,
    SceneAutomation = 1 << 2,
    Actions = 1 << 3,
    LocalNetwork = 1 << 4,
    AudioReactive = 1 << 5,
    AlbumArtSync = 1 << 6,

    /// <summary>The plugin may send commands to the receiver through <see cref="IPluginContext.ReceiverControl"/>.</summary>
    ReceiverControl = 1 << 7,
}

public enum PluginHealthState
{
    Disabled = 0,
    Idle = 1,
    Connected = 2,
    Warning = 3,
    Error = 4,
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Category,
    string AccentHex,
    string IconGlyph,
    PluginCapabilities Capabilities);

public sealed record PluginActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    string Category = "General");

public sealed record PluginActionResult(
    bool Success,
    string Message,
    string? DataJson = null,
    string? ImageBase64 = null,
    string? ImageMimeType = null)
{
    public static PluginActionResult Ok(string message) => new(true, message);

    public static PluginActionResult OkWithData(
        string message,
        string? dataJson = null,
        string? imageBase64 = null,
        string? imageMimeType = null) =>
        new(true, message, dataJson, imageBase64, imageMimeType);

    public static PluginActionResult Fail(string message) => new(false, message);
}

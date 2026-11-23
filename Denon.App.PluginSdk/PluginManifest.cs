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
    string Category = "General")
{
    // The host uses this metadata for the MCP tool schema and safety hints.

    /// <summary>
    /// Parameters the action reads from its parameter dictionary. Shown as the MCP input schema.
    /// Empty (the default): the action takes no parameters.
    /// </summary>
    public IReadOnlyList<PluginActionParameter> Parameters { get; init; } = [];

    /// <summary>
    /// True: the action only reads state (allowed in the MCP "Observer" tier). Null: the host guesses from the id.
    /// </summary>
    public bool? IsReadOnly { get; init; }

    /// <summary>
    /// True: the action can stop, close or switch something off (MCP clients should confirm first; the host
    /// requires the "FullOrchestration" tier). Null: the host guesses from the id.
    /// </summary>
    public bool? IsDestructive { get; init; }
}

/// <summary>One action parameter. Values arrive as strings in the action's parameter dictionary.</summary>
/// <param name="Name">Parameter key, for example <c>device</c>.</param>
/// <param name="Type">JSON schema type for MCP clients: <c>string</c>, <c>integer</c>, <c>number</c> or <c>boolean</c>.</param>
/// <param name="Description">What the value means; shown to MCP clients.</param>
/// <param name="Required">True when the action fails without it.</param>
public sealed record PluginActionParameter(
    string Name,
    string Type,
    string Description,
    bool Required = false);

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

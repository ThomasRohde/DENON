using Denon.App.Core.Models;

namespace Denon.App.Core.Mcp;

/// <summary>
/// The per-tool and per-plugin switches on top of the permission tier (Brain page → Tool access). A denied tool is
/// not listed, and a call to it gets a clear refusal.
/// </summary>
public static class McpToolAccess
{
    /// <summary>The named tools that belong to one plugin: off with that plugin, like its plugin_* tools.</summary>
    private static readonly Dictionary<string, string> NamedPluginTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hue_set_color"] = "philips-hue",
        ["tv_computer"] = "smart-tv",
    };

    /// <summary>The plugin a named tool belongs to, or null (a room tool, or a plugin_* tool).</summary>
    public static string? NamedToolPlugin(string toolName) =>
        NamedPluginTools.TryGetValue(toolName, out var pluginId) ? pluginId : null;

    public static bool IsPluginDenied(McpServerSettings cfg, string? pluginId) =>
        !string.IsNullOrWhiteSpace(pluginId) &&
        cfg.DeniedPluginIds.Contains(pluginId.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the tool is turned off: by its name, or through its plugin (<paramref name="pluginId"/> for a
    /// plugin_* tool; a named plugin tool finds its plugin itself).
    /// </summary>
    public static bool IsDenied(McpServerSettings cfg, string toolName, string? pluginId = null) =>
        cfg.DeniedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase) ||
        IsPluginDenied(cfg, pluginId ?? NamedToolPlugin(toolName));

    /// <summary>The refusal a client gets for a denied tool.</summary>
    public static string RefusalMessage(string toolName) =>
        $"The tool '{toolName}' is turned off in Denon Desktop (Brain page, Tool access). Ask the user to turn it on.";

    /// <summary>The refusal for a room tool that would use a plugin whose tools are turned off.</summary>
    public static string PluginRefusalMessage(string toolName, string pluginId) =>
        $"'{toolName}' would use the plugin '{pluginId}', whose tools are turned off in Denon Desktop (Brain page, Tool access).";
}

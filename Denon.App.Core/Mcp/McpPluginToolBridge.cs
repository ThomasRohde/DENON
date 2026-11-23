using System.Text.Json;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Mcp;

/// <summary>
/// Turns plugin actions into MCP tools (<c>plugin_&lt;id&gt;_&lt;action&gt;</c>): input schema, safety hints and
/// tool results. Plugins on SDK 1.16+ describe their parameters and hints; older plugins keep the legacy
/// generic schema and id-based guesses.
/// </summary>
public static class McpPluginToolBridge
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static readonly string[] AllowedTypes = ["string", "integer", "number", "boolean"];

    /// <summary>Hints for an action: the declared values, else a guess from the action id.</summary>
    public static (bool ReadOnly, bool Destructive, bool Idempotent) ResolveHints(PluginActionDescriptor action)
    {
        var id = action.ActionId;
        var readOnly = action.IsReadOnly ?? id.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase);
        var destructive = !readOnly && (action.IsDestructive ??
            (id.Contains("power_off", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("all_off", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("close_app", StringComparison.OrdinalIgnoreCase)));
        var idempotent = readOnly ||
                         (!id.Contains("toggle", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("toast", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("webhook", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("volume-up", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("volume-down", StringComparison.OrdinalIgnoreCase));
        return (readOnly, destructive, idempotent);
    }

    /// <summary>Input schema: the declared parameters, or the legacy generic fields for older plugins.</summary>
    public static object BuildInputSchema(PluginActionDescriptor action)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        if (action.Parameters is { } parameters) // Declared (SDK 1.16+), also when empty.
        {
            foreach (var p in parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            {
                var type = AllowedTypes.Contains(p.Type, StringComparer.OrdinalIgnoreCase) ? p.Type.ToLowerInvariant() : "string";
                props[p.Name] = new { type, description = p.Description };
                if (p.Required)
                {
                    required.Add(p.Name);
                }
            }
        }
        else
        {
            props["message"] = new { type = "string", description = "Optional text/toast message parameter (if supported by this action)" };
            props["contentId"] = new { type = "string", description = "Optional YouTube video URL/ID (for tv.youtube)" };
            props["entityId"] = new { type = "string", description = "Optional Home Assistant entity ID (for ha.trigger_entity)" };
            props["button"] = new { type = "string", description = "Optional LG remote button name (e.g. HOME, BACK, ENTER)" };
        }

        return required.Count > 0
            ? new { type = "object", properties = props, required }
            : new { type = "object", properties = props };
    }

    /// <summary>
    /// MCP tool result for a plugin action: an image block when the plugin returns one, the JSON data
    /// (pretty text + <c>structuredContent</c> for objects) or else the message, and <c>isError</c> on failure.
    /// </summary>
    public static object BuildResult(PluginActionResult result)
    {
        if (!result.Success)
        {
            return new
            {
                content = new object[] { new { type = "text", text = result.Message } },
                isError = true,
            };
        }

        JsonElement structured = default;
        var hasStructured = false;
        var text = result.Message;

        if (!string.IsNullOrWhiteSpace(result.DataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.DataJson);
                structured = doc.RootElement.Clone();
                hasStructured = structured.ValueKind == JsonValueKind.Object;
                var json = JsonSerializer.Serialize(structured, PrettyJson);
                text = string.IsNullOrWhiteSpace(result.Message) ? json : $"{result.Message}\n\n{json}";
            }
            catch (JsonException)
            {
                text = string.IsNullOrWhiteSpace(result.Message) ? result.DataJson : $"{result.Message}\n\n{result.DataJson}";
            }
        }

        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(result.ImageBase64))
        {
            content.Add(new
            {
                type = "image",
                data = result.ImageBase64,
                mimeType = string.IsNullOrWhiteSpace(result.ImageMimeType) ? "image/jpeg" : result.ImageMimeType,
            });
        }

        content.Add(new { type = "text", text });

        return hasStructured
            ? new { content, structuredContent = structured, isError = false }
            : new { content, isError = false };
    }
}

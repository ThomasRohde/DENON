using System.Text.Json;
using ModelContextProtocol.Protocol;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Mcp;

/// <summary>
/// Turns plugin actions into MCP tools (<c>plugin_&lt;id&gt;_&lt;action&gt;</c>): input schema, safety hints and
/// tool results from the parameters and hints each action declares. Hints a plugin leaves unset are guessed
/// from the action id.
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

    /// <summary>Input schema from the declared parameters.</summary>
    public static object BuildInputSchema(PluginActionDescriptor action)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in action.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            var type = AllowedTypes.Contains(p.Type, StringComparer.OrdinalIgnoreCase) ? p.Type.ToLowerInvariant() : "string";
            props[p.Name] = new { type, description = p.Description };
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

        return required.Count > 0
            ? new { type = "object", properties = props, required }
            : new { type = "object", properties = props };
    }

    /// <summary>
    /// MCP tool result for a plugin action: an image block when the plugin returns one, the JSON data
    /// (pretty text + <c>structuredContent</c> for objects) or else the message, and <c>isError</c> on failure.
    /// </summary>
    public static CallToolResult BuildResult(PluginActionResult result)
    {
        if (!result.Success)
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = result.Message }], IsError = true };
        }

        JsonElement? structured = null;
        var text = result.Message;

        if (!string.IsNullOrWhiteSpace(result.DataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.DataJson);
                var data = doc.RootElement.Clone();
                structured = data.ValueKind == JsonValueKind.Object ? data : null; // an object for every client version
                var json = JsonSerializer.Serialize(data, PrettyJson);
                text = string.IsNullOrWhiteSpace(result.Message) ? json : $"{result.Message}\n\n{json}";
            }
            catch (JsonException)
            {
                text = string.IsNullOrWhiteSpace(result.Message) ? result.DataJson : $"{result.Message}\n\n{result.DataJson}";
            }
        }

        var content = new List<ContentBlock>();
        if (!string.IsNullOrWhiteSpace(result.ImageBase64))
        {
            content.Add(new ImageContentBlock
            {
                Data = System.Text.Encoding.UTF8.GetBytes(result.ImageBase64),
                MimeType = string.IsNullOrWhiteSpace(result.ImageMimeType) ? "image/jpeg" : result.ImageMimeType,
            });
        }

        content.Add(new TextContentBlock { Text = text });
        return new CallToolResult { Content = content, StructuredContent = structured, IsError = false };
    }
}

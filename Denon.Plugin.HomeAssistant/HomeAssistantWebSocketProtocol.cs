using System.Text.Json;

namespace Denon.Plugin.HomeAssistant;

/// <summary>One message from the Home Assistant WebSocket API, as <see cref="HomeAssistantWebSocketProtocol.Parse"/> reads it.</summary>
internal abstract record HaWsMessage
{
    internal sealed record AuthRequired : HaWsMessage;

    internal sealed record AuthOk : HaWsMessage;

    internal sealed record AuthInvalid(string Message) : HaWsMessage;

    internal sealed record Result(int Id, bool Success, string? Error) : HaWsMessage;

    /// <summary>A state_changed event. <see cref="NewState"/> is null when Home Assistant removed the entity.</summary>
    internal sealed record StateChanged(int Id, string EntityId, HaEntityItem? NewState) : HaWsMessage;

    internal sealed record Pong(int Id) : HaWsMessage;

    /// <summary>Any other message, or text that is not a JSON object. The client ignores it.</summary>
    internal sealed record Unknown(string? Type) : HaWsMessage;
}

/// <summary>
/// Pure functions for the Home Assistant WebSocket API (<c>/api/websocket</c>): build the messages the client sends and
/// read the messages the server sends. The socket loop is in <see cref="HomeAssistantLiveSocket"/>.
/// </summary>
internal static class HomeAssistantWebSocketProtocol
{
    /// <summary>
    /// The WebSocket address for a REST base URL: http becomes ws, https becomes wss. The host, the port and a path
    /// prefix (a reverse proxy at https://example.org/ha) stay. Null when the URL is not an absolute http(s) URL.
    /// </summary>
    public static Uri? TryBuildWebSocketUri(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            _ => null,
        };
        if (scheme is null)
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath.TrimEnd('/') + "/api/websocket",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static string BuildAuth(string accessToken) =>
        JsonSerializer.Serialize(new { type = "auth", access_token = accessToken.Trim() });

    public static string BuildSubscribeStateChanged(int id) =>
        JsonSerializer.Serialize(new { id, type = "subscribe_events", event_type = "state_changed" });

    public static string BuildPing(int id) =>
        JsonSerializer.Serialize(new { id, type = "ping" });

    /// <summary>Reads one server message. It never throws: bad JSON gives <see cref="HaWsMessage.Unknown"/>.</summary>
    public static HaWsMessage Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HaWsMessage.Unknown(null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // An array is a coalesced batch. The client does not ask for it (no supported_features), so it is ignored.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new HaWsMessage.Unknown(null);
            }

            var type = GetString(root, "type");
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var n) ? n : 0;
            switch (type)
            {
                case "auth_required":
                    return new HaWsMessage.AuthRequired();
                case "auth_ok":
                    return new HaWsMessage.AuthOk();
                case "auth_invalid":
                    return new HaWsMessage.AuthInvalid(GetString(root, "message") ?? string.Empty);
                case "pong":
                    return new HaWsMessage.Pong(id);
                case "result":
                    var success = root.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                    string? error = null;
                    if (!success && root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                    {
                        error = GetString(errEl, "message") ?? GetString(errEl, "code");
                    }

                    return new HaWsMessage.Result(id, success, error);
                case "event":
                    return (HaWsMessage?)ParseEvent(root, id) ?? new HaWsMessage.Unknown(type);
                default:
                    return new HaWsMessage.Unknown(type);
            }
        }
        catch (JsonException)
        {
            return new HaWsMessage.Unknown(null);
        }
    }

    /// <summary>
    /// Reads one state object, the same shape as an item of REST <c>/api/states</c> and as <c>new_state</c> in a
    /// state_changed event. Null when it has no "domain.object_id" entity id. It does not check the domain.
    /// </summary>
    public static HaEntityItem? ParseState(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(item, "entity_id") ?? string.Empty;
        var dot = id.IndexOf('.');
        if (dot <= 0 || dot == id.Length - 1)
        {
            return null;
        }

        var state = GetString(item, "state") ?? string.Empty;
        var friendly = item.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object
            ? GetString(attrEl, "friendly_name") ?? id
            : id;
        return new HaEntityItem(id, friendly, id[..dot].ToLowerInvariant(), state);
    }

    private static HaWsMessage.StateChanged? ParseEvent(JsonElement root, int id)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object ||
            GetString(ev, "event_type") != "state_changed" ||
            !ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entityId = GetString(data, "entity_id");
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        HaEntityItem? newState = null;
        if (data.TryGetProperty("new_state", out var ns) && ns.ValueKind == JsonValueKind.Object)
        {
            newState = ParseState(ns);
            if (newState is null || !newState.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return new HaWsMessage.StateChanged(id, entityId, newState);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

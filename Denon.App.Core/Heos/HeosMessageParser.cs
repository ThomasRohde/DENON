using System.Text.Json;

namespace Denon.App.Core.Heos;

internal sealed record HeosResponse(
    string Result,
    string Command,
    IReadOnlyDictionary<string, string> MessageFields,
    JsonElement? Payload);

internal static class HeosMessageParser
{
    public static HeosResponse Parse(string rawResponse, string command)
    {
        foreach (var line in rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("heos", out var heosElement))
            {
                continue;
            }

            var result = heosElement.TryGetProperty("result", out var resultElement)
                ? resultElement.GetString() ?? "fail"
                : "fail";

            var resolvedCommand = heosElement.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString() ?? command
                : command;

            var message = heosElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            JsonElement? payload = document.RootElement.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : null;

            return new HeosResponse(result, resolvedCommand, ParseMessageFields(message), payload);
        }

        throw new InvalidOperationException($"No JSON HEOS response received for command '{command}'.");
    }

    private static IReadOnlyDictionary<string, string> ParseMessageFields(string message)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(message))
        {
            return fields;
        }

        foreach (var pair in message.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                fields[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            fields[key] = value;
        }

        return fields;
    }
}

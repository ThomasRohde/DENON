using System.Text.Json;

namespace Denon.App.Core.Heos;

public sealed record HeosResponse(
    string Result,
    string Command,
    string RawMessage,
    IReadOnlyDictionary<string, string> MessageFields,
    JsonElement? Payload)
{
    public bool IsSuccess => string.Equals(Result, "success", StringComparison.OrdinalIgnoreCase);

    public bool IsCommandUnderProcess =>
        RawMessage.Contains("command under process", StringComparison.OrdinalIgnoreCase) ||
        MessageFields.ContainsKey("command under process");
}

public static class HeosMessageParser
{
    public static HeosResponse Parse(string rawResponse, string command)
    {
        HeosResponse? underProcessFallback = null;
        var expectedCommand = NormalizeCommandPath(command);

        foreach (var line in rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseLine(line, command, out var parsed) || parsed is null)
            {
                continue;
            }

            if (IsEvent(parsed.Command))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(expectedCommand) &&
                !string.Equals(NormalizeCommandPath(parsed.Command), expectedCommand, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsed.IsCommandUnderProcess)
            {
                underProcessFallback = parsed;
                continue;
            }

            return parsed;
        }

        if (underProcessFallback is not null)
        {
            return underProcessFallback;
        }

        throw new InvalidOperationException($"No JSON HEOS response received for command '{command}'.");
    }

    public static bool TryParseLine(string line, string fallbackCommand, out HeosResponse? response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("heos", out var heosElement))
            {
                return false;
            }

            var result = heosElement.TryGetProperty("result", out var resultElement)
                ? resultElement.GetString() ?? "fail"
                : "fail";

            var resolvedCommand = heosElement.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString() ?? fallbackCommand
                : fallbackCommand;

            var message = heosElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            JsonElement? payload = document.RootElement.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : null;

            response = new HeosResponse(result, resolvedCommand, message, ParseMessageFields(message), payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsEvent(string command) =>
        command.StartsWith("event/", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeCommandPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith("heos://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["heos://".Length..];
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        return trimmed.Trim('/');
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
                fields[SafeUnescape(pair)] = string.Empty;
                continue;
            }

            var key = SafeUnescape(pair[..separatorIndex]);
            var value = SafeUnescape(pair[(separatorIndex + 1)..]);
            fields[key] = value;
        }

        return fields;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

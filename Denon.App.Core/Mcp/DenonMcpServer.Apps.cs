#pragma warning disable MCPEXP003 // MCP Apps (ModelContextProtocol.Extensions.Apps) is experimental in the C# SDK.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace Denon.App.Core.Mcp;

/// <summary>
/// MCP Apps (extension io.modelcontextprotocol/ui): the now-playing card. The tool <c>show_now_playing</c> points at
/// the UI resource <c>ui://denon/now-playing.html</c>; a host that supports MCP Apps shows the card in the chat and
/// sends it the tool's result. The card's buttons call the ordinary tools (avr_set_volume, avr_set_mute,
/// avr_playback_control) through the host, so the permission tier and the safe-volume ceiling apply as always.
/// Hosts without MCP Apps get the same result as text and structured content.
/// </summary>
public sealed partial class DenonMcpServer
{
    internal const string NowPlayingToolName = "show_now_playing";
    internal const string NowPlayingUiUri = "ui://denon/now-playing.html";

    private static readonly Lazy<string> NowPlayingHtml = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Denon.App.Core.Mcp.Apps.now-playing.html")
            ?? throw new InvalidOperationException("The now-playing card is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The tool's definition with <c>_meta.ui</c> pointing at the card.</summary>
    private static Tool NowPlayingTool()
    {
        var tool = Tool(
            NowPlayingToolName,
            "Show what plays in the living room as an interactive card: the source, title, input, sound mode, volume (with the safe ceiling) and mute, " +
            "with buttons for volume, mute and play/pause. Hosts without MCP Apps get the same data as text. Read-only.",
            EmptySchema(),
            title: "Now Playing",
            readOnly: true,
            idempotent: true);
        tool.Meta = new JsonObject
        {
            ["ui"] = JsonSerializer.SerializeToNode(
                new McpUiToolMeta { ResourceUri = NowPlayingUiUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App] },
                McpApps.SerializerOptions),
        };
        return tool;
    }

    private static Resource NowPlayingResource() => new()
    {
        Uri = NowPlayingUiUri,
        Name = "Now Playing card",
        Description = "MCP App: the living room's now-playing card (shown by show_now_playing).",
        MimeType = McpApps.HtmlMimeType,
        Meta = new JsonObject
        {
            ["ui"] = JsonSerializer.SerializeToNode(new McpUiResourceMeta { PrefersBorder = true }, McpApps.SerializerOptions),
        },
    };

    private static ReadResourceResult ReadNowPlayingResource() => new()
    {
        Contents = [new TextResourceContents { Uri = NowPlayingUiUri, MimeType = McpApps.HtmlMimeType, Text = NowPlayingHtml.Value }],
    };

    /// <summary>What the card shows (and what a host without MCP Apps gets as structured content).</summary>
    internal object BuildNowPlayingPayload()
    {
        var snapshot = _stateStore.Snapshot;
        var status = snapshot.IsConnected ? snapshot.Status : null;
        var cfg = _settingsStore.Current.Mcp;
        var safeVolume = Math.Clamp(cfg.MaxSafeVolume, 15, 98);
        if (status is null)
        {
            return new
            {
                connected = false,
                on = false,
                receiver = _settingsStore.Current.Receiver.Host,
                title = "Receiver not connected",
                detail = snapshot.LastError ?? "Denon Desktop has no connection to the receiver right now.",
                safeVolume,
                tier = cfg.PermissionTier.ToString(),
            };
        }

        var resolved = NowPlayingResolver.Resolve(status, null, _pluginHost.GetPlugins());
        var on = status.IsPoweredOn && status.IsMainZoneOn;
        var volume = status.VolumeExact > 0.01 ? status.VolumeExact : status.Volume;
        var isHeos = DenonCatalog.InputMatches(status.InputSource, "NET");
        return new
        {
            connected = true,
            on,
            receiver = string.IsNullOrWhiteSpace(status.Player.Model) ? "Denon AVR" : status.Player.Model,
            title = resolved.Title,
            detail = resolved.Detail,
            playState = resolved.PlayStateLabel,
            input = status.InputSource,
            inputName = DenonCatalog.GetFriendlyInputName(status.InputSource),
            soundMode = status.SoundMode,
            volume = Math.Round(volume, 1),
            volumeDb = DenonVolumeMath.FormatDecibels(volume),
            maxVolume = status.MaxVolume,
            safeVolume,
            muted = status.IsMuted,
            // Play/pause goes to HEOS, so the card offers it only on the HEOS input.
            canPlayPause = on && isHeos,
            playing = string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase),
            tier = cfg.PermissionTier.ToString(),
        };
    }

    private CallToolResult ShowNowPlaying()
    {
        var payload = BuildNowPlayingPayload();
        var structured = JsonSerializer.SerializeToElement(payload, JsonPrettyOptions);
        var text = structured.TryGetProperty("on", out var on) && on.GetBoolean()
            ? $"{Str("title")} — {Str("detail")}. {Str("inputName")}, {Str("soundMode")}, volume {Num("volume")} ({Str("volumeDb")}){(structured.TryGetProperty("muted", out var m) && m.GetBoolean() ? ", muted" : string.Empty)}."
            : $"{Str("title")}. {Str("detail")}";
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = structured,
            IsError = false,
        };

        string Str(string name) => structured.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        string Num(string name) => structured.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "";
    }
}

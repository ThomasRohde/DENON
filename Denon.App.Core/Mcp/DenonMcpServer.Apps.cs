#pragma warning disable MCPEXP003 // MCP Apps (ModelContextProtocol.Extensions.Apps) is experimental in the C# SDK.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Denon.App.Core.Insights;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

namespace Denon.App.Core.Mcp;

/// <summary>
/// MCP Apps (extension io.modelcontextprotocol/ui): interactive cards in the chat. Each card is a tool whose
/// <c>_meta.ui.resourceUri</c> points at a <c>ui://denon/…</c> HTML resource; a host that supports MCP Apps shows the
/// card and sends it the tool's result. The cards' buttons call the ordinary tools through the host, so the
/// permission tier and the safe-volume ceiling apply as always. Hosts without MCP Apps get the same data as text and
/// structured content. The cards share one script (the view protocol) and one stylesheet, put into each page when it
/// is read.
/// </summary>
public sealed partial class DenonMcpServer
{
    internal const string NowPlayingToolName = "show_now_playing";
    internal const string NowPlayingUiUri = "ui://denon/now-playing.html";
    internal const string RoomRemoteToolName = "show_room_remote";
    internal const string AutomationPreviewToolName = "preview_automation";
    internal const string InsightsToolName = "show_insights";
    internal const string RadioSearchToolName = "show_radio_search";
    internal const string VolumeMemoryToolName = "show_volume_memory";

    /// <summary>A card: its tool, its <c>ui://</c> resource and the embedded page.</summary>
    internal sealed record AppCard(string ToolName, string Uri, string File, string Name, string Description);

    internal static readonly AppCard[] AppCards =
    [
        new(NowPlayingToolName, NowPlayingUiUri, "now-playing.html", "Now Playing card",
            "MCP App: the living room's now-playing card (shown by show_now_playing)."),
        new(RoomRemoteToolName, "ui://denon/room-remote.html", "room-remote.html", "Room remote",
            "MCP App: buttons for power, scenes, saved stations, inputs, sound modes and the sleep timer (shown by show_room_remote)."),
        new(AutomationPreviewToolName, "ui://denon/automation.html", "automation.html", "Automation preview",
            "MCP App: an automation as steps before it is saved, or the saved automations with on/off (shown by preview_automation)."),
        new(InsightsToolName, "ui://denon/insights.html", "insights.html", "Insights",
            "MCP App: listening time, the week's rhythm, top sources and volume habits as charts (shown by show_insights)."),
        new(RadioSearchToolName, "ui://denon/radio-search.html", "radio-search.html", "Radio search",
            "MCP App: internet radio search results with Play and Save buttons (shown by show_radio_search)."),
        new(VolumeMemoryToolName, "ui://denon/volume-memory.html", "volume-memory.html", "Volume memory",
            "MCP App: the learned volume of each source, to adjust or forget (shown by show_volume_memory)."),
    ];

    private static readonly ConcurrentDictionary<string, string> CardPages = new(StringComparer.Ordinal);

    private static string ReadEmbedded(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Denon.App.Core.Mcp.Apps.{file}")
            ?? throw new InvalidOperationException($"The MCP App file '{file}' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The card's page with the shared stylesheet and script put in.</summary>
    internal static string CardPage(AppCard card) =>
        CardPages.GetOrAdd(card.File, file => ReadEmbedded(file)
            .Replace("/*@shared-css*/", ReadEmbedded("_shared.css"), StringComparison.Ordinal)
            .Replace("/*@shared-js*/", ReadEmbedded("_shared.js"), StringComparison.Ordinal));

    private static Tool WithCard(Tool tool, string uri)
    {
        tool.Meta = new JsonObject
        {
            ["ui"] = JsonSerializer.SerializeToNode(
                new McpUiToolMeta { ResourceUri = uri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App] },
                McpApps.SerializerOptions),
        };
        return tool;
    }

    private static string UriOf(string toolName) => AppCards.First(c => c.ToolName == toolName).Uri;

    /// <summary>The card tools, each with <c>_meta.ui</c> pointing at its page.</summary>
    private static IEnumerable<Tool> AppCardTools()
    {
        yield return WithCard(Tool(
            NowPlayingToolName,
            "Show what plays in the living room as an interactive card: the source, title, input, sound mode, volume (with the safe ceiling) and mute, " +
            "with buttons for volume, mute and play/pause. Hosts without MCP Apps get the same data as text. Read-only.",
            EmptySchema(), title: "Now Playing", readOnly: true, idempotent: true), NowPlayingUiUri);

        yield return WithCard(Tool(
            RoomRemoteToolName,
            "Show a room remote as an interactive card: power, the Smart Scenes, the saved radio stations, the inputs, the sound modes and the sleep timer, " +
            "each a button. The buttons call avr_set_power, activate_smart_scene, media_play_station, avr_set_input, avr_set_sound_mode and avr_set_sleep_timer. " +
            "Use it when the user wants to pick something to play or switch the room by hand. Read-only.",
            EmptySchema(), title: "Room Remote", readOnly: true, idempotent: true), UriOf(RoomRemoteToolName));

        yield return WithCard(Tool(
            AutomationPreviewToolName,
            "Show an automation as readable steps (trigger → conditions → actions) in an interactive card, without saving it. " +
            "Pass 'automation' (the same object as create_or_update_automation) to preview a new or changed one: the card lists any problems and has " +
            "Save buttons, so the user checks it before it is saved. Pass 'automation_id' to show a saved one with an on/off button. " +
            "Pass nothing to list the saved automations. Use it before create_or_update_automation. Read-only.",
            ObjSchema(
                ("automation", "object", "An automation graph to preview: { id?, name, enabled?, cooldownSeconds?, nodes, edges }.", false),
                ("automation_id", "string", "The id or name of a saved automation to show.", false)),
            title: "Preview Automation", readOnly: true, idempotent: true), UriOf(AutomationPreviewToolName));

        yield return WithCard(Tool(
            InsightsToolName,
            "Show the room's Insights as charts in an interactive card: listening and watching time per day, the week's rhythm by hour, " +
            "the top apps and sources, the receiver's inputs, the listening volume by time of day and source, and automation runs. Read-only.",
            ObjSchema(("days", "integer", "The period in days: 1 to 90 (default 7).", false)),
            title: "Insights", readOnly: true, idempotent: true), UriOf(InsightsToolName));

        yield return WithCard(Tool(
            RadioSearchToolName,
            "Search internet radio (Radio Browser) and show the stations in an interactive card with a Play button (media_play_station_url) and a " +
            "Save button (save_station) on each, plus a search box. Read-only until the user presses a button.",
            ObjSchema(
                ("query", "string", "Station name or keyword", false),
                ("tag", "string", "Genre/style tag (e.g. jazz, ambient, classical)", false),
                ("hi_res_only", "boolean", "Only lossless FLAC / high-bitrate stations", false)),
            title: "Radio Search", readOnly: true, idempotent: true, openWorld: true), UriOf(RadioSearchToolName));

        yield return WithCard(Tool(
            VolumeMemoryToolName,
            "Show Volume memory in an interactive card: the volume Denon Desktop learned for each source (apps, TV channels, inputs), " +
            "with buttons to adjust a level (volume_memory_set_level) or forget it (volume_memory_forget). Read-only.",
            EmptySchema(), title: "Volume Memory", readOnly: true, idempotent: true), UriOf(VolumeMemoryToolName));
    }

    /// <summary>The tools the cards' buttons need that no other tool covers.</summary>
    private static IEnumerable<Tool> AppHelperTools()
    {
        yield return Tool(
            "media_play_station",
            "Play one of the saved radio stations (the station buttons in the app) by its alias or name.",
            ObjSchema(("station", "string", "The station's alias (e.g. 'dr-p8') or name (e.g. 'DR P8 Jazz')", true)),
            title: "Play Saved Station");
        yield return Tool(
            "save_station",
            "Save an internet radio stream as a station button in the app (Stations). A URL that is saved already is not added twice. Requires FullOrchestration.",
            ObjSchema(
                ("name", "string", "The station's name", true),
                ("url", "string", "The stream URL", true)),
            title: "Save Radio Station", idempotent: true);
        yield return Tool(
            "volume_memory_set_level",
            "Set the volume Volume memory uses for a source (a key from show_volume_memory). Learning goes on from it. Never above the safe ceiling. Requires FullOrchestration.",
            ObjSchema(
                ("key", "string", "The source's key from show_volume_memory", true),
                ("level", "integer", "The volume, 0 to the safe ceiling", true)),
            title: "Set Learned Volume", idempotent: true);
        yield return Tool(
            "volume_memory_forget",
            "Forget the learned volume of a source (a key from show_volume_memory); it is learned again from the next listening. Requires FullOrchestration.",
            ObjSchema(("key", "string", "The source's key from show_volume_memory", true)),
            title: "Forget Learned Volume", destructive: true, idempotent: true);
    }

    private static IEnumerable<Resource> AppCardResources() => AppCards.Select(card => new Resource
    {
        Uri = card.Uri,
        Name = card.Name,
        Description = card.Description,
        MimeType = McpApps.HtmlMimeType,
        Meta = new JsonObject
        {
            ["ui"] = JsonSerializer.SerializeToNode(new McpUiResourceMeta { PrefersBorder = true }, McpApps.SerializerOptions),
        },
    });

    private static ReadResourceResult? ReadAppCardResource(string uri)
    {
        var card = AppCards.FirstOrDefault(c => string.Equals(c.Uri, uri, StringComparison.OrdinalIgnoreCase));
        return card is null
            ? null
            : new ReadResourceResult
            {
                Contents = [new TextResourceContents { Uri = card.Uri, MimeType = McpApps.HtmlMimeType, Text = CardPage(card) }],
            };
    }

    /// <summary>The read-only card tools; null when <paramref name="toolName"/> is not one.</summary>
    private async Task<CallToolResult?> TryHandleAppCardAsync(string toolName, JsonElement args, CancellationToken ct) => toolName switch
    {
        NowPlayingToolName => ShowNowPlaying(),
        RoomRemoteToolName => CardResult(BuildRoomRemotePayload(), RoomRemoteText),
        AutomationPreviewToolName => CardResult(BuildAutomationPayload(args), AutomationText),
        InsightsToolName => CardResult(BuildInsightsPayload(GetInt(args, "days") ?? 7), InsightsText),
        RadioSearchToolName => CardResult(await BuildRadioSearchPayloadAsync(args, ct), RadioText),
        VolumeMemoryToolName => CardResult(BuildVolumeMemoryPayload(), VolumeMemoryText),
        _ => null,
    };

    /// <summary>The card helper tools that change something; null when <paramref name="toolName"/> is not one.</summary>
    private async Task<CallToolResult?> TryHandleAppHelperAsync(string toolName, JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        switch (toolName)
        {
            case "media_play_station":
            {
                var target = RequireString(args, "station").Trim();
                var preset = _settingsStore.Current.Receiver.Stations.FirstOrDefault(s =>
                        s.Alias.Equals(target, StringComparison.OrdinalIgnoreCase) || s.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpInvalidParamsException($"No saved station '{target}'.");
                await _receiverController.PlayPresetAsync(preset, useLowVolume: false, unmute: true, ct);
                return BuildToolText($"Playing {preset.DisplayName}.");
            }

            case "save_station":
            {
                EnsureFullOrchestration(cfg, toolName);
                var name = RequireString(args, "name").Trim();
                var url = RequireString(args, "url").Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    return BuildToolError("The stream URL must be an http or https address.");
                }

                StationPreset? existing = null;
                StationPreset? added = null;
                await _settingsStore.UpdateAsync(s =>
                {
                    existing = s.Receiver.Stations.FirstOrDefault(st => string.Equals(st.Url.Trim(), url, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        added = new StationPreset(UniqueStationAlias(s.Receiver.Stations, name), name, url);
                        s.Receiver.Stations.Add(added);
                    }
                }, ct);
                return existing is not null
                    ? BuildToolText($"'{existing.DisplayName}' is saved already.")
                    : BuildToolText($"Saved '{added!.DisplayName}' as a station ({added.Alias}).");
            }

            case "volume_memory_set_level":
            {
                EnsureFullOrchestration(cfg, toolName);
                var key = RequireString(args, "key");
                var learned = _volumeMemory.Find(key) ?? throw new McpInvalidParamsException($"No learned volume for '{key}'.");
                var safe = Math.Clamp(cfg.MaxSafeVolume, 15, 98);
                var level = Math.Clamp(GetInt(args, "level") ?? throw new McpInvalidParamsException("Missing integer 'level'."), 0, safe);
                _volumeMemory.Set(key, level, DateTimeOffset.UtcNow);
                _volumeMemory.SaveIfChanged();
                return BuildToolText($"{learned.Label}: learned volume {level}.");
            }

            case "volume_memory_forget":
            {
                EnsureFullOrchestration(cfg, toolName);
                var key = RequireString(args, "key");
                var learned = _volumeMemory.Find(key) ?? throw new McpInvalidParamsException($"No learned volume for '{key}'.");
                _volumeMemory.Forget(key);
                _volumeMemory.SaveIfChanged();
                return BuildToolText($"Forgot the learned volume of {learned.Label}.");
            }
        }

        return null;
    }

    private static string UniqueStationAlias(IReadOnlyCollection<StationPreset> stations, string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0)
        {
            slug = "station";
        }

        var alias = slug;
        for (var n = 2; stations.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)); n++)
        {
            alias = $"{slug}-{n}";
        }

        return alias;
    }

    private static CallToolResult CardResult(object payload, Func<JsonElement, string> text)
    {
        var structured = JsonSerializer.SerializeToElement(payload, JsonPrettyOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text(structured) }],
            StructuredContent = structured,
            IsError = false,
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;

    private static int Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array ? el.GetArrayLength() : 0;

    // ── Now playing ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active station's name, the same way the room-state payload finds it.</summary>
    private string? ActiveStationName(ReceiverStatus status) =>
        string.IsNullOrWhiteSpace(status.ActiveStationAlias)
            ? null
            : _settingsStore.Current.Receiver.Stations
                .FirstOrDefault(s => string.Equals(s.Alias, status.ActiveStationAlias, StringComparison.OrdinalIgnoreCase))?.DisplayName;

    private ResolvedNowPlaying Resolve(ReceiverStatus status) => NowPlayingResolver.Resolve(
        status,
        activeStationName: ActiveStationName(status),
        plugins: _pluginHost.GetPlugins(),
        activeYouTubeTrack: _queueService.IsYouTubePlaybackActive ? _queueService.Snapshot.CurrentTrack : null);

    /// <summary>What the now-playing card shows (and what a host without MCP Apps gets as structured content).</summary>
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

        var resolved = Resolve(status);
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

    private CallToolResult ShowNowPlaying() => CardResult(BuildNowPlayingPayload(), s =>
        s.TryGetProperty("on", out var on) && on.GetBoolean()
            ? $"{Str(s, "title")} — {Str(s, "detail")}. {Str(s, "inputName")}, {Str(s, "soundMode")}, volume {Num(s, "volume")} ({Str(s, "volumeDb")})" +
              $"{(s.TryGetProperty("muted", out var m) && m.GetBoolean() ? ", muted" : string.Empty)}."
            : $"{Str(s, "title")}. {Str(s, "detail")}");

    private static string Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble().ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;

    // ── Room remote ───────────────────────────────────────────────────────────────────────────────────────────────

    internal object BuildRoomRemotePayload()
    {
        var snapshot = _stateStore.Snapshot;
        var status = snapshot.IsConnected ? snapshot.Status : null;
        var settings = _settingsStore.Current;
        var on = status is { IsPoweredOn: true, IsMainZoneOn: true };
        var resolved = status is null ? null : Resolve(status);
        return new
        {
            connected = status is not null,
            on,
            receiver = status is null || string.IsNullOrWhiteSpace(status.Player.Model) ? "Denon AVR" : status.Player.Model,
            title = status is null ? "Receiver not connected" : resolved!.Title,
            detail = status is null ? snapshot.LastError ?? "Denon Desktop has no connection to the receiver right now." : resolved!.Detail,
            input = status?.InputSource,
            soundMode = status?.SoundMode,
            activeStation = on ? status?.ActiveStationAlias : null,
            sleepMinutes = status?.SleepTimerMinutes ?? 0,
            scenes = settings.Receiver.Scenes.Select(s => new { id = s.Id, name = s.Name, badge = s.Badge, accent = s.AccentHex, description = s.Description }),
            stations = settings.Receiver.Stations.Select(s => new { alias = s.Alias, name = s.DisplayName }),
            inputs = DenonCatalog.InputSources.Select(i => new { code = i.Code, name = i.DisplayName }),
            soundModes = DenonCatalog.SoundModes.Select(m => new { code = m.Code, name = m.DisplayName }),
            sleepOptions = new[] { 0, 30, 60, 90, 120 },
            tier = settings.Mcp.PermissionTier.ToString(),
        };
    }

    private static string RoomRemoteText(JsonElement s) =>
        $"Room remote: {Str(s, "title")}. {Count(s, "scenes")} scenes, {Count(s, "stations")} stations, {Count(s, "inputs")} inputs.";

    // ── Automation preview ────────────────────────────────────────────────────────────────────────────────────────

    internal object BuildAutomationPayload(JsonElement args)
    {
        var settings = _settingsStore.Current;
        var canSave = settings.Mcp.PermissionTier >= McpPermissionTier.FullOrchestration;

        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("automation", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            AutomationGraph? graph;
            try
            {
                graph = element.Deserialize<AutomationGraph>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException ex)
            {
                return new { mode = "error", message = $"The automation is not valid JSON for a graph: {ex.Message}", canSave };
            }

            if (graph is null)
            {
                return new { mode = "error", message = "The automation is empty.", canSave };
            }

            if (string.IsNullOrWhiteSpace(graph.Id) || !element.TryGetProperty("id", out _))
            {
                graph.Id = AutomationGraph.NewId("mcp");
            }

            graph.Enabled = element.TryGetProperty("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;
            LayOutIfUnplaced(graph);
            var existing = settings.AutomationGraphs.FirstOrDefault(g => g.Id == graph.Id);
            return new
            {
                mode = "preview",
                automation = graph,
                steps = DescribeSteps(graph),
                problems = AutomationGraphValidator.Validate(graph).Select(p => new { nodeId = p.NodeId, message = p.Message }),
                replaces = existing is null ? null : new { name = existing.Name, enabled = existing.Enabled },
                canSave,
            };
        }

        var wanted = GetString(args, "automation_id").Trim();
        if (wanted.Length > 0)
        {
            var graph = settings.AutomationGraphs.FirstOrDefault(g =>
                g.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase) || g.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (graph is null)
            {
                return new { mode = "error", message = $"No saved automation '{wanted}'.", canSave };
            }

            return new
            {
                mode = "saved",
                automation = graph,
                steps = DescribeSteps(graph),
                problems = AutomationGraphValidator.Validate(graph).Select(p => new { nodeId = p.NodeId, message = p.Message }),
                canSave,
            };
        }

        return new
        {
            mode = "list",
            automations = settings.AutomationGraphs.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                enabled = g.Enabled,
                summary = string.Join(" → ", DescribeSteps(g).Where(s => s.Depth <= 2).Select(s => s.Text).Take(3)),
            }),
            canSave,
        };
    }

    internal sealed record AutomationStep(string Id, string Group, string Title, string Text, int Depth, string? Port);

    /// <summary>The graph as a readable outline: from each trigger, every node once, with the branch it is on.</summary>
    internal List<AutomationStep> DescribeSteps(AutomationGraph graph)
    {
        var steps = new List<AutomationStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Walk(AutomationNode node, int depth, string? port)
        {
            if (!seen.Add(node.Id))
            {
                return;
            }

            var info = AutomationNodeTypes.Find(node.Type);
            steps.Add(new AutomationStep(node.Id, info?.Group ?? "unknown", info?.Title ?? node.Type, DescribeNode(node, info), depth, port));
            foreach (var edge in graph.Edges.Where(e => e.From == node.Id).OrderBy(e => e.Port == AutomationNodeTypes.PortNo ? 1 : 0))
            {
                if (graph.FindNode(edge.To) is { } next)
                {
                    Walk(next, depth + 1, edge.Port == AutomationNodeTypes.PortOut ? null : edge.Port);
                }
            }
        }

        foreach (var trigger in graph.Nodes.Where(n => AutomationNodeTypes.IsTrigger(n.Type)))
        {
            Walk(trigger, 0, null);
        }

        // Nodes no trigger reaches still show, so a preview hides nothing.
        foreach (var node in graph.Nodes.Where(n => !seen.Contains(n.Id)))
        {
            Walk(node, 0, "unreached");
        }

        return steps;
    }

    private string DescribeNode(AutomationNode node, AutomationNodeTypeInfo? info)
    {
        if (info is null)
        {
            return $"Unknown step '{node.Type}'";
        }

        var parts = new List<string>();
        foreach (var field in info.Fields)
        {
            string value;
            if (field.Kind == "pluginAction")
            {
                value = DescribePluginAction(node.Get("pluginId"), node.Get("actionId"));
            }
            else
            {
                value = node.Get(field.Key);
                if (value.Length == 0)
                {
                    continue;
                }

                value = field.Kind switch
                {
                    "scene" => _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s => s.Id == value)?.Name ?? $"{value} (not found)",
                    "input" => DenonCatalog.GetFriendlyInputName(value),
                    _ => field.Options?.FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label ?? value,
                };
            }

            if (value.Length > 0 && !(field.Options is not null && value == "Any"))
            {
                parts.Add($"{field.Label}: {value}");
            }
        }

        foreach (var (key, value) in node.Config.Where(kv => kv.Key.StartsWith(AutomationNodeTypes.ParameterPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value)))
        {
            parts.Add($"{key[AutomationNodeTypes.ParameterPrefix.Length..]}: {value}");
        }

        return parts.Count == 0 ? info.Description : string.Join(" · ", parts);
    }

    private string DescribePluginAction(string pluginId, string actionId)
    {
        if (pluginId.Length == 0 && actionId.Length == 0)
        {
            return string.Empty;
        }

        var plugin = _pluginHost.GetPlugins().FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        var action = plugin?.Actions.FirstOrDefault(a => a.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        return plugin is null ? $"{pluginId} · {actionId}" : $"{plugin.Name}: {action?.DisplayName ?? actionId}";
    }

    private static string AutomationText(JsonElement s) => Str(s, "mode") switch
    {
        "list" => $"{Count(s, "automations")} saved automations.",
        "error" => Str(s, "message"),
        _ => $"Automation '{(s.TryGetProperty("automation", out var a) ? Str(a, "name") : string.Empty)}': " +
             string.Join(" → ", s.GetProperty("steps").EnumerateArray().Select(st => $"{Str(st, "title")} ({Str(st, "text")})")) +
             (Count(s, "problems") > 0 ? $". {Count(s, "problems")} problem(s): " + string.Join("; ", s.GetProperty("problems").EnumerateArray().Select(p => Str(p, "message"))) : "."),
    };

    // ── Insights ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal object BuildInsightsPayload(int days)
    {
        days = Math.Clamp(days, 1, 90);
        var now = DateTimeOffset.Now;
        var since = WatchTimeReport.ReadFrom(now, 2 * days);
        var events = _telemetry.ListAvailableStreams().SelectMany(s => _telemetry.ReadEventsSince(s, since));
        var r = InsightsReport.Build(events, now, days);
        return new
        {
            days,
            period = r.Period.Label,
            events = r.EventCount,
            watch = new
            {
                r.Watch.TotalSeconds,
                r.Watch.TotalText,
                r.Watch.Sessions,
                r.Watch.ActiveDays,
                r.Watch.AveragePerActiveDayText,
                r.Watch.PreviousText,
                r.Watch.ChangePercent,
                r.Watch.StreakDays,
                byDay = r.Watch.ByDay.Select(d => new { d.Date, d.Weekday, d.Seconds, d.Text }),
                byKind = r.Watch.ByKind.Select(Slim),
                topApps = r.Watch.TopApps.Take(6).Select(Slim),
                topTitles = r.Watch.TopTitles.Take(5).Select(Slim),
            },
            rhythm = new
            {
                r.Rhythm.Cells,
                r.Rhythm.MaxCell,
                r.Rhythm.PeakText,
                byBlock = r.Rhythm.ByBlock.Select(b => new { b.Label, b.Seconds, b.Text, b.Share }),
            },
            receiver = new
            {
                r.Receiver.InUseText,
                r.Receiver.PowerOns,
                inputs = r.Receiver.Inputs.Take(5).Select(Slim),
                volume = new
                {
                    r.Receiver.Volume.Average,
                    r.Receiver.Volume.AverageDb,
                    r.Receiver.Volume.Changes,
                    byBlock = r.Receiver.Volume.ByBlock.Select(b => new { b.Label, b.Average, b.AverageDb }),
                    byApp = r.Receiver.Volume.ByApp.Take(6).Select(a => new { a.Label, a.Average, a.AverageDb }),
                },
            },
            automations = new
            {
                r.Automations.Runs,
                r.Automations.Acted,
                r.Automations.Failed,
                top = r.Automations.ByAutomation.Take(5).Select(a => new { a.Name, a.Runs, a.Acted }),
            },
            insights = r.Insights.Take(4).Select(i => new { i.Tone, i.Title, i.Text }),
        };

        static object Slim(InsightsReport.Slice s) => new { s.Label, s.Seconds, s.Text, s.Share };
    }

    private static string InsightsText(JsonElement s)
    {
        var watch = s.GetProperty("watch");
        var top = watch.GetProperty("topApps").EnumerateArray().Take(3).Select(a => $"{Str(a, "label")} {Str(a, "text")}");
        return $"Insights, {Str(s, "period")}: {Str(watch, "totalText")} in total; top: {string.Join(", ", top)}. Peak: {Str(s.GetProperty("rhythm"), "peakText")}.";
    }

    // ── Radio search ──────────────────────────────────────────────────────────────────────────────────────────────

    internal async Task<object> BuildRadioSearchPayloadAsync(JsonElement args, CancellationToken ct)
    {
        var query = GetString(args, "query").Trim();
        var tag = GetString(args, "tag").Trim();
        var hiRes = GetBool(args, "hi_res_only") ?? false;
        var settings = _settingsStore.Current;
        var savedUrls = settings.Receiver.Stations.Select(s => s.Url.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stations = await _radioBrowser.SearchStationsAsync(
            query.Length > 0 ? query : null, tag.Length > 0 ? tag : null, null, hiRes, 12, ct);
        return new
        {
            query,
            tag,
            hiRes,
            stations = stations.Select(s => new
            {
                name = s.Name,
                url = s.UrlResolved,
                codec = s.Codec,
                bitrate = s.Bitrate,
                country = s.Country,
                tags = s.Tags,
                saved = savedUrls.Contains(s.UrlResolved?.Trim() ?? string.Empty),
            }),
            canSave = settings.Mcp.PermissionTier >= McpPermissionTier.FullOrchestration,
        };
    }

    private static string RadioText(JsonElement s) =>
        $"{Count(s, "stations")} stations: " + string.Join(", ", s.GetProperty("stations").EnumerateArray().Take(8).Select(st => Str(st, "name")));

    // ── Volume memory ─────────────────────────────────────────────────────────────────────────────────────────────

    internal object BuildVolumeMemoryPayload()
    {
        var settings = _settingsStore.Current;
        var status = _stateStore.Snapshot.IsConnected ? _stateStore.Snapshot.Status : null;
        var current = ListeningSource.From(status, _pluginHost.GetPlugins());
        return new
        {
            enabled = settings.VolumeMemory.Enabled,
            onlyLower = settings.VolumeMemory.OnlyLower,
            minDifference = settings.VolumeMemory.MinDifference,
            safeVolume = Math.Clamp(settings.Mcp.MaxSafeVolume, 15, 98),
            current = current?.Key,
            levels = _volumeMemory.Snapshot().Select(l => new
            {
                key = l.Key,
                kind = l.Kind,
                label = l.Label,
                level = Math.Round(l.Level),
                levelDb = DenonVolumeMath.FormatDecibels(Math.Round(l.Level)),
                minutes = (int)Math.Round(l.Seconds / 60),
                updated = l.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }),
            canEdit = settings.Mcp.PermissionTier >= McpPermissionTier.FullOrchestration,
        };
    }

    private static string VolumeMemoryText(JsonElement s) =>
        $"Volume memory: {Count(s, "levels")} learned levels. " +
        string.Join(", ", s.GetProperty("levels").EnumerateArray().Take(8).Select(l => $"{Str(l, "label")} {Num(l, "level")}"));
}

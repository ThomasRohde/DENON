using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Denon.App.Core.Mcp;

/// <summary>Resources, resource templates, prompts and completion of the Denon Desktop MCP server.</summary>
public sealed partial class DenonMcpServer
{
    private const string JsonMime = "application/json";

    private static ListResourcesResult ListResources() => new()
    {
        Resources =
        [
            new Resource
            {
                Uri = RoomStateUri,
                Name = "Live Living-Room State Graph",
                Description = "Unified real-time state of Denon/Marantz AVR, Now Playing, Up-Next Queue, Philips Hue, LG webOS TV, Home Assistant, Room Doctor Insights, Undo snapshot, and safety limits.",
                MimeType = JsonMime,
            },
            new Resource
            {
                Uri = "denon://diagnostics/health",
                Name = "Home Theater Diagnostics & Health",
                Description = "Cross-device Room Doctor insights, LIVE PUSH status, and recent warning/error log entries.",
                MimeType = JsonMime,
            },
            new Resource
            {
                Uri = "denon://catalog/capabilities",
                Name = "Inputs, Sound Modes, Smart Scenes, Rules & Plugins Catalog",
                Description = "Complete reference of valid AVR input codes, sound mode codes, configured Smart Scenes, automations and their node types, and all installed Plugin Actions.",
                MimeType = JsonMime,
            },
            new Resource
            {
                Uri = "denon://memory/habits",
                Name = "Local Listening Habits & Preferences Memory",
                Description = "Locally learned preferred volume per time of day (Morning/Afternoon/Evening/LateNight), top inputs, sound modes, scenes, and plugin actions.",
                MimeType = JsonMime,
            },
            .. AppCardResources(),
        ],
    };

    private static ListResourceTemplatesResult ListResourceTemplates() => new()
    {
        ResourceTemplates =
        [
            new ResourceTemplate
            {
                UriTemplate = "denon://plugins/{pluginId}",
                Name = "Plugin Details & Actions",
                Description = "Inspect live health, status, capabilities, and actions for a specific plugin (e.g. philips-hue, smart-tv, home-assistant).",
                MimeType = JsonMime,
            },
            new ResourceTemplate
            {
                UriTemplate = "denon://scenes/{sceneId}",
                Name = "Smart Scene Configuration",
                Description = "Read the complete AVR, acoustic, and chained plugin action configuration for a specific Smart Scene (e.g. movie-night, late-night).",
                MimeType = JsonMime,
            },
            new ResourceTemplate
            {
                UriTemplate = "denon://logs/events/{stream}",
                Name = "Smart Event & Habit Telemetry Stream",
                Description = "Read recent habit-ready structured events from a telemetry stream (media, avr, tv, lighting, control, all, or plugin:<id>).",
                MimeType = JsonMime,
            },
        ],
    };

    private ReadResourceResult ReadResource(string uri)
    {
        var sw = Stopwatch.StartNew();
        var lower = uri.ToLowerInvariant();
        if (ReadAppCardResource(lower) is { } card)
        {
            LogActivity("resources/read", uri, "OK", sw.ElapsedMilliseconds);
            return card;
        }

        object payload;

        if (lower == RoomStateUri)
        {
            payload = _intelligence.BuildRoomStatePayload();
        }
        else if (lower == "denon://diagnostics/health")
        {
            payload = _intelligence.BuildDiagnosticsPayload();
        }
        else if (lower == "denon://catalog/capabilities")
        {
            payload = _intelligence.BuildCapabilitiesPayload();
        }
        else if (lower == "denon://memory/habits")
        {
            payload = _intelligence.BuildHabitsPayload();
        }
        else if (lower.StartsWith("denon://plugins/", StringComparison.Ordinal))
        {
            var pluginId = Uri.UnescapeDataString(uri["denon://plugins/".Length..].Trim('/'));
            var plugin = _pluginHost.GetPlugins().FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
                ?? throw NotFound($"Plugin '{pluginId}' not found.");
            payload = new
            {
                id = plugin.Id,
                name = plugin.Name,
                version = plugin.Version,
                category = plugin.Category,
                enabled = plugin.IsEnabled,
                health = plugin.Health.ToString(),
                status = plugin.StatusText,
                lastError = plugin.LastError,
                actions = plugin.Actions.Select(a => new
                {
                    id = a.ActionId,
                    mcpToolName = BuildPluginToolName(plugin.Id, a.ActionId),
                    name = a.DisplayName,
                    description = a.Description,
                    category = a.Category,
                }).ToArray(),
            };
        }
        else if (lower.StartsWith("denon://scenes/", StringComparison.Ordinal))
        {
            var sceneId = Uri.UnescapeDataString(uri["denon://scenes/".Length..].Trim('/'));
            payload = _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s =>
                          s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase) ||
                          s.Name.Equals(sceneId, StringComparison.OrdinalIgnoreCase))
                      ?? throw NotFound($"Smart Scene '{sceneId}' not found.");
        }
        else if (lower.StartsWith("denon://logs/events/", StringComparison.Ordinal))
        {
            var streamName = Uri.UnescapeDataString(uri["denon://logs/events/".Length..].Trim('/'));
            var events = _telemetry.QueryRecentEvents(streamName, hours: 72, limit: 120);
            payload = new
            {
                stream = string.IsNullOrWhiteSpace(streamName) ? "all" : streamName,
                hours = 72,
                count = events.Count,
                availableStreams = _telemetry.ListAvailableStreams(),
                stats = _telemetry.GetStats(),
                events,
            };
        }
        else
        {
            throw NotFound($"Unknown resource URI '{uri}'.");
        }

        LogActivity("resources/read", uri, "OK", sw.ElapsedMilliseconds);
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = JsonMime, Text = JsonSerializer.Serialize(payload, JsonPrettyOptions) }],
        };

        // 2026-07-28: a missing resource is invalid params (-32602), not the old -32002.
        McpProtocolException NotFound(string message)
        {
            LogActivity("resources/read", $"{uri}: {message}", "Not found", sw.ElapsedMilliseconds);
            return new McpProtocolException(message, McpErrorCode.InvalidParams);
        }
    }

    private CompleteResult Complete(Argument? argument)
    {
        var argName = argument?.Name ?? string.Empty;
        var argValue = argument?.Value ?? string.Empty;
        IEnumerable<string> candidates = argName.ToLowerInvariant() switch
        {
            "pluginid" => _pluginHost.GetPlugins().Select(p => p.Id),
            "sceneid" => _settingsStore.Current.Receiver.Scenes.Select(s => s.Id),
            "stream" => new[] { "all" }.Concat(_telemetry.ListAvailableStreams()),
            "vibe" => ["cozy rainy jazz night", "sci-fi cinema night", "late night reading", "upbeat party sync"],
            "goal" => ["clearer movie dialogue at night", "pure audiophile stereo", "punchier subwoofer bass"],
            _ => [],
        };

        var values = candidates
            .Where(c => string.IsNullOrWhiteSpace(argValue) || c.Contains(argValue, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();
        return new CompleteResult { Completion = new Completion { Values = values, Total = values.Count, HasMore = false } };
    }

    private static ListPromptsResult ListPrompts() => new()
    {
        Prompts =
        [
            new Prompt
            {
                Name = "design_atmosphere",
                Description = "Design and apply a cohesive living-room atmosphere across Denon AVR, Philips Hue, LG webOS TV, and music.",
                Arguments = [new PromptArgument { Name = "vibe", Description = "Desired mood or activity (e.g. 'cozy rainy jazz night', 'sci-fi cinema', 'late night reading')", Required = true }],
            },
            new Prompt
            {
                Name = "diagnose_home_theater",
                Description = "Inspect cross-device state and logs to diagnose & fix audio/video/TV/lighting issues.",
                Arguments = [new PromptArgument { Name = "symptom", Description = "What feels wrong (e.g. 'no sound from TV', 'too quiet', 'lights not syncing')", Required = false }],
            },
            new Prompt
            {
                Name = "acoustic_advisor",
                Description = "Recommend and apply Audyssey, Dynamic EQ/Volume, Dialog/Subwoofer trims safely for the user's goal.",
                Arguments = [new PromptArgument { Name = "goal", Description = "Acoustic objective (e.g. 'clearer movie dialogue at night', 'punchier bass for music')", Required = true }],
            },
            new Prompt
            {
                Name = "architect_automation",
                Description = "Build a validated Smart Scene and/or automation graph from a plain-English habit description.",
                Arguments = [new PromptArgument { Name = "habit", Description = "Natural language trigger & actions (e.g. 'When I open Plex after 8pm, dim Hue and switch TV to HDMI 2')", Required = true }],
            },
        ],
    };

    private GetPromptResult GetPrompt(string name, IDictionary<string, JsonElement>? arguments)
    {
        string Arg(string key, string fallback) =>
            arguments is not null && arguments.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text
                ? text
                : fallback;

        var promptText = name.ToLowerInvariant() switch
        {
            "design_atmosphere" =>
                $"Design and apply a cohesive living-room atmosphere for: \"{Arg("vibe", "evening relax")}\". " +
                "First read denon://room/state and denon://memory/habits to respect the user's preferred time-of-day volume and available plugins. " +
                "Then use apply_atmosphere (plus media_search_radio or media_play_youtube if music fits) to orchestrate Denon AVR, Philips Hue, and LG webOS TV smoothly.",
            "diagnose_home_theater" =>
                $"Diagnose this home theater symptom: \"{Arg("symptom", "general health check")}\". " +
                "Call diagnose_room and read denon://diagnostics/health + denon://room/state. Identify any cross-device mismatches (e.g. LG TV speaker output vs HDMI eARC, muted AVR, low volume, plugin warning) and either fix them with fix_room_issue or explain the exact cause clearly.",
            "acoustic_advisor" =>
                $"Optimize Denon AVR acoustics for: \"{Arg("goal", "clear dialogue at moderate volume")}\". " +
                "Read denon://room/state first, then use avr_tune_acoustics safely and explain what each adjustment does.",
            "architect_automation" =>
                $"Create a Denon Desktop Smart Scene and/or automation for this habit: \"{Arg("habit", "movie night automation")}\". " +
                "Read denon://catalog/capabilities to use valid input codes, sound modes, and plugin action IDs and automation node types, then call create_or_update_scene and/or create_or_update_automation (a trigger, optional conditions, then actions).",
            _ => throw new McpProtocolException($"Unknown prompt '{name}'.", McpErrorCode.InvalidParams),
        };

        LogActivity("prompts/get", name, "OK", 0);
        return new GetPromptResult
        {
            Description = $"Denon Desktop guided workflow: {name}",
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = promptText } }],
        };
    }
}

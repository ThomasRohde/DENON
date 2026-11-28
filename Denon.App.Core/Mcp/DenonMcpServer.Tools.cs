using System.Text.Json;
using System.Text.RegularExpressions;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using ModelContextProtocol.Protocol;

namespace Denon.App.Core.Mcp;

/// <summary>The tools of the Denon Desktop MCP server: their definitions, and what each call does.</summary>
public sealed partial class DenonMcpServer
{
    /// <summary>The tools the server lists: every tool, less the ones turned off (by name or by plugin).</summary>
    private List<Tool> BuildToolDefinitions()
    {
        var cfg = _settingsStore.Current.Mcp;
        return BuildAllToolDefinitions()
            .Where(t => !McpToolAccess.IsDenied(cfg, t.Tool.Name, t.PluginId))
            .Select(static t => t.Tool)
            .ToList();
    }

    /// <summary>
    /// Every tool the server can serve now, also the ones turned off, for the Tool access switches in Settings. A
    /// plugin tool names its plugin.
    /// </summary>
    public IReadOnlyList<McpToolCatalogEntry> GetToolCatalog()
    {
        var pluginNames = _pluginHost.GetPlugins().ToDictionary(static p => p.Id, static p => p.Name, StringComparer.OrdinalIgnoreCase);
        return BuildAllToolDefinitions()
            .Select(t =>
            {
                var pluginId = t.PluginId ?? McpToolAccess.NamedToolPlugin(t.Tool.Name);
                var pluginName = pluginId is not null && pluginNames.TryGetValue(pluginId, out var name) ? name : pluginId;
                return new McpToolCatalogEntry(
                    t.Tool.Name,
                    string.IsNullOrWhiteSpace(t.Tool.Title) ? t.Tool.Name : t.Tool.Title,
                    pluginId,
                    pluginName,
                    t.Tool.Annotations?.ReadOnlyHint == true);
            })
            .ToList();
    }

    private List<(Tool Tool, string? PluginId)> BuildAllToolDefinitions()
    {
        var tools = new List<Tool>
        {
            // Room Intelligence & Diagnostics
            Tool("get_room_state",
                "Get the complete live living-room state graph: Denon AVR, Now Playing, Queue, Philips Hue, LG webOS TV, Home Assistant, Room Doctor Insights, Undo state, and safety ceiling.",
                EmptySchema(),
                title: "Get Live Room State",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("timestamp", "string"),
                    ("receiver", "object"),
                    ("mediaQueue", "object"),
                    ("plugins", "array"),
                    ("insights", "array"),
                    ("safety", "object"))),
            Tool("diagnose_room",
                "Run the cross-device Self-Healing Room Doctor to detect mismatches (muted while playing, late-night volume, TV eARC vs speaker mismatch, OLED burn-in risk, plugin warnings).",
                EmptySchema(),
                title: "Diagnose Living Room",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("count", "integer"),
                    ("insights", "array"))),
            Tool("fix_room_issue",
                "Execute a Room Doctor auto-fix key (e.g. 'avr.unmute', 'avr.safe_volume', 'avr.late_night_comfort', 'avr.dim_panel', 'avr.dynvol_off', 'hue.movie_dim', 'room.route_tv_earc', 'tv.screen_off', 'avr.fix_silent', 'avr.refresh').",
                ObjSchema(("fix_action_key", "string", "The fixActionKey returned by diagnose_room", true)),
                title: "Auto-Fix Room Issue",
                idempotent: true),
            Tool("get_listening_habits",
                "Read locally learned listening habits (average volume by Morning/Afternoon/Evening/LateNight, top inputs, sound modes, scenes, and plugin actions).",
                EmptySchema(),
                title: "Get Listening Habits",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("updatedAt", "string"),
                    ("totalObservations", "integer"),
                    ("preferredVolumeByTimeBlock", "object"))),
            Tool("get_activity_history",
                "Query structured habit-ready event logs (media sessions with 15s dwell filter, AVR power/input/settled-volume deltas, LG TV apps/channels, Philips Hue lighting, and control origins distinguishing app commands vs physical remotes/phone streams).",
                ObjSchema(
                    ("stream", "string", "Event stream: 'all', 'media', 'avr', 'tv', 'lighting', 'control', or 'plugin:<id>' (default: 'all')", false),
                    ("hours", "integer", "Lookback window in hours (1-2160, default: 24)", false),
                    ("limit", "integer", "Maximum events to return, newest first (1-500, default: 80)", false),
                    ("origin", "string", "Optional origin filter: 'external', 'desktop_ui', 'mcp_agent', 'phone_remote', 'automation', 'smart_scene'", false)),
                title: "Get Activity & Media History",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("stream", "string"),
                    ("hours", "integer"),
                    ("count", "integer"),
                    ("availableStreams", "array"),
                    ("stats", "object"),
                    ("events", "array"))),
            Tool("get_watch_time",
                "Watch and listen time per day, kind, app and title from media sessions, WITHOUT double counting: " +
                "one viewing (Chromecast program + LG TV HDMI input + Denon TV Audio) counts once, and paused cast time " +
                "does not count. Use this instead of summing raw session events from get_activity_history.",
                ObjSchema(
                    ("days", "integer", "Number of days including today (1-90, default: 7)", false),
                    ("top", "integer", "Number of apps and titles to list (1-50, default: 10)", false)),
                title: "Get Watch & Listen Time",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("days", "integer"),
                    ("totalSeconds", "integer"),
                    ("totalText", "string"),
                    ("byDay", "array"),
                    ("byKind", "array"),
                    ("byApp", "array"),
                    ("topTitles", "array"))),
            Tool("restore_previous_state",
                "1-click Undo: restore the Denon AVR state captured before the most recent AI / atmosphere / scene mutation.",
                EmptySchema(),
                title: "Restore Previous AVR State",
                idempotent: true),
            Tool("apply_atmosphere",
                "High-level semantic multi-device orchestrator. Captures an Undo snapshot first, enforces MaxSafeVolume, and coordinates Denon AVR + Philips Hue + LG webOS TV in one call.",
                ObjSchema(
                    ("reason", "string", "Short description of the atmosphere (shown in Undo history & optional TV toast)", false),
                    ("volume", "integer", "Target master volume (0-98, automatically capped by MaxSafeVolume)", false),
                    ("input_source", "string", "AVR input code (e.g. TV, CBL/SAT, GAME, MPLAY, CD, NET, BT, AUX1)", false),
                    ("sound_mode", "string", "AVR sound mode (e.g. STEREO, MOVIE, MUSIC, GAME, DIRECT, PURE DIRECT, MCH STEREO)", false),
                    ("dynamic_volume", "string", "Audyssey Dynamic Volume: OFF, LIT, MED, or HEV", false),
                    ("hue_action", "string", "Optional Philips Hue actionId (e.g. hue.movie_dim, hue.candlelight, hue.all_on, hue.all_off, hue.music_sync_on)", false),
                    ("tv_action", "string", "Optional LG webOS TV actionId (e.g. tv.hdmi1, tv.hdmi2, tv.audio_earc, tv.youtube, tv.plex, tv.screen_off)", false),
                    ("oled_screen_off", "boolean", "If true, blanks LG OLED panel (Audio-Only Mode) to prevent burn-in during music", false),
                    ("tv_toast", "string", "Optional short message to display as an on-screen toast banner on the LG TV", false)),
                title: "Apply Multi-Device Atmosphere",
                idempotent: true,
                outputSchema: OutputObjSchema(
                    ("applied", "string"),
                    ("undoAvailable", "boolean"),
                    ("steps", "array"))),

            // AVR Core Controls & Acoustics
            Tool("avr_set_power",
                "Power the Denon/Marantz AVR ON or put it into Standby.",
                ObjSchema(("power_on", "boolean", "true to power ON, false for Standby", true)),
                title: "Set AVR Power",
                destructive: true, idempotent: true),
            Tool("avr_set_volume",
                "Set Denon AVR master volume (0-98 scale, where 80 = 0.0 dB, 50 = -30.0 dB). Enforces the configured MaxSafeVolume safety ceiling.",
                ObjSchema(("volume", "integer", "Target volume (0-98)", true)),
                title: "Set AVR Volume",
                idempotent: true),
            Tool("avr_set_mute",
                "Mute or unmute Denon AVR master audio.",
                ObjSchema(("muted", "boolean", "true to mute, false to unmute", true)),
                title: "Set AVR Mute",
                idempotent: true),
            Tool("avr_set_input",
                "Switch Denon AVR input source (e.g. TV, GAME, MPLAY, CBL/SAT, DVD, BD, CD, NET, BT, TUNER, AUX1).",
                ObjSchema(("input_source", "string", "AVR input source code", true)),
                title: "Switch AVR Input",
                idempotent: true),
            Tool("avr_set_sound_mode",
                "Switch Denon AVR sound mode (e.g. STEREO, DIRECT, PURE DIRECT, MOVIE, MUSIC, GAME, AUTO, MCH STEREO).",
                ObjSchema(("sound_mode", "string", "Sound mode code", true)),
                title: "Switch AVR Sound Mode",
                idempotent: true),
            Tool("avr_playback_control",
                "Control HEOS playback transport.",
                ObjSchema(("action", "string", "One of: play, pause, stop, next, previous", true)),
                title: "HEOS Playback Transport"),
            Tool("avr_set_zone2",
                "Control Zone 2 (the receiver's second room): power, source, volume and mute. Give one or more fields; " +
                "power on comes first, then source, volume and mute, and power off last. Volume is the receiver's 0-98 number and is capped by MaxSafeVolume.",
                ObjSchema(
                    ("power_on", "boolean", "true for Zone 2 ON, false for OFF", false),
                    ("source", "string", "Zone 2 source code: SOURCE (follow the main zone), TV, NET, BD, GAME, MPLAY, SAT/CBL, BT, CD, AUX1, PHONO, TUNER", false),
                    ("volume", "integer", "Zone 2 volume (0-98, capped by MaxSafeVolume)", false),
                    ("muted", "boolean", "true to mute Zone 2, false to unmute", false)),
                title: "Control Zone 2",
                idempotent: true),
            Tool("heos_get_groups",
                "List the HEOS players on the network and the multi-room groups they form (leader, members, group volume 0-100 and mute).",
                EmptySchema(),
                title: "Get HEOS Groups",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("ownPlayerId", "integer"),
                    ("players", "array"),
                    ("groups", "array"))),
            Tool("heos_set_group",
                "Group HEOS players for multi-room play: the first player leads and plays its source, the others play the same. " +
                "One player alone ends the group it leads. Requires FullOrchestration: it changes what other rooms play.",
                ObjSchema(("players", "string", "Player names or ids, comma-separated, leader first (e.g. 'Living Room, Kitchen')", true)),
                title: "Set HEOS Group",
                destructive: true, idempotent: true),
            Tool("heos_set_group_volume",
                "Set the volume (0-98, capped by MaxSafeVolume) and/or mute of a HEOS group. Without group_id: the group this receiver is in.",
                ObjSchema(
                    ("group_id", "integer", "Group id from heos_get_groups (default: this receiver's group)", false),
                    ("volume", "integer", "Group volume (0-98, capped by MaxSafeVolume)", false),
                    ("muted", "boolean", "true to mute the group, false to unmute", false)),
                title: "Set HEOS Group Volume",
                idempotent: true),
            Tool("avr_set_eco_mode",
                "Set Denon ECO power mode (ON, AUTO, OFF).",
                ObjSchema(("mode", "string", "ON, AUTO, or OFF", true)),
                title: "Set AVR ECO Mode",
                idempotent: true),
            Tool("avr_set_sleep_timer",
                "Set or cancel the AVR sleep timer in minutes (0 or omitted cancels sleep timer).",
                ObjSchema(("minutes", "integer", "Sleep timer minutes (0 to cancel, or 10-120)", true)),
                title: "Set AVR Sleep Timer",
                idempotent: true),
            Tool("avr_get_acoustics",
                "Read live Denon AVR acoustic configuration over Telnet :23 (Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Compressed Audio Restorer, Dimmer, Subwoofer/Dialog trims, and Tone Control).",
                EmptySchema(),
                title: "Get AVR Acoustics",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("multEq", "string"),
                    ("dynamicVolume", "string"),
                    ("restorer", "string"),
                    ("dimmer", "string"),
                    ("subwooferTrimDb", "integer"),
                    ("dialogTrimDb", "integer"),
                    ("bassDb", "integer"),
                    ("trebleDb", "integer"))),
            Tool("avr_tune_acoustics",
                "Adjust live Denon AVR acoustic settings over Telnet :23 (any omitted parameter is left unchanged).",
                ObjSchema(
                    ("multeq", "string", "Audyssey MultEQ: AUDYSSEY, BYP.LR, FLAT, or OFF", false),
                    ("dynamic_eq", "boolean", "Enable/disable Audyssey Dynamic EQ", false),
                    ("dynamic_volume", "string", "Audyssey Dynamic Volume: OFF, LIT, MED, or HEV", false),
                    ("restorer", "string", "Compressed Audio Restorer: OFF, LOW, MED, or HI", false),
                    ("dimmer", "string", "Front panel display dimmer: BRI, DIM, DAR, or OFF", false),
                    ("subwoofer_trim_db", "integer", "Subwoofer trim in dB (-12 to +12)", false),
                    ("dialog_trim_db", "integer", "Dialog enhancer trim in dB (-12 to +12)", false),
                    ("bass_db", "integer", "Tone Bass trim in dB (-6 to +6)", false),
                    ("treble_db", "integer", "Tone Treble trim in dB (-6 to +6)", false)),
                title: "Tune AVR Acoustics",
                idempotent: true),

            // Media, Lyrics & Queue DJ
            Tool("media_search_radio",
                "Search 40,000+ global internet radio stations via Radio Browser.",
                ObjSchema(
                    ("query", "string", "Station name or keyword", false),
                    ("tag", "string", "Genre/style tag (e.g. jazz, ambient, synthwave, classical)", false),
                    ("hi_res_only", "boolean", "Only return lossless FLAC / high-bitrate stations", false),
                    ("limit", "integer", "Max results (default 10)", false)),
                title: "Search Internet Radio",
                readOnly: true, idempotent: true, openWorld: true,
                outputSchema: OutputObjSchema(
                    ("count", "integer"),
                    ("stations", "array"))),
            Tool("media_play_station_url",
                "Play any direct HTTP/HTTPS internet radio or audio stream URL on the Denon AVR.",
                ObjSchema(
                    ("url", "string", "Direct stream URL", true),
                    ("title", "string", "Friendly station/track title", false),
                    ("low_volume", "boolean", "If true, sets safe low volume before playing", false)),
                title: "Play Internet Radio Stream",
                openWorld: true),
            Tool("media_search_youtube",
                "Search YouTube / YouTube Music for songs, mixes, or live sets playable on Denon HEOS.",
                ObjSchema(
                    ("query", "string", "Search query or YouTube URL", true),
                    ("music_mode", "boolean", "Prioritize music tracks (default true)", false),
                    ("limit", "integer", "Max results (default 6)", false)),
                title: "Search YouTube Music",
                readOnly: true, idempotent: true, openWorld: true,
                outputSchema: OutputObjSchema(
                    ("count", "integer"),
                    ("tracks", "array"))),
            Tool("media_play_youtube",
                "Resolve a YouTube search query or URL and stream it directly to Denon HEOS via the local LAN audio relay.",
                ObjSchema(
                    ("query_or_url", "string", "YouTube video URL or search phrase (plays top match)", true),
                    ("queue_only", "boolean", "If true, adds to Up-Next queue instead of interrupting current track", false)),
                title: "Play or Queue YouTube Track",
                openWorld: true),
            Tool("media_get_now_playing_lyrics",
                "Fetch synced/plain lyrics via LRCLIB for the currently playing track (or an explicitly supplied title & artist).",
                ObjSchema(
                    ("title", "string", "Optional track title (defaults to the AVR's live Now Playing track)", false),
                    ("artist", "string", "Optional artist name (defaults to the AVR's live Now Playing artist)", false)),
                title: "Get Now Playing Lyrics",
                readOnly: true, idempotent: true, openWorld: true,
                outputSchema: OutputObjSchema(
                    ("found", "boolean"),
                    ("title", "string"),
                    ("artist", "string"),
                    ("isSynced", "boolean"),
                    ("lineCount", "integer"),
                    ("lyrics", "string"))),
            Tool("media_get_queue",
                "Inspect the current Denon Desktop YouTube Up-Next queue, shuffle/repeat settings, active track, and history count.",
                EmptySchema(),
                title: "Get Up-Next Queue",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("isActive", "boolean"),
                    ("currentIndex", "integer"),
                    ("isShuffleEnabled", "boolean"),
                    ("repeatMode", "string"),
                    ("queueCount", "integer"),
                    ("queue", "array"))),
            Tool("media_manage_queue",
                "Manage the YouTube Up-Next playback queue: clear, toggle_shuffle, cycle_repeat, remove (by entry_id), play_index (0-based index), next, or previous.",
                ObjSchema(
                    ("action", "string", "One of: clear, toggle_shuffle, cycle_repeat, remove, play_index, next, previous", true),
                    ("entry_id", "string", "Queue entryId (required when action is 'remove')", false),
                    ("index", "integer", "0-based queue index (required when action is 'play_index')", false)),
                title: "Manage Up-Next Queue",
                destructive: true),

            // Curated Smart Lighting & LG webOS TV Direct Tools
            Tool("hue_set_color",
                "Set Philips Hue lights to a custom hex color (#RRGGBB), white warmth in Kelvin (2000-6500K), and/or brightness (1-100%), optionally targeting a specific Hue room/zone.",
                ObjSchema(
                    ("hex", "string", "Optional hex color (e.g. '#FF6B2B', '#00D4FF')", false),
                    ("kelvin", "integer", "Optional white color temperature in Kelvin (2000 warm candlelight to 6500 crisp daylight)", false),
                    ("brightness", "integer", "Optional brightness percentage (1-100)", false),
                    ("room", "string", "Optional Hue room/zone name or 'all' (defaults to active target room)", false)),
                title: "Set Philips Hue Color & Warmth",
                idempotent: true),
            Tool("tv_computer",
                "Unified LG webOS TV Computer-Use tool: see the TV screen via live screenshots (returns MCP image), read/write direct webOS system settings ('get_settings' / 'set_settings' for picture/sound), open URLs in the LG Web Browser, launch/close apps (including 'settings' and casting YouTube videos via content_id), move/drag/click/scroll the Magic Remote pointer (relative dx/dy OR normalized 0..1000 x/y), press remote buttons, type text, or chain multiple steps in one batch with screenshot_after=true.",
                BuildTvComputerSchema(),
                title: "LG webOS TV Computer Use",
                openWorld: true,
                outputSchema: OutputObjSchema(
                    ("action", "string"),
                    ("stepsExecuted", "array"),
                    ("tv", "object"),
                    ("cursor", "object"),
                    ("installedApps", "array"),
                    ("inputs", "array"))),

            // Generative Smart Scene & Automation Architect
            Tool("list_scenes_and_rules",
                "List all configured Smart Scenes and automations (graphs of trigger, condition and action nodes).",
                EmptySchema(),
                title: "List Smart Scenes & Automations",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("scenes", "array"),
                    ("automations", "array"))),
            Tool("activate_smart_scene",
                "Activate an existing Smart Scene by ID or Name (including its chained plugin actions).",
                ObjSchema(("scene_id_or_name", "string", "Smart Scene ID or Name (e.g. 'movie-night' or 'Late Night')", true)),
                title: "Activate Smart Scene",
                idempotent: true),
            Tool("create_or_update_scene",
                "Create or update a persistent Smart Scene in settings.json with AVR settings and optional Hue/TV/HA plugin action.",
                ObjSchema(
                    ("id", "string", "Stable slug ID (e.g. 'blade-runner-night')", true),
                    ("name", "string", "Display name (e.g. 'Blade Runner Night')", true),
                    ("description", "string", "Short description of the scene", false),
                    ("input_source", "string", "AVR input code (e.g. TV, GAME, NET)", false),
                    ("sound_mode", "string", "AVR sound mode (e.g. MOVIE, STEREO, PURE DIRECT)", false),
                    ("target_volume", "integer", "Target volume (0-98, capped by MaxSafeVolume)", false),
                    ("dynamic_volume", "string", "OFF, LIT, MED, or HEV", false),
                    ("zone2_on", "boolean", "Zone 2 power: true on, false off", false),
                    ("zone2_source", "string", "Zone 2 source code (SOURCE, TV, NET, ...)", false),
                    ("zone2_volume", "integer", "Zone 2 volume (0-98, capped by MaxSafeVolume)", false),
                    ("zone2_muted", "boolean", "Zone 2 mute", false),
                    ("heos_group_with", "string", "HEOS player names to group with this receiver, comma-separated (this receiver leads). 'none' ends its group.", false),
                    ("plugin_id", "string", "Optional plugin ID to chain (e.g. 'philips-hue', 'smart-tv', 'home-assistant')", false),
                    ("plugin_action_id", "string", "Optional plugin action ID to chain (e.g. 'hue.movie_dim', 'tv.screen_off')", false)),
                title: "Create or Update Smart Scene",
                idempotent: true),
            Tool("create_or_update_automation",
                "Create or replace an automation graph in settings.json. Nodes: trigger.event (config event, filter), trigger.webhook (hook, filter), " +
                "trigger.schedule (time HH:mm, days), trigger.app_launch (processes), condition.event (filter), condition.time (from, to, days), " +
                "condition.receiver (power any/on/standby/idle — idle: standby, or on with HEOS stopped or paused; input, muted any/yes/no), condition.recent_event (event names comma-separated, seconds, filter; yes when one happened that recently), action.scene (sceneId), action.plugin (pluginId, actionId, p:<param>), " +
                "action.receiver (command power_on/power_off/mute/unmute/volume/input/sound_mode/zone2_on/zone2_off/zone2_volume/zone2_source/zone2_mute/zone2_unmute/group_with/ungroup/group_volume, value; group_with takes HEOS player names comma-separated), action.delay (seconds). " +
                "Edges {from, port, to}: port 'out', or 'yes'/'no' after a condition. No cycles. Read denon://catalog/capabilities for node types, scenes and plugin actions. " +
                "The automation starts disabled unless enabled is true; the user turns it on in the app.",
                ObjSchema(
                    ("automation", "object", "{ id?, name, enabled?, cooldownSeconds?, nodes: [{ id, type, x?, y?, config }], edges: [{ from, port, to }] }. Without id a new one is made.", true)),
                title: "Create or Update Automation",
                idempotent: true),
        };

        // MCP App: the now-playing card (hosts without MCP Apps get its data as text).
        tools.AddRange(AppCardTools());
        tools.AddRange(AppHelperTools());
        var all = tools.Select(static t => (t, (string?)null)).ToList();

        // Zero-config auto-bridge: expose enabled plugin actions as annotated MCP tools
        // (Skip hue.set_color since curated hue_set_color already exposes its full typed schema)
        foreach (var plugin in _pluginHost.GetPlugins().Where(p => p.IsEnabled))
        {
            foreach (var action in plugin.Actions)
            {
                if (action.ActionId.Equals("hue.set_color", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var toolName = BuildPluginToolName(plugin.Id, action.ActionId);
                var (isReadOnly, isDestructive, isIdempotent) = McpPluginToolBridge.ResolveHints(action);

                all.Add((Tool(
                    toolName,
                    $"[{plugin.Name}] {action.DisplayName}: {action.Description}",
                    McpPluginToolBridge.BuildInputSchema(action),
                    title: $"{plugin.Name}: {action.DisplayName}",
                    readOnly: isReadOnly,
                    destructive: isDestructive,
                    idempotent: isIdempotent), plugin.Id));
            }
        }

        return all;
    }

    private async Task<CallToolResult> HandleToolCallAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var cfg = _settingsStore.Current.Mcp;

        // The MCP App cards only read (allowed in all permission tiers including Observer).
        if (await TryHandleAppCardAsync(toolName, args, ct) is { } card)
        {
            return card;
        }

        // Read-only tools (allowed in all permission tiers including Observer)
        switch (toolName)
        {
            case "get_room_state":
                return BuildToolJson(_intelligence.BuildRoomStatePayload());

            case "diagnose_room":
            {
                var insights = _intelligence.DiagnoseRoom();
                return BuildToolJson(new { count = insights.Count, insights });
            }

            case "get_listening_habits":
                return BuildToolJson(_intelligence.BuildHabitsPayload());

            case "heos_get_groups":
            {
                var own = await _receiverController.GetOwnHeosPlayerAsync(ct);
                var state = await _receiverController.RefreshHeosGroupsAsync(ct);
                return BuildToolJson(new
                {
                    ownPlayerId = own.PlayerId,
                    players = state.Players.Select(static p => new { pid = p.PlayerId, name = p.Name, model = p.Model, ip = p.IpAddress }),
                    groups = state.Groups.Select(static g => new
                    {
                        gid = g.GroupId,
                        name = g.Name,
                        leader = g.Leader?.Name,
                        members = g.Members.Select(static m => new { pid = m.PlayerId, name = m.Name, role = m.IsLeader ? "leader" : "member" }),
                        volume = g.Volume,
                        muted = g.IsMuted,
                    }),
                });
            }

            case "list_scenes_and_rules":
                return BuildToolJson(new
                {
                    scenes = _settingsStore.Current.Receiver.Scenes,
                    automations = _settingsStore.Current.AutomationGraphs,
                });

            case "avr_get_acoustics":
            {
                var acoustics = await _receiverController.GetAcousticStatusAsync(ct);
                _intelligence.UpdateCachedAcoustics(acoustics);
                return BuildToolJson(new
                {
                    multEq = acoustics.MultEq,
                    dynamicEq = acoustics.DynamicEq,
                    dynamicVolume = acoustics.DynamicVolume,
                    restorer = acoustics.Restorer,
                    dimmer = acoustics.Dimmer,
                    subwooferAdjustEnabled = acoustics.SubwooferAdjustEnabled,
                    subwooferTrimDb = acoustics.SubwooferTrimDb,
                    dialogAdjustEnabled = acoustics.DialogAdjustEnabled,
                    dialogTrimDb = acoustics.DialogTrimDb,
                    toneControlEnabled = acoustics.ToneControlEnabled,
                    bassDb = acoustics.BassDb,
                    trebleDb = acoustics.TrebleDb,
                    retrievedAt = acoustics.RetrievedAt,
                });
            }

            case "media_get_now_playing_lyrics":
            {
                var title = GetString(args, "title");
                var artist = GetString(args, "artist");
                if (string.IsNullOrWhiteSpace(title))
                {
                    var np = _stateStore.Snapshot.Status?.NowPlaying;
                    var qTrack = _queueService.Snapshot.CurrentTrack;
                    title = !string.IsNullOrWhiteSpace(np?.Song) ? np!.Song : (qTrack?.Title ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = !string.IsNullOrWhiteSpace(np?.Artist) ? np!.Artist : (qTrack?.Author ?? string.Empty);
                    }
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    return BuildToolJson(new
                    {
                        found = false,
                        title = string.Empty,
                        artist = string.Empty,
                        isSynced = false,
                        lineCount = 0,
                        lyrics = "No track is currently playing and no title was provided.",
                        lines = Array.Empty<object>(),
                    });
                }

                var doc = await _lyricsService.FetchLyricsAsync(title, string.IsNullOrWhiteSpace(artist) ? null : artist, ct);
                if (doc is null)
                {
                    return BuildToolJson(new
                    {
                        found = false,
                        title,
                        artist,
                        isSynced = false,
                        lineCount = 0,
                        lyrics = $"No lyrics found on LRCLIB for '{title}'{(string.IsNullOrWhiteSpace(artist) ? string.Empty : $" by {artist}")}.",
                        lines = Array.Empty<object>(),
                    });
                }

                var combined = string.Join("\n", doc.Lines.Select(l => l.Text));
                return BuildToolJson(new
                {
                    found = true,
                    title = doc.TrackName,
                    artist = doc.ArtistName,
                    isSynced = doc.IsSynced,
                    lineCount = doc.Lines.Count,
                    lyrics = combined,
                    lines = doc.Lines.Select(l => new
                    {
                        timestampSeconds = Math.Round(l.TimestampSeconds, 2),
                        text = l.Text,
                    }).ToArray(),
                });
            }

            case "media_get_queue":
            {
                var snap = _queueService.Snapshot;
                return BuildToolJson(new
                {
                    isActive = snap.IsActive,
                    currentIndex = snap.CurrentIndex,
                    isShuffleEnabled = snap.IsShuffleEnabled,
                    repeatMode = snap.RepeatMode.ToString(),
                    queueCount = snap.Queue.Count,
                    historyCount = snap.History.Count,
                    currentTrack = snap.CurrentTrack is { } cur
                        ? new
                        {
                            entryId = cur.EntryId,
                            videoId = cur.VideoId,
                            title = cur.Title,
                            author = cur.Author,
                            duration = cur.Duration?.ToString(@"m\:ss") ?? "LIVE",
                            url = cur.WatchUrl,
                        }
                        : null,
                    queue = snap.Queue.Select((t, idx) => new
                    {
                        index = idx,
                        isCurrent = snap.IsActive && idx == snap.CurrentIndex,
                        entryId = t.EntryId,
                        videoId = t.VideoId,
                        title = t.Title,
                        author = t.Author,
                        duration = t.Duration?.ToString(@"m\:ss") ?? "LIVE",
                        url = t.WatchUrl,
                    }).ToArray(),
                });
            }

            case "media_search_radio":
            {
                var query = GetString(args, "query");
                var tag = GetString(args, "tag");
                var hiRes = GetBool(args, "hi_res_only") ?? false;
                var limit = Math.Clamp(GetInt(args, "limit") ?? 10, 1, 30);
                var stations = await _radioBrowser.SearchStationsAsync(query, tag, null, hiRes, limit, ct);
                var mapped = stations.Select(s => new
                {
                    name = s.Name,
                    url = s.UrlResolved,
                    codec = s.Codec,
                    bitrate = s.Bitrate,
                    country = s.Country,
                    tags = s.Tags,
                }).ToArray();
                return BuildToolJson(new
                {
                    count = mapped.Length,
                    stations = mapped,
                });
            }

            case "media_search_youtube":
            {
                var query = RequireString(args, "query");
                var music = GetBool(args, "music_mode") ?? true;
                var limit = Math.Clamp(GetInt(args, "limit") ?? 6, 1, 16);
                var res = await _youtubeService.SearchOrResolveAsync(query, music ? YouTubeSearchMode.Music : YouTubeSearchMode.Videos, limit, ct);
                return BuildToolJson(new
                {
                    count = res.Tracks.Count,
                    tracks = res.Tracks.Select(t => new { id = t.VideoId, title = t.Title, author = t.Author, duration = t.Duration?.ToString(@"m\:ss") ?? "LIVE", url = t.WatchUrl }),
                });
            }
            case "get_watch_time":
            {
                var days = Math.Clamp(GetInt(args, "days") ?? 7, 1, 90);
                var top = Math.Clamp(GetInt(args, "top") ?? 10, 1, 50);
                var now = DateTimeOffset.Now;
                var since = WatchTimeReport.ReadFrom(now, days);
                var events = _telemetry.ReadEventsSince(EventTelemetryLog.StreamMedia, since, "session.ended");
                return BuildToolJson(WatchTimeReport.Build(events, now, days, top));
            }

            case "get_activity_history":
            {
                var stream = GetString(args, "stream");
                if (string.IsNullOrWhiteSpace(stream))
                {
                    stream = "all";
                }

                var hours = Math.Clamp(GetInt(args, "hours") ?? 24, 1, 24 * 90);
                var limit = Math.Clamp(GetInt(args, "limit") ?? 80, 1, 500);
                var origin = GetString(args, "origin");
                var events = _telemetry.QueryRecentEvents(stream, hours, limit, string.IsNullOrWhiteSpace(origin) ? null : origin);

                return BuildToolJson(new
                {
                    stream,
                    hours,
                    count = events.Count,
                    availableStreams = _telemetry.ListAvailableStreams(),
                    stats = _telemetry.GetStats(),
                    events,
                });
            }

            case "tv_computer":
                return await ExecuteTvComputerToolAsync(args, cfg, ct);
        }

        // Auto-bridged plugin tools. Read-only actions run in every tier (also Observer); others need
        // SafeControl; power_off and actions a plugin declares destructive need FullOrchestration.
        if (toolName.StartsWith("plugin_", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var plugin in _pluginHost.GetPlugins().Where(p => p.IsEnabled))
            {
                foreach (var action in plugin.Actions)
                {
                    if (!BuildPluginToolName(plugin.Id, action.ActionId).Equals(toolName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isReadOnly = EnsurePluginActionAllowed(action, cfg, toolName);
                    if (!isReadOnly)
                    {

                        _telemetry.MarkOrigin("avr", "mcp_agent", "mcp.tool", new Dictionary<string, object?> { ["tool"] = toolName });
                        _telemetry.MarkOrigin("tv", "mcp_agent");
                        _telemetry.MarkOrigin("lighting", "mcp_agent");
                    }

                    var dict = ExtractStringParameters(args);
                    var result = await _pluginHost.ExecuteActionAsync(plugin.Id, action.ActionId, dict.Count > 0 ? dict : null, ct);
                    if (!isReadOnly)
                    {
                        _intelligence.RecordPluginActionUsed(plugin.Id, action.ActionId);
                    }

                    return McpPluginToolBridge.BuildResult(result);
                }
            }
        }

        // Everything below mutates live devices or settings -> block in Observer mode!
        if (cfg.PermissionTier == McpPermissionTier.Observer)
        {
            throw new UnauthorizedAccessException($"MCP Permission Tier is 'Observer' (read-only). Change to SafeControl or FullOrchestration in Denon Desktop Settings to run '{toolName}'.");
        }

        _telemetry.MarkOrigin("avr", "mcp_agent", "mcp.tool", new Dictionary<string, object?> { ["tool"] = toolName });
        _telemetry.MarkOrigin("tv", "mcp_agent");
        _telemetry.MarkOrigin("lighting", "mcp_agent");

        if (await TryHandleAppHelperAsync(toolName, args, cfg, ct) is { } helper)
        {
            return helper;
        }

        switch (toolName)
        {
            case "restore_previous_state":
            {
                var (ok, msg) = await _intelligence.RestoreLastSnapshotAsync(maxSafeVolume: cfg.MaxSafeVolume, ct);
                return ok ? BuildToolText(msg) : BuildToolError(msg);
            }

            case "fix_room_issue":
            {
                var key = RequireString(args, "fix_action_key");
                var (ok, msg) = await _intelligence.ExecuteInsightFixAsync(key, ct, maxSafeVolume: cfg.MaxSafeVolume);
                return ok ? BuildToolText(msg) : BuildToolError(msg);
            }

            case "apply_atmosphere":
                return await ExecuteApplyAtmosphereAsync(args, cfg, ct);

            case "avr_set_power":
            {
                var powerOn = GetBool(args, "power_on") ?? throw new McpInvalidParamsException("Missing boolean 'power_on'.");
                if (!powerOn && cfg.PermissionTier < McpPermissionTier.FullOrchestration)
                {
                    throw new UnauthorizedAccessException("Powering off the AVR requires 'FullOrchestration' permission tier.");
                }

                _intelligence.CaptureSnapshot($"MCP avr_set_power({powerOn})");
                await _receiverController.SetPowerAsync(powerOn, ct);
                return BuildToolText(powerOn ? "Powered Denon AVR ON." : "Put Denon AVR into Standby.");
            }

            case "avr_set_volume":
            {
                var requested = GetInt(args, "volume") ?? throw new McpInvalidParamsException("Missing integer 'volume' (0-98).");
                var clamped = ApplyVolumeCeiling(requested, cfg.MaxSafeVolume, out var wasCapped);
                _intelligence.CaptureSnapshot($"MCP avr_set_volume({clamped})");
                await _receiverController.SetVolumeAsync(clamped, ct);
                var note = wasCapped ? $" (capped from {requested} by MaxSafeVolume ceiling {cfg.MaxSafeVolume})" : string.Empty;
                return BuildToolText($"Set Denon AVR volume to {clamped} ({RoomIntelligenceService.FormatDb(clamped)}){note}.");
            }

            case "avr_set_mute":
            {
                var muted = GetBool(args, "muted") ?? throw new McpInvalidParamsException("Missing boolean 'muted'.");
                await _receiverController.SetMuteAsync(muted, ct);
                return BuildToolText(muted ? "Muted Denon AVR." : "Unmuted Denon AVR.");
            }

            case "avr_set_input":
            {
                var input = RequireString(args, "input_source").ToUpperInvariant();
                _intelligence.CaptureSnapshot($"MCP avr_set_input({input})");
                await _receiverController.SetInputSourceAsync(input, ct);
                return BuildToolText($"Switched Denon AVR input to {input}.");
            }

            case "avr_set_sound_mode":
            {
                var mode = RequireString(args, "sound_mode").ToUpperInvariant();
                _intelligence.CaptureSnapshot($"MCP avr_set_sound_mode({mode})");
                await _receiverController.SetSoundModeAsync(mode, ct);
                return BuildToolText($"Switched Denon AVR sound mode to {mode}.");
            }

            case "avr_playback_control":
            {
                var action = RequireString(args, "action").ToLowerInvariant();
                switch (action)
                {
                    case "play": await _receiverController.SetPlayStateAsync("play", ct); break;
                    case "pause": await _receiverController.SetPlayStateAsync("pause", ct); break;
                    case "stop": await _receiverController.SetPlayStateAsync("stop", ct); break;
                    case "next": await _receiverController.PlayNextAsync(ct); break;
                    case "previous": await _receiverController.PlayPreviousAsync(ct); break;
                    default: throw new McpInvalidParamsException($"Unknown playback action '{action}'. Use play, pause, stop, next, or previous.");
                }

                return BuildToolText($"Sent HEOS playback command: {action}.");
            }

            case "avr_set_zone2":
                return await ExecuteSetZone2Async(args, cfg, ct);

            case "heos_set_group":
                EnsureFullOrchestration(cfg, toolName);
                return await ExecuteSetGroupAsync(args, ct);

            case "heos_set_group_volume":
                return await ExecuteSetGroupVolumeAsync(args, cfg, ct);

            case "avr_set_eco_mode":
            {
                var mode = RequireString(args, "mode").ToUpperInvariant();
                await _receiverController.SetEcoModeAsync(mode, ct);
                return BuildToolText($"Set Denon ECO mode to {mode}.");
            }

            case "avr_set_sleep_timer":
            {
                var mins = GetInt(args, "minutes") ?? 0;
                await _receiverController.SetSleepTimerAsync(mins <= 0 ? null : Math.Clamp(mins, 1, 120), ct);
                return BuildToolText(mins <= 0 ? "Cancelled sleep timer." : $"Set sleep timer to {mins} minutes.");
            }

            case "avr_tune_acoustics":
                return await ExecuteTuneAcousticsAsync(args, ct);

            case "media_play_station_url":
            {
                var url = RequireString(args, "url");
                var title = GetString(args, "title", "Internet Radio");
                var lowVol = GetBool(args, "low_volume") ?? false;
                await _receiverController.PlayCustomUrlAsync(url, lowVol, unmute: true, cancellationToken: ct, title: title);
                return BuildToolText($"Playing stream '{title}' on Denon AVR.");
            }

            case "media_play_youtube":
            {
                var query = RequireString(args, "query_or_url");
                var queueOnly = GetBool(args, "queue_only") ?? false;
                var search = await _youtubeService.SearchOrResolveAsync(query, YouTubeSearchMode.Music, 5, ct);
                var track = search.Tracks.FirstOrDefault() ?? throw new InvalidOperationException($"No playable YouTube track found for '{query}'.");

                if (queueOnly)
                {
                    _queueService.Enqueue(track);
                    return BuildToolText($"Queued '{track.Title}' by {track.Author} into Up-Next queue.");
                }

                await _relayServer.EnsureStartedAsync(ct);
                await _queueService.PlayNowAsync(track, useLowVolume: false, unmute: true, ct);
                return BuildToolText($"Now playing YouTube track '{track.Title}' by {track.Author} on Denon HEOS.");
            }

            case "media_manage_queue":
            {
                var action = RequireString(args, "action").ToLowerInvariant();
                switch (action)
                {
                    case "clear":
                        _queueService.ClearQueue();
                        return BuildToolText("Cleared the YouTube Up-Next queue.");
                    case "toggle_shuffle":
                        _queueService.ToggleShuffle();
                        return BuildToolText($"Queue shuffle is now {(_queueService.Snapshot.IsShuffleEnabled ? "ON" : "OFF")}.");
                    case "cycle_repeat":
                        _queueService.CycleRepeatMode();
                        return BuildToolText($"Queue repeat mode is now {_queueService.Snapshot.RepeatMode}.");
                    case "remove":
                    {
                        var entryId = RequireString(args, "entry_id");
                        _queueService.RemoveFromQueue(entryId);
                        return BuildToolText($"Removed queue entry '{entryId}'.");
                    }
                    case "play_index":
                    {
                        var idx = GetInt(args, "index") ?? throw new McpInvalidParamsException("Missing integer 'index' (0-based).");
                        var snap = _queueService.Snapshot;
                        if (idx < 0 || idx >= snap.Queue.Count)
                        {
                            throw new McpInvalidParamsException($"Queue index {idx} is out of range (queue count: {snap.Queue.Count}).");
                        }

                        var target = snap.Queue[idx];
                        await _relayServer.EnsureStartedAsync(ct);
                        await _queueService.PlayQueueEntryAsync(target.EntryId, useLowVolume: false, unmute: true, ct);
                        return BuildToolText($"Now playing queue item #{idx}: '{target.Title}' by {target.Author}.");
                    }
                    case "next":
                    {
                        var moved = await _queueService.PlayNextAsync(ct);
                        return BuildToolText(moved ? "Skipped to next track in Up-Next queue." : "Already at end of Up-Next queue.");
                    }
                    case "previous":
                    {
                        var moved = await _queueService.PlayPreviousAsync(ct);
                        return BuildToolText(moved ? "Returned to previous track in Up-Next queue." : "Up-Next queue is empty.");
                    }
                    default:
                        throw new McpInvalidParamsException($"Unknown queue action '{action}'. Use clear, toggle_shuffle, cycle_repeat, remove, play_index, next, or previous.");
                }
            }

            case "hue_set_color":
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (GetString(args, "hex") is { Length: > 0 } hex) p["hex"] = hex;
                if (GetInt(args, "kelvin") is { } kelvin) p["kelvin"] = kelvin.ToString();
                if (GetInt(args, "brightness") is { } bri) p["brightness"] = bri.ToString();
                if (GetString(args, "room") is { Length: > 0 } room) p["room"] = room;

                var res = await _pluginHost.ExecuteActionAsync("philips-hue", "hue.set_color", p, ct);
                _intelligence.RecordPluginActionUsed("philips-hue", "hue.set_color");
                return res.Success ? BuildToolText(res.Message) : BuildToolError(res.Message);
            }

            case "activate_smart_scene":
            {
                var target = RequireString(args, "scene_id_or_name");
                var scene = _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s =>
                    s.Id.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpInvalidParamsException($"Smart Scene '{target}' not found.");

                if (scene.HeosGroupMembers.Count > 0 || scene.HeosUngroup)
                {
                    // The same rule as heos_set_group: grouping changes what other rooms play.
                    EnsureFullOrchestration(cfg, $"{toolName} (scene '{scene.Name}' groups HEOS players)");
                }

                var safeScene = scene.Clone();
                if (safeScene.TargetVolume.HasValue)
                {
                    safeScene.TargetVolume = ApplyVolumeCeiling(safeScene.TargetVolume.Value, cfg.MaxSafeVolume, out _);
                }

                if (safeScene.Zone2Volume.HasValue)
                {
                    safeScene.Zone2Volume = ApplyVolumeCeiling(safeScene.Zone2Volume.Value, cfg.MaxSafeVolume, out _);
                }

                _intelligence.CaptureSnapshot($"MCP Smart Scene: {safeScene.Name}");
                await _receiverController.ApplySceneAsync(safeScene, ct);
                _intelligence.RecordSceneUsed(safeScene.Name);
                return BuildToolText($"Activated Smart Scene '{safeScene.Name}' (with {safeScene.PluginActions.Count} chained plugin action(s)).");
            }

            case "create_or_update_scene":
                EnsureFullOrchestration(cfg, toolName);
                return await ExecuteCreateOrUpdateSceneAsync(args, cfg, ct);

            case "create_or_update_automation":
                EnsureFullOrchestration(cfg, toolName);
                return await ExecuteCreateOrUpdateAutomationAsync(args, ct);
        }

        throw new McpInvalidParamsException($"Unknown MCP tool '{toolName}'.");
    }

    private async Task<CallToolResult> ExecuteTvComputerToolAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        var action = GetString(args, "action", "screenshot").Trim().ToLowerInvariant();
        var isReadOnlyAction = action is "screenshot" or "get_state" or "get_settings";
        if (!isReadOnlyAction && cfg.PermissionTier == McpPermissionTier.Observer)
        {
            throw new UnauthorizedAccessException($"MCP Permission Tier is 'Observer' (read-only). Change to SafeControl or FullOrchestration in Denon Desktop Settings to run tv_computer action '{action}'.");
        }

        string payloadJson;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (cfg.PermissionTier == McpPermissionTier.Observer)
            {
                // Keep Observer mode strictly read-only for every read action: no cursor wiggle on the TV.
                var payload = System.Text.Json.Nodes.JsonNode.Parse(args.GetRawText())!.AsObject();
                payload["action"] = action;
                payload["show_cursor"] = false;
                payload["screenshot_after"] = false;
                payloadJson = payload.ToJsonString();
            }
            else
            {
                payloadJson = args.GetRawText();
            }
        }
        else
        {
            payloadJson = "{\"action\":\"screenshot\"}";
        }

        if (!isReadOnlyAction)
        {
            _telemetry.MarkOrigin("tv", "mcp_agent", "mcp.tv_computer", new Dictionary<string, object?> { ["action"] = action });
        }

        var res = await _pluginHost.ExecuteActionAsync(
            "smart-tv",
            "tv.computer",
            new Dictionary<string, string> { ["payloadJson"] = payloadJson },
            ct);

        if (!res.Success)
        {
            return BuildToolError(res.Message);
        }

        if (!isReadOnlyAction)
        {
            _intelligence.RecordPluginActionUsed("smart-tv", $"tv.computer:{action}");
        }

        return McpPluginToolBridge.BuildResult(res);
    }

    private static object BuildTvComputerSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "screenshot", "get_state", "get_settings", "set_settings", "open_url", "launch_app", "close_app",
                    "move", "move_to", "click", "scroll", "button", "type", "backspace", "wait", "batch",
                },
                description = "Operation to perform on the LG webOS TV. Use 'screenshot' to see the screen, 'get_settings'/'set_settings' to read or update picture/sound system settings directly over SSAP, 'open_url' for the Web Browser, 'move_to'/'click'/'move'/'scroll' for the Magic Remote pointer, 'button' for D-pad/Home/Back, 'type'/'backspace' for keyboard input, or 'batch' to chain multiple steps in one round-trip.",
            },
            ["category"] = new { type = "string", description = "System settings category for 'get_settings' / 'set_settings': 'picture', 'sound', 'option', or 'all' (for get_settings)." },
            ["keys"] = new
            {
                type = "array",
                description = "Optional list of specific setting keys to query for action='get_settings' (defaults to key picture/sound properties when omitted).",
                items = new { type = "string" },
            },
            ["settings"] = new { type = "object", description = "Key-value map of settings to apply for action='set_settings' (e.g. {\"backlight\": 70, \"pictureMode\": \"expert2\"})." },
            ["url"] = new { type = "string", description = "URL to open in the LG webOS Web Browser (for action='open_url')." },
            ["app"] = new { type = "string", description = "App name, alias, or webOS appId (e.g. 'settings', 'youtube', 'browser', 'plex', 'netflix', 'livetv', 'com.webos.app.hdmi1') for action='launch_app'. Use 'settings' to open LG webOS Settings directly." },
            ["content_id"] = new { type = "string", description = "Optional deep-link content ID (e.g. YouTube URL or 11-char video ID) for action='launch_app'." },
            ["x"] = new { type = "integer", description = "Normalized horizontal screen coordinate 0..1000 (0=left, 1000=right) for 'move_to' or 'click'." },
            ["y"] = new { type = "integer", description = "Normalized vertical screen coordinate 0..1000 (0=top, 1000=bottom) for 'move_to' or 'click'." },
            ["dx"] = new { type = "integer", description = "Relative horizontal pixel delta (for 'move') or horizontal scroll ticks (for 'scroll')." },
            ["dy"] = new { type = "integer", description = "Relative vertical pixel delta (for 'move') or vertical scroll ticks (for 'scroll', negative scrolls down)." },
            ["drag"] = new { type = "boolean", description = "Hold click down while moving (for action='move')." },
            ["recalibrate"] = new { type = "boolean", description = "Force re-homing the Magic Remote cursor to top-left (0,0) before moving to (x,y)." },
            ["button"] = new { type = "string", description = "Magic Remote button for action='button': HOME, BACK, ENTER, UP, DOWN, LEFT, RIGHT, EXIT, MENU, QMENU, INFO, GUIDE, PLAY, PAUSE, RED, GREEN, YELLOW, BLUE." },
            ["repeat"] = new { type = "integer", description = "Number of times to repeat a 'button' or 'scroll' action (1-15)." },
            ["text"] = new { type = "string", description = "Text to insert into an active TV search/keyboard field (for action='type')." },
            ["press_enter"] = new { type = "boolean", description = "Submit/press Enter after typing (default true, for action='type')." },
            ["replace"] = new { type = "boolean", description = "Replace existing field text instead of appending (default false, for action='type')." },
            ["count"] = new { type = "integer", description = "Characters to delete (1-50, for action='backspace')." },
            ["ms"] = new { type = "integer", description = "Milliseconds to wait (50-10000, for action='wait')." },
            ["steps"] = new
            {
                type = "array",
                description = "Ordered list of sub-action objects (up to 25) to execute sequentially when action='batch'. Each object uses the same fields (action, url, x, y, dx, dy, button, text, ms, etc.).",
                items = new { type = "object" },
            },
            ["screenshot_after"] = new { type = "boolean", description = "If true, automatically waits settle_ms and captures a fresh screenshot after executing the action or batch." },
            ["settle_ms"] = new { type = "integer", description = "Milliseconds to wait before capturing screenshot_after (default 350, max 5000)." },
            ["show_cursor"] = new { type = "boolean", description = "If true (default), gently wakes the pink LG Magic Remote cursor before snapping a screenshot so its position is visible." },
            ["width"] = new { type = "integer", description = "Screenshot width in pixels (320-1920, default 960)." },
            ["height"] = new { type = "integer", description = "Screenshot height in pixels (180-1080, default 540)." },
        },
        required = new[] { "action" },
    };

    /// <summary>
    /// The tier rule for a plugin action, used by the plugin_* tools and by apply_atmosphere: read-only actions run
    /// in every tier, others need SafeControl, and power_off or an action the plugin declares destructive needs
    /// FullOrchestration. Returns true for a read-only action.
    /// </summary>
    private static bool EnsurePluginActionAllowed(Denon.App.PluginSdk.PluginActionDescriptor action, McpServerSettings cfg, string toolName)
    {
        var (isReadOnly, _, _) = McpPluginToolBridge.ResolveHints(action);
        if (isReadOnly)
        {
            return true;
        }

        if (cfg.PermissionTier == McpPermissionTier.Observer)
        {
            throw new UnauthorizedAccessException($"MCP Permission Tier is 'Observer' (read-only). Change to SafeControl or FullOrchestration in Denon Desktop Settings to run '{toolName}'.");
        }

        var needsFullOrchestration = action.IsDestructive == true ||
                                     action.ActionId.Contains("power_off", StringComparison.OrdinalIgnoreCase);
        if (needsFullOrchestration && cfg.PermissionTier < McpPermissionTier.FullOrchestration)
        {
            throw new UnauthorizedAccessException($"Running '{action.ActionId}' requires 'FullOrchestration' permission tier.");
        }

        return false;
    }

    /// <summary>
    /// apply_atmosphere may name any Hue or TV action: it must be one the enabled plugin declares, and it gets the
    /// same tier check as the plugin tools (so tv.power_off still needs FullOrchestration).
    /// </summary>
    private void EnsureAtmospherePluginAction(string pluginId, string actionId, McpServerSettings cfg)
    {
        if (McpToolAccess.IsPluginDenied(cfg, pluginId))
        {
            throw new UnauthorizedAccessException(McpToolAccess.PluginRefusalMessage("apply_atmosphere", pluginId));
        }

        var plugin = _pluginHost.GetPlugins().FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase) && p.IsEnabled)
                     ?? throw new McpInvalidParamsException($"The plugin '{pluginId}' is not enabled.");
        var action = plugin.Actions.FirstOrDefault(a => string.Equals(a.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new McpInvalidParamsException($"'{actionId}' is not an action of the plugin '{pluginId}'.");
        EnsurePluginActionAllowed(action, cfg, "apply_atmosphere");
    }

    private async Task<CallToolResult> ExecuteApplyAtmosphereAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        // Check the plugin actions before any step runs, so a refused action changes nothing.
        var hueAction = GetString(args, "hue_action");
        var tvAction = GetString(args, "tv_action");
        if (!string.IsNullOrWhiteSpace(hueAction))
        {
            EnsureAtmospherePluginAction("philips-hue", hueAction, cfg);
        }

        if (!string.IsNullOrWhiteSpace(tvAction))
        {
            EnsureAtmospherePluginAction("smart-tv", tvAction, cfg);
        }

        var reason = GetString(args, "reason", "AI Atmosphere");
        var captured = _intelligence.CaptureSnapshot($"MCP Atmosphere: {reason}");

        var stepsTaken = new List<string>();
        await _receiverController.SetPowerAsync(true, ct);

        // Apply volume ceiling BEFORE unmuting or switching to a live input so quiet atmospheres never blast!
        if (GetInt(args, "volume") is { } vol)
        {
            var clamped = ApplyVolumeCeiling(vol, cfg.MaxSafeVolume, out var capped);
            await _receiverController.SetVolumeAsync(clamped, ct);
            stepsTaken.Add($"Volume → {clamped} ({RoomIntelligenceService.FormatDb(clamped)}){(capped ? " [capped]" : string.Empty)}");
        }

        if (GetString(args, "input_source") is { Length: > 0 } input)
        {
            await _receiverController.SetInputSourceAsync(input.ToUpperInvariant(), ct);
            stepsTaken.Add($"AVR Input → {input.ToUpperInvariant()}");
        }

        if (GetString(args, "sound_mode") is { Length: > 0 } mode)
        {
            await _receiverController.SetSoundModeAsync(mode.ToUpperInvariant(), ct);
            stepsTaken.Add($"Sound Mode → {mode.ToUpperInvariant()}");
        }

        await _receiverController.SetMuteAsync(false, ct);

        if (GetString(args, "dynamic_volume") is { Length: > 0 } dv)
        {
            await _receiverController.SetDynamicVolumeAsync(dv.ToUpperInvariant(), ct);
            stepsTaken.Add($"Dynamic Volume → {dv.ToUpperInvariant()}");
        }

        if (hueAction is { Length: > 0 } hueAct)
        {
            var res = await _pluginHost.ExecuteActionAsync("philips-hue", hueAct, null, ct);
            stepsTaken.Add($"Philips Hue ({hueAct}): {res.Message}");
            _intelligence.RecordPluginActionUsed("philips-hue", hueAct);
        }

        if (tvAction is { Length: > 0 } tvAct)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", tvAct, null, ct);
            stepsTaken.Add($"LG TV ({tvAct}): {res.Message}");
            _intelligence.RecordPluginActionUsed("smart-tv", tvAct);
        }

        if (GetBool(args, "oled_screen_off") == true)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.screen_off", null, ct);
            stepsTaken.Add($"TV Screen Off: {res.Message}");
        }

        if (GetString(args, "tv_toast") is { Length: > 0 } toast)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.toast", new Dictionary<string, string> { ["message"] = toast }, ct);
            stepsTaken.Add($"TV Toast: {res.Message}");
        }

        return BuildToolJson(new
        {
            applied = reason,
            undoAvailable = captured is not null,
            undoScope = captured is not null ? "Denon AVR power, volume, mute, input, sound mode, Zone 2" : "None (AVR state was unconfirmed)",
            steps = stepsTaken,
        });
    }

    private async Task<CallToolResult> ExecuteTuneAcousticsAsync(JsonElement args, CancellationToken ct)
    {
        var applied = new List<string>();
        if (GetString(args, "multeq") is { Length: > 0 } multeq)
        {
            await _receiverController.SetMultEqAsync(multeq.ToUpperInvariant(), ct);
            applied.Add($"MultEQ={multeq.ToUpperInvariant()}");
        }

        if (GetBool(args, "dynamic_eq") is { } deq)
        {
            await _receiverController.SetDynamicEqAsync(deq, ct);
            applied.Add($"DynamicEQ={(deq ? "ON" : "OFF")}");
        }

        if (GetString(args, "dynamic_volume") is { Length: > 0 } dvol)
        {
            await _receiverController.SetDynamicVolumeAsync(dvol.ToUpperInvariant(), ct);
            applied.Add($"DynamicVol={dvol.ToUpperInvariant()}");
        }

        if (GetString(args, "restorer") is { Length: > 0 } rest)
        {
            await _receiverController.SetRestorerAsync(rest.ToUpperInvariant(), ct);
            applied.Add($"Restorer={rest.ToUpperInvariant()}");
        }

        if (GetString(args, "dimmer") is { Length: > 0 } dim)
        {
            await _receiverController.SetDimmerAsync(dim.ToUpperInvariant(), ct);
            applied.Add($"Dimmer={dim.ToUpperInvariant()}");
        }

        if (GetInt(args, "subwoofer_trim_db") is { } subDb)
        {
            await _receiverController.SetSubwooferLevelAsync(true, Math.Clamp(subDb, -12, 12), ct);
            applied.Add($"SubTrim={subDb:+#;-#;0}dB");
        }

        if (GetInt(args, "dialog_trim_db") is { } dlgDb)
        {
            await _receiverController.SetDialogLevelAsync(true, Math.Clamp(dlgDb, -12, 12), ct);
            applied.Add($"DialogTrim={dlgDb:+#;-#;0}dB");
        }

        var bass = GetInt(args, "bass_db");
        var treble = GetInt(args, "treble_db");
        if (bass.HasValue || treble.HasValue)
        {
            await _receiverController.SetToneControlAsync(true, bass, treble, ct);
            applied.Add($"Tone(Bass={bass?.ToString() ?? "keep"},Treble={treble?.ToString() ?? "keep"})");
        }

        return applied.Count > 0
            ? BuildToolText($"Tuned Denon AVR acoustics: {string.Join(", ", applied)}.")
            : BuildToolText("No acoustic parameters were specified.");
    }

    private async Task<CallToolResult> ExecuteCreateOrUpdateSceneAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        var id = Slugify(RequireString(args, "id"));
        var name = RequireString(args, "name");
        var description = GetString(args, "description", $"Created via MCP ({DateTime.Now:HH:mm})");
        var input = GetString(args, "input_source");
        var mode = GetString(args, "sound_mode");
        var targetVol = GetInt(args, "target_volume");
        if (targetVol.HasValue)
        {
            targetVol = ApplyVolumeCeiling(targetVol.Value, cfg.MaxSafeVolume, out _);
        }

        var dvol = GetString(args, "dynamic_volume");
        var zone2On = GetBool(args, "zone2_on");
        var zone2Source = GetString(args, "zone2_source");
        if (!string.IsNullOrWhiteSpace(zone2Source) && !DenonCatalog.IsZone2SourceCode(zone2Source))
        {
            throw new McpInvalidParamsException($"'{zone2Source}' is not a Zone 2 source.");
        }

        var zone2Volume = GetInt(args, "zone2_volume");
        if (zone2Volume.HasValue)
        {
            zone2Volume = ApplyVolumeCeiling(zone2Volume.Value, cfg.MaxSafeVolume, out _);
        }

        var zone2Muted = GetBool(args, "zone2_muted");
        var groupWith = GetString(args, "heos_group_with").Trim();
        var pluginId = GetString(args, "plugin_id");
        var pluginActionId = GetString(args, "plugin_action_id");

        // Inside the store's lock: a clone saved whole would undo a settings change made at the same moment.
        ScenePreset? saved = null;
        await _settingsStore.UpdateAsync(settings =>
        {
            var existing = settings.Receiver.Scenes.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var scene = existing ?? new ScenePreset { Id = id };

            scene.Name = name;
            scene.Description = description;
            if (!string.IsNullOrWhiteSpace(input)) scene.InputSource = input.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(mode)) scene.SoundMode = mode.ToUpperInvariant();
            if (targetVol.HasValue) scene.TargetVolume = targetVol.Value;
            if (!string.IsNullOrWhiteSpace(dvol)) scene.DynamicVolume = dvol.ToUpperInvariant();
            if (zone2On.HasValue) scene.Zone2On = zone2On.Value;
            if (!string.IsNullOrWhiteSpace(zone2Source)) scene.Zone2Source = zone2Source.Trim().ToUpperInvariant();
            if (zone2Volume.HasValue) scene.Zone2Volume = zone2Volume.Value;
            if (zone2Muted.HasValue) scene.Zone2Muted = zone2Muted.Value;
            if (groupWith.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                scene.HeosGroupMembers = [];
                scene.HeosUngroup = true;
            }
            else if (groupWith.Length > 0)
            {
                scene.HeosGroupMembers = [.. groupWith.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                scene.HeosUngroup = false;
            }

            if (!string.IsNullOrWhiteSpace(pluginId) && !string.IsNullOrWhiteSpace(pluginActionId))
            {
                scene.PluginActions =
                [
                    new ScenePluginActionStep
                    {
                        PluginId = pluginId,
                        ActionId = pluginActionId,
                        DisplayName = $"{pluginId}: {pluginActionId}",
                        DelayMs = 200,
                    },
                ];
            }

            if (existing is null)
            {
                settings.Receiver.Scenes.Add(scene);
            }

            saved = scene;
        }, ct);
        return BuildToolJson(new { saved = true, scene = saved });
    }

    private async Task<CallToolResult> ExecuteCreateOrUpdateAutomationAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("automation", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return BuildToolError("Pass 'automation' as an object with name, nodes and edges.");
        }

        AutomationGraph? graph;
        try
        {
            graph = element.Deserialize<AutomationGraph>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            return BuildToolError($"The automation is not valid JSON for a graph: {ex.Message}");
        }

        if (graph is null)
        {
            return BuildToolError("The automation is empty.");
        }

        if (string.IsNullOrWhiteSpace(graph.Id) || !element.TryGetProperty("id", out _))
        {
            graph.Id = AutomationGraph.NewId("mcp");
        }

        // An agent-made automation stays off until the user turns it on, unless the call says enabled: true.
        graph.Enabled = element.TryGetProperty("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;

        // Validate first: it reports duplicate or missing node ids, which the layout cannot handle.
        var problems = AutomationGraphValidator.Validate(graph);
        if (problems.Count > 0)
        {
            return BuildToolError("The automation was not saved:\n" + string.Join("\n", problems.Select(p => p.NodeId is null ? $"- {p.Message}" : $"- node {p.NodeId}: {p.Message}")));
        }

        LayOutIfUnplaced(graph);

        // Inside the store's lock, so an editor save at the same moment is not undone.
        var replaced = false;
        await _settingsStore.UpdateAsync(s => replaced = AutomationGraph.Upsert(s.AutomationGraphs, graph), ct);
        return BuildToolJson(new { saved = true, replaced, automation = graph });
    }

    /// <summary>Agents rarely send positions: put unplaced nodes in columns by their distance from a trigger.</summary>
    private static void LayOutIfUnplaced(AutomationGraph graph)
    {
        // Skip null nodes, nodes without an id and repeated ids: the validator reports those.
        var nodes = (graph.Nodes ?? []).Where(static n => n is not null && !string.IsNullOrEmpty(n.Id)).ToList();
        if (nodes.Count == 0 || nodes.Any(static n => n.X != 0 || n.Y != 0))
        {
            return;
        }

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            depth.TryAdd(node.Id, AutomationNodeTypes.IsTrigger(node.Type ?? string.Empty) ? 0 : -1);
        }

        var edges = (graph.Edges ?? []).Where(static e => e is not null && e.From is not null && e.To is not null).ToList();
        for (var pass = 0; pass < nodes.Count; pass++)
        {
            foreach (var edge in edges)
            {
                if (depth.TryGetValue(edge.From, out var d) && d >= 0 && depth.ContainsKey(edge.To) && depth[edge.To] < d + 1)
                {
                    depth[edge.To] = d + 1;
                }
            }
        }

        foreach (var column in nodes.GroupBy(n => Math.Max(0, depth[n.Id])))
        {
            var row = 0;
            foreach (var node in column)
            {
                node.X = 60 + column.Key * 260;
                node.Y = 80 + row++ * 150;
            }
        }
    }

    private static void EnsureFullOrchestration(McpServerSettings cfg, string toolName)
    {
        if (cfg.PermissionTier < McpPermissionTier.FullOrchestration)
        {
            throw new UnauthorizedAccessException($"Tool '{toolName}' requires 'FullOrchestration' permission tier in Denon Desktop Settings.");
        }
    }

    /// <summary>avr_set_zone2: power on, source, volume (capped), mute, and power off last.</summary>
    private async Task<CallToolResult> ExecuteSetZone2Async(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        var on = GetBool(args, "power_on");
        var source = GetString(args, "source");
        var volume = GetInt(args, "volume");
        var muted = GetBool(args, "muted");
        if (on is null && string.IsNullOrWhiteSpace(source) && volume is null && muted is null)
        {
            throw new McpInvalidParamsException("Give at least one of power_on, source, volume or muted.");
        }

        if (!string.IsNullOrWhiteSpace(source) && !DenonCatalog.IsZone2SourceCode(source))
        {
            throw new McpInvalidParamsException(
                $"'{source}' is not a Zone 2 source. Use one of: {string.Join(", ", DenonCatalog.Zone2Sources.Select(static s => s.Code))}.");
        }

        int? level = null;
        var applied = new List<string>();
        if (on == true)
        {
            applied.Add("on");
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            applied.Add($"source {DenonCatalog.GetFriendlyZone2SourceName(source)}");
        }

        if (volume is int requested)
        {
            level = ApplyVolumeCeiling(requested, cfg.MaxSafeVolume, out var capped);
            applied.Add(capped ? $"volume {level} (capped from {requested} by MaxSafeVolume {cfg.MaxSafeVolume})" : $"volume {level}");
        }

        if (muted is bool mute)
        {
            applied.Add(mute ? "muted" : "unmuted");
        }

        if (on == false)
        {
            applied.Add("off");
        }

        // One sequence: muted while it turns on, the volume before the sound, and off last.
        await _receiverController.ApplyZone2Async(on, source, level, muted, ct);
        return BuildToolText($"Zone 2: {string.Join(", ", applied)}.");
    }

    /// <summary>heos_set_group: the named players, leader first; one player alone ends its group.</summary>
    private async Task<CallToolResult> ExecuteSetGroupAsync(JsonElement args, CancellationToken ct)
    {
        var names = RequireString(args, "players").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IReadOnlyList<int> ids;
        try
        {
            ids = await _receiverController.ResolveHeosPlayerIdsAsync(names, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpInvalidParamsException(ex.Message);
        }

        if (ids.Count == 0)
        {
            throw new McpInvalidParamsException("Name at least one HEOS player.");
        }

        var state = await _receiverController.SetHeosGroupAsync(ids, ct);
        return BuildToolText(ids.Count == 1
            ? $"Ended the HEOS group of {names[0]}. Groups now: {DescribeGroups(state)}."
            : $"Grouped {string.Join(", ", names)} ({names[0]} leads). Groups now: {DescribeGroups(state)}.");
    }

    /// <summary>heos_set_group_volume: the named group, or the group this receiver is in.</summary>
    private async Task<CallToolResult> ExecuteSetGroupVolumeAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        var volume = GetInt(args, "volume");
        var muted = GetBool(args, "muted");
        if (volume is null && muted is null)
        {
            throw new McpInvalidParamsException("Give volume, muted or both.");
        }

        var state = await _receiverController.RefreshHeosGroupsAsync(ct);
        HeosGroup group;
        if (GetInt(args, "group_id") is int gid)
        {
            group = state.Groups.FirstOrDefault(g => g.GroupId == gid)
                ?? throw new McpInvalidParamsException($"No HEOS group {gid}. Groups: {DescribeGroups(state)}.");
        }
        else
        {
            var own = await _receiverController.GetOwnHeosPlayerAsync(ct);
            group = state.GroupOf(own.PlayerId)
                ?? throw new McpInvalidParamsException($"{own.Name} is not in a HEOS group. Groups: {DescribeGroups(state)}.");
        }

        var applied = new List<string>();
        if (volume is int requested)
        {
            var level = ApplyVolumeCeiling(requested, cfg.MaxSafeVolume, out var capped);
            await _receiverController.SetHeosGroupVolumeAsync(group.GroupId, level, ct);
            applied.Add(capped ? $"volume {level} (capped from {requested} by MaxSafeVolume {cfg.MaxSafeVolume})" : $"volume {level}");
        }

        if (muted is bool mute)
        {
            await _receiverController.SetHeosGroupMuteAsync(group.GroupId, mute, ct);
            applied.Add(mute ? "muted" : "unmuted");
        }

        return BuildToolText($"HEOS group '{group.Name}': {string.Join(", ", applied)}.");
    }

    private static string DescribeGroups(HeosGroupsState state) =>
        state.Groups.Count == 0
            ? "none"
            : string.Join("; ", state.Groups.Select(static g => $"{g.Name} (gid {g.GroupId}: {string.Join(", ", g.Members.Select(static m => m.Name))})"));

    private static int ApplyVolumeCeiling(int requested, int maxSafeVolume, out bool wasCapped)
    {
        var clampedRequested = Math.Clamp(requested, 0, 98);
        var ceiling = Math.Clamp(maxSafeVolume, 15, 98);
        wasCapped = clampedRequested > ceiling;
        return Math.Min(clampedRequested, ceiling);
    }

    private static string BuildPluginToolName(string pluginId, string actionId) =>
        $"plugin_{Slugify(pluginId)}_{Slugify(actionId)}";

    private static string Slugify(string raw) =>
        NonAlnumRegex().Replace((raw ?? string.Empty).Trim().ToLowerInvariant(), "_").Trim('_');

    private static Tool Tool(
        string name,
        string description,
        object inputSchema,
        string? title = null,
        bool readOnly = false,
        bool destructive = false,
        bool idempotent = false,
        bool openWorld = false,
        object? outputSchema = null)
    {
        var shownTitle = string.IsNullOrWhiteSpace(title) ? name : title;
        return new Tool
        {
            Name = name,
            Title = shownTitle,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(inputSchema),
            OutputSchema = outputSchema is null ? null : JsonSerializer.SerializeToElement(outputSchema),
            Annotations = new ToolAnnotations
            {
                Title = shownTitle,
                ReadOnlyHint = readOnly,
                DestructiveHint = destructive,
                IdempotentHint = idempotent,
                OpenWorldHint = openWorld,
            },
        };
    }

    private static object OutputObjSchema(params (string Name, string Type)[] fields)
    {
        var props = new Dictionary<string, object>();
        foreach (var (name, type) in fields)
        {
            props[name] = new { type };
        }

        return new
        {
            type = "object",
            properties = props,
            additionalProperties = true,
        };
    }

    private static object EmptySchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>(),
    };

    private static object ObjSchema(params (string Name, string Type, string Description, bool Required)[] fields)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var (name, type, desc, req) in fields)
        {
            props[name] = new { type, description = desc };
            if (req)
            {
                required.Add(name);
            }
        }

        return required.Count > 0
            ? new { type = "object", properties = props, required }
            : new { type = "object", properties = props };
    }

    private static CallToolResult BuildToolText(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = false,
    };

    private static CallToolResult BuildToolJson(object data)
    {
        var structured = JsonSerializer.SerializeToElement(data, JsonPrettyOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(structured, JsonPrettyOptions) }],
            // Structured content is an object for every client version; other values stay in the text.
            StructuredContent = structured.ValueKind == JsonValueKind.Object ? structured : null,
            IsError = false,
        };
    }

    private static CallToolResult BuildToolError(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };

    private static string GetString(JsonElement obj, string name, string fallback = "")
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null)
        {
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? fallback) : el.ToString();
        }

        return fallback;
    }

    private static string RequireString(JsonElement obj, string name)
    {
        var val = GetString(obj, name);
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new McpInvalidParamsException($"Missing required string parameter '{name}'.");
        }

        return val.Trim();
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var i2)) return i2;
        }

        return null;
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el))
        {
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
        }

        return null;
    }

    private static Dictionary<string, string> ExtractStringParameters(JsonElement args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (args.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (var prop in args.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Null)
            {
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? (prop.Value.GetString() ?? string.Empty)
                    : prop.Value.ToString();
            }
        }

        return dict;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnumRegex();

    private sealed class McpInvalidParamsException(string message) : Exception(message);
}

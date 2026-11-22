using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;

namespace Denon.App.Core.Mcp;

/// <summary>
/// Spec-compliant Streamable HTTP Model Context Protocol (MCP) server hosted natively inside Denon Desktop.
/// - Single endpoint: POST /mcp (JSON-RPC 2.0), GET /mcp (SSE live notifications), DELETE /mcp (session close), GET /health
/// - Exposes live Room Graph Resources, Expert Prompts, Curated Intelligence Tools, and auto-bridged IDenonPlugin Tools
/// - Enforces Bearer authentication, Permission Tiers (Observer / SafeControl / FullOrchestration), and MaxSafeVolume ceiling
/// </summary>
public sealed partial class DenonMcpHttpServer : IAsyncDisposable
{
    private const string ServerName = "denon-desktop-mcp";
    private const string ServerVersion = "1.8.1";
    private const string DefaultProtocolVersion = "2025-11-25";

    private static readonly JsonSerializerOptions JsonOutOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions JsonPrettyOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverController _receiverController;
    private readonly PluginHostService _pluginHost;
    private readonly RoomIntelligenceService _intelligence;
    private readonly RadioBrowserService _radioBrowser;
    private readonly YouTubeMediaService _youtubeService;
    private readonly LocalAudioRelayServer _relayServer;
    private readonly YouTubeQueueService _queueService;
    private readonly AppLogStore _logStore;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _activityLock = new();
    private readonly List<McpActivityEntry> _activityLog = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly ConcurrentDictionary<Guid, StreamWriter> _sseClients = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    public DenonMcpHttpServer(
        AppSettingsStore settingsStore,
        ReceiverController receiverController,
        PluginHostService pluginHost,
        RoomIntelligenceService intelligence,
        RadioBrowserService radioBrowser,
        YouTubeMediaService youtubeService,
        LocalAudioRelayServer relayServer,
        YouTubeQueueService queueService,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _receiverController = receiverController;
        _pluginHost = pluginHost;
        _intelligence = intelligence;
        _radioBrowser = radioBrowser;
        _youtubeService = youtubeService;
        _relayServer = relayServer;
        _queueService = queueService;
        _logStore = logStore;

        _intelligence.RoomGraphChanged += BroadcastRoomUpdatedNotification;
    }

    public event Action? StatusChanged;

    public event Action<McpActivityEntry>? ActivityAdded;

    public bool IsRunning => _listener?.IsListening == true;

    public string ListeningEndpoint { get; private set; } = string.Empty;

    public string? LastError { get; private set; }

    public int RegisteredToolCount => BuildToolDefinitions().Count;

    public IReadOnlyList<McpActivityEntry> GetRecentActivity()
    {
        lock (_activityLock)
        {
            return [.. _activityLog];
        }
    }

    public void ClearActivity()
    {
        lock (_activityLock)
        {
            _activityLog.Clear();
        }
    }

    public string BuildClientConfigJson()
    {
        var cfg = _settingsStore.Current.Mcp;
        var url = string.IsNullOrWhiteSpace(ListeningEndpoint)
            ? $"http://127.0.0.1:{cfg.Port}/mcp"
            : ListeningEndpoint;

        object serverEntry = cfg.RequireBearerAuth && !string.IsNullOrWhiteSpace(cfg.BearerToken)
            ? new
            {
                type = "http",
                url,
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {cfg.BearerToken}",
                },
            }
            : new
            {
                type = "http",
                url,
            };

        var root = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["denon-desktop"] = serverEntry,
            },
        };

        return JsonSerializer.Serialize(root, JsonPrettyOptions);
    }

    public async Task ApplySettingsAsync(CancellationToken ct = default)
    {
        var cfg = _settingsStore.Current.Mcp;
        if (!cfg.Enabled)
        {
            await StopAsync();
            return;
        }

        await RestartAsync(ct);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            await StopCoreLockedAsync();
            StartCoreLocked();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        StatusChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreLockedAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _intelligence.RoomGraphChanged -= BroadcastRoomUpdatedNotification;
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private void StartCoreLocked()
    {
        var cfg = _settingsStore.Current.Mcp;
        if (!cfg.Enabled)
        {
            return;
        }

        var port = Math.Clamp(cfg.Port, 1024, 65535);
        var listener = new HttpListener();
        var boundHost = "127.0.0.1";

        try
        {
            if (cfg.AllowLanConnections)
            {
                try
                {
                    listener.Prefixes.Add($"http://+:{port}/");
                    listener.Start();
                    boundHost = LocalAudioRelayServer.ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host);
                }
                catch
                {
                    // Fallback cleanly to localhost if URLACL isn't registered for '+'
                    listener.Close();
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    boundHost = "127.0.0.1";
                    _logStore.Warning($"MCP LAN bind (+:{port}) needs admin URLACL; fell back to localhost (127.0.0.1:{port}).");
                }
            }
            else
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            ListeningEndpoint = $"http://{boundHost}:{port}/mcp";
            LastError = null;

            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), token);
            _logStore.Info($"Streamable HTTP MCP server listening on {ListeningEndpoint} ({RegisteredToolCount} tools, tier: {cfg.PermissionTier}).");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ListeningEndpoint = string.Empty;
            try { listener.Close(); } catch { }
            _logStore.Warning($"Failed to start MCP HTTP server on port {port}: {ex.Message}");
        }
    }

    private async Task StopCoreLockedAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        foreach (var kv in _sseClients)
        {
            if (_sseClients.TryRemove(kv.Key, out var writer))
            {
                try { writer.Dispose(); } catch { }
            }
        }

        if (_listener is not null)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
        }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _acceptLoop = null;
        }

        ListeningEndpoint = string.Empty;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logStore.Warning($"MCP listener warning: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleHttpContextAsync(ctx, ct), ct);
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        AddCorsHeaders(resp);

        try
        {
            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            // Public lightweight health check
            if (req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                (path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path == "/"))
            {
                await WriteJsonResponseAsync(resp, 200, new
                {
                    status = "ok",
                    server = ServerName,
                    version = ServerVersion,
                    protocolVersion = DefaultProtocolVersion,
                    endpoint = ListeningEndpoint,
                    permissionTier = _settingsStore.Current.Mcp.PermissionTier.ToString(),
                    maxSafeVolume = _settingsStore.Current.Mcp.MaxSafeVolume,
                    toolsCount = RegisteredToolCount,
                    resourcesCount = 4,
                    promptsCount = 4,
                });
                return;
            }

            if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonResponseAsync(resp, 404, new { error = "Not Found. Use /mcp (Streamable HTTP MCP) or /health." });
                return;
            }

            if (!IsAuthorized(req))
            {
                LogActivity("auth", $"{req.HttpMethod} /mcp from {req.RemoteEndPoint?.Address}", "Blocked (401)", 0);
                resp.Headers["WWW-Authenticate"] = "Bearer realm=\"DenonDesktopMCP\"";
                await WriteJsonResponseAsync(resp, 401, new { error = "Unauthorized. Provide header: Authorization: Bearer <token>" });
                return;
            }

            if (req.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var sid = req.Headers["Mcp-Session-Id"];
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    _sessions.TryRemove(sid, out _);
                }

                resp.StatusCode = 204;
                resp.Close();
                return;
            }

            if (req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseStreamAsync(resp, ct);
                return;
            }

            if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonResponseAsync(resp, 405, new { error = "Method Not Allowed. Use POST /mcp or GET /mcp." });
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteJsonResponseAsync(resp, 400, BuildJsonRpcError(null, -32700, "Empty JSON-RPC request body."));
                return;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                await WriteJsonResponseAsync(resp, 400, BuildJsonRpcError(null, -32700, $"Parse error: {ex.Message}"));
                return;
            }

            using (doc)
            {
                var sessionId = req.Headers["Mcp-Session-Id"];
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    sessionId = Guid.NewGuid().ToString("N");
                }

                _sessions[sessionId] = DateTimeOffset.UtcNow;
                resp.Headers["Mcp-Session-Id"] = sessionId;

                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var batchResults = new List<object>();
                    foreach (var item in root.EnumerateArray())
                    {
                        var single = await DispatchJsonRpcAsync(item, ct);
                        if (single is not null)
                        {
                            batchResults.Add(single);
                        }
                    }

                    if (batchResults.Count == 0)
                    {
                        resp.StatusCode = 202;
                        resp.Close();
                    }
                    else
                    {
                        await WriteJsonResponseAsync(resp, 200, batchResults);
                    }
                }
                else
                {
                    var result = await DispatchJsonRpcAsync(root, ct);
                    if (result is null)
                    {
                        // JSON-RPC notification (no id) -> 202 Accepted per MCP Streamable HTTP spec
                        resp.StatusCode = 202;
                        resp.Close();
                    }
                    else
                    {
                        await WriteJsonResponseAsync(resp, 200, result);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonResponseAsync(resp, 500, BuildJsonRpcError(null, -32603, $"Internal server error: {ex.Message}"));
            }
            catch
            {
                try { resp.Abort(); } catch { }
            }
        }
    }

    private async Task HandleSseStreamAsync(HttpListenerResponse resp, CancellationToken ct)
    {
        var clientId = Guid.NewGuid();
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers["Cache-Control"] = "no-cache";
        resp.Headers["Connection"] = "keep-alive";
        resp.SendChunked = true;

        using var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false), leaveOpen: false) { AutoFlush = true };
        _sseClients[clientId] = writer;
        LogActivity("sse", "Connected live room-event SSE stream", "OK", 0);

        try
        {
            await writer.WriteAsync(": connected to denon-desktop-mcp\n\n");
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                await writer.WriteAsync(": keepalive\n\n");
            }
        }
        catch
        {
            // Client disconnected.
        }
        finally
        {
            _sseClients.TryRemove(clientId, out _);
            try { resp.Close(); } catch { }
        }
    }

    private void BroadcastRoomUpdatedNotification()
    {
        if (_sseClients.IsEmpty)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/resources/updated",
            @params = new { uri = "denon://room/state" },
        }, JsonOutOptions);

        var frame = $"event: message\ndata: {payload}\n\n";
        foreach (var kv in _sseClients)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await kv.Value.WriteAsync(frame);
                }
                catch
                {
                    _sseClients.TryRemove(kv.Key, out _);
                }
            });
        }
    }

    private async Task<object?> DispatchJsonRpcAsync(JsonElement msg, CancellationToken ct)
    {
        var hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null;
        object? idObj = hasId ? CloneJsonId(idEl) : null;
        var method = msg.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? string.Empty : string.Empty;
        var hasParams = msg.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object;

        // Notifications (no id) require no JSON-RPC response body
        if (!hasId || method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            object result = method switch
            {
                "initialize" => HandleInitialize(hasParams ? paramsEl : default),
                "ping" => new { },
                "tools/list" => new { tools = BuildToolDefinitions() },
                "tools/call" => await HandleToolCallAsync(hasParams ? paramsEl : default, ct),
                "resources/list" => HandleResourcesList(),
                "resources/read" => HandleResourceRead(hasParams ? paramsEl : default),
                "prompts/list" => HandlePromptsList(),
                "prompts/get" => HandlePromptGet(hasParams ? paramsEl : default),
                _ => throw new McpMethodNotFoundException($"Method '{method}' not found."),
            };

            sw.Stop();
            if (method is not ("ping" or "tools/list" or "resources/list" or "prompts/list"))
            {
                var detail = method == "tools/call" && hasParams && paramsEl.TryGetProperty("name", out var nEl)
                    ? nEl.GetString() ?? method
                    : (method == "resources/read" && hasParams && paramsEl.TryGetProperty("uri", out var uEl) ? uEl.GetString() ?? method : method);
                LogActivity(method, detail, "OK", sw.ElapsedMilliseconds);
            }

            return new
            {
                jsonrpc = "2.0",
                id = idObj,
                result,
            };
        }
        catch (McpMethodNotFoundException ex)
        {
            sw.Stop();
            LogActivity(method, ex.Message, "Error", sw.ElapsedMilliseconds);
            return BuildJsonRpcError(idObj, -32601, ex.Message);
        }
        catch (McpInvalidParamsException ex)
        {
            sw.Stop();
            LogActivity(method, ex.Message, "Invalid Params", sw.ElapsedMilliseconds);
            return BuildJsonRpcError(idObj, -32602, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            sw.Stop();
            LogActivity(method, ex.Message, "Blocked (Tier)", sw.ElapsedMilliseconds);
            return new
            {
                jsonrpc = "2.0",
                id = idObj,
                result = BuildToolError(ex.Message),
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogActivity(method, ex.Message, "Error", sw.ElapsedMilliseconds);
            return new
            {
                jsonrpc = "2.0",
                id = idObj,
                result = BuildToolError(ex.Message),
            };
        }
    }

    private object HandleInitialize(JsonElement paramsEl)
    {
        var negotiatedVersion = DefaultProtocolVersion;
        if (paramsEl.ValueKind == JsonValueKind.Object &&
            paramsEl.TryGetProperty("protocolVersion", out var pvEl) &&
            pvEl.GetString() is { Length: > 0 } requested)
        {
            negotiatedVersion = requested;
        }

        return new
        {
            protocolVersion = negotiatedVersion,
            capabilities = new
            {
                tools = new { listChanged = true },
                resources = new { subscribe = false, listChanged = true },
                prompts = new { listChanged = false },
            },
            serverInfo = new
            {
                name = ServerName,
                version = ServerVersion,
            },
            instructions =
                "Denon Desktop Living-Room Intelligence MCP Server. Read denon://room/state and denon://memory/habits first to understand the user's Denon AVR, Philips Hue lights, LG webOS Smart TV, Home Assistant entities, active Room Doctor insights, and preferred listening volumes. Use apply_atmosphere for cohesive multi-device moods (it automatically captures an Undo snapshot and enforces MaxSafeVolume).",
        };
    }

    private static object HandleResourcesList() => new
    {
        resources = new object[]
        {
            new
            {
                uri = "denon://room/state",
                name = "Live Living-Room State Graph",
                description = "Unified real-time state of Denon/Marantz AVR, Now Playing, Up-Next Queue, Philips Hue, LG webOS TV, Home Assistant, Room Doctor Insights, Undo snapshot, and safety limits.",
                mimeType = "application/json",
            },
            new
            {
                uri = "denon://diagnostics/health",
                name = "Home Theater Diagnostics & Health",
                description = "Cross-device Room Doctor insights, LIVE PUSH status, and recent warning/error log entries.",
                mimeType = "application/json",
            },
            new
            {
                uri = "denon://catalog/capabilities",
                name = "Inputs, Sound Modes, Smart Scenes, Rules & Plugins Catalog",
                description = "Complete reference of valid AVR input codes, sound mode codes, configured Smart Scenes, Automation Rules, and all installed Plugin Actions.",
                mimeType = "application/json",
            },
            new
            {
                uri = "denon://memory/habits",
                name = "Local Listening Habits & Preferences Memory",
                description = "Locally learned preferred volume per time of day (Morning/Afternoon/Evening/LateNight), top inputs, sound modes, scenes, and plugin actions.",
                mimeType = "application/json",
            },
        },
    };

    private object HandleResourceRead(JsonElement paramsEl)
    {
        var uri = GetString(paramsEl, "uri");
        object payload = uri.ToLowerInvariant() switch
        {
            "denon://room/state" => _intelligence.BuildRoomStatePayload(),
            "denon://diagnostics/health" => _intelligence.BuildDiagnosticsPayload(),
            "denon://catalog/capabilities" => _intelligence.BuildCapabilitiesPayload(),
            "denon://memory/habits" => _intelligence.BuildHabitsPayload(),
            _ => throw new McpInvalidParamsException($"Unknown resource URI '{uri}'."),
        };

        return new
        {
            contents = new[]
            {
                new
                {
                    uri,
                    mimeType = "application/json",
                    text = JsonSerializer.Serialize(payload, JsonPrettyOptions),
                },
            },
        };
    }

    private static object HandlePromptsList() => new
    {
        prompts = new object[]
        {
            new
            {
                name = "design_atmosphere",
                description = "Design and apply a cohesive living-room atmosphere across Denon AVR, Philips Hue, LG webOS TV, and music.",
                arguments = new[]
                {
                    new { name = "vibe", description = "Desired mood or activity (e.g. 'cozy rainy jazz night', 'sci-fi cinema', 'late night reading')", required = true },
                },
            },
            new
            {
                name = "diagnose_home_theater",
                description = "Inspect cross-device state and logs to diagnose & fix audio/video/TV/lighting issues.",
                arguments = new[]
                {
                    new { name = "symptom", description = "What feels wrong (e.g. 'no sound from TV', 'too quiet', 'lights not syncing')", required = false },
                },
            },
            new
            {
                name = "acoustic_advisor",
                description = "Recommend and apply Audyssey, Dynamic EQ/Volume, Dialog/Subwoofer trims safely for the user's goal.",
                arguments = new[]
                {
                    new { name = "goal", description = "Acoustic objective (e.g. 'clearer movie dialogue at night', 'punchier bass for music')", required = true },
                },
            },
            new
            {
                name = "architect_automation",
                description = "Synthesize a validated Smart Scene and/or Automation Rule from a plain-English habit description.",
                arguments = new[]
                {
                    new { name = "habit", description = "Natural language trigger & actions (e.g. 'When I open Plex after 8pm, dim Hue and switch TV to HDMI 2')", required = true },
                },
            },
        },
    };

    private static object HandlePromptGet(JsonElement paramsEl)
    {
        var name = GetString(paramsEl, "name");
        var args = paramsEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.Object ? aEl : default;

        var promptText = name.ToLowerInvariant() switch
        {
            "design_atmosphere" =>
                $"Design and apply a cohesive living-room atmosphere for: \"{GetString(args, "vibe", "evening relax")}\". " +
                "First read denon://room/state and denon://memory/habits to respect the user's preferred time-of-day volume and available plugins. " +
                "Then use apply_atmosphere (plus media_search_radio or media_play_youtube if music fits) to orchestrate Denon AVR, Philips Hue, and LG webOS TV smoothly.",
            "diagnose_home_theater" =>
                $"Diagnose this home theater symptom: \"{GetString(args, "symptom", "general health check")}\". " +
                "Call diagnose_room and read denon://diagnostics/health + denon://room/state. Identify any cross-device mismatches (e.g. LG TV speaker output vs HDMI eARC, muted AVR, low volume, plugin warning) and either fix them with fix_room_issue or explain the exact cause clearly.",
            "acoustic_advisor" =>
                $"Optimize Denon AVR acoustics for: \"{GetString(args, "goal", "clear dialogue at moderate volume")}\". " +
                "Read denon://room/state first, then use avr_tune_acoustics safely and explain what each adjustment does.",
            "architect_automation" =>
                $"Create a Denon Desktop Smart Scene and/or Automation Rule for this habit: \"{GetString(args, "habit", "movie night automation")}\". " +
                "Read denon://catalog/capabilities to use valid input codes, sound modes, and plugin action IDs, then call create_or_update_scene and/or create_automation_rule.",
            _ => throw new McpInvalidParamsException($"Unknown prompt '{name}'."),
        };

        return new
        {
            description = $"Denon Desktop guided workflow: {name}",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new { type = "text", text = promptText },
                },
            },
        };
    }

    private List<object> BuildToolDefinitions()
    {
        var tools = new List<object>
        {
            Tool("get_room_state", "Get the complete live living-room state graph: Denon AVR, Now Playing, Queue, Philips Hue, LG webOS TV, Home Assistant, Room Doctor Insights, Undo state, and safety ceiling.", EmptySchema()),
            Tool("diagnose_room", "Run the cross-device Self-Healing Room Doctor to detect mismatches (muted while playing, late-night volume, TV eARC vs speaker mismatch, OLED burn-in risk, plugin warnings).", EmptySchema()),
            Tool("fix_room_issue", "Execute a Room Doctor auto-fix key (e.g. 'avr.unmute', 'avr.safe_volume', 'avr.late_night_comfort', 'room.route_tv_earc', 'tv.screen_off', 'avr.fix_silent', 'avr.refresh').",
                ObjSchema(("fix_action_key", "string", "The fixActionKey returned by diagnose_room", true))),
            Tool("get_listening_habits", "Read locally learned listening habits (average volume by Morning/Afternoon/Evening/LateNight, top inputs, sound modes, scenes, and plugin actions).", EmptySchema()),
            Tool("restore_previous_state", "1-click Undo: restore the Denon AVR state captured before the most recent AI / atmosphere / scene mutation.", EmptySchema()),
            Tool("apply_atmosphere", "High-level semantic multi-device orchestrator. Captures an Undo snapshot first, enforces MaxSafeVolume, and coordinates Denon AVR + Philips Hue + LG webOS TV in one call.",
                ObjSchema(
                    ("reason", "string", "Short description of the atmosphere (shown in Undo history & optional TV toast)", false),
                    ("volume", "integer", "Target master volume (0-98, automatically capped by MaxSafeVolume)", false),
                    ("input_source", "string", "AVR input code (e.g. TV, CBL/SAT, GAME, MPLAY, CD, NET, BT, AUX1)", false),
                    ("sound_mode", "string", "AVR sound mode (e.g. STEREO, MOVIE, MUSIC, GAME, DIRECT, PURE DIRECT, MCH STEREO)", false),
                    ("dynamic_volume", "string", "Audyssey Dynamic Volume: OFF, LIT, MED, or HEV", false),
                    ("hue_action", "string", "Optional Philips Hue actionId (e.g. hue.movie_dim, hue.candlelight, hue.all_on, hue.all_off, hue.music_sync_on)", false),
                    ("tv_action", "string", "Optional LG webOS TV actionId (e.g. tv.hdmi1, tv.hdmi2, tv.audio_earc, tv.youtube, tv.plex, tv.screen_off)", false),
                    ("oled_screen_off", "boolean", "If true, blanks LG OLED panel (Audio-Only Mode) to prevent burn-in during music", false),
                    ("tv_toast", "string", "Optional short message to display as an on-screen toast banner on the LG TV", false))),

            // AVR Core Controls
            Tool("avr_set_power", "Power the Denon/Marantz AVR ON or put it into Standby.",
                ObjSchema(("power_on", "boolean", "true to power ON, false for Standby", true))),
            Tool("avr_set_volume", "Set Denon AVR master volume (0-98 scale, where 80 = 0.0 dB, 50 = -30.0 dB). Enforces the configured MaxSafeVolume safety ceiling.",
                ObjSchema(("volume", "integer", "Target volume (0-98)", true))),
            Tool("avr_set_mute", "Mute or unmute Denon AVR master audio.",
                ObjSchema(("muted", "boolean", "true to mute, false to unmute", true))),
            Tool("avr_set_input", "Switch Denon AVR input source (e.g. TV, GAME, MPLAY, CBL/SAT, DVD, BD, CD, NET, BT, TUNER, AUX1).",
                ObjSchema(("input_source", "string", "AVR input source code", true))),
            Tool("avr_set_sound_mode", "Switch Denon AVR sound mode (e.g. STEREO, DIRECT, PURE DIRECT, MOVIE, MUSIC, GAME, AUTO, MCH STEREO).",
                ObjSchema(("sound_mode", "string", "Sound mode code", true))),
            Tool("avr_playback_control", "Control HEOS playback transport.",
                ObjSchema(("action", "string", "One of: play, pause, stop, next, previous", true))),
            Tool("avr_set_zone2", "Turn Zone 2 power ON or OFF.",
                ObjSchema(("power_on", "boolean", "true for Zone 2 ON, false for OFF", true))),
            Tool("avr_set_eco_mode", "Set Denon ECO power mode (ON, AUTO, OFF).",
                ObjSchema(("mode", "string", "ON, AUTO, or OFF", true))),
            Tool("avr_set_sleep_timer", "Set or cancel the AVR sleep timer in minutes (0 or omitted cancels sleep timer).",
                ObjSchema(("minutes", "integer", "Sleep timer minutes (0 to cancel, or 10-120)", true))),
            Tool("avr_tune_acoustics", "Adjust live Denon AVR acoustic settings over Telnet :23 (any omitted parameter is left unchanged).",
                ObjSchema(
                    ("multeq", "string", "Audyssey MultEQ: AUDYSSEY, BYP.LR, FLAT, or OFF", false),
                    ("dynamic_eq", "boolean", "Enable/disable Audyssey Dynamic EQ", false),
                    ("dynamic_volume", "string", "Audyssey Dynamic Volume: OFF, LIT, MED, or HEV", false),
                    ("restorer", "string", "Compressed Audio Restorer: OFF, LOW, MED, or HI", false),
                    ("dimmer", "string", "Front panel display dimmer: BRI, DIM, DAR, or OFF", false),
                    ("subwoofer_trim_db", "integer", "Subwoofer trim in dB (-12 to +12)", false),
                    ("dialog_trim_db", "integer", "Dialog enhancer trim in dB (-12 to +12)", false),
                    ("bass_db", "integer", "Tone Bass trim in dB (-6 to +6)", false),
                    ("treble_db", "integer", "Tone Treble trim in dB (-6 to +6)", false))),

            // Media & Music DJ
            Tool("media_search_radio", "Search 40,000+ global internet radio stations via Radio Browser.",
                ObjSchema(
                    ("query", "string", "Station name or keyword", false),
                    ("tag", "string", "Genre/style tag (e.g. jazz, ambient, synthwave, classical)", false),
                    ("hi_res_only", "boolean", "Only return lossless FLAC / high-bitrate stations", false),
                    ("limit", "integer", "Max results (default 10)", false))),
            Tool("media_play_station_url", "Play any direct HTTP/HTTPS internet radio or audio stream URL on the Denon AVR.",
                ObjSchema(
                    ("url", "string", "Direct stream URL", true),
                    ("title", "string", "Friendly station/track title", false),
                    ("low_volume", "boolean", "If true, sets safe low volume before playing", false))),
            Tool("media_search_youtube", "Search YouTube / YouTube Music for songs, mixes, or live sets playable on Denon HEOS.",
                ObjSchema(
                    ("query", "string", "Search query or YouTube URL", true),
                    ("music_mode", "boolean", "Prioritize music tracks (default true)", false),
                    ("limit", "integer", "Max results (default 6)", false))),
            Tool("media_play_youtube", "Resolve a YouTube search query or URL and stream it directly to Denon HEOS via the local LAN audio relay.",
                ObjSchema(
                    ("query_or_url", "string", "YouTube video URL or search phrase (plays top match)", true),
                    ("queue_only", "boolean", "If true, adds to Up-Next queue instead of interrupting current track", false))),

            // Generative Smart Scene & Automation Architect
            Tool("list_scenes_and_rules", "List all configured Smart Scenes and Automation Rules.", EmptySchema()),
            Tool("activate_smart_scene", "Activate an existing Smart Scene by ID or Name (including its chained plugin actions).",
                ObjSchema(("scene_id_or_name", "string", "Smart Scene ID or Name (e.g. 'movie-night' or 'Late Night')", true))),
            Tool("create_or_update_scene", "Create or update a persistent Smart Scene in settings.json with AVR settings and optional Hue/TV/HA plugin action.",
                ObjSchema(
                    ("id", "string", "Stable slug ID (e.g. 'blade-runner-night')", true),
                    ("name", "string", "Display name (e.g. 'Blade Runner Night')", true),
                    ("description", "string", "Short description of the scene", false),
                    ("input_source", "string", "AVR input code (e.g. TV, GAME, NET)", false),
                    ("sound_mode", "string", "AVR sound mode (e.g. MOVIE, STEREO, PURE DIRECT)", false),
                    ("target_volume", "integer", "Target volume (0-98, capped by MaxSafeVolume)", false),
                    ("dynamic_volume", "string", "OFF, LIT, MED, or HEV", false),
                    ("plugin_id", "string", "Optional plugin ID to chain (e.g. 'philips-hue', 'smart-tv', 'home-assistant')", false),
                    ("plugin_action_id", "string", "Optional plugin action ID to chain (e.g. 'hue.movie_dim', 'tv.screen_off')", false))),
            Tool("create_automation_rule", "Create a persistent Automation Rule (App Launch Watcher or Time-of-Day Schedule) in settings.json.",
                ObjSchema(
                    ("name", "string", "Friendly rule name", true),
                    ("trigger_type", "string", "'AppLaunch' or 'TimeOfDay'", true),
                    ("enabled", "boolean", "Whether the rule starts enabled (default true)", false),
                    ("process_names", "string", "For AppLaunch: comma-separated process names without .exe (e.g. 'vlc, Plex')", false),
                    ("schedule_time", "string", "For TimeOfDay: 24-hour HH:mm local time (e.g. '22:30')", false),
                    ("days_filter", "string", "Daily, Weekdays, or Weekends (default Daily)", false),
                    ("target_scene_id", "string", "Smart Scene ID to activate when triggered", false))),
        };

        // Zero-config auto-bridge: expose EVERY enabled plugin action as a typed MCP tool!
        foreach (var plugin in _pluginHost.GetPlugins().Where(p => p.IsEnabled))
        {
            foreach (var action in plugin.Actions)
            {
                var toolName = BuildPluginToolName(plugin.Id, action.ActionId);
                tools.Add(Tool(
                    toolName,
                    $"[{plugin.Name}] {action.DisplayName}: {action.Description}",
                    ObjSchema(
                        ("message", "string", "Optional text/toast message parameter (if supported by this action)", false),
                        ("contentId", "string", "Optional YouTube video URL/ID (for tv.youtube)", false),
                        ("entityId", "string", "Optional Home Assistant entity ID (for ha.trigger_entity)", false),
                        ("button", "string", "Optional LG remote button name (e.g. HOME, BACK, ENTER)", false))));
            }
        }

        return tools;
    }

    private async Task<object> HandleToolCallAsync(JsonElement paramsEl, CancellationToken ct)
    {
        var toolName = GetString(paramsEl, "name");
        var args = paramsEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.Object ? aEl : default;
        var cfg = _settingsStore.Current.Mcp;

        // Read-only tools (allowed in all permission tiers including Observer)
        switch (toolName)
        {
            case "get_room_state":
                return BuildToolJson(_intelligence.BuildRoomStatePayload());

            case "diagnose_room":
                return BuildToolJson(new { insights = _intelligence.DiagnoseRoom() });

            case "get_listening_habits":
                return BuildToolJson(_intelligence.BuildHabitsPayload());

            case "list_scenes_and_rules":
                return BuildToolJson(new
                {
                    scenes = _settingsStore.Current.Receiver.Scenes,
                    automations = _settingsStore.Current.Automations,
                });

            case "media_search_radio":
            {
                var query = GetString(args, "query");
                var tag = GetString(args, "tag");
                var hiRes = GetBool(args, "hi_res_only") ?? false;
                var limit = Math.Clamp(GetInt(args, "limit") ?? 10, 1, 30);
                var stations = await _radioBrowser.SearchStationsAsync(query, tag, null, hiRes, limit, ct);
                return BuildToolJson(stations.Select(s => new
                {
                    name = s.Name,
                    url = s.UrlResolved,
                    codec = s.Codec,
                    bitrate = s.Bitrate,
                    country = s.Country,
                    tags = s.Tags,
                }));
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
        }

        // Everything below mutates live devices or settings -> block in Observer mode!
        if (cfg.PermissionTier == McpPermissionTier.Observer)
        {
            throw new UnauthorizedAccessException($"MCP Permission Tier is 'Observer' (read-only). Change to SafeControl or FullOrchestration in Denon Desktop Settings to run '{toolName}'.");
        }

        switch (toolName)
        {
            case "restore_previous_state":
            {
                var (ok, msg) = await _intelligence.RestoreLastSnapshotAsync(ct);
                return ok ? BuildToolText(msg) : BuildToolError(msg);
            }

            case "fix_room_issue":
            {
                var key = RequireString(args, "fix_action_key");
                var (ok, msg) = await _intelligence.ExecuteInsightFixAsync(key, ct);
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
            {
                var on = GetBool(args, "power_on") ?? throw new McpInvalidParamsException("Missing boolean 'power_on'.");
                await _receiverController.SetZone2PowerAsync(on, ct);
                return BuildToolText(on ? "Turned Zone 2 ON." : "Turned Zone 2 OFF.");
            }

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
                await _receiverController.PlayCustomUrlAsync(url, lowVol, unmute: true, cancellationToken: ct);
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

            case "activate_smart_scene":
            {
                var target = RequireString(args, "scene_id_or_name");
                var scene = _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s =>
                    s.Id.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpInvalidParamsException($"Smart Scene '{target}' not found.");

                var safeScene = scene.Clone();
                if (safeScene.TargetVolume.HasValue)
                {
                    safeScene.TargetVolume = ApplyVolumeCeiling(safeScene.TargetVolume.Value, cfg.MaxSafeVolume, out _);
                }

                _intelligence.CaptureSnapshot($"MCP Smart Scene: {safeScene.Name}");
                await _receiverController.ApplySceneAsync(safeScene, ct);
                _intelligence.RecordSceneUsed(safeScene.Name);
                return BuildToolText($"Activated Smart Scene '{safeScene.Name}' (with {safeScene.PluginActions.Count} chained plugin action(s)).");
            }

            case "create_or_update_scene":
                EnsureFullOrchestration(cfg, toolName);
                return await ExecuteCreateOrUpdateSceneAsync(args, cfg, ct);

            case "create_automation_rule":
                EnsureFullOrchestration(cfg, toolName);
                return await ExecuteCreateAutomationRuleAsync(args, ct);
        }

        // Check dynamically auto-bridged plugin tools!
        foreach (var plugin in _pluginHost.GetPlugins().Where(p => p.IsEnabled))
        {
            foreach (var action in plugin.Actions)
            {
                if (!BuildPluginToolName(plugin.Id, action.ActionId).Equals(toolName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (action.ActionId.Contains("power_off", StringComparison.OrdinalIgnoreCase) &&
                    cfg.PermissionTier < McpPermissionTier.FullOrchestration)
                {
                    throw new UnauthorizedAccessException($"Running '{action.ActionId}' requires 'FullOrchestration' permission tier.");
                }

                var dict = ExtractStringParameters(args);
                var result = await _pluginHost.ExecuteActionAsync(plugin.Id, action.ActionId, dict.Count > 0 ? dict : null, ct);
                _intelligence.RecordPluginActionUsed(plugin.Id, action.ActionId);
                return result.Success ? BuildToolText(result.Message) : BuildToolError(result.Message);
            }
        }

        throw new McpInvalidParamsException($"Unknown MCP tool '{toolName}'.");
    }

    private async Task<object> ExecuteApplyAtmosphereAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
        var reason = GetString(args, "reason", "AI Atmosphere");
        _intelligence.CaptureSnapshot($"MCP Atmosphere: {reason}");

        var stepsTaken = new List<string>();
        await _receiverController.SetPowerAsync(true, ct);
        await _receiverController.SetMuteAsync(false, ct);

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

        if (GetInt(args, "volume") is { } vol)
        {
            var clamped = ApplyVolumeCeiling(vol, cfg.MaxSafeVolume, out var capped);
            await _receiverController.SetVolumeAsync(clamped, ct);
            stepsTaken.Add($"Volume → {clamped} ({RoomIntelligenceService.FormatDb(clamped)}){(capped ? " [capped]" : string.Empty)}");
        }

        if (GetString(args, "dynamic_volume") is { Length: > 0 } dv)
        {
            await _receiverController.SetDynamicVolumeAsync(dv.ToUpperInvariant(), ct);
            stepsTaken.Add($"Dynamic Volume → {dv.ToUpperInvariant()}");
        }

        if (GetString(args, "hue_action") is { Length: > 0 } hueAct)
        {
            var res = await _pluginHost.ExecuteActionAsync("philips-hue", hueAct, null, ct);
            stepsTaken.Add($"Philips Hue ({hueAct}): {res.Message}");
            _intelligence.RecordPluginActionUsed("philips-hue", hueAct);
        }

        if (GetString(args, "tv_action") is { Length: > 0 } tvAct)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", tvAct, null, ct);
            stepsTaken.Add($"LG TV ({tvAct}): {res.Message}");
            _intelligence.RecordPluginActionUsed("smart-tv", tvAct);
        }

        if (GetBool(args, "oled_screen_off") == true)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.screen_off", null, ct);
            stepsTaken.Add($"LG OLED Screen Off: {res.Message}");
        }

        if (GetString(args, "tv_toast") is { Length: > 0 } toast)
        {
            var res = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.toast", new Dictionary<string, string> { ["message"] = toast }, ct);
            stepsTaken.Add($"TV Toast: {res.Message}");
        }

        return BuildToolJson(new
        {
            applied = reason,
            undoAvailable = true,
            steps = stepsTaken,
        });
    }

    private async Task<object> ExecuteTuneAcousticsAsync(JsonElement args, CancellationToken ct)
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

    private async Task<object> ExecuteCreateOrUpdateSceneAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
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
        var pluginId = GetString(args, "plugin_id");
        var pluginActionId = GetString(args, "plugin_action_id");

        var updated = _settingsStore.Current.Clone();
        var existing = updated.Receiver.Scenes.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var scene = existing ?? new ScenePreset { Id = id };

        scene.Name = name;
        scene.Description = description;
        if (!string.IsNullOrWhiteSpace(input)) scene.InputSource = input.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(mode)) scene.SoundMode = mode.ToUpperInvariant();
        if (targetVol.HasValue) scene.TargetVolume = targetVol.Value;
        if (!string.IsNullOrWhiteSpace(dvol)) scene.DynamicVolume = dvol.ToUpperInvariant();

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
            updated.Receiver.Scenes.Add(scene);
        }

        await _settingsStore.SaveAsync(updated, ct);
        return BuildToolJson(new { saved = true, scene });
    }

    private async Task<object> ExecuteCreateAutomationRuleAsync(JsonElement args, CancellationToken ct)
    {
        var name = RequireString(args, "name");
        var triggerRaw = RequireString(args, "trigger_type");
        var triggerType = triggerRaw.Equals("TimeOfDay", StringComparison.OrdinalIgnoreCase)
            ? AutomationTriggerType.TimeOfDay
            : AutomationTriggerType.AppLaunch;

        var rule = new AutomationRule
        {
            Id = "mcp-" + Guid.NewGuid().ToString("N")[..6],
            Name = name,
            IsEnabled = GetBool(args, "enabled") ?? true,
            TriggerType = triggerType,
            ProcessNames = GetString(args, "process_names"),
            ScheduleTime = GetString(args, "schedule_time", "22:00"),
            DaysFilter = GetString(args, "days_filter", "Daily"),
            TargetSceneId = GetString(args, "target_scene_id"),
            CooldownSeconds = 120,
        };

        var updated = _settingsStore.Current.Clone();
        updated.Automations.Add(rule);
        await _settingsStore.SaveAsync(updated, ct);
        return BuildToolJson(new { created = true, rule });
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        var cfg = _settingsStore.Current.Mcp;
        if (!cfg.RequireBearerAuth || string.IsNullOrWhiteSpace(cfg.BearerToken))
        {
            return true;
        }

        var auth = req.Headers["Authorization"] ?? string.Empty;
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var supplied = auth[7..].Trim();
            return string.Equals(supplied, cfg.BearerToken.Trim(), StringComparison.Ordinal);
        }

        return false;
    }

    private static void EnsureFullOrchestration(McpServerSettings cfg, string toolName)
    {
        if (cfg.PermissionTier < McpPermissionTier.FullOrchestration)
        {
            throw new UnauthorizedAccessException($"Tool '{toolName}' requires 'FullOrchestration' permission tier in Denon Desktop Settings.");
        }
    }

    private static int ApplyVolumeCeiling(int requested, int maxSafeVolume, out bool wasCapped)
    {
        var clampedRequested = Math.Clamp(requested, 0, 98);
        var ceiling = Math.Clamp(maxSafeVolume, 15, 98);
        wasCapped = clampedRequested > ceiling;
        return Math.Min(clampedRequested, ceiling);
    }

    private void LogActivity(string method, string detail, string outcome, long durationMs)
    {
        var entry = new McpActivityEntry(DateTimeOffset.UtcNow, method, detail, outcome, durationMs);
        lock (_activityLock)
        {
            _activityLog.Insert(0, entry);
            if (_activityLog.Count > 50)
            {
                _activityLog.RemoveAt(_activityLog.Count - 1);
            }
        }

        ActivityAdded?.Invoke(entry);
    }

    private static string BuildPluginToolName(string pluginId, string actionId) =>
        $"plugin_{Slugify(pluginId)}_{Slugify(actionId)}";

    private static string Slugify(string raw) =>
        NonAlnumRegex().Replace((raw ?? string.Empty).Trim().ToLowerInvariant(), "_").Trim('_');

    private static object Tool(string name, string description, object inputSchema) => new
    {
        name,
        description,
        inputSchema,
    };

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

    private static object BuildToolText(string text) => new
    {
        content = new[] { new { type = "text", text } },
        isError = false,
    };

    private static object BuildToolJson(object data) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonPrettyOptions) } },
        isError = false,
    };

    private static object BuildToolError(string message) => new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true,
    };

    private static object BuildJsonRpcError(object? id, int code, string message) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message },
    };

    private static object? CloneJsonId(JsonElement idEl) => idEl.ValueKind switch
    {
        JsonValueKind.Number => idEl.TryGetInt64(out var l) ? l : idEl.GetDouble(),
        JsonValueKind.String => idEl.GetString(),
        _ => idEl.ToString(),
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

    private static void AddCorsHeaders(HttpListenerResponse resp)
    {
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, Accept, Mcp-Session-Id, MCP-Protocol-Version";
        resp.Headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id";
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse resp, int statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOutOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnumRegex();

    private sealed class McpMethodNotFoundException(string message) : Exception(message);

    private sealed class McpInvalidParamsException(string message) : Exception(message);
}

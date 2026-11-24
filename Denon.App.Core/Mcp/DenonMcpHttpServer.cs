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
    private const string ServerVersion = "1.13.1";
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
    private readonly ReceiverStateStore _stateStore;
    private readonly PluginHostService _pluginHost;
    private readonly RoomIntelligenceService _intelligence;
    private readonly RadioBrowserService _radioBrowser;
    private readonly YouTubeMediaService _youtubeService;
    private readonly LocalAudioRelayServer _relayServer;
    private readonly YouTubeQueueService _queueService;
    private readonly LyricsService _lyricsService;
    private readonly EventTelemetryLog _telemetry;
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
        ReceiverStateStore stateStore,
        PluginHostService pluginHost,
        RoomIntelligenceService intelligence,
        RadioBrowserService radioBrowser,
        YouTubeMediaService youtubeService,
        LocalAudioRelayServer relayServer,
        YouTubeQueueService queueService,
        LyricsService lyricsService,
        EventTelemetryLog telemetry,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _pluginHost = pluginHost;
        _intelligence = intelligence;
        _radioBrowser = radioBrowser;
        _youtubeService = youtubeService;
        _relayServer = relayServer;
        _queueService = queueService;
        _lyricsService = lyricsService;
        _telemetry = telemetry;
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
                    resourceTemplatesCount = 3,
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
                "resources/templates/list" => HandleResourceTemplatesList(),
                "resources/read" => HandleResourceRead(hasParams ? paramsEl : default),
                "resources/subscribe" => new { },
                "resources/unsubscribe" => new { },
                "prompts/list" => HandlePromptsList(),
                "prompts/get" => HandlePromptGet(hasParams ? paramsEl : default),
                "completion/complete" => HandleCompletionComplete(hasParams ? paramsEl : default),
                "logging/setLevel" => new { },
                _ => throw new McpMethodNotFoundException($"Method '{method}' not found."),
            };

            sw.Stop();
            if (method is not ("ping" or "tools/list" or "resources/list" or "resources/templates/list" or "prompts/list" or "completion/complete" or "logging/setLevel"))
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
                resources = new { subscribe = true, listChanged = true },
                prompts = new { listChanged = false },
                completions = new { },
                logging = new { },
            },
            serverInfo = new
            {
                name = ServerName,
                version = ServerVersion,
            },
            instructions =
                "Denon Desktop Living-Room Intelligence MCP Server. Read denon://room/state and denon://memory/habits first to understand the user's Denon AVR, Philips Hue lights, LG webOS Smart TV, Home Assistant entities, active Room Doctor insights, and preferred listening volumes. Use apply_atmosphere for cohesive multi-device moods (it automatically captures an Undo snapshot and enforces MaxSafeVolume). Use tv_computer to visually see (screenshot) and operate the LG webOS TV OS, Web Browser, Magic Remote pointer, and apps in a single unified tool.",
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

    private static object HandleResourceTemplatesList() => new
    {
        resourceTemplates = new object[]
        {
            new
            {
                uriTemplate = "denon://plugins/{pluginId}",
                name = "Plugin Details & Actions",
                description = "Inspect live health, status, capabilities, and actions for a specific plugin (e.g. philips-hue, smart-tv, home-assistant).",
                mimeType = "application/json",
            },
            new
            {
                uriTemplate = "denon://scenes/{sceneId}",
                name = "Smart Scene Configuration",
                description = "Read the complete AVR, acoustic, and chained plugin action configuration for a specific Smart Scene (e.g. movie-night, late-night).",
                mimeType = "application/json",
            },
            new
            {
                uriTemplate = "denon://logs/events/{stream}",
                name = "Smart Event & Habit Telemetry Stream",
                description = "Read recent habit-ready structured events from a telemetry stream (media, avr, tv, lighting, control, all, or plugin:<id>).",
                mimeType = "application/json",
            },
        },
    };

    private object HandleResourceRead(JsonElement paramsEl)
    {
        var uri = GetString(paramsEl, "uri");
        var lower = uri.ToLowerInvariant();
        object payload;

        if (lower == "denon://room/state")
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
                ?? throw new McpInvalidParamsException($"Plugin '{pluginId}' not found.");
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
            var scene = _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s =>
                s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(sceneId, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpInvalidParamsException($"Smart Scene '{sceneId}' not found.");
            payload = scene;
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
            throw new McpInvalidParamsException($"Unknown resource URI '{uri}'.");
        }

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

    private object HandleCompletionComplete(JsonElement paramsEl)
    {
        var argName = string.Empty;
        var argVal = string.Empty;
        if (paramsEl.ValueKind == JsonValueKind.Object &&
            paramsEl.TryGetProperty("argument", out var argEl) &&
            argEl.ValueKind == JsonValueKind.Object)
        {
            argName = GetString(argEl, "name");
            argVal = GetString(argEl, "value");
        }

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
            .Where(c => string.IsNullOrWhiteSpace(argVal) || c.Contains(argVal, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToArray();

        return new
        {
            completion = new
            {
                values,
                total = values.Length,
                hasMore = false,
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
            // Room Intelligence & Diagnostics
            Tool("get_room_state",
                "Get the complete live living-room state graph: Denon AVR, Now Playing, Queue, Philips Hue, LG webOS TV, Home Assistant, Room Doctor Insights, Undo state, and safety ceiling.",
                EmptySchema(),
                title: "Get Live Room State",
                readOnly: true, idempotent: true,
                outputSchema: OutputObjSchema(
                    ("timestamp", "string"),
                    ("avr", "object"),
                    ("queue", "object"),
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
                    ("summary", "string"),
                    ("totalSessions", "integer"),
                    ("preferredVolumeByBucket", "object"))),
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
                "Turn Zone 2 power ON or OFF.",
                ObjSchema(("power_on", "boolean", "true for Zone 2 ON, false for OFF", true)),
                title: "Set Zone 2 Power",
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
                "List all configured Smart Scenes and Automation Rules.",
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
                    ("plugin_id", "string", "Optional plugin ID to chain (e.g. 'philips-hue', 'smart-tv', 'home-assistant')", false),
                    ("plugin_action_id", "string", "Optional plugin action ID to chain (e.g. 'hue.movie_dim', 'tv.screen_off')", false)),
                title: "Create or Update Smart Scene",
                idempotent: true),
            Tool("create_automation_rule",
                "Create a persistent Automation Rule (App Launch Watcher or Time-of-Day Schedule) in settings.json.",
                ObjSchema(
                    ("name", "string", "Friendly rule name", true),
                    ("trigger_type", "string", "'AppLaunch' or 'TimeOfDay'", true),
                    ("enabled", "boolean", "Whether the rule starts enabled (default true)", false),
                    ("process_names", "string", "For AppLaunch: comma-separated process names without .exe (e.g. 'vlc, Plex')", false),
                    ("schedule_time", "string", "For TimeOfDay: 24-hour HH:mm local time (e.g. '22:30')", false),
                    ("days_filter", "string", "Daily, Weekdays, or Weekends (default Daily)", false),
                    ("target_scene_id", "string", "Smart Scene ID to activate when triggered", false)),
                title: "Create Automation Rule"),
        };

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

                tools.Add(Tool(
                    toolName,
                    $"[{plugin.Name}] {action.DisplayName}: {action.Description}",
                    McpPluginToolBridge.BuildInputSchema(action),
                    title: $"{plugin.Name}: {action.DisplayName}",
                    readOnly: isReadOnly,
                    destructive: isDestructive,
                    idempotent: isIdempotent));
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
            {
                var insights = _intelligence.DiagnoseRoom();
                return BuildToolJson(new { count = insights.Count, insights });
            }

            case "get_listening_habits":
                return BuildToolJson(_intelligence.BuildHabitsPayload());

            case "list_scenes_and_rules":
                return BuildToolJson(new
                {
                    scenes = _settingsStore.Current.Receiver.Scenes,
                    automations = _settingsStore.Current.Automations,
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

                    var (isReadOnly, _, _) = McpPluginToolBridge.ResolveHints(action);
                    var needsFullOrchestration = action.IsDestructive == true ||
                                                 action.ActionId.Contains("power_off", StringComparison.OrdinalIgnoreCase);
                    if (!isReadOnly)
                    {
                        if (cfg.PermissionTier == McpPermissionTier.Observer)
                        {
                            throw new UnauthorizedAccessException($"MCP Permission Tier is 'Observer' (read-only). Change to SafeControl or FullOrchestration in Denon Desktop Settings to run '{toolName}'.");
                        }

                        if (needsFullOrchestration && cfg.PermissionTier < McpPermissionTier.FullOrchestration)
                        {
                            throw new UnauthorizedAccessException($"Running '{action.ActionId}' requires 'FullOrchestration' permission tier.");
                        }

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

        throw new McpInvalidParamsException($"Unknown MCP tool '{toolName}'.");
    }

    private async Task<object> ExecuteTvComputerToolAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
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
            if (action == "screenshot" && cfg.PermissionTier == McpPermissionTier.Observer)
            {
                // Keep Observer mode strictly read-only (no cursor wiggle input).
                payloadJson = JsonSerializer.Serialize(new
                {
                    action,
                    show_cursor = false,
                    width = GetInt(args, "width") ?? 960,
                    height = GetInt(args, "height") ?? 540,
                }, JsonOutOptions);
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

        JsonElement structured = default;
        var hasStructured = false;
        var textPayload = res.Message;

        if (!string.IsNullOrWhiteSpace(res.DataJson))
        {
            try
            {
                using var parsedDoc = JsonDocument.Parse(res.DataJson);
                structured = parsedDoc.RootElement.Clone();
                hasStructured = structured.ValueKind == JsonValueKind.Object;
                textPayload = JsonSerializer.Serialize(structured, JsonPrettyOptions);
            }
            catch
            {
                textPayload = res.DataJson;
            }
        }

        var contentBlocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(res.ImageBase64))
        {
            contentBlocks.Add(new
            {
                type = "image",
                data = res.ImageBase64,
                mimeType = string.IsNullOrWhiteSpace(res.ImageMimeType) ? "image/jpeg" : res.ImageMimeType,
            });
        }

        contentBlocks.Add(new
        {
            type = "text",
            text = textPayload,
        });

        if (hasStructured)
        {
            return new
            {
                content = contentBlocks,
                structuredContent = structured,
                isError = false,
            };
        }

        return new
        {
            content = contentBlocks,
            isError = false,
        };
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

    private async Task<object> ExecuteApplyAtmosphereAsync(JsonElement args, McpServerSettings cfg, CancellationToken ct)
    {
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

    private static object Tool(
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
        var def = new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
            ["annotations"] = new
            {
                title = string.IsNullOrWhiteSpace(title) ? name : title,
                readOnlyHint = readOnly,
                destructiveHint = destructive,
                idempotentHint = idempotent,
                openWorldHint = openWorld,
            },
        };

        if (outputSchema is not null)
        {
            def["outputSchema"] = outputSchema;
        }

        return def;
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

    private static object BuildToolText(string text) => new
    {
        content = new[] { new { type = "text", text } },
        isError = false,
    };

    private static object BuildToolJson(object data) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, JsonPrettyOptions) } },
        structuredContent = data,
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

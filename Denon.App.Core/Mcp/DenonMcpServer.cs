using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Denon.App.Core.Mcp;

/// <summary>
/// The Denon Desktop MCP server, on the official MCP C# SDK (ModelContextProtocol.AspNetCore): Streamable HTTP on
/// <c>/mcp</c> (Kestrel, 127.0.0.1, or the LAN when allowed), <c>/health</c> without a token.
/// <list type="bullet">
/// <item>Tools: the curated room tools and every enabled plugin action, in the SDK's tool collection; a plugin change
/// updates the collection and the SDK tells sessions the list changed.</item>
/// <item>Resources, templates, prompts and completion: SDK handlers over the room intelligence.</item>
/// <item>Bearer token (fixed-time compare) and a browser Origin check before the SDK; permission tiers and the
/// safe-volume ceiling inside the tools.</item>
/// </list>
/// Session mode <see cref="HttpServerSessionMode.StatefulForInitializeClients"/>: clients on 2026-07-28 are served without
/// a session (the new protocol: version and client info on every request; questions in a call come back as an
/// input-required result). Clients that still start with <c>initialize</c> (2025-11-25 and older) get a session, so the
/// server can notify them (tool list, room state) and ask them questions in a call. Plain <c>Stateful</c> would hold
/// every client, the SDK's own included, on 2025-11-25.
/// </summary>
public sealed partial class DenonMcpServer : IAsyncDisposable
{
    private const string ServerName = "denon-desktop-mcp";
    private const string RoomStateUri = "denon://room/state";

    private const string Instructions =
        "Denon Desktop Living-Room Intelligence MCP Server. Read denon://room/state and denon://memory/habits first to understand the user's " +
        "Denon AVR, Philips Hue lights, LG webOS Smart TV, Home Assistant entities, active Room Doctor insights, and preferred listening volumes. " +
        "Use apply_atmosphere for cohesive multi-device moods (it automatically captures an Undo snapshot and enforces MaxSafeVolume). " +
        "Use tv_computer to visually see (screenshot) and operate the LG webOS TV OS, Web Browser, Magic Remote pointer, and apps in a single unified tool.";

    private static readonly JsonSerializerOptions JsonOutOptions = new()
    {
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
    private readonly VolumeMemoryStore _volumeMemory;
    private readonly AppLogStore _logStore;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _activityLock = new();
    private readonly List<McpActivityEntry> _activityLog = [];
    private readonly Lock _toolsLock = new();

    // The tools the SDK serves. Changing it (a plugin added, removed or changed) makes the SDK send
    // notifications/tools/list_changed to the sessions.
    private readonly McpServerPrimitiveCollection<McpServerTool> _tools = new();

    // Sessions that subscribed to a resource (resources/subscribe), for notifications/resources/updated.
    private readonly ConcurrentDictionary<McpServer, ConcurrentDictionary<string, byte>> _resourceSubscribers = new();

    private WebApplication? _app;
    private bool _disposed;

    public DenonMcpServer(
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
        VolumeMemoryStore volumeMemory,
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
        _volumeMemory = volumeMemory;
        _logStore = logStore;

        _intelligence.RoomGraphChanged += NotifyRoomStateUpdated;
        _pluginHost.PluginsChanged += SyncTools;
    }

    public event Action? StatusChanged;

    public event Action<McpActivityEntry>? ActivityAdded;

    public bool IsRunning => _app is not null;

    public string ListeningEndpoint { get; private set; } = string.Empty;

    public string? LastError { get; private set; }

    public int RegisteredToolCount
    {
        get
        {
            SyncTools();
            return _tools.Count;
        }
    }

    /// <summary>The port the server listens on now (0 when stopped), for tests that ask for a free port.</summary>
    internal int BoundPort { get; private set; }

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
            await StartCoreLockedAsync(ct);
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
        _intelligence.RoomGraphChanged -= NotifyRoomStateUpdated;
        _pluginHost.PluginsChanged -= SyncTools;
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    // ---------------------------------------------------------------- hosting

    private async Task StartCoreLockedAsync(CancellationToken ct)
    {
        var cfg = _settingsStore.Current.Mcp;
        if (!cfg.Enabled)
        {
            return;
        }

        var port = cfg.Port == 0 ? 0 : Math.Clamp(cfg.Port, 1024, 65535);
        WebApplication? app = null;
        try
        {
            SyncTools();

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(DenonMcpServer).Assembly.GetName().Name,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new AppLogLoggerProvider(_logStore));
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                // Kestrel binds the LAN without the admin URL reservation HttpListener needed.
                if (cfg.AllowLanConnections)
                {
                    kestrel.ListenAnyIP(port);
                }
                else
                {
                    kestrel.Listen(IPAddress.Loopback, port);
                }
            });

            // Browser clients on this PC (the MCP Inspector) send a preflight without the token: CORS answers it for
            // this PC's origins only, before the token check. Other origins get no CORS headers (and 403 below).
            builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .SetIsOriginAllowed(static origin => IsAllowedOrigin(origin))
                .AllowAnyHeader()
                .WithMethods("GET", "POST", "DELETE")
                .WithExposedHeaders("Mcp-Session-Id", "MCP-Protocol-Version")));

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = ServerName, Title = "Denon Desktop", Version = ServerVersion };
                    options.ServerInstructions = Instructions;
                    options.ToolCollection = _tools;
                })
                .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
#pragma warning disable MCPEXP003 // MCP Apps is experimental in the C# SDK: announces io.modelcontextprotocol/ui.
                .WithMcpApps()
#pragma warning restore MCPEXP003
                .WithListResourcesHandler((_, _) => ValueTask.FromResult(ListResources()))
                .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(ListResourceTemplates()))
                .WithReadResourceHandler((request, _) => ValueTask.FromResult(ReadResource(request.Params?.Uri ?? string.Empty)))
                .WithSubscribeToResourcesHandler((request, _) => Subscribe(request.Server, request.Params?.Uri))
                .WithUnsubscribeFromResourcesHandler((request, _) => Unsubscribe(request.Server, request.Params?.Uri))
                .WithListPromptsHandler((_, _) => ValueTask.FromResult(ListPrompts()))
                .WithGetPromptHandler((request, _) => ValueTask.FromResult(GetPrompt(request.Params?.Name ?? string.Empty, request.Params?.Arguments)))
                .WithCompleteHandler((request, _) => ValueTask.FromResult(Complete(request.Params?.Argument)));

            app = builder.Build();
            app.UseCors();
            app.Use(GuardAsync);
            app.MapGet("/health", Health);
            app.MapMcp("/mcp");
            await app.StartAsync(ct);

            _app = app;
            BoundPort = BoundPortOf(app) ?? port;
            var host = cfg.AllowLanConnections
                ? LocalAudioRelayServer.ResolveLocalLanIpForReceiver(_settingsStore.Current.Receiver.Host)
                : "127.0.0.1";
            ListeningEndpoint = $"http://{host}:{BoundPort}/mcp";
            LastError = null;
            _logStore.Info($"MCP server (C# SDK {SdkVersion}) listening on {ListeningEndpoint} ({_tools.Count} tools, tier: {cfg.PermissionTier}).");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ListeningEndpoint = string.Empty;
            BoundPort = 0;
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            _logStore.Warning($"Failed to start the MCP server on port {port}: {ex.Message}");
        }
    }

    private async Task StopCoreLockedAsync()
    {
        _resourceSubscribers.Clear();
        if (_app is { } app)
        {
            _app = null;
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await app.StopAsync(stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }

            await app.DisposeAsync();
        }

        ListeningEndpoint = string.Empty;
        BoundPort = 0;
    }

    /// <summary>The port Kestrel bound (the one asked for, or the free one it chose for port 0).</summary>
    private static int? BoundPortOf(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        return address is not null &&
               Uri.TryCreate(address.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    /// <summary>
    /// The app's version (Denon.App.Desktop, the entry assembly), as clients see it in serverInfo and /health. Not the
    /// Core library's own version, which does not follow the releases.
    /// </summary>
    internal static readonly string ServerVersion =
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(DenonMcpServer).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly string SdkVersion =
        typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "?";

    private IResult Health()
    {
        var cfg = _settingsStore.Current.Mcp;
        return Results.Json(new
        {
            status = "ok",
            server = ServerName,
            version = ServerVersion,
            sdk = SdkVersion,
            endpoint = ListeningEndpoint,
            permissionTier = cfg.PermissionTier.ToString(),
            maxSafeVolume = cfg.MaxSafeVolume,
            toolsCount = _tools.Count,
        });
    }

    /// <summary>
    /// Before the SDK: every request (also /health) must name this PC in its Host header, a browser page on another
    /// site may not call the server (Origin check), and every MCP request needs the Bearer token when the settings ask
    /// for one.
    /// </summary>
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAllowedHost(context.Request.Host.Host, _settingsStore.Current.Mcp.AllowLanConnections))
        {
            LogActivity("auth", $"{context.Request.Method} {context.Request.Path} for host {context.Request.Host}", "Blocked (403)", 0);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden host. Connect to this PC by localhost, 127.0.0.1 or its LAN address." });
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!IsAllowedOrigin(context.Request.Headers.Origin))
        {
            LogActivity("auth", $"{context.Request.Method} /mcp from origin {context.Request.Headers.Origin}", "Blocked (403)", 0);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden origin. Only MCP clients on this PC (or no Origin header) may connect." });
            return;
        }

        if (!IsAuthorized(context.Request.Headers.Authorization))
        {
            LogActivity("auth", $"{context.Request.Method} /mcp from {context.Connection.RemoteIpAddress}", "Blocked (401)", 0);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"DenonDesktopMCP\"";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized. Provide header: Authorization: Bearer <token>" });
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The Host header must name this PC. A DNS-rebinding page reaches 127.0.0.1 under its own host name, so a name
    /// that is not localhost, a loopback address or (with LAN access on) an IP address or this PC's name is refused.
    /// </summary>
    internal static bool IsAllowedHost(string? host, bool allowLan)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            return false;
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(name.Trim('[', ']'), out var address))
        {
            return IPAddress.IsLoopback(address) || (allowLan && IsPrivateOrLinkLocal(address));
        }

        var machine = Environment.MachineName;
        return allowLan &&
               (name.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A LAN address: 10/8, 172.16/12, 192.168/16, 169.254/16, or IPv6 unique-local and link-local.</summary>
    private static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    /// <summary>No Origin (a native client), or a page on this PC (localhost, 127.0.0.1, [::1]).</summary>
    internal static bool IsAllowedOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin) ||
        (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    private bool IsAuthorized(string? authorization)
    {
        var cfg = _settingsStore.Current.Mcp;
        if (!cfg.RequireBearerAuth || string.IsNullOrWhiteSpace(cfg.BearerToken))
        {
            return true;
        }

        var auth = authorization ?? string.Empty;
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(auth[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(cfg.BearerToken.Trim());
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    // ---------------------------------------------------------------- tools

    /// <summary>
    /// Brings the SDK's tool collection in line with <see cref="BuildToolDefinitions"/> (the curated tools and the
    /// enabled plugin actions). Only a real change touches the collection, so sessions hear tools/list_changed once.
    /// </summary>
    private void SyncTools()
    {
        var wanted = BuildToolDefinitions();
        lock (_toolsLock)
        {
            var current = _tools.ToArray().ToDictionary(static t => t.ProtocolTool.Name, StringComparer.Ordinal);
            var wantedByName = wanted.ToDictionary(static t => t.Name, StringComparer.Ordinal);
            if (current.Count == wantedByName.Count &&
                wantedByName.All(w => current.TryGetValue(w.Key, out var have) && SameDefinition(have.ProtocolTool, w.Value)))
            {
                return;
            }

            using var deferred = _tools.DeferChangedEvents();
            foreach (var (name, tool) in current)
            {
                if (!wantedByName.TryGetValue(name, out var want) || !SameDefinition(tool.ProtocolTool, want))
                {
                    _tools.Remove(tool);
                }
            }

            foreach (var tool in wanted)
            {
                if (!_tools.TryGetPrimitive(tool.Name, out _))
                {
                    _tools.Add(new DenonTool(this, tool));
                }
            }
        }
    }

    private static bool SameDefinition(Tool a, Tool b) =>
        JsonSerializer.Serialize(a, McpJsonUtilities.DefaultOptions) == JsonSerializer.Serialize(b, McpJsonUtilities.DefaultOptions);

    /// <summary>One tool call: run it, log it, and turn the app's errors into MCP results or protocol errors.</summary>
    private async ValueTask<CallToolResult> CallToolAsync(string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        var args = arguments is null ? default : JsonSerializer.SerializeToElement(arguments, McpJsonUtilities.DefaultOptions);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await HandleToolCallAsync(toolName, args, ct);
            LogActivity("tools/call", toolName, result.IsError == true ? "Error" : "OK", sw.ElapsedMilliseconds);
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogActivity("tools/call", $"{toolName}: {ex.Message}", "Blocked (Tier)", sw.ElapsedMilliseconds);
            return BuildToolError(ex.Message);
        }
        catch (McpInvalidParamsException ex)
        {
            LogActivity("tools/call", $"{toolName}: {ex.Message}", "Invalid Params", sw.ElapsedMilliseconds);
            throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or McpException))
        {
            LogActivity("tools/call", $"{toolName}: {ex.Message}", "Error", sw.ElapsedMilliseconds);
            return BuildToolError(ex.Message);
        }
    }

    /// <summary>A Denon Desktop tool in the SDK's collection: its definition, and the call goes to the server.</summary>
    private sealed class DenonTool(DenonMcpServer owner, Tool protocolTool) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = protocolTool;

        public override IReadOnlyList<object> Metadata { get; } = [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            owner.CallToolAsync(ProtocolTool.Name, request.Params?.Arguments, cancellationToken);
    }

    // ---------------------------------------------------------------- resource updates

    private ValueTask<EmptyResult> Subscribe(McpServer session, string? uri)
    {
        if (!string.IsNullOrWhiteSpace(uri))
        {
            _resourceSubscribers.GetOrAdd(session, static _ => new(StringComparer.OrdinalIgnoreCase))[uri] = 0;
            LogActivity("resources/subscribe", uri, "OK", 0);
        }

        return ValueTask.FromResult(new EmptyResult());
    }

    private ValueTask<EmptyResult> Unsubscribe(McpServer session, string? uri)
    {
        if (!string.IsNullOrWhiteSpace(uri) && _resourceSubscribers.TryGetValue(session, out var uris))
        {
            uris.TryRemove(uri, out _);
            if (uris.IsEmpty)
            {
                // Forget the session with its last subscription (only if no new one came in meanwhile).
                _resourceSubscribers.TryRemove(new KeyValuePair<McpServer, ConcurrentDictionary<string, byte>>(session, uris));
            }
        }

        return ValueTask.FromResult(new EmptyResult());
    }

    /// <summary>Tells the sessions that subscribed to the room state that it changed. A closed session is forgotten.</summary>
    private void NotifyRoomStateUpdated()
    {
        foreach (var (session, uris) in _resourceSubscribers)
        {
            if (!uris.ContainsKey(RoomStateUri))
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await session.SendNotificationAsync(
                        NotificationMethods.ResourceUpdatedNotification,
                        new ResourceUpdatedNotificationParams { Uri = RoomStateUri });
                }
                catch (Exception)
                {
                    _resourceSubscribers.TryRemove(session, out _);
                }
            });
        }
    }

    // ---------------------------------------------------------------- activity

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

    /// <summary>The SDK's and Kestrel's warnings go to the app log; the rest is noise.</summary>
    private sealed class AppLogLoggerProvider(AppLogStore log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new AppLogLogger(log, categoryName);

        public void Dispose()
        {
        }

        private sealed class AppLogLogger(AppLogStore log, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var source = category.Split('.').LastOrDefault() ?? category;
                var message = $"[MCP] {source}: {formatter(state, exception)}";
                if (logLevel >= LogLevel.Error && exception is not null)
                {
                    log.Error(message, exception);
                }
                else
                {
                    log.Warning(message);
                }
            }
        }
    }
}

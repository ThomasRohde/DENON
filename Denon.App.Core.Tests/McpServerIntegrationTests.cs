using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The MCP server on the official C# SDK, end to end: the SDK's own client connects over Streamable HTTP on
/// 127.0.0.1 with the Bearer token and lists and calls tools, reads resources and gets prompts. No receiver is
/// needed: the tools used here only read the app's state.
/// </summary>
public sealed class McpServerIntegrationTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private readonly List<IDisposable> _disposables = [];
    private AppSettingsStore _settings = null!;
    private DenonMcpServer _server = null!;
    private string _token = string.Empty;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        _settings = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await _settings.InitializeAsync();
        _token = "ddn_test_" + Guid.NewGuid().ToString("N");
        await _settings.UpdateAsync(s =>
        {
            s.Mcp.Enabled = true;
            s.Mcp.Port = FreePort();
            s.Mcp.RequireBearerAuth = true;
            s.Mcp.BearerToken = _token;
            s.Mcp.PermissionTier = McpPermissionTier.FullOrchestration;
        });

        var bus = new RoomEventBus(log);
        var telemetry = new EventTelemetryLog(_settings, log, bus, Path.Combine(_tempDir, "events"));
        var state = new ReceiverStateStore();
        var controller = new ReceiverController(new HeosClient(), new DenonAvrClient(), _settings, state, telemetry, log);
        var ytMedia = new YouTubeMediaService(_settings, log);
        var relay = new LocalAudioRelayServer(ytMedia, _settings, log);
        var ytQueue = new YouTubeQueueService(relay, controller, state, log);
        var plugins = new PluginHostService([], _settings, state, controller, telemetry, log);
        var heos = new HeosEventListenerService(_settings, state, controller, log);
        var intelligence = new RoomIntelligenceService(state, controller, _settings, plugins, ytQueue, heos, log, Path.Combine(_tempDir, "room-habits.json"));
        _server = new DenonMcpServer(_settings, controller, state, plugins, intelligence, new RadioBrowserService(log), ytMedia, relay, ytQueue, new LyricsService(), telemetry, log);

        _asyncDisposables.Add(_server);
        _asyncDisposables.Add(telemetry);
        _disposables.AddRange([ytQueue, relay, bus]);

        await _server.ApplySettingsAsync();
        Assert.True(_server.IsRunning, _server.LastError);
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _asyncDisposables)
        {
            await d.DisposeAsync();
        }

        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Uri Endpoint => new(_server.ListeningEndpoint);

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" },
        }));

    [Fact]
    public async Task The_sdk_client_lists_and_calls_tools_reads_resources_and_gets_prompts()
    {
        await using var client = await ConnectAsync();
        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_room_state");
        var volume = Assert.Single(tools, t => t.Name == "avr_set_volume");
        Assert.Equal("object", volume.ProtocolTool.InputSchema.GetProperty("type").GetString());

        var habits = await client.CallToolAsync("get_listening_habits");
        Assert.NotEqual(true, habits.IsError);
        Assert.Equal(JsonValueKind.Object, habits.StructuredContent?.ValueKind);
        Assert.IsType<TextContentBlock>(Assert.Single(habits.Content));

        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "denon://room/state");
        var catalog = await client.ReadResourceAsync("denon://catalog/capabilities");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(catalog.Contents));
        using (JsonDocument.Parse(text.Text))
        {
        }

        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("denon://nothing").AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);

        var prompt = await client.GetPromptAsync("design_atmosphere", new Dictionary<string, object?> { ["vibe"] = "rainy jazz" });
        var message = Assert.Single(prompt.Messages);
        Assert.Contains("rainy jazz", Assert.IsType<TextContentBlock>(message.Content).Text);

        Assert.Contains(_server.GetRecentActivity(), a => a.Method == "tools/call" && a.Detail == "get_listening_habits" && a.Outcome == "OK");
    }

    [Fact]
    public async Task A_client_on_the_older_protocol_still_works_with_a_session()
    {
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_token}" },
            }),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });

        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
        Assert.Contains(await client.ListToolsAsync(), t => t.Name == "get_room_state");
        Assert.NotEqual(true, (await client.CallToolAsync("diagnose_room")).IsError);
    }

    [Fact]
    public async Task The_permission_tier_still_guards_the_tools()
    {
        await _settings.UpdateAsync(s => s.Mcp.PermissionTier = McpPermissionTier.Observer);
        await using var client = await ConnectAsync();

        var blocked = await client.CallToolAsync("avr_set_volume", new Dictionary<string, object?> { ["volume"] = 30 });

        Assert.True(blocked.IsError);
        Assert.Contains("Observer", Assert.IsType<TextContentBlock>(Assert.Single(blocked.Content)).Text);
    }

    [Fact]
    public async Task Without_the_token_or_from_another_site_the_server_says_no()
    {
        using var http = new HttpClient();
        var health = await http.GetAsync(new Uri(Endpoint, "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode); // no token needed

        HttpRequestMessage Initialize(string? token, string? origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize("ddn_wrong", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Initialize(_token, "https://evil.example"))).StatusCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:6274", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("null", false)]
    public void Origins_from_this_pc_only(string? origin, bool allowed) =>
        Assert.Equal(allowed, DenonMcpServer.IsAllowedOrigin(origin));
}

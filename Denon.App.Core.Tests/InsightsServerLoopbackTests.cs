using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Heos;
using Denon.App.Core.Mcp;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Xunit;

namespace Denon.App.Core.Tests;

/// <summary>
/// The Insights server and the automation editor API that it serves, end to end on 127.0.0.1: the key in the address
/// becomes a cookie, a request without the key is refused, changes need the editor header and this server's own
/// Origin, and a body over 512 KiB is refused. No receiver is needed.
/// </summary>
public sealed class InsightsServerLoopbackTests : IAsyncLifetime
{
    private const string CookieName = "denon_insights";
    private const int MaxBodyBytes = 512 * 1024;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "denon-insights-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private readonly List<IDisposable> _disposables = [];
    private AppSettingsStore _settings = null!;
    private InsightsServer _server = null!;
    private HttpClient _http = null!;
    private Uri _base = null!;
    private string _key = string.Empty;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var log = new AppLogStore(Path.Combine(_tempDir, "app.log"));
        _settings = new AppSettingsStore(log, Path.Combine(_tempDir, "settings.json"));
        await _settings.InitializeAsync();
        await _settings.UpdateAsync(s => s.AutomationGraphs = []);

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
        var engine = new AutomationEngineService(_settings, controller, state, plugins, telemetry, bus, log);
        var editor = new AutomationEditorApi(_settings, engine, plugins, relay, state);
        _server = new InsightsServer(telemetry, intelligence, editor, log);

        _asyncDisposables.AddRange([_server, plugins, telemetry]);
        _disposables.AddRange([engine, heos, ytQueue, relay, bus]);

        var withKey = new Uri(await _server.StartAsync());
        _key = withKey.Query["?key=".Length..];
        _base = new Uri(_server.Address!);
        Assert.Equal(IPAddress.Loopback.ToString(), _base.Host);

        // No cookie jar and no redirects: each test sends (or leaves out) the key cookie itself and sees each answer.
        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = _base,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _disposables.Insert(0, _http);
    }

    public async Task DisposeAsync()
    {
        // The server first, so no request is still running when the services under it go.
        foreach (var d in _asyncDisposables)
        {
            await d.DisposeAsync();
        }

        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string Origin => $"{_base.Scheme}://{_base.Authority}";

    private static AutomationGraph WebhookGraph(string id) =>
        new()
        {
            Id = id,
            Name = "Arrive home",
            Enabled = false,
            CooldownSeconds = 0,
            Nodes =
            [
                new() { Id = "t", Type = AutomationNodeTypes.TriggerWebhook, Config = new() { ["hook"] = "arrive-home" } },
                new() { Id = "a", Type = AutomationNodeTypes.ActionScene, Config = new() { ["sceneId"] = "movie-night" } },
            ],
            Edges = [new() { From = "t", Port = AutomationNodeTypes.PortOut, To = "a" }],
        };

    /// <summary>A request with the key cookie, the editor header and this server's Origin, unless a test leaves one out.</summary>
    private HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? cookie = "", bool editorHeader = true, string? origin = "")
    {
        var request = new HttpRequestMessage(method, path);
        cookie = cookie == string.Empty ? _key : cookie;
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        }

        if (editorHeader)
        {
            request.Headers.Add("X-Denon-Request", "1");
        }

        origin = origin == string.Empty ? Origin : origin;
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    [Fact]
    public async Task The_key_in_the_address_becomes_a_strict_cookie()
    {
        using var response = await _http.GetAsync($"/?key={_key}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{CookieName}={_key};", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
    }

    [Fact]
    public async Task A_wrong_key_in_the_address_is_refused()
    {
        using var response = await _http.GetAsync($"/?key={new string('0', _key.Length)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-key")]
    public async Task A_request_without_the_token_is_refused(string? cookie)
    {
        using var read = await _http.SendAsync(Request(HttpMethod.Get, "/api/automations", cookie: cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal("locked", await ErrorOf(read));

        using var page = await _http.SendAsync(Request(HttpMethod.Get, "/automations/", cookie: cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);

        var body = JsonSerializer.Serialize(WebhookGraph("locked-out"), WebJson);
        using var change = await _http.SendAsync(Request(HttpMethod.Put, "/api/automations/locked-out", body, cookie: cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        Assert.Empty(_settings.Current.AutomationGraphs);
    }

    [Fact]
    public async Task A_request_for_another_host_name_is_refused()
    {
        // DNS rebinding: a page on another name that resolves to 127.0.0.1 sends its own name in Host. The key cookie
        // is valid, but the server answers only for the name 127.0.0.1.
        using var request = Request(HttpMethod.Get, "/api/automations");
        request.Headers.Host = $"localhost:{_base.Port}";

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_change_without_the_editor_header_is_refused()
    {
        var body = JsonSerializer.Serialize(WebhookGraph("no-header"), WebJson);

        using var response = await _http.SendAsync(Request(HttpMethod.Put, "/api/automations/no-header", body, editorHeader: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("X-Denon-Request", await ErrorOf(response));
        Assert.Empty(_settings.Current.AutomationGraphs);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task A_change_from_a_foreign_origin_is_refused(string? origin)
    {
        await _settings.UpdateAsync(s => s.AutomationGraphs = [WebhookGraph("kept")]);
        var body = JsonSerializer.Serialize(WebhookGraph("foreign"), WebJson);

        using var save = await _http.SendAsync(Request(HttpMethod.Put, "/api/automations/foreign", body, origin: origin));
        using var otherPort = await _http.SendAsync(Request(HttpMethod.Put, "/api/automations/foreign", body, origin: $"http://127.0.0.1:{(_base.Port == 1 ? 2 : 1)}"));
        using var delete = await _http.SendAsync(Request(HttpMethod.Delete, "/api/automations/kept", origin: origin));
        using var enable = await _http.SendAsync(Request(HttpMethod.Post, "/api/automations/kept/enabled", "{\"enabled\":true}", origin: origin));

        Assert.All([save, otherPort, delete, enable], r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        var kept = Assert.Single(_settings.Current.AutomationGraphs);
        Assert.Equal("kept", kept.Id);
        Assert.False(kept.Enabled);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read()
    {
        // Raw socket: the headers announce one byte over the cap and no body follows. If the server read the body it
        // would wait for bytes that never come; it answers 413 at once instead.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, _base.Port, timeout.Token);
        var stream = socket.GetStream();
        var head =
            "PUT /api/automations/too-big HTTP/1.1\r\n" +
            $"Host: {_base.Authority}\r\n" +
            $"Cookie: {CookieName}={_key}\r\n" +
            "X-Denon-Request: 1\r\n" +
            $"Origin: {Origin}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {MaxBodyBytes + 1}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);

        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            Assert.True(read > 0, "The server closed the connection without an answer.");
            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        Assert.StartsWith("HTTP/1.1 413 ", received.ToString());
        Assert.Empty(_settings.Current.AutomationGraphs);
    }

    [Theory]
    [InlineData('x', 1)] // ASCII: over the cap in characters and bytes
    [InlineData('€', 3)] // three UTF-8 bytes each: under the cap in characters, over it in bytes
    public async Task A_chunked_body_over_the_cap_is_refused(char pad, int bytesPerChar)
    {
        // No Content-Length: the server reads at most the cap plus one byte, then stops.
        var graph = JsonSerializer.Serialize(WebhookGraph("too-big"), WebJson);
        var padding = new string(pad, MaxBodyBytes / bytesPerChar + 1);
        var body = graph.Insert(graph.Length - 1, $",\"padding\":\"{padding}\"");
        Assert.True(Encoding.UTF8.GetByteCount(body) > MaxBodyBytes);
        using var request = Request(HttpMethod.Put, "/api/automations/too-big", body);
        request.Headers.TransferEncodingChunked = true;

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("too large", await ErrorOf(response));
        Assert.Empty(_settings.Current.AutomationGraphs);
    }

    [Theory]
    [InlineData("POST", "/api/report")]
    [InlineData("DELETE", "/")]
    [InlineData("PATCH", "/api/automations/x")]
    public async Task Only_the_editor_api_accepts_changes(string method, string path)
    {
        using var response = await _http.SendAsync(Request(new HttpMethod(method), path, "{}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET", string.Join(",", response.Content.Headers.Allow));
    }

    [Theory]
    [InlineData("/icon.png", "image/png")]
    [InlineData("/fonts/share-tech-mono.ttf", "font/ttf")]
    public async Task The_icon_and_the_font_come_from_the_app_resources(string path, string type)
    {
        // WPF resources, also when the app is not the entry assembly (here: the test host).
        using var response = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_chunked_body_with_multibyte_text_is_saved_whole(bool byteOrderMark)
    {
        var graph = WebhookGraph("ignored-id");
        graph.Name = "Køkken € 🎵";
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(graph, WebJson));
        using var request = Request(HttpMethod.Put, "/api/automations/kitchen", "{}");
        request.Content = new ByteArrayContent(byteOrderMark ? [.. Encoding.UTF8.Preamble, .. json] : json);
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.TransferEncodingChunked = true;

        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Køkken € 🎵", Assert.Single(_settings.Current.AutomationGraphs).Name);
    }

    [Fact]
    public async Task The_editor_saves_lists_and_deletes_an_automation()
    {
        var body = JsonSerializer.Serialize(WebhookGraph("ignored-id"), WebJson);

        using var save = await _http.SendAsync(Request(HttpMethod.Put, "/api/automations/arrive-home", body));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = Assert.Single(_settings.Current.AutomationGraphs);
        Assert.Equal("arrive-home", saved.Id); // the address names the graph, not the body
        Assert.Equal("Arrive home", saved.Name);

        // Reads need only the cookie: no editor header and no Origin.
        using var list = await _http.SendAsync(Request(HttpMethod.Get, "/api/automations", editorHeader: false, origin: null));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.StartsWith("application/json", list.Content.Headers.ContentType?.ToString());
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var graph = Assert.Single(doc.RootElement.GetProperty("graphs").EnumerateArray());
            Assert.Equal("arrive-home", graph.GetProperty("id").GetString());
        }

        using var delete = await _http.SendAsync(Request(HttpMethod.Delete, "/api/automations/arrive-home"));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Empty(_settings.Current.AutomationGraphs);
    }
}

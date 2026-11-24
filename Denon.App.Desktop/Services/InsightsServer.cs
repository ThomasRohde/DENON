using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using System.Windows.Threading;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Insights;
using Denon.App.Core.Mcp;
using Denon.App.Core.Services;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Infrastructure.Theming;

namespace Denon.App.Desktop.Services;

/// <summary>
/// The Insights page: a small web server inside the app that only this PC can reach (127.0.0.1, a free port, a new
/// key each start). Settings → Open Insights starts it and opens the browser. The page reads the habit event log
/// (<c>/api/report</c>, <c>/api/activity</c>), the room right now (<c>/api/live</c>) and the app's current theme
/// (<c>/theme.css</c>). It runs until the app exits.
/// </summary>
public sealed class InsightsServer : IAsyncDisposable
{
    private const string CookieName = "denon_insights";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] IconKeys =
        ["IconPower", "IconPlay", "IconPause", "IconMusicNote", "IconRadio", "IconNavCast", "IconNavMcp", "IconNavYouTube", "IconRefresh", "IconNavHealth", "IconCheck"];

    private readonly EventTelemetryLog _telemetry;
    private readonly RoomIntelligenceService _intelligence;
    private readonly AppLogStore _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<string, (byte[] Body, string Type)> _assets = new(StringComparer.OrdinalIgnoreCase);

    private Dispatcher? _dispatcher;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _key = string.Empty;
    private (string Stamp, string Css) _theme = (string.Empty, string.Empty);
    private string _icons = "{}";
    private (DateTime ReadUtc, IReadOnlyDictionary<string, string> Map)? _logoIndex;

    public InsightsServer(EventTelemetryLog telemetry, RoomIntelligenceService intelligence, AppLogStore log)
    {
        _telemetry = telemetry;
        _intelligence = intelligence;
        _log = log;
    }

    /// <summary>The page address, without the key (for display), or null while the server is off.</summary>
    public string? Address { get; private set; }

    /// <summary>
    /// Starts the server if it is off (call on the UI thread) and returns the address that opens the page with this
    /// run's key. The page then keeps the key in a cookie that only this server sees.
    /// </summary>
    public async Task<string> StartAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (_listener is { IsListening: true } && Address is not null)
            {
                return $"{Address}?key={_key}";
            }

            _dispatcher = Dispatcher.CurrentDispatcher;
            LoadAssets();
            _theme = (ThemeCss.Stamp, ThemeCss.Build());
            _icons = BuildIcons();

            Exception? lastError = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = FreeLoopbackPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException exception)
                {
                    lastError = exception;
                    listener.Close();
                    continue;
                }

                _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                _listener = listener;
                _cts = new CancellationTokenSource();
                Address = $"http://127.0.0.1:{port}/";
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoopAsync(listener, token), token);
                _log.Info($"Insights page on {Address} (this PC only).");
                return $"{Address}?key={_key}";
            }

            throw new InvalidOperationException($"No free port for the Insights page: {lastError?.Message}");
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _listener = null;
        Address = null;
        _cts?.Dispose();
        _cts = null;
        _startGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException exception)
            {
                _log.Warning($"Insights page: {exception.Message}");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            // Only this PC, by its loopback name (http.sys already refuses other host names: no DNS rebinding).
            if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) || request.Url is not { Host: "127.0.0.1" } url)
            {
                await WriteAsync(response, 403, "text/plain; charset=utf-8", "Forbidden");
                return;
            }

            if (request.HttpMethod != "GET")
            {
                // No body: a HEAD response must not carry one.
                response.StatusCode = 405;
                response.Headers["Allow"] = "GET";
                response.Close();
                return;
            }

            var path = url.AbsolutePath;

            // The address the app opens carries the key: keep it in a cookie and drop it from the address.
            if (request.QueryString["key"] is { } key)
            {
                if (FixedTimeEquals(key, _key))
                {
                    response.Headers.Add("Set-Cookie", $"{CookieName}={_key}; Path=/; HttpOnly; SameSite=Strict");
                    response.Redirect("/");
                    response.Close();
                    return;
                }

                await WriteAsync(response, 401, "text/html; charset=utf-8", LockedPage);
                return;
            }

            if (_assets.TryGetValue(path, out var asset) && path != "/")
            {
                response.Headers["Cache-Control"] = "no-cache";
                await WriteAsync(response, 200, asset.Type, asset.Body);
                return;
            }

            // The theme is not private: the locked page uses it too.
            if (path == "/theme.css")
            {
                await WriteAsync(response, 200, "text/css; charset=utf-8", await ThemeAsync());
                return;
            }

            var authorized = request.Cookies[CookieName]?.Value is { } cookie && FixedTimeEquals(cookie, _key);
            if (!authorized)
            {
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(response, new { error = "locked" }, 401);
                }
                else
                {
                    await WriteAsync(response, 401, "text/html; charset=utf-8", LockedPage);
                }

                return;
            }

            switch (path)
            {
                case "/":
                    await WriteAsync(response, 200, "text/html; charset=utf-8", _assets["/"].Body);
                    break;

                case "/icons.json":
                    await WriteAsync(response, 200, "application/json; charset=utf-8", _icons);
                    break;

                case "/api/report":
                {
                    var days = Math.Clamp(int.TryParse(request.QueryString["days"], out var d) ? d : 7, 1, 90);
                    await WriteJsonAsync(response, BuildReport(days));
                    break;
                }

                case "/api/activity":
                {
                    var hours = Math.Clamp(int.TryParse(request.QueryString["hours"], out var h) ? h : 24, 1, 24 * 90);
                    var limit = Math.Clamp(int.TryParse(request.QueryString["limit"], out var l) ? l : 200, 1, 500);
                    var stream = request.QueryString["stream"] is { Length: > 0 } s ? s : "all";
                    var events = _telemetry.QueryRecentEvents(stream, hours, limit);
                    await WriteJsonAsync(response, new { stream, hours, items = ActivityFeed.Describe(events, ChannelLogos()) });
                    break;
                }

                case "/api/live":
                    await WriteJsonAsync(response, await BuildLiveAsync());
                    break;

                case var logoPath when logoPath.StartsWith("/logo/", StringComparison.Ordinal):
                    await WriteLogoAsync(response, logoPath["/logo/".Length..]);
                    break;

                default:
                    await WriteAsync(response, 404, "text/plain; charset=utf-8", "Not found");
                    break;
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The browser left.
        }
        catch (Exception exception)
        {
            _log.Warning($"Insights page: {request.Url?.AbsolutePath}: {exception.Message}");
            try
            {
                await WriteJsonAsync(response, new { error = exception.Message }, 500);
            }
            catch
            {
                // Headers already sent or the stream broke: drop the connection rather than leave it open.
                response.Abort();
            }
        }
    }

    /// <summary>
    /// A channel logo from the Smart TV plugin's cache (<c>channel-logos</c> in its data folder). Only plain file names
    /// (<see cref="InsightsReport.LogoName"/>) of files in that folder.
    /// </summary>
    private static async Task WriteLogoAsync(HttpListenerResponse response, string name)
    {
        var file = InsightsReport.LogoName(Uri.UnescapeDataString(name));
        var path = file is null ? null : Path.Combine(AppDataPaths.GetPluginDataDirectory("smart-tv"), "channel-logos", file);
        if (path is null || !File.Exists(path))
        {
            await WriteAsync(response, 404, "text/plain; charset=utf-8", "Not found");
            return;
        }

        var type = Path.GetExtension(path) switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png",
        };
        response.Headers["Cache-Control"] = "private, max-age=86400";
        await WriteAsync(response, 200, type, await File.ReadAllBytesAsync(path));
    }

    private InsightsReport.Report BuildReport(int days)
    {
        var now = DateTimeOffset.Now;
        // The period and the one before it, for the comparison.
        var since = WatchTimeReport.ReadFrom(now, 2 * days);
        var events = _telemetry.ListAvailableStreams().SelectMany(s => _telemetry.ReadEventsSince(s, since));
        return InsightsReport.Build(events, now, days, ChannelLogos());
    }

    /// <summary>
    /// Channel name → cached logo file, from the Smart TV plugin's logo index (<c>channel-logos/index.json</c>), so Live
    /// TV sessions logged before their logo was cached show it too. Read at most once a minute.
    /// </summary>
    private Func<string, string?> ChannelLogos()
    {
        var now = DateTime.UtcNow;
        if (_logoIndex is not { } index || now - index.ReadUtc > TimeSpan.FromMinutes(1))
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(AppDataPaths.GetPluginDataDirectory("smart-tv"), "channel-logos", "index.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (doc.RootElement.TryGetProperty("Channels", out var channels) && channels.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var channel in channels.EnumerateObject())
                        {
                            if (channel.Value.ValueKind == JsonValueKind.String && channel.Value.GetString() is { Length: > 0 } file)
                            {
                                map[channel.Name] = file;
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.Warning($"Insights page: the channel logo index could not be read: {exception.Message}");
            }

            index = (now, map);
            _logoIndex = index;
        }

        var lookup = index.Map;
        return name => lookup.TryGetValue(name, out var file) ? file : null;
    }

    private async Task<object> BuildLiveAsync()
    {
        var stats = _telemetry.GetStats();
        await ThemeAsync();
        return new
        {
            app = AppInfo.Version,
            theme = _theme.Stamp,
            room = _intelligence.BuildRoomStatePayload(),
            habits = _intelligence.BuildHabitsPayload(),
            logging = new
            {
                enabled = stats.Enabled,
                files = stats.TotalFiles,
                bytes = stats.TotalBytes,
                retentionDays = stats.RetentionDays,
                dwellSeconds = stats.MediaDwellSeconds,
                eventsThisSession = stats.EventsRecordedThisSession,
                streams = stats.Streams.Select(s => s.Stream).ToArray(),
            },
        };
    }

    /// <summary>The theme CSS, built again on the UI thread after a theme or skin switch.</summary>
    private async Task<string> ThemeAsync()
    {
        if (_dispatcher is null)
        {
            return _theme.Css;
        }

        var current = await _dispatcher.InvokeAsync(() =>
            ThemeCss.Stamp == _theme.Stamp ? _theme : (Stamp: ThemeCss.Stamp, Css: ThemeCss.Build()));
        _theme = current;
        return current.Css;
    }

    private void LoadAssets()
    {
        if (_assets.Count > 0)
        {
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var (path, name, type) in new[]
                 {
                     ("/", "Insights.index.html", "text/html; charset=utf-8"),
                     ("/insights.css", "Insights.insights.css", "text/css; charset=utf-8"),
                     ("/insights.js", "Insights.insights.js", "text/javascript; charset=utf-8"),
                 })
        {
            using var stream = assembly.GetManifestResourceStream(name)
                               ?? throw new InvalidOperationException($"Missing Insights asset {name}.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            _assets[path] = (memory.ToArray(), type);
        }

        foreach (var (path, pack, type) in new[]
                 {
                     ("/icon.png", "pack://application:,,,/denon_icon.png", "image/png"),
                     ("/fonts/share-tech-mono.ttf", "pack://application:,,,/Themes/Fonts/ShareTechMono-Regular.ttf", "font/ttf"),
                 })
        {
            if (Application.GetResourceStream(new Uri(pack)) is { } resource)
            {
                using var memory = new MemoryStream();
                resource.Stream.CopyTo(memory);
                _assets[path] = (memory.ToArray(), type);
            }
        }
    }

    /// <summary>The app's icons that the page uses, as SVG path data (24 × 24).</summary>
    private static string BuildIcons()
    {
        var icons = new Dictionary<string, string>();
        foreach (var key in IconKeys)
        {
            if (Application.Current?.TryFindResource(key) is Geometry geometry)
            {
                icons[key] = geometry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return JsonSerializer.Serialize(icons);
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        a.Length == b.Length && b.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static Task WriteJsonAsync(HttpListenerResponse response, object value, int status = 200) =>
        WriteAsync(response, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static Task WriteAsync(HttpListenerResponse response, int status, string type, string body) =>
        WriteAsync(response, status, type, Encoding.UTF8.GetBytes(body));

    private static async Task WriteAsync(HttpListenerResponse response, int status, string type, byte[] body)
    {
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private const string LockedPage = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Denon Insights</title>
        <link rel="stylesheet" href="/theme.css"><link rel="stylesheet" href="/insights.css"></head>
        <body class="locked"><main><h1>Open Insights from the app</h1>
        <p>This page needs the key that Denon Desktop gives it. In the app, go to Settings and click Open Insights.
        A new key is made each time the app starts.</p></main></body></html>
        """;
}

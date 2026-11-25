using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Services;
using Denon.App.Core.Plugins;

namespace Denon.App.Desktop.Services;

/// <summary>
/// The JSON API of the automation node editor (docs/automation-graphs.md, "Editor API"), served by
/// <see cref="InsightsServer"/> on 127.0.0.1 behind its key cookie. Requests that change something also need the
/// header <c>X-Denon-Request: 1</c>: a page on another site cannot send a custom header to this server without a
/// CORS preflight, which it never answers, and the key cookie is SameSite=Strict anyway.
/// </summary>
public sealed class AutomationEditorApi
{
    private const int MaxBodyBytes = 512 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly AppSettingsStore _settingsStore;
    private readonly AutomationEngineService _engine;
    private readonly PluginHostService _pluginHost;
    private readonly LocalAudioRelayServer _relay;

    public AutomationEditorApi(
        AppSettingsStore settingsStore,
        AutomationEngineService engine,
        PluginHostService pluginHost,
        LocalAudioRelayServer relay)
    {
        _settingsStore = settingsStore;
        _engine = engine;
        _pluginHost = pluginHost;
        _relay = relay;
    }

    /// <summary>True for paths this API answers.</summary>
    public static bool Owns(string path) =>
        path == "/api/automations" || path.StartsWith("/api/automations/", StringComparison.Ordinal);

    /// <summary>Handles one request and returns the status and the JSON value to send.</summary>
    public async Task<(int Status, object Body)> HandleAsync(HttpListenerRequest request, string path, CancellationToken ct)
    {
        var method = request.HttpMethod;
        var rest = path == "/api/automations" ? string.Empty : path["/api/automations/".Length..];

        if (method != "GET")
        {
            if (request.Headers["X-Denon-Request"] != "1")
            {
                return (403, new { error = "Send the header X-Denon-Request: 1 with requests that change something." });
            }

            // Browsers (WebView2 too) send Origin on PUT, POST and DELETE: only this server's own pages may change
            // automations, and a request without an Origin is refused.
            var origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin) ||
                !string.Equals(origin.TrimEnd('/'), $"{request.Url!.Scheme}://{request.Url.Authority}", StringComparison.OrdinalIgnoreCase))
            {
                return (403, new { error = "Changes are only accepted from the automation editor page." });
            }
        }

        try
        {
            return await RouteAsync(request, method, rest, ct);
        }
        catch (InvalidDataException ex)
        {
            return (413, new { error = ex.Message });
        }
    }

    private async Task<(int Status, object Body)> RouteAsync(HttpListenerRequest request, string method, string rest, CancellationToken ct)
    {
        switch (method, rest)
        {
            case ("GET", ""):
                return (200, new
                {
                    graphs = _settingsStore.Current.AutomationGraphs,
                    runs = _engine.GetLastRuns(),
                    paused = _engine.EventTriggersPaused,
                });

            case ("GET", "catalog"):
                return (200, BuildCatalog());

            case ("GET", "events"):
            {
                var limit = Math.Clamp(int.TryParse(request.QueryString["limit"], out var l) ? l : 30, 1, 200);
                var events = _engine.GetRecentEvents(limit).Select(static e => new
                {
                    ts = e.Timestamp,
                    stream = e.Stream,
                    device = e.Device,
                    @event = e.Event,
                    origin = e.Origin,
                    data = e.Data,
                });
                return (200, new { events });
            }
        }

        var slash = rest.IndexOf('/');
        var id = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
        var action = slash < 0 ? string.Empty : rest[(slash + 1)..];
        if (id.Length == 0)
        {
            return (404, new { error = "Not found" });
        }

        switch (method, action)
        {
            case ("PUT", ""):
                return await SaveAsync(id, await ReadBodyAsync(request, ct), ct);

            case ("DELETE", ""):
            {
                var updated = _settingsStore.Current.Clone();
                if (updated.AutomationGraphs.RemoveAll(g => g.Id == id) == 0)
                {
                    return (404, new { error = $"No automation '{id}'." });
                }

                await _settingsStore.SaveAsync(updated, ct);
                return (200, new { deleted = id });
            }

            case ("POST", "test"):
                return await TestAsync(id, await ReadBodyAsync(request, ct), ct);

            default:
                return (404, new { error = "Not found" });
        }
    }

    private async Task<(int, object)> SaveAsync(string id, string body, CancellationToken ct)
    {
        AutomationGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<AutomationGraph>(body, ReadOptions);
        }
        catch (JsonException ex)
        {
            return (400, new { error = $"Not a valid automation: {ex.Message}", problems = Array.Empty<object>() });
        }

        if (graph is null)
        {
            return (400, new { error = "The body is empty.", problems = Array.Empty<object>() });
        }

        graph.Id = id;
        var problems = AutomationGraphValidator.Validate(graph);
        if (problems.Count > 0)
        {
            return (400, new { error = "The automation was not saved.", problems });
        }

        var updated = _settingsStore.Current.Clone();
        var index = updated.AutomationGraphs.FindIndex(g => g.Id == id);
        if (index >= 0)
        {
            updated.AutomationGraphs[index] = graph;
        }
        else
        {
            updated.AutomationGraphs.Add(graph);
        }

        await _settingsStore.SaveAsync(updated, ct);
        return (200, new { graph });
    }

    /// <summary>
    /// Dry run or Run now. The body may carry the editor's unsaved <c>graph</c>; otherwise the saved one runs.
    /// </summary>
    private async Task<(int, object)> TestAsync(string id, string body, CancellationToken ct)
    {
        TestRequest request;
        try
        {
            request = string.IsNullOrWhiteSpace(body) ? new TestRequest() : JsonSerializer.Deserialize<TestRequest>(body, ReadOptions) ?? new TestRequest();
        }
        catch (JsonException ex)
        {
            return (400, new { error = $"Not a valid test request: {ex.Message}" });
        }

        var graph = request.Graph ?? _settingsStore.Current.AutomationGraphs.FirstOrDefault(g => g.Id == id)?.Clone();
        if (graph is null)
        {
            return (404, new { error = $"No automation '{id}'. Save it first." });
        }

        graph.Id = id;
        var problems = AutomationGraphValidator.Validate(graph);
        if (problems.Count > 0)
        {
            return (400, new { error = "Fix the automation before you run it.", problems });
        }

        var data = request.Data?.ToDictionary(static kv => kv.Key, static kv => (object?)kv.Value);
        var run = await _engine.RunGraphNowAsync(graph, request.TriggerNodeId, data, request.DryRun, ct);
        return (200, run);
    }

    private object BuildCatalog()
    {
        var settings = _settingsStore.Current;
        var baseUrl = _relay.CurrentStatus.EndpointBaseUrl;
        return new
        {
            nodeTypes = AutomationNodeTypes.All,
            scenes = settings.Receiver.Scenes.Select(static s => new { id = s.Id, name = s.Name }),
            pluginActions = _pluginHost.GetPlugins()
                .Where(static p => p.IsEnabled)
                .SelectMany(static p => p.Actions.Select(a => new
                {
                    pluginId = p.Id,
                    pluginName = p.Name,
                    actionId = a.ActionId,
                    name = a.DisplayName,
                    parameters = (a.Parameters ?? []).Select(static x => new { name = x.Name, type = x.Type, description = x.Description, required = x.Required }),
                })),
            eventNames = AutomationNodeTypes.KnownEventNames,
            inputs = DenonCatalog.InputSources.Select(static i => new { code = i.Code, name = i.DisplayName }),
            soundModes = DenonCatalog.SoundModes.Select(static m => m.Code),
            webhookBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/') + "/api/hooks/",
            maxSafeVolume = settings.Mcp.MaxSafeVolume,
        };
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!request.HasEntityBody)
        {
            return string.Empty;
        }

        if (request.ContentLength64 > MaxBodyBytes)
        {
            throw new InvalidDataException("The request body is too large.");
        }

        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (read > MaxBodyBytes)
        {
            throw new InvalidDataException("The request body is too large.");
        }

        return new string(buffer, 0, read);
    }

    private sealed class TestRequest
    {
        public bool DryRun { get; set; } = true;

        public string? TriggerNodeId { get; set; }

        public Dictionary<string, JsonElement>? Data { get; set; }

        public AutomationGraph? Graph { get; set; }
    }
}

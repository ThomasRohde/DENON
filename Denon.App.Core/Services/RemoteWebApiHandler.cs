using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;

namespace Denon.App.Core.Services;

public interface IRemoteWebApiHandler
{
    Task<RemoteWebResponse> HandleAsync(string method, string path, string? query, string body, string? authToken, CancellationToken cancellationToken);
}

public sealed record RemoteWebResponse(int StatusCode, string ContentType, string Body);

public sealed class RemoteWebApiHandler : IRemoteWebApiHandler
{
    private const string HooksPrefix = "/api/hooks/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly YouTubeMediaService _youtubeService;
    private readonly YouTubeQueueService _youtubeQueueService;
    private readonly LocalMediaJukeboxService _localJukeboxService;
    private readonly PluginHostService _pluginHost;
    private readonly EventTelemetryLog _telemetry;
    private readonly AutomationEngineService _automations;
    private readonly AppLogStore _logStore;
    private readonly StationLogoService? _stationLogos;

    /// <summary>How long a webhook call waits for its automations before it answers "still running".</summary>
    internal static TimeSpan WebhookReplyWait { get; set; } = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan WebhookRepeatWindow = TimeSpan.FromSeconds(5);
    private readonly Lock _webhookGate = new();
    private string? _lastWebhookKey;
    private DateTimeOffset _lastWebhookAt;

    public RemoteWebApiHandler(
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppSettingsStore settingsStore,
        YouTubeMediaService youtubeService,
        YouTubeQueueService youtubeQueueService,
        LocalMediaJukeboxService localJukeboxService,
        PluginHostService pluginHost,
        EventTelemetryLog telemetry,
        AutomationEngineService automations,
        AppLogStore logStore,
        StationLogoService? stationLogos = null)
    {
        _stationLogos = stationLogos;
        _receiverController = receiverController;
        _stateStore = stateStore;
        _settingsStore = settingsStore;
        _youtubeService = youtubeService;
        _youtubeQueueService = youtubeQueueService;
        _localJukeboxService = localJukeboxService;
        _pluginHost = pluginHost;
        _telemetry = telemetry;
        _automations = automations;
        _logStore = logStore;
    }

    public async Task<RemoteWebResponse> HandleAsync(
        string method,
        string path,
        string? query,
        string body,
        string? authToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var remoteCfg = _settingsStore.Current.Remote;
            if (!remoteCfg.Enabled)
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
                {
                    return new RemoteWebResponse(
                        403,
                        "text/html; charset=utf-8",
                        "<!DOCTYPE html><html><body style='background:#0a0c10;color:#e2e8f0;font-family:sans-serif;padding:32px;text-align:center'><h2>Phone Remote Disabled</h2><p style='color:#94a3b8'>Enable Phone Remote in Denon Desktop → Cast &amp; Phone Remote to control this room.</p></body></html>");
                }

                return JsonError(403, "Phone Remote is currently disabled in Denon Desktop.");
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
            {
                return new RemoteWebResponse(200, "text/html; charset=utf-8", RemoteWebUiHtml.PageHtml);
            }

            var effectiveToken = !string.IsNullOrWhiteSpace(authToken)
                ? authToken.Trim()
                : (ExtractQueryParam(query, "token") ?? ExtractQueryParam(query, "pair") ?? string.Empty).Trim();

            var role = ResolveAccessRole(remoteCfg, effectiveToken);
            if (role == "none")
            {
                return new RemoteWebResponse(
                    401,
                    "application/json; charset=utf-8",
                    JsonSerializer.Serialize(new
                    {
                        error = "Pairing required. Scan the QR code in Denon Desktop or enter a valid pairing code.",
                        pairingRequired = true,
                    }, JsonOptions));
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(BuildStatusPayload(role));
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/api/youtube/search", StringComparison.OrdinalIgnoreCase))
            {
                var q = ExtractQueryParam(query, "q");
                if (string.IsNullOrWhiteSpace(q))
                {
                    return JsonError(400, "Query parameter 'q' is required.");
                }

                var searchResult = await _youtubeService.SearchOrResolveAsync(q, YouTubeSearchMode.Music, maxResults: 12, cancellationToken);
                return JsonOk(new
                {
                    summary = searchResult.SummaryText,
                    tracks = searchResult.Tracks,
                });
            }

            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                return JsonError(404, "Endpoint not found.");
            }

            var lowerPath = path.ToLowerInvariant();
            if (role != "owner" && lowerPath != "/api/youtube/queue")
            {
                return JsonError(403, "Owner access required for this control. Guests can search and queue YouTube tracks.");
            }

            if (lowerPath.StartsWith(HooksPrefix, StringComparison.Ordinal))
            {
                return await HandleWebhookAsync(path[HooksPrefix.Length..], body, cancellationToken);
            }

            using var doc = string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(body);
            var root = doc.RootElement;

            _telemetry.MarkOrigin("avr", "phone_remote", "remote.api", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["role"] = role,
            });
            _telemetry.MarkOrigin("tv", "phone_remote");
            _telemetry.MarkOrigin("lighting", "phone_remote");

            switch (lowerPath)
            {
                case "/api/power":
                    // {"on": true|false} sets the power; no body toggles it.
                    if (root.TryGetProperty("on", out var onEl) && onEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var on = onEl.GetBoolean();
                        await _receiverController.SetPowerAsync(on, cancellationToken);
                        return JsonOk(new { message = on ? "Receiver on." : "Receiver in standby." });
                    }

                    await _receiverController.TogglePowerAsync(cancellationToken);
                    return JsonOk(new { message = "Power toggled." });

                case "/api/mute":
                    await _receiverController.ToggleMuteAsync(cancellationToken);
                    return JsonOk(new { message = "Mute toggled." });

                case "/api/volume":
                    if (root.TryGetProperty("level", out var lvlEl) && lvlEl.TryGetInt32(out var lvl))
                    {
                        var clamped = Math.Clamp(lvl, 0, 98);
                        await _receiverController.SetVolumeAsync(clamped, cancellationToken);
                        return JsonOk(new { message = $"Volume {DenonVolumeMath.FormatDecibels(clamped)}" });
                    }
                    return JsonError(400, "Missing 'level'.");

                case "/api/input":
                    var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        return JsonError(400, "Missing 'code'.");
                    }
                    await _receiverController.SetInputSourceAsync(code, cancellationToken);
                    return JsonOk(new { message = $"Input: {DenonCatalog.GetFriendlyInputName(code)}" });

                case "/api/scene":
                    var sceneId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var scene = _settingsStore.Current.Receiver.Scenes.FirstOrDefault(s =>
                        string.Equals(s.Id, sceneId, StringComparison.OrdinalIgnoreCase));
                    if (scene is null)
                    {
                        return JsonError(404, "Scene not found.");
                    }
                    await _receiverController.ApplySceneAsync(scene, cancellationToken);
                    return JsonOk(new { message = $"Scene '{scene.Name}' activated." });

                case "/api/station":
                    var alias = root.TryGetProperty("alias", out var aliasEl) ? aliasEl.GetString() : null;
                    var station = _settingsStore.Current.Receiver.Stations.FirstOrDefault(s =>
                        string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase));
                    if (station is null)
                    {
                        return JsonError(404, "Station not found.");
                    }
                    await _receiverController.PlayPresetAsync(station, useLowVolume: false, unmute: true, cancellationToken);
                    return JsonOk(new { message = $"Tuning {station.DisplayName}" });

                case "/api/transport":
                    var action = root.TryGetProperty("action", out var actEl) ? actEl.GetString()?.ToLowerInvariant() : null;
                    switch (action)
                    {
                        case "play":
                            await _receiverController.SetPlayStateAsync("play", cancellationToken);
                            return JsonOk(new { message = "Playing" });
                        case "pause":
                            await _receiverController.SetPlayStateAsync("pause", cancellationToken);
                            return JsonOk(new { message = "Paused" });
                        case "stop":
                            _youtubeQueueService.StopSession();
                            _localJukeboxService.StopSession();
                            await _receiverController.SetPlayStateAsync("stop", cancellationToken);
                            return JsonOk(new { message = "Stopped" });
                        case "next":
                            if (_youtubeQueueService.IsYouTubePlaybackActive)
                            {
                                await _youtubeQueueService.PlayNextAsync(cancellationToken);
                            }
                            else if (_localJukeboxService.IsLocalPlaybackActive)
                            {
                                await _localJukeboxService.PlayNextAsync(cancellationToken);
                            }
                            else
                            {
                                await _receiverController.PlayNextAsync(cancellationToken);
                            }
                            return JsonOk(new { message = "Next track" });
                        case "prev":
                            if (_youtubeQueueService.IsYouTubePlaybackActive)
                            {
                                await _youtubeQueueService.PlayPreviousAsync(cancellationToken);
                            }
                            else if (_localJukeboxService.IsLocalPlaybackActive)
                            {
                                await _localJukeboxService.PlayPreviousAsync(cancellationToken);
                            }
                            else
                            {
                                await _receiverController.PlayPreviousAsync(cancellationToken);
                            }
                            return JsonOk(new { message = "Previous track" });
                        default:
                            return JsonError(400, "Unknown transport action.");
                    }

                case "/api/youtube/play":
                    var playTrack = BuildRemoteTrack(JsonSerializer.Deserialize<YouTubeTrackItem>(body, JsonOptions));
                    if (playTrack is null)
                    {
                        return JsonError(400, "Invalid track payload.");
                    }
                    _localJukeboxService.StopSession();
                    await _youtubeQueueService.PlayNowAsync(playTrack, useLowVolume: false, unmute: true, cancellationToken);
                    _logStore.Info($"Phone Remote cast YouTube track: {playTrack.Title}");
                    return JsonOk(new { message = $"Casting {playTrack.Title}" });

                case "/api/youtube/queue":
                    var queueTrack = BuildRemoteTrack(JsonSerializer.Deserialize<YouTubeTrackItem>(body, JsonOptions));
                    if (queueTrack is null)
                    {
                        return JsonError(400, "Invalid track payload.");
                    }
                    _youtubeQueueService.Enqueue(queueTrack);
                    _logStore.Info($"Phone Remote queued YouTube track: {queueTrack.Title}");
                    return JsonOk(new { message = $"Queued {queueTrack.Title}" });

                case "/api/plugins/action":
                    var pluginId = root.TryGetProperty("pluginId", out var pidEl) ? pidEl.GetString() : null;
                    var pluginActionId = root.TryGetProperty("actionId", out var aidEl) ? aidEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(pluginActionId))
                    {
                        return JsonError(400, "Missing 'pluginId' or 'actionId'.");
                    }

                    Dictionary<string, string>? actionParams = null;
                    if (root.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        actionParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in paramsEl.EnumerateObject())
                        {
                            actionParams[prop.Name] = prop.Value.ToString();
                        }
                    }

                    var actionResult = await _pluginHost.ExecuteActionAsync(pluginId, pluginActionId, actionParams, cancellationToken);
                    if (!actionResult.Success)
                    {
                        return JsonError(400, actionResult.Message);
                    }

                    return JsonOk(new { message = actionResult.Message });

                case "/api/plugins/toggle":
                    var togglePluginId = root.TryGetProperty("pluginId", out var tpidEl) ? tpidEl.GetString() : null;
                    var enabledVal = root.TryGetProperty("enabled", out var enEl) && enEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? enEl.GetBoolean()
                        : (bool?)null;
                    if (string.IsNullOrWhiteSpace(togglePluginId) || !enabledVal.HasValue)
                    {
                        return JsonError(400, "Missing 'pluginId' or 'enabled'.");
                    }

                    await _pluginHost.SetPluginEnabledAsync(togglePluginId, enabledVal.Value, cancellationToken);
                    return JsonOk(new { message = enabledVal.Value ? $"Enabled {togglePluginId}" : $"Disabled {togglePluginId}" });

                default:
                    return JsonError(404, "Endpoint not found.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logStore.Warning($"Phone Remote API error ({method} {path}): {ex.Message}");
            return JsonError(500, ex.Message);
        }
    }

    /// <summary>
    /// POST /api/hooks/{name}: publishes "webhook.received" on the event bus (and the control log) and starts the
    /// automations with a matching Webhook trigger. A JSON object body adds its simple fields to the event data
    /// (at most 20, values up to 500 characters); any other body is kept as "text". The reply waits a few seconds
    /// for the runs and says what each automation did, so an iPhone Shortcut can show it.
    /// </summary>
    private async Task<RemoteWebResponse> HandleWebhookAsync(string rawName, string body, CancellationToken ct)
    {
        var name = AutomationTriggers.NormalizeHookName(WebUtility.UrlDecode(rawName).TrimEnd('/'));
        if (name is null)
        {
            return JsonError(400, "Use a hook name of letters, digits, '-' or '_' (at most 64 characters), as in /api/hooks/arrive-home.");
        }

        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["hook"] = name };
        ReadWebhookPayload(body, data);

        // A Shortcut tapped several times sends the same call in a burst: each one runs its automations, but only
        // the first in 5 s is written to the event log and the app log.
        var key = name + "|" + string.Join("&", data.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase).Select(static p => $"{p.Key}={p.Value}"));
        var now = DateTimeOffset.Now;
        bool repeat;
        lock (_webhookGate)
        {
            repeat = key == _lastWebhookKey && now - _lastWebhookAt < WebhookRepeatWindow;
            _lastWebhookKey = key;
            _lastWebhookAt = now;
        }

        var published = repeat
            ? new RoomEvent(now, EventTelemetryLog.StreamControl, AutomationTriggers.WebhookDevice, AutomationTriggers.WebhookEventName, AutomationTriggers.WebhookOrigin, data)
            : _telemetry.Record(EventTelemetryLog.StreamControl, AutomationTriggers.WebhookDevice, AutomationTriggers.WebhookEventName, AutomationTriggers.WebhookOrigin, data);
        IReadOnlyList<TriggerOutcome> outcomes = published is null ? [] : _automations.HandleWebhookCall(published);

        var runs = outcomes.Where(static o => o.Run is not null).Select(static o => (Task)o.Run!).ToList();
        if (runs.Count > 0)
        {
            try
            {
                await Task.WhenAll(runs).WaitAsync(WebhookReplyWait, ct);
            }
            catch (TimeoutException)
            {
                // Answer with what is done; the rest is "still running".
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed run is reported per automation below.
            }
        }

        var results = outcomes.Select(DescribeWebhookOutcome).ToList();
        var listening = results.Select(static r => r.Automation).ToList();
        var message = results.Count == 0
            ? $"Received '{name}', but no enabled automation listens to it."
            : string.Join("\n", results.Select(static r => r.Message));

        if (!repeat)
        {
            _logStore.Info($"[Webhook] '{name}' received; {results.Count} enabled automation(s) listen to it.");
        }

        return JsonOk(new { hook = name, automations = listening, results, message });
    }

    private sealed record WebhookResult(string Automation, string Outcome, string Message);

    /// <summary>One automation's line in the webhook reply: ran, nothing, skipped, paused, failed or running.</summary>
    private static WebhookResult DescribeWebhookOutcome(TriggerOutcome outcome)
    {
        var name = outcome.Graph.Name;
        switch (outcome.Kind)
        {
            case TriggerOutcomeKind.Skipped:
                var until = outcome.Until is { } at
                    ? $" until {at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}"
                    : string.Empty;
                return new(name, "skipped", $"{name}: skipped, in its cooldown{until}.");
            case TriggerOutcomeKind.Paused:
                return new(name, "paused", $"{name}: skipped, event automations are paused after too many runs.");
        }

        var run = outcome.Run!;
        if (!run.IsCompleted)
        {
            return new(name, "running", $"{name}: still running.");
        }

        if (!run.IsCompletedSuccessfully)
        {
            return new(name, "failed", $"{name}: failed: {run.Exception?.GetBaseException().Message ?? "cancelled"}");
        }

        var result = run.Result;
        var prefix = result.Trigger + " → ";
        var what = result.Summary.StartsWith(prefix, StringComparison.Ordinal) ? result.Summary[prefix.Length..] : result.Summary;
        var kind = !result.Success ? "failed" : AutomationEngineService.RunActed(outcome.Graph, result) ? "ran" : "nothing";
        return new(name, kind, kind == "ran" ? $"{name}: ran {what}" : $"{name}: {what}");
    }

    private static void ReadWebhookPayload(string body, Dictionary<string, object?> data)
    {
        const int maxFields = 20, maxValue = 500;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        static string Clip(string s) => s.Length <= maxValue ? s : s[..maxValue];

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (data.Count > maxFields || prop.Name.Equals("hook", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    data[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => Clip(prop.Value.GetString() ?? string.Empty),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.Null => null,
                        _ => Clip(prop.Value.GetRawText()),
                    };
                }

                return;
            }
        }
        catch (JsonException)
        {
            // Not JSON: keep it as text.
        }

        data["text"] = Clip(body.Trim());
    }

    private static string ResolveAccessRole(RemoteAccessSettings cfg, string candidateToken)
    {
        if (!cfg.RequirePairing)
        {
            return "owner";
        }

        if (string.IsNullOrWhiteSpace(candidateToken))
        {
            return "none";
        }

        if (SecureEquals(candidateToken, cfg.OwnerToken))
        {
            return "owner";
        }

        if (SecureEquals(candidateToken, cfg.GuestToken))
        {
            return "guest";
        }

        return "none";
    }

    private static bool SecureEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    private object BuildStatusPayload(string role)
    {
        var snapshot = _stateStore.Snapshot;
        var status = snapshot.Status;
        var settings = _settingsStore.Current.Receiver;
        var ytSnap = _youtubeQueueService.Snapshot;
        var localSnap = _localJukeboxService.Snapshot;

        var isAwake = snapshot.IsConnected && status is { IsPoweredOn: true, IsMainZoneOn: true };
        var isPlaying = snapshot.IsConnected && status is not null && string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase);

        string connectionState;
        string title;
        string detail;
        string artwork = string.Empty;

        if (!snapshot.IsConnected && status is null)
        {
            connectionState = string.IsNullOrWhiteSpace(snapshot.LastError) ? "connecting" : "receiver_unreachable";
            title = connectionState == "connecting" ? "Connecting to Receiver..." : "Receiver Unreachable";
            detail = !string.IsNullOrWhiteSpace(snapshot.LastError)
                ? snapshot.LastError
                : "Waiting for Denon Desktop to reach the receiver";
        }
        else if (!snapshot.IsConnected)
        {
            connectionState = "receiver_unreachable";
            title = "Receiver Unreachable";
            detail = !string.IsNullOrWhiteSpace(snapshot.LastError)
                ? snapshot.LastError
                : "Connection lost — showing last known state";
        }
        else if (!isAwake)
        {
            connectionState = "standby";
            title = "Receiver in Standby";
            detail = "Tap Turn on or pick a Scene to wake";
        }
        else if (status is not null)
        {
            connectionState = "connected";
            var activeStationName = !string.IsNullOrWhiteSpace(status.ActiveStationAlias)
                ? settings.Stations.FirstOrDefault(s => string.Equals(s.Alias, status.ActiveStationAlias, StringComparison.OrdinalIgnoreCase))?.DisplayName
                : null;
            var resolved = NowPlayingResolver.Resolve(
                status,
                activeStationName: activeStationName,
                plugins: _pluginHost.GetPlugins(),
                activeLocalTrack: localSnap.IsActive ? localSnap.CurrentTrack : null,
                activeYouTubeTrack: ytSnap.IsActive ? ytSnap.CurrentTrack : null,
                activeStationLogoUrl: _stationLogos?.TryGet(activeStationName));
            title = resolved.Title;
            detail = resolved.Detail;
            // Only a web picture: a phone cannot load a file on this PC, and the path must not leave the PC.
            artwork = IsWebAddress(resolved.ImageUrl) ? resolved.ImageUrl : string.Empty;
        }
        else
        {
            connectionState = "connecting";
            title = "Connecting...";
            detail = string.Empty;
        }

        return new
        {
            role,
            connectionState,
            lastUpdatedUtc = (snapshot.LastSuccessfulRefresh ?? DateTimeOffset.UtcNow).ToString("O"),
            lastError = snapshot.LastError,
            isAvrControlAvailable = _receiverController.IsAvrProtocolAvailable,
            receiverName = status?.Player.Name ?? settings.PlayerName,
            isConnected = snapshot.IsConnected,
            isAwake,
            isPlaying,
            isMuted = status?.IsMuted ?? false,
            volume = status?.Volume ?? 0,
            maxVolume = status?.MaxVolume ?? 98,
            inputCode = status?.InputSource ?? string.Empty,
            inputDisplay = DenonCatalog.GetFriendlyInputName(status?.InputSource),
            soundMode = string.IsNullOrWhiteSpace(status?.SoundMode) ? "—" : status.SoundMode,
            playState = status?.PlayState ?? "Stopped",
            nowPlayingTitle = title,
            nowPlayingDetail = detail,
            artworkUrl = artwork,
            scenes = settings.Scenes.Select(static s => new { id = s.Id, name = s.Name, badge = s.Badge, accentHex = s.AccentHex }),
            inputs = DenonCatalog.InputSources.Select(opt => new
            {
                code = opt.Code,
                displayName = opt.DisplayName,
                isActive = isAwake && DenonCatalog.InputMatches(status?.InputSource, opt.Code),
            }),
            stations = settings.Stations.Select(st => new
            {
                alias = st.Alias,
                displayName = st.DisplayName,
                isPlaying = isAwake && string.Equals(status?.ActiveStationAlias, st.Alias, StringComparison.OrdinalIgnoreCase),
            }),
            youtubeQueue = ytSnap.Queue.Select((t, idx) => new
            {
                videoId = t.VideoId,
                title = t.Title,
                author = t.Author,
                durationDisplay = t.DurationDisplay,
                isCurrent = ytSnap.IsActive && idx == ytSnap.CurrentIndex,
            }),
            plugins = role == "owner"
                ? _pluginHost.GetPlugins().Select(static p => new
                {
                    id = p.Id,
                    name = p.Name,
                    category = p.Category,
                    accentHex = p.AccentHex,
                    iconGlyph = p.IconGlyph,
                    isEnabled = p.IsEnabled,
                    health = p.Health.ToString(),
                    statusText = p.StatusText,
                    actions = p.Actions.Select(static a => new
                    {
                        actionId = a.ActionId,
                        displayName = a.DisplayName,
                        category = a.Category,
                    }),
                }).ToArray()
                : Array.Empty<object>(),
        };
    }

    private static string? ExtractQueryParam(string? query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.UrlDecode(part[(eq + 1)..]);
            }
        }

        return null;
    }

    private const int MaxRemoteTrackTextLength = 200;

    /// <summary>
    /// Builds a YouTube track from a phone request. The phone sends the whole track, but only the
    /// video id is used to make the addresses: a phone must not set the watch or thumbnail address,
    /// because Cinema opens the watch address and the app loads the thumbnail. Returns null when the
    /// id is not an 11-character YouTube video id.
    /// </summary>
    internal static YouTubeTrackItem? BuildRemoteTrack(YouTubeTrackItem? sent)
    {
        var videoId = sent?.VideoId?.Trim();
        if (sent is null || videoId is null || !IsYouTubeVideoId(videoId))
        {
            return null;
        }

        var duration = sent.Duration is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromHours(24) ? d : (TimeSpan?)null;
        return new YouTubeTrackItem(
            VideoId: videoId,
            Title: CleanRemoteText(sent.Title, "YouTube track"),
            Author: CleanRemoteText(sent.Author, string.Empty),
            Duration: duration,
            ThumbnailUrl: $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
            WatchUrl: $"https://www.youtube.com/watch?v={videoId}");
    }

    internal static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsYouTubeVideoId(string id) =>
        id.Length == 11 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string CleanRemoteText(string? text, string fallback)
    {
        var cleaned = new string((text ?? string.Empty).Where(static c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxRemoteTrackTextLength)
        {
            cleaned = cleaned[..MaxRemoteTrackTextLength].TrimEnd();
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static RemoteWebResponse JsonOk(object payload) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.Serialize(payload, JsonOptions));

    private static RemoteWebResponse JsonError(int code, string message) =>
        new(code, "application/json; charset=utf-8", JsonSerializer.Serialize(new { error = message }, JsonOptions));
}

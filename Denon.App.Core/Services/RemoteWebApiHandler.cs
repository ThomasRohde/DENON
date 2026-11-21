using System.Net;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;

namespace Denon.App.Core.Services;

public interface IRemoteWebApiHandler
{
    Task<RemoteWebResponse> HandleAsync(string method, string path, string? query, string body, CancellationToken cancellationToken);
}

public sealed record RemoteWebResponse(int StatusCode, string ContentType, string Body);

public sealed class RemoteWebApiHandler : IRemoteWebApiHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ReceiverController _receiverController;
    private readonly ReceiverStateStore _stateStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly YouTubeMediaService _youtubeService;
    private readonly YouTubeQueueService _youtubeQueueService;
    private readonly LocalMediaJukeboxService _localJukeboxService;
    private readonly PluginHostService _pluginHost;
    private readonly AppLogStore _logStore;

    public RemoteWebApiHandler(
        ReceiverController receiverController,
        ReceiverStateStore stateStore,
        AppSettingsStore settingsStore,
        YouTubeMediaService youtubeService,
        YouTubeQueueService youtubeQueueService,
        LocalMediaJukeboxService localJukeboxService,
        PluginHostService pluginHost,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
        _stateStore = stateStore;
        _settingsStore = settingsStore;
        _youtubeService = youtubeService;
        _youtubeQueueService = youtubeQueueService;
        _localJukeboxService = localJukeboxService;
        _pluginHost = pluginHost;
        _logStore = logStore;
    }

    public async Task<RemoteWebResponse> HandleAsync(
        string method,
        string path,
        string? query,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
            {
                return new RemoteWebResponse(200, "text/html; charset=utf-8", RemoteWebUiHtml.PageHtml);
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(BuildStatusPayload());
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

            using var doc = string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(body);
            var root = doc.RootElement;

            switch (path.ToLowerInvariant())
            {
                case "/api/power":
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
                    var playTrack = JsonSerializer.Deserialize<YouTubeTrackItem>(body, JsonOptions);
                    if (playTrack is null || string.IsNullOrWhiteSpace(playTrack.VideoId))
                    {
                        return JsonError(400, "Invalid track payload.");
                    }
                    _localJukeboxService.StopSession();
                    await _youtubeQueueService.PlayNowAsync(playTrack, useLowVolume: false, unmute: true, cancellationToken);
                    _logStore.Info($"Phone Remote cast YouTube track: {playTrack.Title}");
                    return JsonOk(new { message = $"Casting {playTrack.Title}" });

                case "/api/youtube/queue":
                    var queueTrack = JsonSerializer.Deserialize<YouTubeTrackItem>(body, JsonOptions);
                    if (queueTrack is null || string.IsNullOrWhiteSpace(queueTrack.VideoId))
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

    private object BuildStatusPayload()
    {
        var snapshot = _stateStore.Snapshot;
        var status = snapshot.Status;
        var settings = _settingsStore.Current.Receiver;
        var ytSnap = _youtubeQueueService.Snapshot;
        var localSnap = _localJukeboxService.Snapshot;

        var isAwake = status is { IsPoweredOn: true, IsMainZoneOn: true };
        var isPlaying = status is not null && string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase);

        string title;
        string detail;
        string artwork = string.Empty;

        if (!isAwake)
        {
            title = "Receiver in Standby";
            detail = "Tap STANDBY or pick a Scene to wake";
        }
        else if (status is not null && !DenonCatalog.InputMatches(status.InputSource, "NET"))
        {
            title = DenonCatalog.GetFriendlyInputName(status.InputSource);
            detail = $"Sound Mode: {status.SoundMode}";
        }
        else if (ytSnap.IsActive && ytSnap.CurrentTrack is { } yt)
        {
            title = yt.Title;
            detail = $"{yt.Author} • YouTube • {yt.DurationDisplay}";
            artwork = yt.ThumbnailUrl;
        }
        else if (localSnap.IsActive && localSnap.CurrentTrack is { } loc)
        {
            title = loc.Title;
            detail = $"{loc.Artist} • Local {loc.CodecBadge}";
        }
        else if (status is not null)
        {
            title = string.IsNullOrWhiteSpace(status.NowPlaying.Song) ? "HEOS Stream" : status.NowPlaying.Song;
            detail = string.IsNullOrWhiteSpace(status.NowPlaying.Artist) ? "HEOS Network Audio" : status.NowPlaying.Artist;
            artwork = status.NowPlaying.ImageUrl;
        }
        else
        {
            title = "Connecting...";
            detail = string.Empty;
        }

        return new
        {
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
                isPlaying = string.Equals(status?.ActiveStationAlias, st.Alias, StringComparison.OrdinalIgnoreCase),
            }),
            youtubeQueue = ytSnap.Queue.Select((t, idx) => new
            {
                videoId = t.VideoId,
                title = t.Title,
                author = t.Author,
                durationDisplay = t.DurationDisplay,
                isCurrent = ytSnap.IsActive && idx == ytSnap.CurrentIndex,
            }),
            plugins = _pluginHost.GetPlugins().Select(static p => new
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
            }),
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

    private static RemoteWebResponse JsonOk(object payload) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.Serialize(payload, JsonOptions));

    private static RemoteWebResponse JsonError(int code, string message) =>
        new(code, "application/json; charset=utf-8", JsonSerializer.Serialize(new { error = message }, JsonOptions));
}

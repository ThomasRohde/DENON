using System.Net;
using System.Text.Json;
using Denon.App.PluginSdk;
using Denon.Plugin.AppleTv.Discovery;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.HomePod;

/// <summary>
/// HomePod plugin: finds HomePods (DNS-SD), treats a stereo pair as one speaker, shows what each speaker plays
/// (app, title, artist, play state) over the AirPlay 2 remote-control channel, sends play/pause/next/previous,
/// logs listening sessions and can pause the HomePods when the Denon receiver turns on. No pairing PIN: HomePods
/// accept a transient pairing on each connection, and nothing is stored.
/// </summary>
public sealed class HomePodPlugin : IDenonPlugin, IPluginViewProvider
{
    private const string EventStream = "media";
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly PluginActionParameter SpeakerParam = new(
        "device",
        "string",
        "HomePod or stereo pair: its name (for example \"Køkken\" or \"HomePod Left\"), id or IP. Optional: default is the speaker that plays, then the only one.");

    private static readonly PluginActionParameter[] SpeakerOnly = [SpeakerParam];

    private readonly object _gate = new();
    private readonly Dictionary<string, HomePodClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly CompanionIdentity _identity = CompanionIdentity.Create("Denon Desktop");

    private IPluginContext? _context;
    private string? _lastDiscoverySeen;
    private HomePodConfig _config = new();
    private HomePodListenLog? _listenLog;
    private CancellationTokenSource? _lifetime;
    private Task? _discoveryLoop;
    private Task? _tickLoop;
    private string? _lastStatus;
    private string? _lastDetailsKey;

    public PluginManifest Manifest { get; } = new(
        Id: "homepod",
        Name: "HomePod",
        Version: typeof(HomePodPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Author: "Thomas Klok Rohde",
        Description: "Finds your HomePods (a stereo pair counts as one speaker), shows what each plays (app, title, " +
                     "artist, play state) over the AirPlay 2 remote-control channel, sends play/pause/next/previous, " +
                     "logs listening time for Insights and can pause the HomePods when the Denon turns on. " +
                     "No PIN and no stored pairing. Native .NET, no Python.",
        Category: "Streaming Sources",
        AccentHex: "#9CA3AF",
        IconGlyph: "POD",
        Capabilities: PluginCapabilities.DashboardUi
                      | PluginCapabilities.ReceiverEvents
                      | PluginCapabilities.Actions
                      | PluginCapabilities.LocalNetwork);

    internal event Action? SpeakersChanged;

    internal event Action<string, HomePodState>? SpeakerStateChanged;

    internal bool AutoDiscover => _config.AutoDiscover;

    internal bool PauseWhenReceiverPowersOn => _config.PauseWhenReceiverPowersOn;

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _config = await context.Storage.LoadConfigAsync(static () => new HomePodConfig(), cancellationToken);
        _config.Speakers ??= [];
        _listenLog = new HomePodListenLog((name, data) => RecordRaw(name, data, "external"));
        context.ReportStatus($"{_config.Speakers.Count} HomePod speaker(s) known.", PluginHealthState.Idle);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _lifetime?.Cancel();
        _lifetime = new CancellationTokenSource();
        foreach (var speaker in SnapshotSpeakers().Where(s => s.Enabled))
        {
            StartClient(speaker);
        }

        var token = _lifetime.Token;
        _tickLoop = Task.Run(() => TickLoopAsync(token));
        _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(token));
        ReportStatus(force: true);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _listenLog?.EndAll(DateTimeOffset.UtcNow, _context?.IsAppShuttingDown == true ? "app_shutdown" : "plugin_stopped");
        _lifetime?.Cancel();
        List<HomePodClient> clients;
        lock (_gate)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }

        foreach (var loop in new[] { _discoveryLoop, _tickLoop })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                }
                catch
                {
                }
            }
        }

        _context?.ReportStatus("Stopped.", PluginHealthState.Idle);
        SpeakersChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime?.Dispose();
    }

    public object? CreateDashboardView() => new HomePodDashboardView(this);

    // ---------------------------------------------------------------- actions (MCP tools, Smart Scenes, hotkeys)

    public IReadOnlyList<PluginActionDescriptor> GetActions() =>
    [
        new("list-devices", "List HomePods", "All HomePod speakers (a stereo pair is one) with link and now-playing state as JSON.", "HomePod")
            { Parameters = [], IsReadOnly = true },
        new("now-playing", "Now playing", "App, title, artist, album and play state of a HomePod speaker.", "HomePod")
            { Parameters = SpeakerOnly, IsReadOnly = true },
        new("discover", "Discover HomePods", "Runs DNS-SD discovery now and adds new HomePods.", "HomePod")
            { Parameters = [], IsReadOnly = false, IsDestructive = false },
        new("play", "Play", "Resumes playback on a HomePod.", "Playback") { Parameters = SpeakerOnly, IsDestructive = false },
        new("pause", "Pause", "Pauses playback on a HomePod.", "Playback") { Parameters = SpeakerOnly, IsDestructive = false },
        new("play-pause", "Play / Pause", "Toggles play and pause on a HomePod.", "Playback") { Parameters = SpeakerOnly, IsDestructive = false },
        new("next", "Next track", "Skips to the next track on a HomePod.", "Playback") { Parameters = SpeakerOnly, IsDestructive = false },
        new("previous", "Previous track", "Goes back to the previous track on a HomePod.", "Playback") { Parameters = SpeakerOnly, IsDestructive = false },
    ];

    public async Task<PluginActionResult> ExecuteActionAsync(
        string actionId,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (actionId)
            {
                case "list-devices":
                    var views = GetSpeakerViews();
                    return PluginActionResult.OkWithData($"{views.Count} HomePod speaker(s).", JsonSerializer.Serialize(views.Select(v => ToDto(v.Config, v.State)), JsonOptions));
                case "discover":
                    var added = await DiscoverNowAsync(cancellationToken);
                    return PluginActionResult.Ok($"Discovery finished. {added} new HomePod speaker(s).");
            }

            var (speaker, client) = ResolveSpeaker(parameters?.GetValueOrDefault("device"));
            switch (actionId)
            {
                case "now-playing":
                    var state = client?.State ?? new HomePodState(speaker.Id);
                    return PluginActionResult.OkWithData(Describe(speaker, state), JsonSerializer.Serialize(ToDto(speaker, state), JsonOptions));
                case "play":
                case "pause":
                case "play-pause":
                case "next":
                case "previous":
                    var connected = client ?? throw new InvalidOperationException($"{speaker.Name} is switched off in the plugin.");
                    await CommandAsync(connected, actionId, cancellationToken);
                    return PluginActionResult.Ok($"{speaker.Name}: {actionId} sent.");
                default:
                    return PluginActionResult.Fail($"Unknown HomePod action '{actionId}'.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail(ex.Message);
        }
    }

    public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
    {
        if (!_config.PauseWhenReceiverPowersOn ||
            receiverEvent.EventType != PluginReceiverEventType.PowerChanged ||
            !receiverEvent.Snapshot.IsPoweredOn)
        {
            return;
        }

        foreach (var (speaker, state) in GetSpeakerViews().Where(v => v.State.IsPlaying))
        {
            HomePodClient? client;
            lock (_gate)
            {
                _clients.TryGetValue(speaker.Id, out client);
            }

            if (client is null)
            {
                continue;
            }

            try
            {
                await client.PauseAsync(cancellationToken);
                _context?.Logger.Info($"{speaker.Name} paused: the Denon receiver turned on.");
                RecordRaw("homepod.paused_by_automation", new Dictionary<string, object?>
                {
                    ["homepodDevice"] = speaker.Name,
                    ["app"] = state.AppName,
                    ["title"] = state.NowPlaying.Title,
                    ["trigger"] = "receiver_on",
                }, "automation");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _context?.Logger.Warning($"Could not pause {speaker.Name}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- used by the dashboard

    internal IReadOnlyList<(HomePodSpeakerConfig Config, HomePodState State)> GetSpeakerViews()
    {
        var speakers = SnapshotSpeakers();
        lock (_gate)
        {
            return speakers
                .Select(s => (s, _clients.TryGetValue(s.Id, out var c) ? c.State : new HomePodState(s.Id)))
                .ToList();
        }
    }

    internal async Task CommandAsync(string speakerId, string action, CancellationToken ct)
    {
        HomePodClient? client;
        lock (_gate)
        {
            _clients.TryGetValue(speakerId, out client);
        }

        await CommandAsync(client ?? throw new InvalidOperationException("The speaker is switched off in the plugin."), action, ct);
    }

    internal async Task SetSpeakerEnabledAsync(string speakerId, bool enabled)
    {
        await UpdateConfigAsync(c =>
        {
            if (c.Speakers.FirstOrDefault(s => s.Id == speakerId) is { } speaker)
            {
                speaker.Enabled = enabled;
            }
        });

        var speaker = SnapshotSpeakers().FirstOrDefault(s => s.Id == speakerId);
        if (speaker is null)
        {
            return;
        }

        if (enabled)
        {
            StartClient(speaker);
        }
        else
        {
            _listenLog?.EndSpeaker(speakerId, DateTimeOffset.UtcNow, "speaker_disabled");
            await StopClientAsync(speakerId);
        }

        SpeakersChanged?.Invoke();
        ReportStatus(force: true);
    }

    internal Task SetAutoDiscoverAsync(bool enabled) => UpdateConfigAsync(c => c.AutoDiscover = enabled);

    internal Task SetPauseWhenReceiverPowersOnAsync(bool enabled) => UpdateConfigAsync(c => c.PauseWhenReceiverPowersOn = enabled);

    /// <summary>Runs discovery and merges the speakers it finds. Returns how many speakers are new.</summary>
    internal async Task<int> DiscoverNowAsync(CancellationToken cancellationToken = default)
    {
        var known = SnapshotSpeakers()
            .Select(s => IPAddress.TryParse(s.Host, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Select(ip => ip!)
            .ToList();
        var devices = await AppleDeviceDiscovery.DiscoverAsync(TimeSpan.FromSeconds(4), known, cancellationToken);
        var found = HomePodGroups.Build(devices);

        var added = 0;
        var restart = new List<HomePodSpeakerConfig>();
        await UpdateConfigAsync(c =>
        {
            foreach (var speaker in found)
            {
                // The same speaker by its leader's id, else by a member (the pair's leader can change).
                var existing = c.Speakers.FirstOrDefault(s => s.Id == speaker.Id) ??
                               c.Speakers.FirstOrDefault(s => s.Members.Intersect(speaker.Members, StringComparer.OrdinalIgnoreCase).Any());
                var isNew = existing is null;
                if (existing is null)
                {
                    existing = new HomePodSpeakerConfig { Id = speaker.Id };
                    c.Speakers.Add(existing);
                    added++;
                }

                var moved = isNew || existing.Id != speaker.Id || existing.Host != speaker.Host || existing.AirPlayPort != speaker.AirPlayPort;
                existing.Id = speaker.Id;
                existing.Name = speaker.Name;
                existing.Host = speaker.Host;
                existing.AirPlayPort = speaker.AirPlayPort;
                existing.ModelName = speaker.ModelName;
                existing.OsVersion = speaker.OsVersion;
                existing.Members = speaker.Members.ToList();
                existing.LastSeenUtc = DateTimeOffset.UtcNow;
                if (moved)
                {
                    restart.Add(existing);
                }
            }

            c.Speakers = c.Speakers
                .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        foreach (var speaker in restart.Where(s => s.Enabled))
        {
            await StopClientAsync(speaker.Id);
            StartClient(speaker);
        }

        foreach (var speaker in SnapshotSpeakers().Where(s => s.Enabled))
        {
            StartClient(speaker); // No-op for running clients.
        }

        // Only a change is worth a line: discovery runs every few minutes and usually finds the same speakers.
        var seen = string.Join("; ", found
            .Select(f => $"{f.Name} at {f.Host}")
            .Order(StringComparer.OrdinalIgnoreCase)); // The same speakers can answer in another order.
        if (added > 0 || !string.Equals(seen, _lastDiscoverySeen, StringComparison.Ordinal))
        {
            _context?.Logger.Info($"HomePod discovery: {found.Count} speaker(s) ({seen}), {added} new.");
        }

        _lastDiscoverySeen = seen;
        SpeakersChanged?.Invoke();
        ReportStatus(force: true);
        return added;
    }

    // ---------------------------------------------------------------- internals

    private static Task CommandAsync(HomePodClient client, string action, CancellationToken ct) => action switch
    {
        "play" => client.PlayAsync(ct),
        "pause" => client.PauseAsync(ct),
        "play-pause" => client.PlayPauseAsync(ct),
        "next" => client.NextAsync(ct),
        "previous" => client.PreviousAsync(ct),
        _ => throw new ArgumentException($"Unknown command {action}."),
    };

    private (HomePodSpeakerConfig Speaker, HomePodClient? Client) ResolveSpeaker(string? query)
    {
        var views = GetSpeakerViews();
        if (views.Count == 0)
        {
            throw new InvalidOperationException("No HomePods known yet. Run discover first.");
        }

        (HomePodSpeakerConfig Config, HomePodState State)? match = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            match = views.FirstOrDefault(v =>
                v.Config.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                v.Config.Id.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                v.Config.Host.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                v.Config.Members.Contains(q, StringComparer.OrdinalIgnoreCase));
            if (match?.Config is null)
            {
                throw new InvalidOperationException($"No HomePod named \"{q}\". Known: {string.Join(", ", views.Select(v => v.Config.Name))}.");
            }
        }
        else
        {
            var playing = views.Where(v => v.State.IsPlaying).ToList();
            var connected = views.Where(v => v.State.Link == HomePodLink.Connected).ToList();
            match = playing.Count == 1 ? playing[0]
                : connected.Count == 1 ? connected[0]
                : views.Count == 1 ? views[0]
                : throw new InvalidOperationException($"Name the HomePod with \"device\": {string.Join(", ", views.Select(v => v.Config.Name))}.");
        }

        var speaker = match.Value.Config;
        lock (_gate)
        {
            return (speaker, _clients.TryGetValue(speaker.Id, out var client) ? client : null);
        }
    }

    private void StartClient(HomePodSpeakerConfig speaker)
    {
        if (_lifetime is not { IsCancellationRequested: false } || string.IsNullOrWhiteSpace(speaker.Host))
        {
            return;
        }

        HomePodClient client;
        lock (_gate)
        {
            if (_clients.ContainsKey(speaker.Id))
            {
                return;
            }

            var artwork = Path.Combine(_context?.Storage.PluginDataDirectory ?? Path.GetTempPath(), "artwork");
            client = new HomePodClient(speaker.Id, speaker.Host, speaker.AirPlayPort, _identity, artwork);
            _clients[speaker.Id] = client;
        }

        client.Diagnostic += message => _context?.Logger.Info($"[{speaker.Name}] {message}");
        client.StateChanged += (previous, current) => OnStateChanged(speaker.Id, previous, current);
        client.Start();
    }

    private async Task StopClientAsync(string speakerId)
    {
        HomePodClient? client;
        lock (_gate)
        {
            _clients.Remove(speakerId, out client);
        }

        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    private void OnStateChanged(string speakerId, HomePodState previous, HomePodState current)
    {
        var speaker = SnapshotSpeakers().FirstOrDefault(s => s.Id == speakerId);
        if (speaker is null)
        {
            return;
        }

        _listenLog?.Update(speaker, current, DateTimeOffset.UtcNow);

        var wasLinked = previous.Link == HomePodLink.Connected;
        var isLinked = current.Link == HomePodLink.Connected;
        if (wasLinked != isLinked)
        {
            RecordRaw(isLinked ? "homepod.connected" : "homepod.disconnected", new Dictionary<string, object?>
            {
                ["homepodDevice"] = speaker.Name,
                ["host"] = speaker.Host,
                ["error"] = current.Error,
            }, "system");
        }

        SpeakerStateChanged?.Invoke(speakerId, current);
        ReportStatus();
    }

    /// <summary>
    /// "HOMEPOD • Køkken • Apple Music • Song", "HOMEPOD PAUSED • …" while something plays or is paused;
    /// otherwise a summary. Several speakers that play: the first, plus "(+1 more)".
    /// </summary>
    private void ReportStatus(bool force = false)
    {
        var views = GetSpeakerViews();
        var withMedia = views.Where(v => v.State.HasMedia).OrderByDescending(v => v.State.IsPlaying).ToList();
        var linked = views.Count(v => v.State.Link == HomePodLink.Connected);
        var enabled = views.Count(v => v.Config.Enabled);

        string text;
        PluginHealthState health;
        if (withMedia.FirstOrDefault() is { Config: { } config, State: { } state })
        {
            var title = state.NowPlaying.Title is { Length: > 0 } t ? $" • {t}" : string.Empty;
            var artist = state.NowPlaying.Artist is { Length: > 0 } a ? $" – {a}" : string.Empty;
            var more = withMedia.Count > 1 ? $" (+{withMedia.Count - 1} more)" : string.Empty;
            text = $"{(state.IsPlaying ? "HOMEPOD" : "HOMEPOD PAUSED")} • {config.Name} • {state.AppName}{title}{artist}{more}";
            health = PluginHealthState.Connected;
        }
        else if (views.Count == 0)
        {
            text = "No HomePods found yet. Press Discover now.";
            health = PluginHealthState.Idle;
        }
        else
        {
            text = $"{linked} of {enabled} HomePod speaker(s) linked • nothing plays.";
            health = linked > 0 ? PluginHealthState.Connected : enabled > 0 ? PluginHealthState.Warning : PluginHealthState.Idle;
        }

        var details = HeroDetails(withMedia.FirstOrDefault());
        var detailsKey = string.Join("|", details.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value}"));
        if (!force && text == _lastStatus && detailsKey == _lastDetailsKey)
        {
            return;
        }

        _lastStatus = text;
        _lastDetailsKey = detailsKey;
        _context?.ReportStatus(text, health, details);
    }

    /// <summary>
    /// Structured details of the speaker the status line names (kind "homepod"): Denon Desktop shows it in the
    /// now-playing hero while the receiver sleeps, with its artwork and play/pause.
    /// </summary>
    internal static Dictionary<string, string> HeroDetails((HomePodSpeakerConfig Config, HomePodState State) view)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (view.Config is null || !view.State.HasMedia)
        {
            return details;
        }

        var (config, state) = view;
        details["kind"] = "homepod";
        details["speaker"] = config.Name;
        details["app"] = state.AppName ?? "HomePod";
        details["playbackState"] = state.IsPlaying ? "Playing" : "Paused";
        Add("title", state.NowPlaying.Title);
        Add("artist", state.NowPlaying.Artist);
        Add("album", state.NowPlaying.Album);
        if (state.ArtworkPath is { } art && state.ArtworkKey == state.NowPlaying.ArtworkKey)
        {
            details["artworkPath"] = art;
        }

        return details;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                details[key] = value.Trim();
            }
        }
    }

    private static string Describe(HomePodSpeakerConfig speaker, HomePodState state) =>
        state.Link != HomePodLink.Connected ? $"{speaker.Name} is not linked{(state.Error is { } e ? $": {e}" : ".")}"
        : !state.HasMedia ? $"{speaker.Name} plays nothing."
        : $"{speaker.Name}: {(state.IsPlaying ? "playing" : "paused")} {state.NowPlaying.Title ?? "(no title)"}" +
          $"{(state.NowPlaying.Artist is { } a ? $" by {a}" : "")} in {state.AppName}.";

    private static object ToDto(HomePodSpeakerConfig speaker, HomePodState state) => new
    {
        id = speaker.Id,
        name = speaker.Name,
        members = speaker.Members,
        host = speaker.Host,
        model = speaker.ModelName,
        osVersion = speaker.OsVersion,
        enabled = speaker.Enabled,
        link = state.Link.ToString(),
        error = state.Error,
        app = state.AppName,
        appId = state.HasMedia ? state.NowPlaying.AppBundleId : null,
        playbackState = state.HasMedia ? state.NowPlaying.State.ToString() : null,
        title = state.HasMedia ? state.NowPlaying.Title : null,
        artist = state.HasMedia ? state.NowPlaying.Artist : null,
        album = state.HasMedia ? state.NowPlaying.Album : null,
        durationSec = state.HasMedia ? state.NowPlaying.DurationSeconds : null,
        elapsedSec = state.HasMedia ? state.NowPlaying.ElapsedSeconds : null,
        // The now-playing hero reads the artwork from here (a file in the plugin data folder), as for the Apple TV.
        imageUrl = state.HasMedia && state.ArtworkPath is { } art && state.ArtworkKey == state.NowPlaying.ArtworkKey
            ? new Uri(art).AbsoluteUri
            : null,
    };

    private List<HomePodSpeakerConfig> SnapshotSpeakers()
    {
        lock (_gate)
        {
            return _config.Speakers.ToList();
        }
    }

    private async Task UpdateConfigAsync(Action<HomePodConfig> change)
    {
        await _configGate.WaitAsync();
        try
        {
            lock (_gate)
            {
                change(_config);
            }

            if (_context is not null)
            {
                await _context.Storage.SaveConfigAsync(_config);
            }
        }
        finally
        {
            _configGate.Release();
        }
    }

    private void RecordRaw(string name, Dictionary<string, object?> data, string origin)
    {
        try
        {
            _context?.Events.RecordEvent(name, data, EventStream, origin);
        }
        catch
        {
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                _listenLog?.Tick(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            while (!ct.IsCancellationRequested)
            {
                if (_config.AutoDiscover || _config.Speakers.Count == 0)
                {
                    try
                    {
                        await DiscoverNowAsync(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _context?.Logger.Warning($"HomePod discovery failed: {ex.Message}");
                    }
                }

                await Task.Delay(DiscoveryInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

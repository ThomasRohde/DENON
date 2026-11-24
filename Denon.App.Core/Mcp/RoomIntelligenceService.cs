using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.PluginSdk;

namespace Denon.App.Core.Mcp;

/// <summary>
/// Always-on local Living-Room Intelligence Engine (0 API keys, 100% local):
/// - Builds the unified cross-device Room State Graph (Denon AVR + Media + Plugins + Scenes + Rules)
/// - Runs the Self-Healing Room Doctor to detect &amp; auto-fix multi-device A/V mismatches
/// - Manages reversible Room State Snapshots for 1-click Undo
/// - Learns local time-of-day listening habits (room-habits.json) for both the UI &amp; MCP agents
/// </summary>
public sealed class RoomIntelligenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ReceiverStateStore _stateStore;
    private readonly ReceiverController _receiverController;
    private readonly AppSettingsStore _settingsStore;
    private readonly PluginHostService _pluginHost;
    private readonly YouTubeQueueService _queueService;
    private readonly HeosEventListenerService _heosEvents;
    private readonly AppLogStore _logStore;
    private readonly string _habitsFilePath;

    private readonly object _sync = new();
    private readonly Stack<RoomStateSnapshot> _undoStack = new();
    private readonly HashSet<string> _dismissedInsightIds = new(StringComparer.OrdinalIgnoreCase);

    private RoomHabitsMemory _habits = new();
    private string _lastActiveReceiverId = string.Empty;
    private DateTimeOffset _lastHabitSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHabitSaveAt = DateTimeOffset.MinValue;
    private AcousticStatus? _cachedAcoustics;
    private DateTimeOffset _lastAcousticsRefreshUtc = DateTimeOffset.MinValue;
    private int _acousticsRefreshInFlight;
    private DateTimeOffset _lastHueMovieDimAt = DateTimeOffset.MinValue;
    private IReadOnlyList<RoomInsight> _currentInsights = [];

    public RoomIntelligenceService(
        ReceiverStateStore stateStore,
        ReceiverController receiverController,
        AppSettingsStore settingsStore,
        PluginHostService pluginHost,
        YouTubeQueueService queueService,
        HeosEventListenerService heosEvents,
        AppLogStore logStore,
        string? habitsFilePath = null)
    {
        _stateStore = stateStore;
        _receiverController = receiverController;
        _settingsStore = settingsStore;
        _pluginHost = pluginHost;
        _queueService = queueService;
        _heosEvents = heosEvents;
        _logStore = logStore;
        _habitsFilePath = string.IsNullOrWhiteSpace(habitsFilePath) ? AppDataPaths.RoomHabitsFilePath : habitsFilePath;

        LoadHabitsFromDisk();

        _stateStore.StateChanged += OnReceiverStateChanged;
        _pluginHost.PluginsChanged += RecomputeInsights;
        RecomputeInsights();
    }

    public event Action? InsightsChanged;

    public event Action? UndoStackChanged;

    public event Action? HabitsChanged;

    public event Action? RoomGraphChanged;

    public IReadOnlyList<RoomInsight> ActiveInsights
    {
        get
        {
            lock (_sync)
            {
                return _currentInsights.Where(i => !_dismissedInsightIds.Contains(i.Id)).ToList();
            }
        }
    }

    public bool CanUndo => LastSnapshot is not null;

    public RoomStateSnapshot? LastSnapshot
    {
        get
        {
            var activeId = GetCurrentReceiverIdentity().Id;
            lock (_sync)
            {
                if (_undoStack.Count == 0)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(activeId))
                {
                    return _undoStack.Peek();
                }

                return _undoStack.FirstOrDefault(s =>
                    string.Equals(s.ReceiverId, activeId, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public RoomStateSnapshot? CaptureSnapshot(string reason)
    {
        var live = _stateStore.Snapshot;
        var status = live.Status;
        if (!live.IsConnected || status is null || !status.IsAvrConfirmed)
        {
            _logStore.Warning($"[RoomBrain] Skipped Undo snapshot for '{reason}' because confirmed AVR state is unavailable.");
            return null;
        }

        var (receiverId, receiverName) = GetCurrentReceiverIdentity();
        var snap = new RoomStateSnapshot(
            CapturedAt: DateTimeOffset.UtcNow,
            Reason: reason,
            IsPoweredOn: status.IsPoweredOn,
            Volume: status.Volume,
            IsMuted: status.IsMuted,
            InputSource: status.InputSource,
            SoundMode: status.SoundMode,
            Zone2On: status.IsZone2On,
            ReceiverId: receiverId,
            ReceiverName: receiverName);

        lock (_sync)
        {
            _undoStack.Push(snap);
            if (_undoStack.Count > 10)
            {
                var keep = _undoStack.Take(10).Reverse().ToList();
                _undoStack.Clear();
                foreach (var item in keep)
                {
                    _undoStack.Push(item);
                }
            }
        }

        UndoStackChanged?.Invoke();
        return snap;
    }

    public Task<(bool Success, string Message)> RestoreLastSnapshotAsync(CancellationToken ct = default) =>
        RestoreLastSnapshotAsync(maxSafeVolume: null, ct);

    public async Task<(bool Success, string Message)> RestoreLastSnapshotAsync(int? maxSafeVolume, CancellationToken ct = default)
    {
        var (activeId, activeName) = GetCurrentReceiverIdentity();
        if (string.IsNullOrWhiteSpace(activeId))
        {
            return (false, "Cannot restore Undo snapshot: no confirmed receiver is currently connected.");
        }

        RoomStateSnapshot? target;
        lock (_sync)
        {
            // Scope Undo strictly to the currently selected/connected receiver so Room A's state
            // can NEVER be pushed onto Room B!
            target = _undoStack.FirstOrDefault(s =>
                string.Equals(s.ReceiverId, activeId, StringComparison.OrdinalIgnoreCase));
        }

        if (target is null)
        {
            return (false, $"No Undo snapshot available for '{activeName}'.");
        }

        var effectiveVolume = maxSafeVolume.HasValue
            ? Math.Min(target.Volume, maxSafeVolume.Value)
            : target.Volume;
        var wasVolumeCapped = maxSafeVolume.HasValue && target.Volume > maxSafeVolume.Value;

        try
        {
            if (!string.Equals(GetCurrentReceiverIdentity().Id, target.ReceiverId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Undo aborted: active receiver changed away from '{target.ReceiverName}'.");
            }

            await _receiverController.SetPowerAsync(target.IsPoweredOn, ct);
            if (target.IsPoweredOn)
            {
                // Establish safe mute/volume ordering BEFORE making an input audible:
                if (target.IsMuted)
                {
                    await _receiverController.SetMuteAsync(true, ct);
                }

                await _receiverController.SetVolumeAsync(effectiveVolume, ct);

                if (!string.IsNullOrWhiteSpace(target.InputSource))
                {
                    await _receiverController.SetInputSourceAsync(target.InputSource, ct);
                }

                if (!string.IsNullOrWhiteSpace(target.SoundMode))
                {
                    await _receiverController.SetSoundModeAsync(target.SoundMode, ct);
                }

                if (!target.IsMuted)
                {
                    await _receiverController.SetMuteAsync(false, ct);
                }
            }

            if (target.Zone2On.HasValue)
            {
                await _receiverController.SetZone2PowerAsync(target.Zone2On.Value, ct);
            }

            // Remove ONLY the restored snapshot after all restore commands succeeded!
            lock (_sync)
            {
                var remaining = _undoStack.Where(s => !ReferenceEquals(s, target)).Reverse().ToList();
                _undoStack.Clear();
                foreach (var item in remaining)
                {
                    _undoStack.Push(item);
                }
            }

            UndoStackChanged?.Invoke();

            var capNote = wasVolumeCapped ? $" (capped from {target.Volume} by MaxSafeVolume {maxSafeVolume})" : string.Empty;
            var msg = $"Restored '{target.ReceiverName}' from before '{target.Reason}' (Power: {(target.IsPoweredOn ? "ON" : "Standby")}, Vol: {effectiveVolume}{capNote}, Muted: {target.IsMuted}, Input: {target.InputSource}).";
            _logStore.Info($"[RoomBrain] {msg}");
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, $"Undo failed for '{target.ReceiverName}' (snapshot retained for retry): {ex.Message}");
        }
    }

    private (string Id, string Name) GetCurrentReceiverIdentity()
    {
        var live = _stateStore.Snapshot;
        if (!live.IsConnected || live.Status is null)
        {
            return (string.Empty, string.Empty);
        }

        var player = live.Status.Player;
        var host = !string.IsNullOrWhiteSpace(player.IpAddress)
            ? player.IpAddress.Trim()
            : _settingsStore.Current.Receiver.Host.Trim();
        var id = !string.IsNullOrWhiteSpace(player.Serial)
            ? $"{player.Serial.Trim()}|{host}"
            : $"{player.PlayerId}|{player.Model.Trim()}|{host}";
        var name = !string.IsNullOrWhiteSpace(player.Name)
            ? player.Name.Trim()
            : (!string.IsNullOrWhiteSpace(player.Model) ? player.Model.Trim() : host);
        return (id, name);
    }

    public void DismissInsight(string insightId)
    {
        lock (_sync)
        {
            _dismissedInsightIds.Add(insightId);
        }

        InsightsChanged?.Invoke();
    }

    public void ResetDismissedInsights()
    {
        lock (_sync)
        {
            _dismissedInsightIds.Clear();
        }

        RecomputeInsights();
    }

    public void UpdateCachedAcoustics(AcousticStatus acoustics)
    {
        lock (_sync)
        {
            _cachedAcoustics = acoustics;
            _lastAcousticsRefreshUtc = DateTimeOffset.UtcNow;
        }

        RecomputeInsights();
    }

    public IReadOnlyList<RoomInsight> DiagnoseRoom()
    {
        var list = new List<RoomInsight>();
        var snap = _stateStore.Snapshot;
        var status = snap.Status;
        var plugins = _pluginHost.GetPlugins();

        if (!snap.IsConnected || status is null)
        {
            list.Add(new RoomInsight(
                Id: "avr-offline",
                Severity: "Warning",
                Category: "Receiver",
                Title: "Denon AVR is unreachable or offline",
                Explanation: string.IsNullOrWhiteSpace(snap.LastError)
                    ? "Could not reach your Denon/Marantz receiver on the local LAN."
                    : snap.LastError!,
                FixLabel: "Reconnect AVR",
                FixActionKey: "avr.refresh"));
        }
        else
        {
            var isPlaying = status.PlayState.Equals("play", StringComparison.OrdinalIgnoreCase);

            // 1. Playing while muted
            if (status.IsPoweredOn && isPlaying && status.IsMuted)
            {
                list.Add(new RoomInsight(
                    Id: "avr-muted-playing",
                    Severity: "Warning",
                    Category: "Audio",
                    Title: "Audio is playing while Denon AVR is Muted",
                    Explanation: $"HEOS reports '{status.NowPlaying.Song}' is playing, but master audio is currently muted.",
                    FixLabel: "Unmute AVR",
                    FixActionKey: "avr.unmute"));
            }

            // 2. Playing at near-zero inaudible volume
            if (status.IsPoweredOn && isPlaying && !status.IsMuted && status.Volume < 8)
            {
                list.Add(new RoomInsight(
                    Id: "avr-inaudible-volume",
                    Severity: "Suggestion",
                    Category: "Audio",
                    Title: $"Volume is nearly silent ({status.Volume} / {FormatDb(status.Volume)})",
                    Explanation: "Playback is active at an inaudible volume level.",
                    FixLabel: "Set Vol 26",
                    FixActionKey: "avr.safe_volume"));
            }

            // 3. Late-night high volume guard (22:30 - 07:00)
            var nowHour = DateTime.Now.Hour + (DateTime.Now.Minute / 60.0);
            if (status.IsPoweredOn && (nowHour >= 22.5 || nowHour < 7.0) && status.Volume > 48 && !status.IsMuted)
            {
                list.Add(new RoomInsight(
                    Id: "late-night-loud",
                    Severity: "Suggestion",
                    Category: "Acoustics",
                    Title: $"Late-night volume is high ({status.Volume}.0)",
                    Explanation: "It's late — enabling Dynamic Volume (Heavy) and lowering to 34.0 keeps dialogue crisp without disturbing others.",
                    FixLabel: "Late-Night Mode",
                    FixActionKey: "avr.late_night_comfort"));
            }

            // 3b. Acoustic intelligence (using lazily cached Telnet acoustics)
            AcousticStatus? acoustics;
            DateTimeOffset lastHueDim;
            lock (_sync)
            {
                acoustics = _cachedAcoustics;
                lastHueDim = _lastHueMovieDimAt;
            }

            var isEveningOrNight = nowHour >= 19.0 || nowHour < 6.0;
            var isDaytime = nowHour >= 7.0 && nowHour < 21.5;

            if (status.IsPoweredOn && acoustics is not null)
            {
                var isCinemaMode =
                    status.SoundMode.Contains("MOVIE", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("DOLBY", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("NEURAL", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("ATMOS", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("DTS", StringComparison.OrdinalIgnoreCase);

                if (isEveningOrNight && isCinemaMode &&
                    acoustics.Dimmer.Equals("BRI", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new RoomInsight(
                        Id: "avr-bright-dimmer-cinema",
                        Severity: "Info",
                        Category: "Acoustics",
                        Title: "Denon front panel is at full brightness during evening viewing",
                        Explanation: $"Sound mode is {status.SoundMode} with front display set to Bright. Dimming it to Dark reduces glare beneath your TV.",
                        FixLabel: "Dim Front Panel",
                        FixActionKey: "avr.dim_panel"));
                }

                var isMusicListening =
                    status.InputSource.Equals("NET", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("CD", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("BT", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("PHONO", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("STEREO", StringComparison.OrdinalIgnoreCase) ||
                    status.SoundMode.Contains("DIRECT", StringComparison.OrdinalIgnoreCase);

                if (isDaytime && isMusicListening &&
                    acoustics.DynamicVolume.Equals("HEV", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new RoomInsight(
                        Id: "avr-heavy-dynvol-music",
                        Severity: "Suggestion",
                        Category: "Acoustics",
                        Title: "Heavy Night Compression (Dynamic Volume HEV) is active for music",
                        Explanation: "Dynamic Volume Heavy compresses musical dynamics for late-night listening. Turning it off restores full punch and range.",
                        FixLabel: "Dynamic Vol Off",
                        FixActionKey: "avr.dynvol_off"));
                }
            }

            // 4. Cross-device LG TV ↔ Denon AVR ↔ Philips Hue intelligence
            var tvPlugin = plugins.FirstOrDefault(p => p.Id.Equals("smart-tv", StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
            var huePlugin = plugins.FirstOrDefault(p => p.Id.Equals("philips-hue", StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
            if (tvPlugin is not null && tvPlugin.Health == PluginHealthState.Connected)
            {
                var tvStatus = tvPlugin.StatusText ?? string.Empty;
                var tvIsLiveApp = tvStatus.Contains("LIVE", StringComparison.OrdinalIgnoreCase) &&
                                  !tvStatus.Contains("Screen Off", StringComparison.OrdinalIgnoreCase);

                // 4a. Power-saving / OLED care when listening to dedicated audio-only sources (never when watching TV/video!)
                var isDedicatedAudioInput =
                    status.InputSource.Equals("NET", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("TUNER", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("BT", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("CD", StringComparison.OrdinalIgnoreCase) ||
                    status.InputSource.Equals("PHONO", StringComparison.OrdinalIgnoreCase);

                var tvShowingVideoOrBrowser =
                    tvStatus.Contains("Live TV", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Plex", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Disney", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("DRTV", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Viaplay", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Apple TV", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Prime Video", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("YouSee", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("SkyShowtime", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
                    tvStatus.Contains("Browser", StringComparison.OrdinalIgnoreCase);

                if (status.IsPoweredOn && isDedicatedAudioInput && isPlaying && tvIsLiveApp && !tvShowingVideoOrBrowser)
                {
                    var isOledTv = tvStatus.Contains("OLED", StringComparison.OrdinalIgnoreCase);
                    list.Add(new RoomInsight(
                        Id: "tv-screen-off-music",
                        Severity: "Info",
                        Category: isOledTv ? "OLED Care" : "Display",
                        Title: "Music is playing while your LG TV screen is idle",
                        Explanation: isOledTv
                            ? "Blank the LG OLED panel (Audio-Only Mode) to prevent burn-in and save power while music keeps playing."
                            : "Turn off the LG TV display (Audio-Only Mode) to save power while music keeps playing.",
                        FixLabel: "Screen Off",
                        FixActionKey: "tv.screen_off"));
                }

                // 4b. TV watching Netflix/YouTube/Live TV while Denon is on a non-TV input
                var tvWatchingMedia = tvStatus.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                                      tvStatus.Contains("Live TV", StringComparison.OrdinalIgnoreCase) ||
                                      tvStatus.Contains("Plex", StringComparison.OrdinalIgnoreCase) ||
                                      tvStatus.Contains("Disney", StringComparison.OrdinalIgnoreCase);

                if (status.IsPoweredOn && tvWatchingMedia &&
                    !status.InputSource.Equals("TV", StringComparison.OrdinalIgnoreCase) &&
                    !isPlaying)
                {
                    list.Add(new RoomInsight(
                        Id: "tv-audio-mismatch",
                        Severity: "Suggestion",
                        Category: "Cross-Device",
                        Title: "LG TV is playing video, but Denon isn't on TV Audio",
                        Explanation: $"TV status is '{tvStatus}', while Denon input is '{status.InputSource}'. Route TV eARC straight to Denon!",
                        FixLabel: "Route TV → eARC",
                        FixActionKey: "room.route_tv_earc"));
                }

                // 4c. Evening movie app active while Philips Hue lights are still on
                var tvWatchingMovieApp =
                    tvIsLiveApp &&
                    (tvStatus.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                     tvStatus.Contains("Disney", StringComparison.OrdinalIgnoreCase) ||
                     tvStatus.Contains("Plex", StringComparison.OrdinalIgnoreCase) ||
                     tvStatus.Contains("Apple TV", StringComparison.OrdinalIgnoreCase) ||
                     tvStatus.Contains("Prime Video", StringComparison.OrdinalIgnoreCase) ||
                     tvStatus.Contains("SkyShowtime", StringComparison.OrdinalIgnoreCase));

                if (isEveningOrNight && tvWatchingMovieApp &&
                    huePlugin is not null && huePlugin.Health == PluginHealthState.Connected &&
                    (DateTimeOffset.UtcNow - lastHueDim) > TimeSpan.FromMinutes(15))
                {
                    var hueStatus = huePlugin.StatusText ?? string.Empty;
                    var hasLightsOn = hueStatus.Contains("lights on", StringComparison.OrdinalIgnoreCase) &&
                                      !hueStatus.Contains(" 0/", StringComparison.OrdinalIgnoreCase) &&
                                      !hueStatus.StartsWith("0/", StringComparison.OrdinalIgnoreCase);
                    if (hasLightsOn)
                    {
                        list.Add(new RoomInsight(
                            Id: "hue-bright-movie-evening",
                            Severity: "Suggestion",
                            Category: "Atmosphere",
                            Title: "Philips Hue lights are on during evening movie viewing",
                            Explanation: $"TV is showing '{tvStatus}' while Hue reports '{hueStatus}'. Dim the room to Movie Night ambiance in one click.",
                            FixLabel: "Dim for Movie",
                            FixActionKey: "hue.movie_dim"));
                    }
                }
            }
        }

        // 5. Any enabled plugin in Warning / Error state
        foreach (var plugin in plugins.Where(p => p.IsEnabled && p.Health is PluginHealthState.Warning or PluginHealthState.Error))
        {
            list.Add(new RoomInsight(
                Id: $"plugin-warn-{plugin.Id}",
                Severity: "Warning",
                Category: "Plugins",
                Title: $"{plugin.Name} needs attention",
                Explanation: string.IsNullOrWhiteSpace(plugin.LastError) ? plugin.StatusText : plugin.LastError!,
                FixLabel: $"Open {plugin.IconGlyph}",
                FixActionKey: $"open.plugin:{plugin.Id}"));
        }

        lock (_sync)
        {
            _currentInsights = list;
        }

        return list;
    }

    public async Task<(bool Success, string Message)> ExecuteInsightFixAsync(string fixActionKey, CancellationToken ct = default)
    {
        switch (fixActionKey.ToLowerInvariant())
        {
            case "avr.refresh":
                await _receiverController.RefreshAsync(ct);
                RecomputeInsights();
                return (true, "Refreshed Denon AVR connection.");

            case "avr.unmute":
                CaptureSnapshot("Auto-Fix: Unmute AVR");
                await _receiverController.SetMuteAsync(false, ct);
                RecomputeInsights();
                return (true, "Unmuted Denon AVR.");

            case "avr.safe_volume":
                CaptureSnapshot("Auto-Fix: Set Comfortable Volume");
                await _receiverController.SetVolumeAsync(26, ct);
                RecomputeInsights();
                return (true, "Raised Denon AVR volume to 26 (-54.0 dB).");

            case "avr.late_night_comfort":
                CaptureSnapshot("Auto-Fix: Late-Night Comfort");
                await _receiverController.SetVolumeAsync(34, ct);
                await _receiverController.SetDynamicVolumeAsync("HEV", ct);
                RecomputeInsights();
                return (true, "Applied Late-Night Comfort: Volume 34 (-46.0 dB) + Audyssey Dynamic Volume Heavy.");

            case "tv.screen_off":
            {
                var res = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.screen_off", null, ct);
                RecomputeInsights();
                return (res.Success, res.Message);
            }

            case "room.route_tv_earc":
            {
                CaptureSnapshot("Auto-Fix: Route TV Audio to Denon eARC");
                await _receiverController.SetInputSourceAsync("TV", ct);
                await _receiverController.SetMuteAsync(false, ct);
                var tvRes = await _pluginHost.ExecuteActionAsync("smart-tv", "tv.audio_earc", null, ct);
                RecomputeInsights();
                return (true, $"Switched Denon to TV Audio & LG TV to HDMI eARC ({tvRes.Message}).");
            }

            case "avr.fix_silent":
                CaptureSnapshot("Auto-Fix: Silent Audio");
                await _receiverController.FixSilentAudioAsync(30, ct);
                RecomputeInsights();
                return (true, "Ran Fix Silent Audio (powered ON, unmuted, set audible volume).");

            case "avr.dim_panel":
                await _receiverController.SetDimmerAsync("DAR", ct);
                lock (_sync)
                {
                    if (_cachedAcoustics is not null)
                    {
                        _cachedAcoustics = _cachedAcoustics with { Dimmer = "DAR" };
                    }
                }
                RecomputeInsights();
                return (true, "Dimmed Denon front panel display to Dark.");

            case "avr.dynvol_off":
                await _receiverController.SetDynamicVolumeAsync("OFF", ct);
                lock (_sync)
                {
                    if (_cachedAcoustics is not null)
                    {
                        _cachedAcoustics = _cachedAcoustics with { DynamicVolume = "OFF" };
                    }
                }
                RecomputeInsights();
                return (true, "Turned Audyssey Dynamic Volume OFF for full musical dynamics.");

            case "hue.movie_dim":
            {
                var res = await _pluginHost.ExecuteActionAsync("philips-hue", "hue.movie_dim", null, ct);
                if (res.Success)
                {
                    lock (_sync)
                    {
                        _lastHueMovieDimAt = DateTimeOffset.UtcNow;
                    }
                    RecordPluginActionUsed("philips-hue", "hue.movie_dim");
                }
                RecomputeInsights();
                return (res.Success, res.Message);
            }

            default:
                return (false, $"No automatic fix handler for '{fixActionKey}'.");
        }
    }

    public void RecordSceneUsed(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            return;
        }

        lock (_sync)
        {
            _habits.SceneUsageCounts[sceneName] = _habits.SceneUsageCounts.GetValueOrDefault(sceneName) + 1;
            _habits.UpdatedAt = DateTimeOffset.UtcNow;
        }

        SaveHabitsDebounced();
    }

    public void RecordPluginActionUsed(string pluginId, string actionId)
    {
        var key = $"{pluginId}:{actionId}";
        lock (_sync)
        {
            _habits.PluginActionCounts[key] = _habits.PluginActionCounts.GetValueOrDefault(key) + 1;
            _habits.UpdatedAt = DateTimeOffset.UtcNow;
        }

        SaveHabitsDebounced();
    }

    public object BuildRoomStatePayload()
    {
        var snap = _stateStore.Snapshot;
        var st = snap.Status;
        var settings = _settingsStore.Current;
        var plugins = _pluginHost.GetPlugins();
        var insights = DiagnoseRoom();
        var queue = _queueService.Snapshot.Queue;
        AcousticStatus? acoustics;
        lock (_sync)
        {
            acoustics = _cachedAcoustics;
        }

        var resolvedNowPlaying = st is null
            ? null
            : NowPlayingResolver.Resolve(
                st,
                activeStationName: !string.IsNullOrWhiteSpace(st.ActiveStationAlias)
                    ? settings.Receiver.Stations.FirstOrDefault(s => string.Equals(s.Alias, st.ActiveStationAlias, StringComparison.OrdinalIgnoreCase))?.DisplayName
                    : null,
                plugins: plugins,
                activeYouTubeTrack: _queueService.IsYouTubePlaybackActive ? _queueService.Snapshot.CurrentTrack : null);

        return new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            timeBlock = GetCurrentTimeBlock(),
            receiver = new
            {
                connected = snap.IsConnected,
                livePushActive = _heosEvents.IsPushConnected,
                host = settings.Receiver.Host,
                model = st?.Player.Model ?? string.Empty,
                name = st?.Player.Name ?? string.Empty,
                firmware = st?.Player.Version ?? string.Empty,
                powerOn = st?.IsPoweredOn ?? false,
                mainZoneOn = st?.IsMainZoneOn ?? false,
                zone2On = st?.IsZone2On ?? false,
                volume = st?.Volume ?? 0,
                volumeDb = st is not null ? FormatDb(st.Volume) : "—",
                muted = st?.IsMuted ?? false,
                inputSource = st?.InputSource ?? string.Empty,
                soundMode = st?.SoundMode ?? string.Empty,
                ecoMode = st?.EcoMode ?? string.Empty,
                sleepTimerMinutes = st?.SleepTimerMinutes,
                playState = resolvedNowPlaying?.PlayStateLabel ?? st?.PlayState ?? "unknown",
                nowPlaying = st is null ? null : new
                {
                    title = resolvedNowPlaying?.Title ?? st.NowPlaying.Song,
                    detail = resolvedNowPlaying?.Detail ?? string.Empty,
                    song = st.NowPlaying.IsPlaceholderUrlStream && !DenonCatalog.InputMatches(st.InputSource, "NET") ? string.Empty : st.NowPlaying.Song,
                    station = st.NowPlaying.Station,
                    artist = st.NowPlaying.IsPlaceholderUrlStream && !DenonCatalog.InputMatches(st.InputSource, "NET") ? string.Empty : st.NowPlaying.Artist,
                    album = st.NowPlaying.IsPlaceholderUrlStream && !DenonCatalog.InputMatches(st.InputSource, "NET") ? string.Empty : st.NowPlaying.Album,
                    service = DenonCatalog.GetHeosSourceName(st.NowPlaying.SourceId),
                    type = st.NowPlaying.Type,
                    // Live TV on the LG TV: the channel, its DVB IDs and the program from the TV's guide.
                    liveTv = resolvedNowPlaying?.LiveTv is { } live
                        ? new
                        {
                            channelName = live.ChannelName,
                            channelNumber = live.ChannelNumber,
                            channelId = live.ChannelId,
                            signalChannelId = live.SignalChannelId,
                            programName = live.ProgramName,
                            programStart = live.ProgramStart?.ToString("O"),
                            programEnd = live.ProgramEnd?.ToString("O"),
                            nextProgramName = live.NextProgramName,
                            nextProgramStart = live.NextProgramStart?.ToString("O"),
                            logo = live.LogoFileName,
                        }
                        : null,
                },
                acoustics = acoustics is null ? null : new
                {
                    multEq = acoustics.MultEq,
                    dynamicEq = acoustics.DynamicEq,
                    dynamicVolume = acoustics.DynamicVolume,
                    restorer = acoustics.Restorer,
                    dimmer = acoustics.Dimmer,
                    subwooferTrimDb = acoustics.SubwooferTrimDb,
                    dialogTrimDb = acoustics.DialogTrimDb,
                },
                lastError = snap.LastError,
            },
            mediaQueue = new
            {
                count = queue.Count,
                upNext = queue.Take(8).Select(q => new { title = q.Title, author = q.Author, duration = q.Duration?.ToString(@"m\:ss") ?? "LIVE" }).ToArray(),
            },
            plugins = plugins.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                version = p.Version,
                enabled = p.IsEnabled,
                health = p.Health.ToString(),
                status = p.StatusText,
                lastError = p.LastError,
                actionsCount = p.Actions.Count,
            }).ToArray(),
            insights = insights.Select(i => new
            {
                id = i.Id,
                severity = i.Severity,
                category = i.Category,
                title = i.Title,
                explanation = i.Explanation,
                fixActionKey = i.FixActionKey,
                fixLabel = i.FixLabel,
            }).ToArray(),
            undoAvailable = CanUndo,
            lastSnapshot = LastSnapshot is { } ls ? new { ls.CapturedAt, ls.Reason, ls.Volume, ls.InputSource, ls.SoundMode } : null,
            safety = new
            {
                permissionTier = settings.Mcp.PermissionTier.ToString(),
                maxSafeVolume = settings.Mcp.MaxSafeVolume,
                maxSafeVolumeDb = FormatDb(settings.Mcp.MaxSafeVolume),
            },
        };
    }

    public object BuildDiagnosticsPayload()
    {
        var logs = _logStore.Snapshot();
        var recentIssues = logs
            .Where(l => l.Level is AppLogLevel.Warning or AppLogLevel.Error)
            .TakeLast(15)
            .Select(l => new { timestamp = l.Timestamp.ToString("O"), level = l.Level.ToString(), message = l.Message })
            .ToArray();

        return new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            receiverConnected = _stateStore.Snapshot.IsConnected,
            livePushConnected = _heosEvents.IsPushConnected,
            insights = DiagnoseRoom(),
            recentWarningsAndErrors = recentIssues,
        };
    }

    public object BuildCapabilitiesPayload()
    {
        var settings = _settingsStore.Current;
        return new
        {
            inputs = DenonCatalog.InputSources.Select(i => new { code = i.Code, name = i.DisplayName }).ToArray(),
            soundModes = DenonCatalog.SoundModes.Select(m => new { code = m.Code, name = m.DisplayName }).ToArray(),
            smartScenes = settings.Receiver.Scenes.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                description = s.Description,
                input = s.InputSource,
                soundMode = s.SoundMode,
                targetVolume = s.TargetVolume,
                pluginActions = s.PluginActions.Select(a => $"{a.PluginId}:{a.ActionId}").ToArray(),
            }).ToArray(),
            automationRules = settings.Automations.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                enabled = r.IsEnabled,
                trigger = r.TriggerType.ToString(),
                processes = r.ProcessNames,
                scheduleTime = r.ScheduleTime,
                days = r.DaysFilter,
                targetSceneId = r.TargetSceneId,
            }).ToArray(),
            plugins = _pluginHost.GetPlugins().Select(p => new
            {
                id = p.Id,
                name = p.Name,
                enabled = p.IsEnabled,
                actions = p.Actions.Select(a => new { id = a.ActionId, name = a.DisplayName, description = a.Description, category = a.Category }).ToArray(),
            }).ToArray(),
        };
    }

    public object BuildHabitsPayload()
    {
        lock (_sync)
        {
            return new
            {
                updatedAt = _habits.UpdatedAt,
                totalObservations = _habits.TotalObservations,
                currentTimeBlock = GetCurrentTimeBlock(),
                preferredVolumeByTimeBlock = _habits.VolumeByTimeBlock.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        samples = kv.Value.SampleCount,
                        averageVolume = Math.Round(kv.Value.AverageVolume, 1),
                        averageDb = FormatDb((int)Math.Round(kv.Value.AverageVolume)),
                        minVolume = kv.Value.MinVolume,
                        maxVolume = kv.Value.MaxVolume,
                    }),
                topInputs = _habits.InputUsageCounts.OrderByDescending(kv => kv.Value).Take(5).ToDictionary(kv => kv.Key, kv => kv.Value),
                topSoundModes = _habits.SoundModeUsageCounts.OrderByDescending(kv => kv.Value).Take(5).ToDictionary(kv => kv.Key, kv => kv.Value),
                topScenes = _habits.SceneUsageCounts.OrderByDescending(kv => kv.Value).Take(5).ToDictionary(kv => kv.Key, kv => kv.Value),
                topPluginActions = _habits.PluginActionCounts.OrderByDescending(kv => kv.Value).Take(8).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
        }
    }

    public string GetHabitsHumanSummary()
    {
        lock (_sync)
        {
            if (_habits.TotalObservations == 0)
            {
                return "Learning your local listening patterns as you use Denon Desktop (100% local, zero cloud).";
            }

            var parts = new List<string>();
            foreach (var block in new[] { "Morning", "Afternoon", "Evening", "LateNight" })
            {
                if (_habits.VolumeByTimeBlock.TryGetValue(block, out var b) && b.SampleCount > 0)
                {
                    var avg = (int)Math.Round(b.AverageVolume);
                    parts.Add($"{block}: ~{avg} ({FormatDb(avg)})");
                }
            }

            var topInput = _habits.InputUsageCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
            var summary = parts.Count > 0 ? string.Join(" • ", parts) : $"{_habits.TotalObservations} observations recorded";
            if (!string.IsNullOrWhiteSpace(topInput))
            {
                summary += $"  |  Top input: {topInput}";
            }

            return summary;
        }
    }

    public static string FormatDb(int volume)
    {
        var db = volume - 80.0;
        return db >= 0
            ? $"+{db.ToString("0.0", CultureInfo.InvariantCulture)} dB"
            : $"{db.ToString("0.0", CultureInfo.InvariantCulture)} dB";
    }

    public static string GetCurrentTimeBlock() => DateTime.Now.Hour switch
    {
        >= 6 and < 12 => "Morning",
        >= 12 and < 17 => "Afternoon",
        >= 17 and < 22 => "Evening",
        _ => "LateNight",
    };

    private void OnReceiverStateChanged(ReceiverStateSnapshot snap)
    {
        var currentReceiverId = GetCurrentReceiverIdentity().Id;
        if (!string.Equals(_lastActiveReceiverId, currentReceiverId, StringComparison.OrdinalIgnoreCase))
        {
            _lastActiveReceiverId = currentReceiverId;
            UndoStackChanged?.Invoke();
        }

        if (snap.IsConnected && snap.Status is { IsPoweredOn: true } st)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastHabitSampleAt) >= TimeSpan.FromMinutes(2))
            {
                _lastHabitSampleAt = now;
                RecordLiveObservation(st);
            }

            if ((now - _lastAcousticsRefreshUtc) >= TimeSpan.FromSeconds(90) &&
                Interlocked.CompareExchange(ref _acousticsRefreshInFlight, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        var acoustics = await _receiverController.GetAcousticStatusAsync(cts.Token);
                        UpdateCachedAcoustics(acoustics);
                    }
                    catch
                    {
                        lock (_sync)
                        {
                            _lastAcousticsRefreshUtc = DateTimeOffset.UtcNow;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _acousticsRefreshInFlight, 0);
                    }
                });
            }
        }

        RecomputeInsights();
    }

    private void RecordLiveObservation(ReceiverStatus st)
    {
        var block = GetCurrentTimeBlock();
        lock (_sync)
        {
            _habits.TotalObservations++;
            _habits.UpdatedAt = DateTimeOffset.UtcNow;

            if (!st.IsMuted && st.Volume > 5)
            {
                if (!_habits.VolumeByTimeBlock.TryGetValue(block, out var bucket))
                {
                    bucket = new VolumeHabitBucket();
                    _habits.VolumeByTimeBlock[block] = bucket;
                }

                bucket.AverageVolume = ((bucket.AverageVolume * bucket.SampleCount) + st.Volume) / (bucket.SampleCount + 1);
                bucket.SampleCount++;
                bucket.MinVolume = Math.Min(bucket.MinVolume, st.Volume);
                bucket.MaxVolume = Math.Max(bucket.MaxVolume, st.Volume);
            }

            if (!string.IsNullOrWhiteSpace(st.InputSource))
            {
                _habits.InputUsageCounts[st.InputSource] = _habits.InputUsageCounts.GetValueOrDefault(st.InputSource) + 1;
            }

            if (!string.IsNullOrWhiteSpace(st.SoundMode))
            {
                _habits.SoundModeUsageCounts[st.SoundMode] = _habits.SoundModeUsageCounts.GetValueOrDefault(st.SoundMode) + 1;
            }
        }

        SaveHabitsDebounced();
        HabitsChanged?.Invoke();
    }

    private void RecomputeInsights()
    {
        DiagnoseRoom();
        InsightsChanged?.Invoke();
        RoomGraphChanged?.Invoke();
    }

    private void LoadHabitsFromDisk()
    {
        try
        {
            if (!File.Exists(_habitsFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_habitsFilePath);
            var loaded = JsonSerializer.Deserialize<RoomHabitsMemory>(json, JsonOptions);
            if (loaded is not null)
            {
                _habits = loaded;
            }
        }
        catch
        {
            // Non-fatal: start fresh if corrupted.
        }
    }

    private void SaveHabitsDebounced()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastHabitSaveAt) < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastHabitSaveAt = now;
        _ = Task.Run(() =>
        {
            try
            {
                string json;
                lock (_sync)
                {
                    json = JsonSerializer.Serialize(_habits, JsonOptions);
                }

                var dir = Path.GetDirectoryName(_habitsFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_habitsFilePath, json);
            }
            catch
            {
                // Best-effort local persistence.
            }
        });
    }
}

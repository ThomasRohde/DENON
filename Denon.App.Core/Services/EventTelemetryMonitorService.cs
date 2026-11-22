using System.Globalization;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Denon.App.PluginSdk;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

/// <summary>
/// Smart background telemetry engine:
/// - Records ONLY real state deltas across AVR, TV, Lighting, and Media (zero heartbeat spam).
/// - Coalesces rapid volume knob/remote steps into a single `volume.settled` event (default 3s).
/// - Tracks AVR/HEOS/YouTube/Radio/Phone-cast and LG TV watching sessions with a 15-second dwell
///   filter so rapid channel surfing or track skipping never pollutes habit logs.
/// - Runs an adaptive background probe (15s active / 60s standby) plus periodic Hue bridge sync
///   to capture external remote, wall-switch, and phone-app usage.
/// </summary>
public sealed class EventTelemetryMonitorService : BackgroundService
{
    private readonly EventTelemetryLog _telemetry;
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly YouTubeQueueService _youTubeQueueService;
    private readonly PluginHostService _pluginHostService;
    private readonly AppLogStore _logStore;
    private readonly object _sync = new();

    private ReceiverStatus? _lastAvr;
    private bool? _lastConnected;
    private string _avrDeviceName = "avr";

    // Volume coalescer state
    private double? _volPendingStart;
    private double _volPendingCurrent;
    private DateTimeOffset _volLastChangeUtc;
    private string _volOrigin = "external";
    private string _volInput = string.Empty;
    private string _volSoundMode = string.Empty;

    // Media session slots (15s dwell filter)
    private readonly MediaSessionSlot _avrSession = new("avr");
    private readonly MediaSessionSlot _tvSession = new("lg-tv");

    // Lighting delta cache
    private string? _lastHueStatus;
    private DateTimeOffset _lastHueRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProbeEvalUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.UtcNow;

    public EventTelemetryMonitorService(
        EventTelemetryLog telemetry,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        YouTubeQueueService youTubeQueueService,
        PluginHostService pluginHostService,
        AppLogStore logStore)
    {
        _telemetry = telemetry;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _youTubeQueueService = youTubeQueueService;
        _pluginHostService = pluginHostService;
        _logStore = logStore;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _stateStore.StateChanged += OnReceiverStateChanged;
        _youTubeQueueService.QueueChanged += OnYouTubeQueueChanged;
        _pluginHostService.PluginsChanged += OnPluginsChanged;

        // Capture any state that is already present on startup.
        ProcessReceiverSnapshot(_stateStore.Snapshot);
        ProcessPluginSnapshots();

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stateStore.StateChanged -= OnReceiverStateChanged;
        _youTubeQueueService.QueueChanged -= OnYouTubeQueueChanged;
        _pluginHostService.PluginsChanged -= OnPluginsChanged;

        FlushShutdownState();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var cfg = _settingsStore.Current.Telemetry;
                if (!cfg.Enabled)
                {
                    continue;
                }

                bool isSystemAwake;
                lock (_sync)
                {
                    EvaluateVolumeSettleLocked(nowUtc, cfg.VolumeSettleMs);
                    EvaluateMediaDwellLocked(_avrSession, nowUtc, cfg.MediaDwellSeconds);
                    EvaluateMediaDwellLocked(_tvSession, nowUtc, cfg.MediaDwellSeconds);
                    isSystemAwake = (_lastAvr is { IsPoweredOn: true, IsMainZoneOn: true }) || IsTvActiveLocked();
                }

                // Adaptive periodic probe (default 15s active, 60s standby)
                var probeInterval = TimeSpan.FromSeconds(
                    isSystemAwake ? cfg.ActiveProbeIntervalSeconds : cfg.StandbyProbeIntervalSeconds);

                if (nowUtc - _lastProbeEvalUtc >= probeInterval)
                {
                    _lastProbeEvalUtc = nowUtc;
                    ProcessReceiverSnapshot(_stateStore.Snapshot);
                    ProcessPluginSnapshots();
                    SampleActiveSessionVolume();
                }

                // Periodic Hue Bridge poll (60s active / 5m standby) to capture external wall-switch & Hue phone-app changes
                var hueInterval = isSystemAwake ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);
                if (nowUtc - _lastHueRefreshUtc >= hueInterval)
                {
                    _lastHueRefreshUtc = nowUtc;
                    _ = RefreshHueSilentlyAsync(stoppingToken);
                }

                // Daily retention & disk-cap pruning
                if (nowUtc - _lastPruneUtc >= TimeSpan.FromHours(24))
                {
                    _lastPruneUtc = nowUtc;
                    _telemetry.PruneOldFiles();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logStore.Warning($"[Telemetry] Monitor loop warning: {ex.Message}");
        }
    }

    private void OnReceiverStateChanged(ReceiverStateSnapshot snapshot)
    {
        if (!_telemetry.IsEnabled)
        {
            return;
        }

        ProcessReceiverSnapshot(snapshot);
    }

    private void OnYouTubeQueueChanged(YouTubeQueueSnapshot _)
    {
        if (!_telemetry.IsEnabled)
        {
            return;
        }

        ProcessReceiverSnapshot(_stateStore.Snapshot);
    }

    private void OnPluginsChanged()
    {
        if (!_telemetry.IsEnabled)
        {
            return;
        }

        ProcessPluginSnapshots();
    }

    private void ProcessReceiverSnapshot(ReceiverStateSnapshot snapshot)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var ytSnapshot = _youTubeQueueService.Snapshot;

        lock (_sync)
        {
            if (_lastConnected.HasValue && _lastConnected.Value != snapshot.IsConnected)
            {
                _telemetry.Record(
                    EventTelemetryLog.StreamAvr,
                    _avrDeviceName,
                    "connection.changed",
                    "system",
                    new Dictionary<string, object?>
                    {
                        ["connected"] = snapshot.IsConnected,
                        ["model"] = snapshot.Status?.Player.Model,
                        ["ip"] = snapshot.Status?.Player.IpAddress,
                    });
            }

            _lastConnected = snapshot.IsConnected;

            var current = snapshot.Status;
            if (!snapshot.IsConnected || current is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(current.Player.Model))
            {
                _avrDeviceName = current.Player.Model.Trim();
            }

            var previous = _lastAvr;
            _lastAvr = current;

            if (previous is not null)
            {
                EmitAvrDeltasLocked(previous, current, nowUtc);
            }

            var avrCandidate = BuildAvrMediaCandidate(current, ytSnapshot, nowUtc);
            var endReason = (!current.IsPoweredOn || !current.IsMainZoneOn) ? "standby" : "stopped";
            UpdateMediaSlotLocked(_avrSession, avrCandidate, nowUtc, endReason);
        }
    }

    private void EmitAvrDeltasLocked(ReceiverStatus prev, ReceiverStatus cur, DateTimeOffset nowUtc)
    {
        var origin = _telemetry.ResolveOrigin("avr");
        var curVol = GetEffectiveVolume(cur);
        var prevVol = GetEffectiveVolume(prev);

        if (prev.IsPoweredOn != cur.IsPoweredOn || prev.IsMainZoneOn != cur.IsMainZoneOn)
        {
            var prevActive = prev.IsPoweredOn && prev.IsMainZoneOn;
            var curActive = cur.IsPoweredOn && cur.IsMainZoneOn;
            if (prevActive != curActive)
            {
                _telemetry.Record(
                    EventTelemetryLog.StreamAvr,
                    _avrDeviceName,
                    "power.changed",
                    origin,
                    new Dictionary<string, object?>
                    {
                        ["from"] = prevActive ? "on" : "standby",
                        ["to"] = curActive ? "on" : "standby",
                        ["input"] = cur.InputSource,
                        ["volume"] = Math.Round(curVol, 1),
                    });
            }
        }

        if (prev.IsZone2On != cur.IsZone2On)
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "zone2.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["from"] = prev.IsZone2On ? "on" : "off",
                    ["to"] = cur.IsZone2On ? "on" : "off",
                });
        }

        if (!string.Equals(prev.InputSource, cur.InputSource, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cur.InputSource))
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "input.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["from"] = prev.InputSource,
                    ["to"] = cur.InputSource,
                    ["friendlyName"] = DenonCatalog.GetFriendlyInputName(cur.InputSource),
                    ["soundMode"] = cur.SoundMode,
                    ["volume"] = Math.Round(curVol, 1),
                });
        }

        if (!string.Equals(prev.SoundMode, cur.SoundMode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cur.SoundMode))
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "sound_mode.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["from"] = prev.SoundMode,
                    ["to"] = cur.SoundMode,
                    ["input"] = cur.InputSource,
                });
        }

        if (prev.IsMuted != cur.IsMuted)
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "mute.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["muted"] = cur.IsMuted,
                    ["volume"] = Math.Round(curVol, 1),
                    ["input"] = cur.InputSource,
                });
        }

        if (!string.Equals(prev.EcoMode, cur.EcoMode, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cur.EcoMode))
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "eco.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["from"] = prev.EcoMode,
                    ["to"] = cur.EcoMode,
                });
        }

        if (prev.SleepTimerMinutes != cur.SleepTimerMinutes)
        {
            _telemetry.Record(
                EventTelemetryLog.StreamAvr,
                _avrDeviceName,
                "sleep_timer.changed",
                origin,
                new Dictionary<string, object?>
                {
                    ["minutes"] = cur.SleepTimerMinutes ?? 0,
                });
        }

        // Coalesce rapid volume changes into a single `volume.settled` event after VolumeSettleMs.
        if (cur.IsPoweredOn && Math.Abs(curVol - prevVol) >= 0.4)
        {
            if (!_volPendingStart.HasValue)
            {
                _volPendingStart = prevVol;
                _volOrigin = origin;
            }
            else if (origin != "external")
            {
                _volOrigin = origin;
            }

            _volPendingCurrent = curVol;
            _volLastChangeUtc = nowUtc;
            _volInput = cur.InputSource;
            _volSoundMode = cur.SoundMode;
        }
    }

    private void EvaluateVolumeSettleLocked(DateTimeOffset nowUtc, int settleMs)
    {
        if (!_volPendingStart.HasValue)
        {
            return;
        }

        if ((nowUtc - _volLastChangeUtc).TotalMilliseconds < settleMs)
        {
            return;
        }

        FlushPendingVolumeLocked();
    }

    private void FlushPendingVolumeLocked()
    {
        if (!_volPendingStart.HasValue)
        {
            return;
        }

        var from = Math.Round(_volPendingStart.Value, 1);
        var to = Math.Round(_volPendingCurrent, 1);
        var origin = _volOrigin;
        var input = _volInput;
        var soundMode = _volSoundMode;

        _volPendingStart = null;

        if (Math.Abs(to - from) < 0.4)
        {
            return;
        }

        _telemetry.Record(
            EventTelemetryLog.StreamAvr,
            _avrDeviceName,
            "volume.settled",
            origin,
            new Dictionary<string, object?>
            {
                ["from"] = from,
                ["to"] = to,
                ["delta"] = Math.Round(to - from, 1),
                ["input"] = input,
                ["soundMode"] = soundMode,
            });
    }

    private MediaCandidate? BuildAvrMediaCandidate(
        ReceiverStatus status,
        YouTubeQueueSnapshot ytSnapshot,
        DateTimeOffset nowUtc)
    {
        if (!status.IsPoweredOn || !status.IsMainZoneOn)
        {
            return null;
        }

        var input = status.InputSource?.Trim() ?? string.Empty;
        var isNet = DenonCatalog.InputMatches(input, "NET");
        var vol = Math.Round(GetEffectiveVolume(status), 1);

        if (isNet)
        {
            var isPlaying = string.Equals(status.PlayState, "play", StringComparison.OrdinalIgnoreCase);
            if (!isPlaying)
            {
                return null;
            }

            // 1. Desktop YouTube Music / Video playback (only when HEOS is not playing a distinct external phone stream)
            if (ytSnapshot.CurrentTrack is { } ytTrack && !status.NowPlaying.HasRealMetadata)
            {
                var origin = _telemetry.ResolveOrigin("avr", fallback: "desktop_ui");
                return new MediaCandidate(
                    Key: $"yt:{ytTrack.VideoId}",
                    Kind: "youtube",
                    Title: ytTrack.Title,
                    Artist: ytTrack.Author,
                    Album: null,
                    Source: "youtube",
                    Origin: origin,
                    StartedUtc: nowUtc,
                    InitialVolume: vol,
                    ExtraData: new Dictionary<string, object?>
                    {
                        ["videoId"] = ytTrack.VideoId,
                        ["input"] = "NET",
                        ["soundMode"] = status.SoundMode,
                    });
            }

            // 2. Internet Radio station launched via app / alias
            if (!string.IsNullOrWhiteSpace(status.ActiveStationAlias))
            {
                var song = CleanText(status.NowPlaying.Song);
                var stationLabel = CleanText(status.NowPlaying.Station);
                var title = !string.IsNullOrWhiteSpace(song) && !string.Equals(song, "Url Stream", StringComparison.OrdinalIgnoreCase)
                    ? song
                    : (!string.IsNullOrWhiteSpace(stationLabel) ? stationLabel : status.ActiveStationAlias);
                var origin = _telemetry.ResolveOrigin("avr");
                return new MediaCandidate(
                    Key: $"radio:{status.ActiveStationAlias.ToLowerInvariant()}",
                    Kind: "radio",
                    Title: title,
                    Artist: CleanText(status.NowPlaying.Artist),
                    Album: CleanText(status.NowPlaying.Album),
                    Source: status.ActiveStationAlias,
                    Origin: origin,
                    StartedUtc: nowUtc,
                    InitialVolume: vol,
                    ExtraData: new Dictionary<string, object?>
                    {
                        ["station"] = status.ActiveStationAlias,
                        ["input"] = "NET",
                        ["soundMode"] = status.SoundMode,
                    });
            }

            // 3. External HEOS / Phone Cast (Spotify Connect, AirPlay, TuneIn, HEOS Phone App, USB, DLNA)
            var npSong = CleanText(status.NowPlaying.Song);
            var npStation = CleanText(status.NowPlaying.Station);
            var npArtist = CleanText(status.NowPlaying.Artist);
            var npAlbum = CleanText(status.NowPlaying.Album);
            var npType = CleanText(status.NowPlaying.Type);

            if (status.NowPlaying.IsPlaceholderUrlStream ||
                (string.IsNullOrWhiteSpace(npSong) && string.IsNullOrWhiteSpace(npStation) && string.IsNullOrWhiteSpace(npArtist) && string.IsNullOrWhiteSpace(npAlbum)))
            {
                return null;
            }

            var kind = InferExternalHeosKind(status.NowPlaying);
            var originExternal = _telemetry.ResolveOrigin("avr");
            var displayTitle = !string.IsNullOrWhiteSpace(npSong)
                ? npSong
                : (!string.IsNullOrWhiteSpace(npStation) ? npStation : (npAlbum ?? npArtist ?? "HEOS Stream"));
            var key = $"heos:{kind}:{displayTitle.ToLowerInvariant()}:{(npArtist ?? string.Empty).ToLowerInvariant()}";

            return new MediaCandidate(
                Key: key,
                Kind: kind,
                Title: displayTitle,
                Artist: npArtist,
                Album: npAlbum,
                Source: DenonCatalog.GetHeosSourceName(status.NowPlaying.SourceId) ?? kind,
                Origin: originExternal,
                StartedUtc: nowUtc,
                InitialVolume: vol,
                ExtraData: new Dictionary<string, object?>
                {
                    ["mediaType"] = npType,
                    ["station"] = npStation,
                    ["sourceId"] = status.NowPlaying.SourceId,
                    ["input"] = "NET",
                    ["soundMode"] = status.SoundMode,
                });
        }

        // 4. Non-NET active AVR input (TV Audio / eARC, Game, Blu-ray, Bluetooth, CD, Phono, FM Tuner)
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var friendly = DenonCatalog.GetFriendlyInputName(input);
        var inputOrigin = _telemetry.ResolveOrigin("avr");
        return new MediaCandidate(
            Key: $"input:{input.ToUpperInvariant()}",
            Kind: "avr_input",
            Title: friendly,
            Artist: null,
            Album: null,
            Source: input.ToUpperInvariant(),
            Origin: inputOrigin,
            StartedUtc: nowUtc,
            InitialVolume: vol,
            ExtraData: new Dictionary<string, object?>
            {
                ["input"] = input.ToUpperInvariant(),
                ["soundMode"] = status.SoundMode,
            });
    }

    private void ProcessPluginSnapshots()
    {
        var plugins = _pluginHostService.GetPlugins();
        var nowUtc = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            // Track LG Smart TV watching sessions on the `media` stream
            var tv = plugins.FirstOrDefault(static p => p.Id.Equals("smart-tv", StringComparison.OrdinalIgnoreCase));
            var tvCandidate = BuildTvMediaCandidate(tv, nowUtc);
            var tvEndReason = tv is null || tv.Health != PluginHealthState.Connected ? "standby" : "switched";
            UpdateMediaSlotLocked(_tvSession, tvCandidate, nowUtc, tvEndReason);

            // Track Philips Hue lighting status transitions on the `lighting` stream
            var hue = plugins.FirstOrDefault(static p => p.Id.Equals("philips-hue", StringComparison.OrdinalIgnoreCase));
            if (hue is { IsEnabled: true, IsStarted: true } &&
                !string.IsNullOrWhiteSpace(hue.StatusText) &&
                !hue.StatusText.Contains("Demo", StringComparison.OrdinalIgnoreCase) &&
                !hue.StatusText.Equals("Initializing...", StringComparison.OrdinalIgnoreCase))
            {
                if (_lastHueStatus is not null &&
                    !string.Equals(_lastHueStatus, hue.StatusText, StringComparison.Ordinal))
                {
                    var origin = _telemetry.ResolveOrigin("lighting");
                    _telemetry.Record(
                        EventTelemetryLog.StreamLighting,
                        "philips-hue",
                        "lighting.state_changed",
                        origin,
                        new Dictionary<string, object?>
                        {
                            ["from"] = _lastHueStatus,
                            ["to"] = hue.StatusText,
                            ["health"] = hue.Health.ToString(),
                        });
                }

                _lastHueStatus = hue.StatusText;
            }
        }
    }

    private MediaCandidate? BuildTvMediaCandidate(PluginRecordSnapshot? tv, DateTimeOffset nowUtc)
    {
        if (tv is null || !tv.IsEnabled || !tv.IsStarted || tv.Health != PluginHealthState.Connected)
        {
            return null;
        }

        var status = tv.StatusText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(status) ||
            status.StartsWith("Demo", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Screen Off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Format from SmartTvPlugin: "LIVE • 55UR73006LA • Live TV (Ch 1 • DR1)" or "LIVE • 55UR73006LA • Netflix"
        var parts = status.Split(" • ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("LIVE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = parts[1];
        var showing = string.Join(" • ", parts.Skip(2));
        if (string.IsNullOrWhiteSpace(showing) ||
            showing.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
            showing.Equals("Standby", StringComparison.OrdinalIgnoreCase) ||
            showing.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = showing.StartsWith("Live TV", StringComparison.OrdinalIgnoreCase) ? "tv_channel" : "tv_app";
        var origin = _telemetry.ResolveOrigin("tv");

        return new MediaCandidate(
            Key: $"tv:{showing.ToLowerInvariant()}",
            Kind: kind,
            Title: showing,
            Artist: null,
            Album: null,
            Source: "lg-webos",
            Origin: origin,
            StartedUtc: nowUtc,
            InitialVolume: _lastAvr is { IsPoweredOn: true } ? Math.Round(GetEffectiveVolume(_lastAvr), 1) : null,
            ExtraData: new Dictionary<string, object?>
            {
                ["tvModel"] = model,
                ["showing"] = showing,
            });
    }

    private void UpdateMediaSlotLocked(
        MediaSessionSlot slot,
        MediaCandidate? next,
        DateTimeOffset nowUtc,
        string stopReason)
    {
        // Same session already active
        if (slot.Active is not null && next is not null &&
            string.Equals(slot.Active.Candidate.Key, next.Key, StringComparison.OrdinalIgnoreCase))
        {
            if (next.InitialVolume.HasValue)
            {
                slot.Active.AddVolumeSample(next.InitialVolume.Value);
            }

            return;
        }

        // Same candidate still waiting for 15s dwell confirmation
        if (slot.Active is null && slot.Pending is not null && next is not null &&
            string.Equals(slot.Pending.Key, next.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Session changed or ended: close any previously committed active session
        if (slot.Active is not null)
        {
            var reason = next is not null ? "switched" : stopReason;
            EmitSessionEndedLocked(slot, slot.Active, nowUtc, reason);
            slot.Active = null;
        }

        // Discard any unconfirmed (<15s) pending candidate without logging noise
        slot.Pending = next;
    }

    private void EvaluateMediaDwellLocked(MediaSessionSlot slot, DateTimeOffset nowUtc, int dwellSeconds)
    {
        if (slot.Active is not null || slot.Pending is null)
        {
            return;
        }

        if ((nowUtc - slot.Pending.StartedUtc).TotalSeconds < dwellSeconds)
        {
            return;
        }

        var candidate = slot.Pending;
        slot.Pending = null;
        var active = new ActiveMediaSession(candidate);
        if (candidate.InitialVolume.HasValue)
        {
            active.AddVolumeSample(candidate.InitialVolume.Value);
        }

        slot.Active = active;

        var data = BuildSessionPayload(candidate);
        data["dwellConfirmedSec"] = dwellSeconds;
        if (candidate.InitialVolume.HasValue)
        {
            data["volume"] = candidate.InitialVolume.Value;
        }

        _telemetry.Record(
            EventTelemetryLog.StreamMedia,
            slot.Device,
            "session.started",
            candidate.Origin,
            data);
    }

    private void EmitSessionEndedLocked(
        MediaSessionSlot slot,
        ActiveMediaSession active,
        DateTimeOffset nowUtc,
        string reason)
    {
        var durationSec = Math.Max(1, (int)Math.Round((nowUtc - active.Candidate.StartedUtc).TotalSeconds));
        var data = BuildSessionPayload(active.Candidate);
        data["durationSec"] = durationSec;
        data["reason"] = reason;
        if (active.AverageVolume.HasValue)
        {
            data["avgVolume"] = Math.Round(active.AverageVolume.Value, 1);
        }

        _telemetry.Record(
            EventTelemetryLog.StreamMedia,
            slot.Device,
            "session.ended",
            active.Candidate.Origin,
            data);
    }

    private void SampleActiveSessionVolume()
    {
        lock (_sync)
        {
            if (_lastAvr is not { IsPoweredOn: true, IsMainZoneOn: true })
            {
                return;
            }

            var vol = Math.Round(GetEffectiveVolume(_lastAvr), 1);
            _avrSession.Active?.AddVolumeSample(vol);
            _tvSession.Active?.AddVolumeSample(vol);
        }
    }

    private void FlushShutdownState()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            FlushPendingVolumeLocked();

            if (_avrSession.Active is not null)
            {
                EmitSessionEndedLocked(_avrSession, _avrSession.Active, nowUtc, "app_shutdown");
                _avrSession.Active = null;
            }

            if (_tvSession.Active is not null)
            {
                EmitSessionEndedLocked(_tvSession, _tvSession.Active, nowUtc, "app_shutdown");
                _tvSession.Active = null;
            }

            _avrSession.Pending = null;
            _tvSession.Pending = null;
        }
    }

    private bool IsTvActiveLocked() =>
        _tvSession.Active is not null || _tvSession.Pending is not null;

    private async Task RefreshHueSilentlyAsync(CancellationToken ct)
    {
        try
        {
            var hue = _pluginHostService
                .GetPlugins()
                .FirstOrDefault(static p => p.Id.Equals("philips-hue", StringComparison.OrdinalIgnoreCase));

            if (hue is null || !hue.IsEnabled || !hue.IsStarted || hue.Health != PluginHealthState.Connected)
            {
                return;
            }

            if (hue.StatusText.Contains("Demo", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Intentionally do NOT call MarkOrigin here — any state change discovered by this background
            // poll came from an external wall switch, Hue Dimmer, or Hue phone app!
            await _pluginHostService.ExecuteActionAsync("philips-hue", "hue.refresh", null, ct);
        }
        catch
        {
            // Ignore background probe errors.
        }
    }

    private static Dictionary<string, object?> BuildSessionPayload(MediaCandidate candidate)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = candidate.Kind,
            ["title"] = candidate.Title,
            ["source"] = candidate.Source,
        };

        if (!string.IsNullOrWhiteSpace(candidate.Artist))
        {
            data["artist"] = candidate.Artist;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Album))
        {
            data["album"] = candidate.Album;
        }

        if (candidate.ExtraData is not null)
        {
            foreach (var (k, v) in candidate.ExtraData)
            {
                if (v is not null && !string.IsNullOrWhiteSpace(v.ToString()))
                {
                    data[k] = v;
                }
            }
        }

        return data;
    }

    private static string InferExternalHeosKind(NowPlayingMedia np)
    {
        // Standard HEOS source IDs: 4 = Spotify, 3 = TuneIn, 10 = Tidal, 13 = Amazon Music, 1024 = Local/DLNA/USB, 1025 = Playlist, 1028 = URL
        if (np.SourceId is int sid)
        {
            return sid switch
            {
                3 => "tunein_radio",
                4 => "spotify",
                10 => "tidal",
                13 => "amazon_music",
                1024 => "local_dlna",
                _ => "heos_stream",
            };
        }

        var combined = $"{np.Type} {np.Album} {np.Song}".ToLowerInvariant();
        if (combined.Contains("spotify")) return "spotify";
        if (combined.Contains("airplay")) return "airplay";
        if (combined.Contains("tidal")) return "tidal";
        if (combined.Contains("tunein") || np.Type.Equals("station", StringComparison.OrdinalIgnoreCase)) return "radio";
        return "heos_stream";
    }

    private static double GetEffectiveVolume(ReceiverStatus status) =>
        status.VolumeExact > 0.01 ? status.VolumeExact : status.Volume;

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private sealed class MediaSessionSlot(string device)
    {
        public string Device { get; } = device;

        public MediaCandidate? Pending { get; set; }

        public ActiveMediaSession? Active { get; set; }
    }

    private sealed record MediaCandidate(
        string Key,
        string Kind,
        string Title,
        string? Artist,
        string? Album,
        string Source,
        string Origin,
        DateTimeOffset StartedUtc,
        double? InitialVolume,
        IReadOnlyDictionary<string, object?>? ExtraData);

    private sealed class ActiveMediaSession(MediaCandidate candidate)
    {
        private double _volumeSum;
        private int _volumeSamples;

        public MediaCandidate Candidate { get; } = candidate;

        public double? AverageVolume => _volumeSamples > 0 ? _volumeSum / _volumeSamples : null;

        public void AddVolumeSample(double volume)
        {
            if (volume <= 0.01)
            {
                return;
            }

            _volumeSum += volume;
            _volumeSamples++;
        }
    }
}

using Denon.App.Core.Heos;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class ReceiverController
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly HeosClient _heosClient;
    private readonly DenonAvrClient _avrClient;
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly EventTelemetryLog _telemetry;
    private readonly AppLogStore _logStore;
    private readonly LastStationFile? _lastStationFile;
    private string? _lastPlayedStationAlias;
    private bool _avrProtocolAvailable = true;
    private bool _hasConfirmedAvrOnce;
    private int _consecutiveAvrFailures;

    /// <summary>The last merged status with a real AVR answer. The grace window keeps its power, input and mode.</summary>
    private ReceiverStatus? _lastConfirmedStatus;

    /// <summary>The receiver <see cref="_lastConfirmedStatus"/> came from: another receiver gets no grace window.</summary>
    private string? _lastConfirmedHost;

    public ReceiverController(
        HeosClient heosClient,
        DenonAvrClient avrClient,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        EventTelemetryLog telemetry,
        AppLogStore logStore,
        LastStationFile? lastStationFile = null)
    {
        _heosClient = heosClient;
        _avrClient = avrClient;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _telemetry = telemetry;
        _logStore = logStore;
        _lastStationFile = lastStationFile;
        // A stream that still plays from before a restart is the station this app started last.
        _lastPlayedStationAlias = lastStationFile?.Load();
    }

    /// <summary>The saved station this app started last; null after another source took over. Kept on disk.</summary>
    private string? LastPlayedStation
    {
        set
        {
            if (string.Equals(_lastPlayedStationAlias, value, StringComparison.Ordinal))
            {
                return;
            }

            _lastPlayedStationAlias = value;
            _lastStationFile?.Save(value);
        }
    }

    public event Action? NonRelaySourceActivated;

    /// <summary>
    /// HEOS was told to play an internet radio stream (a station button, a Smart Scene's stream or a custom URL):
    /// the URL and a name for it. The synced meters and Hue Music Sync decode a copy of it.
    /// </summary>
    public event Action<string, string>? StreamUrlStarted;

    public event Action<ScenePreset>? SceneActivated;

    /// <summary>
    /// True when the Denon/Marantz AVR Telnet control interface (port 23) responded on the last check;
    /// false for HEOS-only speakers (e.g. Denon Home) or when AVR port 23 is temporarily unavailable.
    /// </summary>
    public bool IsAvrProtocolAvailable => _avrProtocolAvailable;

    public IReadOnlyList<StationPreset> GetStations() => _settingsStore.Current.Receiver.Stations;

    public IReadOnlyList<ScenePreset> GetScenes() => _settingsStore.Current.Receiver.Scenes;

    public Task<ReceiverStatus> RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "Refresh",
            (settings, token) => GetMergedStatusAsync(settings, token),
            markBusy: false,
            logSuccess: false,
            logFailures: false,
            cancellationToken);

    public Task<ReceiverStatus> SetPowerAsync(bool powerOn, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            powerOn ? "Power on receiver" : "Put receiver in standby",
            async (settings, token) =>
            {
                await _avrClient.SetPowerAsync(settings, powerOn, token);
                if (powerOn)
                {
                    await Task.Delay(500, token);
                    await _avrClient.SetMainZonePowerAsync(settings, true, token);
                }
                else
                {
                    LastPlayedStation = null;
                }

                await Task.Delay(300, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public async Task<ReceiverStatus> TogglePowerAsync(CancellationToken cancellationToken = default)
    {
        var isOn = _stateStore.Snapshot.Status is { IsPoweredOn: true, IsMainZoneOn: true };
        return await SetPowerAsync(!isOn, cancellationToken);
    }

    public Task<ReceiverStatus> SetZone2PowerAsync(bool on, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            on ? "Turn Zone 2 on" : "Turn Zone 2 off",
            async (settings, token) =>
            {
                await _avrClient.SetZone2PowerAsync(settings, on, token);
                await Task.Delay(200, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetInputSourceAsync(string sourceCode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Switch input to {DenonCatalog.GetFriendlyInputName(sourceCode)}",
            async (settings, token) =>
            {
                if (!DenonCatalog.InputMatches(sourceCode, "NET"))
                {
                    LastPlayedStation = null;
                    NonRelaySourceActivated?.Invoke();
                    await TryStopHeosPlaybackAsync(settings, token);
                }

                await _avrClient.SetPowerAsync(settings, true, token);
                await _avrClient.SetMainZonePowerAsync(settings, true, token);
                await _avrClient.SetInputSourceAsync(settings, sourceCode, token);
                await Task.Delay(400, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetSoundModeAsync(string modeCode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set sound mode to {modeCode}",
            async (settings, token) =>
            {
                await _avrClient.SetSoundModeAsync(settings, modeCode, token);
                await Task.Delay(250, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetEcoModeAsync(string mode, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set ECO mode to {mode}",
            async (settings, token) =>
            {
                await _avrClient.SetEcoModeAsync(settings, mode, token);
                await Task.Delay(200, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetSleepTimerAsync(int? minutes, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            minutes is > 0 ? $"Set sleep timer to {minutes}m" : "Disable sleep timer",
            async (settings, token) =>
            {
                await _avrClient.SetSleepTimerAsync(settings, minutes, token);
                await Task.Delay(200, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    /// <param name="logSuccess">False for the steps of a fade: only its last step is worth a log line.</param>
    /// <param name="refreshStatus">
    /// False for the steps of a fade: the known status gets the new volume, and no full status read runs
    /// (it takes about 1 s). The last step reads the status. The 5 s poll still sees a turn of the knob.
    /// </param>
    public Task<ReceiverStatus> SetVolumeAsync(
        int level,
        CancellationToken cancellationToken = default,
        bool logSuccess = true,
        bool refreshStatus = true) =>
        ExecuteAsync(
            $"Set volume to {level} ({DenonVolumeMath.FormatDecibels(level)})",
            async (settings, token) =>
            {
                await SetVolumeWithHeosFallbackAsync(settings, level, token);
                if (!refreshStatus &&
                    _stateStore.Snapshot.Status is { } known &&
                    await TryReadFadeStepAsync(settings, known, token) is { } step)
                {
                    return step;
                }

                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: false,
            logSuccess: logSuccess,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetPlayStateAsync(string state, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set play state to {state}",
            async (settings, token) =>
            {
                if (string.Equals(state, "play", StringComparison.OrdinalIgnoreCase))
                {
                    await TryEnsureMainZoneNetAsync(settings, token);
                }

                await _heosClient.SetPlayStateAsync(settings, state, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> PlayNextAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "Next track",
            async (settings, token) =>
            {
                await _heosClient.PlayNextAsync(settings, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> PlayPreviousAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "Previous track",
            async (settings, token) =>
            {
                await _heosClient.PlayPreviousAsync(settings, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            isMuted ? "Mute receiver" : "Unmute receiver",
            async (settings, token) =>
            {
                await SetMuteWithHeosFallbackAsync(settings, isMuted, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public async Task<ReceiverStatus> ToggleMuteAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _stateStore.Snapshot;
        var targetMute = !(snapshot.Status?.IsMuted ?? false);
        return await SetMuteAsync(targetMute, cancellationToken);
    }

    public async Task<ReceiverStatus> SetLowVolumeAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Current;
        return await SetVolumeAsync(settings.Receiver.DefaultLowVolume, cancellationToken);
    }

    public Task<ReceiverStatus> PlayPresetAsync(
        StationPreset preset,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        var volume = useLowVolume ? (int?)_settingsStore.Current.Receiver.DefaultLowVolume : null;
        return ExecuteAsync(
            $"Play preset {preset.DisplayName}",
            async (settings, token) =>
            {
                await TryStopHeosPlaybackAsync(settings, token);
                await TryEnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await SetVolumeWithHeosFallbackAsync(settings, level, token);
                }

                if (unmute)
                {
                    await SetMuteWithHeosFallbackAsync(settings, false, token);
                }

                await _heosClient.PlayUrlAsync(settings, preset.Url, volume: null, unmute: unmute, token);
                LastPlayedStation = preset.Alias;
                NonRelaySourceActivated?.Invoke();
                StreamUrlStarted?.Invoke(preset.Url, preset.DisplayName);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);
    }

    public Task<ReceiverStatus> PlayCustomUrlAsync(
        string url,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default,
        string? title = null)
    {
        var volume = useLowVolume ? (int?)_settingsStore.Current.Receiver.DefaultLowVolume : null;
        return ExecuteAsync(
            "Play custom stream",
            async (settings, token) =>
            {
                await TryStopHeosPlaybackAsync(settings, token);
                await TryEnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await SetVolumeWithHeosFallbackAsync(settings, level, token);
                }

                if (unmute)
                {
                    await SetMuteWithHeosFallbackAsync(settings, false, token);
                }

                await _heosClient.PlayUrlAsync(settings, url, volume: null, unmute: unmute, token);
                LastPlayedStation = null;
                NonRelaySourceActivated?.Invoke();
                StreamUrlStarted?.Invoke(url, string.IsNullOrWhiteSpace(title) ? "Internet radio" : title);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<HeosFavoriteItem>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Current.Receiver;
            var favorites = await _heosClient.GetFavoritesAsync(settings, cancellationToken);
            _logStore.Info($"Loaded {favorites.Count} HEOS favorites from receiver.");
            return favorites;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task<ReceiverStatus> PlayFavoriteAsync(
        HeosFavoriteItem favorite,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        var volume = useLowVolume ? (int?)_settingsStore.Current.Receiver.DefaultLowVolume : null;
        return ExecuteAsync(
            $"Play HEOS favorite {favorite.Name}",
            async (settings, token) =>
            {
                await TryStopHeosPlaybackAsync(settings, token);
                await TryEnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await SetVolumeWithHeosFallbackAsync(settings, level, token);
                }

                if (unmute)
                {
                    await SetMuteWithHeosFallbackAsync(settings, false, token);
                }

                await _heosClient.PlayFavoriteAsync(settings, favorite.PresetIndex, token);
                LastPlayedStation = null;
                NonRelaySourceActivated?.Invoke();
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);
    }

    public async Task<string> SendRawAvrCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Current.Receiver;
            var result = await _avrClient.SendRawCommandAsync(settings, command, cancellationToken);
            _logStore.Info($"[AVR RAW] {command.Trim()} -> {result.Replace(Environment.NewLine, " | ")}");
            return result;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<string> SendRawHeosCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Current.Receiver;
            var result = await _heosClient.SendRawCommandAsync(settings, command, cancellationToken);
            _logStore.Info($"[HEOS RAW] {command.Trim()} -> {result.Replace(Environment.NewLine, " | ")}");
            return result;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void ResetConnections()
    {
        _heosClient.Disconnect();
        _avrClient.Disconnect();
        _logStore.Info("Reset AVR & HEOS network connections.");
    }

    public async Task<IReadOnlyList<ReceiverPlayerInfo>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            return await _heosClient.GetPlayersAsync(_settingsStore.Current.Receiver, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<AvrDeviceStatus> GetAvrStatusOnlyAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            return await _avrClient.GetStatusAsync(_settingsStore.Current.Receiver, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task<ReceiverStatus> PlayRelayStreamAsync(
        string relayUrl,
        string trackTitle,
        bool useLowVolume,
        bool unmute,
        CancellationToken cancellationToken = default)
    {
        var volume = useLowVolume ? (int?)_settingsStore.Current.Receiver.DefaultLowVolume : null;
        return ExecuteAsync(
            $"Cast LAN audio '{trackTitle}'",
            async (settings, token) =>
            {
                await TryStopHeosPlaybackAsync(settings, token);
                await TryEnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await SetVolumeWithHeosFallbackAsync(settings, level, token);
                }

                if (unmute)
                {
                    await SetMuteWithHeosFallbackAsync(settings, false, token);
                }

                await _heosClient.PlayUrlAsync(settings, relayUrl, volume: null, unmute: unmute, token);
                LastPlayedStation = null;
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);
    }

    public async Task<AcousticStatus> GetAcousticStatusAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Current.Receiver;
            // No log line: the Brain reads this every 90 s while the receiver is on.
            return await _avrClient.GetAcousticStatusAsync(settings, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<AcousticStatus> SetMultEqAsync(string mode, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync($"Set Audyssey MultEQ to {mode}", (s, t) => _avrClient.SetMultEqAsync(s, mode, t), cancellationToken);

    public async Task<AcousticStatus> SetDynamicEqAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync(enabled ? "Enable Audyssey Dynamic EQ" : "Disable Audyssey Dynamic EQ", (s, t) => _avrClient.SetDynamicEqAsync(s, enabled, t), cancellationToken);

    public async Task<AcousticStatus> SetDynamicVolumeAsync(string mode, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync($"Set Audyssey Dynamic Volume to {mode}", (s, t) => _avrClient.SetDynamicVolumeAsync(s, mode, t), cancellationToken);

    public async Task<AcousticStatus> SetRestorerAsync(string mode, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync($"Set Audio Restorer to {mode}", (s, t) => _avrClient.SetRestorerAsync(s, mode, t), cancellationToken);

    public async Task<AcousticStatus> SetDimmerAsync(string mode, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync($"Set Front Panel Dimmer to {mode}", (s, t) => _avrClient.SetDimmerAsync(s, mode, t), cancellationToken);

    public async Task<AcousticStatus> SetSubwooferLevelAsync(bool enabled, int? trimDb, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync(
            enabled ? $"Set Subwoofer Trim to {DenonAcousticCatalog.FormatDbOffset(trimDb ?? 0)}" : "Disable Subwoofer Trim",
            (s, t) => _avrClient.SetSubwooferLevelAsync(s, enabled, trimDb, t),
            cancellationToken);

    public async Task<AcousticStatus> SetDialogLevelAsync(bool enabled, int? trimDb, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync(
            enabled ? $"Set Dialog Enhancer to {DenonAcousticCatalog.FormatDbOffset(trimDb ?? 0)}" : "Disable Dialog Enhancer",
            (s, t) => _avrClient.SetDialogLevelAsync(s, enabled, trimDb, t),
            cancellationToken);

    public async Task<AcousticStatus> SetToneControlAsync(bool enabled, int? bassDb, int? trebleDb, CancellationToken cancellationToken = default) =>
        await ExecuteAcousticAsync(
            enabled ? $"Set Tone Control (Bass {DenonAcousticCatalog.FormatDbOffset(bassDb ?? 0)}, Treble {DenonAcousticCatalog.FormatDbOffset(trebleDb ?? 0)})" : "Disable Tone Control",
            (s, t) => _avrClient.SetToneControlAsync(s, enabled, bassDb, trebleDb, t),
            cancellationToken);

    public Task<ReceiverStatus> ApplySceneAsync(ScenePreset scene, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Activate scene '{scene.Name}'",
            async (settings, token) =>
            {
                if (await AvrAnswersAsync(settings, token))
                {
                    await ApplySceneAvrStepsAsync(settings, scene, token);
                }
                else
                {
                    await ApplySceneHeosOnlyStepsAsync(settings, scene, token);
                }

                await Task.Delay(300, token);
                var updatedStatus = await GetMergedStatusAsync(settings, token);
                _telemetry.MarkOrigin("avr", "smart_scene", "scene.activated", new Dictionary<string, object?>
                {
                    ["sceneId"] = scene.Id,
                    ["sceneName"] = scene.Name,
                    ["input"] = scene.InputSource,
                    ["volume"] = scene.TargetVolume,
                }, overwriteRecent: false);
                _telemetry.MarkOrigin("tv", "smart_scene", overwriteRecent: false);
                _telemetry.MarkOrigin("lighting", "smart_scene", overwriteRecent: false);
                SceneActivated?.Invoke(scene);
                return updatedStatus;
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    /// <summary>The scene on a receiver that answers on port 23: power, input, modes, then volume and mute.</summary>
    private async Task ApplySceneAvrStepsAsync(ReceiverSettings settings, ScenePreset scene, CancellationToken token)
    {
        if (scene.PowerOn)
        {
            await _avrClient.SetPowerAsync(settings, true, token);
            await Task.Delay(350, token);
            await _avrClient.SetMainZonePowerAsync(settings, true, token);
            await Task.Delay(250, token);
        }

        // Read current level/mute BEFORE switching to a live input or releasing mute:
        // for quiet scenes (lowering volume or starting muted), establish the safe target volume FIRST
        // so a receiver muted at 75 never blasts at 75 before dropping to 25!
        var preSceneAvr = await _avrClient.GetStatusAsync(settings, token);
        var volumeAppliedEarly = false;
        if (scene.TargetVolume is int earlyTargetVol &&
            (preSceneAvr.IsMuted || earlyTargetVol < preSceneAvr.Volume))
        {
            if (scene.SmoothVolumeRamp && !preSceneAvr.IsMuted)
            {
                await _avrClient.RampVolumeAsync(settings, preSceneAvr.Volume, earlyTargetVol, token);
            }
            else
            {
                await _avrClient.SetVolumeAsync(settings, earlyTargetVol, token);
            }

            volumeAppliedEarly = true;
        }

        if (!string.IsNullOrWhiteSpace(scene.InputSource))
        {
            if (DenonCatalog.InputMatches(scene.InputSource, "NET"))
            {
                await _avrClient.EnsureMainZoneNetAsync(settings, token);
            }
            else
            {
                LastPlayedStation = null;
                NonRelaySourceActivated?.Invoke();
                await TryStopHeosPlaybackAsync(settings, token);
                await _avrClient.SetInputSourceAsync(settings, scene.InputSource, token);
                await Task.Delay(450, token);
            }
        }

        if (!string.IsNullOrWhiteSpace(scene.SoundMode))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetSoundModeAsync(settings, scene.SoundMode, token));
            await Task.Delay(200, token);
        }

        if (!string.IsNullOrWhiteSpace(scene.EcoMode))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetEcoModeAsync(settings, scene.EcoMode, token));
        }

        if (!string.IsNullOrWhiteSpace(scene.MultEq))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetMultEqAsync(settings, scene.MultEq, token));
        }

        if (scene.DynamicEq is bool dynEq)
        {
            await TryRunSceneStepAsync(() => _avrClient.SetDynamicEqAsync(settings, dynEq, token));
        }

        if (!string.IsNullOrWhiteSpace(scene.DynamicVolume))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetDynamicVolumeAsync(settings, scene.DynamicVolume, token));
        }

        if (!string.IsNullOrWhiteSpace(scene.Restorer))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetRestorerAsync(settings, scene.Restorer, token));
        }

        if (!string.IsNullOrWhiteSpace(scene.Dimmer))
        {
            await TryRunSceneStepAsync(() => _avrClient.SetDimmerAsync(settings, scene.Dimmer, token));
        }

        if (scene.SubwooferTrimDb is int swDb)
        {
            await TryRunSceneStepAsync(() => _avrClient.SetSubwooferLevelAsync(settings, true, swDb, token));
        }

        if (scene.DialogTrimDb is int dilDb)
        {
            await TryRunSceneStepAsync(() => _avrClient.SetDialogLevelAsync(settings, true, dilDb, token));
        }

        if (scene.SleepTimerMinutes is int sleepMins)
        {
            await TryRunSceneStepAsync(() => _avrClient.SetSleepTimerAsync(settings, sleepMins <= 0 ? null : sleepMins, token));
        }

        // For upward volume moves (not muted), establish volume before releasing mute too.
        if (!volumeAppliedEarly && scene.TargetVolume is int targetVol)
        {
            if (scene.SmoothVolumeRamp)
            {
                var currentAvr = await _avrClient.GetStatusAsync(settings, token);
                await _avrClient.RampVolumeAsync(settings, currentAvr.Volume, targetVol, token);
            }
            else
            {
                await _avrClient.SetVolumeAsync(settings, targetVol, token);
            }
        }

        // Release mute ONLY AFTER target volume has succeeded!
        if (scene.Unmute)
        {
            await _avrClient.SetMuteAsync(settings, false, token);
        }

        if (!string.IsNullOrWhiteSpace(scene.StreamUrl))
        {
            var streamUrl = scene.StreamUrl.Trim();
            await _avrClient.EnsureMainZoneNetAsync(settings, token);
            await _heosClient.PlayUrlAsync(settings, streamUrl, volume: null, unmute: scene.Unmute, token);
            // A scene that starts a saved station's stream plays that station: name it in Now playing.
            var station = settings.Stations
                .FirstOrDefault(s => string.Equals(s.Url.Trim(), streamUrl, StringComparison.OrdinalIgnoreCase));
            LastPlayedStation = station?.Alias;
            NonRelaySourceActivated?.Invoke();
            StreamUrlStarted?.Invoke(streamUrl, station?.DisplayName ?? scene.Name);
        }
    }

    /// <summary>
    /// The scene on a HEOS-only speaker (no port 23, for example Denon Home): the AVR steps (power, input,
    /// sound mode and the acoustic settings) do not exist there. Volume, mute and the stream go through HEOS,
    /// in the same order as on a receiver: the target volume is set before mute is released.
    /// </summary>
    private async Task ApplySceneHeosOnlyStepsAsync(ReceiverSettings settings, ScenePreset scene, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(scene.InputSource) && !DenonCatalog.InputMatches(scene.InputSource, "NET"))
        {
            _logStore.Warning($"Scene '{scene.Name}': input {scene.InputSource} skipped (no AVR control on port 23).");
        }

        if (scene.TargetVolume is int target)
        {
            await _heosClient.SetVolumeAsync(settings, Math.Clamp(target, 0, 98), token);
        }

        if (scene.Unmute)
        {
            await _heosClient.SetMuteAsync(settings, false, token);
        }

        if (!string.IsNullOrWhiteSpace(scene.StreamUrl))
        {
            var streamUrl = scene.StreamUrl.Trim();
            await _heosClient.PlayUrlAsync(settings, streamUrl, volume: null, unmute: scene.Unmute, token);
            var station = settings.Stations
                .FirstOrDefault(s => string.Equals(s.Url.Trim(), streamUrl, StringComparison.OrdinalIgnoreCase));
            LastPlayedStation = station?.Alias;
            NonRelaySourceActivated?.Invoke();
            StreamUrlStarted?.Invoke(streamUrl, station?.DisplayName ?? scene.Name);
        }
    }

    private async Task<AcousticStatus> ExecuteAcousticAsync(
        string actionDescription,
        Func<ReceiverSettings, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsStore.Current.Receiver;
            await operation(settings, cancellationToken);
            await Task.Delay(180, cancellationToken);
            var status = await _avrClient.GetAcousticStatusAsync(settings, cancellationToken);
            _logStore.Info($"{actionDescription} completed.");
            return status;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logStore.Error($"{actionDescription} failed.", exception);
            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task TryRunSceneStepAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            _logStore.Warning($"Optional scene step skipped: {ex.Message}");
        }
    }

    public Task<ReceiverStatus> FixSilentAudioAsync(int minimumComfortableVolume = 35, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "Fix silent audio",
            async (settings, token) =>
            {
                if (await AvrAnswersAsync(settings, token))
                {
                    await _avrClient.EnsureMainZoneNetAsync(settings, token);
                    await _avrClient.SetMuteAsync(settings, false, token);
                    await _heosClient.SetMuteAsync(settings, false, token);

                    var currentAvr = await _avrClient.GetStatusAsync(settings, token);
                    if (currentAvr.Volume < minimumComfortableVolume)
                    {
                        await _avrClient.SetVolumeAsync(settings, minimumComfortableVolume, token);
                    }
                }
                else
                {
                    // A HEOS-only speaker has no power, zone or input to fix: raise a too-low volume, then unmute.
                    var current = await _heosClient.GetStatusAsync(settings, token);
                    if (current.Volume < minimumComfortableVolume)
                    {
                        await _heosClient.SetVolumeAsync(settings, minimumComfortableVolume, token);
                    }

                    await _heosClient.SetMuteAsync(settings, false, token);
                }

                await Task.Delay(300, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    private async Task<ReceiverStatus> ExecuteAsync(
        string actionDescription,
        Func<ReceiverSettings, CancellationToken, Task<ReceiverStatus>> operation,
        bool markBusy,
        bool logSuccess,
        bool logFailures,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (markBusy)
            {
                _telemetry.MarkOrigin("avr", "desktop_ui", overwriteRecent: false);
                _stateStore.SetBusy(true);
            }

            var receiverSettings = _settingsStore.Current.Receiver;
            var status = await operation(receiverSettings, cancellationToken);
            _stateStore.UpdateStatus(status);
            if (logSuccess)
            {
                _logStore.Info($"{actionDescription} completed.");
            }

            return status;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _stateStore.UpdateError(exception.Message);
            if (logFailures)
            {
                _logStore.Error($"{actionDescription} failed.", exception);
            }

            throw;
        }
        finally
        {
            if (markBusy)
            {
                _stateStore.SetBusy(false);
            }

            _commandGate.Release();
        }
    }

    private async Task<ReceiverStatus> GetMergedStatusAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        var heosStatus = await _heosClient.GetStatusAsync(settings, cancellationToken);
        AvrDeviceStatus? avrStatus = null;
        try
        {
            avrStatus = await _avrClient.GetStatusAsync(settings, cancellationToken);
            _avrProtocolAvailable = true;
            _hasConfirmedAvrOnce = true;
            _consecutiveAvrFailures = 0;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _consecutiveAvrFailures++;
            // Distinguish internal AVR socket timeout (3s) from caller cancellation so silently
            // dropped port 23 cleanly degrades to HEOS-only mode while caller cancel exits immediately.
            if (_avrProtocolAvailable)
            {
                _logStore.Warning($"AVR port 23 control unavailable ({ex.Message}); operating in HEOS-only mode.");
            }

            _avrProtocolAvailable = false;
        }

        if (avrStatus is null)
        {
            // If this is a known AVR experiencing a brief port 23 collision (e.g., phone AVR app briefly connected),
            // preserve the last confirmed AVR power/input/soundMode for up to 2 polls instead of falsely overwriting
            // TV/HDMI with synthetic "NET / HEOS"!
            if (_hasConfirmedAvrOnce &&
                _consecutiveAvrFailures <= 2 &&
                _lastConfirmedStatus is { } prevConfirmed &&
                string.Equals(_lastConfirmedHost, settings.Host, StringComparison.OrdinalIgnoreCase))
            {
                return heosStatus with
                {
                    Volume = heosStatus.Volume,
                    VolumeExact = heosStatus.Volume,
                    MaxVolume = prevConfirmed.MaxVolume,
                    IsMuted = heosStatus.IsMuted,
                    IsPoweredOn = prevConfirmed.IsPoweredOn,
                    IsMainZoneOn = prevConfirmed.IsMainZoneOn,
                    InputSource = prevConfirmed.InputSource,
                    SoundMode = prevConfirmed.SoundMode,
                    EcoMode = prevConfirmed.EcoMode,
                    SleepTimerMinutes = prevConfirmed.SleepTimerMinutes,
                    IsZone2On = prevConfirmed.IsZone2On,
                    ActiveStationAlias = prevConfirmed.ActiveStationAlias,
                    IsAvrConfirmed = false,
                };
            }

            var heosSyntheticAvr = new AvrDeviceStatus(
                IsPoweredOn: true,
                IsMainZoneOn: true,
                Volume: heosStatus.Volume,
                IsMuted: heosStatus.IsMuted,
                InputSource: "NET",
                SoundMode: "HEOS",
                VolumeExact: heosStatus.Volume);
            var heosAlias = DetectActiveStationAlias(settings, heosStatus, heosSyntheticAvr);

            return heosStatus with
            {
                IsPoweredOn = true,
                IsMainZoneOn = true,
                InputSource = "NET",
                SoundMode = "HEOS",
                VolumeExact = heosStatus.Volume,
                ActiveStationAlias = heosAlias,
                IsAvrConfirmed = false,
            };
        }

        var activeAlias = DetectActiveStationAlias(settings, heosStatus, avrStatus);

        var confirmed = heosStatus with
        {
            Volume = avrStatus.Volume,
            VolumeExact = avrStatus.VolumeExact,
            MaxVolume = avrStatus.MaxVolume,
            IsMuted = avrStatus.IsMuted,
            IsPoweredOn = avrStatus.IsPoweredOn,
            IsMainZoneOn = avrStatus.IsMainZoneOn,
            InputSource = avrStatus.InputSource,
            SoundMode = avrStatus.SoundMode,
            EcoMode = avrStatus.EcoMode,
            SleepTimerMinutes = avrStatus.SleepTimerMinutes,
            IsZone2On = avrStatus.IsZone2On,
            ActiveStationAlias = activeAlias,
            IsAvrConfirmed = true,
        };
        _lastConfirmedStatus = confirmed;
        _lastConfirmedHost = settings.Host;
        return confirmed;
    }

    private async Task TryEnsureMainZoneNetAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await _avrClient.EnsureMainZoneNetAsync(settings, cancellationToken);
            _avrProtocolAvailable = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _avrProtocolAvailable = false;
        }
    }

    /// <summary>
    /// True when the receiver answers on port 23. A failed poll can be short (another app held the port), so when the
    /// last check failed, ask once more (SI?) before a scene or a fix skips the AVR steps.
    /// </summary>
    private async Task<bool> AvrAnswersAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        if (_avrProtocolAvailable)
        {
            return true;
        }

        if (await _avrClient.TryGetInputSourceAsync(settings, cancellationToken) is null)
        {
            return false;
        }

        _avrProtocolAvailable = true;
        return true;
    }

    /// <summary>
    /// For a fade step: reads the input and the real volume only (two queries, not a full status read), so Volume
    /// Memory still sees a turn of the knob or a new source and stops the fade. Null when the input changed or a
    /// query got no answer: then the caller does the full read. A HEOS-only speaker has no input; HEOS gives the
    /// volume and mute.
    /// </summary>
    private async Task<ReceiverStatus?> TryReadFadeStepAsync(ReceiverSettings settings, ReceiverStatus known, CancellationToken cancellationToken)
    {
        if (!_avrProtocolAvailable)
        {
            var heos = await _heosClient.GetStatusAsync(settings, cancellationToken);
            return known with { Volume = heos.Volume, VolumeExact = heos.Volume, IsMuted = heos.IsMuted };
        }

        var input = await _avrClient.TryGetInputSourceAsync(settings, cancellationToken);
        if (input is null || !string.Equals(input, known.InputSource, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _avrClient.TryGetVolumeAsync(settings, cancellationToken) is { } volume
            ? known with { Volume = volume.Volume, VolumeExact = volume.VolumeExact, MaxVolume = volume.MaxVolume }
            : null;
    }

    private async Task SetVolumeWithHeosFallbackAsync(ReceiverSettings settings, int level, CancellationToken cancellationToken)
    {
        try
        {
            await _avrClient.SetVolumeAsync(settings, level, cancellationToken);
            _avrProtocolAvailable = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _avrProtocolAvailable = false;
            await _heosClient.SetVolumeAsync(settings, level, cancellationToken);
        }
    }

    private async Task SetMuteWithHeosFallbackAsync(ReceiverSettings settings, bool isMuted, CancellationToken cancellationToken)
    {
        try
        {
            await _avrClient.SetMuteAsync(settings, isMuted, cancellationToken);
            _avrProtocolAvailable = true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _avrProtocolAvailable = false;
            await _heosClient.SetMuteAsync(settings, isMuted, cancellationToken);
        }
    }

    private string? DetectActiveStationAlias(ReceiverSettings settings, ReceiverStatus heosStatus, AvrDeviceStatus avrStatus)
    {
        if (!avrStatus.IsPoweredOn || !avrStatus.IsMainZoneOn || !DenonCatalog.InputMatches(avrStatus.InputSource, "NET"))
        {
            LastPlayedStation = null;
            return null;
        }

        if (!string.Equals(heosStatus.PlayState, "play", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var song = heosStatus.NowPlaying.Song;
        var artist = heosStatus.NowPlaying.Artist;
        var stationName = heosStatus.NowPlaying.Station;

        foreach (var station in settings.Stations)
        {
            if ((!string.IsNullOrWhiteSpace(song) && song.Contains(station.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(artist) && artist.Contains(station.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(stationName) && stationName.Contains(station.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                return station.Alias;
            }
        }

        // If an external streaming service (Spotify, Tidal, TuneIn, Deezer, Amazon, AirPlay, etc.)
        // is playing via phone/HEOS app, clear the stale Denon Desktop preset highlight. HEOS reports
        // this app's own URL streams as sid 1024 "Local Music" with placeholder metadata: those keep it.
        if (heosStatus.NowPlaying.IsNamedServiceSource)
        {
            LastPlayedStation = null;
            return null;
        }

        return _lastPlayedStationAlias;
    }

    private async Task TryStopHeosPlaybackAsync(ReceiverSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await _heosClient.SetPlayStateAsync(settings, "stop", stopCts.Token);
        }
        catch
        {
            // Best effort — never block source switching if HEOS is already stopped or waking up.
        }
    }
}

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
    private readonly AppLogStore _logStore;
    private string? _lastPlayedStationAlias;

    public ReceiverController(
        HeosClient heosClient,
        DenonAvrClient avrClient,
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        AppLogStore logStore)
    {
        _heosClient = heosClient;
        _avrClient = avrClient;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _logStore = logStore;
    }

    public IReadOnlyList<StationPreset> GetStations() => _settingsStore.Current.Receiver.Stations;

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
                    _lastPlayedStationAlias = null;
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
                    _lastPlayedStationAlias = null;
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

    public Task<ReceiverStatus> SetVolumeAsync(int level, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set volume to {level} ({DenonVolumeMath.FormatDecibels(level)})",
            async (settings, token) =>
            {
                await _avrClient.SetVolumeAsync(settings, level, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: false,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    public Task<ReceiverStatus> SetPlayStateAsync(string state, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set play state to {state}",
            async (settings, token) =>
            {
                if (string.Equals(state, "play", StringComparison.OrdinalIgnoreCase))
                {
                    await _avrClient.EnsureMainZoneNetAsync(settings, token);
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
                await _avrClient.SetMuteAsync(settings, isMuted, token);
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
                await _avrClient.EnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await _avrClient.SetVolumeAsync(settings, level, token);
                }

                if (unmute)
                {
                    await _avrClient.SetMuteAsync(settings, false, token);
                }

                await _heosClient.PlayUrlAsync(settings, preset.Url, volume: null, unmute: unmute, token);
                _lastPlayedStationAlias = preset.Alias;
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
        CancellationToken cancellationToken = default)
    {
        var volume = useLowVolume ? (int?)_settingsStore.Current.Receiver.DefaultLowVolume : null;
        return ExecuteAsync(
            "Play custom stream",
            async (settings, token) =>
            {
                await _avrClient.EnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await _avrClient.SetVolumeAsync(settings, level, token);
                }

                if (unmute)
                {
                    await _avrClient.SetMuteAsync(settings, false, token);
                }

                await _heosClient.PlayUrlAsync(settings, url, volume: null, unmute: unmute, token);
                _lastPlayedStationAlias = null;
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
                await _avrClient.EnsureMainZoneNetAsync(settings, token);
                if (volume is int level)
                {
                    await _avrClient.SetVolumeAsync(settings, level, token);
                }

                if (unmute)
                {
                    await _avrClient.SetMuteAsync(settings, false, token);
                }

                await _heosClient.PlayFavoriteAsync(settings, favorite.PresetIndex, token);
                _lastPlayedStationAlias = null;
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

    public Task<ReceiverStatus> FixSilentAudioAsync(int minimumComfortableVolume = 35, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "Fix silent audio",
            async (settings, token) =>
            {
                await _avrClient.EnsureMainZoneNetAsync(settings, token);
                await _avrClient.SetMuteAsync(settings, false, token);
                await _heosClient.SetMuteAsync(settings, false, token);

                var currentAvr = await _avrClient.GetStatusAsync(settings, token);
                if (currentAvr.Volume < minimumComfortableVolume)
                {
                    await _avrClient.SetVolumeAsync(settings, minimumComfortableVolume, token);
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
        var avrStatus = await _avrClient.GetStatusAsync(settings, cancellationToken);

        var activeAlias = DetectActiveStationAlias(settings, heosStatus, avrStatus);

        return heosStatus with
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
        };
    }

    private string? DetectActiveStationAlias(ReceiverSettings settings, ReceiverStatus heosStatus, AvrDeviceStatus avrStatus)
    {
        if (!avrStatus.IsPoweredOn || !avrStatus.IsMainZoneOn || !DenonCatalog.InputMatches(avrStatus.InputSource, "NET"))
        {
            return null;
        }

        if (!string.Equals(heosStatus.PlayState, "play", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var song = heosStatus.NowPlaying.Song;
        var artist = heosStatus.NowPlaying.Artist;

        foreach (var station in settings.Stations)
        {
            if ((!string.IsNullOrWhiteSpace(song) && song.Contains(station.DisplayName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(artist) && artist.Contains(station.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                return station.Alias;
            }
        }

        return _lastPlayedStationAlias;
    }
}

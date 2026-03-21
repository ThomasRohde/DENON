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

    public Task<ReceiverStatus> SetVolumeAsync(int level, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"Set volume to {level}",
            async (settings, token) =>
            {
                await _avrClient.SetVolumeAsync(settings, level, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
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

                await _heosClient.PlayUrlAsync(settings, preset.Url, volume: null, unmute: false, token);
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

                await _heosClient.PlayUrlAsync(settings, url, volume: null, unmute: false, token);
                return await GetMergedStatusAsync(settings, token);
            },
            markBusy: true,
            logSuccess: true,
            logFailures: true,
            cancellationToken);
    }

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

        return heosStatus with
        {
            Volume = avrStatus.Volume,
            IsMuted = avrStatus.IsMuted,
            IsPoweredOn = avrStatus.IsPoweredOn,
            IsMainZoneOn = avrStatus.IsMainZoneOn,
            InputSource = avrStatus.InputSource,
        };
    }
}

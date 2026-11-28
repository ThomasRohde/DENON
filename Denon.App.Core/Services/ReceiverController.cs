using Denon.App.Core.Heos;
using Denon.App.Core.Models;

namespace Denon.App.Core.Services;

public sealed class ReceiverController
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>Held for a whole groups read (see <see cref="RefreshHeosGroupsAsync"/>). Never taken before the command gate.</summary>
    private readonly SemaphoreSlim _groupsReadGate = new(1, 1);
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

    private readonly object _pushRefreshSync = new();
    private CancellationTokenSource? _pushRefreshCts;

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

        _avrClient.LinesReceived += OnAvrLinesReceived;
        _avrClient.LinkClosed += OnAvrLinkClosed;
    }

    /// <summary>The port 23 link closed. The poll reconnects and reads the full state at once.</summary>
    public event Action? AvrLinkClosed;

    /// <summary>A number for the open port 23 link (a new one for each connection), or 0 when none is open.</summary>
    public long AvrLinkGeneration => _avrClient.OpenLinkGeneration;

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

    /// <summary>
    /// The saved station this app started last, while nothing else took over since (standby, another input,
    /// another HEOS source or a relay stream clear it). Kept on disk, so after an app restart the synced meters
    /// can find the stream that still plays.
    /// </summary>
    public StationPreset? LastStartedStation =>
        _lastPlayedStationAlias is { } alias
            ? _settingsStore.Current.Receiver.Stations.FirstOrDefault(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase))
            : null;

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

    /// <summary>Zone 2 volume on the receiver's 0–98 scale. MCP, automations and scenes cap it at the safe volume first.</summary>
    public Task<ReceiverStatus> SetZone2VolumeAsync(int level, CancellationToken cancellationToken = default) =>
        ExecuteZone2Async(
            $"Set Zone 2 volume to {Math.Clamp(level, 0, DenonVolumeMath.MaxAbsoluteVolume)}",
            (settings, token) => _avrClient.SetZone2VolumeAsync(settings, Math.Clamp(level, 0, DenonVolumeMath.MaxAbsoluteVolume), token),
            cancellationToken);

    /// <summary>One step up or down. <paramref name="ceiling"/>: no step up from this level or above (the safe volume).</summary>
    public Task<ReceiverStatus> StepZone2VolumeAsync(bool up, int? ceiling = null, CancellationToken cancellationToken = default) =>
        ExecuteZone2Async(
            up ? "Zone 2 volume up" : "Zone 2 volume down",
            async (settings, token) =>
            {
                if (up && ceiling is int cap && _stateStore.Snapshot.Status?.Zone2Volume is int now && now >= cap)
                {
                    throw new InvalidOperationException($"Zone 2 is at {now}, the safe volume is {cap}.");
                }

                await _avrClient.StepZone2VolumeAsync(settings, up, token);
            },
            cancellationToken);

    public Task<ReceiverStatus> SetZone2MuteAsync(bool isMuted, CancellationToken cancellationToken = default) =>
        ExecuteZone2Async(
            isMuted ? "Mute Zone 2" : "Unmute Zone 2",
            (settings, token) => _avrClient.SetZone2MuteAsync(settings, isMuted, token),
            cancellationToken);

    public Task<ReceiverStatus> SetZone2SourceAsync(string sourceCode, CancellationToken cancellationToken = default) =>
        ExecuteZone2Async(
            $"Switch Zone 2 to {DenonCatalog.GetFriendlyZone2SourceName(sourceCode)}",
            (settings, token) => _avrClient.SetZone2SourceAsync(settings, sourceCode, token),
            cancellationToken);

    /// <summary>
    /// A Zone 2 command, then the status as it is known, with the receiver's answer in it: the receiver echoes a Zone 2
    /// change on port 23 at once, so no full status read (about 1 s) is needed.
    /// </summary>
    private Task<ReceiverStatus> ExecuteZone2Async(
        string description,
        Func<ReceiverSettings, CancellationToken, Task> command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            description,
            async (settings, token) =>
            {
                await command(settings, token);
                return _stateStore.Snapshot.Status is { IsAvrConfirmed: true } known
                    ? known with { RetrievedAt = DateTimeOffset.Now }
                    : await GetMergedStatusAsync(settings, token);
            },
            markBusy: false,
            logSuccess: true,
            logFailures: true,
            cancellationToken);

    /// <summary>The HEOS player this app controls: its id, to find its group.</summary>
    public Task<ReceiverPlayerInfo> GetOwnHeosPlayerAsync(CancellationToken cancellationToken = default) =>
        _heosClient.GetOwnPlayerAsync(_settingsStore.Current.Receiver, cancellationToken);

    /// <summary>Reads the HEOS players, the groups and each group's volume, and keeps them in the state store.</summary>
    public async Task<HeosGroupsState> RefreshHeosGroupsAsync(CancellationToken cancellationToken = default)
    {
        // One read at a time, whole: two that overlap could end in the wrong order, and the older one would win.
        await _groupsReadGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadHeosGroupsLockedAsync(cancellationToken);
        }
        finally
        {
            _groupsReadGate.Release();
        }
    }

    private async Task<HeosGroupsState> ReadHeosGroupsLockedAsync(CancellationToken cancellationToken)
    {
        // Group volume events that come in while this read runs are newer than it: the store keeps them.
        var readStart = _stateStore.BeginHeosGroupsRead();
        var settings = _settingsStore.Current.Receiver;
        var players = await _heosClient.GetPlayersAsync(settings, cancellationToken);
        var groups = new List<HeosGroup>();
        foreach (var group in await _heosClient.GetGroupsAsync(settings, cancellationToken))
        {
            try
            {
                var (volume, muted) = await _heosClient.GetGroupVolumeAsync(settings, group.GroupId, cancellationToken);
                groups.Add(group with { Volume = volume, IsMuted = muted });
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                groups.Add(group); // The group can end between the two reads: keep it without a volume.
            }
        }

        return _stateStore.UpdateHeosGroups(new HeosGroupsState(players, groups, DateTimeOffset.Now), readStart);
    }

    /// <summary>
    /// Groups the players: the first leads (it plays the source), the others play what it plays. One player alone ends
    /// the group it leads.
    /// </summary>
    public Task<HeosGroupsState> SetHeosGroupAsync(IReadOnlyList<int> playerIds, CancellationToken cancellationToken = default) =>
        ExecuteHeosGroupsAsync(
            playerIds.Count == 1 ? "End HEOS group" : $"Group {playerIds.Count} HEOS players",
            (settings, token) => _heosClient.SetGroupAsync(settings, playerIds, token),
            cancellationToken);

    /// <summary>
    /// Ends the group <paramref name="playerId"/> is in. A member leaves (the others stay a group); a leader ends the
    /// whole group. Nothing happens when the player plays alone.
    /// </summary>
    public async Task<HeosGroupsState> LeaveHeosGroupAsync(int playerId, CancellationToken cancellationToken = default)
    {
        // Read the groups now: another controller may have changed them since the last read.
        var state = await RefreshHeosGroupsAsync(cancellationToken);
        return state.GroupOf(playerId) is { } group
            ? await SetHeosGroupAsync(PlayersAfterLeave(group, playerId), cancellationToken)
            : state;
    }

    /// <summary>A player joins this receiver's HEOS group, or leaves it when it is in it (see <see cref="GroupPlayersAfterToggle"/>).</summary>
    public async Task<HeosGroupsState> ToggleHeosGroupMemberAsync(int playerId, CancellationToken cancellationToken = default)
    {
        var own = await GetOwnHeosPlayerAsync(cancellationToken);
        var state = await RefreshHeosGroupsAsync(cancellationToken);
        return await SetHeosGroupAsync(GroupPlayersAfterToggle(state, own.PlayerId, playerId), cancellationToken);
    }

    /// <summary>
    /// The set_group players after a tap on <paramref name="playerId"/> in the group list of player
    /// <paramref name="ownPlayerId"/> (this receiver): a player in the receiver's group leaves it, any other player joins
    /// it. Without a group the receiver leads a new one.
    /// </summary>
    internal static IReadOnlyList<int> GroupPlayersAfterToggle(HeosGroupsState state, int ownPlayerId, int playerId)
    {
        if (state.GroupOf(ownPlayerId) is not { } group)
        {
            return playerId == ownPlayerId ? [ownPlayerId] : [ownPlayerId, playerId];
        }

        if (group.Contains(playerId))
        {
            return PlayersAfterLeave(group, playerId);
        }

        var leader = group.Leader?.PlayerId ?? ownPlayerId;
        return [leader, .. group.Members.Select(static m => m.PlayerId).Where(p => p != leader), playerId];
    }

    /// <summary>
    /// The set_group players that take <paramref name="playerId"/> out of <paramref name="group"/>: the leader alone
    /// (the group ends) when it leads, else the leader and the other members.
    /// </summary>
    internal static IReadOnlyList<int> PlayersAfterLeave(HeosGroup group, int playerId)
    {
        var leader = group.Leader?.PlayerId ?? playerId;
        return leader == playerId
            ? [leader]
            : [leader, .. group.Members.Select(static m => m.PlayerId).Where(p => p != playerId && p != leader)];
    }

    /// <summary>The group's volume on the HEOS 0–100 scale. MCP and automations cap it at the safe volume first.</summary>
    public Task<HeosGroupsState> SetHeosGroupVolumeAsync(int groupId, int level, CancellationToken cancellationToken = default) =>
        ExecuteHeosGroupsAsync(
            $"Set HEOS group volume to {Math.Clamp(level, 0, 100)}",
            (settings, token) => _heosClient.SetGroupVolumeAsync(settings, groupId, Math.Clamp(level, 0, 100), token),
            cancellationToken);

    public Task<HeosGroupsState> SetHeosGroupMuteAsync(int groupId, bool isMuted, CancellationToken cancellationToken = default) =>
        ExecuteHeosGroupsAsync(
            isMuted ? "Mute HEOS group" : "Unmute HEOS group",
            (settings, token) => _heosClient.SetGroupMuteAsync(settings, groupId, isMuted, token),
            cancellationToken);

    private async Task<HeosGroupsState> ExecuteHeosGroupsAsync(
        string description,
        Func<ReceiverSettings, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            await command(_settingsStore.Current.Receiver, cancellationToken);
            var state = await RefreshHeosGroupsAsync(cancellationToken);
            _logStore.Info($"{description} completed.");
            return state;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logStore.Error($"{description} failed.", exception);
            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

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
    /// (it takes about 1 s). The last step reads the status. The receiver's push lines show a turn of the knob.
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
            var status = await _avrClient.GetAcousticStatusAsync(settings, cancellationToken);
            status = _stateStore.UpdateAcoustics(status, read => _avrClient.GetLiveState(settings.Host).ApplyAcousticsTo(read));
            return status;
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

        await ApplySceneZone2StepsAsync(settings, scene, token);
        await ApplySceneGroupStepsAsync(settings, scene, token);

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

        if (HasZone2Steps(scene))
        {
            _logStore.Warning($"Scene '{scene.Name}': Zone 2 skipped (no AVR control on port 23).");
        }

        await ApplySceneGroupStepsAsync(settings, scene, token);

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

    /// <summary>
    /// The Zone 2 mute as the receiver said it on this link, or its answer to Z2MU?. Null when it does not answer.
    /// </summary>
    private async Task<bool?> ReadZone2MutedAsync(ReceiverSettings settings, CancellationToken token)
    {
        if (_avrClient.GetLiveState(settings.Host).IsZone2Muted is bool live)
        {
            return live;
        }

        try
        {
            return await _avrClient.TryGetZone2MutedAsync(settings, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Runs a step that must work: a failure goes to the caller.</summary>
    private static async Task<bool> RunRequiredStepAsync(Func<Task> step)
    {
        await step();
        return true;
    }

    private static bool HasZone2Steps(ScenePreset scene) =>
        scene.Zone2On is not null || scene.Zone2Volume is not null || scene.Zone2Muted is not null ||
        !string.IsNullOrWhiteSpace(scene.Zone2Source);

    /// <summary>
    /// Zone 2: off alone, or on, then source, then volume, and mute last, so a muted zone never plays loud before the
    /// volume is down. Each step is optional: a model without Zone 2 does not stop the scene.
    /// </summary>
    private Task ApplySceneZone2StepsAsync(ReceiverSettings settings, ScenePreset scene, CancellationToken token) =>
        RunZone2StepsAsync(
            settings,
            scene.Zone2On,
            DenonCatalog.IsZone2SourceCode(scene.Zone2Source) ? scene.Zone2Source : null,
            scene.Zone2Volume,
            scene.Zone2Muted,
            TryRunSceneStepAsync,
            token);

    /// <summary>
    /// Zone 2 power, source, volume and mute in one go (MCP avr_set_zone2). The volume is the caller's, already capped
    /// at the safe volume. The order is that of <see cref="RunZone2StepsAsync"/>.
    /// </summary>
    public Task<ReceiverStatus> ApplyZone2Async(
        bool? on,
        string? source,
        int? volume,
        bool? muted,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(source) && !DenonCatalog.IsZone2SourceCode(source))
        {
            throw new ArgumentException($"'{source}' is not a Zone 2 source.", nameof(source));
        }

        return ExecuteZone2Async(
            "Set Zone 2",
            (settings, token) => RunZone2StepsAsync(
                settings, on, string.IsNullOrWhiteSpace(source) ? null : source, volume, muted, RunRequiredStepAsync, token),
            cancellationToken);
    }

    /// <summary>
    /// The Zone 2 steps in a safe order. Off: the other steps first (a source command can turn the zone on), then off.
    /// With a target volume, the zone is muted before it turns on or gets a new source, the volume is set, and only
    /// then does the mute go to what was asked (or back to what it was): a zone left loud never plays loud first.
    /// </summary>
    /// <param name="run">
    /// Runs one step and says if it worked: a scene skips a failed step, MCP lets it fail. When the volume step fails in
    /// a guarded sequence, the source and the mute release are skipped: the zone stays muted rather than play loud.
    /// </param>
    private async Task RunZone2StepsAsync(
        ReceiverSettings settings,
        bool? on,
        string? source,
        int? volume,
        bool? muted,
        Func<Func<Task>, Task<bool>> run,
        CancellationToken token)
    {
        var guard = volume is not null && (on == true || source is not null);

        // Without a mute asked for, the guard puts back the mute the zone had. Unknown (no answer): it stays muted.
        var finalMute = muted;
        if (guard && muted is null)
        {
            finalMute = await ReadZone2MutedAsync(settings, token) ?? true;
        }

        if (guard && !await run(() => _avrClient.SetZone2MuteAsync(settings, true, token)))
        {
            // Without the mute the zone could play at its old volume: no power on, no source, no volume. Off still runs.
            _logStore.Warning("Zone 2 could not be muted first: its power, source and volume steps were skipped.");
            if (on == false)
            {
                await run(() => _avrClient.SetZone2PowerAsync(settings, false, token));
            }

            return;
        }

        if (on == true)
        {
            await run(() => _avrClient.SetZone2PowerAsync(settings, true, token));
            if (guard)
            {
                // Mute again: a zone that was off may not take the first mute.
                await run(() => _avrClient.SetZone2MuteAsync(settings, true, token));
            }
        }

        if (volume is int level &&
            !await run(() => _avrClient.SetZone2VolumeAsync(settings, Math.Clamp(level, 0, DenonVolumeMath.MaxAbsoluteVolume), token)))
        {
            // The old volume may be loud: no new source and no unmute. A mute asked for, and off, still run.
            _logStore.Warning("Zone 2 volume could not be set: its source and unmute steps were skipped.");
            if (muted == true)
            {
                await run(() => _avrClient.SetZone2MuteAsync(settings, true, token));
            }

            if (on == false)
            {
                await run(() => _avrClient.SetZone2PowerAsync(settings, false, token));
            }

            return;
        }

        if (source is not null)
        {
            await run(() => _avrClient.SetZone2SourceAsync(settings, source, token));
        }

        if (finalMute is bool mute)
        {
            await run(() => _avrClient.SetZone2MuteAsync(settings, mute, token));
        }

        if (on == false)
        {
            await run(() => _avrClient.SetZone2PowerAsync(settings, false, token));
        }
    }

    /// <summary>The scene's HEOS group: this app's player leads, the named players join. Optional, as the Zone 2 steps.</summary>
    private async Task ApplySceneGroupStepsAsync(ReceiverSettings settings, ScenePreset scene, CancellationToken token)
    {
        if (scene.HeosGroupMembers.Count == 0 && !scene.HeosUngroup)
        {
            return;
        }

        await TryRunSceneStepAsync(async () =>
        {
            var own = await _heosClient.GetOwnPlayerAsync(settings, token);
            if (scene.HeosGroupMembers.Count > 0)
            {
                var members = await ResolveHeosPlayerIdsAsync(scene.HeosGroupMembers, token);
                await _heosClient.SetGroupAsync(settings, [own.PlayerId, .. members.Where(p => p != own.PlayerId)], token);
            }
            else if ((await _heosClient.GetGroupsAsync(settings, token)).FirstOrDefault(g => g.Contains(own.PlayerId)) is { } group)
            {
                await _heosClient.SetGroupAsync(settings, PlayersAfterLeave(group, own.PlayerId), token);
            }
        });
    }

    /// <summary>
    /// The ids of the named HEOS players: each name is a player's name (any case) or its id. An unknown name is an
    /// error that lists the players there are.
    /// </summary>
    public async Task<IReadOnlyList<int>> ResolveHeosPlayerIdsAsync(IEnumerable<string> namesOrIds, CancellationToken cancellationToken = default)
    {
        var players = await _heosClient.GetPlayersAsync(_settingsStore.Current.Receiver, cancellationToken);
        return ResolveHeosPlayerIds(players, namesOrIds);
    }

    internal static IReadOnlyList<int> ResolveHeosPlayerIds(IReadOnlyList<ReceiverPlayerInfo> players, IEnumerable<string> namesOrIds)
    {
        var ids = new List<int>();
        foreach (var raw in namesOrIds)
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var match = players.FirstOrDefault(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid)
                    ? players.FirstOrDefault(p => p.PlayerId == pid)
                    : null)
                ?? throw new InvalidOperationException(
                    $"No HEOS player '{name}'. Players: {(players.Count == 0 ? "none" : string.Join(", ", players.Select(static p => p.Name)))}.");

            if (!ids.Contains(match.PlayerId))
            {
                ids.Add(match.PlayerId);
            }
        }

        return ids;
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
            status = _stateStore.UpdateAcoustics(status, read => _avrClient.GetLiveState(settings.Host).ApplyAcousticsTo(read));
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

    /// <summary>Runs an optional scene step. False when it failed (logged, and the scene goes on).</summary>
    private async Task<bool> TryRunSceneStepAsync(Func<Task> step)
    {
        try
        {
            await step();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logStore.Warning($"Optional scene step skipped: {ex.Message}");
            return false;
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
            // Lines that came in after the read (a turn of the knob) are in the live state: they must not be lost.
            status = _stateStore.UpdateStatus(status, read => WithLiveAvrState(_avrClient.GetLiveState(receiverSettings.Host), read));
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

    /// <summary>
    /// The status with what the receiver said on the port 23 link (<paramref name="liveState"/>). Only for a status
    /// with a real AVR answer: a HEOS-only or grace-window status waits for the next full read.
    /// </summary>
    private static ReceiverStatus WithLiveAvrState(AvrLiveState liveState, ReceiverStatus status)
    {
        if (!status.IsAvrConfirmed)
        {
            return status;
        }

        var live = liveState.ApplyTo(status);
        // Off, or away from NET: no station plays (the same rule as the full read).
        return live.IsPoweredOn && live.IsMainZoneOn && DenonCatalog.InputMatches(live.InputSource, "NET")
            ? live
            : live with { ActiveStationAlias = null };
    }

    /// <summary>
    /// Answers and pushed lines alike: the state changes at once, and the events (power.changed, volume.settled and
    /// so on) follow from the state as for a poll. A new power or input also reads the full state soon after, for
    /// what HEOS plays now.
    /// </summary>
    private void OnAvrLinesReceived(AvrLinesReceived received)
    {
        var host = _settingsStore.Current.Receiver.Host;
        if (!string.Equals(received.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var before = _stateStore.Snapshot.Status;
        // Read inside the store's lock: the newest state for this link, also after the link closed (the receiver can
        // send a change and drop the link before this runs). Lines from a link a newer one replaced change nothing.
        _stateStore.PatchFromReceiver(
            status => _avrClient.LatestLiveState(received) is { } live ? WithLiveAvrState(live, status) : status,
            received.Lines.Any(AvrLiveState.IsAcousticLine)
                ? acoustics => _avrClient.LatestLiveState(received)?.ApplyAcousticsTo(acoustics) ?? acoustics
                : null);

        if (before is { IsAvrConfirmed: true } &&
            _stateStore.Snapshot.Status is { } after &&
            (before.IsPoweredOn != after.IsPoweredOn ||
             before.IsMainZoneOn != after.IsMainZoneOn ||
             !string.Equals(before.InputSource, after.InputSource, StringComparison.OrdinalIgnoreCase)))
        {
            ScheduleRefreshAfterPush();
        }
    }

    /// <summary>Any address: after Save &amp; Sync the settings may already name the new receiver when the old link closes.</summary>
    private void OnAvrLinkClosed(string host) => AvrLinkClosed?.Invoke();

    /// <summary>One full read 400 ms after the last power or input push (the remote sends a few in a row).</summary>
    private void ScheduleRefreshAfterPush()
    {
        CancellationToken token;
        lock (_pushRefreshSync)
        {
            _pushRefreshCts?.Cancel();
            _pushRefreshCts?.Dispose();
            _pushRefreshCts = new CancellationTokenSource();
            token = _pushRefreshCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                await RefreshAsync(token);
            }
            catch
            {
                // A newer push took over, or the read failed: the poll reads again.
            }
        }, CancellationToken.None);
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
                    Zone2Volume = prevConfirmed.Zone2Volume,
                    Zone2Source = prevConfirmed.Zone2Source,
                    IsZone2Muted = prevConfirmed.IsZone2Muted,
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
            Zone2Volume = avrStatus.Zone2Volume,
            Zone2Source = avrStatus.Zone2Source,
            IsZone2Muted = avrStatus.IsZone2Muted,
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
    /// For a fade step: reads the main zone power, the input and the real volume only (three queries, not a full
    /// status read), so Volume Memory still sees standby, a new source or a turn of the knob and stops the fade.
    /// Null when the zone is not on, the input changed or a query got no answer: then the caller does the full
    /// read. A HEOS-only speaker has no zone or input; HEOS gives the volume and mute.
    /// </summary>
    private async Task<ReceiverStatus?> TryReadFadeStepAsync(ReceiverSettings settings, ReceiverStatus known, CancellationToken cancellationToken)
    {
        if (!_avrProtocolAvailable)
        {
            var heos = await _heosClient.GetStatusAsync(settings, cancellationToken);
            return known with { Volume = heos.Volume, VolumeExact = heos.Volume, IsMuted = heos.IsMuted };
        }

        if (!await _avrClient.IsMainZoneOnAsync(settings, cancellationToken))
        {
            return null; // Standby (or no answer): the full read shows it.
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

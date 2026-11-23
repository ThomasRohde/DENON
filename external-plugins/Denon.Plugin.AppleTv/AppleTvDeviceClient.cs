using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Hap;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.AppleTv;

internal enum AppleTvLink
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>Live state of one Apple TV.</summary>
internal sealed record AppleTvDeviceState(string DeviceId)
{
    public AppleTvLink Link { get; init; } = AppleTvLink.Disconnected;

    public AttentionState Power { get; init; } = AttentionState.Unknown;

    /// <summary>The AirPlay MRP tunnel (now playing) is open.</summary>
    public bool NowPlayingLinked { get; init; }

    public NowPlayingSnapshot NowPlaying { get; init; } = NowPlayingSnapshot.Empty;

    /// <summary>Readable app name (from MRP, else from the Apple TV's app list).</summary>
    public string? AppName { get; init; }

    public string? Error { get; init; }

    public bool IsAwake => Power is AttentionState.Awake or AttentionState.Screensaver or AttentionState.Idle;

    /// <summary>An app shows a program (playing or paused).</summary>
    public bool HasMedia => Link == AppleTvLink.Connected && IsAwake && NowPlaying.HasMedia && !string.IsNullOrEmpty(NowPlaying.AppBundleId);

    public bool IsPlaying => HasMedia && NowPlaying.State == MrpPlaybackState.Playing;
}

/// <summary>
/// Keeps one Apple TV connected. The light Companion link (power state, keys, apps) stays open with
/// reconnect and backoff; the AirPlay MRP tunnel (now playing) is opened only while the Apple TV is awake
/// and closed when it sleeps, so the plugin never asks a sleeping Apple TV for media services.
/// </summary>
internal sealed class AppleTvDeviceClient : IAsyncDisposable
{
    private static readonly TimeSpan PowerPollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NowPlayingRetry = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _companionPort;
    private readonly int _airPlayPort;
    private readonly HapCredentials _credentials;
    private readonly CompanionIdentity _identity;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _poke = new(0);
    private readonly CancellationTokenSource _cts = new();
    private CompanionClient? _companion;
    private MrpClient? _mrp;
    private bool _mrpDead;
    private DateTimeOffset _mrpRetryAt = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<string, string> _appNames = new Dictionary<string, string>();
    private Task? _loop;

    public AppleTvDeviceClient(string deviceId, string host, int companionPort, int airPlayPort, HapCredentials credentials, CompanionIdentity identity)
    {
        _host = host;
        _companionPort = companionPort;
        _airPlayPort = airPlayPort;
        _credentials = credentials;
        _identity = identity;
        State = new AppleTvDeviceState(deviceId);
    }

    public AppleTvDeviceState State { get; private set; }

    /// <summary>(previous, current) after every change.</summary>
    public event Action<AppleTvDeviceState, AppleTvDeviceState>? StateChanged;

    public event Action<string>? Diagnostic;

    public IReadOnlyDictionary<string, string> AppNames => _appNames;

    public void Start() => _loop = Task.Run(RunAsync);

    // ---------------------------------------------------------------- commands

    public Task PressAsync(HidCommand command, CancellationToken ct) => Companion().PressAsync(command, ct);

    public Task PlayAsync(CancellationToken ct) => MediaCommandAsync(MrpCommand.Play, 1, ct);

    public Task PauseAsync(CancellationToken ct) => MediaCommandAsync(MrpCommand.Pause, 2, ct);

    /// <summary>
    /// Play/pause through the MRP channel to the app that plays (as pyatv does on tvOS); the Companion media
    /// control (_mcc) is the fallback while the MRP channel is closed.
    /// </summary>
    private async Task MediaCommandAsync(MrpCommand command, int companionCommand, CancellationToken ct)
    {
        if (_mrp is { } mrp && !_mrpDead)
        {
            await mrp.SendCommandAsync(command, ct);
            Diagnostic?.Invoke($"{command} accepted by the app.");
            return;
        }

        await Companion().MediaControlAsync(companionCommand, ct);
    }

    public Task LaunchAppAsync(string bundleIdOrUrl, CancellationToken ct) => Companion().LaunchAppAsync(bundleIdOrUrl, ct);

    public async Task<IReadOnlyDictionary<string, string>> FetchAppsAsync(CancellationToken ct)
    {
        var apps = await Companion().FetchAppsAsync(ct);
        _appNames = apps;
        return apps;
    }

    /// <summary>Wake (power on) or sleep (power off). The new power state arrives as an event or at the next poll.</summary>
    public async Task SetPowerAsync(bool on, CancellationToken ct)
    {
        await Companion().PressAsync(on ? HidCommand.Wake : HidCommand.Sleep, ct);
        _poke.Release();
    }

    private CompanionClient Companion() =>
        _companion is { IsConnected: true } c ? c : throw new InvalidOperationException("The Apple TV is not connected.");

    // ---------------------------------------------------------------- connection loop

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            SetState(s => s with { Link = AppleTvLink.Connecting });
            try
            {
                var companion = await CompanionClient.ConnectAsync(_host, _companionPort, _credentials, _identity, ct);
                await using (companion)
                {
                    var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    companion.Closed += ex => closed.TrySetResult(ex);
                    companion.EventReceived += OnCompanionEvent;
                    _companion = companion;
                    backoff = TimeSpan.FromSeconds(5);

                    try
                    {
                        await companion.SubscribeAsync("TVSystemStatus", ct);
                    }
                    catch (CompanionProtocolException)
                    {
                    }

                    try
                    {
                        _appNames = await companion.FetchAppsAsync(ct);
                    }
                    catch (CompanionProtocolException ex)
                    {
                        Diagnostic?.Invoke($"App list unavailable: {ex.Message}");
                    }

                    var power = await companion.FetchAttentionStateAsync(ct);
                    SetState(s => s with { Link = AppleTvLink.Connected, Power = power, Error = null });
                    Diagnostic?.Invoke($"Connected (power: {power}).");

                    Task? pendingPoke = null; // one wait at a time: a stale waiter would swallow a later signal
                    while (!ct.IsCancellationRequested && companion.IsConnected)
                    {
                        await ManageNowPlayingAsync(ct);
                        var delay = Task.Delay(PowerPollInterval, ct);
                        pendingPoke ??= _poke.WaitAsync(ct);
                        var finished = await Task.WhenAny(delay, closed.Task, pendingPoke);
                        if (finished == pendingPoke)
                        {
                            pendingPoke = null;
                        }

                        if (finished == closed.Task)
                        {
                            Diagnostic?.Invoke($"Companion link closed: {closed.Task.Result?.Message ?? "by the Apple TV"}.");
                            break;
                        }

                        if (finished == delay || State.Power == AttentionState.Unknown)
                        {
                            var polled = await companion.FetchAttentionStateAsync(ct);
                            SetState(s => s with { Power = polled });
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"Connection failed: {ex.Message}");
                SetState(s => s with { Link = AppleTvLink.Disconnected, Error = ex.Message });
            }
            finally
            {
                _companion = null;
                await CloseNowPlayingAsync();
            }

            SetState(s => s with { Link = AppleTvLink.Disconnected });
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private async Task ManageNowPlayingAsync(CancellationToken ct)
    {
        if (_mrpDead)
        {
            await CloseNowPlayingAsync();
            _mrpRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        }

        if (!State.IsAwake)
        {
            if (_mrp is not null)
            {
                Diagnostic?.Invoke("Apple TV sleeps: now-playing link closed.");
                await CloseNowPlayingAsync();
            }

            return;
        }

        if (_mrp is not null || DateTimeOffset.UtcNow < _mrpRetryAt)
        {
            return;
        }

        try
        {
            var mrp = await MrpClient.ConnectAsync(_host, _airPlayPort, _credentials, _identity, ct);
            mrp.NowPlayingChanged += snapshot => SetState(s => s with { NowPlaying = snapshot, AppName = NameFor(snapshot) });
            mrp.Closed += _ =>
            {
                _mrpDead = true;
                _poke.Release();
            };
            _mrp = mrp;
            _mrpDead = false;
            var now = mrp.NowPlaying.Current;
            SetState(s => s with { NowPlayingLinked = true, NowPlaying = now, AppName = NameFor(now) });
            Diagnostic?.Invoke("Now-playing link open.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _mrpRetryAt = DateTimeOffset.UtcNow + NowPlayingRetry;
            Diagnostic?.Invoke($"Now-playing link failed (retry in {NowPlayingRetry.TotalSeconds:0} s): {ex.Message}");
        }
    }

    private async Task CloseNowPlayingAsync()
    {
        var mrp = _mrp;
        _mrp = null;
        _mrpDead = false;
        if (mrp is not null)
        {
            try
            {
                await mrp.DisposeAsync();
            }
            catch
            {
            }
        }

        SetState(s => s with { NowPlayingLinked = false, NowPlaying = NowPlayingSnapshot.Empty, AppName = null });
    }

    private void OnCompanionEvent(string name, Dictionary<object, object?> content)
    {
        if (name == "TVSystemStatus" && content.GetValueOrDefault("state") is long state && Enum.IsDefined(typeof(AttentionState), (int)state))
        {
            SetState(s => s with { Power = (AttentionState)state });
            _poke.Release();
        }
    }

    private string? NameFor(NowPlayingSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.AppName) ? snapshot.AppName
        : snapshot.AppBundleId is { } bundle && _appNames.TryGetValue(bundle, out var name) ? name
        : null;

    private void SetState(Func<AppleTvDeviceState, AppleTvDeviceState> change)
    {
        AppleTvDeviceState previous;
        AppleTvDeviceState current;
        lock (_sync)
        {
            previous = State;
            current = change(previous);
            if (current == previous)
            {
                return;
            }

            State = current;
        }

        try
        {
            StateChanged?.Invoke(previous, current);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }

        await CloseNowPlayingAsync();
        _cts.Dispose();
    }
}

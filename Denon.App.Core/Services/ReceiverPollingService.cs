using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

/// <summary>
/// Reads the full receiver state. While the receiver pushes its changes (the port 23 link stayed open since the last
/// read, and the HEOS change events are connected), a full read is only a slow check: see
/// <see cref="SlowCheckInterval"/>. When the link closes, the next read runs at once: it connects again and reads the
/// full state, and the reads keep the normal pace (Poll Interval) until the link holds again.
/// </summary>
public sealed class ReceiverPollingService : BackgroundService
{
    private readonly ReceiverController _receiverController;
    private readonly DenonDiscoveryService _discoveryService;
    private readonly AppSettingsStore _settingsStore;
    private readonly HeosEventListenerService _heosEvents;
    private readonly AppLogStore _logStore;
    private readonly object _wakeSync = new();
    private CancellationTokenSource? _wakeCts;
    private string? _lastPollFailure;
    private int _consecutiveFailures;
    private long _lastLinkGeneration;
    private bool _linkHeld;

    public ReceiverPollingService(
        ReceiverController receiverController,
        DenonDiscoveryService discoveryService,
        AppSettingsStore settingsStore,
        HeosEventListenerService heosEvents,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
        _discoveryService = discoveryService;
        _settingsStore = settingsStore;
        _heosEvents = heosEvents;
        _logStore = logStore;
    }

    /// <summary>
    /// The pace of the full read while the receiver pushes its changes: 9 times the Poll Interval, from 30 to 60 s
    /// (5 s gives 45 s). The pushes show every change at once; this read only checks that nothing was missed.
    /// </summary>
    public static TimeSpan SlowCheckInterval(int pollIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(pollIntervalSeconds * 9, 30, 60));

    /// <summary>True while the reads run at the slow pace.</summary>
    public bool IsSlowCheck { get; private set; }

    /// <summary>Tests: the normal pace in place of Poll Interval (which is at least 2 s).</summary>
    internal TimeSpan? NormalIntervalOverride { get; set; }

    /// <summary>Tests: the slow pace in place of <see cref="SlowCheckInterval"/>.</summary>
    internal TimeSpan? SlowIntervalOverride { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logStore.Info("Receiver polling service started.");
        _receiverController.AvrLinkClosed += Wake;
        _heosEvents.PushConnectionChanged += OnHeosPushChanged;
        try
        {
            await PollLoopAsync(stoppingToken);
        }
        finally
        {
            _receiverController.AvrLinkClosed -= Wake;
            _heosEvents.PushConnectionChanged -= OnHeosPushChanged;
        }

        _logStore.Info("Receiver polling service stopped.");
    }

    private async Task PollLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var currentSettings = _settingsStore.Current;
                if (string.IsNullOrWhiteSpace(currentSettings.Receiver.Host))
                {
                    _logStore.Info("No receiver IP configured — scanning local network for Denon / Marantz HEOS receivers...");
                    var discovered = await _discoveryService.DiscoverAsync(TimeSpan.FromSeconds(3.5), stoppingToken);
                    if (discovered.Count == 1)
                    {
                        var sole = discovered[0];
                        await _settingsStore.UpdateAsync(s =>
                        {
                            s.Receiver.Host = sole.IpAddress;
                            s.Receiver.Port = sole.Port;
                            s.Receiver.PlayerName = sole.Name;
                        }, stoppingToken);
                        _logStore.Info($"Auto-detected receiver: {sole.Name} ({sole.Model}) at {sole.IpAddress}.");
                    }
                    else if (discovered.Count > 1)
                    {
                        var names = string.Join(", ", discovered.Select(static d => $"{d.Name} ({d.IpAddress})"));
                        throw new InvalidOperationException($"Multiple rooms found ({names}). Open Settings → Scan LAN to choose which room to control.");
                    }
                    else
                    {
                        throw new InvalidOperationException("No Denon / Marantz HEOS receiver found automatically. Open Settings to Scan LAN or enter your receiver IP.");
                    }
                }

                var status = await _receiverController.RefreshAsync(stoppingToken);
                if (_consecutiveFailures > 0)
                {
                    _logStore.Info("Receiver connection restored.");
                }

                _lastPollFailure = null;
                _consecutiveFailures = 0;

                // The link held when this read and the one before used the same connection.
                var generation = _receiverController.AvrLinkGeneration;
                _linkHeld = status.IsAvrConfirmed && generation != 0 && generation == _lastLinkGeneration;
                _lastLinkGeneration = generation;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _consecutiveFailures++;
                _linkHeld = false;
                _lastLinkGeneration = _receiverController.AvrLinkGeneration;
                if (!string.Equals(_lastPollFailure, exception.Message, StringComparison.Ordinal))
                {
                    _logStore.Warning($"Receiver poll failed. {exception.Message}");
                    _lastPollFailure = exception.Message;
                }
            }

            var slow = _consecutiveFailures == 0 &&
                       _linkHeld &&
                       _receiverController.AvrLinkGeneration == _lastLinkGeneration &&
                       _heosEvents.IsPushConnected;
            var pollSeconds = _settingsStore.Current.Receiver.PollIntervalSeconds;
            if (slow != IsSlowCheck)
            {
                IsSlowCheck = slow;
                _logStore.Info(slow
                    ? $"Receiver pushes its changes: full check every {SlowCheckInterval(pollSeconds).TotalSeconds:0} s."
                    : $"Receiver push link not held: full read every {pollSeconds} s.");
            }

            try
            {
                if (slow)
                {
                    await WaitSlowCheckAsync(SlowIntervalOverride ?? SlowCheckInterval(pollSeconds), stoppingToken);
                }
                else
                {
                    await Task.Delay(NormalDelay(pollSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private TimeSpan NormalDelay(int pollSeconds)
    {
        var backoffMultiplier = _consecutiveFailures switch
        {
            0 => 1,
            <= 3 => 2,
            _ => 3,
        };

        return NormalIntervalOverride is { } normal
            ? normal * backoffMultiplier
            : TimeSpan.FromSeconds(Math.Clamp(pollSeconds * backoffMultiplier, 2, 30));
    }

    /// <summary>Waits for the slow check, or less when the port 23 link closes or the HEOS events drop.</summary>
    private async Task WaitSlowCheckAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_wakeSync)
        {
            _wakeCts = wakeCts;
        }

        try
        {
            // A link that closed while the read ran must not wait out the slow check.
            if (_receiverController.AvrLinkGeneration != _lastLinkGeneration || !_heosEvents.IsPushConnected)
            {
                return;
            }

            await Task.Delay(delay, wakeCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Woken: connect again and read the full state now.
        }
        finally
        {
            lock (_wakeSync)
            {
                _wakeCts = null;
            }
        }
    }

    private void OnHeosPushChanged(bool connected)
    {
        if (!connected)
        {
            Wake();
        }
    }

    private void Wake()
    {
        lock (_wakeSync)
        {
            try { _wakeCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

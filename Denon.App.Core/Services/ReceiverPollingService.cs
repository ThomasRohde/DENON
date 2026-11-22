using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed class ReceiverPollingService : BackgroundService
{
    private readonly ReceiverController _receiverController;
    private readonly DenonDiscoveryService _discoveryService;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private string? _lastPollFailure;
    private int _consecutiveFailures;

    public ReceiverPollingService(
        ReceiverController receiverController,
        DenonDiscoveryService discoveryService,
        AppSettingsStore settingsStore,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
        _discoveryService = discoveryService;
        _settingsStore = settingsStore;
        _logStore = logStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logStore.Info("Receiver polling service started.");

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

                await _receiverController.RefreshAsync(stoppingToken);
                if (_consecutiveFailures > 0)
                {
                    _logStore.Info("Receiver connection restored.");
                }

                _lastPollFailure = null;
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _consecutiveFailures++;
                if (!string.Equals(_lastPollFailure, exception.Message, StringComparison.Ordinal))
                {
                    _logStore.Warning($"Receiver poll failed. {exception.Message}");
                    _lastPollFailure = exception.Message;
                }
            }

            var baseSeconds = _settingsStore.Current.Receiver.PollIntervalSeconds;
            var backoffMultiplier = _consecutiveFailures switch
            {
                0 => 1,
                <= 3 => 2,
                _ => 3,
            };
            var delaySeconds = Math.Clamp(baseSeconds * backoffMultiplier, 2, 30);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logStore.Info("Receiver polling service stopped.");
    }
}

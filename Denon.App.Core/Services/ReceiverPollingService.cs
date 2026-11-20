using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed class ReceiverPollingService : BackgroundService
{
    private readonly ReceiverController _receiverController;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private string? _lastPollFailure;
    private int _consecutiveFailures;

    public ReceiverPollingService(
        ReceiverController receiverController,
        AppSettingsStore settingsStore,
        AppLogStore logStore)
    {
        _receiverController = receiverController;
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

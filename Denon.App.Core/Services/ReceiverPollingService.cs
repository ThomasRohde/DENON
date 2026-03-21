using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

public sealed class ReceiverPollingService : BackgroundService
{
    private readonly ReceiverController _receiverController;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppLogStore _logStore;
    private string? _lastPollFailure;

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
                _lastPollFailure = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (!string.Equals(_lastPollFailure, exception.Message, StringComparison.Ordinal))
                {
                    _logStore.Warning($"Receiver poll failed. {exception.Message}");
                    _lastPollFailure = exception.Message;
                }
            }

            var delay = TimeSpan.FromSeconds(_settingsStore.Current.Receiver.PollIntervalSeconds);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logStore.Info("Receiver polling service stopped.");
    }
}

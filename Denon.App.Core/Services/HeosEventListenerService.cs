using System.Net.Sockets;
using System.Text;
using Denon.App.Core.Heos;
using Denon.App.Core.Models;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

/// <summary>
/// Maintains a dedicated read-only HEOS change-event subscription on port 1255 so physical volume knob turns,
/// remote mutes, play-state transitions, and track changes appear in the UI in &lt;50ms instead of waiting for polling.
/// </summary>
public sealed class HeosEventListenerService : BackgroundService
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ReceiverStateStore _stateStore;
    private readonly ReceiverController _receiverController;
    private readonly AppLogStore _logStore;

    private volatile bool _isPushConnected;
    private CancellationTokenSource? _reconnectTriggerCts;

    public HeosEventListenerService(
        AppSettingsStore settingsStore,
        ReceiverStateStore stateStore,
        ReceiverController receiverController,
        AppLogStore logStore)
    {
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _receiverController = receiverController;
        _logStore = logStore;

        _settingsStore.SettingsChanged += _ => TriggerReconnect();
    }

    public event Action<bool>? PushConnectionChanged;

    public bool IsPushConnected => _isPushConnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffSeconds = 2;

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settingsStore.Current.Receiver;
            _reconnectTriggerCts?.Dispose();
            _reconnectTriggerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loopToken = _reconnectTriggerCts.Token;

            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), loopToken);
                }
                catch (OperationCanceledException)
                {
                }

                continue;
            }

            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(4));

                await tcp.ConnectAsync(settings.Host, settings.Port, connectCts.Token);

                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                {
                    NewLine = "\r\n",
                    AutoFlush = true,
                };

                await writer.WriteLineAsync("heos://system/register_for_change_events?enable=on");
                SetPushConnected(true);
                backoffSeconds = 2;
                _logStore.Info($"Real-time HEOS push events subscribed ({settings.Host}:{settings.Port}).");

                while (!loopToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(loopToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    HandlePushLine(line, loopToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Settings changed — immediately reconnect to new host/port
                backoffSeconds = 1;
            }
            catch (Exception)
            {
                // Receiver asleep/offline; quietly retry with capped backoff
            }
            finally
            {
                SetPushConnected(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                backoffSeconds = Math.Min(backoffSeconds * 2, 20);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void HandlePushLine(string rawLine, CancellationToken cancellationToken)
    {
        try
        {
            if (!HeosMessageParser.TryParseLine(rawLine, string.Empty, out var parsed) || parsed is null)
            {
                return;
            }

            var command = parsed.Command;
            if (!HeosMessageParser.IsEvent(command))
            {
                return;
            }

            var parameters = parsed.MessageFields;
            var currentPid = _stateStore.Snapshot.Status?.Player.PlayerId;
            if (currentPid is int expectedPid &&
                parameters.TryGetValue("pid", out var pidStr) &&
                int.TryParse(pidStr, out var eventPid) &&
                eventPid != expectedPid)
            {
                return;
            }

            if (command.Equals("event/player_volume_changed", StringComparison.OrdinalIgnoreCase))
            {
                int? level = parameters.TryGetValue("level", out var lvl) && double.TryParse(lvl, System.Globalization.CultureInfo.InvariantCulture, out var parsedLvl)
                    ? Math.Clamp((int)Math.Round(parsedLvl), 0, 98)
                    : null;

                bool? muted = parameters.TryGetValue("mute", out var muteVal)
                    ? string.Equals(muteVal, "on", StringComparison.OrdinalIgnoreCase)
                    : null;

                if (level.HasValue || muted.HasValue)
                {
                    _stateStore.PatchLiveTelemetry(volume: level, isMuted: muted);
                }
            }
            else if (command.Equals("event/player_state_changed", StringComparison.OrdinalIgnoreCase))
            {
                if (parameters.TryGetValue("state", out var state) && !string.IsNullOrWhiteSpace(state))
                {
                    _stateStore.PatchLiveTelemetry(playState: state.Trim());
                }
            }
            else if (command.Equals("event/player_now_playing_changed", StringComparison.OrdinalIgnoreCase))
            {
                // Trigger a fast metadata refresh so album art & song title update immediately
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _receiverController.RefreshAsync(cancellationToken);
                    }
                    catch
                    {
                    }
                }, cancellationToken);
            }
        }
        catch
        {
            // Ignore malformed push frames
        }
    }

    private void TriggerReconnect()
    {
        try { _reconnectTriggerCts?.Cancel(); } catch { }
    }

    private void SetPushConnected(bool connected)
    {
        if (_isPushConnected == connected)
        {
            return;
        }

        _isPushConnected = connected;
        PushConnectionChanged?.Invoke(connected);
    }

    public override void Dispose()
    {
        _reconnectTriggerCts?.Dispose();
        base.Dispose();
    }
}

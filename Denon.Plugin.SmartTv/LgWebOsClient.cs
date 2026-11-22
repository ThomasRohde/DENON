using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Denon.Plugin.SmartTv;

/// <summary>
/// Persistent, auto-reconnecting LG webOS SSAP WebSocket (wss://:3001 &amp; ws://:3000) client
/// with real-time state subscriptions, Magic Remote pointer input socket, IME keyboard input,
/// dynamic app/input discovery, OLED screen-off control, and multi-interface Wake-on-LAN.
/// </summary>
public sealed partial class LgWebOsClient : IAsyncDisposable
{
    private static readonly string[] RegistrationPermissions =
    [
        "LAUNCH",
        "LAUNCH_WEBAPP",
        "APP_TO_APP",
        "CLOSE",
        "TEST_OPEN",
        "TEST_PROTECTED",
        "CONTROL_AUDIO",
        "CONTROL_DISPLAY",
        "CONTROL_INPUT_JOYSTICK",
        "CONTROL_INPUT_MEDIA_RECORDING",
        "CONTROL_INPUT_MEDIA_PLAYBACK",
        "CONTROL_INPUT_TV",
        "CONTROL_POWER",
        "CONTROL_INPUT_TEXT",
        "CONTROL_MOUSE_AND_KEYBOARD",
        "READ_APP_STATUS",
        "READ_CURRENT_CHANNEL",
        "READ_INPUT_DEVICE_LIST",
        "READ_INSTALLED_APPS",
        "READ_NETWORK_STATE",
        "READ_RUNNING_APPS",
        "READ_TV_CHANNEL_LIST",
        "READ_POWER_STATE",
        "READ_SETTINGS",
        "WRITE_SETTINGS",
        "WRITE_NOTIFICATION_TOAST",
        "WRITE_NOTIFICATION_ALERT",
        "READ_UPDATE_INFO",
        "READ_COUNTRY_INFO",
        "CHECK_BLUETOOTH_DEVICE",
        "SEARCH",
    ];

    private static readonly string[] FavoriteAppPriority =
    [
        "youtube",
        "netflix",
        "plex",
        "cdp-30",
        "disney",
        "spotify",
        "amazon",
        "prime",
        "appletv",
        "apple",
        "max",
        "hbo",
        "viaplay",
        "drtv",
        "tv2",
        "twitch",
        "livetv",
        "browser",
    ];

    private static readonly HttpClient SnapshotHttpClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    // Empirically calibrated LG webOS pointer logical coordinate space (1280x720 with uniform small deltas).
    private const int LogicalScreenWidth = 1280;
    private const int LogicalScreenHeight = 720;

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _pointerGate = new(1, 1);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _subscriptions = new();
    private readonly object _stateLock = new();

    private ClientWebSocket? _ws;
    private ClientWebSocket? _pointerWs;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _monitorCts;
    private Task? _receiveTask;
    private Task? _monitorTask;

    private string _configuredIp = string.Empty;
    private string _configuredClientKey = string.Empty;
    private bool _demoMode;
    private string _transportLabel = "OFFLINE";
    private int _msgId;
    private bool _disposed;
    private int _cursorPxX = LogicalScreenWidth / 2;
    private int _cursorPxY = LogicalScreenHeight / 2;
    private bool _cursorCalibrated;
    private DateTimeOffset _lastCaptureUtc = DateTimeOffset.MinValue;

    private LgTvLiveState _state = new();

    public event Action<LgTvLiveState>? StateChanged;

    public event Action<string>? ClientKeyUpdated;

    public event Action<string>? DiagnosticLog;

    public LgTvLiveState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsConnected => _ws?.State == WebSocketState.Open || _demoMode;

    public void Configure(string tvIp, string clientKey, bool demoMode, string? fallbackModel = null, string? fallbackWebOs = null)
    {
        var cleanIp = CleanHost(tvIp);
        var cleanKey = (clientKey ?? string.Empty).Trim();
        var endpointChanged =
            !string.Equals(_configuredIp, cleanIp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_configuredClientKey, cleanKey, StringComparison.Ordinal) ||
            _demoMode != demoMode;

        _configuredIp = cleanIp;
        _configuredClientKey = cleanKey;
        _demoMode = demoMode;

        if (_demoMode)
        {
            _ = DisconnectInternalAsync();
            InitializeDemoState();
            return;
        }

        if (endpointChanged)
        {
            _ = DisconnectInternalAsync();
            UpdateState(s => s with
            {
                IsConnected = false,
                IsDemo = false,
                IsPointerConnected = false,
                TransportLabel = "OFFLINE",
                ModelName = string.IsNullOrWhiteSpace(s.ModelName) ? (fallbackModel ?? string.Empty) : s.ModelName,
                WebOsVersion = string.IsNullOrWhiteSpace(s.WebOsVersion) ? (fallbackWebOs ?? string.Empty) : s.WebOsVersion,
            });
        }
    }

    public void StartBackgroundMonitor()
    {
        if (_monitorTask is not null && !_monitorTask.IsCompleted)
        {
            return;
        }

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        var ct = _monitorCts.Token;
        _monitorTask = Task.Run(() => MonitorLoopAsync(ct), ct);
    }

    public async Task StopBackgroundMonitorAsync()
    {
        _monitorCts?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask; } catch { }
        }

        await DisconnectInternalAsync();
    }

    /// <summary>
    /// Pairs with the LG TV over wss://:3001 (falling back to ws://:3000), keeps the live session open,
    /// subscribes to real-time TV events, and returns the saved client-key.
    /// </summary>
    public async Task<string> PairAsync(string tvIp, string? existingClientKey, bool forcePairing = false, CancellationToken ct = default)
    {
        _demoMode = false;
        _configuredIp = CleanHost(tvIp);
        if (!forcePairing && !string.IsNullOrWhiteSpace(existingClientKey))
        {
            _configuredClientKey = existingClientKey.Trim();
        }
        else if (forcePairing)
        {
            _configuredClientKey = string.Empty;
        }

        await DisconnectInternalAsync();
        await EnsureConnectedAsync(allowInteractivePairing: true, forcePairing: forcePairing, ct: ct);
        return _configuredClientKey;
    }

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await HydrateInitialStateAsync(ct);
    }

    public async Task<JsonElement> RequestAsync(string uri, object? payload = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (_demoMode)
        {
            return default;
        }

        await EnsureConnectedAsync(allowInteractivePairing: string.IsNullOrWhiteSpace(_configuredClientKey), forcePairing: false, ct: ct);
        return await SendRequestCoreAsync(uri, payload, ct, timeout ?? TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Launches an app on the persistent SSAP WebSocket using com.webos.applicationManager/launch
    /// (preventing the session-teardown bounce-back bug) with system.launcher/launch for video deep-links.
    /// </summary>
    public async Task<string> LaunchAppAsync(string appIdOrAlias, string? contentId = null, CancellationToken ct = default)
    {
        var resolvedId = ResolveInstalledAppId(appIdOrAlias);

        if (_demoMode)
        {
            var title = CurrentState.Apps.FirstOrDefault(a => a.Id.Equals(resolvedId, StringComparison.OrdinalIgnoreCase))?.Title ?? resolvedId;
            UpdateState(s => s with
            {
                IsScreenOff = false,
                ForegroundAppId = resolvedId,
                ForegroundAppTitle = title,
                Apps = MarkActiveApp(s.Apps, resolvedId),
                Inputs = MarkActiveInput(s.Inputs, resolvedId),
            });
            return title;
        }

        await EnsureConnectedAsync(allowInteractivePairing: string.IsNullOrWhiteSpace(_configuredClientKey), forcePairing: false, ct: ct);

        if (!string.IsNullOrWhiteSpace(contentId))
        {
            var normalizedContent = NormalizeYouTubeContentId(resolvedId, contentId);
            await SendRequestCoreAsync(
                "ssap://system.launcher/launch",
                new { id = resolvedId, contentId = normalizedContent },
                ct,
                TimeSpan.FromSeconds(10));
        }
        else
        {
            // Prefer com.webos.applicationManager/launch on the persistent WebSocket so the app launches
            // as an independent foreground webOS lifecycle and never bounces back to Live TV.
            try
            {
                await SendRequestCoreAsync(
                    "ssap://com.webos.applicationManager/launch",
                    new { id = resolvedId },
                    ct,
                    TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                DiagnosticLog?.Invoke($"applicationManager/launch fallback ({ex.Message}); trying system.launcher/launch.");
                await SendRequestCoreAsync(
                    "ssap://system.launcher/launch",
                    new { id = resolvedId },
                    ct,
                    TimeSpan.FromSeconds(10));
            }
        }

        // Optimistically refresh foreground info after launch so UI updates immediately.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, CancellationToken.None);
                await QueryForegroundAppOnceAsync(CancellationToken.None);
            }
            catch
            {
                // Best effort.
            }
        });

        return CurrentState.Apps.FirstOrDefault(a => a.Id.Equals(resolvedId, StringComparison.OrdinalIgnoreCase))?.Title ?? resolvedId;
    }

    public async Task CloseCurrentAppAsync(CancellationToken ct = default)
    {
        var currentId = CurrentState.ForegroundAppId;
        if (string.IsNullOrWhiteSpace(currentId))
        {
            return;
        }

        if (_demoMode)
        {
            await LaunchAppAsync("com.webos.app.livetv", null, ct);
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync("ssap://com.webos.applicationManager/closeByAppId", new { id = currentId }, ct);
        }
        catch
        {
            await SendRequestCoreAsync("ssap://system.launcher/close", new { id = currentId }, ct);
        }
    }

    public async Task SwitchInputAsync(string inputIdOrAppId, CancellationToken ct = default)
    {
        var matched = CurrentState.Inputs.FirstOrDefault(i =>
            i.Id.Equals(inputIdOrAppId, StringComparison.OrdinalIgnoreCase) ||
            i.AppId.Equals(inputIdOrAppId, StringComparison.OrdinalIgnoreCase));

        var appId = matched?.AppId ??
                    (inputIdOrAppId.StartsWith("com.webos.", StringComparison.OrdinalIgnoreCase)
                        ? inputIdOrAppId
                        : inputIdOrAppId.ToUpperInvariant() switch
                        {
                            "HDMI_1" => "com.webos.app.hdmi1",
                            "HDMI_2" => "com.webos.app.hdmi2",
                            "HDMI_3" => "com.webos.app.hdmi3",
                            "HDMI_4" => "com.webos.app.hdmi4",
                            _ => inputIdOrAppId,
                        });

        var inputId = matched?.Id ??
                      appId.ToLowerInvariant() switch
                      {
                          "com.webos.app.hdmi1" => "HDMI_1",
                          "com.webos.app.hdmi2" => "HDMI_2",
                          "com.webos.app.hdmi3" => "HDMI_3",
                          "com.webos.app.hdmi4" => "HDMI_4",
                          _ => inputIdOrAppId,
                      };

        if (_demoMode)
        {
            UpdateState(s => s with
            {
                IsScreenOff = false,
                ForegroundAppId = appId,
                ForegroundAppTitle = matched?.Label ?? inputId.Replace('_', ' '),
                Inputs = MarkActiveInput(s.Inputs, appId),
                Apps = MarkActiveApp(s.Apps, appId),
            });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: string.IsNullOrWhiteSpace(_configuredClientKey), forcePairing: false, ct: ct);

        // Modern webOS treats HDMI inputs as launchable apps; fall back to legacy tv/switchInput if needed.
        try
        {
            await SendRequestCoreAsync("ssap://com.webos.applicationManager/launch", new { id = appId }, ct);
        }
        catch
        {
            try
            {
                await SendRequestCoreAsync("ssap://tv/switchInput", new { inputId }, ct);
            }
            catch
            {
                await SendRequestCoreAsync("ssap://system.launcher/launch", new { id = appId }, ct);
            }
        }
    }

    public async Task PowerOffAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with { PowerState = "Standby", IsScreenOff = true, ForegroundAppTitle = "Standby" });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync("ssap://system/turnOff", new { }, ct);
        }
        catch
        {
            await SendRequestCoreAsync("ssap://system/power/off", new { }, ct);
        }
    }

    public async Task TurnOffScreenAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with { IsScreenOff = true, PowerState = "Screen Off" });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync("ssap://com.webos.service.tvpower/power/turnOffScreen", new { standbyMode = "active" }, ct);
        }
        catch
        {
            await SendRequestCoreAsync("ssap://com.webos.service.tv.power/turnOffScreen", new { standbyMode = "active" }, ct);
        }

        UpdateState(s => s with { IsScreenOff = true, PowerState = "Screen Off" });
    }

    public async Task TurnOnScreenAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with { IsScreenOff = false, PowerState = "Active" });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync("ssap://com.webos.service.tvpower/power/turnOnScreen", new { standbyMode = "active" }, ct);
        }
        catch
        {
            await SendRequestCoreAsync("ssap://com.webos.service.tv.power/turnOnScreen", new { standbyMode = "active" }, ct);
        }

        UpdateState(s => s with { IsScreenOff = false, PowerState = "Active" });
    }

    public async Task SetSoundOutputAsync(string output, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with
            {
                SoundOutput = output,
                SoundOutputLabel = LgTvLiveState.FormatSoundOutput(output),
            });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync("ssap://com.webos.service.apiadapter/audio/changeSoundOutput", new { output }, ct);
        }
        catch
        {
            await SendRequestCoreAsync("ssap://audio/changeSoundOutput", new { output }, ct);
        }

        UpdateState(s => s with
        {
            SoundOutput = output,
            SoundOutputLabel = LgTvLiveState.FormatSoundOutput(output),
        });
    }

    public async Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        if (_demoMode)
        {
            UpdateState(s => s with { Volume = clamped, IsMuted = false });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync("ssap://audio/setVolume", new { volume = clamped }, ct);
        UpdateState(s => s with { Volume = clamped });
    }

    public async Task StepVolumeAsync(bool up, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with { Volume = Math.Clamp(s.Volume + (up ? 2 : -2), 0, 100), IsMuted = false });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync(up ? "ssap://audio/volumeUp" : "ssap://audio/volumeDown", new { }, ct);
    }

    public async Task SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            UpdateState(s => s with { IsMuted = mute });
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync("ssap://audio/setMute", new { mute }, ct);
        UpdateState(s => s with { IsMuted = mute });
    }

    public async Task SendToastAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_demoMode)
        {
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync("ssap://system.notifications/createToast", new { message = message.Trim() }, ct);
    }

    public async Task SendMediaControlAsync(string action, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        var normalized = action.ToLowerInvariant();
        var uri = normalized switch
        {
            "play" => "ssap://media.controls/play",
            "pause" => "ssap://media.controls/pause",
            "stop" => "ssap://media.controls/stop",
            "rewind" => "ssap://media.controls/rewind",
            "fastforward" or "ff" => "ssap://media.controls/fastForward",
            _ => string.Empty,
        };

        if (string.IsNullOrEmpty(uri))
        {
            await SendButtonAsync(action.ToUpperInvariant(), ct);
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        try
        {
            await SendRequestCoreAsync(uri, new { }, ct);
        }
        catch
        {
            // Fallback to pointer button for apps like YouTube that prefer HID key events.
            var btn = normalized switch
            {
                "fastforward" or "ff" => "FASTFORWARD",
                _ => normalized.ToUpperInvariant(),
            };
            await SendButtonAsync(btn, ct);
        }
    }

    /// <summary>
    /// Returns the estimated Magic Remote cursor position normalized to 0..1000 x 0..1000.
    /// </summary>
    public (int X, int Y, bool Calibrated) GetNormalizedCursor()
    {
        lock (_stateLock)
        {
            var nx = (int)Math.Round(_cursorPxX * 1000.0 / LogicalScreenWidth);
            var ny = (int)Math.Round(_cursorPxY * 1000.0 / LogicalScreenHeight);
            return (Math.Clamp(nx, 0, 1000), Math.Clamp(ny, 0, 1000), _cursorCalibrated);
        }
    }

    /// <summary>
    /// Opens a URL directly in the LG webOS built-in Web Browser.
    /// </summary>
    public async Task<string> OpenBrowserUrlAsync(string url, CancellationToken ct = default)
    {
        var normalized = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("URL is required.", nameof(url));
        }

        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        if (_demoMode)
        {
            UpdateState(s => s with
            {
                IsScreenOff = false,
                ForegroundAppId = "com.webos.app.browser",
                ForegroundAppTitle = "Web Browser",
                Apps = MarkActiveApp(s.Apps, "com.webos.app.browser"),
            });
            return normalized;
        }

        await EnsureConnectedAsync(allowInteractivePairing: string.IsNullOrWhiteSpace(_configuredClientKey), forcePairing: false, ct: ct);

        try
        {
            await SendRequestCoreAsync("ssap://system.launcher/open", new { target = normalized }, ct, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiagnosticLog?.Invoke($"system.launcher/open fallback ({ex.Message}); trying system.launcher/launch.");
            await SendRequestCoreAsync(
                "ssap://system.launcher/launch",
                new { id = "com.webos.app.browser", target = normalized },
                ct,
                TimeSpan.FromSeconds(10));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, CancellationToken.None);
                await QueryForegroundAppOnceAsync(CancellationToken.None);
            }
            catch
            {
            }
        });

        return normalized;
    }

    /// <summary>
    /// Sends a Magic Remote button press over LG's secondary pointer input WebSocket
    /// (e.g., UP, DOWN, LEFT, RIGHT, ENTER, BACK, HOME, EXIT, MENU, QMENU, INFO, GUIDE, CHANNELUP, CHANNELDOWN).
    /// </summary>
    public async Task SendButtonAsync(string buttonName, CancellationToken ct = default)
    {
        var cleanBtn = buttonName.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(cleanBtn))
        {
            return;
        }

        if (_demoMode)
        {
            if (cleanBtn == "HOME")
            {
                UpdateState(s => s with { ForegroundAppId = "com.webos.app.home", ForegroundAppTitle = "webOS Home" });
            }
            return;
        }

        await SendPointerFrameAsync($"type:button\nname:{cleanBtn}\n\n", ct);
    }

    /// <summary>
    /// Moves the LG Magic Remote pointer by relative (dx, dy) deltas, chunking larger movements
    /// into smooth small steps to minimize webOS pointer acceleration distortion.
    /// </summary>
    public async Task MovePointerRelativeAsync(int dx, int dy, bool drag = false, CancellationToken ct = default)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        if (_demoMode)
        {
            lock (_stateLock)
            {
                _cursorPxX = Math.Clamp(_cursorPxX + dx, 0, LogicalScreenWidth);
                _cursorPxY = Math.Clamp(_cursorPxY + dy, 0, LogicalScreenHeight);
            }
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await _pointerGate.WaitAsync(ct);
        try
        {
            await EnsurePointerSocketLockedAsync(ct);
            await SendChunkedMoveLockedAsync(dx, dy, drag, maxStepPx: 50, delayMs: 12, ct);
        }
        finally
        {
            _pointerGate.Release();
        }

        lock (_stateLock)
        {
            _cursorPxX = Math.Clamp(_cursorPxX + dx, 0, LogicalScreenWidth);
            _cursorPxY = Math.Clamp(_cursorPxY + dy, 0, LogicalScreenHeight);
        }
    }

    /// <summary>
    /// Moves the LG Magic Remote pointer to a normalized (0..1000, 0..1000) coordinate on the TV screen.
    /// Homes the cursor to top-left (0, 0) on first use or when recalibrate=true, then steps smoothly.
    /// </summary>
    public async Task MovePointerToNormalizedAsync(int normX, int normY, bool recalibrate = false, CancellationToken ct = default)
    {
        var clampedX = Math.Clamp(normX, 0, 1000);
        var clampedY = Math.Clamp(normY, 0, 1000);
        var targetPxX = (int)Math.Round(clampedX * LogicalScreenWidth / 1000.0);
        var targetPxY = (int)Math.Round(clampedY * LogicalScreenHeight / 1000.0);

        if (_demoMode)
        {
            lock (_stateLock)
            {
                _cursorPxX = targetPxX;
                _cursorPxY = targetPxY;
                _cursorCalibrated = true;
            }
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await _pointerGate.WaitAsync(ct);
        try
        {
            await EnsurePointerSocketLockedAsync(ct);

            bool needsHome;
            int startX;
            int startY;
            lock (_stateLock)
            {
                needsHome = recalibrate || !_cursorCalibrated;
                startX = _cursorPxX;
                startY = _cursorPxY;
            }

            if (needsHome)
            {
                // Slam smoothly into the top-left corner (0, 0) using moderate steps to stay predictable.
                for (var i = 0; i < 16; i++)
                {
                    await SendPointerFrameLockedAsync("type:move\ndx:-100\ndy:-60\ndown:0\n\n", ct);
                    await Task.Delay(10, ct);
                }

                startX = 0;
                startY = 0;
                lock (_stateLock)
                {
                    _cursorPxX = 0;
                    _cursorPxY = 0;
                    _cursorCalibrated = true;
                }
            }

            var dx = targetPxX - startX;
            var dy = targetPxY - startY;
            if (dx != 0 || dy != 0)
            {
                await SendChunkedMoveLockedAsync(dx, dy, drag: false, maxStepPx: 24, delayMs: 10, ct);
            }

            lock (_stateLock)
            {
                _cursorPxX = targetPxX;
                _cursorPxY = targetPxY;
                _cursorCalibrated = true;
            }
        }
        finally
        {
            _pointerGate.Release();
        }
    }

    public async Task ClickPointerAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        await SendPointerFrameAsync("type:click\n\n", ct);
    }

    public async Task ScrollPointerAsync(int dx, int dy, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        await SendPointerFrameAsync($"type:scroll\ndx:{dx}\ndy:{dy}\n\n", ct);
    }

    /// <summary>
    /// Wakes the LG Magic Remote on-screen cursor with a tiny +/-2px micro-wiggle so it renders in screenshots.
    /// </summary>
    public async Task WakePointerCursorAsync(CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await _pointerGate.WaitAsync(ct);
        try
        {
            await EnsurePointerSocketLockedAsync(ct);
            await SendPointerFrameLockedAsync("type:move\ndx:2\ndy:0\ndown:0\n\n", ct);
            await Task.Delay(35, ct);
            await SendPointerFrameLockedAsync("type:move\ndx:-2\ndy:0\ndown:0\n\n", ct);
        }
        catch
        {
            // Non-fatal if pointer socket isn't ready yet.
        }
        finally
        {
            _pointerGate.Release();
        }
    }

    /// <summary>
    /// Captures a live screenshot of the LG webOS TV screen via ssap://tv/executeOneShot,
    /// downloads the returned imageUri over HTTPS/HTTP, and returns base64-encoded JPEG data.
    /// </summary>
    public async Task<LgTvScreenshotResult> CaptureScreenshotAsync(
        int width = 960,
        int height = 540,
        bool showCursor = true,
        CancellationToken ct = default)
    {
        var w = Math.Clamp(width, 320, 1920);
        var h = Math.Clamp(height, 180, 1080);

        if (_demoMode)
        {
            return new LgTvScreenshotResult(false, null, "image/jpeg", w, h, 0, null, "Screenshots are unavailable in Demo Mode.");
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);

        if (showCursor)
        {
            await WakePointerCursorAsync(ct);
            await Task.Delay(120, ct);
        }

        await _captureGate.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCaptureUtc;
            if (elapsed < TimeSpan.FromMilliseconds(850))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(850) - elapsed, ct);
            }

            JsonElement resp = default;
            Exception? lastEx = null;

            var attempts = new (string Uri, object Payload)[]
            {
                ("ssap://tv/executeOneShot", new { path = "/tmp/denon_cap.jpg", method = "DISPLAY", format = "JPG", width = w, height = h }),
                ("ssap://tv/executeOneShot", new { }),
                ("ssap://com.webos.service.capture/executeOneShot", new { path = "/tmp/denon_cap.jpg", method = "DISPLAY", format = "JPG", width = w, height = h }),
            };

            string? imageUri = null;
            foreach (var (uri, payload) in attempts)
            {
                try
                {
                    resp = await SendRequestCoreAsync(uri, payload, ct, TimeSpan.FromSeconds(8));
                    if (resp.ValueKind == JsonValueKind.Object &&
                        resp.TryGetProperty("imageUri", out var uriEl) &&
                        uriEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(uriEl.GetString()))
                    {
                        imageUri = uriEl.GetString();
                        break;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastEx = ex;
                }
            }

            _lastCaptureUtc = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(imageUri))
            {
                return new LgTvScreenshotResult(
                    false,
                    null,
                    "image/jpeg",
                    w,
                    h,
                    0,
                    null,
                    $"TV firmware did not return an imageUri for executeOneShot. {lastEx?.Message}".Trim());
            }

            var bytes = await SnapshotHttpClient.GetByteArrayAsync(imageUri, ct);
            var mime = imageUri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            return new LgTvScreenshotResult(
                true,
                Convert.ToBase64String(bytes),
                mime,
                w,
                h,
                bytes.Length,
                imageUri,
                null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new LgTvScreenshotResult(false, null, "image/jpeg", w, h, 0, null, $"Screenshot capture failed: {ex.Message}");
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task SendPointerFrameAsync(string frame, CancellationToken ct)
    {
        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await _pointerGate.WaitAsync(ct);
        try
        {
            await EnsurePointerSocketLockedAsync(ct);
            await SendPointerFrameLockedAsync(frame, ct);
        }
        finally
        {
            _pointerGate.Release();
        }
    }

    private async Task EnsurePointerSocketLockedAsync(CancellationToken ct)
    {
        if (_pointerWs is null || _pointerWs.State != WebSocketState.Open)
        {
            await OpenPointerSocketCoreAsync(ct);
        }
    }

    private async Task SendPointerFrameLockedAsync(string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        try
        {
            await _pointerWs!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch
        {
            await OpenPointerSocketCoreAsync(ct);
            await _pointerWs!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task SendChunkedMoveLockedAsync(int dx, int dy, bool drag, int maxStepPx, int delayMs, CancellationToken ct)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / (double)Math.Max(10, maxStepPx)));
        var downVal = drag ? 1 : 0;
        var sentX = 0;
        var sentY = 0;

        for (var i = 1; i <= steps; i++)
        {
            var nextX = (int)Math.Round(dx * (i / (double)steps));
            var nextY = (int)Math.Round(dy * (i / (double)steps));
            var stepDx = nextX - sentX;
            var stepDy = nextY - sentY;
            sentX = nextX;
            sentY = nextY;

            if (stepDx != 0 || stepDy != 0)
            {
                await SendPointerFrameLockedAsync($"type:move\ndx:{stepDx}\ndy:{stepDy}\ndown:{downVal}\n\n", ct);
                if (i < steps && delayMs > 0)
                {
                    await Task.Delay(delayMs, ct);
                }
            }
        }
    }

    public async Task SendKeyboardTextAsync(string text, bool pressEnter = true, bool replace = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || _demoMode)
        {
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync("ssap://com.webos.service.ime/insertText", new { text, replace }, ct);
        if (pressEnter)
        {
            await Task.Delay(120, ct);
            await SendRequestCoreAsync("ssap://com.webos.service.ime/sendEnterKey", new { }, ct);
        }
    }

    public async Task DeleteKeyboardCharactersAsync(int count = 1, CancellationToken ct = default)
    {
        if (_demoMode)
        {
            return;
        }

        await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
        await SendRequestCoreAsync("ssap://com.webos.service.ime/deleteCharacters", new { count = Math.Clamp(count, 1, 50) }, ct);
    }

    public async Task SendWakeOnLanAsync(string macAddress, CancellationToken ct = default)
    {
        var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            throw new InvalidOperationException("Enter a valid 12-hex MAC address (e.g. AA:BB:CC:DD:EE:FF) or click Auto-Discover / Auto-MAC.");
        }

        var macBytes = Convert.FromHexString(hex);
        var packet = new byte[6 + (16 * 6)];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var rep = 0; rep < 16; rep++)
        {
            Buffer.BlockCopy(macBytes, 0, packet, 6 + (rep * 6), 6);
        }

        // Send on every active local adapter to BOTH subnet broadcast & global broadcast on UDP ports 9 & 7.
        var endpoints = LgTvDiscoveryService.GetBroadcastEndpoints();
        if (endpoints.Count == 0)
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            await Task.Delay(100, ct);
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
            return;
        }

        foreach (var (localIp, broadcastIp) in endpoints)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(localIp, 0)) { EnableBroadcast = true };
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(broadcastIp, 9));
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                await Task.Delay(60, ct);
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(broadcastIp, 7));
            }
            catch
            {
                // Ignore non-routable adapters.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopBackgroundMonitorAsync();
        _connectGate.Dispose();
        _sendGate.Dispose();
        _pointerGate.Dispose();
        _captureGate.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var backoffSeconds = 3;
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (!_demoMode &&
                    !string.IsNullOrWhiteSpace(_configuredIp) &&
                    !string.IsNullOrWhiteSpace(_configuredClientKey) &&
                    (_ws is null || _ws.State != WebSocketState.Open))
                {
                    await EnsureConnectedAsync(allowInteractivePairing: false, forcePairing: false, ct: ct);
                    backoffSeconds = 3;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                backoffSeconds = Math.Min(backoffSeconds * 2, 20);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EnsureConnectedAsync(bool allowInteractivePairing, bool forcePairing, CancellationToken ct)
    {
        if (_ws?.State == WebSocketState.Open && !forcePairing)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuredIp))
        {
            throw new InvalidOperationException("Enter your Smart TV IP address first (or click Auto-Discover / Try Demo Mode).");
        }

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_ws?.State == WebSocketState.Open && !forcePairing)
            {
                return;
            }

            await DisconnectCoreLockedAsync();

            var (ws, transport) = await OpenWebSocketAsync(_configuredIp, ct);
            var key = await RegisterSessionCoreAsync(ws, allowInteractivePairing, forcePairing, ct);

            _ws = ws;
            _transportLabel = transport;
            _sessionCts = new CancellationTokenSource();

            if (!string.IsNullOrWhiteSpace(key) && !string.Equals(_configuredClientKey, key, StringComparison.Ordinal))
            {
                _configuredClientKey = key;
                ClientKeyUpdated?.Invoke(key);
            }

            var loopCt = _sessionCts.Token;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(ws, loopCt), loopCt);

            UpdateState(s => s with
            {
                IsConnected = true,
                IsDemo = false,
                TransportLabel = transport,
                LastError = null,
            });

            // Fire initial state hydration & live subscriptions on this persistent socket.
            await HydrateInitialStateAsync(ct);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<(ClientWebSocket Socket, string TransportLabel)> OpenWebSocketAsync(string host, CancellationToken ct)
    {
        var endpoints = new (Uri Uri, string Label)[]
        {
            (new Uri($"wss://{host}:3001/"), "WSS :3001"),
            (new Uri($"ws://{host}:3000/"), "WS :3000"),
        };

        Exception? lastError = null;
        foreach (var (uri, label) in endpoints)
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            {
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3.5));
                await ws.ConnectAsync(uri, cts.Token);
                return (ws, label);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                ws.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to LG webOS TV at {host} (tried wss://:3001 & ws://:3000). Make sure the TV is powered ON and on the same LAN. ({lastError?.Message})",
            lastError);
    }

    private async Task<string> RegisterSessionCoreAsync(ClientWebSocket ws, bool allowInteractivePairing, bool forcePairing, CancellationToken ct)
    {
        var existingKey = forcePairing ? string.Empty : _configuredClientKey;
        var payload = new Dictionary<string, object?>
        {
            ["forcePairing"] = forcePairing,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new
            {
                manifestVersion = 1,
                appVersion = "1.5.0",
                localizedAppNames = new Dictionary<string, string> { [""] = "Denon Desktop" },
                localizedVendorNames = new Dictionary<string, string> { [""] = "Denon Desktop" },
                permissions = RegistrationPermissions,
            },
        };

        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            payload["client-key"] = existingKey;
        }

        var regJson = JsonSerializer.Serialize(new
        {
            id = $"reg_{Interlocked.Increment(ref _msgId)}",
            type = "register",
            payload,
        });

        await SendRawTextAsync(ws, regJson, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(allowInteractivePairing || string.IsNullOrWhiteSpace(existingKey) ? 40 : 8));

        for (var i = 0; i < 8; i++)
        {
            var text = await ReceiveRawTextAsync(ws, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;

            if (string.Equals(type, "registered", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("payload", out var pEl) &&
                    pEl.TryGetProperty("client-key", out var ckEl) &&
                    !string.IsNullOrWhiteSpace(ckEl.GetString()))
                {
                    return ckEl.GetString()!;
                }

                return existingKey;
            }

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                var err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "LG TV rejected registration.";
                throw new InvalidOperationException(err ?? "LG TV rejected registration.");
            }

            // "response" with pairingType="PROMPT" means the TV is now showing the Accept dialog — keep waiting!
            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase) && !allowInteractivePairing && !string.IsNullOrWhiteSpace(existingKey))
            {
                DiagnosticLog?.Invoke("LG TV requested re-confirmation for stored client key.");
            }
        }

        throw new TimeoutException("Timed out waiting for LG TV pairing confirmation. Click Accept on your TV screen.");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var json = await ReceiveRawTextAsync(ws, ct);
                if (string.IsNullOrWhiteSpace(json))
                {
                    if (ws.State != WebSocketState.Open)
                    {
                        break;
                    }
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement.Clone();
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(id) && _pendingRequests.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(root);
                    }

                    if (!string.IsNullOrWhiteSpace(id) && _subscriptions.TryGetValue(id, out var handler))
                    {
                        var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                        if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase) &&
                            root.TryGetProperty("payload", out var payloadEl))
                        {
                            handler(payloadEl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog?.Invoke($"SSAP message parse warning: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke($"LG webOS socket disconnected: {ex.Message}");
        }
        finally
        {
            foreach (var kv in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(kv.Key, out var tcs))
                {
                    tcs.TrySetException(new IOException("LG webOS WebSocket connection closed."));
                }
            }

            if (ReferenceEquals(_ws, ws) && !_demoMode)
            {
                UpdateState(s => s with
                {
                    IsConnected = false,
                    IsPointerConnected = false,
                    TransportLabel = "STANDBY",
                });
            }
        }
    }

    private async Task<JsonElement> SendRequestCoreAsync(string uri, object? payload, CancellationToken ct, TimeSpan? timeout = null)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            throw new IOException("LG webOS TV is not connected.");
        }

        var id = $"req_{Interlocked.Increment(ref _msgId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var msg = JsonSerializer.Serialize(new
        {
            id,
            type = "request",
            uri,
            payload = payload ?? new { },
        });

        try
        {
            await SendRawTextAsync(ws, msg, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

            var resp = await tcs.Task;
            ValidateSsapResponse(uri, resp);
            return resp.TryGetProperty("payload", out var pEl) ? pEl : resp;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task SubscribeCoreAsync(string uri, Action<JsonElement> onUpdate, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        var id = $"sub_{Interlocked.Increment(ref _msgId)}";
        _subscriptions[id] = onUpdate;

        var msg = JsonSerializer.Serialize(new
        {
            id,
            type = "subscribe",
            uri,
            payload = new { },
        });

        try
        {
            await SendRawTextAsync(ws, msg, ct);
        }
        catch (Exception ex)
        {
            _subscriptions.TryRemove(id, out _);
            DiagnosticLog?.Invoke($"Subscribe failed ({uri}): {ex.Message}");
        }
    }

    private async Task HydrateInitialStateAsync(CancellationToken ct)
    {
        _subscriptions.Clear();

        // 1. Live push subscriptions (zero-lag updates whenever user uses their physical LG remote too!)
        await SubscribeCoreAsync("ssap://com.webos.applicationManager/getForegroundAppInfo", HandleForegroundUpdate, ct);
        await SubscribeCoreAsync("ssap://audio/getStatus", HandleAudioStatusUpdate, ct);
        await SubscribeCoreAsync("ssap://audio/getSoundOutput", HandleSoundOutputUpdate, ct);
        await SubscribeCoreAsync("ssap://com.webos.service.tvpower/power/getPowerState", HandlePowerStateUpdate, ct);
        await SubscribeCoreAsync("ssap://tv/getExternalInputList", HandleExternalInputsUpdate, ct);
        await SubscribeCoreAsync("ssap://tv/getCurrentChannel", HandleCurrentChannelUpdate, ct);

        // 2. One-shot metadata & installed apps discovery (isolated so 401 on any optional endpoint never breaks others).
        await TryQueryAsync("ssap://system/getSystemInfo", HandleSystemInfo, ct);
        await TryQueryAsync("ssap://com.webos.service.update/getCurrentSWInformation", HandleSwInfo, ct);
        await TryQueryAsync("ssap://com.webos.applicationManager/listLaunchPoints", HandleLaunchPoints, ct);
    }

    private async Task QueryForegroundAppOnceAsync(CancellationToken ct)
    {
        await TryQueryAsync("ssap://com.webos.applicationManager/getForegroundAppInfo", HandleForegroundUpdate, ct);
    }

    private async Task TryQueryAsync(string uri, Action<JsonElement> handler, CancellationToken ct)
    {
        try
        {
            var payload = await SendRequestCoreAsync(uri, new { }, ct, TimeSpan.FromSeconds(5));
            handler(payload);
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke($"Optional TV query {uri}: {ex.Message}");
        }
    }

    private void HandleSystemInfo(JsonElement payload)
    {
        var model = GetStringProp(payload, "modelName");
        if (!string.IsNullOrWhiteSpace(model))
        {
            UpdateState(s => s with { ModelName = model });
        }
    }

    private void HandleSwInfo(JsonElement payload)
    {
        var productName = GetStringProp(payload, "product_name");
        var major = GetStringProp(payload, "major_ver");
        var minor = GetStringProp(payload, "minor_ver");
        var model = GetStringProp(payload, "model_name");
        var ver = !string.IsNullOrWhiteSpace(productName)
            ? productName
            : (!string.IsNullOrWhiteSpace(major) ? $"FW {major}.{minor}" : string.Empty);

        UpdateState(s => s with
        {
            ModelName = string.IsNullOrWhiteSpace(s.ModelName) ? model : s.ModelName,
            WebOsVersion = string.IsNullOrWhiteSpace(ver) ? s.WebOsVersion : ver,
        });
    }

    private void HandleForegroundUpdate(JsonElement payload)
    {
        var appId = GetStringProp(payload, "appId");
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        UpdateState(s =>
        {
            var title =
                s.Inputs.FirstOrDefault(i => i.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase))?.Label ??
                s.Apps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase))?.Title ??
                FormatFriendlyAppId(appId);

            return s with
            {
                ForegroundAppId = appId,
                ForegroundAppTitle = title,
                Apps = MarkActiveApp(s.Apps, appId),
                Inputs = MarkActiveInput(s.Inputs, appId),
            };
        });
    }

    private void HandleAudioStatusUpdate(JsonElement payload)
    {
        int? volume = null;
        if (payload.TryGetProperty("volume", out var vEl) && vEl.ValueKind == JsonValueKind.Number)
        {
            volume = vEl.GetInt32();
        }
        else if (payload.TryGetProperty("volumeStatus", out var vsEl) &&
                 vsEl.TryGetProperty("volume", out var v2) && v2.ValueKind == JsonValueKind.Number)
        {
            volume = v2.GetInt32();
        }

        bool? muted = null;
        if (payload.TryGetProperty("mute", out var mEl) && (mEl.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            muted = mEl.GetBoolean();
        }
        else if (payload.TryGetProperty("volumeStatus", out var vs2) &&
                 vs2.TryGetProperty("muteStatus", out var msEl) && (msEl.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            muted = msEl.GetBoolean();
        }

        var output = GetStringProp(payload, "soundOutput");
        if (string.IsNullOrWhiteSpace(output) && payload.TryGetProperty("volumeStatus", out var vs3))
        {
            output = GetStringProp(vs3, "soundOutput");
        }

        UpdateState(s => s with
        {
            Volume = volume ?? s.Volume,
            IsMuted = muted ?? s.IsMuted,
            SoundOutput = string.IsNullOrWhiteSpace(output) ? s.SoundOutput : output,
            SoundOutputLabel = string.IsNullOrWhiteSpace(output) ? s.SoundOutputLabel : LgTvLiveState.FormatSoundOutput(output),
        });
    }

    private void HandleSoundOutputUpdate(JsonElement payload)
    {
        var output = GetStringProp(payload, "soundOutput");
        if (!string.IsNullOrWhiteSpace(output))
        {
            UpdateState(s => s with
            {
                SoundOutput = output,
                SoundOutputLabel = LgTvLiveState.FormatSoundOutput(output),
            });
        }
    }

    private void HandlePowerStateUpdate(JsonElement payload)
    {
        var state = GetStringProp(payload, "state");
        if (string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        var screenOff = state.Contains("Screen Off", StringComparison.OrdinalIgnoreCase) ||
                        state.Contains("Standby", StringComparison.OrdinalIgnoreCase);

        UpdateState(s => s with
        {
            PowerState = state,
            IsScreenOff = screenOff,
        });
    }

    private void HandleCurrentChannelUpdate(JsonElement payload)
    {
        var num = GetStringProp(payload, "channelNumber");
        var name = GetStringProp(payload, "channelName");
        if (!string.IsNullOrWhiteSpace(num) || !string.IsNullOrWhiteSpace(name))
        {
            UpdateState(s => s with
            {
                CurrentChannelNumber = num,
                CurrentChannelName = name,
            });
        }
    }

    private void HandleExternalInputsUpdate(JsonElement payload)
    {
        if (!payload.TryGetProperty("devices", out var devsEl) || devsEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var inputs = new List<LgTvInputInfo>();
        foreach (var d in devsEl.EnumerateArray())
        {
            var id = GetStringProp(d, "id");
            var appId = GetStringProp(d, "appId");
            var label = GetStringProp(d, "label");
            var connected = d.TryGetProperty("connected", out var cEl) && cEl.ValueKind == JsonValueKind.True;
            var subLabel = GetStringProp(d, "subList") is { Length: > 0 } sl ? sl : (connected ? "Signal Ready" : "Idle");

            if (string.IsNullOrWhiteSpace(appId))
            {
                continue;
            }

            inputs.Add(new LgTvInputInfo(
                Id: string.IsNullOrWhiteSpace(id) ? appId : id,
                AppId: appId,
                Label: string.IsNullOrWhiteSpace(label) ? id.Replace('_', ' ') : label,
                IsConnected: connected,
                IsActive: appId.Equals(CurrentState.ForegroundAppId, StringComparison.OrdinalIgnoreCase),
                SubLabel: subLabel));
        }

        if (inputs.Count > 0)
        {
            UpdateState(s => s with
            {
                Inputs = MarkActiveInput(inputs, s.ForegroundAppId),
            });
        }
    }

    private void HandleLaunchPoints(JsonElement payload)
    {
        if (!payload.TryGetProperty("launchPoints", out var lpEl) || lpEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<LgTvAppInfo>();

        foreach (var item in lpEl.EnumerateArray())
        {
            var id = GetStringProp(item, "id");
            var title = GetStringProp(item, "title");
            var vendor = GetStringProp(item, "vendor");
            var isSystem = item.TryGetProperty("systemApp", out var sysEl) && sysEl.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            // Skip raw HDMI entries (those live in the dedicated HDMI Inputs bar).
            if (id.StartsWith("com.webos.app.hdmi", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var displayTitle = string.IsNullOrWhiteSpace(title) ? FormatFriendlyAppId(id) : title.Trim();
            apps.Add(new LgTvAppInfo(
                Id: id,
                Title: displayTitle,
                Vendor: vendor,
                IsSystemApp: isSystem,
                IsActive: id.Equals(CurrentState.ForegroundAppId, StringComparison.OrdinalIgnoreCase),
                BadgeText: BuildAppBadge(displayTitle, id)));
        }

        var ordered = apps
            .OrderBy(GetAppSortRank)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdateState(s => s with
        {
            Apps = MarkActiveApp(ordered, s.ForegroundAppId),
            ForegroundAppTitle = ordered.FirstOrDefault(a => a.Id.Equals(s.ForegroundAppId, StringComparison.OrdinalIgnoreCase))?.Title ?? s.ForegroundAppTitle,
        });
    }

    private async Task OpenPointerSocketCoreAsync(CancellationToken ct)
    {
        if (_pointerWs is not null)
        {
            try { _pointerWs.Dispose(); } catch { }
            _pointerWs = null;
        }

        var payload = await SendRequestCoreAsync("ssap://com.webos.service.networkinput/getPointerInputSocket", new { }, ct);
        var socketPath = GetStringProp(payload, "socketPath");
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new InvalidOperationException("LG TV did not return a pointer input socketPath. Try clicking 'Re-Pair (Full Permissions)'.");
        }

        var pws = new ClientWebSocket();
        pws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (socketPath.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            pws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        await pws.ConnectAsync(new Uri(socketPath), cts.Token);
        _pointerWs = pws;

        UpdateState(s => s with { IsPointerConnected = true });
    }

    private string ResolveInstalledAppId(string appIdOrAlias)
    {
        var token = (appIdOrAlias ?? string.Empty).Trim();
        var apps = CurrentState.Apps;

        // Exact ID match first.
        var exact = apps.FirstOrDefault(a => a.Id.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Id;
        }

        // Smart alias resolution against real installed launchPoints on this exact TV.
        var lower = token.ToLowerInvariant();
        var keyword = lower switch
        {
            "youtube" or "youtube.leanback.v4" => "youtube",
            "netflix" => "netflix",
            "plex" or "cdp-30" => "plex",
            "spotify" or "spotify-beehive" => "spotify",
            "disney" or "disney+" or "com.disney.disneyplus-prod" => "disney",
            "prime" or "amazon" => "amazon",
            "appletv" or "com.apple.appletv" => "apple",
            "livetv" or "com.webos.app.livetv" => "livetv",
            "browser" or "web" or "webbrowser" or "com.webos.app.browser" => "browser",
            "settings" or "setting" or "indstillinger" or "quicksettings" or "systemsettings" or "com.palm.app.settings" => "com.palm.app.settings",
            _ => lower,
        };

        // Prefer matching by app ID or title (avoiding YouTube Kids / YouTube Music when user asked for YouTube).
        var match = apps.FirstOrDefault(a =>
            (a.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
             a.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
            !(keyword == "youtube" && (a.Title.Contains("Kids", StringComparison.OrdinalIgnoreCase) || a.Id.Contains("kids", StringComparison.OrdinalIgnoreCase))));

        if (match is not null)
        {
            return match.Id;
        }

        return lower switch
        {
            "youtube" => "youtube.leanback.v4",
            "netflix" => "netflix",
            "plex" => "cdp-30",
            "spotify" => "spotify-beehive",
            "disney" or "disney+" => "com.disney.disneyplus-prod",
            "prime" or "amazon" => "amazon",
            "appletv" => "com.apple.appletv",
            "livetv" => "com.webos.app.livetv",
            "browser" or "web" or "webbrowser" => "com.webos.app.browser",
            "settings" or "setting" or "indstillinger" or "quicksettings" or "systemsettings" => "com.palm.app.settings",
            _ => token,
        };
    }

    public static string ExtractYouTubeVideoId(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("v=", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        var match = YouTubeIdRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    private static string NormalizeYouTubeContentId(string appId, string rawContentId)
    {
        if (!appId.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return rawContentId.Trim();
        }

        var videoId = ExtractYouTubeVideoId(rawContentId);
        return string.IsNullOrWhiteSpace(videoId) ? rawContentId.Trim() : $"v={videoId}";
    }

    private async Task DisconnectInternalAsync()
    {
        await _connectGate.WaitAsync();
        try
        {
            await DisconnectCoreLockedAsync();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DisconnectCoreLockedAsync()
    {
        _subscriptions.Clear();

        if (_sessionCts is not null)
        {
            _sessionCts.Cancel();
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        if (_pointerWs is not null)
        {
            try
            {
                if (_pointerWs.State == WebSocketState.Open)
                {
                    await _pointerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch { }
            _pointerWs.Dispose();
            _pointerWs = null;
        }

        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch { }
            _ws.Dispose();
            _ws = null;
        }
    }

    private async Task SendRawTextAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task<string> ReceiveRawTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            ms.Write(buf, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void ValidateSsapResponse(string uri, JsonElement root)
    {
        var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            var err = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "TV returned an SSAP error.";
            throw new InvalidOperationException($"{err} ({uri})");
        }

        if (root.TryGetProperty("payload", out var payloadEl) &&
            payloadEl.ValueKind == JsonValueKind.Object &&
            payloadEl.TryGetProperty("returnValue", out var rvEl) &&
            rvEl.ValueKind == JsonValueKind.False)
        {
            var errText = GetStringProp(payloadEl, "errorText");
            var errCode = payloadEl.TryGetProperty("errorCode", out var ecEl) ? ecEl.ToString() : string.Empty;
            var detail = !string.IsNullOrWhiteSpace(errText) ? errText : $"returnValue=false (code {errCode})";
            throw new InvalidOperationException($"LG TV rejected command: {detail}");
        }
    }

    private void InitializeDemoState()
    {
        var demoInputs = new List<LgTvInputInfo>
        {
            new("HDMI_1", "com.webos.app.hdmi1", "HDMI 1 • Apple TV 4K", true, false, "Signal Ready"),
            new("HDMI_2", "com.webos.app.hdmi2", "HDMI 2 • Denon AVR (eARC)", true, true, "eARC Active"),
            new("HDMI_3", "com.webos.app.hdmi3", "HDMI 3 • PlayStation 5", true, false, "4K 120Hz"),
            new("HDMI_4", "com.webos.app.hdmi4", "HDMI 4", false, false, "Idle"),
        };

        var demoApps = new List<LgTvAppInfo>
        {
            new("youtube.leanback.v4", "YouTube", "Google", false, false, "YT"),
            new("netflix", "Netflix", "Netflix", false, false, "NF"),
            new("cdp-30", "Plex", "Plex", false, false, "PX"),
            new("com.disney.disneyplus-prod", "Disney+", "Disney", false, false, "D+"),
            new("spotify-beehive", "Spotify", "Spotify", false, false, "SP"),
            new("amazon", "Prime Video", "Amazon", false, false, "PV"),
            new("com.apple.appletv", "Apple TV", "Apple", false, false, "TV"),
            new("com.webos.app.livetv", "Live TV", "LG", true, false, "TV"),
            new("com.webos.app.browser", "Web Browser", "LG", true, false, "WB"),
        };

        UpdateState(_ => new LgTvLiveState
        {
            IsConnected = true,
            IsDemo = true,
            IsPointerConnected = true,
            TransportLabel = "DEMO SIM",
            ModelName = "OLED65C4PUA",
            WebOsVersion = "webOS 24 (Demo)",
            PowerState = "Active",
            IsScreenOff = false,
            ForegroundAppId = "com.webos.app.hdmi2",
            ForegroundAppTitle = "HDMI 2 • Denon AVR (eARC)",
            Volume = 28,
            IsMuted = false,
            SoundOutput = "external_arc",
            SoundOutputLabel = "HDMI ARC / eARC",
            Inputs = demoInputs,
            Apps = demoApps,
        });
    }

    private void UpdateState(Func<LgTvLiveState, LgTvLiveState> mutator)
    {
        LgTvLiveState next;
        lock (_stateLock)
        {
            next = mutator(_state);
            _state = next;
        }

        StateChanged?.Invoke(next);
    }

    private static IReadOnlyList<LgTvAppInfo> MarkActiveApp(IReadOnlyList<LgTvAppInfo> apps, string foregroundId) =>
        apps.Select(a => a with { IsActive = a.Id.Equals(foregroundId, StringComparison.OrdinalIgnoreCase) }).ToList();

    private static IReadOnlyList<LgTvInputInfo> MarkActiveInput(IReadOnlyList<LgTvInputInfo> inputs, string foregroundId) =>
        inputs.Select(i => i with { IsActive = i.AppId.Equals(foregroundId, StringComparison.OrdinalIgnoreCase) }).ToList();

    private static int GetAppSortRank(LgTvAppInfo app)
    {
        var combined = $"{app.Id} {app.Title}".ToLowerInvariant();
        for (var i = 0; i < FavoriteAppPriority.Length; i++)
        {
            if (combined.Contains(FavoriteAppPriority[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return app.IsSystemApp ? 200 : 100;
    }

    private static string BuildAppBadge(string title, string id)
    {
        var lower = $"{id} {title}".ToLowerInvariant();
        if (lower.Contains("youtube")) return "YT";
        if (lower.Contains("netflix")) return "NF";
        if (lower.Contains("plex") || lower.Contains("cdp-30")) return "PX";
        if (lower.Contains("disney")) return "D+";
        if (lower.Contains("spotify")) return "SP";
        if (lower.Contains("amazon") || lower.Contains("prime")) return "PV";
        if (lower.Contains("apple")) return "AT";
        if (lower.Contains("livetv")) return "TV";
        if (lower.Contains("twitch")) return "TW";

        var letters = new string(title.Where(char.IsLetterOrDigit).ToArray());
        return letters.Length >= 2 ? letters[..2].ToUpperInvariant() : (letters.Length == 1 ? letters.ToUpperInvariant() : "AP");
    }

    private static string FormatFriendlyAppId(string appId) => appId.ToLowerInvariant() switch
    {
        "youtube.leanback.v4" => "YouTube",
        "netflix" => "Netflix",
        "cdp-30" => "Plex",
        "spotify-beehive" => "Spotify",
        "com.webos.app.livetv" => "Live TV",
        "com.webos.app.home" => "webOS Home",
        "com.webos.app.hdmi1" => "HDMI 1",
        "com.webos.app.hdmi2" => "HDMI 2",
        "com.webos.app.hdmi3" => "HDMI 3",
        "com.webos.app.hdmi4" => "HDMI 4",
        _ => appId,
    };

    private static string CleanHost(string raw) =>
        (raw ?? string.Empty).Trim()
            .Replace("wss://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

    private static string GetStringProp(JsonElement obj, string propName)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propName, out var el))
        {
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.ToString();
        }

        return string.Empty;
    }

    [GeneratedRegex(@"(?:v=|youtu\.be/|shorts/|live/|embed/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeIdRegex();
}

using System.ComponentModel;
using System.Threading;
using System.Windows;
using Denon.App.Core.Heos;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Mcp;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Denon.App.Desktop.Infrastructure.Theming;
using Denon.App.Desktop.Services;
using Denon.App.Desktop.ViewModels;
using Denon.App.PluginSdk;
using Denon.Plugin.HomeAssistant;
using Denon.Plugin.PhilipsHue;
using Denon.Plugin.SmartTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Desktop;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\DenonDesktop_SingleInstance_Mutex_v2";
    private const string SingleInstanceSignalName = "Local\\DenonDesktop_ActivateSignal_v2";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationSignal;
    private CancellationTokenSource? _activationListenerCts;
    private IHost? _host;
    private TrayIconController? _trayIconController;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                _host?.Services.GetService<AppLogStore>()?.Error("Unhandled UI exception.", args.Exception);
            }
            catch
            {
            }
        };

        var captureIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--capture-screenshots", StringComparison.OrdinalIgnoreCase));
        var captureDir = captureIndex >= 0 && captureIndex + 1 < e.Args.Length ? e.Args[captureIndex + 1] : null;
        var isScreenshotMode = !string.IsNullOrWhiteSpace(captureDir);

        if (!isScreenshotMode)
        {
            // Enforce single instance: if already running, signal the running instance to surface its window
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var existingSignal = EventWaitHandle.OpenExisting(SingleInstanceSignalName);
                    existingSignal.Set();
                }
                catch
                {
                    // Ignore if existing instance hasn't created signal yet
                }

                Shutdown();
                return;
            }
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (isScreenshotMode)
        {
            // Screenshots use default settings and no plugins from this PC: README images must not show
            // the user's own receiver, stations, lights, TV or IP addresses.
            var showcaseRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DenonDesktop-showcase-" + Environment.ProcessId);
            if (System.IO.Directory.Exists(showcaseRoot))
            {
                System.IO.Directory.Delete(showcaseRoot, recursive: true);
            }

            AppDataPaths.UseRootDirectory(showcaseRoot);
        }

        AppDataPaths.EnsureCreated();

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<AppLogStore>();
            builder.Services.AddSingleton<AppSettingsStore>();
            builder.Services.AddSingleton<EventTelemetryLog>();
            builder.Services.AddSingleton<ReceiverStateStore>();
            builder.Services.AddSingleton<HeosClient>();
            builder.Services.AddSingleton<DenonAvrClient>();
            builder.Services.AddSingleton<DenonDiscoveryService>();
            builder.Services.AddSingleton<StreamProbeService>();
            builder.Services.AddSingleton<WindowsStartupService>();
            builder.Services.AddSingleton<GlobalHotkeyService>();
            builder.Services.AddSingleton<ReceiverController>();
            builder.Services.AddSingleton<YouTubeMediaService>();
            builder.Services.AddSingleton<LocalAudioRelayServer>();
            builder.Services.AddSingleton<YouTubeQueueService>();
            builder.Services.AddSingleton<LocalMediaJukeboxService>();
            builder.Services.AddSingleton<AppStreamMeterService>();
            builder.Services.AddSingleton<RadioBrowserService>();
            builder.Services.AddSingleton<RemoteWebApiHandler>();
            builder.Services.AddSingleton<PcAudioCastService>();
            builder.Services.AddSingleton<HeosEventListenerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HeosEventListenerService>());
            builder.Services.AddSingleton<IDenonPlugin, PhilipsHuePlugin>();
            builder.Services.AddSingleton<IDenonPlugin, SmartTvPlugin>();
            builder.Services.AddSingleton<IDenonPlugin, HomeAssistantPlugin>();
            builder.Services.AddSingleton<PluginHostService>();
            builder.Services.AddSingleton<EventTelemetryMonitorService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<EventTelemetryMonitorService>());
            builder.Services.AddSingleton<RoomIntelligenceService>();
            builder.Services.AddSingleton<DenonMcpHttpServer>();
            builder.Services.AddSingleton<LyricsService>();
            builder.Services.AddSingleton<AcousticSyncCalibrationService>();
            builder.Services.AddSingleton<LiveAudioAnalyzerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveAudioAnalyzerService>());
            builder.Services.AddSingleton<AlbumArtPaletteService>();
            builder.Services.AddSingleton<AutomationEngineService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationEngineService>());
            builder.Services.AddSingleton<YouTubeCinemaWindow>();
            builder.Services.AddHostedService<ReceiverPollingService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<TrayFlyoutWindow>();
            builder.Services.AddSingleton<TrayIconController>();

            _host = builder.Build();

            var settingsStore = _host.Services.GetRequiredService<AppSettingsStore>();
            await settingsStore.InitializeAsync();

            // Skin and theme before the first window (docs/theming.md). Screenshots are Default and Dark unless
            // --skin and --theme say otherwise, so README images do not depend on the settings of the PC that makes them.
            var themeArg = ArgValue(e.Args, "--theme");
            var skinArg = ArgValue(e.Args, "--skin");
            ThemeManager.ApplySkin(skinArg ?? (isScreenshotMode ? ThemeManager.DefaultSkinId : settingsStore.Current.Ui.Skin));
            ThemeManager.ApplyTheme(themeArg ?? (isScreenshotMode ? "dark" : settingsStore.Current.Ui.Theme));

            var pluginHost = _host.Services.GetRequiredService<PluginHostService>();
            await pluginHost.InitializeAndStartAsync();

            var relayServer = _host.Services.GetRequiredService<LocalAudioRelayServer>();
            relayServer.AttachWebApiHandler(_host.Services.GetRequiredService<RemoteWebApiHandler>());
            _ = _host.Services.GetRequiredService<PcAudioCastService>(); // ensures live PC audio handler is attached

            if (!isScreenshotMode)
            {
                await _host.StartAsync();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await relayServer.EnsureStartedAsync();
                    }
                    catch (Exception ex)
                    {
                        _host.Services.GetRequiredService<AppLogStore>().Warning($"Local audio relay startup deferred: {ex.Message}");
                    }

                    try
                    {
                        var mcpServer = _host.Services.GetRequiredService<DenonMcpHttpServer>();
                        await mcpServer.ApplySettingsAsync();
                    }
                    catch (Exception ex)
                    {
                        _host.Services.GetRequiredService<AppLogStore>().Warning($"MCP HTTP server startup deferred: {ex.Message}");
                    }
                });

                _host.Services.GetRequiredService<AppLogStore>().Info("Denon Desktop started.");
            }

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.StateChanged += OnMainWindowStateChanged;

            if (isScreenshotMode)
            {
                var vm = _host.Services.GetRequiredService<MainViewModel>();
                vm.LoadShowcaseStateForScreenshots();
                window.Width = 1360;
                window.Height = 920;
                window.ShowActivated = false;
                window.Show();

                // --switch-theme <id> and --switch-skin <id>: switch live with the window open (tests the live path).
                if (ArgValue(e.Args, "--switch-theme") is { } switchTheme)
                {
                    await Task.Delay(400);
                    ThemeManager.ApplyTheme(switchTheme);
                }

                if (ArgValue(e.Args, "--switch-skin") is { } switchSkin)
                {
                    await Task.Delay(400);
                    ThemeManager.ApplySkin(switchSkin);
                }

                // --all-pages: also the pages the README does not show (checks a theme on every page).
                var allPages = e.Args.Any(a => string.Equals(a, "--all-pages", StringComparison.OrdinalIgnoreCase));
                await CaptureScreenshotsAsync(window, vm, captureDir!, allPages);
                await ExitApplicationAsync();
                return;
            }

            _trayIconController = _host.Services.GetRequiredService<TrayIconController>();
            _trayIconController.Initialize(window, ExitApplicationAsync);

            var hotkeyService = _host.Services.GetRequiredService<GlobalHotkeyService>();
            var mainVm = _host.Services.GetRequiredService<MainViewModel>();
            hotkeyService.HotkeyPressed += actionId =>
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    if (string.Equals(actionId, Denon.App.Core.Models.ShortcutActionIds.GlobalShowApp, StringComparison.OrdinalIgnoreCase))
                    {
                        if (window.IsVisible && window.IsActive && window.WindowState != WindowState.Minimized)
                        {
                            if (settingsStore.Current.Ui.MinimizeToTray)
                            {
                                window.Hide();
                            }
                            else
                            {
                                window.WindowState = WindowState.Minimized;
                            }
                        }
                        else
                        {
                            await _trayIconController.ShowWindowAsync();
                        }
                    }
                    else
                    {
                        mainVm.ExecuteShortcutAction(actionId);
                    }
                });
            };

            StartActivationSignalListener();

            var startMinimized = (settingsStore.Current.Ui.StartMinimizedToTray ||
                                  e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) &&
                                 !e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);

            if (startMinimized)
            {
                window.Hide();
            }
            else
            {
                window.Show();
                window.Activate();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Denon Desktop Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    /// <summary>The value after a command-line option (<c>--theme light</c>), or null.</summary>
    private static string? ArgValue(string[] args, string option)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task CaptureScreenshotsAsync(MainWindow window, MainViewModel vm, string outputDir, bool allPages)
    {
        System.IO.Directory.CreateDirectory(outputDir);

        List<(string Section, string? StationsTab, string FileName, PluginCardViewModel? Plugin)> shots =
        [
            ("Control", null, "01-control-deck.png", null),
            ("YouTube", null, "02-youtube-music.png", null),
            ("Acoustics", null, "03-scenes-acoustics.png", null),
            ("CastRemote", null, "04-cast-phone-remote.png", null),
            ("Stations", "Explorer", "05-global-radio-explorer.png", null),
            ("Settings", null, "06-settings-shortcuts.png", null),
        ];

        if (allPages)
        {
            shots.Add(("Stations", "Presets", "07-stations-presets.png", null));
            shots.Add(("Mcp", null, "08-brain.png", null));
            shots.Add(("Diagnostics", null, "09-health.png", null));
            shots.Add(("Help", null, "10-help.png", null));
            shots.Add(("Plugins", null, "11-plugins.png", null));
            shots.AddRange(vm.InstalledPlugins.Select((p, i) => ("Plugins", (string?)null, $"{12 + i}-plugin-{p.Id}.png", (PluginCardViewModel?)p)));
        }

        foreach (var (section, stationsTab, fileName, plugin) in shots)
        {
            vm.ActiveSection = section;
            if (plugin is not null)
            {
                vm.OpenPluginCommand.Execute(plugin);
            }

            if (!string.IsNullOrWhiteSpace(stationsTab))
            {
                vm.StationsSubTab = stationsTab;
            }

            window.UpdateLayout();
            await Task.Delay(320);
            window.UpdateLayout();

            var root = window.Content as System.Windows.FrameworkElement ?? window;
            var width = Math.Max(1, (int)Math.Round(root.ActualWidth));
            var height = Math.Max(1, (int)Math.Round(root.ActualHeight));

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

            await using var fs = System.IO.File.Create(System.IO.Path.Combine(outputDir, fileName));
            encoder.Save(fs);
        }
    }

    private void StartActivationSignalListener()
    {
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceSignalName);
        _activationListenerCts = new CancellationTokenSource();
        var token = _activationListenerCts.Token;

        _ = Task.Run(() =>
        {
            var handles = new[] { _activationSignal, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                var triggered = WaitHandle.WaitAny(handles);
                if (triggered == 0 && _trayIconController is not null)
                {
                    _ = _trayIconController.ShowWindowAsync();
                }
                else
                {
                    break;
                }
            }
        }, token);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListenerCts?.Cancel();
        _activationListenerCts?.Dispose();
        _activationSignal?.Dispose();
        _trayIconController?.Dispose();

        // Reached without ExitApplicationAsync when Windows ends the app (logoff, shutdown,
        // Restart Manager during an upgrade). Stop plugins and hosted services first, so open
        // habit sessions get their session.ended events, then dispose. Bounded: Windows gives ~5 s.
        var remainingHost = _host;
        _host = null;
        if (remainingHost is not null)
        {
            try
            {
                remainingHost.Services.GetRequiredService<AppLogStore>().Info("Application shutting down (Windows session ending).");
            }
            catch
            {
            }

            try
            {
                Task.Run(() => StopHostAsync(
                        remainingHost,
                        pluginTimeout: TimeSpan.FromSeconds(1.5),
                        stopTimeout: TimeSpan.FromSeconds(1.5),
                        disposeTimeout: TimeSpan.FromSeconds(1.5)))
                    .Wait(TimeSpan.FromSeconds(4.5));
            }
            catch
            {
            }
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (_isExiting || sender is not MainWindow window || _host is null)
        {
            return;
        }

        var settings = _host.Services.GetRequiredService<AppSettingsStore>().Current;
        if (settings.Ui.MinimizeToTray && window.WindowState == WindowState.Minimized)
        {
            window.Hide();
            _trayIconController?.ShowBalloon("Denon Desktop", "Running in system tray. Click the tray icon for quick controls.");
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting || sender is not MainWindow window || _host is null)
        {
            return;
        }

        var settings = _host.Services.GetRequiredService<AppSettingsStore>().Current;
        e.Cancel = true;

        if (settings.Ui.CloseToTray)
        {
            window.Hide();
            _trayIconController?.ShowBalloon("Denon Desktop", "Minimized to tray. Click the tray icon to open quick controls.");
            return;
        }

        _ = ExitApplicationAsync();
    }

    /// <summary>
    /// Ordered, bounded host stop shared by both exit paths: plugins first (they write their
    /// own session.ended), then hosted services (telemetry flushes app_shutdown sessions), then disposal.
    /// Call it off the Dispatcher thread.
    /// </summary>
    private static async Task StopHostAsync(IHost host, TimeSpan pluginTimeout, TimeSpan stopTimeout, TimeSpan disposeTimeout)
    {
        try
        {
            var pluginHost = host.Services.GetService<PluginHostService>();
            if (pluginHost is not null)
            {
                await pluginHost.StopAllAsync().WaitAsync(pluginTimeout);
            }
        }
        catch
        {
        }

        try
        {
            await host.StopAsync(stopTimeout);
        }
        catch
        {
        }

        try
        {
            if (host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync().AsTask().WaitAsync(disposeTimeout);
            }
            else
            {
                host.Dispose();
            }
        }
        catch
        {
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        try
        {
            _activationListenerCts?.Cancel();
            _trayIconController?.Dispose();
            _trayIconController = null;

            var hostToStop = _host;
            _host = null;

            if (hostToStop is not null)
            {
                try
                {
                    hostToStop.Services.GetService<YouTubeCinemaWindow>()?.ForceCloseForExit();
                }
                catch
                {
                }

                try
                {
                    hostToStop.Services.GetRequiredService<AppLogStore>().Info("Application shutting down.");
                }
                catch
                {
                }

                // Run async stop + IAsyncDisposable host disposal OFF the WPF Dispatcher thread with a
                // bounded timeout so IAsyncDisposable singletons (MCP server, telemetry log, plugins)
                // can never deadlock against DispatcherSynchronizationContext!
                try
                {
                    await Task.Run(() => StopHostAsync(
                            hostToStop,
                            pluginTimeout: TimeSpan.FromSeconds(2),
                            stopTimeout: TimeSpan.FromSeconds(3),
                            disposeTimeout: TimeSpan.FromSeconds(3)))
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
        finally
        {
            Shutdown(0);
            // Safety net: if a WebView2 / COM thread lingers past WPF Shutdown, terminate cleanly after 1.5s.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Environment.Exit(0);
            });
        }
    }
}

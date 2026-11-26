using System.ComponentModel;
using System.Threading;
using System.Windows;
using Denon.App.Core.Heos;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Mcp;
using Denon.App.Core.Plugins;
using Denon.App.Core.Services;
using Denon.App.Core.Services.Metering;
using Denon.App.Desktop.Infrastructure;
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
    private WindowAutoFit? _windowAutoFit;
    private EventWaitHandle? _activationSignal;
    private CancellationTokenSource? _activationListenerCts;
    private IHost? _host;
    private TrayIconController? _trayIconController;
    private bool _isExiting;
    private ReasonSessionEnding? _windowsSessionEnding;

    // An installer's Restart Manager closes the app with WM_QUERYENDSESSION / WM_ENDSESSION and ENDSESSION_CLOSEAPP;
    // WPF reports that as a Windows shutdown. A hidden top-level window sees the flag (a message-only window would not
    // get these broadcasts).
    private System.Windows.Interop.HwndSource? _sessionWatcher;
    private volatile bool _closedByInstaller;

    /// <summary>True once the app exits (tray Exit, sign-out or shutdown): windows then close without asking.</summary>
    internal static bool IsExiting => (Current as App)?._isExiting == true;

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

        // --data-root <folder>: a separate settings, logs and plugins folder (tests a first start). Read before the
        // single-instance check, so a test start never opens the running app with the normal settings.
        var requestedDataRoot = isScreenshotMode ? null : ArgValue(e.Args, "--data-root");

        if (!isScreenshotMode)
        {
            // Enforce single instance: if already running, signal the running instance to surface its window
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew && requestedDataRoot is not null)
            {
                System.Windows.MessageBox.Show(
                    "Denon Desktop is already running with your normal settings. Exit it (tray icon → Exit) before you start it with --data-root.",
                    "Denon Desktop",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

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
        else if (requestedDataRoot is not null)
        {
            AppDataPaths.UseRootDirectory(requestedDataRoot);
        }

        AppDataPaths.EnsureCreated();

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<AppLogStore>();
            builder.Services.AddSingleton<AppSettingsStore>();
            builder.Services.AddSingleton<RoomEventBus>();
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
            builder.Services.AddSingleton<UpdateService>();
            builder.Services.AddSingleton<AutomationEditorApi>();
            builder.Services.AddSingleton<InsightsServer>();
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

            var startMinimized = (settingsStore.Current.Ui.StartMinimizedToTray ||
                                  e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)) &&
                                 !e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);
            var splash = isScreenshotMode || startMinimized ? null : await ShowSplashIfNewAsync(settingsStore, e.Args);

            splash?.SetStatus("Starting plugins…");
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

            WatchForInstallerClose();

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

                // --fit: size the window as a first start does on this monitor (and pick the skin unless --skin),
                // and write the result to fit.txt (checks the fitter without a fresh install).
                if (e.Args.Contains("--fit", StringComparer.OrdinalIgnoreCase))
                {
                    System.IO.Directory.CreateDirectory(captureDir!);
                    var report = await PlaceMainWindowAsync(window, vm, settingsStore, mayPickSkin: skinArg is null, restore: false);
                    window.UpdateLayout();
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(captureDir!, "fit.txt"), report);

                    // With --switch-skin the switch happens on the Settings page, as a user does it, and the window
                    // refits to the deck it cannot see.
                    ThemeManager.SkinChanged += async (_, _) =>
                    {
                        WindowFitter.Refit(window, window.ControlDeckPage);
                        window.UpdateLayout();
                        await System.IO.File.AppendAllTextAsync(System.IO.Path.Combine(captureDir!, "fit.txt"),
                            $"{Environment.NewLine}after switch to {ThemeManager.CurrentSkin.Id} on {vm.ActiveSection}: {window.ActualWidth:0}x{window.ActualHeight:0}");
                    };
                    vm.ActiveSection = "Settings";
                }

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

                // --update-check-as <version>: the real update check as if the app were that version, with the update
                // panel open in the captures; --update-download also downloads and verifies the installer (no install).
                if (ArgValue(e.Args, "--update-check-as") is { } asVersion)
                {
                    System.IO.Directory.CreateDirectory(captureDir!);
                    string report;
                    try
                    {
                        report = await vm.CheckUpdateForScreenshotAsync(
                            _host.Services.GetRequiredService<UpdateService>(), asVersion,
                            e.Args.Contains("--update-download", StringComparer.OrdinalIgnoreCase));
                    }
                    catch (Exception exception)
                    {
                        report = $"failed: {exception.Message}";
                    }

                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(captureDir!, "update.txt"), report);
                }

                // --all-pages: also the pages the README does not show (checks a theme on every page).
                var allPages = e.Args.Any(a => string.Equals(a, "--all-pages", StringComparison.OrdinalIgnoreCase));
                await CaptureScreenshotsAsync(window, vm, captureDir!, allPages);

                // --capture-splash: the splash screen as 00-splash.png.
                if (e.Args.Contains("--capture-splash", StringComparer.OrdinalIgnoreCase))
                {
                    await CaptureSplashAsync(captureDir!);
                }

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

            splash?.SetStatus("Opening the control deck…");
            var placement = await PlaceMainWindowAsync(window, mainVm, settingsStore, mayPickSkin: skinArg is null, restore: true);
            _host.Services.GetRequiredService<AppLogStore>().Info($"Main window: {placement}");

            // Until the user sizes the window it grows with the deck; a skin switch refits it on its monitor.
            _windowAutoFit = new WindowAutoFit(window, window.ControlDeckPage, settingsStore.Current.Ui.Window?.UserSized == true);
            ThemeManager.SkinChanged += (_, _) => WindowFitter.Refit(window, window.ControlDeckPage);

            if (startMinimized)
            {
                window.Hide();
            }
            else
            {
                window.Show();
                window.Activate();
            }

            if (splash is not null)
            {
                await splash.FadeOutAndCloseAsync();
                window.Activate();
            }

            // In-app updates: a daily check of GitHub; after "Download and install" starts the installer, the app exits
            // so the installer can replace it (it starts the new version by itself).
            mainVm.ExitForUpdateRequested += () => _ = Dispatcher.InvokeAsync(ExitApplicationAsync);
            mainVm.StartUpdateChecks(_host.Services.GetRequiredService<UpdateService>(), allowInstall: requestedDataRoot is null);

            // Settings -> Open Insights starts the local Insights page. --insights-url-file <file> starts it now and
            // writes its address (with the key) to the file, without a browser (for tests).
            var insights = _host.Services.GetRequiredService<InsightsServer>();
            mainVm.AttachInsights(insights);
            if (ArgValue(e.Args, "--insights-url-file") is { } insightsUrlFile)
            {
                await System.IO.File.WriteAllTextAsync(insightsUrlFile, await insights.StartAsync());
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

    /// <summary>
    /// Shows the splash screen on the first start after an install, upgrade or reinstall (<c>--splash</c> forces it)
    /// and records that this install has shown it.
    /// </summary>
    private static async Task<SplashWindow?> ShowSplashIfNewAsync(AppSettingsStore settingsStore, string[] args)
    {
        var kind = SplashWindow.KindFor(settingsStore.Current.Ui.SplashShownFor, settingsStore.CreatedThisRun);
        if (args.Contains("--splash", StringComparer.OrdinalIgnoreCase))
        {
            kind ??= SplashKind.Updated;
        }

        if (kind is not { } shownKind)
        {
            return null;
        }

        var splash = new SplashWindow(shownKind);
        splash.Show();

        // The marker must never stop the start: if it cannot be saved now, the splash shows again next time.
        await TrySaveUiStateAsync(settingsStore, ui => ui.SplashShownFor = AppInfo.InstallStamp, "splash marker");
        return splash;
    }

    /// <summary>Saves UI state (window, splash, picked skin); a failure is logged and the app goes on.</summary>
    private static async Task TrySaveUiStateAsync(AppSettingsStore store, Action<Denon.App.Core.Models.UiSettings> change, string what)
    {
        try
        {
            await store.SaveUiStateAsync(change);
        }
        catch (Exception exception)
        {
            Current?.Dispatcher.Invoke(() =>
                (Current as App)?._host?.Services.GetService<AppLogStore>()?.Warning($"Could not save the {what}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Gives the main window its size and place before it shows: the user's saved placement when it is still on a
    /// monitor, else a fit to the monitor under the pointer and the skin (docs/theming.md). On the very first fit
    /// with the Default skin, a skin that suits the screen better is picked: Compact when Default would scroll,
    /// Large on a big screen at low scaling. Returns a line for the log.
    /// </summary>
    private static async Task<string> PlaceMainWindowAsync(MainWindow window, MainViewModel vm, AppSettingsStore settingsStore, bool mayPickSkin, bool restore)
    {
        var ui = settingsStore.Current.Ui;
        if (restore && WindowFitter.TryRestore(window, ui.Window))
        {
            return $"restored {ui.Window!.Width}x{ui.Window.Height} at {ui.Window.Left},{ui.Window.Top}{(ui.Window.Maximized ? ", maximized" : string.Empty)}";
        }

        await WindowFitter.LoadContentAsync(window);
        var monitor = WindowFitter.MonitorAtPointer();
        var area = monitor.WorkArea;
        var deck = window.ControlDeckPage;
        var fit = WindowFitter.Measure(window, deck, area);
        var picked = string.Empty;

        if (mayPickSkin && ui.Window is null && ThemeManager.CurrentSkin.Id == ThemeManager.DefaultSkinId)
        {
            SkinInfo? better = null;
            WindowFitter.Fit betterFit = default;
            if (!fit.NoScroll)
            {
                ThemeManager.ApplySkin("compact");
                (better, betterFit) = (ThemeManager.CurrentSkin, WindowFitter.Measure(window, deck, area));
            }
            else if (area.Height >= 1400 && area.Width >= 2200)
            {
                ThemeManager.ApplySkin("large");
                var largeFit = WindowFitter.Measure(window, deck, area);
                if (largeFit.NoScroll)
                {
                    (better, betterFit) = (ThemeManager.CurrentSkin, largeFit);
                }
                else
                {
                    ThemeManager.ApplySkin(ThemeManager.DefaultSkinId);
                }
            }

            if (better is not null)
            {
                fit = betterFit;
                picked = $", picked skin {better.Id}";
                vm.ReportSkinPickedForScreen(better);
                _ = TrySaveUiStateAsync(settingsStore, u => u.Skin = better.Id, "picked skin");
            }
        }

        WindowFitter.Place(window, monitor, fit.Width, fit.Height);
        return $"fitted {fit.Width}x{fit.Height} on work area {area.Width}x{area.Height} at scale {monitor.Scale:0.##}" +
               $"{(fit.NoScroll ? string.Empty : " (deck scrolls)")}{picked}";
    }

    private async Task SaveWindowPlacementAsync(System.Windows.Window window)
    {
        if (_host?.Services.GetService<AppSettingsStore>() is not { } store || !window.IsLoaded)
        {
            return;
        }

        var placement = WindowFitter.Capture(window);
        placement.UserSized = _windowAutoFit?.UserSized == true;
        await TrySaveUiStateAsync(store, ui => ui.Window = placement, "window placement");
    }

    private static async Task CaptureSplashAsync(string outputDir)
    {
        var splash = new SplashWindow(SplashKind.Updated) { ShowActivated = false };
        splash.Show();
        await Task.Delay(1100);
        splash.UpdateLayout();
        var width = Math.Max(1, (int)Math.Round(splash.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(splash.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(splash);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        await using (var file = System.IO.File.Create(System.IO.Path.Combine(outputDir, "00-splash.png")))
        {
            encoder.Save(file);
        }

        splash.Close();
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

    private void WatchForInstallerClose()
    {
        try
        {
            const int wsPopup = unchecked((int)0x80000000); // hidden: no WS_VISIBLE
            _sessionWatcher = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("DenonDesktopSessionWatcher")
            {
                WindowStyle = wsPopup,
                Width = 0,
                Height = 0,
            });
            _sessionWatcher.AddHook(SessionWatcherHook);
        }
        catch (Exception ex)
        {
            // Only the shutdown log line's wording depends on it.
            _host?.Services.GetService<AppLogStore>()?.Warning($"Could not watch for an installer closing the app: {ex.Message}");
        }
    }

    private IntPtr SessionWatcherHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmQueryEndSession = 0x0011;
        const int wmEndSession = 0x0016;
        const long endSessionCloseApp = 0x1;
        if (msg is wmQueryEndSession or wmEndSession && (lParam.ToInt64() & endSessionCloseApp) != 0)
        {
            _closedByInstaller = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        _windowsSessionEnding = e.ReasonSessionEnding;

        // Sign-out or shutdown skips ExitApplicationAsync: save the window placement here (off the UI thread, bounded).
        if (MainWindow is MainWindow window && window.IsLoaded && _host?.Services.GetService<AppSettingsStore>() is { } store)
        {
            var placement = WindowFitter.Capture(window);
            placement.UserSized = _windowAutoFit?.UserSized == true;
            try
            {
                Task.Run(() => store.SaveUiStateAsync(ui => ui.Window = placement)).Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListenerCts?.Cancel();
        _activationListenerCts?.Dispose();
        _activationSignal?.Dispose();
        _trayIconController?.Dispose();
        _sessionWatcher?.Dispose();

        // Reached without ExitApplicationAsync when Windows ends the app (logoff, shutdown,
        // Restart Manager during an upgrade). Stop plugins and hosted services first, so open
        // habit sessions get their session.ended events, then dispose. Bounded: Windows gives ~5 s.
        var remainingHost = _host;
        _host = null;
        if (remainingHost is not null)
        {
            try
            {
                // Windows sign-out or shutdown says so; anything else here is an installer's Restart Manager or a
                // closed main window with no tray, not a session ending.
                var why = _closedByInstaller
                    ? "closed by an installer"
                    : _windowsSessionEnding switch
                    {
                        ReasonSessionEnding.Logoff => "Windows sign-out",
                        ReasonSessionEnding.Shutdown => "Windows shutdown",
                        _ => $"exit code {e.ApplicationExitCode}, not through Exit",
                    };
                remainingHost.Services.GetRequiredService<AppLogStore>().Info($"Application shutting down ({why}).");
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
        _ = SaveWindowPlacementAsync(window);

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
            if (MainWindow is MainWindow shownWindow)
            {
                try
                {
                    await SaveWindowPlacementAsync(shownWindow).WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch
                {
                }
            }

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

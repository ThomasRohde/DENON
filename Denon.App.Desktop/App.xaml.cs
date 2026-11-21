using System.ComponentModel;
using System.Threading;
using System.Windows;
using Denon.App.Core.Heos;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Services;
using Denon.App.Desktop.Services;
using Denon.App.Desktop.ViewModels;
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

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppDataPaths.EnsureCreated();

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<AppLogStore>();
            builder.Services.AddSingleton<AppSettingsStore>();
            builder.Services.AddSingleton<ReceiverStateStore>();
            builder.Services.AddSingleton<HeosClient>();
            builder.Services.AddSingleton<DenonAvrClient>();
            builder.Services.AddSingleton<DenonDiscoveryService>();
            builder.Services.AddSingleton<StreamProbeService>();
            builder.Services.AddSingleton<WindowsStartupService>();
            builder.Services.AddSingleton<ReceiverController>();
            builder.Services.AddSingleton<YouTubeMediaService>();
            builder.Services.AddSingleton<LocalAudioRelayServer>();
            builder.Services.AddSingleton<YouTubeQueueService>();
            builder.Services.AddSingleton<LocalMediaJukeboxService>();
            builder.Services.AddSingleton<RadioBrowserService>();
            builder.Services.AddSingleton<RemoteWebApiHandler>();
            builder.Services.AddSingleton<PcAudioCastService>();
            builder.Services.AddSingleton<HeosEventListenerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<HeosEventListenerService>());
            builder.Services.AddSingleton<YouTubeCinemaWindow>();
            builder.Services.AddHostedService<ReceiverPollingService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<TrayFlyoutWindow>();
            builder.Services.AddSingleton<TrayIconController>();

            _host = builder.Build();

            var settingsStore = _host.Services.GetRequiredService<AppSettingsStore>();
            await settingsStore.InitializeAsync();

            var relayServer = _host.Services.GetRequiredService<LocalAudioRelayServer>();
            relayServer.AttachWebApiHandler(_host.Services.GetRequiredService<RemoteWebApiHandler>());
            _ = _host.Services.GetRequiredService<PcAudioCastService>(); // ensures live PC audio handler is attached

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
            });

            _host.Services.GetRequiredService<AppLogStore>().Info("Denon Desktop started.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.StateChanged += OnMainWindowStateChanged;

            _trayIconController = _host.Services.GetRequiredService<TrayIconController>();
            _trayIconController.Initialize(window, ExitApplicationAsync);

            StartActivationSignalListener();

            var startMinimized = settingsStore.Current.Ui.StartMinimizedToTray &&
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

    protected override void OnExit(ExitEventArgs e)
    {
        _activationListenerCts?.Cancel();
        _activationListenerCts?.Dispose();
        _activationSignal?.Dispose();
        _trayIconController?.Dispose();

        if (_host is not null)
        {
            try
            {
                _host.Dispose();
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

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        try
        {
            _trayIconController?.Dispose();
            _trayIconController = null;

            if (_host is not null)
            {
                try
                {
                    _host.Services.GetService<YouTubeCinemaWindow>()?.HideAndStopVideo();
                }
                catch
                {
                }

                _host.Services.GetRequiredService<AppLogStore>().Info("Application shutting down.");
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
                _host = null;
            }
        }
        finally
        {
            Shutdown();
        }
    }
}

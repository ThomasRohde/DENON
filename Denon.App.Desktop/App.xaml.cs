using System.ComponentModel;
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
    private IHost? _host;
    private TrayIconController? _trayIconController;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppDataPaths.EnsureCreated();

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Services.AddSingleton<AppLogStore>();
            builder.Services.AddSingleton<AppSettingsStore>();
            builder.Services.AddSingleton<ReceiverStateStore>();
            builder.Services.AddSingleton<HeosClient>();
            builder.Services.AddSingleton<DenonAvrClient>();
            builder.Services.AddSingleton<ReceiverController>();
            builder.Services.AddHostedService<ReceiverPollingService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<TrayIconController>();

            _host = builder.Build();

            var settingsStore = _host.Services.GetRequiredService<AppSettingsStore>();
            await settingsStore.InitializeAsync();
            await _host.StartAsync();

            _host.Services.GetRequiredService<AppLogStore>().Info("Application started.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Closing += OnMainWindowClosing;
            window.StateChanged += OnMainWindowStateChanged;

            _trayIconController = _host.Services.GetRequiredService<TrayIconController>();
            _trayIconController.Initialize(window, ExitApplicationAsync);

            if (settingsStore.Current.Ui.StartMinimizedToTray)
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

    protected override void OnExit(ExitEventArgs e)
    {
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
            _trayIconController?.ShowBalloon("Denon Desktop", "Receiver control is still running in the tray.");
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
            _trayIconController?.ShowBalloon("Denon Desktop", "Window closed. Use the tray icon to reopen the app.");
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

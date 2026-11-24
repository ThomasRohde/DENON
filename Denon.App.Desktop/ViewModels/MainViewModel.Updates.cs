using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Denon.App.Desktop.Infrastructure;
using Denon.App.Desktop.Services;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// In-app updates: a daily check of GitHub (Settings → Check for updates), an Update button in the status line when
/// a newer version is out, and a panel with what changed and "Download and install".
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The first check waits until the app has settled after its start.</summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private UpdateService? _updateService;
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _updateCts;
    private bool _canInstallInPlace;
    private AppRelease? _availableUpdate;
    private bool _isUpdatePanelOpen;
    private bool _isUpdateBusy;
    private string _updateProgressText = string.Empty;
    private bool _settingsCheckForUpdates = true;

    private ICommand? _openUpdatePanelCommand;
    private ICommand? _closeUpdatePanelCommand;
    private ICommand? _installUpdateCommand;
    private ICommand? _openUpdatePageCommand;
    private ICommand? _checkForUpdatesNowCommand;
    private ICommand? _cancelUpdateCommand;

    /// <summary>The app asks to exit after it started the update installer (App closes it).</summary>
    public event Action? ExitForUpdateRequested;

    public bool SettingsCheckForUpdates
    {
        get => _settingsCheckForUpdates;
        set => SetProperty(ref _settingsCheckForUpdates, value);
    }

    public AppRelease? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (SetProperty(ref _availableUpdate, value))
            {
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(UpdateButtonText));
                OnPropertyChanged(nameof(UpdatePanelTitle));
                OnPropertyChanged(nameof(UpdateNotesText));
            }
        }
    }

    public bool IsUpdateAvailable => _availableUpdate is not null;

    public string UpdateButtonText => _availableUpdate is null ? string.Empty : $"Update to {_availableUpdate.Label}";

    public string UpdatePanelTitle => _availableUpdate is null ? string.Empty : $"Denon Desktop {_availableUpdate.Label} is available";

    public string UpdateCurrentText => IsInstalledCopy
        ? $"You have version {AppInfo.Version}. The update installs over it and keeps your settings. The app closes, updates and starts again."
        : UpdateService.IsInstalled
            ? $"You have version {AppInfo.Version}, started with a separate data folder (--data-root). Install the update from a normal start."
            : $"You have version {AppInfo.Version}, the portable app or a copy outside the install folder. Download the new version from the release page.";

    public string UpdateNotesText => _availableUpdate is null ? string.Empty : UpdateService.PlainNotes(_availableUpdate.Notes);

    /// <summary>
    /// The registered install, started normally, can update itself. The portable exe, a copy elsewhere, or a start
    /// with --data-root (its relaunch would open the normal settings) get the release page.
    /// </summary>
    public bool IsInstalledCopy
    {
        get => _canInstallInPlace;
        private set
        {
            if (SetProperty(ref _canInstallInPlace, value))
            {
                OnPropertyChanged(nameof(UpdateCurrentText));
            }
        }
    }

    public bool IsUpdatePanelOpen
    {
        get => _isUpdatePanelOpen;
        set
        {
            if (SetProperty(ref _isUpdatePanelOpen, value))
            {
                OnPropertyChanged(nameof(IsUpdatePanelClosed));
            }
        }
    }

    /// <summary>The window's content takes input only while the update panel is closed (the panel is modal).</summary>
    public bool IsUpdatePanelClosed => !_isUpdatePanelOpen;

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (SetProperty(ref _isUpdateBusy, value))
            {
                OnPropertyChanged(nameof(IsUpdateIdle));
            }
        }
    }

    public bool IsUpdateIdle => !_isUpdateBusy;

    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set => SetProperty(ref _updateProgressText, value);
    }

    public ICommand OpenUpdatePanelCommand => _openUpdatePanelCommand ??= new RelayCommand(_ =>
    {
        UpdateProgressText = string.Empty;
        IsUpdatePanelOpen = _availableUpdate is not null;
    });

    public ICommand CloseUpdatePanelCommand => _closeUpdatePanelCommand ??= new RelayCommand(_ =>
    {
        if (!_isUpdateBusy)
        {
            IsUpdatePanelOpen = false;
        }
    });

    public ICommand OpenUpdatePageCommand => _openUpdatePageCommand ??= new RelayCommand(_ =>
    {
        if (_availableUpdate is not { PageUrl.Length: > 0 } release)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Could not open the release page: {exception.Message}");
            UpdateProgressText = $"Windows could not open the browser. Open this address yourself: {release.PageUrl}";
        }
    });

    /// <summary>Stops a download in progress; the panel is ready to try again.</summary>
    public ICommand CancelUpdateCommand => _cancelUpdateCommand ??= new RelayCommand(_ => _updateCts?.Cancel());

    public ICommand InstallUpdateCommand => _installUpdateCommand ??= new RelayCommand(_ => _ = InstallUpdateAsync());

    public ICommand CheckForUpdatesNowCommand => _checkForUpdatesNowCommand ??= new RelayCommand(_ => _ = CheckForUpdatesAsync(manual: true));

    /// <summary>
    /// Starts the automatic checks (the app calls it once the main window shows; never in screenshot mode). Each
    /// check runs only while Settings → Check for updates is on.
    /// </summary>
    public void StartUpdateChecks(UpdateService updateService, bool allowInstall)
    {
        _updateService = updateService;
        IsInstalledCopy = allowInstall && UpdateService.IsInstalled;

        // Installers from earlier updates: Setup has exited by now.
        _ = Task.Run(UpdateService.CleanDownloads);

        // Once a day across restarts: the first check is due a day after the last one (at least FirstCheckDelay away).
        var last = _settingsStore.Current.Ui.LastUpdateCheck;
        var due = last is { } at ? at + CheckInterval - DateTimeOffset.Now : TimeSpan.Zero;
        _updateTimer = new DispatcherTimer { Interval = due > FirstCheckDelay ? due : FirstCheckDelay };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = CheckInterval;
            if (_settingsStore.Current.Ui.CheckForUpdates)
            {
                _ = CheckForUpdatesAsync(manual: false);
            }
        };
        _updateTimer.Start();
    }

    /// <summary>
    /// Screenshot mode (--update-check-as): the real check as if this were <paramref name="asVersion"/>, then the
    /// update panel; with <paramref name="download"/> also the real download and checksum check (no install).
    /// Returns a line for the report.
    /// </summary>
    public async Task<string> CheckUpdateForScreenshotAsync(UpdateService updateService, string asVersion, bool download)
    {
        _updateService = updateService;
        IsInstalledCopy = UpdateService.IsInstalled;
        var update = await updateService.FindUpdateAsync(asVersion);
        AvailableUpdate = update;
        IsUpdatePanelOpen = update is not null;
        if (update is null)
        {
            return $"no update newer than {asVersion}";
        }

        var report = $"update {update.Label} ({update.Tag}), installer {update.InstallerUrl}, notes {update.Notes.Length} chars";
        if (download)
        {
            var path = await updateService.DownloadVerifiedAsync(update, null);
            report += $"; downloaded and verified: {path} ({new System.IO.FileInfo(path).Length} bytes)";
        }

        return report;
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateService is null)
        {
            return;
        }

        try
        {
            if (manual)
            {
                StatusMessage = "Checking for updates…";
            }

            var update = await _updateService.FindUpdateAsync();
            AvailableUpdate = update;
            await _settingsStore.SaveUiStateAsync(ui => ui.LastUpdateCheck = DateTimeOffset.Now);
            if (manual)
            {
                StatusMessage = update is null
                    ? $"Denon Desktop {AppInfo.Version} is up to date."
                    : $"Denon Desktop {update.Label} is available. Click Update in the status line.";
            }
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Update check failed: {exception.Message}");
            if (manual)
            {
                StatusMessage = $"Could not check for updates: {exception.Message}";
            }
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updateService is null || _availableUpdate is not { } release || _isUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        _updateCts = new CancellationTokenSource();
        try
        {
            UpdateProgressText = "Downloading…";
            var progress = new Progress<double>(p =>
            {
                if (_isUpdateBusy)
                {
                    UpdateProgressText = $"Downloading… {p:P0}";
                }
            });
            var installer = await _updateService.DownloadVerifiedAsync(release, progress, _updateCts.Token);
            UpdateProgressText = "Download checked. Starting the installer…";
            _updateService.StartInstaller(installer);
            ExitForUpdateRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_updateCts.IsCancellationRequested)
        {
            UpdateProgressText = "Download canceled.";
            IsUpdateBusy = false;
        }
        catch (Exception exception)
        {
            _logStore.Warning($"Update failed: {exception.Message}");
            UpdateProgressText = $"The update failed: {exception.Message} Try again, or download it from the release page.";
            IsUpdateBusy = false;
        }
        finally
        {
            _updateCts.Dispose();
            _updateCts = null;
        }
    }
}

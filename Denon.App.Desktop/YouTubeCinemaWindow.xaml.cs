using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Denon.App.Desktop;

public partial class YouTubeCinemaWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private const string PureVideoModeScript = """
        (() => {
            if (window.__denonCinemaInstalled) return;
            window.__denonCinemaInstalled = true;

            const css = `
                html, body {
                    background: #000 !important;
                    overflow: hidden !important;
                    margin: 0 !important;
                    padding: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                }
                #masthead-container,
                ytd-masthead,
                #secondary,
                #below,
                #related,
                #comments,
                #chat,
                #guide,
                tp-yt-app-drawer,
                ytd-mini-guide-renderer,
                ytd-consent-bump-v2-lightbox,
                ytd-popup-container,
                tp-yt-paper-dialog,
                .ytp-chrome-top,
                .ytp-chrome-bottom,
                .ytp-gradient-top,
                .ytp-gradient-bottom,
                .ytp-watermark,
                .ytp-ce-element,
                .ytp-cards-teaser,
                .ytp-cards-button,
                .ytp-pause-overlay,
                .ytp-paid-content-overlay,
                .ytp-suggested-action,
                .ytp-endscreen-content,
                .ytp-upnext,
                .ytp-autonav-endscreen-upnext-container,
                .ytp-bezel-text-wrapper,
                .iv-branding {
                    display: none !important;
                    visibility: hidden !important;
                    opacity: 0 !important;
                    pointer-events: none !important;
                }
                #movie_player,
                .html5-video-player {
                    position: fixed !important;
                    inset: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    background: #000 !important;
                    z-index: 2147483640 !important;
                }
                .html5-video-container {
                    position: fixed !important;
                    inset: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    background: #000 !important;
                }
                video.html5-main-video {
                    position: fixed !important;
                    top: 0 !important;
                    left: 0 !important;
                    right: 0 !important;
                    bottom: 0 !important;
                    margin: 0 !important;
                    transform: none !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    object-fit: contain !important;
                    background: #000 !important;
                    z-index: 2147483645 !important;
                }
            `;

            const injectStyle = () => {
                if (!document.getElementById('denon-pure-cinema-css')) {
                    const style = document.createElement('style');
                    style.id = 'denon-pure-cinema-css';
                    style.textContent = css;
                    (document.head || document.documentElement).appendChild(style);
                }
            };

            const dismissOverlays = () => {
                const skipBtn = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern');
                if (skipBtn) skipBtn.click();

                // Disable YouTube's built-in web Autoplay so our Denon Queue stays in control
                const autoNav = document.querySelector('.ytp-autonav-toggle-button[aria-checked="true"]');
                if (autoNav) autoNav.click();

                const cancelAutoplay = document.querySelector('.ytp-autonav-endscreen-upnext-cancel-button, .ytp-upnext-cancel-button');
                if (cancelAutoplay) cancelAutoplay.click();

                const v = document.querySelector('video');
                if (v && !v.__denonEndedHooked) {
                    v.__denonEndedHooked = true;
                    v.addEventListener('ended', () => {
                        v.pause();
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage('VIDEO_ENDED');
                        }
                    });
                }
            };

            window.addEventListener('keydown', (ev) => {
                if (ev.key === 'Escape' || ev.key === 'F11' || ev.key === 'h' || ev.key === 'H') {
                    ev.preventDefault();
                    ev.stopPropagation();
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(ev.key.toUpperCase());
                    }
                }
            }, true);

            injectStyle();
            document.addEventListener('DOMContentLoaded', injectStyle);
            const observer = new MutationObserver(() => {
                injectStyle();
                dismissOverlays();
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private bool _isInitialized;
    private bool _keepVideoMuted = true;
    private bool _isFullscreen;
    private bool _barsHidden;
    private string? _currentVideoId;
    private string? _currentWatchUrl;
    private int _syncOffsetMs = 1800;
    private Func<int, double>? _positionProvider;
    private CancellationTokenSource? _syncWaitCts;

    public YouTubeCinemaWindow()
    {
        InitializeComponent();
        ApplyDwmStyling();
    }

    public async Task LoadTrackAsync(
        YouTubeTrackItem track,
        int syncOffsetMs = 1800,
        Func<int, double>? positionProvider = null)
    {
        _currentVideoId = track.VideoId;
        _positionProvider = positionProvider;
        _syncOffsetMs = Math.Clamp(syncOffsetMs, 0, 8000);

        var initialSeconds = positionProvider is not null ? Math.Max(0, (int)Math.Floor(positionProvider(_syncOffsetMs))) : 0;
        var baseUrl = string.IsNullOrWhiteSpace(track.WatchUrl)
            ? $"https://www.youtube.com/watch?v={track.VideoId}"
            : track.WatchUrl;

        _currentWatchUrl = baseUrl;
        var navUrl = initialSeconds > 1
            ? (baseUrl.Contains('?') ? $"{baseUrl}&t={initialSeconds}s" : $"{baseUrl}?t={initialSeconds}s")
            : baseUrl;

        TitleText.Text = $"{track.Title}  •  {track.Author}";
        Title = $"Denon Cinema — {track.Title}";

        if (!IsVisible)
        {
            Show();
        }

        // Automatically open YouTube Cinema in borderless fullscreen
        SetFullscreen(true);
        Activate();
        await EnsureWebViewReadyAsync();

        if (VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        LoadingOverlay.Text = initialSeconds > 1
            ? $"Syncing video to {FormatTime(initialSeconds)}..."
            : "Loading pure Cinema video...";
        LoadingOverlay.Visibility = Visibility.Visible;
        VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;
        VideoWebView.CoreWebView2.Navigate(navUrl);
        UpdateSyncStatus(initialSeconds);
    }

    public async Task SyncPlaybackStateAsync(bool isPaused)
    {
        if (!_isInitialized || !IsVisible || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            if (isPaused)
            {
                await VideoWebView.ExecuteScriptAsync("(() => { const v = document.querySelector('video'); if (v && !v.paused) v.pause(); })();");
            }
            else
            {
                await ApplyLiveSyncPositionAsync();
            }
        }
        catch
        {
        }
    }

    private async Task EnsureWebViewReadyAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            var userDataDir = Path.Combine(AppDataPaths.RootDirectory, "webview2");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDir);
            await VideoWebView.EnsureCoreWebView2Async(env);

            VideoWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            VideoWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;
            await VideoWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PureVideoModeScript);
            VideoWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            VideoWebView.CoreWebView2.SourceChanged += OnWebSourceChanged;
            VideoWebView.NavigationCompleted += OnNavigationCompleted;
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            LoadingOverlay.Text = $"WebView2 initialization failed: {ex.Message}\nClick 'Open Browser' to watch externally.";
            LoadingOverlay.Visibility = Visibility.Visible;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;

        try
        {
            await VideoWebView.ExecuteScriptAsync(PureVideoModeScript);
        }
        catch
        {
        }

        await WaitForVideoReadyAndSyncAsync();
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task WaitForVideoReadyAndSyncAsync()
    {
        _syncWaitCts?.Cancel();
        _syncWaitCts?.Dispose();
        var cts = new CancellationTokenSource();
        _syncWaitCts = cts;

        try
        {
            for (var attempt = 0; attempt < 24 && !cts.IsCancellationRequested; attempt++)
            {
                var readyRaw = await VideoWebView.ExecuteScriptAsync("(() => { const v = document.querySelector('video'); return v && v.readyState >= 1 ? 1 : 0; })();");
                if (readyRaw.Trim() == "1")
                {
                    await ApplyLiveSyncPositionAsync();
                    return;
                }

                await Task.Delay(180, cts.Token);
            }

            await ApplyLiveSyncPositionAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task ApplyLiveSyncPositionAsync()
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        var targetSeconds = _positionProvider is not null ? Math.Max(0.0, _positionProvider(_syncOffsetMs)) : 0.0;
        var targetLiteral = targetSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var mutedLiteral = _keepVideoMuted ? "true" : "false";

        var script = $$"""
            (() => {
                const v = document.querySelector('video');
                if (!v) return;
                v.muted = {{mutedLiteral}};
                const target = {{targetLiteral}};
                if (Math.abs((v.currentTime || 0) - target) > 0.35) {
                    v.currentTime = target;
                }
                if (v.paused) {
                    v.play().catch(() => {});
                }
            })();
            """;

        try
        {
            VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;
            await VideoWebView.ExecuteScriptAsync(script);
            UpdateSyncStatus(targetSeconds);
        }
        catch
        {
        }
    }

    private async void ToggleVideoMute_Click(object sender, RoutedEventArgs e)
    {
        _keepVideoMuted = !_keepVideoMuted;
        MuteVideoButton.Content = _keepVideoMuted ? "Video Muted (Denon Audio)" : "PC Audio Active (Unmuted)";

        if (_isInitialized && VideoWebView.CoreWebView2 is not null)
        {
            VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;
            var mutedLiteral = _keepVideoMuted ? "true" : "false";
            try
            {
                await VideoWebView.ExecuteScriptAsync($"(() => {{ const v = document.querySelector('video'); if (v) v.muted = {mutedLiteral}; }})();");
            }
            catch
            {
            }
        }

        UpdateSyncStatus();
    }

    private async Task NudgeVideoSecondsAsync(double deltaSeconds)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        // Remember offset adjustment so future Re-Syncs & next tracks inherit the dialed-in sync
        _syncOffsetMs = Math.Clamp(_syncOffsetMs - (int)Math.Round(deltaSeconds * 1000), 0, 8000);

        var deltaStr = deltaSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        try
        {
            await VideoWebView.ExecuteScriptAsync($"(() => {{ const v = document.querySelector('video'); if (v) v.currentTime = Math.max(0, v.currentTime + ({deltaStr})); }})();");
            SyncStatusText.Text = $"Nudged {(deltaSeconds >= 0 ? "+" : "")}{(int)(deltaSeconds * 1000)} ms (HEOS offset {_syncOffsetMs}ms)";
        }
        catch
        {
        }
    }

    private async void NudgeMinus500_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(-0.5);

    private async void NudgeMinus100_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(-0.1);

    private async void NudgePlus100_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(0.1);

    private async void NudgePlus500_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(0.5);

    private async void ResyncToDenon_Click(object sender, RoutedEventArgs e)
    {
        await ApplyLiveSyncPositionAsync();
    }

    private async void OnWebSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null || string.IsNullOrWhiteSpace(_currentVideoId))
        {
            return;
        }

        // Prevent YouTube's web Autoplay SPA from jumping to an unrelated video on its own
        var source = VideoWebView.CoreWebView2.Source ?? string.Empty;
        if (source.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) &&
            !source.Contains(_currentVideoId, StringComparison.Ordinal))
        {
            try
            {
                await VideoWebView.ExecuteScriptAsync("(() => { const v = document.querySelector('video'); if (v) v.pause(); })();");
                SyncStatusText.Text = "Blocked YouTube web autoplay — Denon Queue is in control";
            }
            catch
            {
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = e.TryGetWebMessageAsString();
            if (string.Equals(msg, "VIDEO_ENDED", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => SyncStatusText.Text = "Video ended — waiting for Denon Queue...");
                return;
            }

            Dispatcher.Invoke(() => HandleCinemaShortcutKey(msg));
        }
        catch
        {
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                HandleCinemaShortcutKey("ESCAPE");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F11:
                HandleCinemaShortcutKey("F11");
                e.Handled = true;
                break;
            case System.Windows.Input.Key.H:
                HandleCinemaShortcutKey("H");
                e.Handled = true;
                break;
        }
    }

    private void HandleCinemaShortcutKey(string keyName)
    {
        switch (keyName.ToUpperInvariant())
        {
            case "F11":
                SetFullscreen(!_isFullscreen);
                break;
            case "H":
                SetBarsHidden(!_barsHidden);
                break;
            case "ESCAPE":
                if (_barsHidden)
                {
                    SetBarsHidden(false);
                }
                else if (_isFullscreen)
                {
                    SetFullscreen(false);
                }
                else
                {
                    HideAndStopVideo();
                }
                break;
        }
    }

    private void SetFullscreen(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            // Temporarily switch to Normal before WindowStyle.None + Maximized so Windows covers the taskbar cleanly
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            CinemaWindowChrome.CaptionHeight = 0;
            CinemaWindowChrome.ResizeBorderThickness = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(0);
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "Exit Fullscreen (F11)";
        }
        else
        {
            SetBarsHidden(false);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            CinemaWindowChrome.CaptionHeight = 42;
            CinemaWindowChrome.ResizeBorderThickness = new Thickness(6);
            RootBorder.BorderThickness = new Thickness(1);
            Width = 960;
            Height = 600;
            FullscreenButton.Content = "Fullscreen (F11)";
        }
    }

    private void SetBarsHidden(bool hidden)
    {
        _barsHidden = hidden;
        TitleBarBorder.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRow.Height = hidden ? new GridLength(0) : new GridLength(42);
        BottomSyncBar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        HideBarsButton.Content = hidden ? "Show Bars (H)" : "Hide Bars (H)";
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

    private void ToggleBars_Click(object sender, RoutedEventArgs e) => SetBarsHidden(!_barsHidden);

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Content = Topmost ? "Pinned ✓" : "Pin on Top";
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentWatchUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_currentWatchUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideAndStopVideo();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Hide instead of permanently destroying so Cinema Companion reopens instantaneously
        e.Cancel = true;
        HideAndStopVideo();
    }

    public void HideAndStopVideo()
    {
        _syncWaitCts?.Cancel();
        SetBarsHidden(false);
        try
        {
            if (_isInitialized && VideoWebView.CoreWebView2 is not null)
            {
                VideoWebView.CoreWebView2.Navigate("about:blank");
            }
        }
        catch
        {
        }

        Hide();
    }

    private void UpdateSyncStatus(double? syncedSeconds = null)
    {
        var timeTag = syncedSeconds is > 0.5 ? $"Synced {FormatTime(syncedSeconds.Value)} • " : string.Empty;
        SyncStatusText.Text = _keepVideoMuted
            ? $"{timeTag}Audio on Denon AVR • Video muted (offset {_syncOffsetMs}ms)"
            : $"{timeTag}Video audio unmuted on PC / HDMI";
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void ApplyDwmStyling()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch
        {
        }
    }
}

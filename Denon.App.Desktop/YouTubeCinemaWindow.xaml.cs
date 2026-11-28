using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Denon.App.Core.Infrastructure;
using Denon.App.Core.Models;
using Denon.App.Desktop.Infrastructure.Theming;
using Microsoft.Web.WebView2.Core;

namespace Denon.App.Desktop;

public partial class YouTubeCinemaWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private const string PureVideoModeScript = """
        (() => {
            const css = `
                html, body {
                    background: #000 !important;
                    overflow: hidden !important;
                    margin: 0 !important;
                    padding: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                }
                ytd-app,
                ytd-watch-flexy,
                #page-manager,
                #columns,
                #primary,
                #primary-inner,
                #player,
                #player-container-outer,
                #player-container-inner,
                #player-container,
                #ytd-player {
                    transform: none !important;
                    contain: none !important;
                    filter: none !important;
                    perspective: none !important;
                    background: #000 !important;
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
                .ytp-title,
                .ytp-show-cards-title,
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
                .ytp-fullscreen-grid,
                .ytp-fullscreen-metadata-top-container,
                .ytp-fullscreen-quick-actions,
                .ytp-overlay-bottom-right,
                .ytp-scroll-min,
                .ytp-bezel-text-wrapper,
                .iv-branding {
                    display: none !important;
                    visibility: hidden !important;
                    opacity: 0 !important;
                    pointer-events: none !important;
                }
                #movie_player,
                .html5-video-player,
                #movie_player:fullscreen,
                .html5-video-player:fullscreen {
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
                #denon-cinema-hud {
                    position: fixed !important;
                    top: 14px !important;
                    right: 16px !important;
                    z-index: 2147483647 !important;
                    display: none;
                    align-items: center;
                    gap: 6px;
                    padding: 6px 8px;
                    border-radius: 10px;
                    background: rgba(13, 16, 23, 0.86);
                    border: 1px solid rgba(56, 189, 248, 0.35);
                    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.55);
                    backdrop-filter: blur(8px);
                    opacity: 0;
                    transition: opacity 0.22s ease;
                    pointer-events: auto !important;
                    font-family: "Segoe UI", system-ui, sans-serif;
                }
                #denon-cinema-hud.active {
                    display: flex !important;
                }
                #denon-cinema-hud.visible {
                    opacity: 1 !important;
                }
                #denon-cinema-hud button {
                    appearance: none;
                    border: 1px solid rgba(148, 163, 184, 0.28);
                    background: rgba(30, 41, 59, 0.85);
                    color: #e2e8f0;
                    font-size: 12px;
                    font-weight: 600;
                    padding: 5px 10px;
                    border-radius: 7px;
                    cursor: pointer;
                    line-height: 1.2;
                }
                #denon-cinema-hud button:hover {
                    background: rgba(51, 65, 85, 0.95);
                    border-color: rgba(56, 189, 248, 0.65);
                    color: #ffffff;
                }
                #denon-cinema-hud button.denon-hud-close {
                    background: rgba(127, 29, 29, 0.85);
                    border-color: rgba(239, 68, 68, 0.55);
                    color: #fecaca;
                    padding: 5px 11px;
                }
                #denon-cinema-hud button.denon-hud-close:hover {
                    background: #dc2626;
                    border-color: #f87171;
                    color: #ffffff;
                }
            `;

            const ensureFloatingHud = () => {
                const targetParent = document.fullscreenElement || document.documentElement || document.body;
                if (!targetParent) return null;
                let hud = document.getElementById('denon-cinema-hud');
                if (!hud) {
                    hud = document.createElement('div');
                    hud.id = 'denon-cinema-hud';

                    const showBtn = document.createElement('button');
                    showBtn.type = 'button';
                    showBtn.textContent = 'Show Bars (H)';
                    showBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        window.chrome?.webview?.postMessage('SHOW_BARS');
                    });

                    const fsBtn = document.createElement('button');
                    fsBtn.type = 'button';
                    fsBtn.textContent = 'Fullscreen (F11)';
                    fsBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        window.chrome?.webview?.postMessage('F11');
                    });

                    const closeBtn = document.createElement('button');
                    closeBtn.type = 'button';
                    closeBtn.className = 'denon-hud-close';
                    closeBtn.title = 'Close Cinema Window';
                    closeBtn.textContent = '✕ Close';
                    closeBtn.addEventListener('click', (e) => {
                        e.preventDefault();
                        e.stopPropagation();
                        window.chrome?.webview?.postMessage('CLOSE_WINDOW');
                    });

                    hud.appendChild(showBtn);
                    hud.appendChild(fsBtn);
                    hud.appendChild(closeBtn);

                    hud.addEventListener('mouseenter', () => {
                        window.__denonHudHovered = true;
                        if (window.__denonHudTimer) clearTimeout(window.__denonHudTimer);
                        hud.classList.add('visible');
                    });
                    hud.addEventListener('mouseleave', () => {
                        window.__denonHudHovered = false;
                        window.__denonFlashHud?.(1600);
                    });
                }

                if (hud.parentElement !== targetParent) {
                    targetParent.appendChild(hud);
                }

                if (window.__denonBarsHidden) {
                    hud.classList.add('active');
                }
                return hud;
            };

            window.__denonFlashHud = (ms = 2400) => {
                if (!window.__denonBarsHidden) return;
                const hud = ensureFloatingHud();
                if (!hud) return;
                hud.classList.add('active', 'visible');
                if (window.__denonHudTimer) clearTimeout(window.__denonHudTimer);
                window.__denonHudTimer = setTimeout(() => {
                    if (!window.__denonHudHovered) {
                        hud.classList.remove('visible');
                    }
                }, ms);
            };

            window.__denonSetBarsHidden = (hidden) => {
                window.__denonBarsHidden = !!hidden;
                const hud = ensureFloatingHud();
                if (!hud) return;
                if (window.__denonBarsHidden) {
                    hud.classList.add('active');
                    window.__denonFlashHud(3500);
                } else {
                    if (window.__denonHudTimer) clearTimeout(window.__denonHudTimer);
                    hud.classList.remove('active', 'visible');
                }
            };

            const injectStyle = () => {
                const host = document.head || document.documentElement;
                if (!host) return false;
                if (!document.getElementById('denon-pure-cinema-css')) {
                    const style = document.createElement('style');
                    style.id = 'denon-pure-cinema-css';
                    style.textContent = css;
                    host.appendChild(style);
                }
                return true;
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

            injectStyle();
            dismissOverlays();
            ensureFloatingHud();

            if (!window.__denonCinemaKeyHooked) {
                window.__denonCinemaKeyHooked = true;
                window.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Escape' || ev.key === 'F11' || ev.key === 'h' || ev.key === 'H') {
                        ev.preventDefault();
                        ev.stopPropagation();
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(ev.key.toUpperCase());
                        }
                    }
                }, true);
                window.addEventListener('mousemove', () => {
                    if (window.__denonBarsHidden) {
                        window.__denonFlashHud?.(2400);
                    }
                }, { passive: true });
                document.addEventListener('fullscreenchange', () => {
                    ensureFloatingHud();
                    if (window.__denonBarsHidden && !document.fullscreenElement) {
                        window.chrome?.webview?.postMessage('SHOW_BARS');
                    }
                });
            }

            if (!window.__denonCinemaObserverInstalled) {
                const attachObserver = () => {
                    if (window.__denonCinemaObserverInstalled) return;
                    if (!document.documentElement) return;
                    window.__denonCinemaObserverInstalled = true;
                    injectStyle();
                    dismissOverlays();
                    let pending = false;
                    const observer = new MutationObserver(() => {
                        if (pending) return;
                        pending = true;
                        requestAnimationFrame(() => {
                            pending = false;
                            injectStyle();
                            dismissOverlays();
                            ensureFloatingHud();
                        });
                    });
                    observer.observe(document.documentElement, { childList: true, subtree: true });
                };

                if (document.documentElement) {
                    attachObserver();
                } else {
                    document.addEventListener('DOMContentLoaded', attachObserver, { once: true });
                }
            }
        })();
        """;

    private const string EnterEmbeddedPlayerFullscreenScript = """
        (async () => {
            if (document.fullscreenElement) return 'already';

            const fsBtn = document.querySelector('.ytp-fullscreen-button');
            if (fsBtn && fsBtn.getAttribute('aria-pressed') !== 'true') {
                try {
                    fsBtn.click();
                } catch {}
                await new Promise(resolve => setTimeout(resolve, 120));
                if (document.fullscreenElement) return 'button';
            }

            const player = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
            if (player && typeof player.requestFullscreen === 'function') {
                try {
                    await player.requestFullscreen();
                    if (document.fullscreenElement) return 'player';
                } catch {}
            }

            const video = document.querySelector('video');
            if (video && typeof video.requestFullscreen === 'function') {
                try {
                    await video.requestFullscreen();
                    if (document.fullscreenElement) return 'video';
                } catch {}
            }

            return 'fallback';
        })()
        """;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private bool _isInitialized;

    /// <summary>The one WebView2 start: a second caller during the first start waits for it (no second environment).</summary>
    private Task? _initTask;

    /// <summary>A sync nudge (the +/- buttons): the new HEOS offset in ms, so the next track keeps it.</summary>
    public event Action<int>? SyncOffsetChanged;

    /// <summary>The title bar height in the current skin (42 in the Default skin).</summary>
    private static double TitleBarHeight => SkinMetrics.Scale(42, SkinMetrics.Current.SizeScale);
    private bool _allowClose;
    private bool _keepVideoMuted = true;
    private bool _isFullscreen;
    private bool _barsHidden;
    private double _restoreWidth = 960;
    private double _restoreHeight = 600;
    private double _restoreLeft = double.NaN;
    private double _restoreTop = double.NaN;
    private string? _currentVideoId;
    private string? _currentWatchUrl;
    private int _syncOffsetMs = 1800;
    private Func<int, double>? _positionProvider;
    private CancellationTokenSource? _syncWaitCts;

    public YouTubeCinemaWindow()
    {
        InitializeComponent();
        ApplyDwmStyling();
        CinemaWindowChrome.CaptionHeight = TitleBarHeight;
        SkinMetrics.Current.PropertyChanged += (_, _) =>
        {
            if (!_barsHidden && !_isFullscreen)
            {
                CinemaWindowChrome.CaptionHeight = TitleBarHeight;
            }
        };
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
        // Built from the video id, never the track's own address: a track can come from a phone.
        var baseUrl = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(track.VideoId)}";

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

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

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

        _initTask ??= InitializeWebViewAsync();
        await _initTask;
        if (!_isInitialized)
        {
            _initTask = null; // It failed: the next call tries again.
        }
    }

    private async Task InitializeWebViewAsync()
    {
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
            VideoWebView.CoreWebView2.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
            VideoWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            VideoWebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            VideoWebView.NavigationCompleted += OnNavigationCompleted;
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            LoadingOverlay.Text = $"WebView2 initialization failed: {ex.Message}\nClick 'Open Browser' to watch externally.";
            LoadingOverlay.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Only web pages: a file, a network share or an app link (ms-*, search-ms) is never opened here.</summary>
    internal static bool IsWebAddress(string? uri) =>
        uri is not null &&
        (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
         (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
          (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)));

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsWebAddress(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!IsWebAddress(e.Uri))
        {
            e.Handled = true; // Blocked.
        }
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        // Intentionally do NOT maximize the WPF desktop window when YouTube's embedded player enters
        // HTML5 fullscreen — WebView2 automatically expands the video player to fill the Cinema viewport
        // while keeping the desktop window at its normal resizable size.
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        var source = VideoWebView.CoreWebView2.Source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source) || source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;

        try
        {
            await VideoWebView.ExecuteScriptAsync(PureVideoModeScript);
            await VideoWebView.ExecuteScriptAsync($"window.__denonSetBarsHidden?.({(_barsHidden ? "true" : "false")});");
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
                    await VideoWebView.ExecuteScriptAsync(PureVideoModeScript);
                    await ApplyLiveSyncPositionAsync();
                    await EnterEmbeddedPlayerFullscreenAsync(cts.Token);
                    return;
                }

                await Task.Delay(180, cts.Token);
            }

            await VideoWebView.ExecuteScriptAsync(PureVideoModeScript);
            await ApplyLiveSyncPositionAsync();
            await EnterEmbeddedPlayerFullscreenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task EnterEmbeddedPlayerFullscreenAsync(CancellationToken cancellationToken)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var cdpParams = JsonSerializer.Serialize(new
        {
            expression = EnterEmbeddedPlayerFullscreenScript,
            userGesture = true,
            awaitPromise = true,
            returnByValue = true,
        });

        try
        {
            await VideoWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", cdpParams);

            // Retry once briefly in case YouTube's Polymer player mounted the fullscreen button slightly after readyState >= 1
            if (!VideoWebView.CoreWebView2.ContainsFullScreenElement && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);
                if (_isInitialized && VideoWebView.CoreWebView2 is not null && !VideoWebView.CoreWebView2.ContainsFullScreenElement)
                {
                    await VideoWebView.ExecuteScriptAsync(PureVideoModeScript);
                    await VideoWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", cdpParams);
                }
            }
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
        SyncOffsetChanged?.Invoke(_syncOffsetMs);

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
            case "SHOW_BARS":
                SetBarsHidden(false);
                break;
            case "CLOSE_WINDOW":
                HideAndStopVideo();
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
        if (fullscreen == _isFullscreen)
        {
            return;
        }

        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            if (WindowState == WindowState.Normal)
            {
                _restoreWidth = ActualWidth > 200 ? ActualWidth : Width;
                _restoreHeight = ActualHeight > 200 ? ActualHeight : Height;
                _restoreLeft = Left;
                _restoreTop = Top;
            }

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
            CinemaWindowChrome.CaptionHeight = TitleBarHeight;
            CinemaWindowChrome.ResizeBorderThickness = new Thickness(6);
            RootBorder.BorderThickness = new Thickness(1);
            Width = _restoreWidth > 200 ? _restoreWidth : 960;
            Height = _restoreHeight > 200 ? _restoreHeight : 600;
            if (!double.IsNaN(_restoreLeft) && !double.IsNaN(_restoreTop))
            {
                Left = _restoreLeft;
                Top = _restoreTop;
            }
            FullscreenButton.Content = "Fullscreen (F11)";
        }
    }

    private void SetBarsHidden(bool hidden)
    {
        _barsHidden = hidden;
        TitleBarBorder.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (hidden)
        {
            TitleBarRow.Height = new GridLength(0);
        }
        else
        {
            SkinBinding.Size(TitleBarRow, System.Windows.Controls.RowDefinition.HeightProperty, 42);
        }

        BottomSyncBar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        CinemaWindowChrome.CaptionHeight = (hidden || _isFullscreen) ? 0 : TitleBarHeight;
        HideBarsButton.Content = hidden ? "Show Bars (H)" : "Hide Bars (H)";

        if (_isInitialized && VideoWebView.CoreWebView2 is not null)
        {
            _ = VideoWebView.ExecuteScriptAsync($"window.__denonSetBarsHidden?.({(hidden ? "true" : "false")});");
            if (hidden)
            {
                VideoWebView.Focus();
            }
        }
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
        if (string.IsNullOrWhiteSpace(_currentWatchUrl) || !IsWebAddress(_currentWatchUrl))
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
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        // Hide instead of permanently destroying so Cinema Companion reopens instantaneously
        e.Cancel = true;
        HideAndStopVideo();
    }

    public void ForceCloseForExit()
    {
        _allowClose = true;
        HideAndStopVideo();
        try { Close(); } catch { }
    }

    public void HideAndStopVideo()
    {
        _syncWaitCts?.Cancel();
        SetBarsHidden(false);
        if (_isFullscreen)
        {
            SetFullscreen(false);
        }
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

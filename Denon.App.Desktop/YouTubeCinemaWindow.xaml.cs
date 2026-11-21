using System.ComponentModel;
using System.Diagnostics;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private bool _isInitialized;
    private bool _keepVideoMuted = true;
    private string? _currentVideoId;
    private string? _currentWatchUrl;
    private int _syncOffsetMs = 1800;

    public YouTubeCinemaWindow()
    {
        InitializeComponent();
        ApplyDwmStyling();
    }

    public async Task LoadTrackAsync(YouTubeTrackItem track, int syncOffsetMs = 1800)
    {
        _currentVideoId = track.VideoId;
        _currentWatchUrl = string.IsNullOrWhiteSpace(track.WatchUrl)
            ? $"https://www.youtube.com/watch?v={track.VideoId}"
            : track.WatchUrl;
        _syncOffsetMs = Math.Clamp(syncOffsetMs, 0, 8000);

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

        LoadingOverlay.Visibility = Visibility.Visible;
        VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;
        VideoWebView.CoreWebView2.Navigate(_currentWatchUrl);
        UpdateSyncStatus();
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
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        VideoWebView.CoreWebView2.IsMuted = _keepVideoMuted;

        // Cleanly apply mute state and nudge video slightly if Denon HEOS buffer delay is active
        var mutedLiteral = _keepVideoMuted ? "true" : "false";
        var script = $$"""
            (() => {
                const v = document.querySelector('video');
                if (v) {
                    v.muted = {{mutedLiteral}};
                    if (v.paused) { v.play().catch(() => {}); }
                }
            })();
            """;

        try
        {
            await VideoWebView.ExecuteScriptAsync(script);
        }
        catch
        {
            // Ignore transient script injection errors
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

    private Task NudgeVideoSecondsAsync(double deltaSeconds)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        var deltaStr = deltaSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        SyncStatusText.Text = $"Nudged video {(deltaSeconds >= 0 ? "+" : "")}{(int)(deltaSeconds * 1000)} ms";
        return VideoWebView.ExecuteScriptAsync($"(() => {{ const v = document.querySelector('video'); if (v) v.currentTime = Math.max(0, v.currentTime + ({deltaStr})); }})();");
    }

    private async void NudgeMinus500_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(-0.5);

    private async void NudgeMinus100_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(-0.1);

    private async void NudgePlus100_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(0.1);

    private async void NudgePlus500_Click(object sender, RoutedEventArgs e) => await NudgeVideoSecondsAsync(0.5);

    private async void RestartVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || VideoWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await VideoWebView.ExecuteScriptAsync("(() => { const v = document.querySelector('video'); if (v) { v.currentTime = 0; v.play().catch(() => {}); } })();");
            SyncStatusText.Text = "Video restarted to 0:00";
        }
        catch
        {
        }
    }

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

    private void UpdateSyncStatus()
    {
        SyncStatusText.Text = _keepVideoMuted
            ? $"Audio routed to Denon AVR • Video muted (HEOS offset ~{_syncOffsetMs}ms)"
            : "Video audio unmuted on PC / HDMI";
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

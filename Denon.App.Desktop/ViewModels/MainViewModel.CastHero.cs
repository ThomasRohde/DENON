using Denon.App.Core.Infrastructure;
using Denon.App.Core.Services;

namespace Denon.App.Desktop.ViewModels;

/// <summary>
/// Now-playing hero while it shows a Chromecast or Apple TV program (TV Audio + TV on HDMI): its artwork and a
/// play/pause button that controls that streamer (through its plugin) instead of HEOS.
/// </summary>
public sealed partial class MainViewModel
{
    private CastNowPlaying? _heroCast;
    private string? _castArtKey;
    private string _castArtUrl = string.Empty;
    private DateTimeOffset _castArtFetchedUtc = DateTimeOffset.MinValue;
    private bool _isTransportPlaying;
    private bool _canPauseTransport = true;

    /// <summary>Play/pause icon state: the cast's state while the hero shows a cast, else the HEOS state.</summary>
    public bool IsTransportPlaying
    {
        get => _isTransportPlaying;
        private set
        {
            if (SetProperty(ref _isTransportPlaying, value))
            {
                PlayPauseCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PlayPauseToolTip));
            }
        }
    }

    /// <summary>False when the active streamer (for example live DRTV on Apple TV) reports pause unsupported.</summary>
    public bool CanPauseTransport
    {
        get => _canPauseTransport;
        private set
        {
            if (SetProperty(ref _canPauseTransport, value))
            {
                PlayPauseCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PlayPauseToolTip));
            }
        }
    }

    public string PlayPauseToolTip =>
        IsTransportPlaying && !CanPauseTransport
            ? "Pause is not supported for this live stream"
            : "Play / Pause (Space)";

    /// <summary>
    /// Remembers the cast the hero shows, sets the transport state, and adds the cast artwork when it is known.
    /// Starts an artwork lookup when the program changes.
    /// </summary>
    private ResolvedNowPlaying ApplyHeroCast(ResolvedNowPlaying resolved)
    {
        _heroCast = resolved.Cast;
        IsTransportPlaying = resolved.Cast?.IsPlaying ?? IsPlaying;
        CanPauseTransport = resolved.CanPause;

        if (resolved.Cast is not { } cast)
        {
            return resolved;
        }

        var key = $"{cast.PluginId}|{cast.DeviceName}|{cast.App}|{cast.Title}";
        var isNewProgram = !string.Equals(key, _castArtKey, StringComparison.Ordinal);
        if (isNewProgram)
        {
            _castArtKey = key;
            _castArtUrl = string.Empty;
        }

        // Fetch on a new program; retry a missing artwork at most every 15 s (it can arrive after the title: the
        // Chromecast sends it later, the Apple TV plugin fetches it from the Apple TV first).
        if (isNewProgram ||
            (_castArtUrl.Length == 0 && DateTimeOffset.UtcNow - _castArtFetchedUtc > TimeSpan.FromSeconds(15)))
        {
            _castArtFetchedUtc = DateTimeOffset.UtcNow;
            _ = FetchCastArtworkAsync(key, cast.PluginId, cast.DeviceName);
        }

        return _castArtUrl.Length > 0 ? resolved with { ImageUrl = _castArtUrl } : resolved;
    }

    private async Task FetchCastArtworkAsync(string key, string pluginId, string deviceName)
    {
        try
        {
            var result = await _pluginHost.ExecuteActionAsync(
                pluginId,
                "now-playing",
                new Dictionary<string, string> { ["device"] = deviceName, ["includeImage"] = "false" });

            var url = result.Success
                ? NowPlayingResolver.TryReadCastImageUrl(result.DataJson, AppDataPaths.GetPluginDataDirectory(pluginId))
                : null;
            if (url is null)
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (!string.Equals(_castArtKey, key, StringComparison.Ordinal))
                {
                    return; // The program changed while we waited.
                }

                _castArtUrl = url;
                if (_heroCast is not null)
                {
                    NowPlayingImageUrl = url;
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logStore.Warning($"Could not read the cast artwork: {ex.Message}");
        }
    }

    /// <summary>Play/pause for the Chromecast or Apple TV the hero shows (explicit play or pause, from the known state).</summary>
    private async Task PlayPauseCastAsync(CastNowPlaying cast)
    {
        if (cast.IsPlaying && !cast.CanPause)
        {
            StatusMessage = $"Pause is not supported for {cast.Title ?? cast.App} on {cast.DeviceName}.";
            return;
        }

        var action = cast.IsPlaying ? "pause" : "play";
        try
        {
            var result = await _pluginHost.ExecuteActionAsync(
                cast.PluginId,
                action,
                new Dictionary<string, string> { ["device"] = cast.DeviceName });

            StatusMessage = result.Success
                ? $"{(cast.IsPlaying ? "Paused" : "Resumed")} {cast.Title ?? cast.App} on {cast.DeviceName}."
                : $"Could not {action} {cast.DeviceName}: {result.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not {action} {cast.DeviceName}: {ex.Message}";
        }
    }
}

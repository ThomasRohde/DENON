using System.Security.Cryptography;
using System.Text;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.HomePod;

internal enum HomePodLink
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>What one speaker does now.</summary>
internal sealed record HomePodState(string SpeakerId)
{
    public HomePodLink Link { get; init; } = HomePodLink.Disconnected;

    public NowPlayingSnapshot NowPlaying { get; init; } = NowPlayingSnapshot.Empty;

    public string? Error { get; init; }

    /// <summary>The artwork of what plays, cached in the plugin data folder; null until fetched or without artwork.</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>The program the artwork belongs to (<see cref="NowPlayingSnapshot.ArtworkKey"/>).</summary>
    public string? ArtworkKey { get; init; }

    /// <summary>
    /// Music, a podcast or an AirPlay stream. System sounds (Siri, timers, alarms: <c>com.apple.SoundBoard</c>)
    /// and the empty AirPlay player are not media.
    /// </summary>
    public bool HasMedia =>
        Link == HomePodLink.Connected && NowPlaying.HasMedia &&
        NowPlaying.AppBundleId is { Length: > 0 } bundle && !HomePodApps.IsSystem(bundle);

    public bool IsPlaying => HasMedia && NowPlaying.State == MrpPlaybackState.Playing;

    /// <summary>"Apple Music", "Podcasts", "AirMusic (AirPlay)" or "AirPlay".</summary>
    public string? AppName => HasMedia ? HomePodApps.DisplayName(NowPlaying.AppBundleId, NowPlaying.AppName) : null;
}

/// <summary>Names for the apps a HomePod reports.</summary>
internal static class HomePodApps
{
    public const string AirPlayBundle = "com.apple.tvairplayd";

    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.apple.Music"] = "Apple Music",
        ["com.apple.podcasts"] = "Podcasts",
        ["com.apple.iBooks"] = "Books",
        [AirPlayBundle] = "AirPlay",
    };

    public static bool IsSystem(string bundle) =>
        bundle.Equals("com.apple.SoundBoard", StringComparison.OrdinalIgnoreCase) ||
        bundle.Equals("com.apple.siri", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The app to show. An AirPlay stream names the sending app when the HomePod knows it ("AirMusic (AirPlay)");
    /// other apps use their known name, the name the HomePod gives, or the bundle id.
    /// </summary>
    public static string DisplayName(string? bundle, string? reportedName)
    {
        var name = string.IsNullOrWhiteSpace(reportedName) ? null : reportedName.Trim();
        if (string.Equals(bundle, AirPlayBundle, StringComparison.OrdinalIgnoreCase))
        {
            return name is null || name.Equals("AirPlay", StringComparison.OrdinalIgnoreCase) ? "AirPlay" : $"{name} (AirPlay)";
        }

        return bundle is not null && Known.TryGetValue(bundle, out var known) ? known : name ?? bundle ?? "HomePod";
    }
}

/// <summary>
/// One speaker's connection. HomePods have no screen and so no PIN: each connection pairs transiently
/// (nothing is stored) and opens the AirPlay 2 remote-control channel that carries MRP. The HomePod pushes
/// now-playing changes; the client reconnects with back-off (5 s, doubling to 60 s) when the link drops.
/// </summary>
internal sealed class HomePodClient : IAsyncDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArtworkRetry = TimeSpan.FromMinutes(1);
    private const int ArtworkFilesKept = 40;

    private readonly string _host;
    private readonly int _port;
    private readonly CompanionIdentity _identity;
    private readonly string _artworkDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private MrpClient? _mrp;
    private Task? _loop;
    private string? _artworkKey;
    private DateTimeOffset _artworkRetryAt;

    public HomePodClient(string speakerId, string host, int port, CompanionIdentity identity, string artworkDirectory)
    {
        _host = host;
        _port = port;
        _identity = identity;
        _artworkDirectory = artworkDirectory;
        State = new HomePodState(speakerId);
    }

    public HomePodState State { get; private set; }

    public string Host => _host;

    /// <summary>Previous and new state.</summary>
    public event Action<HomePodState, HomePodState>? StateChanged;

    public event Action<string>? Diagnostic;

    public void Start() => _loop = Task.Run(RunAsync);

    public Task PlayAsync(CancellationToken ct) => CommandAsync(MrpCommand.Play, ct);

    public Task PauseAsync(CancellationToken ct) => CommandAsync(MrpCommand.Pause, ct);

    public Task PlayPauseAsync(CancellationToken ct) => CommandAsync(MrpCommand.TogglePlayPause, ct);

    public Task NextAsync(CancellationToken ct) => CommandAsync(MrpCommand.NextTrack, ct);

    public Task PreviousAsync(CancellationToken ct) => CommandAsync(MrpCommand.PreviousTrack, ct);

    private Task CommandAsync(MrpCommand command, CancellationToken ct)
    {
        MrpClient? mrp;
        lock (_sync)
        {
            mrp = _mrp;
        }

        return mrp is null
            ? throw new InvalidOperationException("The HomePod is not connected yet. Try again in a moment.")
            : mrp.SendCommandAsync(command, ct);
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        var retry = FirstRetry;
        while (!ct.IsCancellationRequested)
        {
            MrpClient? mrp = null;
            try
            {
                SetState(s => s with { Link = HomePodLink.Connecting });
                mrp = await MrpClient.ConnectTransientAsync(_host, _port, _identity, ct);
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                mrp.Closed += ex => closed.TrySetResult(ex);
                var linked = mrp;
                mrp.NowPlayingChanged += snapshot =>
                {
                    SetState(s => s with { NowPlaying = snapshot });
                    RequestArtwork(linked, snapshot);
                };
                lock (_sync)
                {
                    _mrp = mrp;
                }

                SetState(s => s with { Link = HomePodLink.Connected, NowPlaying = mrp.NowPlaying.Current, Error = null });
                RequestArtwork(mrp, mrp.NowPlaying.Current);
                Diagnostic?.Invoke($"Linked to {_host} ({mrp.DeviceInfo?.String(2) ?? "HomePod"}, build {mrp.DeviceInfo?.String(4) ?? "?"}).");
                retry = FirstRetry;

                var reason = await closed.Task.WaitAsync(ct);
                Diagnostic?.Invoke($"Link to {_host} closed{(reason is null ? "" : $": {reason.Message}")}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(s => s with { Error = ex.Message });
                Diagnostic?.Invoke($"Link to {_host} failed (retry in {retry.TotalSeconds:0} s): {ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _mrp = null;
                }

                if (mrp is not null)
                {
                    try
                    {
                        await mrp.DisposeAsync();
                    }
                    catch
                    {
                    }
                }

                SetState(s => s with { Link = HomePodLink.Disconnected, NowPlaying = NowPlayingSnapshot.Empty });
            }

            try
            {
                await Task.Delay(retry, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retry = TimeSpan.FromSeconds(Math.Min(MaxRetry.TotalSeconds, retry.TotalSeconds * 2));
        }
    }

    /// <summary>
    /// Gets the artwork of what plays when the HomePod has one: from the disk cache, else with a
    /// PLAYBACK_QUEUE_REQUEST in the background (as the Apple TV plugin does). One request per program; a failed one
    /// is retried after a minute.
    /// </summary>
    private void RequestArtwork(MrpClient mrp, NowPlayingSnapshot snapshot)
    {
        if (!snapshot.ArtworkAvailable || snapshot.ArtworkKey is not { } key || State.ArtworkKey == key)
        {
            return;
        }

        lock (_sync)
        {
            if (_artworkKey == key && DateTimeOffset.UtcNow < _artworkRetryAt)
            {
                return;
            }

            _artworkKey = key;
            _artworkRetryAt = DateTimeOffset.UtcNow + ArtworkRetry;
        }

        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var cached = Directory.Exists(_artworkDirectory)
            ? Directory.EnumerateFiles(_artworkDirectory, name + ".*").FirstOrDefault()
            : null;
        if (cached is not null)
        {
            SetState(s => s with { ArtworkKey = key, ArtworkPath = cached });
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var artwork = await mrp.RequestPlaybackQueueAsync(snapshot.QueueLocation, _cts.Token);
                if (artwork is null)
                {
                    return;
                }

                Directory.CreateDirectory(_artworkDirectory);
                var path = Path.Combine(_artworkDirectory, name + artwork.Extension);
                await File.WriteAllBytesAsync(path, artwork.Data, _cts.Token);
                PruneArtwork();
                SetState(s => s with { ArtworkKey = key, ArtworkPath = path });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"Artwork not fetched (retry in {ArtworkRetry.TotalSeconds:0} s): {ex.Message}");
            }
        });
    }

    /// <summary>Keeps the newest artwork files only (the folder is shared by all speakers).</summary>
    private void PruneArtwork()
    {
        try
        {
            foreach (var old in new DirectoryInfo(_artworkDirectory).EnumerateFiles()
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(ArtworkFilesKept))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetState(Func<HomePodState, HomePodState> change)
    {
        HomePodState previous, next;
        lock (_sync)
        {
            previous = State;
            next = change(previous);
            if (next == previous)
            {
                return;
            }

            State = next;
        }

        StateChanged?.Invoke(previous, next);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }
}

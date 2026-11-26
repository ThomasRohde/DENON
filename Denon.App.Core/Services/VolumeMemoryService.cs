using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Models;
using Denon.App.Core.Plugins;
using Microsoft.Extensions.Hosting;

namespace Denon.App.Core.Services;

/// <summary>
/// Volume memory. Learns the listening volume of each source (a live TV channel, a TV or streaming app, a receiver
/// input) from the time spent at each volume, and when the source changes, sets the learned volume: down at once, up
/// in a short fade, never above the safe volume. It also publishes <c>source.changed</c> for automations.
/// </summary>
public sealed class VolumeMemoryService : BackgroundService
{
    /// <summary>A new source must hold this long before it counts (flipping through channels does nothing).</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(5);

    /// <summary>The first seconds of a source are not learned: the user may still be setting the volume.</summary>
    public static readonly TimeSpan LearnDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan LearnEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SeedFrom = TimeSpan.FromDays(30);
    private static readonly TimeSpan SeedMinimum = TimeSpan.FromMinutes(10);

    /// <summary>Changes made by these are left alone: a scene, an automation, an agent or a plugin set its own volume.</summary>
    private static readonly HashSet<string> OrchestratedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        AutomationTriggers.AutomationOrigin, "smart_scene", "mcp_agent", "plugin",
    };

    public const string Origin = "volume_memory";
    public const string SourceChangedEvent = "source.changed";

    private readonly AppSettingsStore _settings;
    private readonly ReceiverStateStore _state;
    private readonly ReceiverController _receiver;
    private readonly PluginHostService _plugins;
    private readonly EventTelemetryLog _telemetry;
    private readonly VolumeMemoryStore _store;
    private readonly AppLogStore _log;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    // The source as it is now (after the dwell), and a new one waiting out the dwell.
    private ListeningSource? _current;
    private DateTimeOffset _currentSince;
    private ListeningSource? _candidate;
    private bool _hasCandidate;
    private DateTimeOffset _candidateSince;
    private double _candidateVolume;
    private string _candidateOrigin = "external";
    private bool _sawStandby;
    private bool _committedOnce;
    private DateTimeOffset _lastLearnAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSaveAt = DateTimeOffset.MinValue;

    public VolumeMemoryService(
        AppSettingsStore settings,
        ReceiverStateStore state,
        ReceiverController receiver,
        PluginHostService plugins,
        EventTelemetryLog telemetry,
        VolumeMemoryStore store,
        AppLogStore log)
    {
        _settings = settings;
        _state = state;
        _receiver = receiver;
        _plugins = plugins;
        _telemetry = telemetry;
        _store = store;
        _log = log;
    }

    /// <summary>Tests: sets the receiver volume instead of the real receiver (level, log the step).</summary>
    internal Func<int, bool, CancellationToken, Task>? SetVolumeOverride { get; set; }

    /// <summary>Tests: the fade waits this long between steps.</summary>
    internal TimeSpan FadeStepDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>A short note for the user ("Volume 42 for DR1 (learned)") after Volume memory set the volume.</summary>
    public event Action<string>? Applied;

    /// <summary>Raised when <see cref="CurrentSource"/> changed (after the dwell), standby included.</summary>
    public event Action? SourceChanged;

    /// <summary>The source after the dwell, or null while the receiver sleeps.</summary>
    public ListeningSource? CurrentSource => _current;

    public VolumeMemoryStore Store => _store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Load();
        if (_store.IsNew)
        {
            SeedFromLogs();
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var snapshot = _state.Snapshot;
                    await TickAsync(DateTimeOffset.UtcNow, snapshot.IsConnected ? snapshot.Status : null, _plugins.GetPlugins(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warning($"[Volume memory] {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _store.SaveIfChanged();
        }
    }

    private void SeedFromLogs()
    {
        try
        {
            var ends = _telemetry.ReadEventsSince(EventTelemetryLog.StreamMedia, DateTimeOffset.Now - SeedFrom, "session.ended");
            var added = _store.Seed(ends, SeedMinimum, DateTimeOffset.UtcNow);
            _store.SaveIfChanged();
            _log.Info($"[Volume memory] Learned {added} source volume(s) from the last {SeedFrom.TotalDays:0} days of logs.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warning($"[Volume memory] Could not read the logs to start from: {ex.Message}");
        }
    }

    /// <summary>One second of Volume memory: notice a source change, set its volume after the dwell, and learn.</summary>
    internal async Task TickAsync(DateTimeOffset now, ReceiverStatus? status, IReadOnlyList<PluginRecordSnapshot>? plugins, CancellationToken ct)
    {
        if (status is null)
        {
            return; // Not connected: nothing is known, nothing changes.
        }

        var source = ListeningSource.From(status, plugins);
        var volume = Volume(status);
        if (source is null)
        {
            _sawStandby = true;
        }

        if (!_hasCandidate || !SameSource(source, _candidate))
        {
            _hasCandidate = true;
            _candidate = source;
            _candidateSince = now;
            _candidateVolume = volume;
            // Who changed it, read now: the mark lasts a few seconds, the dwell is longer.
            _candidateOrigin = _telemetry.ResolveOrigin("avr", fallback: _telemetry.ResolveOrigin("tv"));
        }

        if ((!_committedOnce || !SameSource(_candidate, _current)) && now - _candidateSince >= Dwell)
        {
            var from = _current;
            var wasStartup = !_committedOnce;
            _committedOnce = true;
            _current = _candidate;
            _currentSince = now;
            _lastLearnAt = now;
            SourceChanged?.Invoke();
            if (_current is { } to && !(wasStartup && from is null && !_sawStandby))
            {
                PublishSourceChanged(from, to);
                await MaybeApplyAsync(to, status, volume, ct);
            }
        }

        Learn(now, status, volume, ListeningSource.IsPlaying(status, plugins));

        if (now - _lastSaveAt >= SaveEvery)
        {
            _lastSaveAt = now;
            _store.SaveIfChanged();
        }
    }

    /// <summary>
    /// Learns while the source plays and is heard. Paused, stopped or muted time does not count (the volume set for
    /// a paused stream overnight is not a listening level).
    /// </summary>
    private void Learn(DateTimeOffset now, ReceiverStatus status, double volume, bool playing)
    {
        if (_current is null || status.IsMuted || !playing || !SameSource(_current, _candidate) || now - _currentSince < LearnDelay)
        {
            _lastLearnAt = now;
            return;
        }

        var elapsed = now - _lastLearnAt;
        if (elapsed < LearnEvery)
        {
            return;
        }

        _lastLearnAt = now;
        _store.Learn(_current, volume, Math.Min(elapsed.TotalSeconds, 60), now);
    }

    private void PublishSourceChanged(ListeningSource? from, ListeningSource to)
    {
        var origin = _candidateOrigin == Origin ? "external" : _candidateOrigin;
        _telemetry.Record(EventTelemetryLog.StreamControl, "source", SourceChangedEvent, origin, new Dictionary<string, object?>
        {
            ["source"] = to.Label,
            ["kind"] = to.Kind,
            ["key"] = to.Key,
            ["from"] = from?.Label,
        });
    }

    /// <summary>Why Volume memory leaves the volume alone, or null to set <paramref name="target"/>.</summary>
    internal static string? Decide(
        VolumeMemorySettings settings, LearnedVolume? learned, double volume, double volumeAtSwitch, bool muted,
        string switchOrigin, int safeVolume, out int target)
    {
        target = 0;
        if (!settings.Enabled)
        {
            return "off";
        }

        if (learned is null)
        {
            return "nothing learned yet";
        }

        if (muted)
        {
            return "muted";
        }

        if (OrchestratedOrigins.Contains(switchOrigin))
        {
            return $"the {switchOrigin.Replace('_', ' ')} set the source and its volume";
        }

        if (Math.Abs(volume - volumeAtSwitch) >= 1)
        {
            return "the volume was changed after the switch";
        }

        target = (int)Math.Clamp(Math.Round(learned.Level), 0, safeVolume);
        var difference = target - (int)Math.Round(volume);
        if (Math.Abs(difference) < Math.Max(1, settings.MinDifference))
        {
            return "already close";
        }

        return settings.OnlyLower && difference > 0 ? "only turning down" : null;
    }

    private async Task MaybeApplyAsync(ListeningSource source, ReceiverStatus status, double volume, CancellationToken ct)
    {
        var settings = _settings.Current;
        var why = Decide(settings.VolumeMemory, _store.Find(source.Key), volume, _candidateVolume, status.IsMuted,
            _candidateOrigin, SafeVolume(settings), out var target);
        if (why is not null)
        {
            return;
        }

        if (!await SetLevelAsync(target, volume, Origin, source, ct))
        {
            _log.Info($"[Volume memory] {source.Label}: stopped the fade to {target}, the volume or the source changed.");
            return;
        }

        var message = $"Volume {target} for {source.Label} (learned)";
        _log.Info($"[Volume memory] {source.Label}: {Math.Round(volume)} → {target}.");
        Applied?.Invoke(message);
    }

    /// <summary>Down at once (no shock); up in steps of 2 over a moment.</summary>
    /// <summary>
    /// Sets <paramref name="target"/>. Returns false when an upward fade stopped because the user changed the volume or
    /// the source changed between its steps: their change wins.
    /// </summary>
    private async Task<bool> SetLevelAsync(int target, double from, string origin, ListeningSource source, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct);
        try
        {
            // Read the receiver again: another adjustment (the automation action and the switch at the same time)
            // may have run while this one waited, or the source may have changed.
            var snapshot = _state.Snapshot;
            if (snapshot.IsConnected && snapshot.Status is { } now)
            {
                if (!SameSource(ListeningSource.From(now, _plugins.GetPlugins()), source))
                {
                    return false;
                }

                from = Volume(now);
            }

            var current = (int)Math.Round(from);
            if (current == target)
            {
                return true; // Already there.
            }

            _telemetry.MarkOrigin("avr", origin);
            if (target > current)
            {
                var sent = current;
                for (var step = current + 2; step < target; step += 2)
                {
                    await SetVolumeAsync(step, log: false, ct);
                    sent = step;
                    _telemetry.MarkOrigin("avr", origin);
                    await Task.Delay(FadeStepDelay, ct);
                    if (Superseded(source, sent))
                    {
                        return false;
                    }
                }
            }

            await SetVolumeAsync(target, log: true, ct);
            return true;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// True when, during a fade, the receiver's volume is no longer where the fade put it (the user turned it), or what
    /// plays is no longer <paramref name="source"/>. The reading may lag one step behind, so 2 steps of slack.
    /// </summary>
    private bool Superseded(ListeningSource source, int sent)
    {
        var snapshot = _state.Snapshot;
        if (!snapshot.IsConnected || snapshot.Status is not { } status)
        {
            return false;
        }

        var volume = Volume(status);
        return volume < sent - 2.5 || volume > sent + 2.5 ||
               !SameSource(ListeningSource.From(status, _plugins.GetPlugins()), source);
    }

    private Task SetVolumeAsync(int level, bool log, CancellationToken ct) =>
        SetVolumeOverride is { } set ? set(level, log, ct) : _receiver.SetVolumeAsync(level, ct, logSuccess: log);

    /// <summary>
    /// The automation action "Set learned volume": the learned volume of what plays now, capped at the safe volume.
    /// Ignores the on/off switch and the minimum difference (the automation decides when). <paramref name="origin"/> is
    /// who asked ("automation"): the volume change is theirs, so it cannot start an automation again.
    /// </summary>
    public async Task<string> ApplyLearnedNowAsync(string origin, CancellationToken ct)
    {
        var snapshot = _state.Snapshot;
        var status = snapshot.IsConnected ? snapshot.Status : null;
        var source = ListeningSource.From(status, _plugins.GetPlugins())
                     ?? throw new InvalidOperationException("The receiver is in standby: no source to set a volume for.");
        if (_store.Find(source.Key) is not { } learned)
        {
            return $"No learned volume for {source.Label} yet";
        }

        var target = (int)Math.Clamp(Math.Round(learned.Level), 0, SafeVolume(_settings.Current));
        return await SetLevelAsync(target, Volume(status!), origin, source, ct)
            ? $"Volume {target} for {source.Label} (learned)"
            : $"Stopped the fade to {target} for {source.Label}: the volume or the source changed";
    }

    private static int SafeVolume(AppSettings settings) => Math.Clamp(settings.Mcp.MaxSafeVolume, 15, 98);

    private static double Volume(ReceiverStatus status) => status.VolumeExact > 0.01 ? status.VolumeExact : status.Volume;

    private static bool SameSource(ListeningSource? a, ListeningSource? b) =>
        string.Equals(a?.Key, b?.Key, StringComparison.OrdinalIgnoreCase);

    public override void Dispose()
    {
        _applyGate.Dispose();
        base.Dispose();
    }

    public static string Describe(LearnedVolume level) =>
        $"{level.Label}: {Math.Round(level.Level).ToString(CultureInfo.InvariantCulture)} ({DenonVolumeMath.FormatDecibels((int)Math.Round(level.Level))})";
}

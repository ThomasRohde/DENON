using Denon.App.PluginSdk;
using Denon.Plugin.AppleTv.Protocol.Companion;
using Denon.Plugin.AppleTv.Protocol.Mrp;

namespace Denon.Plugin.AppleTv;

/// <summary>
/// Turns live Apple TV state into habit-ready watch sessions on the <c>media</c> stream, with the same
/// event names and keys as the host's AVR/LG TV sessions and the Chromecast plugin
/// (<c>session.started</c> / <c>session.ended</c>, kind, title, source, durationSec, watchedSec, reason, avgVolume).
///
/// Rules (as the Chromecast watch log):
/// - One session per program (app + title). A new title closes the old session ("switched").
/// - The 15 s dwell starts at the first Playing; a program that never plays is not logged.
/// - <c>watchedSec</c> counts only Playing time; <c>durationSec</c> is wall-clock time.
/// - Paused for <see cref="PauseTimeout"/> ends the session ("paused"); playing again starts a new one.
/// - One <c>appletv.app_ended</c> summary per app run, with the titles seen.
/// - End reasons: "switched", "app_closed", "standby" (Apple TV sleeps), "disconnected", "plugin_stopped",
///   "app_shutdown", "device_disabled".
/// </summary>
internal sealed class AppleTvWatchLog
{
    public const int DwellSeconds = 15;

    public static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Grace window after an app closes while the Apple TV is still awake: pressing the remote's power button
    /// stops playback ~30 s before tvOS reports <see cref="AttentionState.Asleep"/>, so deferring emission
    /// briefly lets those sessions finalize with <c>"standby"</c> instead of <c>"app_closed"</c>.
    /// </summary>
    public static readonly TimeSpan StandbyGracePeriod = TimeSpan.FromSeconds(35);

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, Dictionary<string, object?>> _record;
    private readonly TimeSpan _standbyGracePeriod;

    public AppleTvWatchLog(Action<string, Dictionary<string, object?>> record, TimeSpan? standbyGracePeriod = null)
    {
        _record = record;
        _standbyGracePeriod = standbyGracePeriod ?? StandbyGracePeriod;
    }

    /// <summary>Call on every device state change.</summary>
    public void Update(AppleTvDeviceConfig device, AppleTvDeviceState state, PluginReceiverSnapshot receiver, DateTimeOffset now)
    {
        lock (_gate)
        {
            var slot = GetSlot(device);
            slot.Accumulate(now);
            slot.IsPlaying = state.IsPlaying;

            var next = state.HasMedia ? BuildCandidate(state, receiver, now) : null;
            CheckPendingEnds(slot, state, next is not null, now);

            var current = slot.Active?.Candidate ?? slot.Pending;

            if (current is not null && next is not null && current.AppKey == next.AppKey)
            {
                if (current.Title == next.Title)
                {
                    current.Subtitle ??= next.Subtitle;
                    current.Season ??= next.Season;
                    current.Episode ??= next.Episode;
                    return;
                }

                // The title arrived late (or the app briefly showed none): keep the open session.
                if (current.Title is null || next.Title is null)
                {
                    if (next.Title is not null)
                    {
                        current.Title = next.Title;
                        current.Subtitle = next.Subtitle;
                        current.Season = next.Season;
                        current.Episode = next.Episode;
                        RememberTitle(slot, next.Title);
                    }

                    return;
                }
            }

            if (slot.Active is not null)
            {
                var reason = next is not null ? "switched" : EndReason(state);
                if (reason == "app_closed" && _standbyGracePeriod > TimeSpan.Zero)
                {
                    DeferSessionEnd(slot, now);
                }
                else
                {
                    EndSession(slot, now, reason);
                }
            }

            UpdateAppRun(slot, state, now, receiver);
            slot.Pending = next;
            if (next?.Title is not null)
            {
                RememberTitle(slot, next.Title);
            }
        }
    }

    /// <summary>Call every few seconds: confirms the dwell, flushes expired standby grace windows, samples volume and play time.</summary>
    public void Tick(Func<PluginReceiverSnapshot> receiver, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Accumulate(now);
                CheckPendingEnds(slot, state: null, hasNextMedia: false, now);

                if (slot.Active is null && slot.Pending is { WatchedSec: <= 0 } notStarted && !slot.IsPlaying)
                {
                    slot.Pending = notStarted with { StartedUtc = now };
                    continue;
                }

                // A paused candidate needs the full dwell of real play: a second of play then a pause is no session.
                if (slot.Active is null && slot.Pending is { } pending && (now - pending.StartedUtc).TotalSeconds >= DwellSeconds &&
                    (slot.IsPlaying || pending.WatchedSec >= DwellSeconds))
                {
                    if (pending.ReceiverInput is null && receiver() is { IsPoweredOn: true } rx)
                    {
                        pending = pending with { ReceiverInput = rx.InputSource, ReceiverSoundMode = rx.SoundMode };
                    }

                    slot.Pending = null;
                    slot.Active = new ActiveSession(pending);
                    var data = Payload(slot, pending);
                    data["dwellConfirmedSec"] = (int)Math.Round((now - pending.StartedUtc).TotalSeconds);
                    _record("session.started", data);
                }

                if (slot.Active is { } paused && !slot.IsPlaying && now - slot.LastPlayingUtc >= PauseTimeout)
                {
                    var program = paused.Candidate;
                    EndSession(slot, now, "paused");
                    slot.Pending = program with { StartedUtc = now, WatchedSec = 0 };
                    continue;
                }

                if (slot.Active is not null && receiver() is { IsPoweredOn: true, Volume: > 0 } r)
                {
                    slot.Active.AddVolume(r.Volume);
                }
            }
        }
    }

    /// <summary>Closes every open and deferred session (app shutdown, plugin stop, device removed or disabled).</summary>
    public void EndAll(DateTimeOffset now, string reason, string? deviceId = null)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values.Where(s => deviceId is null || s.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                slot.Accumulate(now);
                FlushPendingEnds(slot, "app_closed");
                if (slot.Active is not null)
                {
                    EndSession(slot, now, reason);
                }

                slot.Pending = null;
                EndAppRun(slot, now, reason);
            }
        }
    }

    /// <summary>"standby" when the Apple TV sleeps, "disconnected" when the link is down, else "app_closed".</summary>
    internal static string EndReason(AppleTvDeviceState state) =>
        state.Link != AppleTvLink.Connected ? "disconnected"
        : state.Power is AttentionState.Asleep ? "standby"
        : "app_closed";

    private void CheckPendingEnds(DeviceSlot slot, AppleTvDeviceState? state, bool hasNextMedia, DateTimeOffset now)
    {
        if (slot.PendingEnd is null && slot.PendingAppRunEnd is null)
        {
            return;
        }

        var endedUtc = slot.PendingEnd?.EndedUtc ?? slot.PendingAppRunEnd!.EndedUtc;
        var elapsed = now - endedUtc;

        if (state is { Link: AppleTvLink.Connected, Power: AttentionState.Asleep } && elapsed <= _standbyGracePeriod)
        {
            FlushPendingEnds(slot, "standby");
            return;
        }

        if (elapsed >= _standbyGracePeriod || hasNextMedia || (state is not null && state.Link != AppleTvLink.Connected))
        {
            FlushPendingEnds(slot, "app_closed");
        }
    }

    private void FlushPendingEnds(DeviceSlot slot, string reason)
    {
        if (slot.PendingEnd is { } pendingSession)
        {
            slot.PendingEnd = null;
            pendingSession.SessionData["reason"] = reason;
            _record("session.ended", pendingSession.SessionData);
        }

        if (slot.PendingAppRunEnd is { } pendingApp)
        {
            slot.PendingAppRunEnd = null;
            pendingApp.AppRunData["reason"] = reason;
            _record("appletv.app_ended", pendingApp.AppRunData);
        }
    }

    private void UpdateAppRun(DeviceSlot slot, AppleTvDeviceState state, DateTimeOffset now, PluginReceiverSnapshot receiver)
    {
        var runKey = state.HasMedia ? state.NowPlaying.AppBundleId : null;
        if (slot.AppRun?.Key == runKey)
        {
            return;
        }

        var reason = state.HasMedia ? "switched" : EndReason(state);
        if (reason == "app_closed" && _standbyGracePeriod > TimeSpan.Zero)
        {
            DeferAppRunEnd(slot, now);
        }
        else
        {
            EndAppRun(slot, now, reason);
        }

        if (runKey is not null)
        {
            slot.AppRun = new AppRun(runKey, AppNameOf(state), now, receiver.IsPoweredOn ? receiver.InputSource : null);
        }
    }

    private void DeferAppRunEnd(DeviceSlot slot, DateTimeOffset now)
    {
        if (BuildAppRunEndData(slot, now, "app_closed") is { } data)
        {
            slot.PendingAppRunEnd = new PendingAppRunEnd(now, data);
        }
    }

    private void EndAppRun(DeviceSlot slot, DateTimeOffset now, string reason)
    {
        if (BuildAppRunEndData(slot, now, reason) is { } data)
        {
            _record("appletv.app_ended", data);
        }
    }

    private static Dictionary<string, object?>? BuildAppRunEndData(DeviceSlot slot, DateTimeOffset now, string reason)
    {
        if (slot.AppRun is not { } run)
        {
            return null;
        }

        slot.AppRun = null;
        var duration = (now - run.StartedUtc).TotalSeconds;
        if (duration < DwellSeconds)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["appleTvDevice"] = slot.DeviceName,
            ["app"] = run.AppName,
            ["appId"] = run.Key,
            ["durationSec"] = (int)Math.Round(duration),
            ["watchedSec"] = (int)Math.Round(run.WatchedSec),
            ["titles"] = run.Titles.ToList(),
            ["receiverInput"] = run.ReceiverInput,
            ["reason"] = reason,
        };
    }

    private void DeferSessionEnd(DeviceSlot slot, DateTimeOffset now)
    {
        var data = BuildSessionEndData(slot, now, "app_closed");

        // The host writes the event when the grace period ends; "endedAt" keeps the real end for its overlap math.
        data["endedAt"] = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        slot.PendingEnd = new PendingSessionEnd(now, data);
    }

    private void EndSession(DeviceSlot slot, DateTimeOffset now, string reason)
    {
        var data = BuildSessionEndData(slot, now, reason);
        _record("session.ended", data);
    }

    private static Dictionary<string, object?> BuildSessionEndData(DeviceSlot slot, DateTimeOffset now, string reason)
    {
        var active = slot.Active!;
        slot.Active = null;
        var data = Payload(slot, active.Candidate);
        data["durationSec"] = Math.Max(1, (int)Math.Round((now - active.Candidate.StartedUtc).TotalSeconds));
        data["watchedSec"] = (int)Math.Round(Math.Min(active.Candidate.WatchedSec, (now - active.Candidate.StartedUtc).TotalSeconds));
        data["reason"] = reason;
        if (active.AverageVolume is { } volume)
        {
            data["avgVolume"] = Math.Round(volume, 1);
        }

        return data;
    }

    private static Dictionary<string, object?> Payload(DeviceSlot slot, Candidate c)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = "appletv",
            ["title"] = c.Title ?? c.AppName,
            ["source"] = c.AppName,
            ["app"] = c.AppName,
            ["appId"] = c.AppKey,
            ["appleTvDevice"] = slot.DeviceName,
            ["appleTvModel"] = slot.DeviceModel,
            ["hasTitle"] = c.Title is not null,
        };
        if (!string.IsNullOrWhiteSpace(c.Subtitle))
        {
            data["subtitle"] = c.Subtitle;
        }

        if (c.Season is not null)
        {
            data["season"] = c.Season;
            data["episode"] = c.Episode;
        }

        if (!string.IsNullOrWhiteSpace(c.ReceiverInput))
        {
            data["receiverInput"] = c.ReceiverInput;
            data["receiverSoundMode"] = c.ReceiverSoundMode;
        }

        return data;
    }

    private static string AppNameOf(AppleTvDeviceState state) =>
        state.AppName ?? state.NowPlaying.AppName ?? state.NowPlaying.AppBundleId ?? "Unknown app";

    private static Candidate BuildCandidate(AppleTvDeviceState state, PluginReceiverSnapshot receiver, DateTimeOffset now)
    {
        var np = state.NowPlaying;
        var appName = AppNameOf(state);
        var title = string.IsNullOrWhiteSpace(np.Title) ? null : np.Title.Trim();
        if (title is not null && title.Equals(appName, StringComparison.OrdinalIgnoreCase))
        {
            title = null; // an app name is not a program title
        }

        var subtitle = !string.IsNullOrWhiteSpace(np.SeriesName) && !string.Equals(np.SeriesName, title, StringComparison.OrdinalIgnoreCase)
            ? np.SeriesName
            : np.Artist ?? np.Subtitle;

        return new Candidate(
            AppKey: np.AppBundleId ?? appName,
            AppName: appName,
            StartedUtc: now,
            ReceiverInput: receiver.IsPoweredOn ? receiver.InputSource : null,
            ReceiverSoundMode: receiver.IsPoweredOn ? receiver.SoundMode : null)
        {
            Title = title,
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
            Season = np.Season,
            Episode = np.Episode,
        };
    }

    private static void RememberTitle(DeviceSlot slot, string title)
    {
        if (slot.AppRun is { } run && run.Titles.Count < 50)
        {
            run.Titles.Add(title);
        }
    }

    private DeviceSlot GetSlot(AppleTvDeviceConfig device)
    {
        if (!_slots.TryGetValue(device.Id, out var slot))
        {
            slot = new DeviceSlot(device.Id);
            _slots[device.Id] = slot;
        }

        slot.DeviceName = device.Name;
        slot.DeviceModel = device.Model;
        return slot;
    }

    private sealed class DeviceSlot(string deviceId)
    {
        private DateTimeOffset? _lastTick;

        public string DeviceId { get; } = deviceId;

        public string DeviceName { get; set; } = deviceId;

        public string DeviceModel { get; set; } = string.Empty;

        public bool IsPlaying { get; set; }

        public DateTimeOffset LastPlayingUtc { get; private set; }

        public Candidate? Pending { get; set; }

        public ActiveSession? Active { get; set; }

        public AppRun? AppRun { get; set; }

        public PendingSessionEnd? PendingEnd { get; set; }

        public PendingAppRunEnd? PendingAppRunEnd { get; set; }

        public void Accumulate(DateTimeOffset now)
        {
            if (_lastTick is { } last && IsPlaying)
            {
                var seconds = Math.Clamp((now - last).TotalSeconds, 0, 120);
                if ((Active?.Candidate ?? Pending) is { } program)
                {
                    program.WatchedSec += seconds;
                }

                if (AppRun is not null)
                {
                    AppRun.WatchedSec += seconds;
                }
            }

            if (IsPlaying)
            {
                LastPlayingUtc = now;
            }

            _lastTick = now;
        }
    }

    private sealed record Candidate(
        string AppKey,
        string AppName,
        DateTimeOffset StartedUtc,
        string? ReceiverInput,
        string? ReceiverSoundMode)
    {
        public string? Title { get; set; }

        public string? Subtitle { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public double WatchedSec { get; set; }
    }

    private sealed class ActiveSession(Candidate candidate)
    {
        private double _volumeSum;
        private int _volumeSamples;

        public Candidate Candidate { get; } = candidate;

        public double? AverageVolume => _volumeSamples > 0 ? _volumeSum / _volumeSamples : null;

        public void AddVolume(double volume)
        {
            _volumeSum += volume;
            _volumeSamples++;
        }
    }

    private sealed class AppRun(string key, string appName, DateTimeOffset startedUtc, string? receiverInput)
    {
        public string Key { get; } = key;

        public string AppName { get; } = appName;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public string? ReceiverInput { get; } = receiverInput;

        public double WatchedSec { get; set; }

        public HashSet<string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PendingSessionEnd(DateTimeOffset EndedUtc, Dictionary<string, object?> SessionData);

    private sealed record PendingAppRunEnd(DateTimeOffset EndedUtc, Dictionary<string, object?> AppRunData);
}

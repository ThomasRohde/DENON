using Denon.App.PluginSdk;
using Denon.Plugin.Chromecast.Cast;

namespace Denon.Plugin.Chromecast;

/// <summary>
/// Turns live Cast state into habit-ready watch sessions on the <c>media</c> stream.
/// It uses the same event names and payload keys as the host's AVR and LG TV sessions
/// (<c>session.started</c> / <c>session.ended</c> with kind, title, source, durationSec, reason, avgVolume),
/// so the habit system can read Cast sessions without changes.
///
/// Rules:
/// - One session per program (app + title). A new title closes the old session ("switched").
/// - A session is logged only after it lasts <see cref="DwellSeconds"/> (same 15 s dwell as the host).
/// - If the title arrives after the app started, the open session gets the title. It does not restart.
/// - <c>watchedSec</c> counts only PLAYING/BUFFERING time. <c>durationSec</c> is wall-clock time.
///   Both start when the program first shows (the dwell time is included in both).
/// - One <c>cast.app_ended</c> summary per app run, with the titles seen.
/// </summary>
internal sealed class CastWatchLog
{
    public const int DwellSeconds = 15;

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, Dictionary<string, object?>> _record;

    public CastWatchLog(Action<string, Dictionary<string, object?>> record)
    {
        _record = record;
    }

    /// <summary>Call on every device state change.</summary>
    public void Update(CastDeviceConfig device, CastDeviceState state, PluginReceiverSnapshot receiver, DateTimeOffset now)
    {
        lock (_gate)
        {
            var slot = GetSlot(device);
            slot.Accumulate(now);
            slot.PlayerState = state.PlayerState;

            var next = state.HasActiveApp ? BuildCandidate(state, receiver, now) : null;
            var current = slot.Active?.Candidate ?? slot.Pending;

            if (current is not null && next is not null && current.AppKey == next.AppKey)
            {
                if (current.Title == next.Title)
                {
                    return;
                }

                // The title arrived late: give it to the open session.
                if (current.Title is null)
                {
                    current.Title = next.Title;
                    current.Subtitle = next.Subtitle;
                    RememberTitle(slot, next.Title!); // Not null: equal titles returned above.
                    return;
                }
            }

            if (slot.Active is not null)
            {
                EndSession(slot, now, next is not null ? "switched" : EndReason(state));
            }

            UpdateAppRun(slot, state, receiver, now);
            slot.Pending = next;
            if (next?.Title is not null)
            {
                RememberTitle(slot, next.Title);
            }
        }
    }

    /// <summary>Call every few seconds: confirms the dwell, samples volume and play time.</summary>
    public void Tick(Func<string, PluginReceiverSnapshot> receiverFor, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Accumulate(now);

                // A program that has not played yet (opened paused, or still loading) is not watching:
                // the dwell starts at the first PLAYING/BUFFERING, not when the app shows the title.
                if (slot.Active is null && slot.Pending is { WatchedSec: <= 0 } notStarted && !slot.IsPlaying)
                {
                    slot.Pending = notStarted with { StartedUtc = now };
                    continue;
                }

                if (slot.Active is null && slot.Pending is { } pending &&
                    (now - pending.StartedUtc).TotalSeconds >= DwellSeconds)
                {
                    // The receiver may not have been known when the program started (plugin just loaded).
                    if (pending.ReceiverInput is null && receiverFor(slot.DeviceId) is { IsPoweredOn: true } rx)
                    {
                        pending = pending with { ReceiverInput = rx.InputSource, ReceiverSoundMode = rx.SoundMode };
                    }

                    if (slot.AppRun is { ReceiverInput: null or "" } run && !string.IsNullOrEmpty(pending.ReceiverInput))
                    {
                        run.ReceiverInput = pending.ReceiverInput;
                    }

                    slot.Pending = null;
                    slot.Active = new ActiveSession(pending);
                    var data = Payload(slot, pending);
                    data["dwellConfirmedSec"] = (int)Math.Round((now - pending.StartedUtc).TotalSeconds); // Real seconds since start.
                    _record("session.started", data);
                }

                if (slot.Active is not null)
                {
                    var receiver = receiverFor(slot.DeviceId);
                    if (receiver.IsPoweredOn && receiver.Volume > 0)
                    {
                        slot.Active.AddVolume(receiver.Volume);
                    }
                }
            }
        }
    }

    /// <summary>Closes every open session (plugin stop, device removed or disabled).</summary>
    public void EndAll(DateTimeOffset now, string reason, string? deviceId = null)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values.Where(s => deviceId is null || s.DeviceId == deviceId))
            {
                slot.Accumulate(now);
                if (slot.Active is not null)
                {
                    EndSession(slot, now, reason);
                }

                slot.Pending = null;
                EndAppRun(slot, now, reason);
            }
        }
    }

    private void UpdateAppRun(DeviceSlot slot, CastDeviceState state, PluginReceiverSnapshot receiver, DateTimeOffset now)
    {
        var runKey = state.HasActiveApp ? $"{state.AppId}|{state.SessionId}" : null;
        if (slot.AppRun?.Key == runKey)
        {
            return;
        }

        EndAppRun(slot, now, state.HasActiveApp ? "switched" : EndReason(state));
        if (runKey is not null)
        {
            slot.AppRun = new AppRun(runKey, state.AppName ?? "Unknown app", state.AppId, now, receiver.InputSource);
        }
    }

    private void EndAppRun(DeviceSlot slot, DateTimeOffset now, string reason)
    {
        if (slot.AppRun is not { } run)
        {
            return;
        }

        slot.AppRun = null;
        var duration = (now - run.StartedUtc).TotalSeconds;
        if (duration < DwellSeconds)
        {
            return;
        }

        _record("cast.app_ended", new Dictionary<string, object?>
        {
            ["castDevice"] = slot.DeviceName,
            ["app"] = run.AppName,
            ["appId"] = run.AppId,
            ["durationSec"] = (int)Math.Round(duration),
            ["watchedSec"] = (int)Math.Round(run.WatchedSec),
            ["titles"] = run.Titles.ToList(),
            ["receiverInput"] = run.ReceiverInput,
            ["reason"] = reason,
        });
    }

    private void EndSession(DeviceSlot slot, DateTimeOffset now, string reason)
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

        _record("session.ended", data);
    }

    private static Dictionary<string, object?> Payload(DeviceSlot slot, Candidate c)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = "cast",
            ["title"] = c.Title ?? c.AppName,
            ["source"] = c.AppName,
            ["app"] = c.AppName,
            ["appId"] = c.AppId,
            ["castDevice"] = slot.DeviceName,
            ["castDeviceModel"] = slot.DeviceModel,
            ["hasTitle"] = c.Title is not null,
        };
        if (!string.IsNullOrWhiteSpace(c.Subtitle))
        {
            data["artist"] = c.Subtitle;
        }

        if (!string.IsNullOrWhiteSpace(c.ReceiverInput))
        {
            data["receiverInput"] = c.ReceiverInput;
            data["receiverSoundMode"] = c.ReceiverSoundMode;
        }

        return data;
    }

    private static Candidate BuildCandidate(CastDeviceState state, PluginReceiverSnapshot receiver, DateTimeOffset now)
    {
        var appName = state.AppName ?? "Unknown app";
        var title = string.IsNullOrWhiteSpace(state.Title) ? null : state.Title.Trim();

        // Some apps (Netflix) report only their own name as the media title: that is not a program title.
        if (title is not null && title.Equals(appName, StringComparison.OrdinalIgnoreCase))
        {
            title = null;
        }

        return new Candidate(
            AppKey: $"{state.AppId}|{state.SessionId}",
            AppName: appName,
            AppId: state.AppId,
            StartedUtc: now,
            ReceiverInput: receiver.IsPoweredOn ? receiver.InputSource : null,
            ReceiverSoundMode: receiver.IsPoweredOn ? receiver.SoundMode : null)
        {
            Title = title,
            Subtitle = string.IsNullOrWhiteSpace(state.Subtitle) ? null : state.Subtitle.Trim(),
        };
    }

    private static string EndReason(CastDeviceState state) =>
        state.Connection == CastConnectionState.Connected ? "app_closed" : "disconnected";

    private static void RememberTitle(DeviceSlot slot, string title)
    {
        if (slot.AppRun is { } run && run.Titles.Count < 50)
        {
            run.Titles.Add(title);
        }
    }

    private DeviceSlot GetSlot(CastDeviceConfig device)
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

        public string PlayerState { get; set; } = string.Empty;

        public bool IsPlaying => PlayerState is "PLAYING" or "BUFFERING";

        public Candidate? Pending { get; set; }

        public ActiveSession? Active { get; set; }

        public AppRun? AppRun { get; set; }

        /// <summary>Adds the time since the last call to the play counters, if the player was playing.</summary>
        public void Accumulate(DateTimeOffset now)
        {
            if (_lastTick is { } last && PlayerState is "PLAYING" or "BUFFERING")
            {
                var seconds = Math.Clamp((now - last).TotalSeconds, 0, 120);

                // Count play time from the start of the program, also during the dwell,
                // so watchedSec and durationSec measure the same period.
                if ((Active?.Candidate ?? Pending) is { } program)
                {
                    program.WatchedSec += seconds;
                }

                if (AppRun is not null)
                {
                    AppRun.WatchedSec += seconds;
                }
            }

            _lastTick = now;
        }
    }

    private sealed record Candidate(
        string AppKey,
        string AppName,
        string? AppId,
        DateTimeOffset StartedUtc,
        string? ReceiverInput,
        string? ReceiverSoundMode)
    {
        public string? Title { get; set; }

        public string? Subtitle { get; set; }

        /// <summary>PLAYING/BUFFERING seconds since <see cref="StartedUtc"/>, dwell included.</summary>
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

    private sealed class AppRun(string key, string appName, string? appId, DateTimeOffset startedUtc, string receiverInput)
    {
        public string Key { get; } = key;

        public string AppName { get; } = appName;

        public string? AppId { get; } = appId;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public string ReceiverInput { get; set; } = receiverInput;

        public double WatchedSec { get; set; }

        public HashSet<string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

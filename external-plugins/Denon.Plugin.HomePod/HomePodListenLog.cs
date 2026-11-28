namespace Denon.Plugin.HomePod;

/// <summary>
/// Turns live HomePod state into listening sessions on the <c>media</c> stream, with the same events and keys as
/// the Apple TV and Chromecast plugins: <c>session.started</c> / <c>session.ended</c>, <c>kind: "homepod"</c>,
/// title, app, durationSec, watchedSec (time it played), reason.
///
/// Rules:
/// - One session per item per speaker (app + title: a song, an episode, a radio stream). A new item ends the old
///   session ("switched").
/// - The <see cref="DwellSeconds"/> dwell starts at the first Playing; an item that never plays is not logged.
/// - <c>watchedSec</c> counts Playing time only; <c>durationSec</c> is wall-clock time.
/// - Paused for <see cref="PauseTimeout"/> ends the session ("paused"); playing again starts a new one.
/// - End reasons: "switched", "stopped", "paused", "disconnected", "plugin_stopped", "app_shutdown",
///   "speaker_disabled".
/// The host counts HomePod time in full: it is listening in its own room, not covered by the TV or the receiver.
/// </summary>
internal sealed class HomePodListenLog(Action<string, Dictionary<string, object?>> record)
{
    public const int DwellSeconds = 15;

    public static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call on every state change of a speaker.</summary>
    public void Update(HomePodSpeakerConfig speaker, HomePodState state, DateTimeOffset now)
    {
        lock (_gate)
        {
            var slot = GetSlot(speaker);
            slot.Accumulate(now);
            slot.IsPlaying = state.IsPlaying;
            if (slot.IsPlaying)
            {
                slot.LastPlayingUtc = now;
            }

            var next = state.HasMedia
                ? new Item(state.NowPlaying.AppBundleId!, state.AppName ?? "HomePod", Clean(state.NowPlaying.Title),
                    Clean(state.NowPlaying.Artist), Clean(state.NowPlaying.Album), now)
                : null;
            var current = slot.Active?.Item ?? slot.Pending;

            if (current is not null && next is not null && current.Bundle == next.Bundle)
            {
                if (current.Title == next.Title)
                {
                    current.Artist ??= next.Artist;
                    current.Album ??= next.Album;
                    return;
                }

                // The title arrived after the app: keep the open session.
                if (current.Title is null)
                {
                    current.Title = next.Title;
                    current.Artist = next.Artist;
                    current.Album = next.Album;
                    return;
                }
            }

            if (slot.Active is not null)
            {
                End(slot, now, next is not null ? "switched" : state.Link == HomePodLink.Connected ? "stopped" : "disconnected");
            }

            slot.Pending = next;
        }
    }

    /// <summary>Call every few seconds: counts play time, confirms the dwell, ends long pauses.</summary>
    public void Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Accumulate(now);
                if (slot.IsPlaying)
                {
                    slot.LastPlayingUtc = now;
                }

                if (slot.Active is null && slot.Pending is { } pending)
                {
                    if (!slot.IsPlaying && pending.PlayedSec <= 0)
                    {
                        pending.StartedUtc = now; // The dwell starts at the first Playing.
                        continue;
                    }

                    // A paused item needs the full dwell of real play: a second of play then a pause is no session.
                    if ((now - pending.StartedUtc).TotalSeconds >= DwellSeconds && (slot.IsPlaying || pending.PlayedSec >= DwellSeconds))
                    {
                        slot.Pending = null;
                        slot.Active = new Session(pending);
                        var data = Payload(slot, pending);
                        data["dwellConfirmedSec"] = (int)Math.Round((now - pending.StartedUtc).TotalSeconds);
                        record("session.started", data);
                    }
                }

                if (slot.Active is { } active && !slot.IsPlaying && now - slot.LastPlayingUtc >= PauseTimeout)
                {
                    var item = active.Item;
                    End(slot, now, "paused");
                    slot.Pending = item with { StartedUtc = now, PlayedSec = 0 };
                }
            }
        }
    }

    /// <summary>Ends the open session of one speaker (removed, disabled).</summary>
    public void EndSpeaker(string speakerId, DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(speakerId, out var slot))
            {
                slot.Accumulate(now);
                if (slot.Active is not null)
                {
                    End(slot, now, reason);
                }

                slot.Pending = null;
            }
        }
    }

    /// <summary>Ends every open session (plugin stop, app exit).</summary>
    public void EndAll(DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                slot.Accumulate(now);
                if (slot.Active is not null)
                {
                    End(slot, now, reason);
                }

                slot.Pending = null;
            }
        }
    }

    private Slot GetSlot(HomePodSpeakerConfig speaker)
    {
        if (!_slots.TryGetValue(speaker.Id, out var slot))
        {
            slot = new Slot();
            _slots[speaker.Id] = slot;
        }

        slot.Name = speaker.Name;
        slot.Model = speaker.ModelName;
        slot.Members = string.Join(", ", speaker.Members);
        return slot;
    }

    private void End(Slot slot, DateTimeOffset now, string reason)
    {
        var active = slot.Active!;
        slot.Active = null;
        var data = Payload(slot, active.Item);
        var wall = (now - active.Item.StartedUtc).TotalSeconds;
        data["durationSec"] = Math.Max(1, (int)Math.Round(wall));
        data["watchedSec"] = (int)Math.Round(Math.Min(active.Item.PlayedSec, wall));
        data["reason"] = reason;
        record("session.ended", data);
    }

    private static Dictionary<string, object?> Payload(Slot slot, Item item)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = "homepod",
            ["title"] = item.Title ?? item.App,
            ["source"] = "HomePod",
            ["app"] = item.App,
            ["appId"] = item.Bundle,
            ["homepodDevice"] = slot.Name,
            ["homepodModel"] = slot.Model,
            ["hasTitle"] = item.Title is not null,
        };
        if (item.Artist is not null)
        {
            data["artist"] = item.Artist;
        }

        if (item.Album is not null)
        {
            data["album"] = item.Album;
        }

        if (slot.Members.Contains(',', StringComparison.Ordinal))
        {
            data["homepodSpeakers"] = slot.Members;
        }

        return data;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private sealed class Slot
    {
        public string Name { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string Members { get; set; } = string.Empty;

        public bool IsPlaying { get; set; }

        public DateTimeOffset LastPlayingUtc { get; set; }

        public DateTimeOffset? LastTickUtc { get; set; }

        public Item? Pending { get; set; }

        public Session? Active { get; set; }

        /// <summary>Most play time one step may add. Ticks come every few seconds; a longer gap is a PC sleep.</summary>
        public const double MaxStepSeconds = 120;

        /// <summary>
        /// Adds the time since the last call to the play time of the item that plays. Each step is clamped to
        /// [0, <see cref="MaxStepSeconds"/>], so a PC that sleeps while the speaker played does not add hours.
        /// </summary>
        public void Accumulate(DateTimeOffset now)
        {
            if (LastTickUtc is { } last && IsPlaying && (Active?.Item ?? Pending) is { } item)
            {
                item.PlayedSec += Math.Clamp((now - last).TotalSeconds, 0, MaxStepSeconds);
            }

            LastTickUtc = now;
        }
    }

    private sealed record Session(Item Item);

    private sealed record Item(string Bundle, string App, string? Title, string? Artist, string? Album, DateTimeOffset StartedUtc)
    {
        public string? Title { get; set; } = Title;

        public string? Artist { get; set; } = Artist;

        public string? Album { get; set; } = Album;

        public DateTimeOffset StartedUtc { get; set; } = StartedUtc;

        public double PlayedSec { get; set; }
    }
}

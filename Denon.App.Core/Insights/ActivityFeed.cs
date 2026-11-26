using System.Globalization;
using System.Text.Json;
using Denon.App.Core.Services;

namespace Denon.App.Core.Insights;

/// <summary>
/// The Insights page activity feed: habit events as short sentences ("Volume 54 → 50 (−30 dB)"), with the device,
/// who made the change and an icon. Connection and duplicate bookkeeping events are marked as <c>Quiet</c>; the page
/// hides them unless the user asks for all events.
/// </summary>
public static class ActivityFeed
{
    public sealed record Item(
        string Ts,
        string Stream,
        string Device,
        string Event,
        string Origin,
        string OriginLabel,
        string Icon,
        string Text,
        bool Quiet,
        string? Logo = null);

    private sealed record CoveredSessionWindow(
        string Kind,
        string Device,
        string PhysicalDevice,
        string Input,
        string Title,
        DateTimeOffset Start,
        DateTimeOffset End);

    /// <param name="logoForChannel">A cached logo file for a channel name, for Live TV entries logged without one.</param>
    public static IReadOnlyList<Item> Describe(IEnumerable<JsonElement> events, Func<string, string?>? logoForChannel = null)
    {
        var list = events.ToList();
        var coveredWindows = CollectCoveredEndedSessions(list);
        var items = new List<Item>(list.Count);

        foreach (var ev in list)
        {
            var item = Describe(ev, logoForChannel);
            if (item is null)
            {
                continue;
            }

            if (!item.Quiet &&
                string.Equals(item.Event, "session.started", StringComparison.Ordinal) &&
                coveredWindows.Count > 0 &&
                MatchesCoveredEndedSession(ev, coveredWindows))
            {
                item = item with { Quiet = true };
            }

            items.Add(item);
        }

        return items;
    }

    public static Item? Describe(JsonElement ev, Func<string, string?>? logoForChannel = null)
    {
        if (ev.ValueKind != JsonValueKind.Object || Str(ev, "ts") is not { } ts || Str(ev, "event") is not { } name)
        {
            return null;
        }

        var stream = Str(ev, "stream") ?? "";
        var device = Str(ev, "device") ?? stream;
        var origin = Str(ev, "origin") ?? "";
        var data = ev.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        string? Get(string key) => data.ValueKind == JsonValueKind.Object ? DataStr(data, key) : null;
        double? Num(string key) => data.ValueKind == JsonValueKind.Object &&
                                   data.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
        bool? Flag(string key) => data.ValueKind == JsonValueKind.Object &&
                                  data.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null;

        var who = DeviceLabel(stream, device);
        var icon = "dot";
        var quiet = false;
        string text;

        switch (name)
        {
            case "session.started":
            {
                var kind = Get("kind") ?? "";
                var title = Get("title") ?? "";
                var app = data.ValueKind == JsonValueKind.Object ? WatchTimeReport.AppLabel(kind, data, title) : kind;
                text = title.Length > 0 && !title.Equals(app, StringComparison.OrdinalIgnoreCase)
                    ? $"Started {title} · {app}"
                    : $"Started {app}";
                icon = "play";
                break;
            }

            case "session.ended":
            {
                var kind = Get("kind") ?? "";
                var title = Get("title") ?? "";
                var app = data.ValueKind == JsonValueKind.Object ? WatchTimeReport.AppLabel(kind, data, title) : kind;
                var seconds = (int)Math.Round(Num("exclusiveSec") ?? Num("durationSec") ?? 0);
                var label = title.Length > 0 && !title.Equals(app, StringComparison.OrdinalIgnoreCase) ? $"{title} · {app}" : app;
                text = seconds > 0
                    ? $"{label}: {WatchTimeReport.FormatDuration(seconds)}"
                    : $"{label} ended (counted under another session)";
                icon = "stop";
                quiet = seconds == 0 || (data.ValueKind == JsonValueKind.Object && IsCoveredReceiverInput(kind, data));
                break;
            }

            case "volume.settled":
                text = Num("to") is { } to
                    ? $"Volume {Fmt(Num("from"))} → {Fmt(to)} ({InsightsReport.Db(to)})"
                    : "Volume changed";
                icon = "volume";
                break;

            case "power.changed":
                text = $"{who} {(Get("to") is "on" ? "turned on" : Get("to") is { } to2 ? $"went to {to2}" : "changed power")}";
                icon = "power";
                break;

            case "input.changed":
                text = $"Input {Get("from") ?? "?"} → {Get("friendlyName") ?? Get("to") ?? "?"}";
                icon = "input";
                quiet = Get("from") is null;
                break;

            case "sound_mode.changed":
                text = $"Sound mode {Get("to") ?? "?"}";
                icon = "wave";
                quiet = Get("from") is null;
                break;

            case "zone2.changed":
                text = $"Zone 2 {Get("to") ?? "changed"}";
                icon = "speaker";
                break;

            case "eco.changed":
                text = $"ECO mode {Get("to") ?? "changed"}";
                icon = "leaf";
                quiet = Get("from") is null;
                break;

            case "connection.changed":
                text = Flag("connected") == true ? $"Connected to {Get("model") ?? "the receiver"}" : "Receiver connection lost";
                icon = "link";
                quiet = true;
                break;

            case "app.changed":
                text = $"TV: {Get("appTitle") ?? Get("toAppId") ?? "app changed"}";
                icon = "tv";
                break;

            case "channel.changed":
                text = Get("channelName") is { } channelName
                    ? $"TV channel {channelName}{(Get("channelNumber") is { } number ? $" (Ch {number})" : "")}"
                    : $"TV channel {Get("channelNumber") ?? "changed"}";
                icon = "tv";
                break;

            case "audio_output.changed":
                text = $"TV sound → {Get("label") ?? Get("to") ?? "?"}";
                icon = "speaker";
                quiet = Get("from") is null;
                break;

            case "screen.changed":
                text = Flag("screenOff") == true ? "TV screen off" : "TV screen on";
                icon = "tv";
                break;

            case "cast.connected":
            case "cast.disconnected":
                text = $"{Get("castDevice") ?? "Cast device"} {(name == "cast.connected" ? "online" : "offline")}";
                icon = "cast";
                quiet = true;
                break;

            case "appletv.connected":
                text = $"{Get("appleTvDevice") ?? "Apple TV"} linked";
                icon = "link";
                quiet = true;
                break;

            case "appletv.playback_changed":
                text = $"{Get("app") ?? "Apple TV"}{(Get("title") is { } t ? $": {t}" : "")} · {Get("playbackState") ?? "changed"}";
                icon = "play";
                break;

            case "appletv.app_ended":
                text = $"{Get("app") ?? "Apple TV app"} closed";
                icon = "stop";
                quiet = true;
                break;

            case "homepod.connected":
            case "homepod.disconnected":
                text = $"{Get("homepodDevice") ?? "HomePod"} {(name == "homepod.connected" ? "linked" : "not linked")}";
                icon = "link";
                quiet = true;
                break;

            case "homepod.paused_by_automation":
                text = $"{Get("homepodDevice") ?? "HomePod"} paused: the receiver turned on";
                icon = "pause";
                break;

            case "appletv.slept":
                text = $"{Get("appleTvDevice") ?? "Apple TV"} went to sleep";
                icon = "moon";
                break;

            case "lighting.state_changed":
                // "to" is how many lights are on (2.9.3 and later). Older entries logged the Hue plugin's status text
                // ("Connecting…", "Stopped"): not a light changing, so they are quiet.
                icon = "bulb";
                if (Num("to") is { } on)
                {
                    text = on == 0
                        ? "Lights off"
                        : Num("total") is { } total ? $"Lights: {on:0} of {total:0} on" : $"Lights: {on:0} on";
                }
                else
                {
                    text = $"Lights: {Get("to") ?? "changed"}";
                    quiet = true;
                }

                break;

            case "music_sync.changed":
                text = Flag("enabled") == true ? $"Hue music sync on{(Get("mode") is { } m ? $" ({m})" : "")}" : "Hue music sync off";
                icon = "bulb";
                break;

            case "plugin.action":
            {
                var actionId = Get("actionId") ?? "action";
                text = $"{Get("pluginId") ?? device}: {actionId}";
                icon = "bolt";
                // Reads logged as actions before 2.9.3 (the Deck's artwork read every 15 s): quiet, like Insights skips them.
                quiet = actionId == "now-playing" || actionId.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase);
                break;
            }

            case "source.changed":
                text = $"Now playing from {Get("source") ?? "a new source"}";
                icon = "play";
                quiet = true; // the input, app or channel change already has its own entry
                break;

            case "scene.activated":
                text = $"Scene {Get("sceneName") ?? ""}".TrimEnd();
                icon = "bolt";
                break;

            case "automation.triggered":
            {
                // The summary is "<trigger> → <what it did>"; the feed shows the automation and what it did.
                var summary = Get("summary") ?? "";
                var prefix = (Get("trigger") ?? "") + " → ";
                var what = summary.StartsWith(prefix, StringComparison.Ordinal) ? summary[prefix.Length..] : summary;
                var automationName = Get("automation") ?? "";
                text = what.Length > 0
                    ? $"Automation {automationName}: {what}"
                    : $"Automation {automationName}".TrimEnd();
                icon = "bolt";
                quiet = Get("outcome") == "nothing"; // a run whose checks said no changed nothing
                break;
            }

            default:
                text = $"{who}: {name.Replace('_', ' ').Replace('.', ' ')}";
                break;
        }

        // A Live TV session or channel change shows the channel's logo (a file in the Smart TV plugin's cache).
        string? logo = null;
        if (name is "session.started" or "session.ended" or "channel.changed")
        {
            logo = InsightsReport.LogoName(Get("channelLogo"));
            var channel = name == "channel.changed"
                ? Get("channelName")
                : Get("kind") == "tv_channel" && data.ValueKind == JsonValueKind.Object
                    ? WatchTimeReport.AppLabel("tv_channel", data, Get("title") ?? "")
                    : null;
            if (logo is null && channel is not null && logoForChannel is not null)
            {
                logo = InsightsReport.LogoName(logoForChannel(channel));
            }
        }

        return new Item(ts, stream, who, name, origin, InsightsReport.OriginLabel(origin), icon, text, quiet, logo);
    }

    public static string DeviceLabel(string stream, string device) => device switch
    {
        "avr" => "Receiver",
        "smart-tv" or "lg-tv" => "TV",
        "apple-tv" => "Apple TV",
        "homepod" => "HomePod",
        "chromecast" => "Chromecast",
        "philips-hue" => "Hue",
        "home-assistant" => "Home Assistant",
        _ when stream == EventTelemetryLog.StreamAvr => device,
        _ => device,
    };

    private static List<CoveredSessionWindow> CollectCoveredEndedSessions(List<JsonElement> events)
    {
        var windows = new List<CoveredSessionWindow>();
        foreach (var ev in events)
        {
            if (ev.ValueKind != JsonValueKind.Object ||
                !string.Equals(Str(ev, "event"), "session.ended", StringComparison.Ordinal) ||
                !TryParseTs(ev, out var endTs) ||
                !ev.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = DataStr(data, "kind") ?? string.Empty;
            if (!IsCoveredEndedSession(kind, data))
            {
                continue;
            }

            var duration = Math.Max(0, DataNum(data, "durationSec") ?? 0);
            windows.Add(new CoveredSessionWindow(
                kind,
                Str(ev, "device") ?? string.Empty,
                PhysicalDevice(data),
                DataStr(data, "input") ?? string.Empty,
                DataStr(data, "title") ?? string.Empty,
                endTs.AddSeconds(-duration),
                endTs));
        }

        return windows;
    }

    private static bool IsCoveredEndedSession(string kind, JsonElement data)
    {
        var exclusive = DataNum(data, "exclusiveSec");
        if (exclusive is not null && (int)Math.Round(exclusive.Value) == 0)
        {
            return true;
        }

        return IsCoveredReceiverInput(kind, data);
    }

    /// <summary>
    /// True when a receiver-input session (<c>avr_input</c>) was covered by a Chromecast or Apple TV session
    /// for at least as long as its own uncovered remainder (suppressing brief menu/switch tails too).
    /// </summary>
    private static bool IsCoveredReceiverInput(string kind, JsonElement data)
    {
        if (!string.Equals(kind, "avr_input", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var covered = DataNum(data, "coveredSec") ?? 0;
        if (covered <= 0 || !HasStreamerCoverer(data))
        {
            return false;
        }

        var duration = DataNum(data, "durationSec") ?? covered;
        var exclusive = DataNum(data, "exclusiveSec") ?? Math.Max(0, duration - covered);
        return covered >= exclusive;
    }

    private static bool HasStreamerCoverer(JsonElement data)
    {
        if (!data.TryGetProperty("coveredBy", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in el.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } label &&
                (label.StartsWith("cast:", StringComparison.OrdinalIgnoreCase) ||
                 label.StartsWith("appletv:", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCoveredEndedSession(JsonElement ev, List<CoveredSessionWindow> windows)
    {
        if (!TryParseTs(ev, out var startTs) ||
            !ev.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var kind = DataStr(data, "kind") ?? string.Empty;
        var device = Str(ev, "device") ?? string.Empty;
        var physical = PhysicalDevice(data);
        var input = DataStr(data, "input") ?? string.Empty;
        var title = DataStr(data, "title") ?? string.Empty;
        var dwell = Math.Max(0, DataNum(data, "dwellConfirmedSec") ?? 0);
        var effectiveStart = startTs.AddSeconds(-dwell);

        foreach (var w in windows)
        {
            if (!string.Equals(w.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (w.Device.Length > 0 && device.Length > 0 &&
                !string.Equals(w.Device, device, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The envelope's device is the plugin id, shared by every Cast device or Apple TV it manages.
            if (w.PhysicalDevice.Length > 0 && physical.Length > 0 &&
                !string.Equals(w.PhysicalDevice, physical, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sameTarget = string.Equals(kind, "avr_input", StringComparison.OrdinalIgnoreCase)
                ? (w.Input.Length > 0 && input.Length > 0 && string.Equals(w.Input, input, StringComparison.OrdinalIgnoreCase)) ||
                  (w.Title.Length > 0 && title.Length > 0 && string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase))
                : w.Title.Length > 0 && title.Length > 0 && string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase);

            if (!sameTarget)
            {
                continue;
            }

            if (startTs <= w.End.AddSeconds(5) &&
                effectiveStart >= w.Start.AddSeconds(-10) &&
                effectiveStart <= w.End.AddSeconds(5))
            {
                return true;
            }
        }

        return false;
    }

    private static string PhysicalDevice(JsonElement data) =>
        DataStr(data, "castDevice") ?? DataStr(data, "appleTvDevice") ?? string.Empty;

    private static bool TryParseTs(JsonElement ev, out DateTimeOffset ts)
    {
        ts = default;
        return Str(ev, "ts") is { } raw &&
               DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ts);
    }

    private static double? DataNum(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n) ? n : null;

    private static string Fmt(double? value) => value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) : "?";

    private static string? Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? DataStr(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s ? s : null;
}

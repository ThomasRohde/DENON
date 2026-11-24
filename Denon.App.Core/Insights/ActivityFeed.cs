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
        bool Quiet);

    public static IReadOnlyList<Item> Describe(IEnumerable<JsonElement> events) =>
        events.Select(Describe).Where(i => i is not null).Select(i => i!).ToList();

    public static Item? Describe(JsonElement ev)
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
                quiet = seconds == 0;
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

            case "appletv.slept":
                text = $"{Get("appleTvDevice") ?? "Apple TV"} went to sleep";
                icon = "moon";
                break;

            case "lighting.state_changed":
                text = $"Lights: {Get("to") ?? "changed"}";
                icon = "bulb";
                quiet = (Get("to") ?? "").StartsWith("Connecting", StringComparison.OrdinalIgnoreCase);
                break;

            case "music_sync.changed":
                text = Flag("enabled") == true ? $"Hue music sync on{(Get("mode") is { } m ? $" ({m})" : "")}" : "Hue music sync off";
                icon = "bulb";
                break;

            case "plugin.action":
                text = $"{Get("pluginId") ?? device}: {Get("actionId") ?? "action"}";
                icon = "bolt";
                break;

            case "scene.activated":
                text = $"Scene {Get("sceneName") ?? ""}".TrimEnd();
                icon = "bolt";
                break;

            case "automation.triggered":
                text = $"Automation {Get("ruleName") ?? ""}".TrimEnd();
                icon = "bolt";
                break;

            default:
                text = $"{who}: {name.Replace('_', ' ').Replace('.', ' ')}";
                break;
        }

        return new Item(ts, stream, who, name, origin, InsightsReport.OriginLabel(origin), icon, text, quiet);
    }

    public static string DeviceLabel(string stream, string device) => device switch
    {
        "avr" => "Receiver",
        "smart-tv" or "lg-tv" => "TV",
        "apple-tv" => "Apple TV",
        "chromecast" => "Chromecast",
        "philips-hue" => "Hue",
        "home-assistant" => "Home Assistant",
        _ when stream == EventTelemetryLog.StreamAvr => device,
        _ => device,
    };

    private static string Fmt(double? value) => value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) : "?";

    private static string? Str(JsonElement ev, string name) =>
        ev.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? DataStr(JsonElement data, string name) =>
        data.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s ? s : null;
}

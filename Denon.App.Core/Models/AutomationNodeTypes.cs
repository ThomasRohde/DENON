namespace Denon.App.Core.Models;

public sealed record AutomationFieldOption(string Value, string Label);

/// <summary>One config field of a node type, as the editor shows it. <see cref="Kind"/> is listed in docs/automation-graphs.md.</summary>
public sealed record AutomationField(
    string Key,
    string Label,
    string Kind,
    bool Required = false,
    string? Placeholder = null,
    string? Help = null,
    IReadOnlyList<AutomationFieldOption>? Options = null);

public sealed record AutomationNodeTypeInfo(
    string Type,
    string Group,
    string Title,
    string Description,
    IReadOnlyList<string> Ports,
    bool HasInput,
    IReadOnlyList<AutomationField> Fields);

/// <summary>The node types of an automation graph: names, ports and fields. The validator, runner and editor use them.</summary>
public static class AutomationNodeTypes
{
    public const string GroupTrigger = "trigger";
    public const string GroupCondition = "condition";
    public const string GroupAction = "action";

    public const string PortOut = "out";
    public const string PortYes = "yes";
    public const string PortNo = "no";

    public const string TriggerEvent = "trigger.event";
    public const string TriggerWebhook = "trigger.webhook";
    public const string TriggerSchedule = "trigger.schedule";
    public const string TriggerAppLaunch = "trigger.app_launch";
    public const string TriggerSource = "trigger.source";
    public const string ConditionEvent = "condition.event";
    public const string ConditionTime = "condition.time";
    public const string ConditionReceiver = "condition.receiver";
    public const string ActionScene = "action.scene";
    public const string ActionPlugin = "action.plugin";
    public const string ActionReceiver = "action.receiver";
    public const string ActionDelay = "action.delay";
    public const string ActionLearnedVolume = "action.learned_volume";

    /// <summary>Plugin action parameters are config keys with this prefix, e.g. "p:roomId".</summary>
    public const string ParameterPrefix = "p:";

    private static readonly IReadOnlyList<string> OutPort = [PortOut];
    private static readonly IReadOnlyList<string> YesNoPorts = [PortYes, PortNo];

    private static readonly IReadOnlyList<AutomationFieldOption> Days =
        [new("Daily", "Every day"), new("Weekdays", "Monday to Friday"), new("Weekends", "Saturday and Sunday")];

    public static IReadOnlyList<AutomationNodeTypeInfo> All { get; } =
    [
        new(TriggerEvent, GroupTrigger, "Room event",
            "Starts when something happens in the room: the receiver turns on, the input changes, a cast starts.",
            OutPort, false,
            [
                new("event", "Event", "event", Required: true, Placeholder: "power.changed",
                    Help: "An event name, or a prefix with * such as cast.*. Changes made by an automation never start it."),
                new("filter", "Only when", "filter", Placeholder: "stream=avr, to=on",
                    Help: "key=value terms, comma-separated; key!=value for 'not'. stream, device and origin test the event itself."),
            ]),
        new(TriggerWebhook, GroupTrigger, "Webhook",
            "Starts when an iPhone Shortcut or another device calls POST /api/hooks/{name} with the owner token.",
            OutPort, false,
            [
                new("hook", "Hook name", "hook", Required: true, Placeholder: "arrive-home",
                    Help: "Letters, digits, '-' or '_'. The JSON body fields become event data."),
                new("filter", "Only when", "filter", Placeholder: "focus=Sleep"),
            ]),
        new(TriggerSchedule, GroupTrigger, "Schedule", "Starts at a time of day.",
            OutPort, false,
            [
                new("time", "Time", "time", Required: true, Placeholder: "22:00"),
                new("days", "Days", "days", Options: Days),
            ]),
        new(TriggerSource, GroupTrigger, "Source changes",
            "Starts when what plays through the receiver changes and holds for 5 s: a TV channel, a TV or streaming app, an input.",
            OutPort, false,
            [
                new("source", "Only for", "text", Placeholder: "DR1",
                    Help: "A source name as Volume memory shows it (DR1, Netflix, Media Player). Leave empty for any source."),
                new("filter", "Only when", "filter", Placeholder: "kind=tv_channel",
                    Help: "kind is tv_channel, tv_app, cast, appletv or input; from is the source before."),
            ]),
        new(TriggerAppLaunch, GroupTrigger, "App starts", "Starts when a program starts on this PC.",
            OutPort, false,
            [
                new("processes", "Programs", "text", Required: true, Placeholder: "vlc, mpv, Plex",
                    Help: "Process names without .exe, comma-separated."),
            ]),
        new(ConditionEvent, GroupCondition, "Event data", "Checks the data of the event that started the run.",
            YesNoPorts, true,
            [new("filter", "Data must match", "filter", Required: true, Placeholder: "to=TV")]),
        new(ConditionTime, GroupCondition, "Time window", "Checks the time of day. The window may pass midnight.",
            YesNoPorts, true,
            [
                new("from", "From", "time", Required: true, Placeholder: "17:00"),
                new("to", "To", "time", Required: true, Placeholder: "01:00"),
                new("days", "Days", "days", Options: Days),
            ]),
        new(ConditionReceiver, GroupCondition, "Receiver state", "Checks the receiver right now: power, input, mute.",
            YesNoPorts, true,
            [
                new("power", "Power", "select",
                    Options: [new("any", "Any"), new("on", "On"), new("standby", "Standby")]),
                new("input", "Input", "input", Help: "Leave empty for any input."),
                new("muted", "Mute", "select",
                    Options: [new("any", "Any"), new("yes", "Muted"), new("no", "Not muted")]),
            ]),
        new(ActionScene, GroupAction, "Smart Scene", "Runs a Smart Scene: receiver settings and its plugin steps.",
            OutPort, true,
            [new("sceneId", "Scene", "scene", Required: true)]),
        new(ActionPlugin, GroupAction, "Plugin action", "Runs an action of a plugin: Hue, LG TV, Home Assistant and more.",
            OutPort, true,
            [new("action", "Action", "pluginAction", Required: true)]),
        new(ActionReceiver, GroupAction, "Receiver", "Sends one command to the receiver.",
            OutPort, true,
            [
                new("command", "Command", "select", Required: true,
                    Options:
                    [
                        new("power_on", "Power on"), new("power_off", "Standby"), new("mute", "Mute"), new("unmute", "Unmute"),
                        new("volume", "Volume"), new("input", "Input"), new("sound_mode", "Sound mode"),
                    ]),
                new("value", "Value", "text", Placeholder: "45, TV or MOVIE",
                    Help: "Volume 0–98 (capped at the safe volume), an input code, or a sound mode."),
            ]),
        new(ActionLearnedVolume, GroupAction, "Learned volume",
            "Sets the volume Volume memory learned for what plays now, capped at the safe volume.",
            OutPort, true, []),
        new(ActionDelay, GroupAction, "Wait", "Waits before the next step.",
            OutPort, true,
            [new("seconds", "Seconds", "number", Required: true, Placeholder: "5", Help: "1 to 600.")]),
    ];

    /// <summary>Event names a room event trigger can use (any other name works too).</summary>
    public static IReadOnlyList<string> KnownEventNames { get; } =
    [
        "power.changed", "input.changed", "sound_mode.changed", "mute.changed", "volume.settled", "zone2.changed",
        "sleep_timer.changed", "connection.changed", "session.started", "session.ended", "app.changed",
        "scene.activated", "lighting.state_changed", "appletv.woke", "appletv.slept", "appletv.playback_changed",
        "cast.app_started", "cast.playback_changed", "cast.app_ended", "homepod.connected", "homepod.disconnected",
        "source.changed",
    ];

    private static readonly Dictionary<string, AutomationNodeTypeInfo> ByType =
        All.ToDictionary(static t => t.Type, StringComparer.Ordinal);

    public static AutomationNodeTypeInfo? Find(string? type) =>
        type is not null && ByType.TryGetValue(type, out var info) ? info : null;

    public static bool IsTrigger(string? type) => Find(type)?.Group == GroupTrigger;

    public static bool IsCondition(string? type) => Find(type)?.Group == GroupCondition;
}

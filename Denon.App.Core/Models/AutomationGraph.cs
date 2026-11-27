namespace Denon.App.Core.Models;

/// <summary>
/// An automation: trigger nodes start it, condition nodes choose a path and action nodes do things in order.
/// See docs/automation-graphs.md.
/// </summary>
public sealed class AutomationGraph
{
    public string Id { get; set; } = NewId("g");

    public string Name { get; set; } = "New automation";

    public bool Enabled { get; set; }

    /// <summary>Minimum seconds between two runs. Event triggers wait at least 10 s whatever this says.</summary>
    public int CooldownSeconds { get; set; } = 120;

    public List<AutomationNode> Nodes { get; set; } = [];

    public List<AutomationEdge> Edges { get; set; } = [];

    public AutomationGraph Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            CooldownSeconds = CooldownSeconds,
            Nodes = Nodes.Select(static n => n.Clone()).ToList(),
            Edges = Edges.Select(static e => e.Clone()).ToList(),
        };

    public AutomationNode? FindNode(string id) =>
        Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Replaces the graph with the same id in <paramref name="graphs"/>, or adds it. True when it replaced one.</summary>
    public static bool Upsert(List<AutomationGraph> graphs, AutomationGraph graph)
    {
        var index = graphs.FindIndex(g => string.Equals(g.Id, graph.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            graphs[index] = graph;
            return true;
        }

        graphs.Add(graph);
        return false;
    }

    /// <summary>The example automations of a new install, all off.</summary>
    public static List<AutomationGraph> CreateDefaults() =>
    [
        Chain("auto-cinema-app", "Media player starts → Movie Night", 180,
            Node("t", AutomationNodeTypes.TriggerAppLaunch, ("processes", "vlc, mpv, Plex, Kodi")),
            Node("a1", AutomationNodeTypes.ActionScene, ("sceneId", "movie-night")),
            Node("a2", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.movie_dim"))),
        Chain("auto-night-guard", "Night Guard at 22:00", 300,
            Node("t", AutomationNodeTypes.TriggerSchedule, ("time", "22:00"), ("days", "Daily")),
            Node("c", AutomationNodeTypes.ConditionReceiver, ("power", "on"), ("input", ""), ("muted", "any")),
            Node("a1", AutomationNodeTypes.ActionScene, ("sceneId", "late-night")),
            Node("a2", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.candlelight"))),
        Chain("auto-evening-lights", "Receiver on in the evening → lights on", 120,
            Node("t", AutomationNodeTypes.TriggerEvent, ("event", "power.changed"), ("filter", "stream=avr, to=on")),
            Node("c", AutomationNodeTypes.ConditionTime, ("from", "17:00"), ("to", "01:00"), ("days", "Daily")),
            Node("a1", AutomationNodeTypes.ActionPlugin, ("pluginId", "philips-hue"), ("actionId", "hue.all_on"))),
        Chain("auto-arrive-home", "iPhone: arrive home → Morning Jazz", 0,
            Node("t", AutomationNodeTypes.TriggerWebhook, ("hook", "arrive-home"), ("filter", "")),
            Node("c", AutomationNodeTypes.ConditionReceiver, ("power", "idle"), ("input", ""), ("muted", "any")),
            Node("a1", AutomationNodeTypes.ActionScene, ("sceneId", "morning-coffee"))),
        LiveTvOnPowerOn(),
    ];

    /// <summary>
    /// The TV came on away from Live TV (it resumes the last input, often HDMI) → Live TV. Not when a cast or the
    /// Apple TV woke it: those turn the TV on over HDMI-CEC to show their own picture. The wait lets their events
    /// arrive first; origin=external leaves out the app's own HDMI-CEC wake (that one goes to Live TV itself).
    /// </summary>
    private static AutomationGraph LiveTvOnPowerOn()
    {
        var graph = Chain("auto-tv-live", "TV on → Live TV", 60,
            Node("t", AutomationNodeTypes.TriggerEvent, ("event", "app.changed"),
                ("filter", "stream=tv, fromAppId=, toAppId!=com.webos.app.livetv")),
            Node("w", AutomationNodeTypes.ActionDelay, ("seconds", "4")),
            Node("c", AutomationNodeTypes.ConditionRecentEvent,
                ("event", "cast.app_started, appletv.woke"), ("seconds", "30"), ("filter", "origin=external")),
            Node("a1", AutomationNodeTypes.ActionPlugin, ("pluginId", "smart-tv"), ("actionId", "tv.livetv")));
        // No recent cast: switch. Chain wires a condition's "yes"; this one acts on "no".
        graph.Edges.Single(e => e.From == "c").Port = AutomationNodeTypes.PortNo;
        return graph;
    }

    private static AutomationNode Node(string id, string type, params (string Key, string Value)[] config) =>
        new() { Id = id, Type = type, Config = config.ToDictionary(static c => c.Key, static c => c.Value) };

    /// <summary>A straight line of nodes; a condition passes on its "yes" port. Laid out left to right.</summary>
    private static AutomationGraph Chain(string id, string name, int cooldown, params AutomationNode[] nodes)
    {
        var graph = new AutomationGraph { Id = id, Name = name, Enabled = false, CooldownSeconds = cooldown, Nodes = [.. nodes] };
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i].X = 60 + i * 260;
            nodes[i].Y = 120;
            if (i > 0)
            {
                var port = AutomationNodeTypes.IsCondition(nodes[i - 1].Type) ? AutomationNodeTypes.PortYes : AutomationNodeTypes.PortOut;
                graph.Edges.Add(new AutomationEdge { From = nodes[i - 1].Id, Port = port, To = nodes[i].Id });
            }
        }

        return graph;
    }
}

public sealed class AutomationNode
{
    public string Id { get; set; } = AutomationGraph.NewId("n");

    public string Type { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public Dictionary<string, string> Config { get; set; } = [];

    public string Get(string key) => Config.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

    public AutomationNode Clone() =>
        new() { Id = Id, Type = Type, X = X, Y = Y, Config = new Dictionary<string, string>(Config) };
}

public sealed class AutomationEdge
{
    public string From { get; set; } = string.Empty;

    /// <summary>The output port of <see cref="From"/>: "out", or "yes"/"no" on a condition.</summary>
    public string Port { get; set; } = AutomationNodeTypes.PortOut;

    public string To { get; set; } = string.Empty;

    public AutomationEdge Clone() => new() { From = From, Port = Port, To = To };
}

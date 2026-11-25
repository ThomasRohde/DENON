# Automation graphs

An automation is a small graph: one or more **trigger** nodes start it, **condition** nodes choose a path, and
**action** nodes do things in order. Graphs are saved in `settings.json` (`automationGraphs`) and edited in the node
editor, a page that the app serves on `127.0.0.1` (the Insights server) and shows in its own window.

## Graph JSON

```json
{
  "id": "g-3f9a1c",
  "name": "Receiver on in the evening → lights",
  "enabled": false,
  "cooldownSeconds": 120,
  "nodes": [
    { "id": "n1", "type": "trigger.event", "x": 80, "y": 120,
      "config": { "event": "power.changed", "filter": "stream=avr, to=on" } },
    { "id": "n2", "type": "condition.time", "x": 340, "y": 120,
      "config": { "from": "17:00", "to": "01:00", "days": "Daily" } },
    { "id": "n3", "type": "action.plugin", "x": 600, "y": 80,
      "config": { "pluginId": "philips-hue", "actionId": "hue.all_on" } }
  ],
  "edges": [
    { "from": "n1", "port": "out", "to": "n2" },
    { "from": "n2", "port": "yes", "to": "n3" }
  ]
}
```

- `id`: graph ids and node ids are short strings (letters, digits, `-`, `_`). Node ids are unique in a graph.
- `x`, `y`: the node's top-left position on the canvas, in canvas units. Only the editor uses them.
- `config`: a flat map of strings. Plugin action parameters use keys `p:<name>`, e.g. `p:roomId`.
- `edges`: `port` is the output port of `from`. Every node except a trigger has one input.

## Node types

| Type | Group | Output ports | Config |
|---|---|---|---|
| `trigger.event` | trigger | `out` | `event` (e.g. `power.changed`, `cast.*`), `filter` (`key=value, key!=value`) |
| `trigger.webhook` | trigger | `out` | `hook` (name in `POST /api/hooks/{hook}`), `filter` |
| `trigger.schedule` | trigger | `out` | `time` (`HH:mm`), `days` (`Daily`, `Weekdays`, `Weekends`) |
| `trigger.app_launch` | trigger | `out` | `processes` (comma-separated, without `.exe`) |
| `condition.event` | condition | `yes`, `no` | `filter`: tests the data of the event that started the run |
| `condition.time` | condition | `yes`, `no` | `from`, `to` (`HH:mm`, may pass midnight), `days` |
| `condition.receiver` | condition | `yes`, `no` | `power` (`any`, `on`, `standby`), `input` (code or empty), `muted` (`any`, `yes`, `no`) |
| `action.scene` | action | `out` | `sceneId` |
| `action.plugin` | action | `out` | `pluginId`, `actionId`, `p:<name>` parameters |
| `action.receiver` | action | `out` | `command` (`power_on`, `power_off`, `mute`, `unmute`, `volume`, `input`, `sound_mode`), `value` |
| `action.delay` | action | `out` | `seconds` (1–600) |

A receiver volume set by an automation is capped by the safe-volume ceiling (Settings → MCP, `MaxSafeVolume`), as for
AI agents: nobody watches an automation when it runs.

## How a run works

1. A trigger fires with an event: a room event, a webhook call (its JSON fields are the event data), a schedule or an
   app start. The graph runs only if it is enabled and out of its cooldown.
2. The runner walks the edges from that trigger. An action runs, then its `out` edges run in the order they were made.
   A condition follows `yes` or `no`. A node with several outgoing edges runs each branch in turn.
3. A run stops after 100 steps. Graphs must have no cycles (the editor and the server refuse them).
4. Loop protection as for room event rules: events caused by an automation never start an event trigger, event
   triggers wait at least 10 s per graph, and more than 20 event-started runs in a minute pause event triggers for a
   minute. Webhooks are not paused.

## Editor API (Insights server, `127.0.0.1`, cookie key)

The editor page is `/automations/` (assets `/automations/editor.js` and `/automations/editor.css`). Requests that
change something (`PUT`, `POST`, `DELETE`) must send the header `X-Denon-Request: 1` and an `Origin` that is the
server's own (browsers send it on these methods), and, when they have a body, the content type `application/json`.
A body over 512 KB gets `413`. All answers are JSON.

| Request | Answer |
|---|---|
| `GET /api/automations` | `{ graphs: [Graph], runs: { [graphId]: Run }, paused: bool }` |
| `GET /api/automations/catalog` | `Catalog` (below) |
| `GET /api/automations/events?limit=30` | `{ events: [{ ts, stream, device, event, origin, data }] }`, newest first |
| `PUT /api/automations/{id}` body `Graph` | `200 { graph }` or `400 { error, problems: [{ nodeId?, message }] }` |
| `DELETE /api/automations/{id}` | `200 { deleted: id }` |
| `POST /api/automations/{id}/enabled` body `{ enabled: bool }` | `200 { graph }`: changes only `enabled` of the saved graph (the on/off switch) |
| `POST /api/automations/{id}/test` body `{ dryRun: bool, triggerNodeId?: string, data?: { key: value }, graph?: Graph }` | `200 Run`; with `graph`, that unsaved graph runs instead of the saved one (the editor's Dry run sends its draft this way and saves nothing) |

`Run`: `{ at, graphId, trigger, dryRun, success, summary, steps: [{ nodeId, outcome, message }] }` where `outcome` is
`ran`, `yes`, `no`, `failed`, `skipped` or `would-run` (dry run). `runs` in the list holds the last run per graph.

`Catalog`:

```json
{
  "nodeTypes": [
    { "type": "trigger.event", "group": "trigger", "title": "Room event", "description": "…",
      "ports": ["out"], "hasInput": false,
      "fields": [
        { "key": "event", "label": "Event", "kind": "event", "required": true, "help": "…" },
        { "key": "filter", "label": "Only when", "kind": "filter", "placeholder": "stream=avr, to=on" }
      ] }
  ],
  "scenes": [{ "id": "movie-night", "name": "Movie Night" }],
  "pluginActions": [{ "pluginId": "philips-hue", "pluginName": "Philips Hue Lighting", "actionId": "hue.all_on",
                      "name": "All Hue Lights On",
                      "parameters": [{ "name": "roomId", "type": "string", "description": "…", "required": false }] }],
  "eventNames": ["power.changed", "input.changed", "…"],
  "inputs": [{ "code": "TV", "name": "TV Audio" }],
  "soundModes": ["MOVIE", "STEREO", "…"],
  "webhookBaseUrl": "http://192.168.1.31:18925/api/hooks/",
  "maxSafeVolume": 65
}
```

Field `kind` values: `text`, `number`, `time`, `days`, `select` (with `options: [{ value, label }]`), `event`
(free text with `eventNames` as suggestions), `filter`, `hook`, `scene`, `pluginAction` (picks `pluginId` +
`actionId`, then shows the action's parameters as `p:<name>` fields), `input`, `soundMode`.

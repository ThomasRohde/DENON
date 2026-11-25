// Denon Desktop automation editor: a node graph per automation (docs/automation-graphs.md), drawn as DOM nodes with
// SVG edges, saved through the app's local API. No libraries, no network beyond this PC, no HTML from data: every
// text from the API goes in with textContent.
"use strict";

const NS = "http://www.w3.org/2000/svg";
const SNAP = 10;
const ZOOM_MIN = 0.3;
const ZOOM_MAX = 2;
const POLL_MS = 5000;
const RUN_FADE_MS = 20000;
const GROUPS = [
  ["trigger", "Triggers", "Trigger"],
  ["condition", "Conditions", "If"],
  ["action", "Actions", "Do"],
];
const OUTCOME = {
  ran: { tone: "ok", label: "Ran" },
  yes: { tone: "ok", label: "Yes" },
  no: { tone: "muted", label: "No" },
  failed: { tone: "failed", label: "Failed" },
  "would-run": { tone: "info", label: "Would run" },
  skipped: { tone: "dim", label: "Skipped" },
};
const RECEIVER_COMMANDS = {
  power_on: "Power on",
  power_off: "Power off",
  mute: "Mute",
  unmute: "Unmute",
  volume: "Volume",
  input: "Input",
  sound_mode: "Sound mode",
};
const LOCKED_TEXT = "Open the automation editor from Denon Desktop (Scenes → Automation Rules).";

const state = {
  catalog: null,
  types: new Map(),
  graphs: [], // as saved on the server
  runs: {},
  paused: false,
  locked: false,
  offline: false,
  loaded: false,

  openId: null,
  draft: null, // the graph being edited
  baseline: "", // stable JSON of the saved version of the draft
  isNew: false, // the draft is not on the server yet

  selection: null, // { kind: "node", id } | { kind: "edge", key }
  problems: [],
  overlay: null, // { run, steps: Map(nodeId → step) }
  overlayTimers: [],
  seenRunAt: {},
  busy: false,

  view: { x: 60, y: 60, z: 1 },
  views: new Map(),
  nodeEls: new Map(),
  edgeSvg: null,
  drag: null,
  pending: null,
  space: false,

  events: [],
  eventsOpen: false,
  paletteCollapsed: false,
  paletteNarrow: false,
};

// ---------------------------------------------------------------- tiny DOM helpers (text only, never HTML)
function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  setAttrs(el, attrs);
  append(el, kids);
  return el;
}

function s(tag, attrs, ...kids) {
  const el = document.createElementNS(NS, tag);
  setAttrs(el, attrs);
  append(el, kids);
  return el;
}

function setAttrs(el, attrs) {
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") el.setAttribute("class", v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else if (k === "text") el.textContent = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (k === "value") el.value = v;
    else el.setAttribute(k, v === true ? "" : v);
  }
}

function append(el, kids) {
  for (const kid of kids.flat(Infinity)) {
    if (kid === null || kid === undefined || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
}

const $ = (id) => document.getElementById(id);
const clone = (v) => structuredClone(v);
const snap = (v) => Math.round(v / SNAP) * SNAP;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const plural = (n, one, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

// JSON with sorted keys, to compare graphs whatever the key order.
function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value)
      .filter((k) => value[k] !== undefined)
      .sort()
      .map((k) => `${JSON.stringify(k)}:${stable(value[k])}`)
      .join(",")}}`;
  }
  return JSON.stringify(value);
}

// ---------------------------------------------------------------- formatting
const fmtTime = (d) => d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const fmtSeconds = (d) =>
  d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });

function toDate(value) {
  if (value === null || value === undefined || value === "") return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

function fmtWhen(value) {
  const d = toDate(value);
  if (!d) return "";
  const now = new Date();
  if (d.toDateString() === now.toDateString()) return fmtTime(d);
  if (now - d < 6 * 864e5) return `${d.toLocaleDateString(undefined, { weekday: "short" })} ${fmtTime(d)}`;
  return `${d.toLocaleDateString(undefined, { day: "numeric", month: "short" })} ${fmtTime(d)}`;
}

function fmtData(data) {
  if (data === null || data === undefined) return "";
  if (typeof data !== "object") return String(data);
  return Object.entries(data)
    .map(([k, v]) => `${k}=${v !== null && typeof v === "object" ? JSON.stringify(v) : v}`)
    .join(", ");
}

// ---------------------------------------------------------------- API
async function api(method, path, body) {
  const init = { method, cache: "no-store", credentials: "same-origin", headers: {} };
  if (method !== "GET") init.headers["X-Denon-Request"] = "1";
  if (body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(path, init);
  } catch {
    setOffline(true);
    throw Object.assign(new Error("Denon Desktop is not reachable."), { offline: true });
  }
  setOffline(false);
  let data = null;
  try {
    data = await response.json();
  } catch {
    data = null;
  }
  if (response.status === 401) {
    setLocked(true);
    throw Object.assign(new Error(LOCKED_TEXT), { locked: true, status: 401, data });
  }
  if (!response.ok) {
    throw Object.assign(new Error(data?.error || `The app answered ${response.status}.`), { status: response.status, data });
  }
  if (method === "GET") setLocked(false);
  return data;
}

function setLocked(locked) {
  if (state.locked === locked) return;
  state.locked = locked;
  renderBanner();
  renderStatus();
  updateToolbar();
}

function setOffline(offline) {
  if (state.offline === offline) return;
  state.offline = offline;
  renderBanner();
  renderStatus();
}

function renderStatus() {
  const el = $("status");
  const [kind, text] = state.locked
    ? ["locked", "Locked"]
    : state.offline
      ? ["offline", "App not reachable"]
      : state.loaded
        ? ["live", "Live"]
        : ["", "Connecting…"];
  el.dataset.state = kind;
  el.querySelector(".status-text").textContent = text;
}

function renderBanner() {
  const el = $("banner");
  let text = "";
  let tone = "warn";
  if (state.locked) {
    text = LOCKED_TEXT;
    tone = "error";
  } else if (state.offline) {
    text = "Denon Desktop is not running or not reachable. Start it, then open the automation editor again.";
    tone = "error";
  } else if (state.paused) {
    text = "Event triggers are paused for a minute: too many runs looked like a loop.";
  }
  el.hidden = !text;
  el.dataset.tone = tone;
  el.textContent = text;
}

// ---------------------------------------------------------------- toast and dialog
let toastTimer = 0;
function toast(text, tone = "", ms = 3200) {
  const el = $("toast");
  el.textContent = text;
  el.dataset.tone = tone;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), ms);
}

// An in-page confirm. Buttons: [{ label, value, tone, focus }]. Resolves with the value, or null for Escape.
function ask({ title, text, buttons }) {
  const dialog = $("dialog");
  $("dialog-title").textContent = title;
  $("dialog-text").textContent = text ?? "";
  $("dialog-text").hidden = !text;
  const actions = $("dialog-actions");
  actions.replaceChildren();
  let focus = null;
  for (const b of buttons) {
    const btn = h("button", { type: "button", class: `btn ${b.tone ?? ""}`.trim(), text: b.label });
    btn.addEventListener("click", () => dialog.close(b.value));
    actions.append(btn);
    if (b.focus) focus = btn;
  }
  return new Promise((resolve) => {
    dialog.addEventListener(
      "close",
      () => {
        const v = dialog.returnValue;
        dialog.returnValue = "";
        resolve(v === "" ? null : v);
      },
      { once: true },
    );
    dialog.returnValue = "";
    dialog.showModal();
    focus?.focus();
  });
}

// ---------------------------------------------------------------- catalog and graph helpers
function typeInfo(type) {
  const t = state.types.get(type);
  if (t) return t;
  const group = String(type).split(".")[0];
  return {
    type,
    group: ["trigger", "condition", "action"].includes(group) ? group : "action",
    title: type,
    description: "This node type is not known to this version of the app.",
    ports: group === "condition" ? ["yes", "no"] : ["out"],
    hasInput: group !== "trigger",
    fields: [],
  };
}

const groupLabel = (group) => GROUPS.find((g) => g[0] === group)?.[2] ?? group;
const portsOf = (type) => (typeInfo(type).ports?.length ? typeInfo(type).ports : ["out"]);
const hasInput = (type) => typeInfo(type).hasInput !== false && typeInfo(type).group !== "trigger";
const isTrigger = (node) => typeInfo(node.type).group === "trigger";
const edgeKey = (e) => `${e.from}|${e.port}|${e.to}`;
const nodeById = (id) => state.draft?.nodes.find((n) => n.id === id);

function normalize(graph) {
  return {
    ...graph,
    id: String(graph.id),
    name: String(graph.name ?? ""),
    enabled: !!graph.enabled,
    cooldownSeconds: Number.isFinite(Number(graph.cooldownSeconds)) ? Number(graph.cooldownSeconds) : 120,
    nodes: (graph.nodes ?? []).map((n) => {
      const config = {};
      for (const [k, v] of Object.entries(n.config ?? {})) {
        if (v !== null && v !== undefined && v !== "") config[k] = String(v);
      }
      return { ...n, id: String(n.id), type: String(n.type), x: Math.round(Number(n.x) || 0), y: Math.round(Number(n.y) || 0), config };
    }),
    edges: (graph.edges ?? []).map((e) => ({ from: String(e.from), port: String(e.port || "out"), to: String(e.to) })),
  };
}

function randomHex(n) {
  const bytes = new Uint8Array(Math.ceil(n / 2));
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("").slice(0, n);
}

function newGraphId() {
  let id;
  do id = `g-${randomHex(6)}`;
  while (state.graphs.some((g) => g.id === id));
  return id;
}

function newNodeId() {
  const used = new Set(state.draft.nodes.map((n) => n.id));
  let i = 1;
  while (used.has(`n${i}`)) i++;
  return `n${i}`;
}

function isDirty() {
  return !!state.draft && (state.isNew || stable(normalize(state.draft)) !== state.baseline);
}

// Whether `target` can be reached from `start` along the edges (so an edge target → start would close a loop).
function reaches(start, target) {
  const seen = new Set();
  const stack = [start];
  while (stack.length) {
    const id = stack.pop();
    if (id === target) return true;
    if (seen.has(id)) continue;
    seen.add(id);
    for (const e of state.draft.edges) if (e.from === id) stack.push(e.to);
  }
  return false;
}

// Why an edge from → to on that port is refused, or null when it is fine.
function connectProblem(fromId, port, toId) {
  const to = nodeById(toId);
  if (!to) return "Drop the line on a node.";
  if (fromId === toId) return "A node can't connect to itself.";
  if (!hasInput(to.type)) return "Triggers start a run, so nothing can connect into them.";
  if (state.draft.edges.some((e) => e.from === fromId && e.port === port && e.to === toId)) return "These two are already connected.";
  if (reaches(toId, fromId)) return "That would make a loop. A run can't come back to an earlier step.";
  return null;
}

// ---------------------------------------------------------------- summaries
const sceneName = (id) => state.catalog?.scenes?.find((sc) => sc.id === id)?.name ?? id;
const inputName = (code) => state.catalog?.inputs?.find((i) => i.code === code)?.name ?? code;
const pluginAction = (pluginId, actionId) =>
  state.catalog?.pluginActions?.find((a) => a.pluginId === pluginId && a.actionId === actionId) ?? null;

function optionLabel(type, key, value) {
  const field = typeInfo(type).fields?.find((f) => f.key === key);
  return field?.options?.find((o) => o.value === value)?.label ?? value;
}

function nodeSummary(node) {
  const c = node.config ?? {};
  const join = (...parts) => parts.filter(Boolean).join(" · ");
  switch (node.type) {
    case "trigger.event":
      return join(c.event, c.filter);
    case "trigger.webhook":
      return c.hook ? join(`POST /api/hooks/${c.hook}`, c.filter) : "";
    case "trigger.schedule":
      return c.time ? `${c.time} ${c.days || "Daily"}` : "";
    case "trigger.app_launch":
      return (c.processes ?? "")
        .split(",")
        .map((p) => p.trim())
        .filter(Boolean)
        .join(", ");
    case "condition.event":
      return c.filter ?? "";
    case "condition.time":
      return c.from || c.to ? `${c.from || "…"}–${c.to || "…"} ${c.days || "Daily"}` : "";
    case "condition.receiver": {
      const parts = [];
      if (c.power === "on") parts.push("Power on");
      else if (c.power === "standby") parts.push("In standby");
      if (c.input) parts.push(`Input ${inputName(c.input)}`);
      if (c.muted === "yes") parts.push("Muted");
      else if (c.muted === "no") parts.push("Not muted");
      return parts.join(" · ") || "Any receiver state";
    }
    case "action.scene":
      return c.sceneId ? `Scene: ${sceneName(c.sceneId)}` : "";
    case "action.plugin": {
      if (!c.actionId) return "";
      const action = pluginAction(c.pluginId, c.actionId);
      const params = Object.entries(c)
        .filter(([k]) => k.startsWith("p:"))
        .map(([k, v]) => `${k.slice(2)}=${v}`)
        .join(", ");
      return join(action ? action.name : c.actionId, params);
    }
    case "action.receiver": {
      if (!c.command) return "";
      const label = optionLabel(node.type, "command", c.command) || RECEIVER_COMMANDS[c.command] || c.command;
      if (c.command === "volume") {
        const cap = state.catalog?.maxSafeVolume;
        const over = cap !== undefined && Number(c.value) > cap;
        return c.value ? `Volume ${c.value}${over ? ` (capped at ${cap})` : ""}` : "Volume";
      }
      if (c.command === "input") return c.value ? `Input: ${inputName(c.value)}` : "Input";
      if (c.command === "sound_mode") return c.value ? `Sound mode: ${c.value}` : "Sound mode";
      return RECEIVER_COMMANDS[c.command] ?? label;
    }
    case "action.delay":
      return c.seconds ? `Wait ${c.seconds} s` : "";
    default:
      return Object.values(c).filter(Boolean).join(" · ");
  }
}

function graphSummary(graph) {
  const nodes = graph.nodes ?? [];
  if (!nodes.length) return "Empty";
  const triggers = nodes.filter(isTrigger);
  const conditions = nodes.filter((n) => typeInfo(n.type).group === "condition").length;
  const actions = nodes.filter((n) => typeInfo(n.type).group === "action").length;
  let left = "No trigger";
  if (triggers.length) {
    const t = triggers[0];
    const c = t.config ?? {};
    const detail = c.event || c.hook || c.time || c.processes || "";
    left = `${typeInfo(t.type).title}${detail ? ` ${detail}` : ""}${triggers.length > 1 ? ` +${triggers.length - 1}` : ""}`;
  }
  const right = [conditions ? plural(conditions, "condition") : "", actions ? plural(actions, "action") : ""].filter(Boolean);
  return `${left} → ${right.join(", ") || "nothing yet"}`;
}

// ---------------------------------------------------------------- sidebar: the automations
let listFrame = 0;
function scheduleList() {
  if (listFrame) return;
  listFrame = requestAnimationFrame(() => {
    listFrame = 0;
    renderList();
  });
}

function listedGraphs() {
  const list = state.graphs.map((g) => (g.id === state.openId && state.draft ? { ...state.draft, enabled: g.enabled } : g));
  if (state.isNew && state.draft) list.unshift(state.draft);
  return list;
}

function renderList() {
  const ul = $("graph-list");
  const active = document.activeElement;
  let refocus = null;
  if (active && ul.contains(active)) {
    refocus = { id: active.closest(".graph-item")?.dataset.id, cls: active.classList.contains("switch") ? ".switch" : ".gi-open" };
  }
  ul.replaceChildren();
  const graphs = listedGraphs();
  if (!graphs.length) {
    ul.append(h("li", { class: "list-empty" }, state.loaded ? "No automations yet. Click New to make one." : "Loading…"));
  }
  const dirty = isDirty();
  for (const g of graphs) {
    const open = g.id === state.openId;
    const isNewOne = open && state.isNew;
    const run = state.runs[g.id];
    const name = g.name || "Untitled automation";
    const sw = h("input", {
      type: "checkbox",
      class: "switch",
      role: "switch",
      "aria-label": `Enabled: ${name}`,
      title: g.enabled ? "On" : "Off",
      disabled: state.locked,
    });
    sw.checked = !!g.enabled;
    sw.addEventListener("change", () => toggleEnabled(g.id, sw.checked, sw));
    const runState = run ? (run.dryRun ? "dry" : run.success ? "ok" : "failed") : "";
    const runText = run
      ? `${fmtWhen(run.at)} · ${run.dryRun ? "dry run" : run.success ? "ok" : "failed"}`
      : isNewOne
        ? "Not saved yet"
        : "Never run";
    ul.append(
      h(
        "li",
        { class: "graph-item", dataset: { id: g.id }, "aria-current": open ? "true" : null },
        h(
          "button",
          { type: "button", class: "gi-open", onclick: () => openGraph(g.id) },
          h("span", { class: "gi-name", text: name }),
          open && dirty ? h("span", { class: "dirty-dot", title: "Unsaved changes", "aria-label": "Unsaved changes" }) : null,
        ),
        sw,
        h("div", { class: "gi-summary", title: graphSummary(g), text: graphSummary(g) }),
        h("div", { class: "gi-run" }, h("span", { class: "run-dot", dataset: { state: runState } }), runText),
      ),
    );
  }
  if (refocus?.id) ul.querySelector(`.graph-item[data-id="${CSS.escape(refocus.id)}"] ${refocus.cls}`)?.focus();
}

// ---------------------------------------------------------------- palette
function renderPalette() {
  const body = $("palette-body");
  body.replaceChildren();
  for (const [group, title] of GROUPS) {
    const types = state.catalog.nodeTypes.filter((t) => t.group === group);
    if (!types.length) continue;
    body.append(
      h(
        "section",
        { class: "pal-group", dataset: { group } },
        h("h3", { class: "label", text: title }),
        types.map((t) => {
          const item = h(
            "button",
            { type: "button", class: "pal-item", draggable: "true", dataset: { group, type: t.type }, title: `Add ${t.title}` },
            h("span", { class: "pal-title", text: t.title }),
            h("span", { class: "pal-desc", text: t.description ?? "" }),
          );
          item.addEventListener("click", () => {
            addNode(t.type);
            closePaletteFlyout();
          });
          item.addEventListener("dragstart", (e) => {
            e.dataTransfer.setData("application/x-denon-node", t.type);
            e.dataTransfer.setData("text/plain", t.title);
            e.dataTransfer.effectAllowed = "copy";
          });
          return item;
        }),
      ),
    );
  }
}

function applyPaletteLayout() {
  state.paletteNarrow = window.innerWidth < 1360;
  const collapsed = state.paletteNarrow || state.paletteCollapsed;
  $("layout").classList.toggle("palette-collapsed", collapsed);
  $("palette-toggle").setAttribute("aria-expanded", String(!collapsed || $("palette").classList.contains("open")));
  if (!collapsed) $("palette").classList.remove("open");
}

function closePaletteFlyout() {
  if (!$("palette").classList.contains("open")) return;
  $("palette").classList.remove("open");
  applyPaletteLayout();
}

function togglePalette() {
  if (state.paletteNarrow) {
    $("palette").classList.toggle("open");
  } else {
    state.paletteCollapsed = !state.paletteCollapsed;
    try {
      localStorage.setItem("automations.palette", state.paletteCollapsed ? "collapsed" : "open");
    } catch {
      // ignore
    }
  }
  applyPaletteLayout();
}

// ---------------------------------------------------------------- canvas
function applyView() {
  const { x, y, z } = state.view;
  $("world").style.transform = `translate(${x}px, ${y}px) scale(${z})`;
  const canvas = $("canvas");
  canvas.style.backgroundSize = `${22 * z}px ${22 * z}px`;
  canvas.style.backgroundPosition = `${x}px ${y}px`;
  $("zoom-level").textContent = `${Math.round(z * 100)} %`;
}

function screenToWorld(clientX, clientY) {
  const r = $("canvas").getBoundingClientRect();
  return { x: (clientX - r.left - state.view.x) / state.view.z, y: (clientY - r.top - state.view.y) / state.view.z };
}

function zoomAt(factor, clientX, clientY) {
  const r = $("canvas").getBoundingClientRect();
  const cx = clientX ?? r.left + r.width / 2;
  const cy = clientY ?? r.top + r.height / 2;
  const before = screenToWorld(cx, cy);
  state.view.z = clamp(state.view.z * factor, ZOOM_MIN, ZOOM_MAX);
  state.view.x = cx - r.left - before.x * state.view.z;
  state.view.y = cy - r.top - before.y * state.view.z;
  applyView();
}

function nodeBox(node) {
  const el = state.nodeEls.get(node.id);
  return { x: node.x, y: node.y, w: el?.offsetWidth ?? 212, h: el?.offsetHeight ?? 80 };
}

function fit() {
  const canvas = $("canvas");
  const nodes = state.draft?.nodes ?? [];
  if (!nodes.length) {
    state.view = { x: 60, y: 60, z: 1 };
    applyView();
    return;
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const n of nodes) {
    const b = nodeBox(n);
    minX = Math.min(minX, b.x);
    minY = Math.min(minY, b.y);
    maxX = Math.max(maxX, b.x + b.w);
    maxY = Math.max(maxY, b.y + b.h);
  }
  const pad = 56;
  const cw = canvas.clientWidth;
  const ch = canvas.clientHeight;
  // Readable first: below 60 % the text is too small, so a wide graph starts at its left edge instead.
  const z = clamp(Math.min((cw - 2 * pad) / (maxX - minX), (ch - 2 * pad) / (maxY - minY), 1.1), 0.6, ZOOM_MAX);
  const x = Math.max(pad - minX * z, (cw - (maxX - minX) * z) / 2 - minX * z);
  const y = Math.max(pad - minY * z, (ch - (maxY - minY) * z) / 2 - minY * z);
  state.view = { z, x: (maxX - minX) * z > cw - 2 * pad ? pad - minX * z : x, y: (maxY - minY) * z > ch - 2 * pad ? pad - minY * z : y };
  applyView();
}

// Pans so that the node is in view (used when a run step or a problem points at it).
function reveal(nodeId) {
  const node = nodeById(nodeId);
  const canvas = $("canvas");
  if (!node) return;
  const b = nodeBox(node);
  const { x, y, z } = state.view;
  const left = b.x * z + x;
  const top = b.y * z + y;
  if (left < 20 || top < 20 || left + b.w * z > canvas.clientWidth - 20 || top + b.h * z > canvas.clientHeight - 20) {
    state.view.x = canvas.clientWidth / 2 - (b.x + b.w / 2) * z;
    state.view.y = canvas.clientHeight / 2 - (b.y + b.h / 2) * z;
    applyView();
  }
}

function renderCanvas() {
  const world = $("world");
  world.classList.remove("fading");
  world.replaceChildren();
  state.nodeEls.clear();
  state.edgeSvg = s("svg", { class: "edges", "aria-hidden": "true" });
  world.append(state.edgeSvg);
  for (const node of state.draft?.nodes ?? []) {
    const el = buildNode(node);
    state.nodeEls.set(node.id, el);
    world.append(el);
  }
  renderEdges();
  renderEmpty();
  applyView();
}

function renderEmpty() {
  const el = $("canvas-empty");
  el.replaceChildren();
  if (!state.loaded) {
    el.hidden = true;
    return;
  }
  if (!state.draft) {
    el.hidden = false;
    el.append(
      h("strong", { text: state.graphs.length ? "No automation open" : "No automations yet" }),
      h("span", { text: "An automation starts from a trigger and runs actions in order." }),
      h("button", { type: "button", class: "btn primary", text: "New automation", onclick: () => newGraph() }),
    );
    return;
  }
  el.hidden = state.draft.nodes.length > 0;
  el.append(
    h("strong", { text: "Start with a trigger" }),
    h("span", { text: "Click or drag one in from the palette, or turn a recent event into one." }),
  );
}

function buildNode(node) {
  const info = typeInfo(node.type);
  const ports = portsOf(node.type);
  const summary = nodeSummary(node);
  const selected = state.selection?.kind === "node" && state.selection.id === node.id;
  const step = state.overlay?.steps.get(node.id);
  const outcome = step ? OUTCOME[step.outcome] ?? { tone: "muted", label: step.outcome } : null;
  const problems = state.problems.filter((p) => p.nodeId === node.id);

  const single = ports.length === 1;
  const el = h(
    "div",
    {
      class: `node${selected ? " selected" : ""}${problems.length ? " has-problem" : ""}`,
      tabindex: "0",
      role: "group",
      "aria-label": `${groupLabel(info.group)}: ${info.title}. ${summary || "Not set up yet"}`,
      dataset: { id: node.id, group: info.group, outcome: outcome?.tone },
    },
    hasInput(node.type) ? h("span", { class: "handle in", "aria-hidden": "true" }) : null,
    h(
      "div",
      { class: "node-head" },
      h("span", { class: "node-group", text: groupLabel(info.group) }),
      h("span", { class: "node-title", text: info.title }),
      single ? h("span", { class: "handle out", dataset: { port: ports[0] }, title: "Drag to connect", "aria-hidden": "true" }) : null,
    ),
    h("div", { class: `node-summary${summary ? "" : " unset"}`, title: summary || null, text: summary || "Not set up yet" }),
    single
      ? null
      : h(
          "div",
          { class: "node-ports" },
          ports.map((p) =>
            h(
              "div",
              { class: "port-row", dataset: { port: p } },
              p,
              h("span", { class: "handle out", dataset: { port: p }, title: "Drag to connect", "aria-hidden": "true" }),
            ),
          ),
        ),
    problems.map((p) => h("div", { class: "node-note problem", text: p.message })),
    step?.message ? h("div", { class: "node-note step", text: step.message }) : null,
    outcome ? h("span", { class: "node-badge", text: outcome.label }) : null,
  );
  el.style.left = `${node.x}px`;
  el.style.top = `${node.y}px`;
  el.addEventListener("focus", () => {
    if (!(state.selection?.kind === "node" && state.selection.id === node.id)) select({ kind: "node", id: node.id });
  });
  return el;
}

function rerenderNode(node) {
  const old = state.nodeEls.get(node.id);
  if (!old) return;
  const hadFocus = document.activeElement === old;
  const el = buildNode(node);
  old.replaceWith(el);
  state.nodeEls.set(node.id, el);
  if (hadFocus) el.focus({ preventScroll: true });
  renderEdges();
}

// Where a node's handle sits, in canvas units.
function handlePoint(nodeId, selector) {
  const node = nodeById(nodeId);
  const el = state.nodeEls.get(nodeId);
  const handle = el?.querySelector(selector);
  if (!node || !handle) return null;
  let x = handle.offsetWidth / 2;
  let y = handle.offsetHeight / 2;
  for (let cur = handle; cur && cur !== el; cur = cur.offsetParent) {
    x += cur.offsetLeft;
    y += cur.offsetTop;
  }
  return { x: node.x + x, y: node.y + y };
}

const outPoint = (id, port) => handlePoint(id, `.handle.out[data-port="${CSS.escape(port)}"]`);
const inPoint = (id) => handlePoint(id, ".handle.in");

function curve(a, b) {
  const dx = b.x >= a.x ? Math.max(40, (b.x - a.x) * 0.5) : Math.max(90, (a.x - b.x) * 0.5);
  return `M ${a.x} ${a.y} C ${a.x + dx} ${a.y}, ${b.x - dx} ${b.y}, ${b.x} ${b.y}`;
}

function walked(edge) {
  const steps = state.overlay?.steps;
  if (!steps) return false;
  const from = steps.get(edge.from);
  const to = steps.get(edge.to);
  if (!to || to.outcome === "skipped") return false;
  if (!from) return isTrigger(nodeById(edge.from) ?? { type: "" });
  if (from.outcome === "yes") return edge.port === "yes";
  if (from.outcome === "no") return edge.port === "no";
  return from.outcome === "ran" || from.outcome === "would-run";
}

function renderEdges() {
  const svg = state.edgeSvg;
  if (!svg || !state.draft) return;
  const lines = [];
  for (const e of state.draft.edges) {
    const a = outPoint(e.from, e.port);
    const b = inPoint(e.to);
    if (a && b) lines.push({ e, a, b });
  }
  const pts = lines.flatMap((l) => [l.a, l.b]);
  if (state.pending) pts.push(state.pending.a, state.pending.b);
  svg.replaceChildren();
  if (!pts.length) return;
  const m = 160;
  const minX = Math.min(...pts.map((p) => p.x)) - m;
  const minY = Math.min(...pts.map((p) => p.y)) - m;
  const maxX = Math.max(...pts.map((p) => p.x)) + m;
  const maxY = Math.max(...pts.map((p) => p.y)) + m;
  svg.style.left = `${minX}px`;
  svg.style.top = `${minY}px`;
  svg.setAttribute("width", String(maxX - minX));
  svg.setAttribute("height", String(maxY - minY));
  const g = s("g", { transform: `translate(${-minX} ${-minY})` });
  for (const { e, a, b } of lines) {
    const key = edgeKey(e);
    const selected = state.selection?.kind === "edge" && state.selection.key === key;
    const d = curve(a, b);
    g.append(
      s("path", { class: "edge-hit", d, dataset: { key } }),
      s("path", { class: `edge${selected ? " selected" : ""}${walked(e) ? " walked" : ""}`, d, dataset: { port: e.port } }),
    );
  }
  if (state.pending) g.append(s("path", { class: "edge pending", d: curve(state.pending.a, state.pending.b) }));
  svg.append(g);
}

// ---------------------------------------------------------------- selection
function select(selection) {
  state.selection = selection;
  for (const [id, el] of state.nodeEls) el.classList.toggle("selected", selection?.kind === "node" && selection.id === id);
  renderEdges();
  renderInspector();
}

function selectNode(id, { focus = true, show = false } = {}) {
  select({ kind: "node", id });
  if (show) reveal(id);
  if (focus) state.nodeEls.get(id)?.focus({ preventScroll: true });
}

// ---------------------------------------------------------------- edits
function changed() {
  const dirty = isDirty();
  $("doc-dirty").hidden = !dirty;
  $("doc-name").textContent = state.draft ? state.draft.name || "Untitled automation" : "No automation open";
  updateToolbar();
  scheduleList();
}

function updateToolbar() {
  const has = !!state.draft;
  const dirty = isDirty();
  const triggers = state.draft?.nodes.some(isTrigger);
  $("btn-save").disabled = !has || !dirty || state.locked || state.busy;
  $("btn-dry").disabled = !has || !triggers || state.locked || state.busy;
  $("btn-run").disabled = !has || !triggers || state.locked || state.busy;
  $("btn-dup").disabled = !has;
  $("btn-del").disabled = !has || state.locked;
  $("btn-new").disabled = false;
}

function defaultConfig(type) {
  const config = {};
  for (const f of typeInfo(type).fields ?? []) {
    let v = f.default;
    if (v === undefined && f.kind === "select" && f.options?.length) v = f.options[0].value;
    if (v === undefined && f.kind === "days") v = f.options?.[0]?.value ?? "Daily";
    if (v !== undefined && v !== null && v !== "") config[f.key] = String(v);
  }
  return config;
}

// A free spot for a new node: right of the selected node (the next step), or the middle of what the canvas shows
// (above the events drawer when it is open); moved down until it overlaps no other node.
function freeSpot() {
  const sel = state.selection?.kind === "node" ? nodeById(state.selection.id) : null;
  let x;
  let y;
  if (sel) {
    x = sel.x + nodeBox(sel).w + 70;
    y = sel.y;
  } else {
    const drawer = state.eventsOpen ? $("events").offsetHeight : 0;
    const r = $("canvas").getBoundingClientRect();
    const c = screenToWorld(r.left + r.width / 2, r.top + (r.height - drawer) / 2);
    x = c.x - 106;
    y = c.y - 40;
  }
  x = snap(x);
  y = snap(y);
  const overlaps = (px, py) =>
    state.draft.nodes.some((n) => {
      const b = nodeBox(n);
      return px < b.x + b.w + 20 && px + 212 + 20 > b.x && py < b.y + b.h + 20 && py + 80 + 20 > b.y;
    });
  for (let i = 0; i < 40 && overlaps(x, y); i++) y += 40;
  return { x, y };
}

function addNode(type, at, config) {
  if (!state.draft) newGraphNow();
  const pos = at ? { x: snap(at.x), y: snap(at.y) } : freeSpot();
  const node = { id: newNodeId(), type, x: pos.x, y: pos.y, config: { ...defaultConfig(type), ...(config ?? {}) } };
  state.draft.nodes.push(node);
  renderCanvas();
  changed();
  selectNode(node.id, { show: true });
  return node;
}

function removeNode(id) {
  state.draft.nodes = state.draft.nodes.filter((n) => n.id !== id);
  state.draft.edges = state.draft.edges.filter((e) => e.from !== id && e.to !== id);
  state.problems = state.problems.filter((p) => p.nodeId !== id);
  if (state.selection?.kind === "node" && state.selection.id === id) state.selection = null;
  renderCanvas();
  renderInspector();
  changed();
}

function removeEdge(key) {
  state.draft.edges = state.draft.edges.filter((e) => edgeKey(e) !== key);
  if (state.selection?.kind === "edge" && state.selection.key === key) state.selection = null;
  renderEdges();
  renderInspector();
  changed();
}

function deleteSelection() {
  if (!state.draft || !state.selection) return;
  if (state.selection.kind === "node") removeNode(state.selection.id);
  else removeEdge(state.selection.key);
  $("canvas").focus({ preventScroll: true });
}

function duplicateNode(id) {
  const src = nodeById(id);
  if (!src) return;
  const node = { ...clone(src), id: newNodeId(), x: src.x + 30, y: src.y + 30 };
  state.draft.nodes.push(node);
  renderCanvas();
  changed();
  selectNode(node.id);
}

function connect(fromId, port, toId) {
  const problem = connectProblem(fromId, port, toId);
  if (problem) {
    toast(problem, "error");
    return false;
  }
  state.draft.edges.push({ from: fromId, port, to: toId });
  renderEdges();
  changed();
  if (state.selection?.kind === "node") renderInspector();
  return true;
}

function configChanged(node, { inspector = false } = {}) {
  // A problem the server named for this node may be fixed now; the next save says.
  const hadProblems = state.problems.some((p) => p.nodeId === node.id);
  if (hadProblems) state.problems = state.problems.filter((p) => p.nodeId !== node.id);
  rerenderNode(node);
  changed();
  if (inspector) renderInspector({ keepFocus: true });
  else if (hadProblems) refreshProblems();
}

function refreshProblems() {
  const old = $("inspector").querySelector(".problems");
  if (state.problems.length && old) old.replaceWith(problemsBox());
  else if (state.problems.length) $("inspector").prepend(problemsBox());
  else old?.remove();
}

// ---------------------------------------------------------------- pointer: pan, zoom, drag, connect
function onPointerDown(e) {
  if (!state.draft && !e.target.closest(".canvas-empty")) return;
  if (e.button !== 0 && e.button !== 1) return;
  if (e.target.closest("button, input, select, a")) return;
  closePaletteFlyout();
  const canvas = $("canvas");
  const start = { x: e.clientX, y: e.clientY };

  if (state.space || e.button === 1) {
    e.preventDefault();
    beginPan(e, start);
    return;
  }

  const handle = e.target.closest(".handle.out");
  if (handle) {
    const fromId = handle.closest(".node").dataset.id;
    const port = handle.dataset.port;
    const a = outPoint(fromId, port);
    state.pending = { a, b: screenToWorld(e.clientX, e.clientY) };
    state.drag = { mode: "connect", fromId, port, pointerId: e.pointerId };
    handle.classList.add("active");
    for (const [id, el] of state.nodeEls) {
      if (id === fromId) continue;
      el.classList.add(connectProblem(fromId, port, id) ? "drop-no" : "drop-ok");
    }
    canvas.setPointerCapture(e.pointerId);
    renderEdges();
    e.preventDefault();
    return;
  }

  const edgeHit = e.target.closest(".edge-hit");
  if (edgeHit) {
    select({ kind: "edge", key: edgeHit.dataset.key });
    canvas.focus({ preventScroll: true });
    e.preventDefault();
    return;
  }

  const nodeEl = e.target.closest(".node");
  if (nodeEl) {
    const node = nodeById(nodeEl.dataset.id);
    if (!node) return;
    if (!(state.selection?.kind === "node" && state.selection.id === node.id)) select({ kind: "node", id: node.id });
    nodeEl.focus({ preventScroll: true });
    state.drag = { mode: "node", id: node.id, start, origin: { x: node.x, y: node.y }, moved: false, pointerId: e.pointerId };
    canvas.setPointerCapture(e.pointerId);
    e.preventDefault();
    return;
  }

  // Background: clear the selection and pan.
  if (state.selection) select(null);
  canvas.focus({ preventScroll: true });
  beginPan(e, start);
}

function beginPan(e, start) {
  const canvas = $("canvas");
  state.drag = { mode: "pan", start, origin: { x: state.view.x, y: state.view.y }, pointerId: e.pointerId };
  canvas.classList.add("panning");
  canvas.setPointerCapture(e.pointerId);
}

function onPointerMove(e) {
  const d = state.drag;
  if (!d || e.pointerId !== d.pointerId) return;
  if (d.mode === "pan") {
    state.view.x = d.origin.x + (e.clientX - d.start.x);
    state.view.y = d.origin.y + (e.clientY - d.start.y);
    applyView();
  } else if (d.mode === "node") {
    const dx = (e.clientX - d.start.x) / state.view.z;
    const dy = (e.clientY - d.start.y) / state.view.z;
    if (!d.moved && Math.hypot(e.clientX - d.start.x, e.clientY - d.start.y) < 3) return;
    d.moved = true;
    const node = nodeById(d.id);
    node.x = snap(d.origin.x + dx);
    node.y = snap(d.origin.y + dy);
    const el = state.nodeEls.get(d.id);
    el.classList.add("dragging");
    el.style.left = `${node.x}px`;
    el.style.top = `${node.y}px`;
    renderEdges();
  } else if (d.mode === "connect") {
    state.pending.b = screenToWorld(e.clientX, e.clientY);
    const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(".node");
    const p = target && target.dataset.id !== d.fromId ? inPoint(target.dataset.id) : null;
    if (p && target.classList.contains("drop-ok")) state.pending.b = p;
    renderEdges();
  }
}

function onPointerUp(e) {
  const d = state.drag;
  if (!d || e.pointerId !== d.pointerId) return;
  state.drag = null;
  const canvas = $("canvas");
  canvas.classList.remove("panning");
  if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
  if (d.mode === "node") {
    state.nodeEls.get(d.id)?.classList.remove("dragging");
    if (d.moved) changed();
  } else if (d.mode === "connect") {
    endConnect();
    if (e.type === "pointerup") {
      const target = document.elementFromPoint(e.clientX, e.clientY)?.closest(".node");
      if (target && target.dataset.id !== d.fromId) connect(d.fromId, d.port, target.dataset.id);
      else if (target) toast("A node can't connect to itself.", "error");
    }
  }
}

function endConnect() {
  state.pending = null;
  for (const el of state.nodeEls.values()) el.classList.remove("drop-ok", "drop-no");
  for (const h2 of document.querySelectorAll(".handle.out.active")) h2.classList.remove("active");
  renderEdges();
}

function onWheel(e) {
  if (!state.draft) return;
  e.preventDefault();
  const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
  zoomAt(Math.exp(-delta * 0.0015), e.clientX, e.clientY);
}

function onDragOver(e) {
  if (!e.dataTransfer?.types.includes("application/x-denon-node")) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
  $("canvas").classList.add("dropping");
}

function onDrop(e) {
  $("canvas").classList.remove("dropping");
  const type = e.dataTransfer?.getData("application/x-denon-node");
  if (!type) return;
  e.preventDefault();
  const p = screenToWorld(e.clientX, e.clientY);
  addNode(type, { x: p.x - 106, y: p.y - 22 });
  closePaletteFlyout();
}

// ---------------------------------------------------------------- keyboard
function isEditable(el) {
  return !!el?.closest?.("input, select, textarea, [contenteditable]");
}

function onKeyDown(e) {
  const mod = e.ctrlKey || e.metaKey;
  if (mod && (e.key === "s" || e.key === "S")) {
    e.preventDefault();
    if (!$("dialog").open) save();
    return;
  }
  if ($("dialog").open) return;
  const editing = isEditable(e.target);

  if (mod && (e.key === "d" || e.key === "D")) {
    e.preventDefault();
    if (!editing && state.selection?.kind === "node") duplicateNode(state.selection.id);
    return;
  }
  if (e.key === "Escape") {
    if (state.drag?.mode === "connect") {
      state.drag = null;
      endConnect();
    } else if ($("palette").classList.contains("open")) {
      closePaletteFlyout();
    } else if (editing) {
      e.target.blur();
    } else if (state.selection) {
      select(null);
      $("canvas").focus({ preventScroll: true });
    } else if (state.eventsOpen) {
      toggleEvents(false);
    }
    return;
  }
  if (editing) return;
  const onStage = e.target === document.body || !!e.target.closest?.(".stage, .inspector");
  if ((e.key === "Delete" || e.key === "Backspace") && state.selection && onStage) {
    e.preventDefault();
    deleteSelection();
    return;
  }
  if (e.key === " " && !e.target.closest?.("button")) {
    e.preventDefault();
    if (!state.space) {
      state.space = true;
      $("canvas").classList.add("space");
    }
    return;
  }
  const nodeEl = e.target.closest?.(".node");
  const arrows = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
  if (nodeEl && arrows[e.key]) {
    e.preventDefault();
    const node = nodeById(nodeEl.dataset.id);
    const step = e.shiftKey ? 50 : SNAP;
    node.x += arrows[e.key][0] * step;
    node.y += arrows[e.key][1] * step;
    nodeEl.style.left = `${node.x}px`;
    nodeEl.style.top = `${node.y}px`;
    renderEdges();
    changed();
  }
}

function onKeyUp(e) {
  if (e.key === " ") {
    state.space = false;
    $("canvas").classList.remove("space");
  }
}

// ---------------------------------------------------------------- inspector
function renderInspector({ keepFocus = false } = {}) {
  const el = $("inspector");
  const focusId = keepFocus && el.contains(document.activeElement) ? document.activeElement.id : null;
  el.replaceChildren();
  if (state.problems.length) el.append(problemsBox());
  if (!state.draft) {
    el.append(
      h("div", { class: "insp-head" }, h("p", { class: "label", text: "Inspector" }), h("p", { class: "insp-desc", text: "Choose an automation on the left or make a new one." })),
    );
  } else if (state.selection?.kind === "node" && nodeById(state.selection.id)) {
    el.append(nodeInspector(nodeById(state.selection.id)));
  } else if (state.selection?.kind === "edge" && state.draft.edges.some((e) => edgeKey(e) === state.selection.key)) {
    el.append(edgeInspector(state.draft.edges.find((e) => edgeKey(e) === state.selection.key)));
  } else {
    el.append(graphInspector());
  }
  if (focusId) $(focusId)?.focus();
}

function problemsBox() {
  return h(
    "div",
    { class: "problems", role: "alert" },
    h("p", { class: "label", text: "Not saved" }),
    h(
      "ul",
      null,
      state.problems.map((p) => {
        const node = p.nodeId ? nodeById(p.nodeId) : null;
        return h(
          "li",
          null,
          node
            ? [h("button", { type: "button", class: "link", text: typeInfo(node.type).title, onclick: () => selectNode(node.id, { show: true }) }), ": "]
            : null,
          p.message,
        );
      }),
    ),
  );
}

function head(kicker, title, desc, { group, id } = {}) {
  return h(
    "div",
    { class: "insp-head", dataset: group ? { group } : null },
    h("div", { class: "insp-kicker" }, h("p", { class: "label", text: kicker }), id ? h("span", { class: "insp-id mono", text: id }) : null),
    h("h2", { class: "insp-title", text: title }),
    desc ? h("p", { class: "insp-desc", text: desc }) : null,
  );
}

function fieldWrap(id, label, control, { required = false, help = "", extra = null } = {}) {
  return h(
    "div",
    { class: "field" },
    h("label", { class: "field-label", for: id }, label, required ? h("span", { class: "req", "aria-hidden": "true", text: "*" }) : null),
    control,
    help ? h("span", { class: "field-help", id: `${id}-help`, text: help }) : null,
    extra,
  );
}

function graphInspector() {
  const g = state.draft;
  const name = h("input", { id: "g-name", class: "input", type: "text", value: g.name, maxlength: "80", autocomplete: "off" });
  name.addEventListener("input", () => {
    g.name = name.value;
    changed();
  });
  const enabled = h("input", { id: "g-enabled", type: "checkbox", class: "switch", role: "switch", disabled: state.locked });
  const saved = state.graphs.find((x) => x.id === g.id);
  enabled.checked = state.isNew ? g.enabled : !!saved?.enabled;
  enabled.addEventListener("change", () => toggleEnabled(g.id, enabled.checked, enabled));
  const cooldown = h("input", { id: "g-cooldown", class: "input mono", type: "number", min: "0", max: "86400", step: "1", value: String(g.cooldownSeconds) });
  cooldown.addEventListener("input", () => {
    if (cooldown.value === "") return;
    g.cooldownSeconds = clamp(Math.round(Number(cooldown.value) || 0), 0, 86400);
    changed();
  });

  return h(
    "div",
    null,
    head("Automation", g.name || "Untitled automation", null, { id: g.id }),
    fieldWrap("g-name", "Name", name, { required: true }),
    h(
      "div",
      { class: "row-switch" },
      h("label", { class: "field-label", for: "g-enabled", text: "Enabled" }),
      enabled,
    ),
    h(
      "p",
      { class: "field-help" },
      state.isNew ? "Saved with the automation." : "Switching it on or off saves right away. Triggers start it only while it is on.",
    ),
    fieldWrap("g-cooldown", "Cooldown (seconds)", cooldown, { help: "After a run, the automation waits this long before it can run again." }),
    lastRunSection(),
    h(
      "section",
      { class: "section" },
      h("h3", { class: "label", text: "How to" }),
      h(
        "p",
        { class: "hint" },
        "Drag from a node's right handle to another node to connect them. ",
        h("kbd", { text: "Delete" }),
        " removes the selection, ",
        h("kbd", { text: "Ctrl+D" }),
        " duplicates a node, ",
        h("kbd", { text: "Ctrl+S" }),
        " saves. Drag the background or hold ",
        h("kbd", { text: "Space" }),
        " to pan; the wheel zooms.",
      ),
    ),
  );
}

function lastRunSection() {
  const run = state.runs[state.draft.id];
  const section = h("section", { class: "section" }, h("h3", { class: "label", text: "Last run" }));
  if (!run) {
    section.append(h("p", { class: "hint", text: state.isNew ? "Save it, then try a dry run." : "Not run yet. A dry run shows what would happen." }));
    return section;
  }
  const d = toDate(run.at);
  section.append(
    h(
      "dl",
      { class: "kv" },
      h("dt", { text: "When" }),
      h("dd", { text: d ? `${d.toLocaleDateString()} ${fmtSeconds(d)}` : String(run.at ?? "") }),
      h("dt", { text: "Started by" }),
      h("dd", { text: typeof run.trigger === "object" ? fmtData(run.trigger) : String(run.trigger ?? "") }),
      h("dt", { text: "Result" }),
      h("dd", { text: `${run.dryRun ? "Dry run" : "Real run"} · ${run.success ? "ok" : "failed"}` }),
      run.summary ? [h("dt", { text: "Summary" }), h("dd", { text: run.summary })] : null,
    ),
    stepsList(run),
  );
  return section;
}

function stepsList(run) {
  return h(
    "ol",
    { class: "steps" },
    (run.steps ?? []).map((st) => {
      const node = nodeById(st.nodeId);
      const o = OUTCOME[st.outcome] ?? { tone: "muted", label: st.outcome };
      const li = h(
        "li",
        { class: `step${node ? " clickable" : ""}` },
        h("span", { class: "step-outcome", dataset: { tone: o.tone }, text: o.label }),
        h("span", { class: "step-node", text: node ? typeInfo(node.type).title : st.nodeId || "Run" }),
        h("span", { class: "step-msg", text: st.message ?? "" }),
      );
      if (node) li.addEventListener("click", () => selectNode(node.id, { show: true }));
      return li;
    }),
  );
}

function edgeInspector(edge) {
  const from = nodeById(edge.from);
  const to = nodeById(edge.to);
  return h(
    "div",
    null,
    head("Connection", `${typeInfo(from.type).title} → ${typeInfo(to.type).title}`, portsOf(from.type).length > 1 ? `Follows the “${edge.port}” path.` : null),
    h(
      "div",
      { class: "insp-actions" },
      h("button", { type: "button", class: "btn", text: `Select ${typeInfo(from.type).title}`, onclick: () => selectNode(from.id, { show: true }) }),
      h("button", { type: "button", class: "btn danger", text: "Remove connection", onclick: () => removeEdge(edgeKey(edge)) }),
    ),
    h("p", { class: "hint section" }, h("kbd", { text: "Delete" }), " removes the selected connection."),
  );
}

function nodeInspector(node) {
  const info = typeInfo(node.type);
  const kicker = { trigger: "Trigger", condition: "Condition", action: "Action" }[info.group] ?? info.group;
  const wrap = h("div", null, head(kicker, info.title, info.description, { group: info.group, id: node.id }));
  const fields = h("div", { class: "fields" });
  for (const f of info.fields ?? []) fields.append(renderField(node, f));
  if (!(info.fields ?? []).length) fields.append(h("p", { class: "hint", text: "Nothing to set up." }));
  wrap.append(fields, connectionsSection(node));
  wrap.append(
    h(
      "section",
      { class: "section insp-actions" },
      h("button", { type: "button", class: "btn", text: "Duplicate", title: "Ctrl+D", onclick: () => duplicateNode(node.id) }),
      h("button", { type: "button", class: "btn danger", text: "Delete node", title: "Delete", onclick: () => removeNode(node.id) }),
    ),
  );
  return wrap;
}

function connectionsSection(node) {
  const section = h("section", { class: "section" }, h("h3", { class: "label", text: "Connections" }));
  const ports = portsOf(node.type);
  if (hasInput(node.type)) {
    const incoming = state.draft.edges.filter((e) => e.to === node.id);
    section.append(
      h(
        "div",
        { class: "conn" },
        h("span", { class: "conn-port", text: "Comes from" }),
        incoming.length
          ? h("ul", { class: "conn-list" }, incoming.map((e) => connItem(e, e.from, portsOf(nodeById(e.from)?.type ?? "").length > 1 ? `${e.port}` : "")))
          : h("span", { class: "hint", text: "Nothing yet: connect a trigger or an earlier step into it." }),
      ),
    );
  }
  for (const port of ports) {
    const out = state.draft.edges.filter((e) => e.from === node.id && e.port === port);
    const id = `conn-${node.id}-${port}`;
    const choices = state.draft.nodes.filter((n) => n.id !== node.id && !connectProblem(node.id, port, n.id));
    const pick = h(
      "select",
      { id, class: "input", "aria-label": ports.length > 1 ? `Connect “${port}” to` : "Connect to", disabled: !choices.length },
      h("option", { value: "", text: choices.length ? "Connect to…" : "No node can follow here" }),
      choices.map((n) => h("option", { value: n.id, text: `${typeInfo(n.type).title}${nodeSummary(n) ? ` · ${nodeSummary(n)}` : ""}` })),
    );
    pick.addEventListener("change", () => {
      if (pick.value && connect(node.id, port, pick.value)) renderInspector();
    });
    section.append(
      h(
        "div",
        { class: "conn" },
        h("span", { class: "conn-port", text: ports.length > 1 ? `If ${port}` : "Then" }),
        out.length ? h("ul", { class: "conn-list" }, out.map((e) => connItem(e, e.to))) : null,
        pick,
      ),
    );
  }
  return section;
}

function connItem(edge, otherId, tag = "") {
  const other = nodeById(otherId);
  const text = other ? `${typeInfo(other.type).title}${tag ? ` (${tag})` : ""}${nodeSummary(other) ? ` · ${nodeSummary(other)}` : ""}` : otherId;
  return h(
    "li",
    { class: "conn-item" },
    h("span", { title: text }, h("button", { type: "button", class: "link", text, onclick: () => selectNode(otherId, { show: true }) })),
    h("button", { type: "button", class: "btn icon ghost", "aria-label": `Remove connection to ${text}`, title: "Remove connection", text: "×", onclick: () => removeEdge(edgeKey(edge)) }),
  );
}

// One form field from the catalog. Values are strings in node.config; an empty value removes the key.
function renderField(node, f) {
  const c = node.config;
  const id = `f-${node.id}-${f.key.replace(/[^A-Za-z0-9_-]/g, "_")}`;
  const value = c[f.key] ?? "";
  const set = (v, opts) => {
    if (v === "" || v === null || v === undefined) delete c[f.key];
    else c[f.key] = String(v);
    configChanged(node, opts);
  };
  const opts = { required: !!f.required, help: f.help ?? "" };

  // The receiver action's value depends on its command.
  if (node.type === "action.receiver" && f.key === "value") return receiverValueField(node, f, id);

  switch (f.kind) {
    case "number": {
      const input = h("input", { id, class: "input mono", type: "number", value, min: f.min, max: f.max, step: f.step ?? "any", placeholder: f.placeholder });
      input.addEventListener("input", () => set(input.value));
      return fieldWrap(id, f.label, input, opts);
    }
    case "time": {
      const input = h("input", { id, class: "input mono", type: "time", value, step: "60" });
      input.addEventListener("input", () => set(input.value));
      return fieldWrap(id, f.label, input, opts);
    }
    case "days": {
      const options = f.options?.length ? f.options : ["Daily", "Weekdays", "Weekends"].map((d) => ({ value: d, label: d }));
      return fieldWrap(id, f.label, selectFor(id, options, value || options[0].value, set), opts);
    }
    case "select": {
      const isCommand = node.type === "action.receiver" && f.key === "command";
      const onChange = (v) => {
        if (isCommand) delete c.value; // the value means something else for another command
        set(v, { inspector: isCommand });
      };
      return fieldWrap(id, f.label, selectFor(id, f.options ?? [], value, onChange), opts);
    }
    case "event": {
      const input = h("input", { id, class: "input mono", type: "text", value, list: "event-names", placeholder: f.placeholder ?? "power.changed", autocomplete: "off", spellcheck: "false" });
      input.addEventListener("input", () => set(input.value.trim()));
      return fieldWrap(id, f.label, input, opts);
    }
    case "filter":
      return filterField(id, f, value, set, opts);
    case "hook":
      return hookField(node, id, f, value, set, opts);
    case "scene": {
      const options = (state.catalog.scenes ?? []).map((sc) => ({ value: sc.id, label: sc.name }));
      return fieldWrap(id, f.label, selectFor(id, options, value, set, "Choose a scene"), opts);
    }
    case "input": {
      const options = (state.catalog.inputs ?? []).map((i) => ({ value: i.code, label: i.name && i.name !== i.code ? `${i.name} (${i.code})` : i.code }));
      return fieldWrap(id, f.label, selectFor(id, options, value, set, f.required ? "Choose an input" : "Any input"), opts);
    }
    case "soundMode": {
      const options = (state.catalog.soundModes ?? []).map((m) => ({ value: m, label: m }));
      return fieldWrap(id, f.label, selectFor(id, options, value, set, f.required ? "Choose a sound mode" : "Any"), opts);
    }
    case "pluginAction":
      return pluginActionField(node, id, f, opts);
    default: {
      const input = h("input", { id, class: "input", type: "text", value, placeholder: f.placeholder, autocomplete: "off" });
      input.addEventListener("input", () => set(input.value));
      return fieldWrap(id, f.label, input, opts);
    }
  }
}

// A <select>; `empty` adds a first option with value "" (none). A value not in the list stays as "(not found)".
function selectFor(id, options, value, onChange, empty = null) {
  const sel = h("select", { id, class: "input" });
  if (empty !== null) sel.append(h("option", { value: "", text: empty }));
  for (const o of options) sel.append(h("option", { value: o.value, text: o.label ?? o.value }));
  if (value && !options.some((o) => o.value === value)) sel.append(h("option", { value, text: `${value} (not found)` }));
  sel.value = value;
  if (empty === null && !value && options.length) sel.value = options[0].value;
  sel.addEventListener("change", () => onChange(sel.value));
  return sel;
}

function filterField(id, f, value, set, opts) {
  const input = h("input", { id, class: "input mono", type: "text", value, placeholder: f.placeholder ?? "key=value, key!=value", autocomplete: "off", spellcheck: "false" });
  const note = h("span", { class: "field-help bad", hidden: true, text: "Write it as key=value or key!=value, separated by commas." });
  const check = () => {
    const bad = input.value
      .split(",")
      .map((p) => p.trim())
      .filter(Boolean)
      .some((p) => !/^[A-Za-z0-9_.:-]+\s*!?=/.test(p));
    note.hidden = !bad;
    input.setAttribute("aria-invalid", String(bad));
  };
  input.addEventListener("input", () => {
    check();
    set(input.value.trim());
  });
  check();
  return fieldWrap(id, f.label, input, { ...opts, help: opts.help || "Leave empty to match every time.", extra: note });
}

function hookField(node, id, f, value, set, opts) {
  const base = state.catalog.webhookBaseUrl ?? "";
  const input = h("input", { id, class: "input mono", type: "text", value, placeholder: f.placeholder ?? "movie-time", autocomplete: "off", spellcheck: "false" });
  const note = h("span", { class: "field-help bad", hidden: true, text: "Use letters, digits, - and _ only." });
  const url = h("input", { id: `${id}-url`, class: "input mono", type: "text", readonly: true, "aria-label": "Webhook URL" });
  const copy = h("button", { type: "button", class: "btn small", text: "Copy" });
  const update = () => {
    const ok = /^[A-Za-z0-9_-]*$/.test(input.value);
    note.hidden = ok;
    input.setAttribute("aria-invalid", String(!ok));
    url.value = input.value ? `${base}${input.value}` : "";
    url.placeholder = "Name the hook first";
    copy.disabled = !input.value || !ok;
  };
  input.addEventListener("input", () => {
    update();
    set(input.value.trim());
  });
  copy.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(url.value);
    } catch {
      url.select();
      document.execCommand("copy");
    }
    toast("Webhook URL copied.", "ok");
  });
  update();
  return h(
    "div",
    null,
    fieldWrap(id, f.label, input, { ...opts, extra: note }),
    h(
      "div",
      { class: "field" },
      h("span", { class: "field-label", text: "URL" }),
      h("div", { class: "hook-url" }, url, copy),
      h(
        "span",
        { class: "field-help" },
        "iPhone Shortcuts: add Get Contents of URL with this URL, method POST and the header Authorization: Bearer <owner token>. JSON fields in the body become the event data.",
      ),
    ),
  );
}

function pluginActionField(node, id, f, opts) {
  const c = node.config;
  const actions = state.catalog.pluginActions ?? [];
  const sel = h("select", { id, class: "input" }, h("option", { value: "", text: actions.length ? "Choose an action" : "No plugin actions available" }));
  const byPlugin = new Map();
  actions.forEach((a, i) => {
    const name = a.pluginName || a.pluginId;
    if (!byPlugin.has(name)) byPlugin.set(name, []);
    byPlugin.get(name).push([a, i]);
  });
  for (const [name, list] of byPlugin) {
    sel.append(h("optgroup", { label: name }, list.map(([a, i]) => h("option", { value: String(i), text: a.name || a.actionId }))));
  }
  const index = actions.findIndex((a) => a.pluginId === c.pluginId && a.actionId === c.actionId);
  if (index < 0 && c.actionId) sel.append(h("option", { value: "missing", text: `${c.actionId} (not installed)` }));
  sel.value = index >= 0 ? String(index) : c.actionId ? "missing" : "";
  sel.addEventListener("change", () => {
    if (sel.value === "missing") return;
    // "" is "Choose an action": clear the action (Number("") would be 0, the first action in the list).
    const a = sel.value === "" ? null : actions[Number(sel.value)];
    const keep = new Set((a?.parameters ?? []).map((p) => `p:${p.name}`));
    for (const k of Object.keys(c)) if (k.startsWith("p:") && !keep.has(k)) delete c[k];
    if (a) {
      c.pluginId = a.pluginId;
      c.actionId = a.actionId;
    } else {
      delete c.pluginId;
      delete c.actionId;
    }
    configChanged(node, { inspector: true });
  });

  const action = index >= 0 ? actions[index] : null;
  const wrap = h("div", null, fieldWrap(id, f.label, sel, { ...opts, help: action ? `${action.pluginName || action.pluginId}` : opts.help }));
  for (const p of action?.parameters ?? []) {
    const key = `p:${p.name}`;
    const pid = `${id}-p-${p.name.replace(/[^A-Za-z0-9_-]/g, "_")}`;
    const numeric = /^(number|integer|int|double|float)$/i.test(p.type ?? "");
    const input = h("input", { id: pid, class: "input mono", type: numeric ? "number" : "text", value: c[key] ?? "", step: numeric ? "any" : null, autocomplete: "off", spellcheck: "false" });
    input.addEventListener("input", () => {
      if (input.value === "") delete c[key];
      else c[key] = input.value;
      configChanged(node);
    });
    wrap.append(fieldWrap(pid, p.name, input, { required: !!p.required, help: p.description ?? "" }));
  }
  return wrap;
}

function receiverValueField(node, f, id) {
  const c = node.config;
  const cmd = c.command;
  const set = (v) => {
    if (v === "" || v === null || v === undefined) delete c.value;
    else c.value = String(v);
    configChanged(node);
  };
  if (cmd === "volume") {
    const cap = state.catalog.maxSafeVolume;
    const input = h("input", { id, class: "input mono", type: "number", min: "0", max: "98", step: "0.5", value: c.value ?? "", placeholder: "40" });
    const capNote = cap !== undefined ? h("span", { class: "cap", text: `Capped at ${cap}` }) : null;
    const over = h("span", { class: "field-help warn", hidden: true, text: "" });
    const check = () => {
      const isOver = cap !== undefined && Number(input.value) > cap;
      over.hidden = !isOver;
      over.textContent = isOver ? `The receiver goes to ${cap}, the safe-volume ceiling (Settings → MCP).` : "";
    };
    input.addEventListener("input", () => {
      check();
      set(input.value);
    });
    check();
    return fieldWrap(id, "Volume", input, { required: true, help: "0 to 98, as on the receiver's display.", extra: [capNote, over] });
  }
  if (cmd === "input") {
    const options = (state.catalog.inputs ?? []).map((i) => ({ value: i.code, label: i.name && i.name !== i.code ? `${i.name} (${i.code})` : i.code }));
    return fieldWrap(id, "Input", selectFor(id, options, c.value ?? "", set, "Choose an input"), { required: true });
  }
  if (cmd === "sound_mode") {
    const options = (state.catalog.soundModes ?? []).map((m) => ({ value: m, label: m }));
    return fieldWrap(id, "Sound mode", selectFor(id, options, c.value ?? "", set, "Choose a sound mode"), { required: true });
  }
  return h("span", { hidden: true });
}

// ---------------------------------------------------------------- open, new, duplicate, delete
function loadGraph(graph, { isNew = false } = {}) {
  if (state.openId) state.views.set(state.openId, { ...state.view });
  const g = normalize(graph);
  state.draft = clone(g);
  state.baseline = isNew ? "" : stable(g);
  state.isNew = isNew;
  state.openId = g.id;
  state.selection = null;
  state.problems = [];
  clearOverlay(false);
  const view = state.views.get(g.id);
  if (view) state.view = { ...view };
  renderCanvas();
  renderInspector();
  changed();
  renderList();
  if (!view) requestAnimationFrame(fit);
  try {
    localStorage.setItem("automations.open", g.id);
  } catch {
    // ignore
  }
}

function closeGraph() {
  if (state.openId) state.views.set(state.openId, { ...state.view });
  state.draft = null;
  state.openId = null;
  state.isNew = false;
  state.baseline = "";
  state.selection = null;
  state.problems = [];
  clearOverlay(false);
  renderCanvas();
  renderInspector();
  changed();
  renderList();
}

// Asks what to do with unsaved changes. True when it is fine to leave the open graph.
async function guardLeave() {
  if (!isDirty()) return true;
  const choice = await ask({
    title: `Save changes to “${state.draft.name || "Untitled automation"}”?`,
    text: state.isNew ? "This automation has not been saved yet." : "You have changes that are not saved.",
    buttons: [
      { label: "Discard", value: "discard", tone: "danger" },
      { label: "Cancel", value: "cancel", focus: true },
      { label: "Save", value: "save", tone: "primary" },
    ],
  });
  if (choice === "save") return await save();
  if (choice === "discard") {
    if (state.isNew) closeGraph();
    else {
      const saved = state.graphs.find((g) => g.id === state.openId);
      if (saved) loadGraph(saved);
    }
    return true;
  }
  return false;
}

async function openGraph(id) {
  if (id === state.openId) return;
  if (!(await guardLeave())) return;
  const g = state.graphs.find((x) => x.id === id);
  if (g) loadGraph(g);
}

function newGraphNow() {
  loadGraph({ id: newGraphId(), name: "New automation", enabled: false, cooldownSeconds: 120, nodes: [], edges: [] }, { isNew: true });
}

async function newGraph() {
  if (!(await guardLeave())) return;
  newGraphNow();
  const name = $("g-name");
  name?.focus();
  name?.select();
}

async function duplicateGraph() {
  if (!state.draft) return;
  if (!(await guardLeave())) return;
  if (!state.draft) return;
  const copy = clone(normalize(state.draft));
  copy.id = newGraphId();
  copy.name = `${state.draft.name || "Untitled automation"} copy`;
  copy.enabled = false;
  loadGraph(copy, { isNew: true });
  toast("Copy made. Save it to keep it.");
}

async function deleteGraph() {
  if (!state.draft) return;
  const name = state.draft.name || "Untitled automation";
  const choice = await ask({
    title: `Delete “${name}”?`,
    text: state.isNew ? "It has not been saved, so nothing else changes." : "The automation and its triggers are removed. This can't be undone.",
    buttons: [
      { label: "Cancel", value: "cancel", focus: true },
      { label: "Delete", value: "delete", tone: "danger-solid" },
    ],
  });
  if (choice !== "delete") return;
  const id = state.draft.id;
  if (!state.isNew) {
    try {
      await api("DELETE", `/api/automations/${encodeURIComponent(id)}`);
    } catch (error) {
      toast(`Not deleted: ${error.message}`, "error", 5000);
      return;
    }
    state.graphs = state.graphs.filter((g) => g.id !== id);
    delete state.runs[id];
  }
  state.views.delete(id);
  state.openId = null;
  const next = state.graphs[0];
  if (next) loadGraph(next);
  else closeGraph();
  toast(`Deleted “${name}”.`);
}

// The enabled switch saves at once with a targeted request that changes only `enabled`; other edits wait for Save.
// It waits for a Save in flight, and sets the draft's `enabled` first, so neither request undoes the other.
async function toggleEnabled(id, on, control) {
  const saved = state.graphs.find((g) => g.id === id);
  const before = state.draft?.id === id ? state.draft.enabled : null;
  if (state.draft?.id === id) state.draft.enabled = on;
  if (!saved) {
    if (state.draft?.id === id) changed();
    return;
  }
  try {
    if (state.saving) await state.saving;
    const res = await api("POST", `/api/automations/${encodeURIComponent(id)}/enabled`, { enabled: on });
    const g = normalize(res?.graph ?? { ...saved, enabled: on });
    state.graphs = state.graphs.map((x) => (x.id === id ? g : x));
    if (state.draft?.id === id) {
      state.draft.enabled = g.enabled;
      state.baseline = stable(g);
    }
    toast(g.enabled ? `“${g.name}” is on.` : `“${g.name}” is off.`, "ok");
  } catch (error) {
    if (control) control.checked = !on;
    if (before !== null && state.draft?.id === id) state.draft.enabled = before;
    if (error.status === 400 && state.draft?.id === id) {
      state.problems = problemsFrom(error.data);
      renderCanvas();
      renderInspector();
    }
    toast(error.status === 400 ? "Can't switch it on: fix the problems first." : `Not changed: ${error.message}`, "error", 5000);
  }
  changed();
  if (state.draft?.id === id && !state.selection) renderInspector();
}

// ---------------------------------------------------------------- save and run
function problemsFrom(data) {
  const list = Array.isArray(data?.problems) ? data.problems.filter((p) => p && p.message) : [];
  if (!list.length) list.push({ message: data?.error || "The app refused this automation." });
  return list.map((p) => ({ nodeId: p.nodeId ?? null, message: String(p.message) }));
}

async function save() {
  if (!state.draft || state.locked || state.busy) return false;
  if (!isDirty()) return true;
  const body = normalize(state.draft);
  const sent = stable(body);
  state.busy = true;
  // A switch flipped meanwhile waits for this save (toggleEnabled), so its change lands after it.
  let saveDone;
  state.saving = new Promise((resolve) => (saveDone = resolve));
  updateToolbar();
  $("btn-save").textContent = "Saving…";
  try {
    const res = await api("PUT", `/api/automations/${encodeURIComponent(body.id)}`, body);
    const saved = normalize(res?.graph ?? body);
    const i = state.graphs.findIndex((g) => g.id === saved.id);
    if (i >= 0) state.graphs[i] = saved;
    else state.graphs.push(saved);
    state.isNew = false;
    state.baseline = stable(saved);
    // Keep edits made while the request was on its way; otherwise take the server's version.
    if (stable(normalize(state.draft)) === sent) state.draft = clone(saved);
    state.problems = [];
    renderCanvas();
    renderInspector({ keepFocus: true });
    toast("Saved.", "ok");
    return true;
  } catch (error) {
    if (error.status === 400) {
      state.problems = problemsFrom(error.data);
      renderCanvas();
      renderInspector({ keepFocus: true });
      toast(`Not saved: ${plural(state.problems.length, "problem")} to fix.`, "error", 5000);
    } else {
      toast(`Not saved: ${error.message}`, "error", 6000);
    }
    return false;
  } finally {
    state.busy = false;
    state.saving = null;
    saveDone();
    $("btn-save").textContent = "Save";
    changed();
  }
}

async function runTest(dryRun) {
  if (!state.draft || state.locked || state.busy) return;
  if (!state.draft.nodes.some(isTrigger)) {
    toast("Add a trigger first.", "error");
    return;
  }
  const dirty = isDirty();
  if (!dryRun) {
    const choice = await ask({
      title: `Run “${state.draft.name || "Untitled automation"}” now?`,
      text: `The actions run for real and change the room.${dirty ? " Your changes are saved first." : ""}`,
      buttons: [
        { label: "Cancel", value: "cancel", focus: true },
        { label: "Run now", value: "run", tone: "warn" },
      ],
    });
    if (choice !== "run") return;
  }
  // Run now saves first (its dialog says so). A dry run tests the draft as it is and publishes nothing: saving an
  // enabled automation would let a real event run the unsaved actions while you think you are only testing.
  if (!dryRun && dirty && !(await save())) return;
  const sel = state.selection?.kind === "node" ? nodeById(state.selection.id) : null;
  const body = { dryRun };
  if (dryRun && (dirty || state.isNew)) body.graph = normalize(state.draft);
  if (sel && isTrigger(sel)) body.triggerNodeId = sel.id;
  const btn = dryRun ? $("btn-dry") : $("btn-run");
  const label = btn.textContent;
  state.busy = true;
  updateToolbar();
  btn.textContent = "Running…";
  try {
    const run = await api("POST", `/api/automations/${encodeURIComponent(state.draft.id)}/test`, body);
    state.runs[state.draft.id] = run;
    if (run?.at) state.seenRunAt[state.draft.id] = run.at;
    showRun(run);
    toast(run?.summary || (run?.success ? "Done." : "The run failed."), run?.success ? "ok" : "error", 4000);
    scheduleList();
  } catch (error) {
    if (error.status === 400 && error.data?.problems) {
      state.problems = problemsFrom(error.data);
      renderCanvas();
      renderInspector({ keepFocus: true });
      toast(`${dryRun ? "Dry run" : "Run"} not started: ${plural(state.problems.length, "problem")} to fix.`, "error", 5000);
    } else {
      toast(`${dryRun ? "Dry run" : "Run"} failed: ${error.message}`, "error", 6000);
    }
  } finally {
    state.busy = false;
    btn.textContent = label;
    updateToolbar();
  }
}

function clearOverlay(render = true) {
  for (const t of state.overlayTimers) clearTimeout(t);
  state.overlayTimers = [];
  const hadOverlay = !!state.overlay;
  state.overlay = null;
  const card = $("run-card");
  card.hidden = true;
  card.classList.remove("fading");
  if (render && hadOverlay) {
    renderCanvas();
  }
}

function showRun(run) {
  if (!run || run.graphId && run.graphId !== state.openId) return;
  clearOverlay(false);
  state.overlay = { run, steps: new Map((run.steps ?? []).map((st) => [st.nodeId, st])) };
  renderCanvas();
  renderRunCard(run);
  if (!state.selection) renderInspector();
  state.overlayTimers.push(
    setTimeout(() => {
      $("world").classList.add("fading");
      $("run-card").classList.add("fading");
      state.overlayTimers.push(setTimeout(() => clearOverlay(), 1300));
    }, RUN_FADE_MS),
  );
}

function renderRunCard(run) {
  const card = $("run-card");
  card.classList.remove("fading");
  card.replaceChildren(
    h(
      "div",
      { class: "run-head" },
      h("span", { class: "run-dot", dataset: { state: run.dryRun ? "dry" : run.success ? "ok" : "failed" } }),
      h("span", { class: "run-title", text: `${run.dryRun ? "Dry run" : "Run"} · ${run.success ? "ok" : "failed"}` }),
      h("span", { class: "muted mono", text: toDate(run.at) ? fmtSeconds(toDate(run.at)) : "" }),
      h("button", { type: "button", class: "btn icon ghost", "aria-label": "Close", title: "Close", text: "×", onclick: () => clearOverlay() }),
    ),
    run.summary ? h("p", { class: "run-summary", text: run.summary }) : null,
    stepsList(run),
  );
  card.hidden = false;
}

// ---------------------------------------------------------------- recent events
async function toggleEvents(open = !state.eventsOpen) {
  state.eventsOpen = open;
  $("events").hidden = !open;
  $("events-toggle").hidden = open;
  $("events-toggle").setAttribute("aria-expanded", String(open));
  if (open) {
    await loadEvents();
    $("events-refresh").focus();
  } else {
    $("events-toggle").focus();
  }
}

async function loadEvents() {
  const list = $("events-list");
  try {
    const data = await api("GET", "/api/automations/events?limit=30");
    state.events = data?.events ?? [];
    renderEvents();
  } catch (error) {
    list.replaceChildren(h("p", { class: "hint", text: `Can't load events: ${error.message}` }));
  }
}

function renderEvents() {
  const list = $("events-list");
  list.replaceChildren();
  if (!state.events.length) {
    list.append(h("p", { class: "hint", text: "No room events yet. Turn the receiver on or cast something, then refresh." }));
    return;
  }
  for (const ev of state.events) {
    const d = toDate(ev.ts);
    const data = fmtData(ev.data);
    const where = [ev.device, ev.stream].filter(Boolean).join(" · ");
    list.append(
      h(
        "div",
        { class: "ev" },
        h("span", { class: "ev-time mono", text: d ? fmtSeconds(d) : "", title: d ? d.toLocaleString() : null }),
        h("span", { class: "ev-event mono", text: ev.event ?? "", title: ev.event ?? "" }),
        h("span", { class: "ev-where", text: where, title: where }),
        h("span", { class: "ev-origin", text: ev.origin ?? "", title: ev.origin ? `Origin: ${ev.origin}` : null }),
        h("span", { class: "ev-data mono", text: data, title: data || null }),
        h("button", { type: "button", class: "btn small", text: "Use as trigger", disabled: !ev.event, onclick: () => useEventAsTrigger(ev) }),
      ),
    );
  }
}

function useEventAsTrigger(ev) {
  const config = { event: ev.event };
  if (ev.stream) config.filter = `stream=${ev.stream}`;
  addNode("trigger.event", null, config);
  toast(`Added a room event trigger for ${ev.event}.`, "ok");
}

// ---------------------------------------------------------------- live status
async function poll() {
  if (document.hidden) return;
  let data;
  try {
    data = await api("GET", "/api/automations");
  } catch {
    renderStatus();
    return;
  }
  applyList(data);
}

function applyList(data) {
  const incoming = (data?.graphs ?? []).map(normalize);
  const runs = data?.runs ?? {};
  state.paused = !!data?.paused;
  renderBanner();

  const open = state.openId && !state.isNew ? incoming.find((g) => g.id === state.openId) : null;
  const dirty = isDirty();
  state.graphs = incoming;
  if (state.openId && !state.isNew) {
    if (!open) {
      // Deleted elsewhere: keep unsaved work as a new graph, otherwise close it.
      if (dirty) state.isNew = true;
      else if (state.loaded) {
        const next = incoming[0];
        if (next) loadGraph(next);
        else closeGraph();
      }
    } else if (!dirty && stable(open) !== state.baseline) {
      const sel = state.selection;
      state.baseline = stable(open);
      state.draft = clone(open);
      state.selection = sel?.kind === "node" && !nodeById(sel.id) ? null : sel;
      renderCanvas();
      renderInspector();
    }
  }

  for (const [id, run] of Object.entries(runs)) {
    if (!run?.at || state.seenRunAt[id] === run.at) continue;
    state.seenRunAt[id] = run.at;
    if (state.loaded && id === state.openId) {
      state.runs[id] = run;
      showRun(run);
    }
  }
  state.runs = runs;
  if (!state.loaded) return;
  scheduleList();
  updateToolbar();
  renderStatus();
}

// ---------------------------------------------------------------- start
// For the app's editor window (AutomationEditorWindow): closing it asks first when there are unsaved changes.
window.denonEditor = {
  isDirty: () => isDirty(),
  async saveForClose() {
    let ok = false;
    try {
      ok = (await save()) === true && !isDirty();
    } finally {
      window.chrome?.webview?.postMessage({ type: "saved", ok });
    }
  },
};

async function start() {
  try {
    state.paletteCollapsed = localStorage.getItem("automations.palette") === "collapsed";
  } catch {
    // ignore
  }
  applyPaletteLayout();
  renderStatus();
  renderList();
  updateToolbar();
  changed();

  $("btn-save").addEventListener("click", () => save());
  $("btn-dry").addEventListener("click", () => runTest(true));
  $("btn-run").addEventListener("click", () => runTest(false));
  $("btn-new").addEventListener("click", () => newGraph());
  $("btn-dup").addEventListener("click", () => duplicateGraph());
  $("btn-del").addEventListener("click", () => deleteGraph());
  $("palette-toggle").addEventListener("click", togglePalette);
  $("zoom-in").addEventListener("click", () => zoomAt(1.2));
  $("zoom-out").addEventListener("click", () => zoomAt(1 / 1.2));
  $("zoom-fit").addEventListener("click", fit);
  $("events-toggle").addEventListener("click", () => toggleEvents(true));
  $("events-close").addEventListener("click", () => toggleEvents(false));
  $("events-refresh").addEventListener("click", loadEvents);

  const canvas = $("canvas");
  canvas.addEventListener("pointerdown", onPointerDown);
  canvas.addEventListener("pointermove", onPointerMove);
  canvas.addEventListener("pointerup", onPointerUp);
  canvas.addEventListener("pointercancel", onPointerUp);
  canvas.addEventListener("wheel", onWheel, { passive: false });
  canvas.addEventListener("dragover", onDragOver);
  canvas.addEventListener("dragleave", () => canvas.classList.remove("dropping"));
  canvas.addEventListener("drop", onDrop);
  document.addEventListener("keydown", onKeyDown);
  document.addEventListener("keyup", onKeyUp);
  window.addEventListener("blur", () => {
    state.space = false;
    canvas.classList.remove("space");
  });
  document.addEventListener("pointerdown", (e) => {
    if ($("palette").classList.contains("open") && !e.target.closest("#palette")) closePaletteFlyout();
  });
  window.addEventListener("resize", applyPaletteLayout);
  window.addEventListener("beforeunload", (e) => {
    if (!isDirty()) return;
    e.preventDefault();
    e.returnValue = "";
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) poll();
  });

  let list;
  try {
    const [catalog, data] = await Promise.all([api("GET", "/api/automations/catalog"), api("GET", "/api/automations")]);
    state.catalog = catalog;
    list = data;
  } catch (error) {
    renderStatus();
    if (!error.locked && !error.offline) toast(`Can't load automations: ${error.message}`, "error", 8000);
    setTimeout(retryStart, POLL_MS);
    return;
  }
  boot(list);
}

// Retries the first load until the app answers (it may be starting, or the page lost its key).
async function retryStart() {
  try {
    const [catalog, data] = await Promise.all([api("GET", "/api/automations/catalog"), api("GET", "/api/automations")]);
    state.catalog = catalog;
    boot(data);
  } catch {
    setTimeout(retryStart, POLL_MS);
  }
}

function boot(list) {
  state.types = new Map((state.catalog.nodeTypes ?? []).map((t) => [t.type, t]));
  $("event-names").replaceChildren(...(state.catalog.eventNames ?? []).map((n) => h("option", { value: n })));
  renderPalette();
  applyList(list);
  for (const [id, run] of Object.entries(state.runs)) if (run?.at) state.seenRunAt[id] = run.at;
  state.loaded = true;
  renderStatus();

  let last = null;
  try {
    last = localStorage.getItem("automations.open");
  } catch {
    // ignore
  }
  // The app opens the editor on one automation with #<id> (Edit in the Automation Rules list).
  const wanted = hashId();
  const first = state.graphs.find((g) => g.id === wanted) ?? state.graphs.find((g) => g.id === last) ?? state.graphs[0];
  if (first) loadGraph(first);
  else closeGraph();

  window.addEventListener("hashchange", () => {
    const id = hashId();
    if (id && state.graphs.some((g) => g.id === id)) openGraph(id);
  });

  setInterval(poll, POLL_MS);
}

function hashId() {
  try {
    return decodeURIComponent(location.hash.slice(1));
  } catch {
    return "";
  }
}

start();

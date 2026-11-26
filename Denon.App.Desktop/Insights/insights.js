// Denon Insights: draws the report, the room right now and the activity feed from the app's local API.
// No libraries and no network beyond this PC: every chart is plain SVG or HTML, colored with the app's theme.
"use strict";

const NS = "http://www.w3.org/2000/svg";
const state = {
  days: 7,
  report: null,
  live: null,
  activity: null,
  stream: "all",
  showQuiet: false,
  theme: null,
  icons: {},
  ramp: null,
};

// ---------------------------------------------------------------- tiny DOM helpers (text only, never HTML)
function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") el.className = v;
    else if (k === "style") el.style.cssText = v;
    else if (k === "dataset") Object.assign(el.dataset, v);
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? "" : v);
  }
  append(el, kids);
  return el;
}

function s(tag, attrs, ...kids) {
  const el = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === null || v === undefined) continue;
    if (k === "dataset") Object.assign(el.dataset, v);
    else if (k === "style") el.style.cssText = v; // CSSOM: allowed by the page's Content-Security-Policy
    else el.setAttribute(k, v);
  }
  append(el, kids);
  return el;
}

function append(el, kids) {
  for (const kid of kids.flat(Infinity)) {
    if (kid === null || kid === undefined || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
}

function fill(id, ...kids) {
  const el = document.getElementById(id);
  el.replaceChildren();
  append(el, kids);
  return el;
}

const label = (text) => h("p", { class: "label" }, text.toUpperCase());
const headline = (text) => h("h2", { class: "headline" }, text);
const empty = (text) => h("p", { class: "empty" }, text);

// A channel logo from the Smart TV plugin's cache, on a light plate so dark logos read in both themes. Without a
// logo (or when it fails to load) the plate shows a short monogram of the channel name instead, if one is given.
function logo(file, size = 28, name = "") {
  const plate = h("span", { class: "logo", style: `--logo:${size}px`, "aria-hidden": name ? null : "true" });
  const monogram = () => {
    plate.classList.add("monogram");
    plate.replaceChildren(name.replace(/\s+/g, "").slice(0, 4));
  };
  if (file) {
    const img = h("img", { class: "logo-img", src: `/logo/${encodeURIComponent(file)}`, alt: name, decoding: "async" });
    img.addEventListener("error", () => (name ? monogram() : plate.remove()));
    plate.append(img);
  } else if (name) {
    monogram();
  } else {
    return null;
  }
  return plate;
}

function icon(name, size = 16, color = "currentColor") {
  const d = state.icons[name];
  if (!d) return null;
  return s("svg", { viewBox: "0 0 24 24", width: size, height: size, "aria-hidden": "true" }, s("path", { d, fill: color }));
}

// ---------------------------------------------------------------- formatting
function duration(sec) {
  sec = Math.max(0, Math.round(sec || 0));
  const hrs = Math.floor(sec / 3600);
  const min = Math.floor((sec % 3600) / 60);
  if (hrs >= 1) return `${hrs} h ${min} min`;
  if (min >= 1) return `${min} min`;
  return `${sec} s`;
}

function shortDuration(sec) {
  const hrs = sec / 3600;
  if (hrs >= 10) return `${Math.round(hrs)} h`;
  if (hrs >= 1) return `${hrs.toFixed(1).replace(/\.0$/, "")} h`;
  return `${Math.round(sec / 60)} min`;
}

const pct = (share) => `${Math.round((share || 0) * 100)} %`;
const db = (volume) => {
  const v = volume - 80;
  return `${v >= 0 ? "+" : "−"}${Math.abs(v).toFixed(1).replace(/\.0$/, "")} dB`;
};

const dateOf = (iso) => {
  const [y, m, d] = iso.split("-").map(Number);
  return new Date(y, m - 1, d);
};
const fmtDay = (date, opts) => date.toLocaleDateString(undefined, opts);
const fmtTime = (date) => date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

// ---------------------------------------------------------------- colors (theme variables)
const KIND_VAR = {
  cast: "--accent-cyan",
  appletv: "--accent-violet",
  homepod: "--accent-rose-text",
  tv_app: "--accent-amber",
  tv_channel: "--accent-amber-bright",
  youtube: "--accent-crimson",
  avr_input: "--accent-indigo",
  radio: "--accent-pink",
  tunein_radio: "--accent-pink",
  spotify: "--accent-emerald",
  tidal: "--accent-blue-text",
  amazon_music: "--accent-sky",
  local_dlna: "--accent-emerald-bright",
  airplay: "--accent-rose-text",
  heos_stream: "--accent-indigo-text",
};
const kindColor = (kind) => `var(${KIND_VAR[kind] ?? "--accent-indigo"})`;

const STREAM_VAR = { media: "--accent-cyan", avr: "--accent-emerald", tv: "--accent-amber", lighting: "--accent-violet", control: "--accent-pink" };
const streamColor = (stream) => `var(${STREAM_VAR[stream] ?? "--accent-indigo"})`;

const ORIGIN_VAR = {
  external: "--text-faint",
  desktop_ui: "--accent-cyan",
  app: "--accent-cyan",
  mcp_agent: "--accent-violet",
  phone_remote: "--accent-emerald",
  automation: "--accent-amber",
  smart_scene: "--accent-pink",
  hotkey: "--accent-sky",
  plugin: "--accent-indigo",
};
const originColor = (origin) => `var(${ORIGIN_VAR[origin] ?? "--accent-blue-text"})`;

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function parseColor(text) {
  const t = text.trim();
  let m = /^#([0-9a-f]{6})$/i.exec(t);
  if (m) return [0, 2, 4].map((i) => parseInt(m[1].slice(i, i + 2), 16));
  m = /^rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)/i.exec(t);
  if (m) return [Number(m[1]), Number(m[2]), Number(m[3])];
  return [56, 189, 248];
}

// The LED heat ramp of the app's meters (MeterPalette.LedHeat), read from the theme.
function readRamp() {
  const n = parseInt(cssVar("--meter-heat-count"), 10) || 0;
  const stops = [];
  for (let i = 0; i < n; i++) {
    const raw = cssVar(`--meter-heat-${i}`);
    const space = raw.indexOf(" ");
    if (space > 0) stops.push([Number(raw.slice(0, space)), parseColor(raw.slice(space + 1))]);
  }
  const ghost = Number(cssVar("--meter-led-ghost-opacity")) || 0.075;
  const bloom = Number(cssVar("--meter-led-bloom")) || 0.5;
  return { stops: stops.length ? stops : [[0, [3, 105, 161]], [1, [239, 68, 68]]], ghost, bloom, label: parseColor(cssVar("--meter-led-label") || "#5b6b82") };
}

function heat(t) {
  const { stops } = state.ramp;
  t = Math.min(1, Math.max(0, t));
  for (let i = 1; i < stops.length; i++) {
    const [o1, c1] = stops[i];
    const [o0, c0] = stops[i - 1];
    if (t <= o1) {
      const f = o1 === o0 ? 0 : (t - o0) / (o1 - o0);
      return c0.map((c, k) => Math.round(c + (c1[k] - c) * f));
    }
  }
  return stops[stops.length - 1][1];
}

const rgb = (c, a = 1) => `rgb(${c[0]} ${c[1]} ${c[2]} / ${a})`;

// ---------------------------------------------------------------- tooltip
const tip = () => document.getElementById("tip");

function setTip(el, title, lines = []) {
  el.dataset.tipTitle = title;
  el.dataset.tipLines = JSON.stringify(lines);
}

function showTip(target, x, y) {
  const box = tip();
  const lines = JSON.parse(target.dataset.tipLines || "[]");
  box.replaceChildren(
    h("strong", null, target.dataset.tipTitle),
    ...lines.map(([a, b]) => h("div", { class: "tip-line" }, h("span", null, a), b !== undefined ? h("span", null, b) : null)),
  );
  box.hidden = false;
  const r = box.getBoundingClientRect();
  const left = Math.min(window.innerWidth - r.width - 8, x + 14);
  const top = y + 16 + r.height > window.innerHeight ? y - r.height - 12 : y + 16;
  box.style.left = `${Math.max(8, left)}px`;
  box.style.top = `${Math.max(8, top)}px`;
}

document.addEventListener("pointermove", (e) => {
  const target = e.target instanceof Element ? e.target.closest("[data-tip-title]") : null;
  if (target) showTip(target, e.clientX, e.clientY);
  else tip().hidden = true;
});
document.addEventListener("pointerleave", () => (tip().hidden = true));

// ---------------------------------------------------------------- data
async function api(path) {
  const response = await fetch(path, { cache: "no-store", credentials: "same-origin" });
  if (response.status === 401) {
    const error = new Error("locked");
    error.locked = true;
    throw error;
  }
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

function setStatus(kind, text) {
  const el = document.getElementById("status");
  el.dataset.state = kind;
  el.querySelector(".status-text").textContent = text;
}

function setBanner(text, tone = "warn") {
  const el = document.getElementById("banner");
  el.hidden = !text;
  el.dataset.tone = tone;
  el.textContent = text || "";
}

function failed(error) {
  if (error.locked) {
    setStatus("locked", "Locked");
    setBanner("Denon Desktop restarted, so this page's key is no longer valid. In the app, go to Settings and click Open Insights again.", "error");
  } else {
    setStatus("offline", "App not reachable");
    setBanner("Denon Desktop is not running or not reachable. Start it, then open Insights again from Settings.", "error");
  }
}

async function loadReport() {
  try {
    state.report = await api(`/api/report?days=${state.days}`);
    renderReport();
  } catch (error) {
    failed(error);
  }
}

async function loadActivity() {
  try {
    const hours = Math.max(24, state.days * 24);
    state.activity = await api(`/api/activity?hours=${hours}&limit=400&stream=${encodeURIComponent(state.stream)}`);
    renderActivity();
  } catch (error) {
    failed(error);
  }
}

async function loadLive() {
  try {
    const live = await api("/api/live");
    const themeChanged = state.theme !== null && state.theme !== live.theme;
    state.theme = live.theme;
    state.live = live;
    if (themeChanged) await reloadTheme(live.theme);
    const r = live.room?.receiver;
    setStatus("live", r?.connected ? `Live · ${r.model || r.name || "Receiver"}` : "Live · receiver offline");
    setBanner(live.logging.enabled ? "" : "Event logging is off (Settings → Smart event & habit logging). Nothing new is recorded, so these numbers do not grow.");
    renderNow();
    renderFooter();
    if (themeChanged) renderReport();
  } catch (error) {
    failed(error);
  }
}

function reloadTheme(stamp) {
  return new Promise((resolve) => {
    const old = document.getElementById("theme-css");
    const link = h("link", { rel: "stylesheet", href: `/theme.css?v=${encodeURIComponent(stamp)}` });
    link.addEventListener("load", () => {
      old.remove();
      link.id = "theme-css";
      state.ramp = readRamp();
      resolve();
    });
    link.addEventListener("error", () => resolve());
    old.after(link);
  });
}

// ---------------------------------------------------------------- hero: the display
function renderHero() {
  const { watch, period } = state.report;
  const total = watch.totalSeconds;
  const hrs = Math.floor(total / 3600);
  const min = Math.floor((total % 3600) / 60);

  const parts = hrs > 0 ? [[String(hrs), "h"], [String(min).padStart(2, "0"), "min"]] : [[String(min), "min"]];
  const lit = h("span", { class: "lit" }, parts.map(([n, u]) => [n, h("span", { class: "unit" }, u)]));
  const ghost = h("span", { class: "ghost", "aria-hidden": "true" }, parts.map(([n, u]) => ["8".repeat(n.length), h("span", { class: "unit" }, u)]));

  let delta = null;
  if (watch.changePercent !== null && watch.changePercent !== undefined && period.days > 1) {
    const up = watch.changePercent >= 0;
    delta = h("span", { class: "delta", dataset: { dir: up ? "up" : "down" } },
      `${up ? "▲" : "▼"} ${Math.abs(watch.changePercent)} % vs the ${period.days} days before`);
  }

  const receiverOnly = Math.max(0, total - watch.videoSeconds - watch.audioSeconds);
  const split = [
    ["Watching", watch.videoSeconds, "var(--accent-cyan)"],
    ["Listening", watch.audioSeconds, "var(--accent-emerald)"],
    ["Receiver inputs", receiverOnly, "var(--accent-amber)"],
  ].filter(([, v]) => v > 0);

  fill("vfd",
    h("div", { class: "vfd-top" },
      label(`Watch & listen time · ${period.label}`),
      delta),
    h("div", { class: "readout", "aria-label": `${watch.totalText} ${period.label.toLowerCase()}` }, ghost, lit),
    h("div", { class: "vfd-facts" },
      h("span", null, h("b", null, watch.sessions), watch.sessions === 1 ? " session" : " sessions"),
      period.days > 1 ? h("span", null, h("b", null, `${watch.activeDays} of ${period.days}`), " days") : null,
      watch.activeDays > 0 && period.days > 1 ? h("span", null, h("b", null, watch.averagePerActiveDayText), " on an active day") : null,
      watch.streakDays >= 2 ? h("span", null, h("b", null, `${watch.streakDays} days`), " in a row") : null),
    total > 0
      ? h("div", { class: "split" },
        h("div", { class: "split-bar", role: "img", "aria-label": split.map(([n, v]) => `${n} ${duration(v)}`).join(", ") },
          split.map(([n, v, c]) => {
            const seg = h("span", { style: `width:${(v / total) * 100}%;background:${c}` });
            setTip(seg, n, [[duration(v), pct(v / total)]]);
            return seg;
          })),
        h("ul", { class: "legend" }, split.map(([n, v, c]) => h("li", null, h("i", { class: "swatch", style: `background:${c}` }), `${n} ${duration(v)}`))))
      : h("p", { class: "muted", style: "margin:auto 0 0" }, "Nothing played in this period yet."),
  );
}

// ---------------------------------------------------------------- right now
function renderNow() {
  const room = state.live?.room;
  const r = room?.receiver;
  if (!r) return;

  const on = r.connected && r.powerOn;
  const np = r.nowPlaying;
  const title = on ? (np?.title || r.inputSource || "Receiver on") : r.connected ? "Receiver in standby" : "Receiver offline";
  // Live TV names its channel and times in the detail; the HEOS service is stale then.
  const detail = on
    ? (np?.liveTv ? np.detail : [np?.detail || np?.artist, np?.service].filter(Boolean).join(" · "))
    : r.connected ? "Turn it on to see what plays." : (r.lastError || "Denon Desktop cannot reach the receiver.");

  const volume = Number(r.volume) || 0;
  const leds = h("div", { class: "vol-leds", role: "img", "aria-label": `Volume ${volume} of 98` },
    Array.from({ length: 32 }, (_, i) => {
      const lit = on && !r.muted && i < Math.round((volume / 98) * 32);
      const c = heat(i / 31);
      return h("i", { style: `background:${lit ? rgb(c) : rgb(c, state.ramp.ghost * 2.2)};${lit ? `box-shadow:0 0 5px ${rgb(c, state.ramp.bloom * 0.8)}` : ""}` });
    }));

  const issues = (room.insights || []).filter((i) => i.severity !== "info");
  const check = issues.length === 0
    ? h("p", { class: "room-check" }, icon("IconCheck", 16, "var(--accent-emerald)"), "Room check: all clear.")
    : h("p", { class: "room-check" }, icon("IconNavHealth", 16, "var(--accent-amber)"),
      `Room check: ${issues.slice(0, 2).map((i) => i.title).join("; ")}${issues.length > 2 ? ` and ${issues.length - 2} more` : ""}.`);

  const habits = state.live.habits;
  const learned = habits?.preferredVolumeByTimeBlock?.[habits.currentTimeBlock];
  const tv = on ? np?.liveTv : null;

  fill("now",
    h("div", { class: "card-head" },
      label("Right now"),
      h("span", { class: "label mono" }, (r.name || r.model || "").toUpperCase())),
    h("div", { class: tv ? "now-head" : null },
      tv ? logo(tv.logo, 56, tv.channelName) : null,
      h("div", null,
        h("p", { class: "now-title" }, title),
        detail ? h("p", { class: "now-detail" }, detail) : null)),
    tv ? programBar(tv) : null,
    h("div", { class: "readings" },
      reading("Input", on ? r.inputSource || "—" : "—"),
      reading("Sound mode", on ? r.soundMode || "—" : "—"),
      reading("Volume", on ? (r.muted ? "Muted" : String(r.volume)) : "—"),
      reading("Usual now", learned ? String(Math.round(learned.averageVolume)) : "Learning…")),
    leds,
    check);
}

// How far the Live TV program has come, from the TV's guide, and what comes next.
function programBar(tv) {
  if (!tv.programStart || !tv.programEnd) return null;
  const start = new Date(tv.programStart);
  const end = new Date(tv.programEnd);
  const now = Date.now();
  const share = Math.min(1, Math.max(0, (now - start) / Math.max(1, end - start)));
  const left = Math.max(0, Math.round((end - now) / 60000));
  const bar = h("div", { class: "meter", role: "progressbar", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": String(Math.round(share * 100)), "aria-label": "Program progress" },
    h("span", { style: `width:${share * 100}%;background:${kindColor("tv_channel")}` }));
  return h("div", { class: "program" },
    bar,
    h("div", { class: "program-times" },
      h("span", null, fmtTime(start)),
      h("span", null, left > 0 ? `${left} min left` : "Ending"),
      h("span", null, fmtTime(end))),
    tv.nextProgramName
      ? h("p", { class: "now-detail" }, `Next ${tv.nextProgramStart ? fmtTime(new Date(tv.nextProgramStart)) : ""}: ${tv.nextProgramName}`)
      : null);
}

const reading = (name, value) => h("div", { class: "reading" }, h("span", { class: "label" }, name.toUpperCase()), h("span", { class: "value", title: value }, value));

// ---------------------------------------------------------------- observations
function renderObservations() {
  const items = state.report.insights;
  fill("observations", items.map((i) => h("article", { class: "observation", dataset: { tone: i.tone } }, h("h3", null, i.title), h("p", null, i.text))));
}

// ---------------------------------------------------------------- daily bars
function niceStep(maxSec) {
  const steps = [300, 900, 1800, 3600, 7200, 10800, 14400, 21600, 28800, 43200];
  return steps.find((st) => maxSec / st <= 4) ?? 86400;
}

function renderDaily() {
  const { watch, period } = state.report;
  const host = fill("daily",
    h("div", { class: "card-head" },
      h("div", null, label(period.days === 1 ? "Time per hour" : "Time per day"), headline(dailyHeadline(watch, period)))));

  if (watch.totalSeconds === 0) {
    host.append(empty("No sessions in this period. Time shows here per day once something plays on the receiver, the TV, a cast or the Apple TV."));
    return;
  }

  if (period.days === 1) {
    host.append(hourlyBars(host));
    return;
  }

  const kinds = watch.byKind.map((k) => k.key);
  const width = Math.max(300, host.clientWidth - 2 * 20);
  const height = 230;
  const pad = { l: 44, r: 6, t: 10, b: 26 };
  const innerW = width - pad.l - pad.r;
  const innerH = height - pad.t - pad.b;
  const max = Math.max(...watch.byDay.map((d) => d.seconds), 60);
  const step = niceStep(max);
  const top = Math.ceil(max / step) * step;
  const n = watch.byDay.length;
  const slot = innerW / n;
  const barW = Math.max(2, Math.min(38, slot * 0.64));
  const y = (v) => pad.t + innerH - (v / top) * innerH;

  const svg = s("svg", { class: "chart", viewBox: `0 0 ${width} ${height}`, width, height, role: "img", "aria-label": `Watch and listen time per day, ${period.label}` });
  for (let v = 0; v <= top; v += step) {
    svg.append(s("line", { class: "grid-line", x1: pad.l, x2: width - pad.r, y1: y(v), y2: y(v) }));
    svg.append(s("text", { x: pad.l - 8, y: y(v) + 4, "text-anchor": "end" }, v === 0 ? "0" : shortDuration(v)));
  }

  const labelEvery = n <= 14 ? 1 : n <= 31 ? 5 : 15;
  const todayIso = state.report.period.to;
  watch.byDay.forEach((day, i) => {
    const x = pad.l + i * slot + (slot - barW) / 2;
    let acc = 0;
    const g = s("g");
    for (const kind of kinds) {
      const v = day.byKind[kind] || 0;
      if (!v) continue;
      const h0 = (v / top) * innerH;
      g.append(s("rect", { x, y: y(acc + v), width: barW, height: Math.max(1, h0 - 1), rx: Math.min(3, barW / 4), style: `fill:${kindColor(kind)}` }));
      acc += v;
    }
    svg.append(g);

    const date = dateOf(day.date);
    if (i % labelEvery === 0 || i === n - 1) {
      const text = n <= 7 ? fmtDay(date, { weekday: "short" }) : n <= 14 ? fmtDay(date, { weekday: "narrow", day: "numeric" }) : fmtDay(date, { day: "numeric", month: "short" });
      svg.append(s("text", { x: pad.l + i * slot + slot / 2, y: height - 6, "text-anchor": "middle", class: day.date === todayIso ? "today" : null }, text));
    }

    const hit = s("rect", { class: "bar-hit", x: pad.l + i * slot, y: pad.t, width: slot, height: innerH, rx: 3 });
    const lines = watch.byKind.filter((k) => day.byKind[k.key]).map((k) => [k.label, duration(day.byKind[k.key])]);
    setTip(hit, `${fmtDay(date, { weekday: "long", day: "numeric", month: "short" })} · ${day.text}`, lines.length ? lines : [["Nothing played"]]);
    svg.append(hit);
  });

  host.append(svg, h("ul", { class: "legend" }, watch.byKind.map((k) => h("li", null, h("i", { class: "swatch", style: `background:${kindColor(k.key)}` }), k.label))));
}

// Today: the hours of today (the rhythm row of today's weekday holds only today in a one-day period).
function hourlyBars(host) {
  const { rhythm } = state.report;
  const row = rhythm.cells[(new Date().getDay() + 6) % 7];
  const width = Math.max(300, host.clientWidth - 40);
  const height = 200;
  const pad = { l: 44, r: 6, t: 10, b: 26 };
  const innerW = width - pad.l - pad.r;
  const innerH = height - pad.t - pad.b;
  const top = Math.max(15 * 60, Math.ceil(Math.max(...row) / 900) * 900);
  const y = (v) => pad.t + innerH - (v / top) * innerH;
  const slot = innerW / 24;
  const barW = Math.max(3, slot * 0.66);
  const nowHour = new Date().getHours();
  const svg = s("svg", { class: "chart", viewBox: `0 0 ${width} ${height}`, width, height, role: "img", "aria-label": "Watch and listen time per hour today" });
  for (const v of [0, top / 2, top]) {
    svg.append(s("line", { class: "grid-line", x1: pad.l, x2: width - pad.r, y1: y(v), y2: y(v) }));
    svg.append(s("text", { x: pad.l - 8, y: y(v) + 4, "text-anchor": "end" }, v === 0 ? "0" : `${Math.round(v / 60)} min`));
  }
  row.forEach((v, hr) => {
    const x = pad.l + hr * slot + (slot - barW) / 2;
    if (v > 0) svg.append(s("rect", { x, y: y(v), width: barW, height: Math.max(1, (v / top) * innerH), rx: 2, style: `fill:${rgb(heat(0.15 + 0.8 * Math.sqrt(v / top)))}` }));
    if (hr % 3 === 0) svg.append(s("text", { x: pad.l + hr * slot + slot / 2, y: height - 6, "text-anchor": "middle", class: hr === nowHour ? "today" : null }, String(hr).padStart(2, "0")));
    const hit = s("rect", { class: "bar-hit", x: pad.l + hr * slot, y: pad.t, width: slot, height: innerH, rx: 3 });
    setTip(hit, `${String(hr).padStart(2, "0")}:00–${String((hr + 1) % 24).padStart(2, "0")}:00`, [[v > 0 ? duration(v) : (hr > nowHour ? "Still to come" : "Nothing played")]]);
    svg.append(hit);
  });
  return svg;
}

function dailyHeadline(watch, period) {
  if (watch.totalSeconds === 0) return "Nothing played yet";
  if (period.days === 1) {
    const hour = state.report.rhythm.peakHour;
    return hour === null || hour === undefined ? `${watch.totalText} today` : `${watch.totalText} today, most around ${String(hour).padStart(2, "0")}:00`;
  }
  const best = watch.byDay.reduce((a, b) => (b.seconds > a.seconds ? b : a));
  return `Busiest: ${fmtDay(dateOf(best.date), { weekday: "long", day: "numeric", month: "short" })}, ${best.text}`;
}

// ---------------------------------------------------------------- by source (donut)
function renderKinds() {
  const { watch } = state.report;
  const host = fill("kinds", label("By source"), headline(watch.byKind[0] ? `${watch.byKind[0].label}: ${pct(watch.byKind[0].share)}` : "No sources yet"));
  if (!watch.byKind.length) {
    host.append(empty("Chromecast, Apple TV, TV apps, receiver inputs and HEOS music each get a slice."));
    return;
  }

  const size = 150;
  const r = 58;
  const c = 2 * Math.PI * r;
  const svg = s("svg", { class: "chart", viewBox: `0 0 ${size} ${size}`, width: size, height: size, role: "img", "aria-label": watch.byKind.map((k) => `${k.label} ${k.text}`).join(", ") });
  svg.append(s("circle", { cx: size / 2, cy: size / 2, r, fill: "none", style: "stroke:var(--control)", "stroke-width": 16 }));
  let offset = 0;
  const gap = watch.byKind.length > 1 ? 2 : 0;
  for (const k of watch.byKind) {
    const len = Math.max(0, k.share * c - gap);
    const arc = s("circle", {
      cx: size / 2, cy: size / 2, r, fill: "none", "stroke-width": 16,
      "stroke-dasharray": `${len} ${c - len}`, "stroke-dashoffset": -offset,
      transform: `rotate(-90 ${size / 2} ${size / 2})`, style: `stroke:${kindColor(k.key)}`,
    });
    setTip(arc, k.label, [[k.text, pct(k.share)], [`${k.sessions} ${k.sessions === 1 ? "session" : "sessions"}`]]);
    svg.append(arc);
    offset += k.share * c;
  }
  svg.append(s("text", { class: "donut-center", x: size / 2, y: size / 2 + 5, "text-anchor": "middle" }, shortDuration(watch.totalSeconds)));

  host.append(h("div", { class: "donut-wrap" }, svg,
    h("ul", { class: "rows" }, watch.byKind.map((k) => h("li", { class: "row" },
      h("span", { class: "row-name" }, h("i", { class: "swatch", style: `background:${kindColor(k.key)};display:inline-block;margin-right:8px` }), k.label),
      h("span", { class: "row-value" }, pct(k.share)))))));
}

// ---------------------------------------------------------------- rhythm: LED matrix
function renderRhythm() {
  const { rhythm, watch } = state.report;
  const host = fill("rhythm", label("Weekly rhythm"), headline(watch.totalSeconds ? `Prime time: ${rhythm.peakText}` : "Your rhythm shows here"));
  const screen = h("div", { class: "led-screen" });
  const matrix = h("div", { class: "matrix", role: "img", "aria-label": `Watch and listen time by weekday and hour. ${rhythm.peakText}.` });
  const now = new Date();
  const nowDay = (now.getDay() + 6) % 7;
  const nowHour = now.getHours();
  const max = rhythm.maxCell || 1;

  for (let d = 0; d < 7; d++) {
    matrix.append(h("span", { class: "day", style: `grid-row:${d + 1}` }, WEEKDAYS[d]));
    for (let hr = 0; hr < 24; hr++) {
      const v = rhythm.cells[d][hr];
      const t = v > 0 ? 0.12 + 0.8 * Math.sqrt(v / max) : 0;
      const c = heat(t);
      const cell = h("span", {
        class: "cell",
        style: `grid-row:${d + 1};grid-column:${hr + 2};background:${v > 0 ? rgb(c) : rgb(state.ramp.stops[0][1], state.ramp.ghost * 2.4)};${v > 0 ? `box-shadow:0 0 ${4 + 6 * t}px ${rgb(c, state.ramp.bloom * t)}` : ""}`,
        "data-now": d === nowDay && hr === nowHour ? "" : null,
      });
      setTip(cell, `${["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"][d]} ${String(hr).padStart(2, "0")}:00–${String((hr + 1) % 24).padStart(2, "0")}:00`, [[v > 0 ? duration(v) : "Nothing played"]]);
      matrix.append(cell);
    }
  }
  for (let hr = 0; hr < 24; hr += 3) {
    matrix.append(h("span", { class: "hour", style: `grid-column:${hr + 2} / span 3` }, String(hr).padStart(2, "0")));
  }
  screen.append(matrix);
  host.append(screen);

  host.append(h("div", { class: "blocks" }, rhythm.byBlock.map((b) => h("div", { class: "block" },
    h("b", null, b.seconds ? pct(b.share) : "—"),
    h("span", null, `${b.label} · ${b.seconds ? b.text : "none"}`)))));

  if (watch.totalSeconds === 0) {
    host.append(empty("Each light is one hour of one weekday. The brighter it glows, the more played in that hour. The outlined light is now."));
  }
}

// ---------------------------------------------------------------- top apps and titles
function renderApps() {
  const { watch } = state.report;
  const host = fill("apps", label("Top apps & inputs"), headline(watch.topApps[0] ? `${watch.topApps[0].label} leads` : "No apps yet"));
  if (!watch.topApps.length) {
    host.append(empty("DRTV, Netflix, YouTube, Spotify, HDMI inputs and more rank here by time."));
    return;
  }
  const max = watch.topApps[0].seconds || 1;
  host.append(h("ul", { class: "rows" }, watch.topApps.map((a) => {
    const row = h("li", { class: "row" },
      h("span", { class: "row-name" }, a.logo ? logo(a.logo, 22, a.label) : null, a.label),
      h("span", { class: "row-value" }, a.text),
      h("div", { class: "meter" }, h("span", { style: `width:${(a.seconds / max) * 100}%;background:${kindColor(a.kind)}` })));
    setTip(row, a.label, [["Time", a.text], ["Share", pct(a.share)], ["Sessions", a.sessions], ["Source", kindLabel(a.kind)]]);
    return row;
  })));
}

// Live TV by channel: a logo tile per channel with its time.
function renderChannels() {
  const channels = state.report.watch.topChannels ?? [];
  const el = document.getElementById("channels");
  el.hidden = channels.length === 0;
  if (!channels.length) {
    el.replaceChildren();
    return;
  }
  const total = channels.reduce((sum, c) => sum + c.seconds, 0);
  const programs = channels.reduce((sum, c) => sum + c.sessions, 0);
  const host = fill("channels",
    label("Live TV channels"),
    headline(`${channels[0].label} leads · ${duration(total)} of live TV in ${programs} ${programs === 1 ? "program" : "programs"}`));
  const max = channels[0].seconds || 1;
  host.append(h("ul", { class: "channels" }, channels.map((c) => {
    const tile = h("li", { class: "channel" },
      logo(c.logo, 56, c.label),
      h("span", { class: "channel-name" }, c.label),
      h("span", { class: "row-value" }, c.text),
      h("div", { class: "meter" }, h("span", { style: `width:${(c.seconds / max) * 100}%;background:${kindColor("tv_channel")}` })));
    setTip(tile, c.label, [["Time", c.text], ["Share of live TV", pct(c.share)], ["Programs", c.sessions]]);
    return tile;
  })));
}

function kindLabel(kind) {
  return state.report.watch.byKind.find((k) => k.key === kind)?.label ?? kind;
}

function renderTitles() {
  const { watch } = state.report;
  const longest = watch.longest;
  const host = fill("titles", label("Most watched"), headline(watch.topTitles[0] ? watch.topTitles[0].label : "No titles yet"));
  if (!watch.topTitles.length) {
    host.append(empty("Program, episode and track titles show here when the source shares them (Apple TV, Chromecast apps, HEOS music)."));
    return;
  }
  host.append(h("ol", { class: "rows" }, watch.topTitles.map((t) => h("li", { class: "row" },
    h("span", { class: "row-name", title: t.label }, t.logo ? logo(t.logo, 22, t.detail ?? "") : null, t.label),
    h("span", { class: "row-value" }, t.text),
    h("span", { class: "row-sub" }, `${t.detail ?? ""} · ${t.sessions} ${t.sessions === 1 ? "session" : "sessions"}`)))));

  if (longest) {
    const start = new Date(longest.start);
    host.append(h("p", { class: "sub-label label" }, "LONGEST SITTING"),
      h("p", { class: "row-name", style: "white-space:normal;margin:0" },
        `${longest.title === longest.app ? longest.title : `${longest.title} · ${longest.app}`}, ${longest.text} on ${fmtDay(start, { weekday: "long" })} at ${fmtTime(start)}`));
  }
}

// ---------------------------------------------------------------- volume
function renderVolume() {
  const { receiver, period } = state.report;
  const vol = receiver.volume;
  const habits = state.live?.habits?.preferredVolumeByTimeBlock ?? {};
  const host = fill("volume", label("Volume"),
    headline(vol.average !== null && vol.average !== undefined ? `Average ${Math.round(vol.average)}` : "No volume yet"));

  if (vol.points.length) {
    host.append(volumeChart(vol, period));
  } else {
    host.append(empty("Each settled volume change on the receiver is a point here, from any remote, the app or an agent."));
  }

  host.append(h("p", { class: "sub-label label" }, "BY TIME OF DAY · THIS PERIOD, AND LEARNED"));
  host.append(h("div", { class: "block-volumes" }, vol.byBlock.map((b) => {
    const learned = habits[b.block];
    const el = h("div", { class: "block" },
      h("b", null, b.average !== null && b.average !== undefined ? String(Math.round(b.average)) : learned ? String(Math.round(learned.averageVolume)) : "—"),
      h("span", null, `${b.label}${b.average === null || b.average === undefined ? (learned ? " · learned" : "") : ` · ${b.samples}×`}`));
    setTip(el, b.label, [
      ["This period", b.average !== null && b.average !== undefined ? String(b.average) : "—"],
      ["Learned", learned ? `${learned.averageVolume} (${learned.samples} samples)` : "—"],
    ]);
    return el;
  })));

  // The average and the blocks above are weighted by listening time; so is each app here.
  if (vol.byApp?.length) {
    host.append(h("p", { class: "sub-label label" }, "BY APP · WEIGHTED BY LISTENING TIME"));
    host.append(h("ul", { class: "rows" }, vol.byApp.map((a) => h("li", { class: "row" },
      h("span", { class: "row-name", title: a.label }, a.label),
      h("span", { class: "row-value" }, String(Math.round(a.average))),
      h("span", { class: "row-sub" }, duration(a.seconds))))));
  }
}

function volumeChart(vol, period) {
  const host = document.getElementById("volume");
  const width = Math.max(280, host.clientWidth - 40);
  const height = 170;
  const pad = { l: 52, r: 8, t: 10, b: 22 };
  const innerW = width - pad.l - pad.r;
  const innerH = height - pad.t - pad.b;
  const from = dateOf(period.from).getTime();
  const to = Date.now();
  const pts = vol.points.map((p) => ({ t: new Date(p.ts).getTime(), v: p.volume, db: p.db }));
  let lo = Math.min(...pts.map((p) => p.v));
  let hi = Math.max(...pts.map((p) => p.v));
  if (hi - lo < 6) {
    lo -= 3;
    hi += 3;
  }
  lo = Math.floor(lo - 1);
  hi = Math.ceil(hi + 1);
  const x = (t) => pad.l + ((t - from) / Math.max(1, to - from)) * innerW;
  const y = (v) => pad.t + innerH - ((v - lo) / (hi - lo)) * innerH;

  const svg = s("svg", { class: "chart", viewBox: `0 0 ${width} ${height}`, width, height, role: "img", "aria-label": `Volume changes, ${period.label}` });
  const gid = `vol-fill-${Math.random().toString(36).slice(2)}`;
  svg.append(s("defs", null, s("linearGradient", { id: gid, x1: 0, y1: 0, x2: 0, y2: 1 },
    s("stop", { offset: "0%", style: "stop-color:var(--meter-left-accent);stop-opacity:0.32" }),
    s("stop", { offset: "100%", style: "stop-color:var(--meter-left-accent);stop-opacity:0" }))));

  for (const v of [lo, Math.round((lo + hi) / 2), hi]) {
    svg.append(s("line", { class: "grid-line", x1: pad.l, x2: width - pad.r, y1: y(v), y2: y(v) }));
    svg.append(s("text", { x: pad.l - 8, y: y(v) + 4, "text-anchor": "end" }, db(v)));
  }

  // Steps: the volume holds until the next change.
  let d = `M${x(Math.max(from, pts[0].t))},${y(pts[0].v)}`;
  for (let i = 1; i < pts.length; i++) d += ` H${x(pts[i].t)} V${y(pts[i].v)}`;
  d += ` H${x(to)}`;
  const area = `${d} V${pad.t + innerH} H${x(Math.max(from, pts[0].t))} Z`;
  svg.append(s("path", { d: area, fill: `url(#${gid})` }));
  svg.append(s("path", { d, fill: "none", style: "stroke:var(--meter-left-accent)", "stroke-width": 2, "stroke-linejoin": "round" }));

  for (const p of pts) {
    const dot = s("circle", { cx: x(p.t), cy: y(p.v), r: 3.5, style: "fill:var(--meter-left-accent);stroke:var(--card);stroke-width:1.5" });
    const date = new Date(p.t);
    setTip(dot, `${fmtDay(date, { weekday: "short", day: "numeric", month: "short" })} ${fmtTime(date)}`, [["Volume", String(p.v)]]);
    svg.append(dot);
  }

  const days = period.days;
  const tickEvery = days <= 1 ? 0 : days <= 7 ? 1 : days <= 31 ? 7 : 30;
  if (tickEvery === 0) {
    for (let hr = 0; hr <= 24; hr += 6) {
      const t = from + hr * 3600e3;
      if (t <= to) svg.append(s("text", { x: x(t), y: height - 4, "text-anchor": "middle" }, `${String(hr).padStart(2, "0")}:00`));
    }
  } else {
    for (let i = 0; i < days; i += tickEvery) {
      const date = new Date(from + i * 86400e3);
      svg.append(s("text", { x: x(date.getTime() + 43200e3), y: height - 4, "text-anchor": "middle" },
        days <= 7 ? fmtDay(date, { weekday: "short" }) : fmtDay(date, { day: "numeric", month: "short" })));
    }
  }
  return svg;
}

// ---------------------------------------------------------------- receiver
function renderReceiver() {
  const { receiver, period } = state.report;
  const host = fill("receiver", label("Receiver"), headline(receiver.inUseSeconds ? `In use ${receiver.inUseText}` : "No receiver sessions yet"));

  host.append(h("div", { class: "facts" },
    fact(receiver.powerOns, receiver.powerOns === 1 ? "time turned on" : "times turned on"),
    fact(receiver.inputs.length, receiver.inputs.length === 1 ? "input used" : "inputs used"),
    fact(receiver.volume.changes, receiver.volume.changes === 1 ? "volume change" : "volume changes")));

  if (period.days > 1 && receiver.inUseSeconds) {
    host.append(h("p", { class: "sub-label label" }, "IN USE PER DAY"), miniBars(receiver.byDay, "var(--accent-emerald)"));
  }

  if (receiver.inputs.length) {
    host.append(h("p", { class: "sub-label label" }, "INPUTS"));
    const max = receiver.inputs[0].seconds || 1;
    host.append(h("ul", { class: "rows" }, receiver.inputs.slice(0, 5).map((i) => h("li", { class: "row" },
      h("span", { class: "row-name", title: i.detail ? `${i.label} · ${i.detail}` : i.label }, i.label),
      h("span", { class: "row-value" }, `${i.text} · ${pct(i.share)}`),
      i.detail ? h("span", { class: "row-sub" }, i.detail) : null,
      h("div", { class: "meter" }, h("span", { style: `width:${(i.seconds / max) * 100}%;background:var(--accent-emerald)` }))))));
  }

  if (receiver.soundModes.length) {
    host.append(h("p", { class: "sub-label label" }, "SOUND MODES"),
      h("ul", { class: "tags" }, receiver.soundModes.map((m) => h("li", { class: "tag" }, m.label, h("b", null, pct(m.share))))));
  }
}

const fact = (value, text) => h("div", { class: "fact" }, h("b", null, value), h("span", null, text));

function miniBars(days, color) {
  const host = document.getElementById("receiver");
  const width = Math.max(260, host.clientWidth - 40);
  const labels = days.length <= 14;
  const height = labels ? 80 : 64;
  const barsH = labels ? 60 : 64;
  const max = Math.max(...days.map((d) => d.seconds), 60);
  const slot = width / days.length;
  const barW = Math.max(2, Math.min(26, slot * 0.6));
  const svg = s("svg", { class: "chart", viewBox: `0 0 ${width} ${height}`, width, height, role: "img", "aria-label": "Receiver time per day" });
  days.forEach((d, i) => {
    const bh = d.seconds ? Math.max(2, (d.seconds / max) * (barsH - 4)) : 1;
    svg.append(s("rect", { x: i * slot + (slot - barW) / 2, y: barsH - bh, width: barW, height: bh, rx: Math.min(2, barW / 4), style: `fill:${d.seconds ? color : "var(--control)"}` }));
    if (labels) {
      svg.append(s("text", { x: i * slot + slot / 2, y: height - 4, "text-anchor": "middle" }, fmtDay(dateOf(d.date), days.length <= 7 ? { weekday: "short" } : { weekday: "narrow" })));
    }
    const hit = s("rect", { class: "bar-hit", x: i * slot, y: 0, width: slot, height });
    setTip(hit, fmtDay(dateOf(d.date), { weekday: "long", day: "numeric", month: "short" }), [[d.seconds ? duration(d.seconds) : "Not used"]]);
    svg.append(hit);
  });
  return svg;
}

// ---------------------------------------------------------------- who's in control, lighting
function renderControl() {
  const { control, lighting } = state.report;
  const host = fill("control", label("Who's in control"),
    headline(control.total ? (control.fromOutside >= control.fromApp ? "Mostly the remotes and devices" : "Mostly Denon Desktop") : "No changes yet"));

  if (control.total) {
    host.append(h("div", { class: "stack", role: "img", "aria-label": control.origins.map((o) => `${o.label} ${o.count}`).join(", ") },
      control.origins.map((o) => {
        const seg = h("span", { style: `flex:${o.count} 1 0;background:${originColor(o.key)}` });
        setTip(seg, o.label, [[`${o.count} ${o.count === 1 ? "change" : "changes"}`, pct(o.share)]]);
        return seg;
      })));
    host.append(h("ul", { class: "rows" }, control.origins.map((o) => h("li", { class: "row" },
      h("span", { class: "row-name" }, h("i", { class: "swatch", style: `background:${originColor(o.key)};display:inline-block;margin-right:8px` }), o.label),
      h("span", { class: "row-value" }, `${o.count} · ${pct(o.share)}`)))));
  } else {
    host.append(empty("Power, input, volume and TV changes count here, split by who made them: a remote, this app, the phone remote, an automation or an AI agent."));
  }

  host.append(h("p", { class: "sub-label label" }, "LIGHTING"),
    h("div", { class: "facts" },
      fact(lighting.changes, lighting.changes === 1 ? "light change" : "light changes"),
      fact(lighting.musicSyncStarts, "music sync starts"),
      lighting.lastState ? h("div", { class: "fact" }, h("b", { style: "font-size:14px" }, lighting.lastState), h("span", null, "last state")) : null));
}

// ---------------------------------------------------------------- automations
function renderAutomations() {
  const a = state.report.automations;
  const host = fill("automations", label("Automations"),
    headline(a.runs ? `${a.acted} of ${a.runs} ${a.runs === 1 ? "run" : "runs"} changed something` : "No automation runs yet"));
  if (!a.runs) {
    host.append(empty("Each automation run shows here with what it did: ran a scene, nothing to do (and why), or failed. A run that only checked and found nothing to do does not count as a change."));
    return;
  }

  host.append(h("div", { class: "facts" },
    fact(a.acted, "changed something"),
    fact(a.nothing, "nothing to do"),
    fact(a.failed, "failed")));
  host.append(h("ul", { class: "rows" }, a.byAutomation.slice(0, 8).map((r) => {
    const when = r.lastTs ? new Date(r.lastTs) : null;
    return h("li", { class: "row" },
      h("span", { class: "row-name", title: r.name }, r.name),
      h("span", { class: "row-value" }, `${r.acted} of ${r.runs}`),
      h("span", { class: "row-sub", title: r.lastSummary ?? "" },
        `${when ? `${fmtDay(when, { weekday: "short" })} ${fmtTime(when)}: ` : ""}${r.lastSummary ?? ""}`));
  })));
}

// ---------------------------------------------------------------- activity feed
const STREAMS = [["all", "All"], ["media", "Media"], ["avr", "Receiver"], ["tv", "TV"], ["lighting", "Lighting"], ["control", "Control"]];

function renderActivity() {
  if (!state.activity) return;
  const items = state.activity.items.filter((i) => state.showQuiet || !i.quiet);
  const hidden = state.activity.items.length - items.length;

  const filters = h("div", { class: "filters", role: "group", "aria-label": "Filter by stream" },
    STREAMS.map(([key, name]) => h("button", {
      type: "button", class: "chip", "aria-pressed": String(state.stream === key),
      onclick: () => {
        state.stream = key;
        loadActivity();
      },
    }, name)),
    h("label", { class: "toggle" },
      h("input", {
        type: "checkbox", checked: state.showQuiet,
        onchange: (e) => {
          state.showQuiet = e.target.checked;
          renderActivity();
        },
      }),
      `Connection events${hidden ? ` (${hidden})` : ""}`));

  const host = fill("activity",
    h("div", { class: "card-head" }, h("div", null, label("Activity"), headline(items.length ? `${items.length} events · ${state.report?.period.label ?? ""}` : "No events")), filters));

  if (!items.length) {
    host.append(empty("Nothing recorded for this filter and period."));
    return;
  }

  const feed = h("div", { class: "feed" });
  const today = new Date();
  const yesterday = new Date(Date.now() - 86400e3);
  let currentDay = "";
  let list = null;
  for (const item of items) {
    const at = new Date(item.ts);
    const dayKey = at.toDateString();
    if (dayKey !== currentDay) {
      currentDay = dayKey;
      const name = dayKey === today.toDateString() ? "Today" : dayKey === yesterday.toDateString() ? "Yesterday" : fmtDay(at, { weekday: "long", day: "numeric", month: "long" });
      list = h("ul");
      feed.append(h("h3", { class: "feed-day" }, name.toUpperCase()), list);
    }
    list.append(h("li", { class: "event" },
      h("time", { datetime: item.ts }, fmtTime(at)),
      h("span", { class: "dot", style: `background:${streamColor(item.stream)}` }),
      h("span", { class: "event-text" }, item.logo ? logo(item.logo, 20, "") : null, item.text),
      h("span", { class: "event-meta" }, item.device,
        h("span", { class: "origin", style: item.origin !== "external" ? `color:${originColor(item.origin)};border-color:currentColor` : null }, item.originLabel))));
  }
  host.append(feed);
}

// ---------------------------------------------------------------- footer
function renderFooter() {
  const log = state.live?.logging;
  if (!log) return;
  const size = log.bytes >= 1048576 ? `${(log.bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(log.bytes / 1024))} KB`;
  fill("footer",
    h("div", { class: "card" },
      label("Private by design"),
      h("p", null,
        `Everything on this page stays on this PC. Denon Desktop reads its own event log (${log.files} ${log.files === 1 ? "file" : "files"}, ${size}, kept ${log.retentionDays} days) and serves this page only to this computer, with a new key each time the app starts. Sessions shorter than ${log.dwellSeconds} s are not recorded.`)),
    h("div", { class: "card" },
      label("Ask an AI agent"),
      h("p", null, "The same data is open to an agent over MCP (Settings → MCP): ",
        h("code", null, "get_watch_time"), ", ", h("code", null, "get_listening_habits"), " and ", h("code", null, "get_activity_history"), "."),
      h("p", { class: "muted" }, `Denon Desktop ${state.live.app}`)));
}

// ---------------------------------------------------------------- orchestration
function renderReport() {
  if (!state.report) return;
  renderHero();
  renderObservations();
  renderDaily();
  renderKinds();
  renderRhythm();
  renderApps();
  renderChannels();
  renderTitles();
  renderVolume();
  renderReceiver();
  renderControl();
  renderAutomations();
  renderActivity();
}

function setDays(days) {
  state.days = days;
  for (const chip of document.querySelectorAll(".period .chip")) chip.setAttribute("aria-pressed", String(Number(chip.dataset.days) === days));
  try {
    localStorage.setItem("insights.days", String(days));
  } catch {
    // Storage can be off; the period then resets on reload.
  }
  history.replaceState(null, "", `#days=${days}`);
}

async function start() {
  state.ramp = readRamp();
  const fromHash = Number(/days=(\d+)/.exec(location.hash)?.[1]);
  let stored = NaN;
  try {
    stored = Number(localStorage.getItem("insights.days"));
  } catch {
    // ignore
  }
  setDays([1, 7, 30, 90].includes(fromHash) ? fromHash : [1, 7, 30, 90].includes(stored) ? stored : 7);

  for (const chip of document.querySelectorAll(".period .chip")) {
    chip.addEventListener("click", () => {
      setDays(Number(chip.dataset.days));
      loadReport();
      loadActivity();
    });
  }

  try {
    state.icons = await api("/icons.json");
  } catch {
    state.icons = {};
  }

  await loadLive();
  await Promise.all([loadReport(), loadActivity()]);

  setInterval(loadLive, 5000);
  setInterval(loadReport, 60000);
  setInterval(loadActivity, 30000);

  let resizeTimer = 0;
  let lastWidth = window.innerWidth;
  window.addEventListener("resize", () => {
    if (window.innerWidth === lastWidth) return;
    lastWidth = window.innerWidth;
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(renderReport, 150);
  });
}

start();

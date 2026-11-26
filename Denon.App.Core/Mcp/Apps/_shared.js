"use strict";
// Shared by every Denon Desktop MCP App card: the MCP Apps view protocol (spec 2026-01-26), JSON-RPC 2.0 over
// postMessage with the host, plus small DOM helpers. A card calls Card.start({...}) once.
const Card = (() => {
  let nextId = 1;
  const pending = new Map();
  let options = {};

  const send = (message) => window.parent.postMessage({ jsonrpc: "2.0", ...message }, "*");
  const notify = (method, params) => send({ method, params });
  const request = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    send({ id, method, params });
    setTimeout(() => { if (pending.delete(id)) reject(new Error("The host did not answer.")); }, 30000);
  });

  const $ = (id) => document.getElementById(id);

  /** el("button", { className: "x", onclick }, "text", child…): text is always set as text, never as markup. */
  function el(tag, props, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (value === undefined || value === null || value === false) continue;
      if (key === "dataset") Object.assign(node.dataset, value);
      else if (key === "style") Object.assign(node.style, value);
      else if (key in node) node[key] = value;
      else node.setAttribute(key, value === true ? "" : String(value));
    }
    for (const child of children.flat()) {
      if (child === undefined || child === null || child === false) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  function applyHostContext(context) {
    if (!context) return;
    if (context.theme) document.documentElement.dataset.theme = context.theme;
    const variables = context.styles?.variables || {};
    for (const [name, value] of Object.entries(variables)) {
      if (value) document.documentElement.style.setProperty(name, value);
    }
  }

  function setStatus(text, isError = false) {
    const status = $("status");
    if (!status) return;
    status.textContent = text || "";
    status.classList.toggle("error", isError);
    resize();
  }

  function structuredFrom(result) {
    if (!result) return null;
    if (result.structuredContent) return result.structuredContent;
    const text = (result.content || []).find((c) => c.type === "text")?.text;
    try { return text ? JSON.parse(text) : null; } catch { return null; }
  }

  const errorText = (result) => (result?.content || []).find((c) => c.type === "text")?.text || "The tool reported an error.";
  const resultText = (result) => (result?.content || []).find((c) => c.type === "text")?.text || "";

  async function callTool(name, args) {
    const result = await request("tools/call", { name, arguments: args || {} });
    if (result?.isError) throw new Error(errorText(result));
    return result;
  }

  let busy = false;
  /** Runs one action: buttons are off meanwhile, the status line says what happens, an error stays in it. */
  async function run(label, action) {
    if (busy) return;
    busy = true;
    document.body.classList.add("busy");
    setStatus(`${label}…`);
    try {
      const message = await action();
      setStatus(typeof message === "string" ? message : "");
    } catch (error) {
      setStatus(error.message || String(error), true);
    } finally {
      busy = false;
      document.body.classList.remove("busy");
    }
  }

  /** Calls the card's own tool again and renders its result. */
  async function reload(args) {
    const data = structuredFrom(await callTool(options.tool, args ?? options.lastArgs ?? {}));
    if (args) options.lastArgs = args;
    if (data) options.render(data);
  }

  /**
   * Calls a tool that changes the receiver, then reads the card again, and once more a little later: the receiver
   * reports some changes (a station's name, the input) only after the next poll.
   */
  async function act(tool, args, reloadArgs) {
    await callTool(tool, args);
    await reload(reloadArgs);
    setTimeout(() => { if (!busy) reload(reloadArgs).catch(() => {}); }, 2500);
  }

  let lastSize = "";
  function resize() {
    const width = document.documentElement.scrollWidth;
    const height = document.documentElement.scrollHeight;
    const size = `${width}x${height}`;
    if (size === lastSize || window.parent === window) return;
    lastSize = size;
    notify("ui/notifications/size-changed", { width, height });
  }

  window.addEventListener("message", (event) => {
    // Only the host that embeds the card speaks to it; messages from any other window are ignored.
    if (event.source !== window.parent) return;
    const message = event.data;
    if (!message || message.jsonrpc !== "2.0") return;
    if (message.id !== undefined && !message.method && pending.has(message.id)) {
      const { resolve, reject } = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) reject(new Error(message.error.message || "Error")); else resolve(message.result);
      return;
    }
    switch (message.method) {
      case "ui/notifications/tool-input":
        options.lastArgs = message.params?.arguments || {};
        options.onInput?.(options.lastArgs);
        break;
      case "ui/notifications/tool-result":
        if (message.params?.isError) setStatus(errorText(message.params), true);
        else { const data = structuredFrom(message.params); if (data) options.render(data); }
        resize();
        break;
      case "ui/notifications/host-context-changed":
        applyHostContext(message.params);
        break;
      case "ui/resource-teardown":
        if (message.id !== undefined) send({ id: message.id, result: {} });
        break;
    }
  });

  /** start({ name, tool, render(data), onInput(args)?, outsideHost? }) */
  async function start(startOptions) {
    options = startOptions;
    new ResizeObserver(resize).observe(document.body);
    if (window.parent === window) {
      options.outsideHost?.();
      return;
    }
    try {
      const init = await request("ui/initialize", {
        protocolVersion: "2026-01-26",
        appInfo: { name: options.name, version: "1.0.0" },
        appCapabilities: { availableDisplayModes: ["inline"] },
      });
      applyHostContext(init?.hostContext);
    } catch {
      setStatus("The host did not start the card.", true);
    }
    notify("ui/notifications/initialized", {});
  }

  return { $, el, start, run, reload, act, callTool, resultText, setStatus, resize, isBusy: () => busy };
})();

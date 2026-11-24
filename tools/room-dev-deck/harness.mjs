// Test harness for room-dev-deck.html: serves the page with a shim that maps the desktop API to this
// process (real files, real commands). Localhost only, token-guarded, for a short test run.
import http from "node:http";
import fs from "node:fs";
import { exec, spawn } from "node:child_process";
import crypto from "node:crypto";

// Usage: node tools/room-dev-deck/harness.mjs [page.html] [port], then open http://127.0.0.1:<port>/
const page = process.argv[2] || new URL("./room-dev-deck.html", import.meta.url);
const port = Number(process.argv[3] || 47811);
const token = crypto.randomBytes(12).toString("hex");

const shim = `<script>
(() => {
  const T = ${JSON.stringify(token)};
  const rpc = async (op, ...args) => {
    const r = await fetch("/rpc", { method: "POST", headers: { "x-t": T, "content-type": "application/json" }, body: JSON.stringify({ op, args }) });
    const j = await r.json(); if (j.error) throw new Error(j.error); return j.value;
  };
  const ok = async () => {};
  window.__deckLog = [];
  window.__deckDesktop = {
    install: { state: async () => "installed", request: async () => "installed" },
    permissions: { state: async (n) => { const all = { "full-access": "granted", notifications: "granted", "menu-bar": "granted", overlay: "unavailable", shortcuts: "unavailable" }; return n ? all[n] : all; }, request: async () => ({ "full-access": "granted", notifications: "granted", "menu-bar": "granted" }), manage: ok },
    window: { setOptions: async (o) => ({ applied: "now", reloading: false, options: o }), show: ok, hide: ok },
    app: { dock: { setIcon: async (u) => { window.__deckIcon = u; return { written: false }; } } },
    tray: { set: async (o) => { window.__deckTray = o; window.__deckLog.push(["tray", o.toolTip]); return true; }, remove: ok },
    notification: { show: async (o) => { window.__deckLog.push(["notify", o.body]); } },
    shell: { showItemInFolder: async (p) => window.__deckLog.push(["show", p]), openPath: async (p) => window.__deckLog.push(["open", p]) },
    fs: {
      readFile: (p, e) => rpc("readFile", p, e), writeFile: (p, d, e) => rpc("writeFile", p, d, e), stat: (p) => rpc("stat", p),
      readdir: (p) => rpc("readdir", p), exists: (p) => rpc("exists", p), rm: (p, o) => rpc("rm", p, o), mkdir: (p, o) => rpc("mkdir", p, o),
    },
    childProcess: {
      exec: (c, o) => rpc("exec", c, o),
      spawn: (cmd, args, opts) => {
        const h = { out: [], err: [], exit: [], error: [] };
        const p = { stdout: { on: (_, f) => h.out.push(f) }, stderr: { on: (_, f) => h.err.push(f) }, on: (ev, f) => (h[ev] || (h[ev] = [])).push(f), kill: async () => rpc("kill") };
        rpc("spawn", cmd, args, opts).then((r) => { if (r.stdout) h.out.forEach((f) => f(r.stdout)); if (r.stderr) h.err.forEach((f) => f(r.stderr)); h.exit.forEach((f) => f(r.code, null)); },
          (e) => h.error.forEach((f) => f(e)));
        return p;
      },
    },
  };
})();
</script>`;

let child = null;
const ops = {
  readFile: (p, e) => fs.readFileSync(p, e === "utf8" ? "utf8" : undefined),
  writeFile: (p, d) => { fs.writeFileSync(p, d, "utf8"); return null; },
  stat: (p) => { const s = fs.statSync(p); return { size: s.size, mtimeMs: s.mtimeMs, isFile: s.isFile(), isDirectory: s.isDirectory() }; },
  readdir: (p) => fs.readdirSync(p, { withFileTypes: true }).map((d) => ({ name: d.name, isFile: d.isFile(), isDirectory: d.isDirectory(), isSymbolicLink: false })),
  exists: (p) => fs.existsSync(p),
  rm: (p, o) => { fs.rmSync(p, { force: !!o?.force }); return null; },
  mkdir: (p, o) => { fs.mkdirSync(p, { recursive: !!o?.recursive }); return null; },
  exec: (c, o) => new Promise((res) => exec(c, { cwd: o?.cwd, timeout: o?.timeout ?? 60000, maxBuffer: 64 << 20, encoding: "utf8" },
    (err, stdout, stderr) => res({ stdout, stderr, code: err ? (typeof err.code === "number" ? err.code : 1) : 0, signal: null, truncated: false }))),
  spawn: (cmd, args, o) => new Promise((res, rej) => {
    let out = "", err = "";
    child = spawn(cmd, args || [], { cwd: o?.cwd });
    child.stdout.on("data", (d) => out += d); child.stderr.on("data", (d) => err += d);
    child.on("error", rej); child.on("close", (code) => { child = null; res({ stdout: out, stderr: err, code }); });
  }),
  kill: () => { child?.kill(); return null; },
};

http.createServer(async (req, res) => {
  if (req.method === "GET" && (req.url === "/" || req.url.startsWith("/?"))) {
    const html = fs.readFileSync(page, "utf8");
    res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    res.end(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body>${shim}${html}</body></html>`);
    return;
  }
  if (req.method === "POST" && req.url === "/rpc" && req.headers["x-t"] === token) {
    let body = ""; for await (const c of req) body += c;
    const { op, args } = JSON.parse(body);
    try { const value = await ops[op](...args); res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify({ value })); }
    catch (e) { res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify({ error: e.message })); }
    return;
  }
  res.writeHead(404); res.end();
}).listen(port, "127.0.0.1", () => console.log(`harness on http://127.0.0.1:${port}`));

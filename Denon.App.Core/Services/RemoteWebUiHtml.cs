namespace Denon.App.Core.Services;

public static class RemoteWebUiHtml
{
    public const string PageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Denon Remote &amp; Jukebox</title>
<style>
  :root {
    --bg: #0a0c10; --card: #13161e; --elev: #1a1f2b; --border: #252c3d;
    --cyan: #00d4ff; --amber: #f59e0b; --green: #10b981; --red: #ef4444;
    --text: #f1f5f9; --sub: #cbd5e1; --muted: #94a3b8;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); padding: 14px 14px 86px; }
  .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; gap: 8px; }
  .brand { display: flex; align-items: center; gap: 8px; font-weight: 800; letter-spacing: 0.04em; font-size: 15px; min-width: 0; }
  .logo { width: 26px; height: 26px; border-radius: 7px; background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; display: grid; place-items: center; font-weight: 900; flex-shrink: 0; }
  .pill { font-size: 11px; font-weight: 700; padding: 5px 10px; border-radius: 99px; border: 1px solid var(--border); background: var(--card); color: var(--sub); cursor: pointer; }
  .pill.on { background: #0c2d22; border-color: var(--green); color: var(--green); }
  .pill.guest { background: #261f0d; border-color: var(--amber); color: #fcd34d; cursor: default; }
  .banner { display: none; align-items: center; justify-content: space-between; gap: 8px; background: #2a1216; border: 1px solid #ef4444; border-radius: 12px; padding: 9px 12px; margin-bottom: 12px; font-size: 12px; color: #fecaca; }
  .banner.show { display: flex; }
  .banner.warn { background: #261c0b; border-color: var(--amber); color: #fde68a; }
  .card { background: var(--card); border: 1px solid var(--border); border-radius: 16px; padding: 14px; margin-bottom: 12px; }
  .hero { background: linear-gradient(135deg, #141925, #0c1018 70%, #0a141d); border-color: #1f3347; }
  .label { font-size: 11px; font-weight: 800; color: var(--muted); letter-spacing: 0.07em; text-transform: uppercase; margin-bottom: 10px; display: flex; justify-content: space-between; gap: 8px; }
  .now-row { display: flex; gap: 12px; align-items: center; }
  .art { width: 62px; height: 62px; border-radius: 12px; background: #07090d; border: 1px solid #223144; object-fit: cover; flex-shrink: 0; }
  .now-title { font-size: 17px; font-weight: 700; line-height: 1.25; overflow: hidden; text-overflow: ellipsis; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
  .now-sub { font-size: 12.5px; color: var(--sub); margin-top: 3px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .badges { display: flex; gap: 6px; margin-bottom: 6px; flex-wrap: wrap; }
  .badge { font-size: 10.5px; font-weight: 700; padding: 2px 7px; border-radius: 5px; background: #191e2a; color: var(--sub); }
  .badge.cyan { background: #0e2430; color: var(--cyan); }
  .badge.amber { color: var(--amber); }
  .transport { display: flex; justify-content: center; gap: 12px; margin-top: 14px; }
  .tbtn { width: 44px; height: 44px; border-radius: 50%; border: 1px solid #2a3142; background: var(--elev); color: var(--text); font-size: 15px; cursor: pointer; display: grid; place-items: center; }
  .tbtn.play { width: 52px; height: 52px; background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; border-color: #7dd3fc; font-weight: 900; font-size: 18px; }
  .oled { background: #07090d; border: 1px solid #1c2738; border-radius: 12px; padding: 10px; text-align: center; margin-bottom: 12px; }
  .oled-big { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 30px; font-weight: 800; color: var(--cyan); }
  .oled-sub { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 11.5px; color: var(--muted); }
  input[type=range] { width: 100%; accent-color: var(--cyan); height: 28px; }
  .vol-row { display: grid; grid-template-columns: 1fr 1fr 1.4fr 1fr 1fr; gap: 6px; margin-top: 8px; }
  .btn { background: var(--elev); color: var(--text); border: 1px solid var(--border); border-radius: 10px; padding: 9px 10px; font-size: 12.5px; font-weight: 600; cursor: pointer; }
  .btn:active { opacity: 0.75; }
  .btn:focus-visible, .chip:focus-visible, .tab:focus-visible, .tbtn:focus-visible, .pill:focus-visible { outline: 2px solid var(--cyan); outline-offset: 2px; }
  .btn.primary { background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; border-color: #38bdf8; font-weight: 800; }
  .btn.muted { background: #4c151b; border-color: var(--red); color: #fca5a5; }
  .chips { display: flex; flex-wrap: wrap; gap: 7px; }
  .chip { background: #181c26; border: 1px solid var(--border); color: var(--sub); border-radius: 9px; padding: 8px 11px; font-size: 12.5px; font-weight: 600; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; }
  .chip.active { background: #0c2e3d; border-color: var(--cyan); color: var(--cyan); }
  .scene-tag { font-size: 9.5px; font-weight: 900; padding: 2px 5px; border-radius: 4px; color: #06121a; }
  .search-row { display: flex; gap: 7px; margin-bottom: 10px; }
  .search-input { flex: 1; background: #0c0f15; border: 1px solid var(--border); border-radius: 10px; color: var(--text); padding: 10px 12px; font-size: 14px; outline: none; }
  .search-input:focus { border-color: var(--cyan); }
  .track { display: flex; gap: 10px; align-items: center; background: #161a24; border: 1px solid var(--border); border-radius: 11px; padding: 8px; margin-bottom: 8px; }
  .track.current { background: #0e2430; border-color: var(--cyan); }
  .thumb { width: 54px; height: 36px; border-radius: 6px; object-fit: cover; background: #07090d; flex-shrink: 0; }
  .tmeta { flex: 1; min-width: 0; }
  .ttitle { font-size: 13px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tsub { font-size: 11.5px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tacts { display: flex; gap: 5px; }
  .tacts .btn { padding: 6px 9px; font-size: 12px; }
  .tabs { position: fixed; bottom: 0; left: 0; right: 0; background: #0e1016; border-top: 1px solid var(--border); display: grid; grid-template-columns: repeat(4, 1fr); padding: 8px 8px calc(8px + env(safe-area-inset-bottom)); gap: 6px; }
  .tab { background: transparent; border: none; color: var(--muted); font-size: 12px; font-weight: 700; padding: 8px 4px; border-radius: 10px; cursor: pointer; }
  .tab.active { background: #142330; color: var(--cyan); }
  .panel { display: none; }
  .panel.active { display: block; }
  .toast { position: fixed; top: 12px; left: 50%; transform: translateX(-50%); background: #0e2430; border: 1px solid var(--cyan); color: var(--text); padding: 8px 14px; border-radius: 99px; font-size: 12px; font-weight: 600; opacity: 0; pointer-events: none; transition: opacity 0.2s; z-index: 99; }
  .toast.show { opacity: 1; }
  .owner-only.hidden { display: none !important; }
  #pairGate, #disabledGate { display: none; }
  #pairGate.show, #disabledGate.show { display: block; }
  #appShell.locked { display: none; }
  .header-locked { display: none !important; }
</style>
</head>
<body>
<div id="toast" class="toast" role="status" aria-live="polite"></div>

<div class="header">
  <div class="brand"><div class="logo" aria-hidden="true">D</div><span id="rxName">DENON REMOTE</span></div>
  <div id="headerControls" style="display:flex;gap:6px;align-items:center">
    <span id="roleBadge" class="pill guest" style="display:none">GUEST</span>
    <button id="powerBtn" class="pill owner-only" type="button" aria-label="Toggle Receiver Power" onclick="post('/api/power')">STANDBY</button>
  </div>
</div>

<!-- DISABLED GATE -->
<div id="disabledGate" class="card" role="alert">
  <div class="label"><span>Phone Remote Disabled</span><span>Desktop Setting</span></div>
  <p id="disabledMsg" style="font-size:13px;color:var(--sub);margin:0 0 12px;line-height:1.45">
    Phone Remote is currently disabled in Denon Desktop. Enable it under Cast &amp; Phone Remote to resume control.
  </p>
  <button class="btn primary" type="button" onclick="refreshStatus()">Retry</button>
</div>

<!-- PAIRING GATE -->
<div id="pairGate" class="card">
  <div class="label"><span>Pairing Required</span><span>Secure Access</span></div>
  <p style="font-size:13px;color:var(--sub);margin:0 0 12px;line-height:1.45">
    Scan the Owner or Guest QR code in <strong>Denon Desktop → Cast &amp; Phone Remote</strong>, or paste your pairing code below.
  </p>
  <div class="search-row">
    <input id="pairInput" class="search-input" type="password" placeholder="Paste pairing token..." aria-label="Pairing token" onkeydown="if(event.key==='Enter') submitPairToken()" />
    <button class="btn primary" type="button" onclick="submitPairToken()">Pair</button>
  </div>
  <div id="pairError" style="font-size:12px;color:#fca5a5"></div>
</div>

<div id="appShell">
  <div id="connBanner" class="banner" role="alert">
    <span id="connBannerText">Connection issue</span>
    <button class="btn" type="button" style="padding:4px 10px;font-size:11.5px" onclick="refreshStatus()">Retry</button>
  </div>

  <!-- TAB 1: REMOTE -->
  <div id="tab-remote" class="panel active" role="tabpanel">
    <div class="card hero">
      <div class="now-row">
        <img id="artImg" class="art" alt="Album artwork" style="display:none" />
        <div style="flex:1; min-width:0">
          <div class="badges">
            <span id="badgeInput" class="badge cyan">HEOS</span>
            <span id="badgeState" class="badge">Connecting</span>
            <span id="badgeMode" class="badge amber">—</span>
          </div>
          <div id="nowTitle" class="now-title">Connecting...</div>
          <div id="nowSub" class="now-sub">Syncing with Denon Desktop</div>
        </div>
      </div>
      <div class="transport owner-only">
        <button class="tbtn" type="button" aria-label="Previous track" title="Previous track" onclick="post('/api/transport', {action:'prev'})"><span aria-hidden="true">⏮</span></button>
        <button id="playPauseBtn" class="tbtn play" type="button" aria-label="Play" title="Play / Pause" onclick="togglePlayPause()"><span aria-hidden="true">▶</span></button>
        <button class="tbtn" type="button" aria-label="Stop playback" title="Stop" onclick="post('/api/transport', {action:'stop'})"><span aria-hidden="true">⏹</span></button>
        <button class="tbtn" type="button" aria-label="Next track" title="Next track" onclick="post('/api/transport', {action:'next'})"><span aria-hidden="true">⏭</span></button>
      </div>
    </div>

    <div class="card owner-only">
      <div class="label"><label for="volSlider">Master Volume</label><span id="volRangeLabel">0–98</span></div>
      <div class="oled">
        <div id="volDb" class="oled-big">---</div>
        <div id="volAbs" class="oled-sub">VOL 0</div>
      </div>
      <input id="volSlider" type="range" min="0" max="98" value="20" aria-label="Master Volume" aria-valuetext="20" oninput="onSlide(this.value)" onchange="commitSlide(this.value)" />
      <div class="vol-row">
        <button class="btn" type="button" aria-label="Decrease volume by 5" onclick="stepVol(-5)">-5</button>
        <button class="btn" type="button" aria-label="Decrease volume by 1" onclick="stepVol(-1)">-1</button>
        <button id="muteBtn" class="btn" type="button" aria-label="Toggle mute" onclick="post('/api/mute')">MUTE</button>
        <button class="btn" type="button" aria-label="Increase volume by 1" onclick="stepVol(1)">+1</button>
        <button class="btn" type="button" aria-label="Increase volume by 5" onclick="stepVol(5)">+5</button>
      </div>
    </div>

    <div class="card owner-only">
      <div class="label"><span>Smart Scenes</span><span>One-Tap Macros</span></div>
      <div id="scenesBox" class="chips"></div>
    </div>

    <div class="card owner-only">
      <div class="label"><span>Input Source</span></div>
      <div id="inputsBox" class="chips"></div>
    </div>
  </div>

  <!-- TAB 2: PARTY JUKEBOX (YOUTUBE) -->
  <div id="tab-jukebox" class="panel" role="tabpanel">
    <div class="card">
      <div class="label"><span>YouTube Party Jukebox</span><span id="jukeboxSubtitle">Cast to Denon</span></div>
      <div class="search-row">
        <input id="ytQuery" class="search-input" aria-label="Search YouTube" placeholder="Search YouTube Music or paste URL..." onkeydown="if(event.key==='Enter') searchYt()" />
        <button class="btn primary" type="button" onclick="searchYt()">Search</button>
      </div>
      <div id="ytResults"></div>
    </div>

    <div class="card">
      <div class="label"><span>Up Next Queue</span><span id="qCount">0 tracks</span></div>
      <div id="ytQueue"></div>
    </div>
  </div>

  <!-- TAB 3: LIGHTS & PLUGINS -->
  <div id="tab-plugins" class="panel owner-only" role="tabpanel">
    <div id="pluginsContainer">
      <div class="card"><div style="color:var(--muted);font-size:12px">Loading plugins...</div></div>
    </div>
  </div>

  <!-- TAB 4: STATIONS -->
  <div id="tab-stations" class="panel owner-only" role="tabpanel">
    <div class="card">
      <div class="label"><span>Quick Radio Stations</span><span>Preset HEOS Streams</span></div>
      <div id="stationsBox" class="chips"></div>
    </div>
  </div>

  <div class="tabs" role="tablist" aria-label="Remote sections">
    <button id="tabBtn-remote" class="tab active" role="tab" aria-selected="true" type="button" onclick="switchTab('remote', this)">Remote</button>
    <button id="tabBtn-plugins" class="tab owner-only" role="tab" aria-selected="false" type="button" onclick="switchTab('plugins', this)">Lights</button>
    <button id="tabBtn-jukebox" class="tab" role="tab" aria-selected="false" type="button" onclick="switchTab('jukebox', this)">Jukebox</button>
    <button id="tabBtn-stations" class="tab owner-only" role="tab" aria-selected="false" type="button" onclick="switchTab('stations', this)">Stations</button>
  </div>
</div>

<script>
const TOKEN_KEY = 'denon_remote_token_v1';
let state = null;
let dragging = false;
let pendingVol = null;
let acknowledgedVol = null;
let acknowledgedVolAt = 0;
let volCommitTimer = null;
let volInFlight = false;
let lastSuccessTime = null;
let ytSearchTracks = [];
const lastMarkup = { scenes: '', inputs: '', stations: '', plugins: '', queue: '' };

function initTokenFromUrl() {
  try {
    const params = new URLSearchParams(window.location.search);
    const p = params.get('pair') || params.get('token');
    if (p && p.trim()) {
      localStorage.setItem(TOKEN_KEY, p.trim());
      params.delete('pair');
      params.delete('token');
      const clean = window.location.pathname + (params.toString() ? '?' + params.toString() : '');
      window.history.replaceState({}, '', clean);
    }
  } catch (e) {}
}

function getToken() {
  try { return localStorage.getItem(TOKEN_KEY) || ''; } catch (e) { return ''; }
}

function authHeaders(extra) {
  const h = Object.assign({}, extra || {});
  const t = getToken();
  if (t) h['X-Denon-Token'] = t;
  return h;
}

function showPairGate(msg) {
  document.getElementById('disabledGate').classList.remove('show');
  document.getElementById('pairGate').classList.add('show');
  document.getElementById('appShell').classList.add('locked');
  document.getElementById('headerControls').classList.add('header-locked');
  document.getElementById('pairError').textContent = msg || '';
  setTimeout(() => document.getElementById('pairInput')?.focus(), 50);
}

function showDisabledGate(msg) {
  document.getElementById('pairGate').classList.remove('show');
  document.getElementById('disabledGate').classList.add('show');
  document.getElementById('appShell').classList.add('locked');
  document.getElementById('headerControls').classList.add('header-locked');
  document.getElementById('disabledMsg').textContent = msg || 'Phone Remote is currently disabled in Denon Desktop.';
}

function hideGates() {
  document.getElementById('pairGate').classList.remove('show');
  document.getElementById('disabledGate').classList.remove('show');
  document.getElementById('appShell').classList.remove('locked');
  document.getElementById('headerControls').classList.remove('header-locked');
}

function submitPairToken() {
  const val = document.getElementById('pairInput').value.trim();
  if (!val) return;
  try { localStorage.setItem(TOKEN_KEY, val); } catch (e) {}
  document.getElementById('pairInput').value = '';
  refreshStatus();
}

function toast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(window._tt);
  window._tt = setTimeout(() => el.classList.remove('show'), 2200);
}

function switchTab(id, btn) {
  document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.tab').forEach(t => { t.classList.remove('active'); t.setAttribute('aria-selected', 'false'); });
  const target = document.getElementById('tab-' + id);
  if (target) target.classList.add('active');
  if (btn) { btn.classList.add('active'); btn.setAttribute('aria-selected', 'true'); }
}

function fmtDb(v) {
  if (v <= 0) return '--- dB';
  const d = (v - 80).toFixed(1);
  return (Number(d) > 0 ? '+' : '') + d + ' dB';
}

function applyVolumeDisplay(v, maxVol) {
  const slider = document.getElementById('volSlider');
  slider.max = maxVol || 98;
  slider.value = v;
  slider.setAttribute('aria-valuetext', `${v} (${fmtDb(v)})`);
  document.getElementById('volDb').textContent = Number(v).toFixed(1);
  document.getElementById('volAbs').textContent = `${fmtDb(v)}  •  MAX ${maxVol || 98}`;
}

function onSlide(v) {
  dragging = true;
  pendingVol = Number(v);
  applyVolumeDisplay(pendingVol, state?.maxVolume || 98);
}

function commitSlide(v) {
  dragging = false;
  pendingVol = Number(v);
  scheduleVolumeFlush(0);
}

function stepVol(delta) {
  const maxV = state?.maxVolume || 98;
  const freshAck = acknowledgedVol !== null && (Date.now() - acknowledgedVolAt) < 2800;
  const base = pendingVol !== null
    ? pendingVol
    : (freshAck ? acknowledgedVol : (state ? state.volume : Number(document.getElementById('volSlider').value)));
  pendingVol = Math.max(0, Math.min(maxV, base + delta));
  applyVolumeDisplay(pendingVol, maxV);
  scheduleVolumeFlush(120);
}

function scheduleVolumeFlush(delayMs) {
  clearTimeout(volCommitTimer);
  volCommitTimer = setTimeout(flushPendingVolume, delayMs);
}

async function flushPendingVolume() {
  if (volInFlight || pendingVol === null) return;
  const target = pendingVol;
  volInFlight = true;
  try {
    const ok = await post('/api/volume', { level: target });
    if (ok) {
      acknowledgedVol = target;
      acknowledgedVolAt = Date.now();
    }
  } finally {
    volInFlight = false;
    if (pendingVol !== null && pendingVol !== target) {
      scheduleVolumeFlush(60);
    } else {
      pendingVol = null;
    }
  }
}

async function togglePlayPause() {
  const act = state && state.isPlaying ? 'pause' : 'play';
  await post('/api/transport', { action: act });
}

async function post(url, body) {
  try {
    const r = await fetch(url, {
      method: 'POST',
      headers: authHeaders({ 'Content-Type': 'application/json' }),
      body: body ? JSON.stringify(body) : '{}'
    });
    const j = await r.json().catch(() => ({}));
    if (r.status === 401) { showPairGate(j.error || 'Pairing required'); return false; }
    if (r.status === 403 && /disabled/i.test(j.error || '')) { showDisabledGate(j.error); return false; }
    if (!r.ok) { toast(j.error || 'Command failed'); return false; }
    if (j.message) toast(j.message);
    setTimeout(refreshStatus, 250);
    return true;
  } catch (e) { toast('Desktop unreachable'); return false; }
}

async function searchYt() {
  const q = document.getElementById('ytQuery').value.trim();
  if (!q) return;
  const box = document.getElementById('ytResults');
  box.innerHTML = '<div style="color:var(--sub);font-size:12px;padding:6px">Searching YouTube...</div>';
  try {
    const r = await fetch('/api/youtube/search?q=' + encodeURIComponent(q), { headers: authHeaders() });
    const j = await r.json().catch(() => ({}));
    if (r.status === 401) { showPairGate(j.error || 'Pairing required'); return; }
    if (!r.ok) { box.innerHTML = '<div style="color:#fca5a5;font-size:12px">' + esc(j.error || 'Search failed') + '</div>'; return; }
    ytSearchTracks = Array.isArray(j.tracks) ? j.tracks : [];
    if (!ytSearchTracks.length) { box.innerHTML = '<div style="color:var(--muted);font-size:12px">No results found.</div>'; return; }
    const isGuest = state && state.role === 'guest';
    box.innerHTML = '';
    ytSearchTracks.forEach((t, idx) => {
      const row = document.createElement('div');
      row.className = 'track';

      const img = document.createElement('img');
      img.className = 'thumb';
      img.src = t.thumbnailUrl || '';
      img.alt = '';
      row.appendChild(img);

      const meta = document.createElement('div');
      meta.className = 'tmeta';
      const titleEl = document.createElement('div');
      titleEl.className = 'ttitle';
      titleEl.textContent = t.title || '';
      const subEl = document.createElement('div');
      subEl.className = 'tsub';
      subEl.textContent = `${t.author || ''} • ${t.durationDisplay || ''}`;
      meta.appendChild(titleEl);
      meta.appendChild(subEl);
      row.appendChild(meta);

      const acts = document.createElement('div');
      acts.className = 'tacts';
      if (!isGuest) {
        const playBtn = document.createElement('button');
        playBtn.type = 'button';
        playBtn.className = 'btn primary';
        playBtn.setAttribute('aria-label', 'Play ' + (t.title || 'track'));
        playBtn.textContent = '▶';
        playBtn.addEventListener('click', () => post('/api/youtube/play', ytSearchTracks[idx]));
        acts.appendChild(playBtn);
      }
      const qBtn = document.createElement('button');
      qBtn.type = 'button';
      qBtn.className = isGuest ? 'btn primary' : 'btn';
      qBtn.setAttribute('aria-label', 'Queue ' + (t.title || 'track'));
      qBtn.textContent = '+Q';
      qBtn.addEventListener('click', () => post('/api/youtube/queue', ytSearchTracks[idx]));
      acts.appendChild(qBtn);

      row.appendChild(acts);
      box.appendChild(row);
    });
  } catch (e) { box.innerHTML = '<div style="color:#fca5a5;font-size:12px">Search failed</div>'; }
}

function esc(s) {
  return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

function patchContainerPreservingFocus(el, key, html) {
  if (lastMarkup[key] === html) return;
  const activeEl = document.activeElement;
  const focusedKey = activeEl && el.contains(activeEl) ? activeEl.getAttribute('data-focus-key') : null;
  lastMarkup[key] = html;
  el.innerHTML = html;
  if (focusedKey) {
    const restored = el.querySelector(`[data-focus-key="${CSS.escape(focusedKey)}"]`);
    if (restored) restored.focus({ preventScroll: true });
  }
}

function updateBanner(kind, text) {
  const b = document.getElementById('connBanner');
  if (!kind) { b.classList.remove('show', 'warn'); return; }
  document.getElementById('connBannerText').textContent = text;
  b.classList.add('show');
  b.classList.toggle('warn', kind === 'warn');
}

function applyRoleUi(role) {
  const isGuest = role === 'guest';
  document.getElementById('roleBadge').style.display = isGuest ? 'inline-block' : 'none';
  document.getElementById('jukeboxSubtitle').textContent = isGuest ? 'Guest Queue Mode' : 'Cast to Denon';
  document.querySelectorAll('.owner-only').forEach(el => el.classList.toggle('hidden', isGuest));
  if (isGuest && !window._guestSwitchedOnce) {
    window._guestSwitchedOnce = true;
    switchTab('jukebox', document.getElementById('tabBtn-jukebox'));
  }
}

async function refreshStatus() {
  try {
    const r = await fetch('/api/status', { headers: authHeaders() });
    if (r.status === 401) {
      const j = await r.json().catch(() => ({}));
      showPairGate(j.error || 'Pairing required');
      return;
    }
    if (r.status === 403) {
      const j = await r.json().catch(() => ({}));
      showDisabledGate(j.error || 'Phone Remote disabled on Desktop');
      return;
    }
    if (!r.ok) {
      updateBanner('err', `Desktop returned HTTP ${r.status}`);
      return;
    }
    hideGates();
    state = await r.json();
    lastSuccessTime = new Date();
    applyRoleUi(state.role || 'owner');

    if (state.connectionState === 'receiver_unreachable') {
      updateBanner('err', `Receiver unreachable • ${state.lastError || 'Check power/LAN'}`);
    } else if (state.connectionState === 'connecting') {
      updateBanner('warn', 'Connecting to Denon receiver...');
    } else {
      updateBanner(null, '');
    }

    document.getElementById('rxName').textContent = state.receiverName || 'DENON REMOTE';
    const pbtn = document.getElementById('powerBtn');
    pbtn.textContent = state.connectionState === 'receiver_unreachable' ? 'OFFLINE' : (state.isAwake ? 'ACTIVE' : 'STANDBY');
    pbtn.classList.toggle('on', !!state.isAwake);
    document.getElementById('badgeInput').textContent = state.inputDisplay || '—';
    document.getElementById('badgeState').textContent = state.connectionState === 'receiver_unreachable' ? 'Offline' : (state.playState || 'Stopped');
    document.getElementById('badgeMode').textContent = state.soundMode || '—';
    document.getElementById('nowTitle').textContent = state.nowPlayingTitle || 'Ready';
    document.getElementById('nowSub').textContent = state.nowPlayingDetail || '';

    const pp = document.getElementById('playPauseBtn');
    pp.innerHTML = `<span aria-hidden="true">${state.isPlaying ? '⏸' : '▶'}</span>`;
    pp.setAttribute('aria-label', state.isPlaying ? 'Pause' : 'Play');

    const art = document.getElementById('artImg');
    if (state.artworkUrl) { art.src = state.artworkUrl; art.style.display = 'block'; }
    else { art.style.display = 'none'; }

    if (!dragging && pendingVol === null) {
      const freshAck = acknowledgedVol !== null && (Date.now() - acknowledgedVolAt) < 2800;
      if (freshAck && state.volume === acknowledgedVol) {
        acknowledgedVol = null;
      }
      const displayVol = (freshAck && acknowledgedVol !== null) ? acknowledgedVol : (state.volume || 0);
      applyVolumeDisplay(displayVol, state.maxVolume || 98);
    }

    const mbtn = document.getElementById('muteBtn');
    mbtn.textContent = state.isMuted ? 'MUTED' : 'MUTE';
    mbtn.classList.toggle('muted', !!state.isMuted);

    const scenesHtml = (state.scenes || []).map(sc =>
      `<button class="chip" type="button" data-focus-key="scene:${esc(sc.id)}" onclick="post('/api/scene',{id:'${esc(sc.id)}'})"><span class="scene-tag" style="background:${esc(sc.accentHex)}">${esc(sc.badge)}</span>${esc(sc.name)}</button>`
    ).join('');
    patchContainerPreservingFocus(document.getElementById('scenesBox'), 'scenes', scenesHtml);

    const inputsHtml = (state.inputs || []).map(inp =>
      `<button class="chip ${inp.isActive ? 'active' : ''}" type="button" data-focus-key="input:${esc(inp.code)}" onclick="post('/api/input',{code:'${esc(inp.code)}'})">${esc(inp.displayName)}</button>`
    ).join('');
    patchContainerPreservingFocus(document.getElementById('inputsBox'), 'inputs', inputsHtml);

    const stationsHtml = (state.stations || []).map(st =>
      `<button class="chip ${st.isPlaying ? 'active' : ''}" type="button" data-focus-key="station:${esc(st.alias)}" onclick="post('/api/station',{alias:'${esc(st.alias)}'})">▶ ${esc(st.displayName)}</button>`
    ).join('');
    patchContainerPreservingFocus(document.getElementById('stationsBox'), 'stations', stationsHtml);

    const plugins = state.plugins || [];
    const pluginsHtml = plugins.length
      ? plugins.map(p => `
          <div class="card">
            <div class="label">
              <span><span class="scene-tag" style="background:${esc(p.accentHex)};margin-right:6px">${esc(p.iconGlyph)}</span>${esc(p.name)}</span>
              <button class="pill ${p.isEnabled ? 'on' : ''}" type="button" data-focus-key="ptog:${esc(p.id)}" style="padding:2px 8px;font-size:10px" onclick="post('/api/plugins/toggle',{pluginId:'${esc(p.id)}',enabled:${!p.isEnabled}})">${p.isEnabled ? 'ACTIVE' : 'OFF'}</button>
            </div>
            <div style="font-size:12px;color:var(--sub);margin-bottom:10px">${esc(p.statusText || p.category)}</div>
            <div class="chips">
              ${(p.actions || []).map(a =>
                `<button class="chip" type="button" data-focus-key="pact:${esc(p.id)}:${esc(a.actionId)}" onclick="post('/api/plugins/action',{pluginId:'${esc(p.id)}',actionId:'${esc(a.actionId)}'})">${esc(a.displayName)}</button>`
              ).join('')}
            </div>
          </div>`).join('')
      : '<div class="card"><div style="color:var(--muted);font-size:12px">No plugins available.</div></div>';
    patchContainerPreservingFocus(document.getElementById('pluginsContainer'), 'plugins', pluginsHtml);

    const q = state.youtubeQueue || [];
    document.getElementById('qCount').textContent = q.length + (q.length === 1 ? ' track' : ' tracks');
    const queueHtml = q.length
      ? q.map((t, i) => `
          <div class="track ${t.isCurrent ? 'current' : ''}">
            <div style="font-family:monospace;font-weight:800;color:var(--cyan);width:22px">${t.isCurrent ? '▶' : String(i+1).padStart(2,'0')}</div>
            <div class="tmeta">
              <div class="ttitle">${esc(t.title)}</div>
              <div class="tsub">${esc(t.author)} • ${esc(t.durationDisplay)}</div>
            </div>
          </div>`).join('')
      : '<div style="color:var(--muted);font-size:12px">Queue is empty. Search YouTube above to add tracks!</div>';
    patchContainerPreservingFocus(document.getElementById('ytQueue'), 'queue', queueHtml);
  } catch (e) {
    const ago = lastSuccessTime ? ` (last confirmed ${lastSuccessTime.toLocaleTimeString()})` : '';
    updateBanner('err', `Denon Desktop unreachable${ago}`);
  }
}

initTokenFromUrl();
refreshStatus();
setInterval(refreshStatus, 2200);
</script>
</body>
</html>
""";
}

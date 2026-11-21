namespace Denon.App.Core.Services;

public static class RemoteWebUiHtml
{
    public const string PageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no" />
<title>Denon Remote &amp; Jukebox</title>
<style>
  :root {
    --bg: #0a0c10; --card: #13161e; --elev: #1a1f2b; --border: #252c3d;
    --cyan: #00d4ff; --amber: #f59e0b; --green: #10b981; --red: #ef4444;
    --text: #f1f5f9; --sub: #94a3b8; --muted: #64748b;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); padding: 14px 14px 86px; }
  .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
  .brand { display: flex; align-items: center; gap: 8px; font-weight: 800; letter-spacing: 0.04em; font-size: 15px; }
  .logo { width: 26px; height: 26px; border-radius: 7px; background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; display: grid; place-items: center; font-weight: 900; }
  .pill { font-size: 11px; font-weight: 700; padding: 5px 10px; border-radius: 99px; border: 1px solid var(--border); background: var(--card); color: var(--sub); cursor: pointer; }
  .pill.on { background: #0c2d22; border-color: var(--green); color: var(--green); }
  .card { background: var(--card); border: 1px solid var(--border); border-radius: 16px; padding: 14px; margin-bottom: 12px; }
  .hero { background: linear-gradient(135deg, #141925, #0c1018 70%, #0a141d); border-color: #1f3347; }
  .label { font-size: 10.5px; font-weight: 800; color: var(--muted); letter-spacing: 0.08em; text-transform: uppercase; margin-bottom: 10px; display: flex; justify-content: space-between; }
  .now-row { display: flex; gap: 12px; align-items: center; }
  .art { width: 62px; height: 62px; border-radius: 12px; background: #07090d; border: 1px solid #223144; object-fit: cover; flex-shrink: 0; }
  .now-title { font-size: 17px; font-weight: 700; line-height: 1.25; overflow: hidden; text-overflow: ellipsis; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
  .now-sub { font-size: 12px; color: var(--sub); margin-top: 3px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .badges { display: flex; gap: 6px; margin-bottom: 6px; flex-wrap: wrap; }
  .badge { font-size: 10px; font-weight: 700; padding: 2px 7px; border-radius: 5px; background: #191e2a; color: var(--sub); }
  .badge.cyan { background: #0e2430; color: var(--cyan); }
  .badge.amber { color: var(--amber); }
  .transport { display: flex; justify-content: center; gap: 12px; margin-top: 14px; }
  .tbtn { width: 44px; height: 44px; border-radius: 50%; border: 1px solid #2a3142; background: var(--elev); color: var(--text); font-size: 15px; cursor: pointer; display: grid; place-items: center; }
  .tbtn.play { width: 52px; height: 52px; background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; border-color: #7dd3fc; font-weight: 900; font-size: 18px; }
  .oled { background: #07090d; border: 1px solid #1c2738; border-radius: 12px; padding: 10px; text-align: center; margin-bottom: 12px; }
  .oled-big { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 30px; font-weight: 800; color: var(--cyan); }
  .oled-sub { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 11px; color: var(--muted); }
  input[type=range] { width: 100%; accent-color: var(--cyan); height: 28px; }
  .vol-row { display: grid; grid-template-columns: 1fr 1fr 1.4fr 1fr 1fr; gap: 6px; margin-top: 8px; }
  .btn { background: var(--elev); color: var(--text); border: 1px solid var(--border); border-radius: 10px; padding: 9px 10px; font-size: 12.5px; font-weight: 600; cursor: pointer; }
  .btn:active { opacity: 0.75; }
  .btn.primary { background: linear-gradient(135deg, #0284c7, #00d4ff); color: #04151c; border-color: #38bdf8; font-weight: 800; }
  .btn.muted { background: #4c151b; border-color: var(--red); color: #fca5a5; }
  .chips { display: flex; flex-wrap: wrap; gap: 7px; }
  .chip { background: #181c26; border: 1px solid var(--border); color: var(--sub); border-radius: 9px; padding: 8px 11px; font-size: 12px; font-weight: 600; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; }
  .chip.active { background: #0c2e3d; border-color: var(--cyan); color: var(--cyan); }
  .scene-tag { font-size: 9.5px; font-weight: 900; padding: 2px 5px; border-radius: 4px; color: #06121a; }
  .search-row { display: flex; gap: 7px; margin-bottom: 10px; }
  .search-input { flex: 1; background: #0c0f15; border: 1px solid var(--border); border-radius: 10px; color: var(--text); padding: 10px 12px; font-size: 14px; outline: none; }
  .search-input:focus { border-color: var(--cyan); }
  .track { display: flex; gap: 10px; align-items: center; background: #161a24; border: 1px solid var(--border); border-radius: 11px; padding: 8px; margin-bottom: 8px; }
  .track.current { background: #0e2430; border-color: var(--cyan); }
  .thumb { width: 54px; height: 36px; border-radius: 6px; object-fit: cover; background: #07090d; flex-shrink: 0; }
  .tmeta { flex: 1; min-width: 0; }
  .ttitle { font-size: 12.5px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tsub { font-size: 11px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tacts { display: flex; gap: 5px; }
  .tacts .btn { padding: 6px 9px; font-size: 11.5px; }
  .tabs { position: fixed; bottom: 0; left: 0; right: 0; background: #0e1016; border-top: 1px solid var(--border); display: grid; grid-template-columns: repeat(4, 1fr); padding: 8px 8px calc(8px + env(safe-area-inset-bottom)); gap: 6px; }
  .tab { background: transparent; border: none; color: var(--muted); font-size: 11.5px; font-weight: 700; padding: 8px 4px; border-radius: 10px; cursor: pointer; }
  .tab.active { background: #142330; color: var(--cyan); }
  .panel { display: none; }
  .panel.active { display: block; }
  .toast { position: fixed; top: 12px; left: 50%; transform: translateX(-50%); background: #0e2430; border: 1px solid var(--cyan); color: var(--text); padding: 8px 14px; border-radius: 99px; font-size: 12px; font-weight: 600; opacity: 0; pointer-events: none; transition: opacity 0.2s; z-index: 99; }
  .toast.show { opacity: 1; }
</style>
</head>
<body>
<div id="toast" class="toast"></div>

<div class="header">
  <div class="brand"><div class="logo">D</div><span id="rxName">DENON REMOTE</span></div>
  <button id="powerBtn" class="pill" onclick="post('/api/power')">STANDBY</button>
</div>

<!-- TAB 1: REMOTE -->
<div id="tab-remote" class="panel active">
  <div class="card hero">
    <div class="now-row">
      <img id="artImg" class="art" alt="" style="display:none" />
      <div style="flex:1; min-width:0">
        <div class="badges">
          <span id="badgeInput" class="badge cyan">HEOS</span>
          <span id="badgeState" class="badge">Stopped</span>
          <span id="badgeMode" class="badge amber">—</span>
        </div>
        <div id="nowTitle" class="now-title">Connecting...</div>
        <div id="nowSub" class="now-sub">Syncing with Denon Desktop</div>
      </div>
    </div>
    <div class="transport">
      <button class="tbtn" onclick="post('/api/transport', {action:'prev'})">⏮</button>
      <button id="playPauseBtn" class="tbtn play" onclick="togglePlayPause()">▶</button>
      <button class="tbtn" onclick="post('/api/transport', {action:'stop'})">⏹</button>
      <button class="tbtn" onclick="post('/api/transport', {action:'next'})">⏭</button>
    </div>
  </div>

  <div class="card">
    <div class="label"><span>Master Volume</span><span id="volRangeLabel">0–98</span></div>
    <div class="oled">
      <div id="volDb" class="oled-big">--- dB</div>
      <div id="volAbs" class="oled-sub">VOL 0</div>
    </div>
    <input id="volSlider" type="range" min="0" max="98" value="20" oninput="onSlide(this.value)" onchange="commitSlide(this.value)" />
    <div class="vol-row">
      <button class="btn" onclick="stepVol(-5)">-5</button>
      <button class="btn" onclick="stepVol(-1)">-1</button>
      <button id="muteBtn" class="btn" onclick="post('/api/mute')">MUTE</button>
      <button class="btn" onclick="stepVol(1)">+1</button>
      <button class="btn" onclick="stepVol(5)">+5</button>
    </div>
  </div>

  <div class="card">
    <div class="label"><span>Smart Scenes</span><span>One-Tap Macros</span></div>
    <div id="scenesBox" class="chips"></div>
  </div>

  <div class="card">
    <div class="label"><span>Input Source</span></div>
    <div id="inputsBox" class="chips"></div>
  </div>
</div>

<!-- TAB 2: PARTY JUKEBOX (YOUTUBE) -->
<div id="tab-jukebox" class="panel">
  <div class="card">
    <div class="label"><span>YouTube Party Jukebox</span><span>Cast to Denon</span></div>
    <div class="search-row">
      <input id="ytQuery" class="search-input" placeholder="Search YouTube Music or paste URL..." onkeydown="if(event.key==='Enter') searchYt()" />
      <button class="btn primary" onclick="searchYt()">Search</button>
    </div>
    <div id="ytResults"></div>
  </div>

  <div class="card">
    <div class="label"><span>Up Next Queue</span><span id="qCount">0 tracks</span></div>
    <div id="ytQueue"></div>
  </div>
</div>

<!-- TAB 3: LIGHTS & PLUGINS -->
<div id="tab-plugins" class="panel">
  <div id="pluginsContainer">
    <div class="card"><div style="color:#64748b;font-size:12px">Loading plugins...</div></div>
  </div>
</div>

<!-- TAB 4: STATIONS -->
<div id="tab-stations" class="panel">
  <div class="card">
    <div class="label"><span>Quick Radio Stations</span><span>Preset HEOS Streams</span></div>
    <div id="stationsBox" class="chips"></div>
  </div>
</div>

<div class="tabs">
  <button class="tab active" onclick="switchTab('remote', this)">Remote</button>
  <button class="tab" onclick="switchTab('plugins', this)">Lights</button>
  <button class="tab" onclick="switchTab('jukebox', this)">Jukebox</button>
  <button class="tab" onclick="switchTab('stations', this)">Stations</button>
</div>

<script>
let state = null;
let dragging = false;

function toast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(window._tt);
  window._tt = setTimeout(() => el.classList.remove('show'), 2200);
}

function switchTab(id, btn) {
  document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
  document.getElementById('tab-' + id).classList.add('active');
  btn.classList.add('active');
}

function fmtDb(v) {
  if (v <= 0) return '--- dB';
  const d = (v - 80).toFixed(1);
  return (d > 0 ? '+' : '') + d + ' dB';
}

function onSlide(v) {
  dragging = true;
  document.getElementById('volDb').textContent = fmtDb(Number(v));
  document.getElementById('volAbs').textContent = 'VOL ' + v;
}

async function commitSlide(v) {
  dragging = false;
  await post('/api/volume', { level: Number(v) });
}

async function stepVol(delta) {
  const cur = state ? state.volume : Number(document.getElementById('volSlider').value);
  const next = Math.max(0, Math.min(state?.maxVolume || 98, cur + delta));
  await post('/api/volume', { level: next });
}

async function togglePlayPause() {
  const act = state && state.isPlaying ? 'pause' : 'play';
  await post('/api/transport', { action: act });
}

async function post(url, body) {
  try {
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : '{}'
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { toast(j.error || 'Command failed'); return; }
    if (j.message) toast(j.message);
    setTimeout(refreshStatus, 250);
  } catch (e) { toast('Connection error'); }
}

async function searchYt() {
  const q = document.getElementById('ytQuery').value.trim();
  if (!q) return;
  const box = document.getElementById('ytResults');
  box.innerHTML = '<div style="color:#94a3b8;font-size:12px;padding:6px">Searching YouTube...</div>';
  try {
    const r = await fetch('/api/youtube/search?q=' + encodeURIComponent(q));
    const j = await r.json();
    if (!r.ok) { box.innerHTML = '<div style="color:#ef4444;font-size:12px">' + esc(j.error || 'Search failed') + '</div>'; return; }
    if (!j.tracks || !j.tracks.length) { box.innerHTML = '<div style="color:#64748b;font-size:12px">No results found.</div>'; return; }
    box.innerHTML = j.tracks.map(t => `
      <div class="track">
        <img class="thumb" src="${esc(t.thumbnailUrl)}" alt="" />
        <div class="tmeta">
          <div class="ttitle">${esc(t.title)}</div>
          <div class="tsub">${esc(t.author)} • ${esc(t.durationDisplay)}</div>
        </div>
        <div class="tacts">
          <button class="btn primary" onclick='playYt(${JSON.stringify(t)})'>▶</button>
          <button class="btn" onclick='queueYt(${JSON.stringify(t)})'>+Q</button>
        </div>
      </div>`).join('');
  } catch (e) { box.innerHTML = '<div style="color:#ef4444;font-size:12px">Search failed</div>'; }
}

function playYt(track) { post('/api/youtube/play', track); }
function queueYt(track) { post('/api/youtube/queue', track); }

function esc(s) {
  return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

async function refreshStatus() {
  try {
    const r = await fetch('/api/status');
    if (!r.ok) return;
    state = await r.json();
    document.getElementById('rxName').textContent = state.receiverName || 'DENON REMOTE';
    const pbtn = document.getElementById('powerBtn');
    pbtn.textContent = state.isAwake ? 'ACTIVE' : 'STANDBY';
    pbtn.classList.toggle('on', !!state.isAwake);
    document.getElementById('badgeInput').textContent = state.inputDisplay || '—';
    document.getElementById('badgeState').textContent = state.playState || 'Stopped';
    document.getElementById('badgeMode').textContent = state.soundMode || '—';
    document.getElementById('nowTitle').textContent = state.nowPlayingTitle || 'Ready';
    document.getElementById('nowSub').textContent = state.nowPlayingDetail || '';
    document.getElementById('playPauseBtn').textContent = state.isPlaying ? '⏸' : '▶';

    const art = document.getElementById('artImg');
    if (state.artworkUrl) { art.src = state.artworkUrl; art.style.display = 'block'; }
    else { art.style.display = 'none'; }

    if (!dragging) {
      const slider = document.getElementById('volSlider');
      slider.max = state.maxVolume || 98;
      slider.value = state.volume || 0;
      document.getElementById('volDb').textContent = fmtDb(state.volume || 0);
      document.getElementById('volAbs').textContent = `VOL ${state.volume || 0} / ${state.maxVolume || 98}`;
    }

    const mbtn = document.getElementById('muteBtn');
    mbtn.textContent = state.isMuted ? 'MUTED' : 'MUTE';
    mbtn.classList.toggle('muted', !!state.isMuted);

    document.getElementById('scenesBox').innerHTML = (state.scenes || []).map(sc =>
      `<button class="chip" onclick="post('/api/scene',{id:'${esc(sc.id)}'})"><span class="scene-tag" style="background:${esc(sc.accentHex)}">${esc(sc.badge)}</span>${esc(sc.name)}</button>`
    ).join('');

    document.getElementById('inputsBox').innerHTML = (state.inputs || []).map(inp =>
      `<button class="chip ${inp.isActive ? 'active' : ''}" onclick="post('/api/input',{code:'${esc(inp.code)}'})">${esc(inp.displayName)}</button>`
    ).join('');

    document.getElementById('stationsBox').innerHTML = (state.stations || []).map(st =>
      `<button class="chip ${st.isPlaying ? 'active' : ''}" onclick="post('/api/station',{alias:'${esc(st.alias)}'})">▶ ${esc(st.displayName)}</button>`
    ).join('');

    const plugins = state.plugins || [];
    document.getElementById('pluginsContainer').innerHTML = plugins.length
      ? plugins.map(p => `
          <div class="card">
            <div class="label">
              <span><span class="scene-tag" style="background:${esc(p.accentHex)};margin-right:6px">${esc(p.iconGlyph)}</span>${esc(p.name)}</span>
              <button class="pill ${p.isEnabled ? 'on' : ''}" style="padding:2px 8px;font-size:10px" onclick="post('/api/plugins/toggle',{pluginId:'${esc(p.id)}',enabled:${!p.isEnabled}})">${p.isEnabled ? 'ACTIVE' : 'OFF'}</button>
            </div>
            <div style="font-size:11.5px;color:#94a3b8;margin-bottom:10px">${esc(p.statusText || p.category)}</div>
            <div class="chips">
              ${(p.actions || []).map(a =>
                `<button class="chip" onclick="post('/api/plugins/action',{pluginId:'${esc(p.id)}',actionId:'${esc(a.actionId)}'})">${esc(a.displayName)}</button>`
              ).join('')}
            </div>
          </div>`).join('')
      : '<div class="card"><div style="color:#64748b;font-size:12px">No plugins installed.</div></div>';

    const q = state.youtubeQueue || [];
    document.getElementById('qCount').textContent = q.length + (q.length === 1 ? ' track' : ' tracks');
    document.getElementById('ytQueue').innerHTML = q.length
      ? q.map((t, i) => `
          <div class="track ${t.isCurrent ? 'current' : ''}">
            <div style="font-family:monospace;font-weight:800;color:var(--cyan);width:22px">${t.isCurrent ? '▶' : String(i+1).padStart(2,'0')}</div>
            <div class="tmeta">
              <div class="ttitle">${esc(t.title)}</div>
              <div class="tsub">${esc(t.author)} • ${esc(t.durationDisplay)}</div>
            </div>
          </div>`).join('')
      : '<div style="color:#64748b;font-size:12px">Queue is empty. Search YouTube above to add tracks!</div>';
  } catch (e) {}
}

refreshStatus();
setInterval(refreshStatus, 2200);
</script>
</body>
</html>
""";
}

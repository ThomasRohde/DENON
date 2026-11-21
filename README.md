# Denon Desktop — Precision Control

Flagship WPF desktop controller & system-tray companion for HEOS-enabled Denon / Marantz AV receivers.

## Architecture

- `Denon.App.Core`: Persistent, line-framed HEOS CLI (`:1255`) and Denon AVR Telnet (`:23`) clients, SSDP LAN auto-discovery, HTTP/Icecast stream health prober, embedded zero-admin LAN HTTP audio relay (`LocalAudioRelayServer` with FFmpeg 320 kbps MP3 transcoding & Direct M4A fallback), YouTube / YouTube Music resolver & queue engine (`YoutubeExplode`), Smart Scenes macro engine, Audyssey/Acoustic Telnet controls, atomic settings persistence, state store, and smart-backoff polling service.
- `Denon.App.Desktop`: Custom-chromed obsidian Hi-Fi WPF console, floating WebView2 YouTube Cinema Companion window with A/V sync controls, floating taskbar mini-flyout, dark-themed tray menu with Quick Scenes, single-instance guard, and Windows startup integration.

## Highlights

- **YouTube & YouTube Music Deck + LAN Stream Bridge**: Search YouTube Music, Videos, or Playlists (or paste any YouTube / YouTube Music URL or playlist link), cast directly to your Denon AVR over LAN via the built-in zero-admin HTTP audio relay (`FFmpeg 320 kbps MP3` or `Direct M4A` proxy), manage an Up-Next queue with auto-advance, shuffle, repeat, and recent history, and launch the floating **Cinema Companion** window (muted video on PC with `-500ms / -100ms / +100ms / +500ms` A/V sync nudges + Pin-on-Top PiP while Denon plays the audio).
- **Smart Scenes & Live Acoustic Tuning**: One-click customizable AVR macros (`Movie Night`, `Late Night`, `Pure Hi-Fi`, `Morning Jazz`, `Party Multi-Ch`, plus Capture Live AVR & Scene Editor) with smooth volume ramping, plus live Telnet `:23` controls for **Audyssey MultEQ**, **Dynamic EQ**, **Dynamic Volume**, **Audio Restorer**, **Front Panel Dimmer**, **Subwoofer Level Trim (-12..+12 dB)**, **Dialog Enhancer (-12..+12 dB)**, and **Tone Control (Bass/Treble)**.
- **Custom Hi-Fi Control Deck**: Frameless integrated Windows 11 chrome, OLED Now Playing readout with live album/station/YouTube artwork and animated 5-bar equalizer, full transport controls (`Prev`, `Play/Pause`, `Stop`, `Next`), and a Quick Scenes + Stations shelf.
- **Precision Volume Engine**: Clickable dB (`-80.0 dB` to `+18.0 dB`) / Absolute (`0–98`) OLED display, live debounced volume slider with drag-lock (never fights polling), mouse-wheel support, ±1 / ±5 steps, Mute, and one-tap volume presets.
- **Full AVR Hardware Control**: Power / Standby toggle, Input Source switcher (`HEOS`, `TV`, `Blu-ray`, `Game`, `Media Player`, `SAT/CBL`, `BT`, `CD`, `AUX1`, `Phono`, `Tuner`), Sound Mode selector (`Auto`, `Stereo`, `Direct`, `Pure Direct`, `Movie`, `Music`, `Game`, `Multi-Ch Stereo`), Sleep Timer (`Off / 30m / 60m / 90m / 120m`), ECO Mode, and Zone 2 toggle.
- **Stations, Stream Prober & HEOS Favorites**: Quick-play station cards with live `ON AIR` indicator, inline station manager with reorder & search, HTTP/Icecast stream health tester (status, codec, bitrate), Direct Stream launcher, and receiver HEOS Favorites browser.
- **Auto-Discovery & Diagnostics**: One-click SSDP + HEOS LAN receiver scanner, live filtered telemetry logs, and a built-in **Raw AVR / HEOS Command Console**.
- **System Tray Turned to 11**: Single-click floating taskbar mini-controller flyout, dark-themed right-click context menu with Quick Scenes, single-instance activation, and optional launch at Windows startup.
- **Keyboard Shortcuts**: `Space` (Play/Pause), `M` (Mute), `Ctrl+↑/↓` (Volume ±2), `F5` / `Ctrl+R` (Refresh), `Ctrl+1..6` (Switch Views).

## Build & Run

```powershell
dotnet build .\DenonDesktop.slnx
dotnet run --project .\Denon.App.Desktop\Denon.App.Desktop.csproj -- --show
```

## Publish Release Executable

```powershell
dotnet publish .\Denon.App.Desktop\Denon.App.Desktop.csproj -c Release -r win-x64 --self-contained false
```

## Persistence

- Settings: `%LocalAppData%\DenonDesktop\settings.json` (JSON import/export supported in Settings)
- Logs: `%LocalAppData%\DenonDesktop\logs\app.log`

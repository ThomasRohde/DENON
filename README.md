# Denon Desktop — Precision Control

Flagship WPF desktop controller & system-tray companion for HEOS-enabled Denon / Marantz AV receivers.

## Architecture

- `Denon.App.Core`: Persistent, line-framed HEOS CLI (`:1255`) and Denon AVR Telnet (`:23`) clients, SSDP LAN auto-discovery, HTTP/Icecast stream health prober, embedded zero-admin LAN HTTP audio relay (`LocalAudioRelayServer` with FFmpeg 320 kbps MP3 transcoding & Direct M4A fallback), YouTube / YouTube Music resolver & queue engine (`YoutubeExplode`), Smart Scenes macro engine, Audyssey/Acoustic Telnet controls, atomic settings persistence, state store, and smart-backoff polling service.
- `Denon.App.Desktop`: Custom-chromed obsidian Hi-Fi WPF console, floating WebView2 YouTube Cinema Companion window with A/V sync controls, floating taskbar mini-flyout, dark-themed tray menu with Quick Scenes, single-instance guard, and Windows startup integration.

## Highlights

- **Couch Phone Remote & Party Jukebox (QR Code Web UI)**: Scan the in-app QR code from any phone on your Wi-Fi to control Power, Volume, Mute, Inputs, Smart Scenes, Radio Stations, and search/queue YouTube songs from a mobile-first Obsidian web app served directly on port `18925` (zero app install).
- **Cast Any PC Audio to Denon (WASAPI Loopback)**: Stream live Windows audio from your default output or any specific playback device wirelessly to your Denon AVR over LAN in **MP3 320 kbps** or **Lossless WAV**, with silence keepalive so HEOS never disconnects and real-time L/R audio metering.
- **YouTube & YouTube Music Deck + Cinema Companion**: Search YouTube Music, Videos, or Playlists (or paste any YouTube / YouTube Music URL or playlist link), cast via the built-in LAN HTTP audio relay (`FFmpeg 320 kbps MP3` or `Direct M4A`), manage an Up-Next queue with auto-advance, shuffle, repeat, and launch the floating **WebView2 Cinema Companion** with A/V sync nudges & Pin-on-Top PiP.
- **40,000+ Global Radio Explorer & Local Hi-Res FLAC Jukebox**: Search the global Radio Browser directory by name, genre, country, or **FLAC / 256k+ Hi-Res** with 1-click play & preset save, and drag-and-drop local `.flac`, `.wav`, `.mp3`, `.m4a`, `.ogg`, or `.opus` files/folders for bit-perfect LAN streaming.
- **Zero-Lag Real-Time Push Events & Retro Analog VU Meters**: Dedicated HEOS `register_for_change_events` push listener syncs physical knob turns, mutes, and play-state changes in `<50ms` (`LIVE PUSH` badge), paired with toggleable dual-channel illuminated **Analog Hi-Fi VU Meters** right on the Hero OLED deck.
- **Smart Scenes & Live Acoustic Tuning**: One-click customizable AVR macros (`Movie Night`, `Late Night`, `Pure Hi-Fi`, `Morning Jazz`, `Party Multi-Ch`, plus Capture Live AVR & Scene Editor) with smooth volume ramping, plus live Telnet `:23` controls for **Audyssey MultEQ**, **Dynamic EQ**, **Dynamic Volume**, **Audio Restorer**, **Front Panel Dimmer**, **Subwoofer Level Trim (-12..+12 dB)**, **Dialog Enhancer (-12..+12 dB)**, and **Tone Control (Bass/Treble)**.
- **Custom Hi-Fi Control Deck & System Tray**: Frameless integrated Windows 11 chrome, precision dB / 0–98 OLED volume engine, floating taskbar mini-flyout, dark-themed tray menu with Quick Scenes, SSDP auto-discovery, 5-point Health Check, Raw AVR/HEOS Console, and `Ctrl+1..7` shortcuts.

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

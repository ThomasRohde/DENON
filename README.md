# Denon Desktop — Precision Hi-Fi Controller & LAN Audio Hub

<p align="center">
  <strong>Flagship Windows 10/11 desktop controller, YouTube/PC audio stream bridge, Smart Scenes engine, and Couch Phone Remote for HEOS-enabled Denon &amp; Marantz AV Receivers.</strong>
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" />
  <img alt="WPF Windows" src="https://img.shields.io/badge/Windows-10%20%2F%2011%20WPF-0078D6?style=flat-square&logo=windows" />
  <img alt="Protocols" src="https://img.shields.io/badge/Protocols-HEOS%20%3A1255%20%2B%20AVR%20Telnet%20%3A23-00D4FF?style=flat-square" />
  <img alt="License" src="https://img.shields.io/badge/License-MIT-10B981?style=flat-square" />
</p>

---

## Screenshots

### Control Deck & Retro Analog Hi-Fi VU Meters
Real-time zero-lag HEOS push sync (`<50 ms`), auto-detected receiver model badge, dual illuminated L/R Analog VU Meters, clickable dB / 0–98 OLED master volume, and one-click Scenes & Stations.

![Control Deck](docs/screenshots/01-control-deck.png)

### YouTube & YouTube Music Deck + LAN Stream Bridge
Search YouTube Music, Videos, or Playlists (or paste any YouTube / YouTube Music URL), stream directly to your Denon over LAN via the built-in zero-admin 320 kbps MP3 / M4A relay, manage an auto-advancing Up-Next queue, and launch the floating **WebView2 Cinema Companion** with A/V sync nudges.

![YouTube & Music](docs/screenshots/02-youtube-music.png)

### Smart Scenes & Live Acoustic Tuning Console
One-click AVR macros (`Movie Night`, `Late Night`, `Pure Hi-Fi`, `Morning Jazz`, `Party Multi-Ch`, plus **Capture Live AVR** & inline Scene Editor) with smooth volume ramping, paired with live Telnet `:23` controls for Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, Front Panel Dimmer, Subwoofer/Dialog trims, and Tone Control.

![Scenes & Acoustics](docs/screenshots/03-scenes-acoustics.png)

### Couch Phone Remote (QR Code), Cast PC Audio & Local Hi-Res FLAC Jukebox
Scan the QR code from any phone on your Wi-Fi for a mobile Remote & Party Jukebox (zero app install), cast live Windows system audio via WASAPI loopback (`MP3 320 kbps` or `Lossless WAV`), or drag-and-drop `.flac` / `.wav` / `.mp3` folders for bit-perfect LAN streaming.

![Cast & Phone Remote](docs/screenshots/04-cast-phone-remote.png)

### Global Radio Explorer (40,000+ Live Stations)
Browse and filter 40,000+ worldwide internet radio stations by name, genre, country, or **FLAC / 256k+ Hi-Res** via the open Radio Browser directory, play in one click, and save directly to your presets.

![Global Radio Explorer](docs/screenshots/05-global-radio-explorer.png)

---

## Key Features

- **Zero-Config Auto-Discovery & Model Detection**: Automatically discovers Denon & Marantz HEOS receivers on your local network via SSDP + HEOS `:1255`, detects the exact hardware model (`AVR-X2400H`, `AVR-X3800H`, `CINEMA 50`, etc.) and firmware version, and dynamically updates the UI branding (`DENON` / `MARANTZ`) and title bar.
- **Zero-Lag Real-Time Push Events (`LIVE PUSH`)**: Subscribes to `heos://system/register_for_change_events?enable=on` on a dedicated listener socket so physical volume knob turns, remote mutes, and playback changes update the OLED dial in `<50 ms`.
- **Retro Dual-Channel Analog VU Meters**: Illuminated obsidian/amber/cyan stereo VU meters right on the Hero OLED deck — driven by real live WASAPI RMS levels during PC Audio Cast and smooth volume-scaled ballistics during HEOS playback.
- **YouTube & YouTube Music Cast + Cinema Companion**: Search music/videos/playlists or paste any URL, transcode on the fly to 320 kbps MP3 (via `ffmpeg`, auto-detected) or proxy M4A directly over LAN to HEOS, auto-advance queues, and optionally watch muted video in a floating **Pin-on-Top** WebView2 Cinema window with `±100ms` / `±500ms` A/V sync nudges.
- **Couch Phone Remote & Party Jukebox**: Mobile-friendly web app served on port `18925` with an in-app QR code — control Power, Volume, Mute, Inputs, Scenes, Radio Stations, and shared YouTube Music queue from any phone on your Wi-Fi.
- **Cast Any PC Audio (WASAPI Loopback)**: Wirelessly stream Windows audio from your default output or any specific playback device to your Denon AVR in **MP3 320 kbps** or **Lossless WAV** with silence keepalive so HEOS never drops between tracks.
- **Drag-and-Drop Local Hi-Res FLAC Jukebox**: Drop `.flac`, `.wav`, `.mp3`, `.m4a`, `.aac`, `.ogg`, or `.opus` files/folders directly onto the app for bit-perfect HTTP Range streaming over LAN.
- **Smart Scenes & Deep Audyssey / Acoustic Tuning**: Customize multi-step hardware macros with smooth volume ramping, snapshot your current AVR state in 1 click, and tune Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, Front Panel Dimmer (`Bright`/`Dim`/`Dark`/`Off`), Subwoofer & Dialog trims (`-12..+12 dB`), and Bass/Treble.
- **System Tray Companion & Shortcuts**: Single-click floating taskbar mini-flyout, dark-themed tray menu with Quick Scenes & Stations, single-instance guard, optional launch at Windows startup, and keyboard shortcuts (`Space`, `M`, `Ctrl+↑/↓`, `F5`, `Ctrl+1..7`).

---

## Denon & Marantz Model Compatibility

Denon Desktop combines the **HEOS CLI Protocol (TCP `:1255`)** for streaming, metadata, real-time push events, and discovery with the **Denon/Marantz AVR Control Protocol (Telnet TCP `:23`)** for power, exact dB volume, inputs, sound modes, Audyssey, dimmer, and trims.

| Hardware Family | Representative Models | HEOS Streaming, YouTube, PC Cast & Push (`:1255`) | Full AVR Control, Scenes & Acoustics (`:23`) | Notes |
| :--- | :--- | :---: | :---: | :--- |
| **Denon X-Series AVRs (HEOS Built-In, 2017–Present)** | `AVR-X1400H`–`X1800H`, `X2400H`–`X2800H`, `X3400H`–`X3800H`, `X4400H`–`X4800H`, `X6400H`–`X6800H`, `X8500H`, `AVC-A1H`, `A10H` | **Full** | **Full** | Verified live on **`Denon AVR-X2400H`**; all model names & firmware auto-detected |
| **Denon S-Series AVRs (HEOS Built-In)** | `AVR-S650H`–`S670H`, `S750H`–`S770H`, `S950H`–`S970H` | **Full** | **Full** | Zone 2 / specific Audyssey tiers depend on AVR hardware |
| **Denon Stereo & Network Receivers (HEOS)** | `DRA-800H`, `DRA-900H`, `PMA-900HNE`, `CEOL N10`–`N12` | **Full** | **Partial** | Power, Volume, Mute & Inputs work; surround/Audyssey controls skipped gracefully |
| **Marantz HEOS AVRs & Cinema Series** | `NR1509`–`NR1711`, `SR5012`–`SR8015`, `CINEMA 70s / 60 / 50 / 40 / 30`, `AV 10`, `MODEL 40n` | **Full** | **Full** | UI automatically switches branding badge to **`MARANTZ`** upon detection |
| **Denon Home / HEOS Speakers & Soundbars** | `Denon Home 150 / 250 / 350 / 550`, `HEOS 1 / 3 / 5 / 7 / Amp / Link` | **Full** | **N/A** | HEOS streaming, YouTube/PC Cast, Queue & Phone Remote work; no AVR `:23` port |
| **Legacy Non-HEOS Denon/Marantz AVRs (2012–2016)** | `AVR-X1000`–`X4200W`, `SR5008`–`SR7010` | **Not Supported** | **Partial** | Requires HEOS Built-In for discovery, streaming & push events |

> **Recommended Receiver Setting:** On your Denon/Marantz receiver, enable **`Setup → Network → Network Control → Always On`** so Denon Desktop can wake your receiver from Standby and discover it reliably at any time.
>
> **Note on AVR Telnet (`:23`):** Denon/Marantz receivers accept only one concurrent Telnet `:23` connection at a time. Close other Telnet-based remote apps if AVR commands time out, or click **Reset Links** in Health & Support.

---

## Getting Started

### Prerequisites
- **Windows 10 or Windows 11 (x64)**
- **Optional (Recommended for YouTube MP3 320k & PC Audio Cast):** [`ffmpeg`](https://ffmpeg.org/) installed on `PATH` (e.g. `winget install Gyan.FFmpeg`). Denon Desktop auto-detects WinGet and `PATH` installs, and falls back to Direct M4A / Lossless WAV if FFmpeg is not present.
- **Optional (Cinema Companion Video Window):** Microsoft Edge WebView2 Runtime (pre-installed on Windows 11 and most Windows 10 systems).

### Download Prebuilt Executable (GitHub Releases)
Download the latest self-contained single-file **`Denon.App.Desktop-win-x64.exe`** from the [**Releases**](../../releases/latest) page (no .NET runtime installation required):
1. Double-click `Denon.App.Desktop-win-x64.exe` — on first launch, Denon Desktop automatically scans your LAN and connects to your Denon/Marantz receiver.
2. To use **YouTube Cast**, **PC Audio Cast**, **Local FLAC Jukebox**, or the **Couch Phone Remote**, click **Allow Firewall** in the **YouTube & Music** or **Cast & Remote** tab once to open Private-LAN TCP port `18925`.

### Build From Source (.NET 10 SDK)

```powershell
# Build entire solution
dotnet build .\DenonDesktop.slnx

# Run from source (opens window immediately)
dotnet run --project .\Denon.App.Desktop\Denon.App.Desktop.csproj -- --show

# Publish self-contained single-file Windows x64 executable
dotnet publish .\Denon.App.Desktop\Denon.App.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\Denon.App.Desktop\AppRelease
```

---

## Architecture

| Project | Responsibility |
| :--- | :--- |
| **`Denon.App.Core`** | Persistent HEOS CLI (`:1255`) & AVR Telnet (`:23`) clients, real-time HEOS push listener, SSDP LAN auto-discovery, unified zero-admin LAN HTTP server (`LocalAudioRelayServer` serving YouTube MP3/M4A, Local FLAC/WAV Range files, Live PC Audio, and Phone Remote Web UI + JSON API), `YoutubeExplode` search/queue engine, Radio Browser 40k client, Smart Scenes macro engine, HTTP/Icecast stream prober, and atomic JSON settings store. |
| **`Denon.App.Desktop`** | Custom-chromed Obsidian Hi-Fi WPF UI, dual-channel `AnalogVuMeterControl`, `NAudio` WASAPI loopback PC audio caster, `QRCoder` Phone Remote QR generator, `WebView2` Cinema Companion window, floating taskbar mini-flyout, dark-themed tray context menu, single-instance guard, and Windows startup integration. |

### Local Data & Persistence
- **Settings:** `%LocalAppData%\DenonDesktop\settings.json` (JSON export/import supported in Settings)
- **Logs:** `%LocalAppData%\DenonDesktop\logs\app.log`
- **WebView2 Cache:** `%LocalAppData%\DenonDesktop\webview2\`

### Keyboard Shortcuts (Customizable in Settings)
All global Windows hotkeys and in-app shortcuts can be recorded, cleared, and checked for internal & OS collisions in **Settings → Shortcut Editor**:

| Default Shortcut | Scope | Action |
| :--- | :---: | :--- |
| `Ctrl + Alt + D` | Global | Open / Toggle Main Window (even from tray) |
| `Ctrl + Alt + P` | Global | Play / Pause Playback |
| `Ctrl + Alt + M` | Global | Toggle Receiver Mute |
| `Ctrl + Alt + ↑` / `↓` | Global | Adjust Master Volume ±2 steps |
| `Ctrl + Alt + →` / `←` | Global | Next / Previous Track |
| `Space` | In-App | Play / Pause Playback |
| `M` | In-App | Toggle Receiver Mute |
| `Ctrl + ↑` / `Ctrl + ↓` | In-App | Adjust Master Volume ±2 steps |
| `F5` / `Ctrl + R` | In-App | Force Refresh Receiver Telemetry |
| `Ctrl + 1..7` | In-App | Switch Navigation Views |
| **Mouse Wheel** (over Volume card) | In-App | Adjust Master Volume ±2 steps |

---

## Changelog & License

- See [CHANGELOG.md](CHANGELOG.md) for release history.
- MIT License — see [LICENSE](LICENSE) for details.

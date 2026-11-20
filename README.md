# Denon Desktop — Precision Control

Flagship WPF desktop controller & system-tray companion for HEOS-enabled Denon / Marantz AV receivers.

## Architecture

- `Denon.App.Core`: Persistent, line-framed HEOS CLI (`:1255`) and Denon AVR Telnet (`:23`) clients, SSDP LAN auto-discovery, HTTP/Icecast stream health prober, atomic settings persistence, state store, and smart-backoff polling service.
- `Denon.App.Desktop`: Custom-chromed obsidian Hi-Fi WPF console, floating taskbar mini-flyout, dark-themed tray menu, single-instance guard, and Windows startup integration.

## Highlights

- **Custom Hi-Fi Control Deck**: Frameless integrated Windows 11 chrome, OLED Now Playing readout with live album/station artwork and animated 5-bar equalizer, full transport controls (`Prev`, `Play/Pause`, `Stop`, `Next`).
- **Precision Volume Engine**: Clickable dB (`-80.0 dB` to `+18.0 dB`) / Absolute (`0–98`) OLED display, live debounced volume slider with drag-lock (never fights polling), mouse-wheel support, ±1 / ±5 steps, Mute, and one-tap volume presets.
- **Full AVR Hardware Control**: Power / Standby toggle, Input Source switcher (`HEOS`, `TV`, `Blu-ray`, `Game`, `Media Player`, `SAT/CBL`, `BT`, `CD`, `AUX1`, `Phono`, `Tuner`), Sound Mode selector (`Auto`, `Stereo`, `Direct`, `Pure Direct`, `Movie`, `Music`, `Game`, `Multi-Ch Stereo`), Sleep Timer (`Off / 30m / 60m / 90m / 120m`), ECO Mode, and Zone 2 toggle.
- **Stations, Stream Prober & HEOS Favorites**: Quick-play station cards with live `ON AIR` indicator, inline station manager with reorder & search, HTTP/Icecast stream health tester (status, codec, bitrate), Direct Stream launcher, and receiver HEOS Favorites browser.
- **Auto-Discovery & Diagnostics**: One-click SSDP + HEOS LAN receiver scanner, live filtered telemetry logs, and a built-in **Raw AVR / HEOS Command Console**.
- **System Tray Turned to 11**: Single-click floating taskbar mini-controller flyout, dark-themed right-click context menu, single-instance activation, and optional launch at Windows startup.
- **Keyboard Shortcuts**: `Space` (Play/Pause), `M` (Mute), `Ctrl+↑/↓` (Volume ±2), `F5` / `Ctrl+R` (Refresh), `Ctrl+1..4` (Switch Views).

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

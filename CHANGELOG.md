# Changelog

All notable changes to **Denon Desktop** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Auto-Fullscreen YouTube Cinema Mode**: Opening a YouTube video now automatically launches the Cinema Companion in borderless fullscreen covering the taskbar, with `F11` / **Exit Fullscreen** toggle, `H` / **Hide Bars** for 100% pure edge-to-edge video, and `Esc` progressive exit (show bars → exit fullscreen → close).

### Fixed
- **Prevented Finished YouTube/Local Streams From Replaying or Autoplaying**: Immediately expires completed LAN relay stream tokens on EOF so HEOS firmware cannot reconnect and loop a finished track, cleanly stops HEOS when the Up-Next queue ends, and disables/blocks YouTube's built-in web Autoplay inside the Cinema Companion window.

## [1.1.0] - 2026-11-21

### Added
- **Interactive Shortcut Editor & Collision Checker** in Settings:
  - System-wide global Windows hotkeys (`RegisterHotKey` on a hidden message-only window so they work even when minimized to tray), including **Open / Toggle Main Window** (`Ctrl+Alt+D`), **Global Play/Pause** (`Ctrl+Alt+P`), **Global Mute** (`Ctrl+Alt+M`), **Global Volume ±2** (`Ctrl+Alt+Up/Down`), and **Global Next/Previous Track** (`Ctrl+Alt+Right/Left`).
  - Editable in-app shortcuts (`Space`, `M`, `Ctrl+Up/Down`, `F5`) with live key recording (`Esc` to cancel, `Backspace` or `✕` to clear) and full persistence in `settings.json`.
  - Real-time 4-layer collision detection checking internal duplicates (`CONFLICT`), live Win32 OS hotkey ownership (`OS TAKEN`), reserved Windows/navigation shortcuts (`RESERVED`), and cross-scope overlaps (`OVERLAP`).
- **Auto-Synced Pure-Video YouTube Cinema Companion**:
  - Tracks live YouTube stream playback position from the exact moment the Denon AVR begins pulling audio frames, automatically pausing/resuming with HEOS and compensating for HEOS buffer latency + page load time.
  - Added **Re-Sync to Denon** button and persistent buffer offset adjustments (`±100ms` / `±500ms`).
  - Strips all YouTube page & player chrome via injected CSS + `MutationObserver` so the Cinema window displays pure full-bleed video only (`100vw × 100vh`).

### Changed
- **Ultra-Low-Latency Analog VU Meters (~180 ms → <12 ms)**:
  - Upgraded from a 30 FPS `DispatcherTimer` with WPF property bindings to DWM VSync (`CompositionTarget.Rendering`, 60–144 Hz) calling direct transform updates (`SetLevelsImmediate`) with zero per-frame binding allocations.
  - Replaced per-frame `DropShadowEffect` shader blurs on rotating needles with hardware-native dual-stroke glow needles and 180 ms peak-LED hold.
  - Removed the 33 ms WASAPI capture throttle for unthrottled ~100 Hz lock-free Peak + RMS metering with frame-rate-independent 8 ms attack / 85 ms decay ballistics.

### Fixed
- **Preserved Receiver Volume on Source/Stream Changes**: Defaulted `UseLowVolumeForStreams` to `false` and removed automatic low-volume resets when launching YouTube tracks, PC Audio Cast, Local FLAC files, Global Radio Explorer streams, or system tray station presets.

## [1.0.0] - 2026-11-21

### Added
- **Obsidian Hi-Fi Control Deck**: Custom-chromed Windows 10/11 WPF console, OLED Now Playing deck with live artwork and 5-bar equalizer, clickable dB (`-80.0 dB` to `+18.0 dB`) / 0–98 precision master volume engine, full AVR hardware input/sound-mode/ECO/sleep/Zone 2 controls, and system tray companion with floating taskbar mini-flyout.
- **Zero-Config Auto-Discovery & Dynamic Model Detection**: Automatic SSDP + HEOS LAN receiver discovery on first launch, automatic hardware model & firmware detection (`DENON` / `MARANTZ` dynamic branding and model badge).
- **YouTube & YouTube Music Deck + Zero-Admin LAN Audio Relay**: Direct search/URL/playlist resolution (`YoutubeExplode`), zero-admin LAN HTTP relay (`:18925`) with FFmpeg 320 kbps MP3 transcoding and Direct M4A fallback, auto-advancing Up-Next queue, one-click `Allow Firewall` helper, and WebView2 Cinema Companion.
- **Smart Scenes & Live Acoustic Tuning Console**: 5 customizable AVR macro presets (`Movie Night`, `Late Night`, `Pure Hi-Fi`, `Morning Jazz`, `Party Multi-Ch`) with smooth volume ramping, Capture Live AVR snapshot, inline Scene Editor, plus live Telnet `:23` controls for Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, Front Panel Dimmer, Subwoofer/Dialog trims, and Tone Control.
- **Couch Phone Remote & Party Jukebox (QR Code Web UI)**: Mobile-first Obsidian web app served on port `18925` with in-app QR code for controlling volume, power, inputs, scenes, radio stations, and shared YouTube queue from any phone on Wi-Fi.
- **Cast Any PC Audio (WASAPI Loopback)**: Wireless live Windows system/device audio casting to Denon HEOS in MP3 320 kbps or Lossless WAV with silence keepalive.
- **40,000+ Global Radio Explorer & Local Hi-Res FLAC Jukebox**: Radio Browser directory search with FLAC/Hi-Res filtering and drag-and-drop bit-perfect local `.flac` / `.wav` / `.mp3` / `.m4a` streaming.
- **Zero-Lag Real-Time HEOS Push Events (`LIVE PUSH`) & Analog VU Meters**: Dedicated `register_for_change_events` push listener for `<50 ms` knob/remote sync and toggleable dual-channel illuminated VU dials.
- **Automated GitHub Releases Workflow**: `.github/workflows/release.yml` builds self-contained single-file `win-x64` executables and SHA-256 checksums on version tags.

[Unreleased]: https://github.com/ThomasRohde/DENON/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/ThomasRohde/DENON/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ThomasRohde/DENON/releases/tag/v1.0.0

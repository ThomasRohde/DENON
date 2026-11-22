# Changelog

All notable changes to **Denon Desktop** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.7.0] - 2026-11-22

### Added
- **Built-In Context-Aware Help & Interactive Guide System (`F1` / `? Help`)**:
  - Added a dedicated **Help & Guide** workspace (`Views/HelpGuideView.xaml`, `ViewModels/MainViewModel.Help.cs`, `Models/HelpGuideModels.cs`) accessible via the left navigation sidebar, a persistent **`? Help`** title-bar button, or pressing **`F1`** from any screen or plugin (with a **`← Back to [Page]`** button to snap straight back).
  - **Live Setup Readiness Checklist**: automatically inspects your real Denon AVR connection (`LIVE PUSH`), Phone Remote / Audio Relay (`:18925`), Philips Hue Bridge, LG webOS Smart TV, Home Assistant Bridge, and Smart Scenes with live progress bar and 1-click **`Open →`** jump buttons.
  - **11 Searchable, Category-Filtered Deep Guides**: covers Quick Start, Control Deck (31-Band RTA & Live Lyrics), YouTube Cinema & Acoustic Lip-Sync Calibration, Smart Scenes 2.0 & Automations, Couch Phone Remote & FLAC Jukebox, 40k Radio Explorer, Philips Hue 50 Hz DTLS Music Sync, LG webOS 2.0 & Magic Remote, Home Assistant Bridge, Complete Keyboard Shortcuts Reference Table, and Local Network Ports / Firewall Troubleshooting FAQ.
  - Updated `Ctrl+1..9` section shortcuts so all 9 sections (including Plugins `Ctrl+6`, Settings `Ctrl+7`, Health `Ctrl+8`, and Help `Ctrl+9`) are directly reachable.

## [1.6.0] - 2026-11-21

### Changed
- **Full-Width Plugins Workspace & Live Ecosystem Quick-Jump Sidebar**: Replaced the cramped 290px inner plugin sidebar with a sleek horizontal **Top Plugin Switcher Strip** (reclaiming +300px of horizontal screen space for Philips Hue, LG webOS TV, and Home Assistant dashboards) and added a **LIVE ECOSYSTEM** quick-jump list in the main left sidebar with live health dots for 1-click access from anywhere in the app.
- **Obsidian Dark Theme Controls Across All Plugins**: Added app-wide implicit Obsidian dark `Slider` (`IsMoveToPointEnabled`), rounded `CheckBox`, and rounded `ProgressBar` styles in `DenonTheme.xaml`, plus inherited dark focus-ring `TextBox` watermark placeholders (`Tag`) across every plugin.
- **Smart Collapsible Setup Panels (`Setup ▾`) & Instant Auto-Save**: Philips Hue, LG webOS Smart TV, and Home Assistant now automatically collapse their IP/pairing/token setup rows once connected so daily controls, telemetry, rooms, lights, apps, and Magic Remote sit front-and-center; Smart TV & Home Assistant coordination checkboxes now auto-save immediately, and Smart TV inputs support pressing `Enter` directly for YouTube Casting, TV Keyboard, and Toasts.
- **Philips Hue Responsive Multi-Column Lights Grid & Quick Header Bar**: Switched Individual Lights to a responsive multi-column card layout (`WrapPanel`) with instant `All On`, `All Off`, and `Sync` right in the top bar.
- **Home Assistant Bridge 2.0 UX Upgrade**: Added live connection badge, domain filter pills (`All`, `Scenes`, `Scripts`, `Switches`, `Lights`), entity search box, live ON/OFF entity state pills, live `sensor.denon_desktop_receiver` telemetry preview, and guided Long-Lived Access Token setup tip.

## [1.5.0] - 2026-11-21

### Added
- **Smart TV Control (LG webOS) 2.0 Overhaul (`Denon.Plugin.SmartTv`)**:
  - **Persistent, Auto-Reconnecting SSAP WebSocket Engine (`wss://:3001` & `ws://:3000`)** with multiplexed request correlation, strict SSAP error validation, and zero-lag real-time push subscriptions (`getForegroundAppInfo`, `audio/getStatus`, `audio/getSoundOutput`, `getPowerState`, `getExternalInputList`, `getCurrentChannel`).
  - **Dynamic Installed Apps & Real HDMI Inputs Discovery**: queries `listLaunchPoints` and `getExternalInputList` directly from your LG TV to show every real installed app (searchable, favorites-pinned, live active highlight) and every physical HDMI port with its actual label & live signal indicator dot.
  - **Virtual Magic Remote & PC → TV Keyboard**: connects to LG's secondary pointer input socket (`getPointerInputSocket`) for a full D-Pad (`Up`/`Down`/`Left`/`Right`/`OK`), `Home`/`Back`/`Exit`/`Settings`/`Info`/`Guide`/`Ch ±`, Media Transport (`Rew`/`Play`/`Pause`/`Stop`/`FF`), and live IME text entry (`insertText`, `sendEnterKey`, `deleteCharacters`).
  - **OLED Screen Off (Audio-Only Mode) & Audio Output Switcher**: one-click **OLED Screen Off** (`turnOffScreen`) to blank the panel while keeping music/eARC playing with zero burn-in, plus one-click switching between **HDMI ARC/eARC**, **TV Speakers**, **Optical**, and **Bluetooth**, and a debounced TV Volume slider.
  - **YouTube URL / Video ID Caster**: paste any YouTube link or 11-character video ID to deep-link directly on your LG TV (`contentId: "v=..."`).
  - **Multi-Interface SSDP Auto-Discovery + Win32 ARP MAC Lookup + Multi-NIC Wake-on-LAN**: auto-discover LG webOS TVs on the LAN, auto-fill their Wake-on-LAN MAC address via ARP, and auto-reconnect after Wake-on-LAN.
  - **Expanded Denon AVR Coordination (24 Plugin Actions)**: auto-switch LG TV audio to **HDMI ARC/eARC** on Denon Power ON, auto-mute TV speakers, optional **OLED Screen Off** on Music Smart Scenes, and full availability across Smart Scenes 2.0, Global Hotkeys, and Couch Phone Remote.

### Fixed
- **LG webOS YouTube Launch Bounce-Back & Lost Pairing Key**: Switched standard app launches to `ssap://com.webos.applicationManager/launch` over the persistent WebSocket (eliminating the short-lived socket teardown that caused apps like YouTube to briefly flash and return to Live TV), added dynamic installed app ID resolution, validated SSAP error responses instead of silently ignoring failures, and immediately persisted returned `client-key` credentials to `config.json`.

## [1.4.0] - 2026-11-21

### Added
- **Unsigned Windows x64 Installer (`DenonDesktop-Setup-win-x64.exe`)**: Added an Inno Setup 6 installer (`installer/DenonDesktop.iss`) built automatically alongside the portable single-file `.exe` in GitHub Releases, featuring per-user zero-admin installation (with optional All Users), Start Menu & optional Desktop/Startup shortcuts, the R2C5 app icon, clean Add/Remove Programs uninstaller, and SHA-256 verification.

### Fixed
- **Philips Hue "All Lights" View & Internal Group Cleanup**: Added an **All Lights** chip (default view) and a **Show All Lights** button on the Individual Lights header so every light on the Hue Bridge is visible at once instead of being filtered to only the first alphabetical room; filtered out internal `hgrp-*` Hue system groups and fixed singular/plural light count labels (`1 light` vs `3 lights`).
- **Responsive Plugin Dashboard Headers**: Converted the Philips Hue, Smart TV, and Home Assistant dashboard headers from `DockPanel` to 2-column `Grid` layouts with subtitle wrapping so action buttons (`Try Demo Mode`, `Push Status`, `Save & Sync`, `Pair with LG TV`) never overlap text at narrower window widths.

## [1.3.0] - 2026-11-21

### Added
- **Couch Phone Remote 2.0 — Lights & Plugins on Your Phone**:
  - Added a dedicated **Lights** tab to the QR-code mobile web UI (`:18925`) backed by new `/api/plugins/action` and `/api/plugins/toggle` REST endpoints so anyone on Wi-Fi can control Philips Hue lights, toggle Music Sync, switch Smart TV inputs, or trigger Home Assistant actions right from their phone.
- **True 50 Hz Philips Hue Entertainment API v2 DTLS/UDP Engine**:
  - Built `HueEntertainmentStreamClient` using `BouncyCastle.Cryptography` DTLS 1.2 PSK (`TLS_PSK_WITH_AES_128_GCM_SHA256`) over UDP port `2100` (`HueStream v2.0` binary RGB frames) — automatically activates 50 Hz zero-lag streaming on detected Hue Entertainment Areas when Music Sync starts, with seamless fallback to LAN REST & Demo Mode.
- **31-Band Live OLED FFT Spectrum Analyzer (RTA) & Synced Live Lyrics (`LRCLIB`)**:
  - Extended `LiveAudioAnalyzerService` with 31 logarithmically spaced ISO frequency bands (`25 Hz–16 kHz`) rendered at 60–144 Hz VSync in `SpectrumAnalyzerControl` with peak-hold caps and a one-click **VU Meter → RTA 31 → VU + RTA** switcher on the Hero OLED deck.
  - Added zero-API-key `LyricsService` (`lrclib.net`) with live LRC timestamp synchronization displayed in a toggleable OLED Lyrics bar right on the Hero Now Playing card.
- **Two New Official Decoupled Plugins**:
  - **`Denon.Plugin.SmartTv` (LG webOS + Wake-on-LAN)**: SSAP WebSocket (`:3000`) pairing & control for Power On/Off, HDMI 1/2/3 switching, YouTube/Plex launch, TV mute, on-screen toast alerts on Smart Scenes, and Demo Simulator mode.
  - **`Denon.Plugin.HomeAssistant` (Home Assistant REST & Webhook Bridge)**: Trigger HA scenes, scripts, blinds, and smart plugs from Denon Desktop or Phone Remote, and automatically push live `sensor.denon_desktop_receiver` telemetry & webhooks to Home Assistant.
- **1-Click Acoustic Lip-Sync Auto-Calibration (PC Mic Chirp)**:
  - Generates a precision 1,750 Hz 3-burst chirp WAV served via `/calibrate/{token}.wav`, listens on your PC microphone with a narrowband Goertzel detector (`AcousticSyncCalibrationService`), measures the exact HEOS buffer + speaker arrival latency in milliseconds, and automatically saves it to `CinemaSyncOffsetMs`.
- **Designer R2C5 Icon Pack & Dynamic Live System Tray Icons**:
  - Replaced the application, taskbar, title-bar, and executable icons with the custom-designed **R2C5 Headphones + Cyan Waveform** multi-resolution icon pack (`denon_icon.ico`, `denon_icon.png`, plus 12 themed variants & editable SVGs).
  - Upgraded `TrayIconController` to dynamically switch the Windows system tray icon in real time between **Active Playback** (`active.ico`), **Muted** (`muted.ico`), **Standby/Offline** (`power.ico`), and **Connected Idle** (`denon_tray.ico`).
- **Full Release Notes on GitHub Releases Pages**:
  - Upgraded `.github/workflows/release.yml` to extract each tag's full Markdown notes from `CHANGELOG.md` and publish them directly in the GitHub Release body along with inline SHA-256 verification.

## [1.2.0] - 2026-11-21

### Added
- **Extensible Plugin System & Official SDK (`Denon.App.PluginSdk`)**:
  - Added a standalone `Denon.App.PluginSdk` contract library (`IDenonPlugin`, `IPluginViewProvider`, `IPluginContext`, `IPluginStorage`, `IPluginReceiverBridge`, `PluginReceiverEvent`, `PluginActionDescriptor`) plus `PluginHostService` supporting both built-in plugins and external drop-in `.NET 10` plugin `.dll` assemblies loaded via isolated `AssemblyLoadContext` from `%LocalAppData%\DenonDesktop\plugins\external\`.
  - Added a dedicated **Plugins** section to the main navigation sidebar with an installed plugins manager (live health badges, per-plugin enable/disable persistence, **Rescan External DLLs**, **Open Plugins Folder**, and 1-click **Plugin Actions** runner).
  - Wired real-time Denon AVR event broadcasting (Power, Input, Sound Mode, Playback, and Smart Scene activation — including scenes triggered from the Couch Phone Remote) directly to enabled plugins.
  - Added **Audio-Reactive & Album-Art Sync SDK Extensions** (`IAudioReactivePlugin`, zero-allocation `PluginAudioFrame`, `PluginAlbumArtPalette`, `LiveAudioAnalyzerService` 512-point Radix-2 FFT + adaptive spectral onset beat detector, and `AlbumArtPaletteService` vibrant cover-art color clustering).
- **Smart Scenes 2.0 & Cross-Plugin Automation Engine**:
  - **Scene → Plugin Action Chains**: attach ordered plugin actions with configurable millisecond delays directly inside the Scene Editor (e.g., `Movie Night` → `Philips Hue: Cinema Dim`, `Late Night` → `Philips Hue: Candlelight`).
  - **Automation Rules Engine** (`AutomationEngineService` + new **Automation Rules** sub-tab under **Scenes & Acoustics**): edge-triggered **App Launch Watchers** (e.g. launching `vlc`, `Plex`, or games automatically triggers Smart Scenes & plugin actions with cooldown), **Time-of-Day / Night Guard Schedules** (`Daily` / `Weekdays` / `Weekends`), one-click **Test Run**, and a live Automation Activity Log.
  - **Global Hotkeys for Any Smart Scene or Plugin Action**: added `+ Scene Key` and `+ Plugin Key` quick-binders to the Settings Shortcut Editor so any global Windows hotkey can fire Smart Scenes or Plugin Actions (e.g., toggle Hue Music Sync or All Lights Off) with full 4-layer collision checking.
- **First-Party Plugin — Philips Hue Smart Lighting (`Denon.Plugin.PhilipsHue`)**:
  - Auto-discovers Philips Hue Bridges via Hue Cloud Discovery + offline LAN SSDP M-SEARCH, supports manual Bridge IP entry, 30-second guided Link-Button pairing over HTTPS (with local private-LAN self-signed TLS validation, Entertainment `clientkey` capture, and HTTP fallback), and a built-in **Demo Simulator** mode.
  - Full control over Master All-On/Off & brightness presets, Rooms & Zones, debounced dimmers, CIE 1931 RGB mood swatches, Hue Bridge native scenes, and individual lights (power, brightness, Kelvin warmth, color dots).
  - **Real-Time FFT Music Sync & Album-Art Ambient Engine**: 4 beat-reactive modes (**Bass Pulse**, **Spectrum Split**, **Album-Art Ambient**, **Cinema Subtle**), live 3-band **Bass / Mid / High** FFT spectrum meters + **Beat** LED, sensitivity and brightness floor/ceiling sliders, CLIP v2 Entertainment Area detection, a 1-click **Simulate Beat** tester, and callable actions (`hue.music_sync_on/off/toggle`).
  - Reactive **Denon AVR ↔ Hue Automations**: automatically sync Hue lighting to Denon Smart Scenes, optional Power ON / Standby sync, and **Cinema Pause Lights** (brighten on Pause, dim on Resume).
  - Upgraded the **Analog VU Meters** to automatically tap live Windows PC WASAPI audio via `LiveAudioAnalyzerService` even when PC Audio Cast is not active.

### Fixed
- **Embedded YouTube Player Auto-Fullscreen & Chrome Removal**: Opening YouTube Cinema now keeps the Cinema window at its normal resizable desktop size (`960×600`, with optional `F11` monitor fullscreen) and automatically maximizes YouTube's embedded video player inside the viewer (triggering YouTube's native fullscreen button / `#movie_player.requestFullscreen()` with Chromium user-gesture activation via WebView2 DevTools Protocol). Fixed an early-injection null DOM race in `PureVideoModeScript` that could prevent YouTube page/player chrome CSS from applying cleanly.

## [1.1.1] - 2026-11-21

### Added
- **Auto-Fullscreen YouTube Cinema Mode**: Opening a YouTube video now automatically launches the Cinema Companion in borderless fullscreen covering the taskbar, with `F11` / **Exit Fullscreen** toggle, `H` / **Hide Bars** for 100% pure edge-to-edge video, and `Esc` progressive exit (show bars → exit fullscreen → close).

### Changed
- **Larger Default Window & Commanding Analog VU Meters**: Increased default window dimensions to `1360×920`, expanded the dual-channel Analog VU Meters to `138 px` height with a wider high-contrast dB arc (`-20` to `+3 dB`), and gave the Settings Shortcut Editor its own dedicated internal scroll view so Desktop Behavior and Relay controls stay anchored in place.

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

[Unreleased]: https://github.com/ThomasRohde/DENON/compare/v1.7.0...HEAD
[1.7.0]: https://github.com/ThomasRohde/DENON/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/ThomasRohde/DENON/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/ThomasRohde/DENON/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/ThomasRohde/DENON/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/ThomasRohde/DENON/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/ThomasRohde/DENON/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/ThomasRohde/DENON/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ThomasRohde/DENON/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ThomasRohde/DENON/releases/tag/v1.0.0

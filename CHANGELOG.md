# Changelog

All notable changes to **Denon Desktop** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.16.0] - 2026-11-23

### Added
- **Plugin SDK 1.16 — action metadata for MCP**: `PluginActionDescriptor` gets init-only `Parameters` (`PluginActionParameter`: name, JSON type, description, required), `IsReadOnly` and `IsDestructive`. The constructor is unchanged, so plugins built against 1.15 still load. `Parameters = null` means "not declared"; an empty list means "no parameters".

### Fixed
- **MCP plugin tools return their data**: Auto-bridged tools (`plugin_<id>_<action>`) now return the plugin's JSON (pretty text + `structuredContent` for objects) and image (for example Chromecast artwork) instead of only the message text, and set `isError` on failure. New `McpPluginToolBridge` with xUnit tests.
- **MCP plugin tool schemas and hints**: Tools use the declared parameters as their input schema (older plugins keep the generic `message`/`contentId`/`entityId`/`button` schema) and the declared read-only/destructive hints (older plugins keep the id-based guesses).
- **Read-only plugin tools work in the Observer tier**; actions a plugin declares destructive need FullOrchestration (as `power_off` already did).

## [1.15.2] - 2026-11-23

### Fixed
- **Paused or unseen casts no longer count as watching**: Live TV, or a TV app that is not on an HDMI input, now covers a cast session (the TV shows something else, so the Chromecast plays unseen). Sessions that report `watchedSec` (cast) never get more `exclusiveSec` than `watchedSec`. Before, a DR TV program paused behind live DR1 for 10 minutes counted as 10 extra minutes.

## [1.15.1] - 2026-11-23

### Added
- **`TODO.md`**: Open work in one place (meter phases 2–3, habit telemetry, Chromecast plugin, release).

### Changed
- **VU meters & 31-band RTA follow the Denon, not the PC**: A new source router picks the meter signal from what the receiver plays. A badge shows the source: **DENON** (real PC Cast audio, delayed by the measured HEOS delay `CinemaSyncOffsetMs` so it is in sync with the speakers), **SIM** (the Denon plays TV/HDMI/HEOS audio the app cannot hear; simulated from the Denon volume and fading out below volume 15), **MUTED**, **PC** (the Denon is off or idle; the PC's own output, labelled "PC AUDIO (NOT DENON)" on the RTA) and **IDLE**. A podcast on the laptop no longer shows as Denon audio.

### Fixed
- **PC Cast meters were 1.5–3.5 s ahead of the speakers**: Meter and FFT frames are now kept in a short history and shown after the HEOS buffer delay.

## [1.15.0] - 2026-11-23

### Added
- **Plugin SDK 1.15 — Receiver Control API**: New `IPluginReceiverControl` (power, input, sound mode, volume, mute, Smart Scenes) exposed as `IPluginContext.ReceiverControl`. The host grants it only to plugins that declare the new `PluginCapabilities.ReceiverControl` flag and logs every command with the plugin id. The member has a default `null` implementation, so existing plugins are not affected.
- **First standalone external plugin — Chromecast & Google Cast** (`external-plugins/Denon.Plugin.Chromecast`): mDNS discovery, live now-playing, Cast v2 media and volume control, `media` telemetry events, and per-device Denon automations (power on, Smart Scene, input, sound mode on session start; idle power-off; pause when the receiver powers off). Writes habit-ready `session.started`/`session.ended` watch sessions (`kind: "cast"`, per program, 15 s dwell, `watchedSec` excludes pauses) plus `cast.app_ended` summaries on the `media` stream. Not part of the app solution; install with `install.ps1`.
- **Standalone plugin releases**: The Chromecast plugin is released on its own (`chromecast-vX.Y.Z` tags, zip + SHA-256) by the new `release-chromecast-plugin.yml` workflow, separate from app releases. The README now covers the plugin, SDK 1.15, the MCP server, event telemetry, tests and `build-release.ps1`.

### Fixed
- **Plugins view**: The plugin card now always shows the version (a long name no longer pushes it out), and the header badge shows `SDK v1.15`.
- **Chromecast dashboard**: Dark-themed drop-downs (the host has no ComboBox theme) and a "No artwork" placeholder.
- **Local release build**: `release-out/` is now ignored by git.
- **No double counting of watch time**: Every `session.ended` on the `media` stream now has `coveredSec`, `exclusiveSec` and (when covered) `coveredBy`. One viewing makes parallel sessions (Cast program, LG TV on the HDMI input, Denon "TV Audio" via ARC); the less specific ones are marked as covered for that time. Sum `exclusiveSec` to get real time. New `MediaSessionOverlapTracker` with xUnit tests.
- **Sessions lost when Windows closes the app**: On logoff, shutdown or an installer upgrade, the app only disposed the host and never stopped plugins or hosted services, so open sessions got no `session.ended`. Both exit paths now share one ordered, bounded stop (plugins → services → dispose). LG TV sessions now end with `app_shutdown` (not `standby`) at app exit, and `plugin_stopped` when the TV plugin is stopped or disabled.
- **Exact session start times**: `dwellConfirmedSec` on `session.started` is now the real time since the session began (it was always 15, but the check runs on a timer, so the real value is often 16–20 s). `ts - dwellConfirmedSec` is the exact start.
- **Chromecast watch log**: `watchedSec` now includes play time during the 15 s dwell (it was too low by up to 15 s per program; a 15 s clip was logged as 3 s). Apps get clean names by Cast app id (e.g. DR TV instead of "AXIS 10 PROD - www.drtv-platform.dr.dk"). `receiverInput` is filled in when the Denon becomes known after the program started.

## [1.14.0] - 2026-11-22

### Changed
- **Custom Denon Obsidian & Cyan Windows Installer**:
  - Pre-selected **I accept the agreement** by default on the license step for faster installs and upgrades.
  - Branded the Inno Setup wizard with custom Hi-DPI Denon Desktop sidebar artwork (`wizard-sidebar.bmp`), glowing top-right badge (`wizard-small.bmp`), Windows 11 dark title bar (`DWMWA_USE_IMMERSIVE_DARK_MODE`), Obsidian `#0A0C10` / `#13161E` surfaces, Denon Cyan `#00D4FF` headings & accent line, and high-contrast custom radio labels.
- **CI Pull Request Test Gate**:
  - Extended `.github/workflows/release.yml` to run automated `dotnet test` verification on `pull_request` events targeting `master` (skipping installer packaging & release uploads on PRs).

### Fixed
- **Live External Remote, Smart TV & Phone App Now-Playing Sync**:
  - Added unified `NowPlayingResolver` across Desktop UI, Phone Remote, and MCP `get_room_state`: when the AVR is on `TV Audio`, Denon Desktop now displays the live LG webOS TV app, Live TV channel, or labeled HDMI input (e.g. `DVD Player (HDMI 1)`, `Netflix`, `Live TV (Ch 1 • DR1)`) and updates immediately when switching with the physical TV remote, showing `Live TV` / `Active` instead of misleading HEOS `Stop` or stale `Url Stream`.
  - Parsed HEOS `"station"` metadata and service names (`Spotify`, `TuneIn`, `Tidal`, `Deezer`, `Amazon Music`, `AirPlay`, etc.) in `HeosClient`, recognized AirPlay/Tidal/Deezer/Amazon/TuneIn/USB inputs, and surfaced live track/artist/artwork metadata for Bluetooth phone streams.
  - Auto-deactivated stale YouTube Cast, Local Hi-Res Jukebox, and radio preset highlights whenever a physical remote switches inputs/standby or a phone app (Spotify Connect, AirPlay, HEOS, TuneIn) takes over playback, and added coalesced 250ms HEOS push refreshes plus a 15-second LG webOS foreground/channel/input sync guard against dropped TV subscriptions.
  - Preserved confirmed AVR power/input/soundMode across brief port 23 collisions (up to 2 polls) so external Denon phone apps cannot transiently overwrite `TV` with synthetic `NET / HEOS`.
- **Local Relay Range Probes & Stream Isolation**:
  - `LocalAudioRelayServer` no longer invalidates local track URLs or fires `LocalTrackStreamCompleted` on metadata suffix reads (`Range: bytes=-1`); completion now fires only when a full-track transfer reaches EOF, and tokens stay registered for the active session until explicitly unregistered.
  - Clearing an inactive `YouTubeQueueService` or `LocalMediaJukeboxService` now cancels only its own tracked relay token (`CancelStream(token)`) instead of interrupting unrelated active PC Audio Cast or local streams.
- **Pause-Aware & Mute-Preserving Queue Advancement**:
  - `YouTubeQueueService` and `LocalMediaJukeboxService` now suspend their end-of-track buffer drain timers while HEOS is paused (`playState == "pause"`) and preserve the receiver's current mute state across automatic next-track transitions.
- **Atomic Settings Updates, Safe Backup Recovery & Multi-Receiver Discovery**:
  - Added atomic `AppSettingsStore.UpdateAsync(...)` merges inside the persistence gate and switched quick toggles, Remote token rotation, calibration saves, and discovery updates to it so concurrent saves never clobber independent fields; validated the existing primary file before rotating to `.bak` so saving after backup recovery never overwrites a good `.bak` with corrupt JSON; surfaced startup recovery warnings with the exact settings path.
  - `ReceiverPollingService` now auto-selects only when a single receiver is discovered on the LAN; when multiple receivers are found, it lists them clearly so the user chooses the right room in Settings.
- **Room-Scoped Undo & HEOS-Only Timeout Fallback**:
  - `RoomIntelligenceService` now scopes `CanUndo`, `LastSnapshot`, and `RestoreLastSnapshotAsync` strictly to the currently connected receiver's identity so Room A's snapshot can never be applied to Room B, and refuses to capture synthetic HEOS-only status (`IsAvrConfirmed == false`).
  - `ReceiverController` now distinguishes `DenonAvrClient`'s internal 3-second connect/command timeout from explicit caller cancellation (`!cancellationToken.IsCancellationRequested`) so HEOS-only speakers seamlessly enter HEOS fallback instead of failing.
- **Phone Remote, Desktop UI, PC Cast & Windows Startup Polish**:
  - Phone Remote (`RemoteWebUiHtml`) now retains optimistic volume steps across spaced taps until a fresh poll confirms, hides the header Power button when locked/disabled, displays a dedicated `Remote Disabled` recovery card with Retry on `403`, and auto-focuses the pairing input on `401`.
  - Split the Scene Editor toggle into separate **Power on AVR** and **Auto-unmute AVR** switches, enabled text wrapping on the Phone Remote QR caption, lowered `MainWindow.MinHeight` to `580` for compact displays, and relabeled the PC Cast WAV button honestly to **PCM 16-bit WAV**.
  - `PcAudioCastService` now supports 64-bit IEEE float WASAPI loopback streams in `ConvertToStereo16Pcm`, and `WindowsStartupService.SetEnabled` preserves the installer's `--minimized` autostart argument when saving general Settings.
  - Isolated `AppLogStore`, `EventTelemetryLog`, and `RoomIntelligenceService` file paths in `Denon.App.Core.Tests` so running `dotnet test` never reads or modifies `%LocalAppData%\DenonDesktop`, and expanded the suite to cover all follow-up review findings.

## [1.13.1] - 2026-11-22

### Fixed
- **Clean Process Exit (No Lingering Background Process on Shutdown / Upgrade)**:
  - Fixed a WPF UI-thread async-over-sync deadlock in `App.ExitApplicationAsync` where synchronous `_host.Dispose()` blocked while disposing `IAsyncDisposable` singletons (`DenonMcpHttpServer`, `PluginHostService`, `EventTelemetryLog`), leaving a hidden `Denon.App.Desktop` process running after clicking Exit and triggering Inno Setup's "application still using files" prompt.
  - Host shutdown & async disposal now run off the WPF Dispatcher thread with bounded timeouts, `YouTubeCinemaWindow` force-closes cleanly on exit instead of cancelling `OnClosing`, and Windows session/Restart Manager shutdown exits cleanly without hiding to tray.

## [1.13.0] - 2026-11-22

### Added
- **Phone Remote Pairing, Roles & Access Controls**:
  - Added switchable Phone Remote access (`Remote: ON / OFF` toggle — media fetches to Denon AVR stay online even when phone control is off), separate **Owner QR** (full room control) and **Guest QR** (Party Jukebox YouTube search & queue only), secure token pairing (`?pair=<token>` / `X-Denon-Token`), and instant **Revoke & Rotate** without interrupting active streams.
- **Automated Test Suite (`Denon.App.Core.Tests`) & CI Test Gate**:
  - Added xUnit project covering settings recovery/empty libraries/custom shortcuts/secret redaction, HTTP range & split-UTF-8 body parsing, PC Audio Cast 16/24/float/6-ch PCM conversion, Phone Remote pairing/roles/revocation, Radio Browser FLAC/256k+ union filtering, and Room Undo / telemetry origin attribution — wired into `.github/workflows/release.yml`.

### Fixed
- **Automatic Queue Transitions & Failed Track Starts**:
  - Fixed self-cancelling delay token bug in `YouTubeQueueService` and `LocalMediaJukeboxService` auto-advance transitions (`PlayNextAsync` now uses an independent cancellation lifetime after the drain delay), added rollback of active index/history if relay registration or cast fails, added clean end-of-queue completion for local files, and allowed valid short local clips (`> 0` bytes) to complete naturally.
- **YouTube Playback Cutting Off Early Mid-Song**:
  - `LocalAudioRelayServer.StreamTranscodedMp3Async` bursts the first 6 seconds (240 KB) immediately for instant HEOS playback start and then paces streaming gently at ~1.03× real-time (`41.2 KB/s`) with `-err_detect ignore_err`, preventing Denon HEOS from over-buffering 60+ seconds ahead.
  - `YouTubeQueueService.OnRelayTrackStreamCompleted` and `LocalMediaJukeboxService` wait for HEOS to naturally drain its internal audio buffer (`playState == "stop"` / remaining `track.Duration`) before advancing or stopping.
- **Quiet Scene & Room Undo Safe Volume Ordering**:
  - `ReceiverController.ApplySceneAsync`, `RoomIntelligenceService.RestoreLastSnapshotAsync`, and `DenonMcpHttpServer.ExecuteApplyAtmosphereAsync` now establish target volume **before** switching inputs or releasing mute so a muted receiver at high volume never briefly blasts when activating a quiet scene or undoing.
  - `RoomIntelligenceService` now refuses to synthesize assumed Undo snapshots when disconnected, retains snapshots on failed restore for retry, and enforces `MaxSafeVolume` on MCP Undo.
- **HEOS-Only Speaker Fallback**:
  - `ReceiverController` now tracks AVR port 23 availability independently (`IsAvrProtocolAvailable`) and degrades gracefully to HEOS-only status, volume, mute, transport, and streaming when port 23 is unavailable (automatically restoring full AVR controls when port 23 recovers).
- **PC Audio Cast Format Fidelity & Cleanup**:
  - `PcAudioCastService` now accurately converts 16-bit, 24-bit, 32-bit int, 32-bit float, and multichannel WASAPI loopback audio to stereo 16-bit PCM WAV (and passes the exact `f32le`/`s24le`/`s32le`/`s16le` format to FFmpeg MP3), paces silence keepalive 1:1 to real time (100 ms per 100 ms idle), and cancels the silence producer before awaiting on disconnect.
- **Settings Safety, Custom Shortcuts, Empty Libraries & Secret Redaction**:
  - `AppSettingsStore` preserves corrupt `settings.json` files (`.corrupt-*`), keeps a last-good `.bak` backup, publishes in-memory state only after atomic disk replacement succeeds, preserves custom `scene:*` / `plugin:*` shortcuts, honors intentionally empty station/scene libraries, and redacts MCP/Remote tokens on JSON export (restoring local secrets on import).
- **Phone Remote Web UI, Relay & Accessibility**:
  - Replaced inline JSON click handlers in `RemoteWebUiHtml` with DOM event listeners so apostrophes/quotes/Unicode titles (`"Don't Stop"`, `"Björk"`) never break Play/Queue; preserved keyboard focus across 2.2s polls; serialized rapid volume taps without dropping steps; distinguished Connecting / Standby / Receiver Unreachable / Desktop Unreachable with Retry; enabled pinch-zoom and added ARIA labels/tablist semantics.
  - Fixed `LocalAudioRelayServer` to decode UTF-8 HTTP request bodies once from raw bytes (preserving non-ASCII titles split across packets), implemented RFC suffix ranges (`bytes=-N`) + `416 Range Not Satisfiable`, prevented advertising nonzero `206` offsets on non-seekable streams, and pointed `Allow Firewall` at the actual bound port.
- **Desktop UI, Cinema Viewer, Plugins, Telemetry & Packaging**:
  - Added a translucent auto-hiding floating hover HUD (`Show Bars (H)` | `Fullscreen (F11)` | `✕ Close`) inside `YouTubeCinemaWindow` when bars are hidden, clearing `WindowChrome.CaptionHeight` so top-right clicks work cleanly.
  - Prevented quick UI/remote preference toggles from wiping unsaved Settings/Stations/Scenes edits; prevented `Space`/`Enter` from hijacking focused buttons/checkboxes; raised muted/secondary text contrast to ≥ 4.5:1 WCAG AA; ran local folder drops off the UI thread with `IgnoreInaccessible` and added/duplicate/skipped reporting; updated spectrum analyzer labels (`PC LOOPBACK FFT` / `SIMULATED (VOL)`); guarded Lyrics fetches against stale out-of-order responses and paused playback.
  - Made Hue & Home Assistant startup syncs non-blocking with 5s timeouts; retained Hue Entertainment stop identifiers before DTLS handshake so failed handshakes always release the bridge area; masked Home Assistant token with a `PasswordBox`; preserved distinct parameterized plugin steps and surfaced per-step failures in Automation; fixed Radio Browser `FLAC / 256k+` union filter; switched event telemetry files to daily granularity (`yyyy-MM-dd`) for exact day-based retention; passed `--minimized` in installer autostart and derived branch installer version from `Denon.App.Desktop.csproj`.

## [1.12.0] - 2026-11-22

### Added
- **Comprehensive Smart Event Telemetry & Habit-Ready Logging System**:
  - **Split Monthly NDJSON Event Streams (`%LocalAppData%\DenonDesktop\logs\events\`)**: Non-blocking bounded-channel writer (`EventTelemetryLog`) writes structured `.jsonl` files separated by domain and plugin (`media-yyyy-MM.jsonl`, `avr-yyyy-MM.jsonl`, `tv-yyyy-MM.jsonl`, `lighting-yyyy-MM.jsonl`, `control-yyyy-MM.jsonl`, and `plugins/<plugin-id>-yyyy-MM.jsonl`) with automatic 90-day / 50 MB retention pruning and automatic 5 MB log rotation for `app.log`.
  - **Zero-Noise Smart Delta & Session Engine (`EventTelemetryMonitorService`)**:
    - Records **only real state transitions** across Denon AVR (`power.changed`, `zone2.changed`, `input.changed`, `sound_mode.changed`, `mute.changed`, `eco.changed`, `sleep_timer.changed`), LG webOS TV (`power.changed`, `app.changed`, `channel.changed`, `audio_output.changed`, `screen.changed`), and Philips Hue (`lighting.state_changed`, `preset.applied`, `color.set`, `music_sync.changed`).
    - **3-Second Volume Coalescer**: Absorbs rapid remote/UI volume steps into a single `volume.settled` event (`from`, `to`, `delta`, `input`, `soundMode`).
    - **15-Second Media Dwell Filter**: Tracks both AVR/HEOS sessions (YouTube, Internet Radio, external Spotify Connect / AirPlay / TuneIn / HEOS phone streams, and active AVR inputs) and LG TV watching sessions (Live TV channels & streaming apps), recording `session.started` / `session.ended` (`durationSec`, `avgVolume`, `reason`) only after dwelling ≥ 15 seconds — discarding rapid channel surfing and track skips cleanly.
    - **Adaptive Background Probing & External Capture**: Polls active devices every 15s (60s in standby) plus periodic Hue bridge sync (60s active / 5m standby) to capture external physical remote, phone cast, and wall-switch/Hue-app activity.
  - **Automatic Origin Attribution**: 6-second origin watermarks accurately tag every event with its real source: `desktop_ui`, `mcp_agent`, `phone_remote`, `automation`, `smart_scene`, or `external`.
  - **Plugin SDK Event Hooks (`IPluginContext.Events`)**: First-class `IPluginEventLogger` in `Denon.App.PluginSdk` automatically routes built-in and third-party plugin events to their dedicated streams.
  - **Agent-Ready MCP Tool & Resource Template**: Added read-only MCP tool `get_activity_history` (`stream`, `hours`, `limit`, `origin` filters + live stats) and resource template `denon://logs/events/{stream}` with `stream` completions so AI agents can derive rich viewing/listening habits.
  - **Settings UI Controls**: Added a **Smart Event & Habit Logging** card in Settings with enable toggle, configurable retention days & media dwell filter, live session/disk stats, **Open Events Folder**, and **Clear Event History** (plus fixed Settings save to preserve MCP/Plugin/Automation configs cleanly).

## [1.11.1] - 2026-11-22

### Fixed
- **VU Meters & 31-Band RTA Auto-Rebind on Windows Audio Device Switch**: `LiveAudioAnalyzerService` now monitors the active Windows Default Audio Render Endpoint (`MMDeviceEnumerator`) and automatically rebinds `WasapiLoopbackCapture` when turning an HDMI TV on/off or switching speakers/headphones, preventing VU meters from freezing.
- **LG webOS 23/24 Screen Off Cascade & Accurate Display Wording**: Updated `TurnOffScreenAsync` / `TurnOnScreenAsync` with a multi-endpoint cascade (`tvpower/power/turnOffScreen` with `standbyMode`, empty `{}` payload for webOS 23/24, and `settings/setSystemSettings` `energySaving: "screen_off"` fallback); replaced blanket "OLED" labels with universal **Screen Off / TV Display** wording (only mentioning OLED burn-in on actual OLED model IDs) and prevented Room Doctor from suggesting Screen Off while watching Live TV / video apps or when AVR input is `TV`.
- **YouTube Playback Falling Back to Previous Radio Station & Source-Switching Hardening**:
  - `YouTubeMediaService.FindFfmpegPath()` now prefers the real `WinGet\Packages\...\ffmpeg.exe` binary, resolves symlinks (`ResolveLinkTarget`), and verifies FFmpeg launches cleanly (`-version`) before advertising `.mp3` relay URLs — fixing Windows *"untrusted mount point"* crashes on WinGet symlinks.
  - `LocalAudioRelayServer.StreamTranscodedMp3Async` now starts FFmpeg **before** writing HTTP `200 OK` headers and automatically falls back to Direct M4A streaming if FFmpeg ever fails to spawn (preventing 0-byte HTTP drops that caused HEOS to fall back to the previously loaded radio station).
  - `ReceiverController` now cleanly stops stale HEOS playback (`state=stop`) before switching to `SINET` for YouTube / Radio / Favorites and when switching away from `NET` to `TV` / `HDMI` / Smart Scenes so old radio stations never auto-resume during source switches.

## [1.11.0] - 2026-11-22

### Added
- **Direct LG webOS System Settings API (`get_settings` & `set_settings` in `tv_computer`)**:
  - Added `GetSystemSettingsAsync` and `SetSystemSettingsAsync` in `LgWebOsClient` (`ssap://settings/getSystemSettings` / `setSystemSettings` with `com.webos.settingsservice` fallback) for fast 100 ms reads/writes of `picture`, `sound`, `option`, or `all` (picture + sound) settings — with clear fallback guidance to on-screen Settings navigation on firmwares that restrict direct writes.
  - Read-only `get_settings` works in all MCP permission tiers (including `Observer`).
- **Smart TV Live Preview & Virtual Trackpad Polish**:
  - Added a quick **`TV Settings`** shortcut right in the Live TV Screen header bar (automatically snaps a fresh preview after launch when `Auto-Refresh` is off).
  - Added adaptive **Active-Gesture Fast Refresh (`1 s` while actively steering the Virtual Trackpad or clicking the preview, relaxing to `2.5 s` when idle)** and persisted your `Auto-Refresh` preference in `config.json`.
  - Added an inline **Quick Type** input bar directly beneath the Virtual Trackpad so you can click a TV search box and type without scrolling.
- **Smarter Living-Room Room Doctor 2.0 Checks**:
  - Added lazy background Telnet acoustic caching in `RoomIntelligenceService` (also surfaced live in `get_room_state`) powering three new zero-clutter 1-click Auto-Fix insights:
    - **Evening Cinema Display Glow (`avr.dim_panel`)**: detects Denon front panel left on `BRI` during evening movie/surround sound modes and dims it to `DAR` in 1 click.
    - **Daytime Music Night-Compression (`avr.dynvol_off`)**: detects `Dynamic Volume: HEV` left active during daytime music listening and turns it off for full dynamics.
    - **Evening Movie Lights (`hue.movie_dim`)**: detects Philips Hue lights still bright while Netflix/Disney+/Plex/Apple TV/Prime Video/SkyShowtime is playing in the evening and offers 1-click Movie Night dimming.

## [1.10.1] - 2026-11-22

### Changed
- **Denon Front-Panel Volume Scale by Default (`28.0` instead of `-52.0 dB`)**: Defaulted `DisplayVolumeInDb` to `false`, formatted the main volume display to exact `0.0` Denon front-panel steps (e.g. `28.0` / `28.5`), and persisted clicking the volume readout to `settings.json` immediately (dB mode remains 1 click away).
- **Calibrated LG webOS Pointer Targeting & Built-In `settings` App Alias**: Updated `MovePointerToNormalizedAsync` to the empirically verified `1280×720` LG webOS logical pointer plane with smaller uniform `24 px` steps to avoid high-velocity pointer acceleration drift; added `"settings"` / `"indstillinger"` → `com.palm.app.settings` alias and wired the Smart TV **Settings** remote button to launch it directly.

## [1.10.0] - 2026-11-22

### Added
- **Live TV Screen Preview & Interactive Virtual Trackpad (Smart TV Plugin UI)**:
  - Added a full-width **Live TV Screen & Virtual Trackpad** card inside the LG webOS Smart TV dashboard with **Snap Now**, **Auto-Refresh (`2s`)** (automatically paused when navigated away), **Click-to-Click** directly on the live TV preview image (with cyan target feedback), **Open in TV Browser** URL launcher, **Center Cursor** recalibration, and a laptop-style **Virtual Trackpad** supporting drag-to-move, tap-to-click, right-click Back, and mouse-wheel scrolling.
- **Unified LG webOS TV Computer-Use MCP Tool (`tv_computer`)**:
  - Added live TV screen capture (`ssap://tv/executeOneShot`) returning native MCP `image` content blocks (`image/jpeg`) alongside structured TV state (`foregroundApp`, `inputs`, `installedApps`, estimated cursor position), with cursor-wake micro-wiggle so the pink Magic Remote pointer is visible in screenshots.
  - Added Magic Remote pointer control (`move` with drag support, corner-homed chunked `move_to` & `click` on a `0..1000` normalized grid, `scroll`), LG webOS Web Browser launcher (`open_url` via `ssap://system.launcher/open`), `launch_app` (with YouTube `content_id`), `close_app`, `button` (with `repeat`), `type` (with `replace`/`press_enter`), `backspace`, `wait`, and single-round-trip `batch` + `screenshot_after: true`.
  - Extended `PluginActionResult` in `Denon.App.PluginSdk` with optional `DataJson`, `ImageBase64`, and `ImageMimeType` for multimodal plugin-to-MCP responses.

### Changed
- **Consolidated LG TV MCP Controls into `tv_computer`**: Replaced separate `tv_remote_button`, `tv_type_text`, and `tv_cast_youtube` tools with the single unified `tv_computer` tool (while keeping `screenshot` / `get_state` strictly read-only in `Observer` permission tier).

## [1.9.0] - 2026-11-22

### Added
- **2026 MCP Protocol Upgrades (Tool Annotations, Typed Structured Outputs & Resource Templates)**:
  - Added full MCP `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) across all core and auto-bridged plugin tools so AI clients like Antigravity, Claude, and Cursor know which calls are safe to auto-run vs. power-off or queue-destructive.
  - Added typed `outputSchema` definitions and dual `structuredContent` + backwards-compatible text `content` responses on core query & intelligence tools (`get_room_state`, `diagnose_room`, `get_listening_habits`, `apply_atmosphere`, `avr_get_acoustics`, `media_get_now_playing_lyrics`, `media_get_queue`, `media_search_radio`, `media_search_youtube`, `list_scenes_and_rules`).
  - Added live **Resource Templates** (`denon://plugins/{pluginId}`, `denon://scenes/{sceneId}`), **Argument Completions** (`completion/complete`), **Resource Subscriptions** (`resources/subscribe`, `resources/unsubscribe`), and `logging/setLevel` for full MCP Inspector compatibility.
- **8 New Curated Living-Room MCP Tools**:
  - `avr_get_acoustics`: Read live Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Compressed Audio Restorer, Front Panel Dimmer, Subwoofer/Dialog trims, and Tone Control over Telnet `:23`.
  - `media_get_now_playing_lyrics`: Fetch synced/plain lyrics via LRCLIB for the currently playing track (or an explicit `title`/`artist`).
  - `media_get_queue` & `media_manage_queue`: Inspect and control the YouTube Up-Next playback queue (`clear`, `toggle_shuffle`, `cycle_repeat`, `remove`, `play_index`, `next`, `previous`).
  - `hue_set_color`: Set Philips Hue lights to any custom hex color (`#RRGGBB`), white warmth in Kelvin (`2000–6500K`), and brightness (`1–100%`), optionally targeting a specific Hue room/zone (`hue.set_color` plugin action).
  - `tv_cast_youtube`, `tv_remote_button`, `tv_type_text`: Direct LG webOS 2.0 YouTube casting, Magic Remote button presses, and TV keyboard text entry over the persistent SSAP pointer socket.

### Fixed
- **MCP Inspector `resources/templates/list` Error**: Implemented `resources/templates/list` and companion protocol methods so MCP Inspector loads all tabs cleanly with zero errors.

## [1.8.1] - 2026-11-22

### Fixed
- **Persist Auto-Generated MCP Bearer Token on First Boot**: `AppSettingsStore` now immediately writes newly initialized MCP settings (including the generated `BearerToken`) to `settings.json` when upgrading from older versions so the token never rotates across restarts.

## [1.8.0] - 2026-11-22

### Added
- **Streamable HTTP MCP Server (`POST /mcp` + SSE, 2025-11-25 Spec) & Always-On Living-Room Brain**:
  - Added `DenonMcpHttpServer` hosted natively on `http://127.0.0.1:18926/mcp` (localhost-only default, optional LAN bind, auto-generated `ddn_...` Bearer token, 3 Permission Tiers: `Observer` / `SafeControl` / `FullOrchestration`, and a hard `MaxSafeVolume` ceiling enforced on every volume mutation).
  - Exposes **4 Live MCP Resources** (`denon://room/state`, `denon://diagnostics/health`, `denon://catalog/capabilities`, `denon://memory/habits`), **4 Expert Prompts** (`design_atmosphere`, `diagnose_home_theater`, `acoustic_advisor`, `architect_automation`), **22 Core Intelligence/AVR/Acoustic/Media/Scene/Rule Tools** (including semantic `apply_atmosphere`, `diagnose_room`, `fix_room_issue`, `restore_previous_state`, `create_or_update_scene`, and `create_automation_rule`), plus **zero-config auto-bridging** of every enabled `IDenonPlugin` action (Philips Hue, LG webOS TV, Home Assistant, and drop-in `.dll` plugins).
  - Added `RoomIntelligenceService` (**Self-Healing Room Doctor** detecting cross-device mismatches like AVR muted while playing, late-night loud volume, LG TV video playing while Denon isn't on TV eARC, and OLED burn-in protection during audio-only playback, with 1-click Auto-Fix + **Room State Snapshot Undo** + 100% local **Habits Memory** in `room-habits.json`).
  - Added the **MCP & Room Brain** studio dashboard (`Views/McpIntelligenceView.xaml`), a live **`MCP :18926`** status pill in the Title Bar, a zero-clutter **Room Brain Insight Strip** on Control Deck (only shown when actionable), 1-click **Copy MCP Config JSON**, a live MCP Activity Feed, and Guide #12 in the built-in Help & Guide system.

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

[Unreleased]: https://github.com/ThomasRohde/DENON/compare/v1.11.1...HEAD
[1.11.1]: https://github.com/ThomasRohde/DENON/compare/v1.11.0...v1.11.1
[1.11.0]: https://github.com/ThomasRohde/DENON/compare/v1.10.1...v1.11.0
[1.10.1]: https://github.com/ThomasRohde/DENON/compare/v1.10.0...v1.10.1
[1.10.0]: https://github.com/ThomasRohde/DENON/compare/v1.9.0...v1.10.0
[1.9.0]: https://github.com/ThomasRohde/DENON/compare/v1.8.1...v1.9.0
[1.8.1]: https://github.com/ThomasRohde/DENON/compare/v1.8.0...v1.8.1
[1.8.0]: https://github.com/ThomasRohde/DENON/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/ThomasRohde/DENON/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/ThomasRohde/DENON/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/ThomasRohde/DENON/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/ThomasRohde/DENON/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/ThomasRohde/DENON/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/ThomasRohde/DENON/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/ThomasRohde/DENON/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ThomasRohde/DENON/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ThomasRohde/DENON/releases/tag/v1.0.0

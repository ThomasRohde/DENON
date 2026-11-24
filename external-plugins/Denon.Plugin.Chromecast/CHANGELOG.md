# Changelog — Chromecast & Google Cast plugin

Released separately from Denon Desktop with tags `chromecast-vX.Y.Z`.

## [Unreleased]

### Changed
- **Follows live theme switches** (Denon Desktop 2.0): the page reads theme brushes at each use instead of caching them, so elements made after a switch get the new theme's colors.
- **Follows the host skin** (Denon Desktop 2.0: Default, Hi-Fi, Compact, Large): font sizes and corner radii are live references to the host tokens, and margins, padding and sizes scale with the skin (`HostSkin`). No Plugin SDK change.

## [0.4.0] - 2026-11-23

Needs Denon Desktop **1.21.0** or later (Plugin SDK 2.0).

### Removed
- The fallbacks for older Denon Desktop versions (missing `IsAppShuttingDown` or receiver control).

### Changed
- **Follows the Denon Desktop theme**: The dashboard reads its colors from the app's theme tokens (cards, text levels, borders, status colors, the dark combo box) and uses the app's own styles for buttons (`PrimaryButton`, `SecondaryButton`), checkboxes and the automation expander (`DarkExpanderStyle`), and the app's type and corner-radius scale, so it looks like the rest of the app and follows future themes.

## [0.3.0] - 2026-11-23

Works with Denon Desktop **1.16.0** or later. With **1.17.0** sessions also end with `app_shutdown` on app exit, and the now-playing hero shows the cast program.

### Changed
- **Status shows what plays**: While a device has an app, the plugin status (Plugins card, and Denon Desktop's now-playing hero from app 1.17) is `CASTING • <device> • <app> • <title>` or `CAST PAUSED • …` instead of "1 Cast device(s) connected.".
- **Sessions end with `reason: "app_shutdown"` when Denon Desktop exits** (app 1.17+, SDK `IsAppShuttingDown`), and with `plugin_stopped` only when the plugin itself is stopped or disabled. On app 1.16 the plugin still loads and falls back to `plugin_stopped`.
- **Long pauses end the session**: A cast session that stays paused for 10 minutes ends with `reason: "paused"`. When the program plays again, a new session starts (after 15 s of play). Before, a whole evening of pause and resume was one long session.

## [0.2.0] - 2026-11-23

Needs **Denon Desktop 1.16.0** or later (Plugin SDK 1.16). Use 0.1.1 with Denon Desktop 1.15.x.

### Added
- **MCP-ready actions**: Every action declares its parameters (`device`, `level`, `host`, `name`, `includeImage`) and safety hints. MCP clients now see the real input schema; `now-playing` and `list-devices` are read-only (allowed in the Observer tier), `stop` and `quit-app` are destructive (need FullOrchestration).
- With Denon Desktop 1.16, `plugin_chromecast_now_playing` returns the artwork image and the device state as structured JSON over MCP.

## [0.1.1] - 2026-11-23

### Fixed
- **No session for a program that never plays**: The 15 s dwell now starts at the first PLAYING/BUFFERING. A program that opens paused (or stays paused) no longer logs a 10-minute session with `watchedSec: 0`.
- **Netflix and similar apps**: A media title that is only the app name ("Netflix") now gives `hasTitle: false`. Netflix does not share the program title over Cast.
- **Plugin version** is now read from `<Version>` in the `.csproj` (the manifest said 0.1.0 by hand).

## [0.1.0] - 2026-11-23

First release. Needs **Denon Desktop 1.15.0** or later (Plugin SDK 1.15, Receiver Control API).

### Added
- **Discovery**: mDNS (`_googlecast._tcp`) at start and every 10 minutes, plus "Add by IP". Private-LAN addresses only.
- **Live state and control** over Cast v2 (TLS `:8009`): app, title, artist, artwork, player state, volume; play, pause, stop, quit app, volume, mute.
- **Habit watch log** on the `media` stream: one `session.started` / `session.ended` per program (`kind: "cast"`, title, app, `watchedSec` without pauses, Denon input), plus `cast.app_ended` summaries. Clean app names (for example DR TV).
- **Denon automations** per device (all off by default): power on, Smart Scene, input and sound mode when an app starts; idle power-off; pause the device when the Denon powers off.
- **15 actions** for Smart Scenes, hotkeys, the phone remote and MCP.
- **Dashboard** in the Plugins view, dark-themed, built in code (no XAML).

# Changelog — Chromecast & Google Cast plugin

Released separately from Denon Desktop with tags `chromecast-vX.Y.Z`.

## [0.7.0] - 2026-11-28

### Fixed
- **A device added by IP stays connected when discovery finds it** (#49). mDNS gives it the device's own id; the
  plugin now stops the old connection and closes its watch session under the old id, then connects under the new
  one. Before, the card showed the device as disconnected for good, commands said "The device is not active", and the
  old connection stayed open.
- **A paused program is not logged as watched** (#70). One second of play and then a pause no longer becomes a watch
  session after 15 s; a paused program needs 15 s of play first. A session for a device id in other letter case now
  ends too, and the dashboard finds its card in any case.
- **Media commands after the idle screen** (#70): when the device goes back to Backdrop, the plugin forgets the media
  channel of the app that ran before.
- **Cleaner disconnects and stops** (#70). Closing a half-open connection waits at most 1 s for the CLOSE message; a
  connect that fails half-way frees its socket, TLS stream and timers. Stopping the plugin cancels pending playback
  events, and a restart frees the old stop signal.
- **mDNS parsing** (#70): a name with a reserved label type (first bits 01 or 10) is refused and the rest of that
  packet is skipped.

### Security
- **Artwork from a Cast device is checked** (#70). `now-playing` fetches the image only over http or https, never from
  this PC (loopback) or a link-local address such as the 169.254.169.254 metadata service, also not by a redirect or
  a name that resolves there. It stops reading at 2 MB instead of loading the whole body first, and the whole fetch
  times out after 5 s.

### Added
- **A test project for the plugin** (#59): `Denon.Plugin.Chromecast.Tests` covers the Cast message codec, the mDNS
  parser (with malformed packets), the private-LAN check, the watch log, app names, the id change on discovery and the
  artwork checks. CI and the release workflow run it.

## [0.6.0] - 2026-11-27

Use it with Denon Desktop **2.10.12** or later: the LG TV plugin there calls `wake-tv`.

### Added
- **Wake the TV (HDMI-CEC)** (`wake-tv`): starts the Cast default receiver, so HDMI-CEC turns on the TV and selects
  the Chromecast's input. It does nothing when an app already runs. The LG TV plugin uses it when Wake-on-LAN gets
  no answer, and closes the app with `quit-app` after.

## [0.5.1] - 2026-11-26

### Fixed
- **Playback changes are written only when they hold.** `cast.playback_changed` is written when the new state holds
  for 5 s, and BUFFERING counts as PLAYING (the event says PLAYING). A stream that stutters (PLAYING → BUFFERING →
  PLAYING) writes nothing; before, one evening of Prime Video wrote 134 events, 86 of them within 5 s of the last.
- **The first state after a connect is not a change**: an app start no longer writes a playback event.
- **Discovery is logged only when something changed** (a new device, a moved one, or a different count). Before, every
  run wrote "mDNS discovery: 1 device(s) answered, 0 new."

## [0.5.0] - 2026-11-24

Needs Denon Desktop **1.21.0** or later. The live theme and skin switches come with Denon Desktop 2.0; with older versions the page keeps the Dark colors and the Default sizes, as before.

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

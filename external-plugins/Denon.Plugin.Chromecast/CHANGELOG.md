# Changelog — Chromecast & Google Cast plugin

Released separately from Denon Desktop with tags `chromecast-vX.Y.Z`.

## [0.1.0] - 2026-11-23

First release. Needs **Denon Desktop 1.15.0** or later (Plugin SDK 1.15, Receiver Control API).

### Added
- **Discovery**: mDNS (`_googlecast._tcp`) at start and every 10 minutes, plus "Add by IP". Private-LAN addresses only.
- **Live state and control** over Cast v2 (TLS `:8009`): app, title, artist, artwork, player state, volume; play, pause, stop, quit app, volume, mute.
- **Habit watch log** on the `media` stream: one `session.started` / `session.ended` per program (`kind: "cast"`, title, app, `watchedSec` without pauses, Denon input), plus `cast.app_ended` summaries. Clean app names (for example DR TV).
- **Denon automations** per device (all off by default): power on, Smart Scene, input and sound mode when an app starts; idle power-off; pause the device when the Denon powers off.
- **15 actions** for Smart Scenes, hotkeys, the phone remote and MCP.
- **Dashboard** in the Plugins view, dark-themed, built in code (no XAML).

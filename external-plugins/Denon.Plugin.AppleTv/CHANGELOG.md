# Changelog — Apple TV plugin

Released separately from Denon Desktop with tags `appletv-vX.Y.Z`.

## [0.1.0] - 2026-11-23

First release. Works with Denon Desktop **1.16.0** or later. From **1.20.0** the now-playing hero shows the Apple TV program and its play/pause button controls the Apple TV.

Native .NET: no Python, no sidecar. The protocols are ported from pyatv (MIT); see `THIRD-PARTY-NOTICES.md`.

### Added
- **Discovery** of Apple TVs on the LAN (DNS-SD), at start and every 10 minutes. Name, model, tvOS, ports and moved IP addresses are kept current.
- **Pairing from the dashboard**: press **Pair**, type the PIN the TV shows. One pairing gives both the Companion link and the AirPlay now-playing channel (tvOS 26). The credentials stay in the plugin's config on this PC.
- **Live state**: power (awake, screensaver, asleep), the app, the title, series/episode and play state, pushed by the Apple TV within about a second.
- **Wake-safe design**: only the light Companion link stays open. The now-playing channel opens only while the Apple TV is awake and closes when it sleeps.
- **Remote control**: play, pause, play/pause, remote buttons, launch app (bundle id or URL), wake and sleep.
- **MCP-ready actions** with parameters and safety hints: `list-devices`, `now-playing` and `list-apps` are read-only; `power-off` is destructive.
- **Watch log** on the `media` stream: `session.started` / `session.ended` with `kind: "appletv"` (15 s dwell, `watchedSec` counts only play time, 10-minute pause timeout), plus `appletv.app_ended` per app run. Denon Desktop 1.20 counts these sessions once with the TV and the receiver (`exclusiveSec`).
- **Denon automations** per Apple TV (all off by default): on wake power on the receiver, run a Smart Scene, select an input and a sound mode; power off the receiver N minutes after the Apple TV sleeps (only while the receiver is still on that input); pause the Apple TV when the receiver powers off.
- **Status line** for the Plugins card and the host's now-playing hero: `APPLE TV • <device> • <app> • <title>` or `APPLE TV PAUSED • …`.

### Known limits
- No artwork yet (needs an MRP playback-queue request). Some apps (DRTV) send no duration or position.

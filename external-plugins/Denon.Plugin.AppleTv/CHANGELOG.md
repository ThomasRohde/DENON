# Changelog — Apple TV plugin

Released separately from Denon Desktop with tags `appletv-vX.Y.Z`.

## [Unreleased]

### Changed
- **BouncyCastle 2.7.0** (#72), the same version as the Hue plugin in Denon Desktop (it was 2.5.1). The package
  versions now come from the repository's `Directory.Packages.props`.

## [0.6.0] - 2026-11-28

### Security
- **Bad network data can no longer crash Denon Desktop or stall the link** (#35). The OPACK reader stops at 64
  nested levels (a few KB of nested containers in a Companion frame ended the whole app with a stack overflow) and
  rejects negative references and lengths past the data. The binary plist reader checks its trailer, offsets and
  lengths before it allocates (a 40-byte file could ask for gigabytes), caps the objects it decodes, and fails with a
  format error. A data-stream frame smaller than its header or over 16 MB, a negative or huge HTTP Content-Length,
  and a protobuf length past the data now close the connection (it reconnects) instead of stalling it.
- **Pairing fails when the Apple TV's signature does not verify** (#35). The M6 signature was checked, but the
  pairing was saved anyway. A low-order X25519 session key (an all-zero secret) is refused.

### Fixed
- **Now playing comes back when the link drops right after it opens** (#50). A close before the plugin listened was
  missed, and now playing stayed dead until the Apple TV slept. A request in flight fails at once when the link
  closes, instead of after 5 seconds.
- **A wrong PIN refreshes the Apple TV page** (#70): the PIN box no longer stays on a pairing that has ended.
- **One damaged saved pairing no longer stops the other Apple TVs** (#70). The log names the Apple TV to pair again.
- **Wake automation stops when the Apple TV sleeps again** (#70), and the plugin no longer asks a sleeping Apple TV
  for its queue and artwork when it fell asleep while now playing connected.
- **Companion encryption starts in step with the reader** (#70): the first encrypted frame is never read as plain
  text. A frame over 16 MB is refused instead of being cut to the 24-bit length.
- **Insights: a second of play and then a pause is no session** (#70); it needs 15 seconds of play. Ending the
  sessions of one Apple TV matches its id in any case.
- **Discovery: an empty TXT string no longer hides the entries after it** (#70), and a reserved DNS label type
  drops the packet instead of misreading the rest of it.
- Connections and timers are cleaned up when a remote-control setup fails part-way or the plugin restarts, and a
  send lock is no longer disposed while a send may still release it (#70).

## [0.5.1] - 2026-11-26

### Fixed
- **Discovery is logged only when something changed** (a new Apple TV, a moved one, or a different count). Before,
  every run wrote "Apple TV discovery: 1 answered, 0 new."

## [0.5.0] - 2026-11-24

Needs Denon Desktop **2.5.0** or later. Older versions do not read the `APPLE TV LIVE` status and drop a live program from the now-playing hero.

### Added
- **Live programs that cannot pause** (for example DRTV live): the plugin reads the Apple TV's supported commands. While a program plays and the player does not list Pause, the status reads `APPLE TV LIVE • …`, the snapshot has `canPause: false`, and the Pause button on the Apple TV page is greyed out with a tooltip. Only an explicit Pause command counts, because the app sends Pause.

### Changed
- **Standby is logged as standby:** the remote's power button stops playback about 30 seconds before tvOS reports sleep. The watch log now waits up to 35 seconds after an app closes, so the session ends with `standby` instead of `app_closed`. A late session end carries `endedAt`, so Insights counts the real end time.

## [0.4.0] - 2026-11-24

Needs Denon Desktop **1.22.0** or later (artwork). The live theme and skin switches come with Denon Desktop 2.0; with older versions the page keeps the Dark colors and the Default sizes, as before.

### Changed
- **Follows live theme switches** (Denon Desktop 2.0): the page reads theme brushes at each use instead of caching them, so elements made after a switch get the new theme's colors.
- **Follows the host skin** (Denon Desktop 2.0: Default, Hi-Fi, Compact, Large): font sizes and corner radii are live references to the host tokens, and margins, padding and sizes scale with the skin (`HostSkin`). No Plugin SDK change.

## [0.3.0] - 2026-11-23

Needs Denon Desktop **1.22.0** or later to show the artwork.

### Added
- **Artwork**: when the app has artwork for the program, the plugin asks the Apple TV for it (PLAYBACK_QUEUE_REQUEST, as pyatv does), saves it in the plugin data folder (the newest 30 images per Apple TV) and gives it to the now-playing hero.

### Fixed
- **DRTV autoplay**: when an app goes on to the next program by itself, tvOS sometimes names no player until you pause. While the Apple TV is awake and nothing is reported, the plugin now asks for the playback queue once a minute, and shows a player that starts to play without being named.

## [0.2.0] - 2026-11-23

Needs Denon Desktop **1.21.0** or later (Plugin SDK 2.0).

### Removed
- The fallbacks for older Denon Desktop versions (missing `IsAppShuttingDown` or receiver control).

### Changed
- **Follows the Denon Desktop theme**: The dashboard reads its colors from the app's theme tokens (cards, text levels, borders, status colors, the dark combo box) and uses the app's own styles for buttons (`PrimaryButton`, `SecondaryButton`), checkboxes and the automation expander (`DarkExpanderStyle`), and the app's type and corner-radius scale, so it looks like the rest of the app and follows future themes.

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

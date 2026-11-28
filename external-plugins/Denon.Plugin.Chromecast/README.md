# Chromecast & Google Cast plugin (external)

A standalone Denon Desktop plugin. It is **not** part of `DenonDesktop.slnx` and is not shipped
with the app. It is the reference example of an external drop-in plugin.

## What it does

- **Finds Cast devices** with mDNS (`_googlecast._tcp`) at start and every 10 minutes. You can also add a device by IP.
- **Shows the live state** of each device: app, title, artist, artwork, player state, volume.
- **Controls the device** over Cast v2 (TLS on port 8009): play, pause, stop, quit app, volume, mute.
- **Drives the Denon receiver** (per device, all off by default):
  - when an app starts: power on, run a Smart Scene, select an input, select a sound mode;
  - when the device is idle for N minutes: power off the receiver (only if it is still on that input);
  - when the receiver powers off: pause the device.
- **Writes habit-ready watch sessions** to the `media` stream (see below).

The ambient screen (Backdrop) does not count as a session. A reconnect does not count as a new session.

## Habit telemetry (`media` stream)

The main purpose of this plugin is to log *what* you cast, not only "HDMI input on for 5 hours".
Watch sessions use the same event names and keys as the host's AVR and LG TV sessions, so the habit
system reads them without changes.

| Event | When | Key fields |
|---|---|---|
| `session.started` | A program plays for 15 s (same dwell as the host) | `kind: "cast"`, `title`, `source` (app), `artist`, `castDevice`, `receiverInput`, `receiverSoundMode` |
| `session.ended` | Title changes, app closes, device drops, plugin stops | as above + `durationSec`, `watchedSec`, `avgVolume`, `reason` |
| `cast.app_ended` | An app run ends (15 s or longer) | `app`, `durationSec`, `watchedSec`, `titles[]`, `receiverInput`, `reason` |
| `cast.app_started`, `cast.playback_changed` | State changes | `app`, `playerState`, `title` |
| `cast.connected`, `cast.disconnected` | Device link changes | `castDevice` |
| `automation.*` | The plugin changed the receiver | `steps` |

Rules:
- **One session per program.** A new title closes the old session (`reason: "switched"`). A session that stays
  paused for 10 minutes ends (`reason: "paused"`); playing again starts a new session.
  If the title arrives a moment after the app starts, the open session gets the title.
- **`watchedSec` counts only PLAYING/BUFFERING time.** `durationSec` is wall-clock time, so a long pause shows as a gap.
- `avgVolume` is the Denon volume during the session. `receiverInput` lets you join a Cast session to the
  AVR's HDMI session for the same time span.
- **No double counting.** Denon Desktop adds `coveredSec`, `exclusiveSec` and `coveredBy` to every
  `session.ended` on the `media` stream. While you cast, the LG TV session on the HDMI input and the Denon
  "TV Audio" session get `coveredBy: ["cast:<device>"]` for that time. Sum `exclusiveSec` (not `durationSec`)
  to get real watching time; use the cast session's `title`/`source` to see *what* was watched.
- `hasTitle: false` means the app does not report titles (for example Netflix). Then `title` is the app name.

Example:

```json
{"event":"session.ended","kind":"cast","title":"Lecture A","source":"YouTube","artist":"MIT OCW",
 "castDevice":"Stuen 2","receiverInput":"MPLAY","durationSec":920,"watchedSec":601,"avgVolume":45,"reason":"switched"}
```

## Actions

Use these in Smart Scenes, hotkeys, the phone remote or MCP. The `device` parameter is optional:
the plugin picks the device that plays, then the device with an active app, then the only device.

**MCP:** each action is the tool `plugin_chromecast_<action>` (for example `plugin_chromecast_now_playing`).
The tools show their real parameters and safety hints: `now-playing` and `list-devices`
are read-only (allowed in the Observer tier); `stop` and `quit-app` are destructive (need FullOrchestration).
`now-playing` returns the artwork as an image and the device state as structured JSON.

| Action | Parameters |
|---|---|
| `list-devices`, `discover` | – |
| `add-device` | `host` (private IPv4), `name` (optional) |
| `now-playing` | `device`, `includeImage` (`false` to skip artwork) |
| `play`, `pause`, `toggle-play`, `stop`, `quit-app` | `device` |
| `pause-all` | – |
| `set-volume` | `level` (0–100), `device` |
| `volume-up`, `volume-down`, `mute`, `unmute` | `device` |

## Requirements

- The Denon Desktop release that matches this plugin release (same Plugin SDK).
- The PC and the Cast devices must be on the same LAN. Windows Firewall must allow inbound UDP
  for Denon Desktop, or mDNS answers do not arrive. "Add by IP" works without mDNS.
- **Windows Private vs. Public network (mDNS):** Windows often marks a Wi-Fi or Ethernet connection as a
  **Public** network by default. On a Public profile, Windows Firewall blocks inbound mDNS (UDP 5353)
  multicast replies unless an inbound rule allows them (neither the Denon Desktop installer nor the app adds one).
  If **Discover now** finds no devices, switch your home network to **Private**:
  open **Settings → Network & internet → Wi-Fi** (or **Ethernet**) → select your network → set
  **Network profile type** to **Private network**, then click **Discover now** again.

## Install

**From a release (no SDK needed):** the plugin has its own GitHub releases, tagged `chromecast-vX.Y.Z`.

1. Download `Denon.Plugin.Chromecast-<version>.zip` from the latest `chromecast-v*` release.
2. Unzip it to `%LocalAppData%\DenonDesktop\plugins\external\Chromecast\`.
3. Restart Denon Desktop, then open **Plugins → Chromecast & Google Cast**.

**From source:**

```powershell
pwsh ./external-plugins/Denon.Plugin.Chromecast/install.ps1
```

The script publishes the plugin and copies it to
`%LocalAppData%\DenonDesktop\plugins\external\Chromecast\`. Close Denon Desktop first when you update
the plugin, because the loaded DLL is in use.

**Release a new version:** set `<Version>` in `Denon.Plugin.Chromecast.csproj`, then push a tag
`chromecast-v<same version>`. The `release-chromecast-plugin.yml` workflow builds the zip and publishes the release.
The SDK reference is compile-only (`Private=false`, `ExcludeAssets=runtime`), so the plugin uses the host's
`Denon.App.PluginSdk.dll`. Build and test with `external-plugins/Denon.Plugin.Chromecast.slnx`; the tests are in
`Denon.Plugin.Chromecast.Tests`.

## Security

- The plugin connects only to private LAN addresses (10/8, 172.16/12, 192.168/16, 169.254/16).
  It accepts the Cast device's self-signed certificate only after this check.
- Artwork for `now-playing` comes only over http or https, never from this PC or a link-local address (also not
  by a redirect), and at most 2 MB is read.
- Every receiver command is logged by the host with the plugin id.

## Limits (version 0.1)

- Speaker groups (`Google Cast Group`) are skipped.
- Netflix shares only its own name as the media title, so a Netflix session is per app run, not per episode
  (`hasTitle: false`). Its play state is reported, so `watchedSec` is correct.
- A program counts as a session only after it has played for 15 s; a program that stays paused is not logged.

## Code map

| File | Purpose |
|---|---|
| `Cast/CastMessage.cs` | Cast v2 protobuf frame codec (no protobuf dependency) |
| `Cast/CastChannel.cs` | TLS connection, framing, heartbeat, request/response |
| `Cast/CastDeviceClient.cs` | One device: reconnect, receiver and media status, commands |
| `Cast/MdnsCastDiscovery.cs` | Minimal mDNS browser (PTR, SRV, TXT, A) |
| `Cast/CastAppNames.cs` | Clean app names by Cast app id (for example DR TV) |
| `Cast/CastArtwork.cs` | Checked, size-limited artwork fetch for `now-playing` |
| `CastWatchLog.cs` | Habit watch sessions (`session.started` / `session.ended`, `cast.app_ended`) |
| `ChromecastConfig.cs` | Per-device settings and automation config |
| `ChromecastPlugin.cs` | `IDenonPlugin`: actions, events, automations, config |
| `ChromecastDashboardView.cs` | Dashboard UI, built in code (no XAML) |

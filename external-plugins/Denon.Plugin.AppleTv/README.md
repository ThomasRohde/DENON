# Apple TV plugin for Denon Desktop

Native .NET plugin: no Python, no sidecar. It is built in milestones, each with a go/no-go.

| Milestone | Content | State |
|---|---|---|
| 0. Discovery | DNS-SD browse of `_companion-link`, `_airplay`, `_raop`, `_mediaremotetv`, `_device-info`; group per device; model, tvOS, ports | Done |
| 1. Pairing + Companion | HAP pair-setup with the PIN on the TV (SRP-6a, Ed25519, X25519, ChaCha20-Poly1305, HKDF), pair-verify, encrypted Companion session (OPACK): power state, power on/off, remote keys, app list and launch | Done: paired live with tvOS 26.6.1; power state, app list and events verified |
| 2. Now playing (MRP) | Encrypted AirPlay 2 channel → MRP protobuf messages: app, title, artwork, play state, push updates. **Go/no-go for the plugin.** | **GO**: live on tvOS 26 with the Companion pairing (no second PIN); app, title and play state, pushed live |
| 3. Plugin | Hero, watch log (`session.*`, `kind: "appletv"`), `get_watch_time`, Denon automations, MCP tools, dashboard, release | **Success**: live on tvOS 26 (pairing, hero, play/pause, sessions, sleep without wake) |

## Install and use

1. Unzip the release to `%LocalAppData%\DenonDesktop\plugins\external\AppleTv\`, or build and copy it with
   `pwsh ./external-plugins/Denon.Plugin.AppleTv/install.ps1`. Close Denon Desktop first when you update.
2. Open **Plugins → Apple TV** and press **Discover now**.
3. Press **Pair** on the Apple TV card. The TV shows a 4-digit PIN: type it and press **Send PIN**.
4. Open **Settings and Denon automation** on the card to choose what the receiver does when the Apple TV wakes or sleeps.

To remove the pairing on the Apple TV too: Settings › Remotes and Devices › Remote App and Devices.

### Actions (MCP tools, Smart Scenes, hotkeys)

| Action | Parameters | Notes |
|---|---|---|
| `list-devices` | — | Read-only. All Apple TVs with pairing, power and now-playing state. |
| `now-playing` | `device` | Read-only. App, title, series/episode, play state. |
| `list-apps` | `device` | Read-only. Bundle ids and names. |
| `discover` | — | Runs discovery now. |
| `play`, `pause`, `play-pause` | `device` | |
| `key` | `button`, `device` | up, down, left, right, select, menu, home, playpause, volumeup, volumedown, … |
| `launch-app` | `app`, `device` | Bundle id (see `list-apps`) or URL. |
| `power-on` | `device` | Wakes the Apple TV. The TV can turn on through HDMI-CEC. |
| `power-off` | `device` | Destructive: the Apple TV sleeps and playback stops. |

`device` is optional: the default is the Apple TV that plays, then the only connected one.

### Status line

`APPLE TV • <device> • <app>[ • <title>]` or `APPLE TV PAUSED • …` while an app shows a program. Denon Desktop 1.20
reads this line for the now-playing hero. Keep the format stable.

### Watch sessions

`session.started` / `session.ended` on the `media` stream with `kind: "appletv"`, `title`, `app`, `appId`,
`appleTvDevice`, `appleTvModel`, `hasTitle`, `subtitle`, `season`/`episode`, `receiverInput`, `durationSec`,
`watchedSec`, `avgVolume` and `reason` (`switched`, `app_closed`, `standby`, `paused`, `disconnected`,
`plugin_stopped`, `app_shutdown`, `device_disabled`). One `appletv.app_ended` summary per app run.

## Build and probe

```powershell
dotnet build external-plugins\Denon.Plugin.AppleTv.slnx
dotnet test external-plugins\Denon.Plugin.AppleTv.Tests
external-plugins\Denon.Plugin.AppleTv.Probe\bin\Debug\net10.0-windows\atvprobe.exe discover --all --txt
```

`atvprobe discover` only sends DNS-SD queries; it never pairs with, wakes or controls a device. Use `--host <ip>` to also
ask a known device directly (useful when a firewall drops multicast answers).

```powershell
atvprobe pair   --host <ip> [--name "Denon Desktop"] [--pin-file <path>]   # the Apple TV shows a PIN
atvprobe status --host <ip>        # read-only: power state
atvprobe apps   --host <ip>        # read-only: launchable apps
atvprobe watch  --host <ip> [--seconds 30]   # read-only: pushed Companion events
atvprobe nowplaying --host <ip> [--seconds 30] [--dump]   # read-only: now playing over the AirPlay MRP tunnel
atvprobe airplay-pair --host <ip>   # separate AirPlay pairing (not needed on tvOS 26: Companion credentials work)
atvprobe key <button> | power <on|off> | launch <bundle-id>  --host <ip>   # change the Apple TV
```

Pairings are stored in `%LocalAppData%\DenonDesktop\appletv-probe\pairings.json` (pyatv-compatible credentials:
`ltpk:ltsk:atvId:clientId`). The file holds a private key; it is never in the repository.

### Notes from the port

- SRP follows srptools exactly where it is deterministic (H(g) over the single byte 0x05; k over PAD(g)); A, B and S
  are padded to the size of N (HomeKit style). Both agree except when a value starts with a zero byte (~1/256).
- OPACK `pack` does not put containers in the back-reference table, so it agrees with `unpack` (pyatv's `pack`
  does, and its own round trip can break after nested containers).
- Pair-setup M6: the Apple TV's signature is verified (pyatv leaves this as a TODO). tvOS 26.6.1 signs per HAP.
- AirPlay MRP tunnel: control connection + RTSP SETUP (event channel) + RECORD + SETUP (data stream type 130),
  POST /feedback every 2 s. Channel nonces = 4 zero bytes + 8-byte LE counter (Companion: counter first).
- Binary plists are written and read by an own codec, cross-checked both ways with Python's plistlib.
- Apps can have several MRP players: DRTV plays in an "avkit-…" player while "MediaRemote-DefaultPlayer" stays
  paused and empty. Follow SET_NOW_PLAYING_PLAYER; a newly named player without state counts as unknown.
- DRTV sends no duration or elapsed time over MRP. Artwork needs a PLAYBACK_QUEUE_REQUEST (not built yet).

## Protocol reference

The protocol logic follows [pyatv](https://github.com/postlund/pyatv) (MIT). See `THIRD-PARTY-NOTICES.md`.

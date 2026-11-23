# Apple TV plugin for Denon Desktop (work in progress)

Native .NET plugin: no Python, no sidecar. It is built in milestones, each with a go/no-go.

| Milestone | Content | State |
|---|---|---|
| 0. Discovery | DNS-SD browse of `_companion-link`, `_airplay`, `_raop`, `_mediaremotetv`, `_device-info`; group per device; model, tvOS, ports | Done |
| 1. Pairing + Companion | HAP pair-setup with the PIN on the TV (SRP-6a, Ed25519, X25519, ChaCha20-Poly1305, HKDF), pair-verify, encrypted Companion session (OPACK): power state, power on/off, remote keys, app list and launch | Done: paired live with tvOS 26.6.1; power state, app list and events verified |
| 2. Now playing (MRP) | Encrypted AirPlay 2 channel → MRP protobuf messages: app, title, artwork, play state, push updates. **Go/no-go for the plugin.** | Next |
| 3. Plugin | Hero, watch log (`session.*`, `kind: "appletv"`), `get_watch_time`, Denon automations, MCP tools, dashboard, release | Open |

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
atvprobe watch  --host <ip> [--seconds 30]   # read-only: pushed events
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

## Protocol reference

The protocol logic follows [pyatv](https://github.com/postlund/pyatv) (MIT). See `THIRD-PARTY-NOTICES.md`.

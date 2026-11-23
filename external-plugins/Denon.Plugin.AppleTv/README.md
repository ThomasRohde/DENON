# Apple TV plugin for Denon Desktop (work in progress)

Native .NET plugin: no Python, no sidecar. It is built in milestones, each with a go/no-go.

| Milestone | Content | State |
|---|---|---|
| 0. Discovery | DNS-SD browse of `_companion-link`, `_airplay`, `_raop`, `_mediaremotetv`, `_device-info`; group per device; model, tvOS, ports | Done |
| 1. Pairing + Companion | HAP pair-setup with the PIN on the TV (SRP-6a, Ed25519, X25519, ChaCha20-Poly1305, HKDF), pair-verify, encrypted Companion session (OPACK): power state, power on/off, remote keys, app list and launch | Next |
| 2. Now playing (MRP) | Encrypted AirPlay 2 channel → MRP protobuf messages: app, title, artwork, play state, push updates. **Go/no-go for the plugin.** | Open |
| 3. Plugin | Hero, watch log (`session.*`, `kind: "appletv"`), `get_watch_time`, Denon automations, MCP tools, dashboard, release | Open |

## Build and probe

```powershell
dotnet build external-plugins\Denon.Plugin.AppleTv.slnx
dotnet test external-plugins\Denon.Plugin.AppleTv.Tests
external-plugins\Denon.Plugin.AppleTv.Probe\bin\Debug\net10.0-windows\atvprobe.exe discover --all --txt
```

`atvprobe discover` only sends DNS-SD queries; it never pairs with, wakes or controls a device. Use `--host <ip>` to also
ask a known device directly (useful when a firewall drops multicast answers).

## Protocol reference

The protocol logic follows [pyatv](https://github.com/postlund/pyatv) (MIT). See `THIRD-PARTY-NOTICES.md`.

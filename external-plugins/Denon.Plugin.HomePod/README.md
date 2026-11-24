# HomePod plugin for Denon Desktop

Native .NET plugin: no Python, no sidecar, no pairing PIN. It shows what your HomePods play, controls them, and
logs listening time for Insights.

## What it does

- **Finds HomePods** on the LAN with DNS-SD (`_airplay._tcp`). A **stereo pair** is one speaker (for example
  "HomePod Left + Right"); the plugin talks to the pair's leader, which reports what plays on both.
- **Now playing, live:** the app (Apple Music, Podcasts, or the app that sends AirPlay, for example
  "AirMusic (AirPlay)"), the title, the artist, the album and the play state. The HomePod pushes changes.
- **Controls:** play, pause, play/pause, next, previous.
- **Listening sessions** on the `media` stream with `kind: "homepod"`: Insights counts them as listening time, next
  to the TV (a HomePod plays in its own room, so its time is never "covered" by the TV or the receiver).
- **Receiver automation (off by default):** pause the HomePods that play when the Denon turns on.

HomePods are not connected to the receiver, so the Control Deck's now-playing card does not show them.

## How it connects (no PIN)

A HomePod has no screen, so it cannot show a PIN. It accepts a **transient pairing** instead: HAP pair-setup
M1–M4 with the fixed PIN 3939 and the transient flag (`X-Apple-HKP: 4`), as pyatv does. The SRP session key then
encrypts the AirPlay 2 control connection, and the plugin opens the remote-control channel that carries MRP (the
Media Remote Protocol) — the same channel the Apple TV plugin uses for now playing. Nothing is stored: each
connection pairs again. The plugin reconnects by itself (after 5 s, doubling to 1 minute).

The protocol code (HAP, AirPlay 2, MRP, DNS-SD) is the Apple TV plugin's: this project compiles it in from
`../Denon.Plugin.AppleTv/Protocol` and `../Denon.Plugin.AppleTv/Discovery`, so it exists once in the repository.

## Install and use

1. Unzip the release to `%LocalAppData%\DenonDesktop\plugins\external\HomePod\`, or build and copy it with
   `pwsh ./external-plugins/Denon.Plugin.HomePod/install.ps1`. Close Denon Desktop first when you update.
2. Open **Plugins → HomePod**. The plugin looks for HomePods at the start and every 10 minutes; press
   **Discover now** to look at once.
3. Each speaker card shows what plays and has **Previous**, **Play / Pause** and **Next**. Untick
   **Linked by Denon Desktop** to leave a speaker alone.

### Actions (MCP tools, Smart Scenes, hotkeys)

| Action | Parameters | Notes |
|---|---|---|
| `list-devices` | — | Read-only. All speakers with link and now-playing state. |
| `now-playing` | `device` | Read-only. App, title, artist, album, play state. |
| `discover` | — | Runs discovery now. |
| `play`, `pause`, `play-pause`, `next`, `previous` | `device` | |

`device` is optional: the default is the speaker that plays, then the only linked one. It matches the speaker's
name, a member's name ("HomePod Left"), its id or its IP address.

### Status line

`HOMEPOD • <speaker> • <app> • <title> – <artist>` or `HOMEPOD PAUSED • …` while something plays or is paused;
otherwise `<n> of <m> HomePod speaker(s) linked • nothing plays.`

### Listening sessions

`session.started` / `session.ended` on the `media` stream with `kind: "homepod"`, `title`, `app`, `appId`,
`artist`, `album`, `homepodDevice`, `homepodModel`, `homepodSpeakers` (a stereo pair's members), `hasTitle`,
`durationSec`, `watchedSec` (time it played) and `reason` (`switched`, `stopped`, `paused`, `disconnected`,
`plugin_stopped`, `app_shutdown`, `speaker_disabled`). One session per song, episode or stream; the 15 s dwell starts
at the first Playing; a pause of 10 minutes ends the session. Also `homepod.connected` / `homepod.disconnected` and
`homepod.paused_by_automation`.

## Build and probe

```powershell
dotnet build external-plugins\Denon.Plugin.HomePod.slnx
dotnet test external-plugins\Denon.Plugin.HomePod.slnx
external-plugins\Denon.Plugin.AppleTv.Probe\bin\Debug\net10.0-windows\atvprobe.exe homepod --host <ip> --seconds 20
```

`atvprobe homepod` pairs transiently and prints what the HomePod plays. It reads only: it plays nothing and
changes nothing.

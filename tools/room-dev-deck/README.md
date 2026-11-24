# Room Dev Deck

A developer tool for Denon Desktop. It is a Claude artifact that you install as a desktop app (Claude desktop app,
artifact menu, **Install as app**). It is not part of the app and does not ship with it.

Published copy (private): https://claude.ai/artifact/Dw8Q9LHFajrqLbUKnAwnjU

`room-dev-deck.html` is the source. Edit it here, then publish it again to the same artifact.

## Modes

- **Room:** a timeline per day of the media sessions on each device (receiver, LG TV, Apple TV, Chromecast, each
  HomePod), app starts, app.log warnings and errors, link drops and off times. Also a live feed of `app.log` and the
  event files (`%LOCALAPPDATA%\DenonDesktop\logs`), checks for problems, and **Ask Claude** about a session or a span.
- **MCP:** the tools of the app's MCP server (`127.0.0.1:18926`), a form for the arguments, the result and a history.
  Read-only tools run with one click. Tools that change the room ask first.
- **LAN:** port checks for the devices in the plugin configs, and read-only `atvprobe` commands (it can build the probe).
  It does not open the receiver's Telnet port 23: the receiver takes one Telnet client, and the app holds it.
- **Release:** versions in the projects against the newest tags, the working tree, GitHub Actions runs (with an alert
  when one finishes), the `[Unreleased]` changelog, open TODO items, and a SHA-256 check of a release.

The tray icon shows green (quiet), blue (a GitHub run is going), amber (warnings in the last 30 min) or red (errors).

## Checks in Room mode

- A session that never ended (no `session.ended`), and why: replaced by a new session, or the app stopped.
- The app stopped without a clean shutdown (no "Application shutting down" line).
- Covered + exclusive time that does not add up to the played time (`watchedSec`, else `durationSec`).
- `coveredBy` names a device that had no session at that time.
- A session that kept counting more than 2 minutes after the receiver went to standby.
- A device that lost its link 3 or more times in 10 minutes (app restarts are left out).
- app.log warnings and errors, grouped.

## What it needs

The **full-access** permission (it reads files and runs `git`, `gh`, `curl.exe`, `powershell` and `atvprobe`). It
reads the MCP bearer token from `settings.json` for each call and writes it only to a temporary header file that it
deletes after the call. It never reads or shows pairing keys from the plugin configs.

## Test without installing

`harness.mjs` serves the page with a stand-in for the desktop API that uses the real files and commands:

```powershell
node tools/room-dev-deck/harness.mjs
```

Then open http://127.0.0.1:47811/. The harness runs commands that the page sends, so it listens on 127.0.0.1 only and
checks a random token. Stop it when you are done. The tray, notifications and Ask Claude work only in the installed app.

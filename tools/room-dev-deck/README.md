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
- **TODO:** the items in `TODO.md`, by section, with search. Pick one to see its full text, then **Start in Claude
  Code**: it opens Windows Terminal in the repository with a new `claude` session and a prompt you can edit first
  (the item, its line and section, and "update TODO.md and the CHANGELOG, run the tests, do not commit"). Choose how
  the session starts: **Plan first** (`--permission-mode plan`, the default), **Ask before edits** or **Accept edits**.
  The list follows `TODO.md` while you work, so a ticked or removed item shows at once.
  To pick several items, tick their boxes, Ctrl+click or Shift+click (a range), or use **Pick all** on a section.
  The prompt for one session then changes with the pick:
  - Items from one section: Claude looks for one shared change first.
  - Items from several sections: Claude keeps the changes separate.
  - Items that name the same code, file or class: the prompt names those links.
  - Ticked items: Claude only checks that nothing is left.
  - More than five items: Claude stops cleanly when its context gets full.

  In every case, Claude first proposes an order, then finishes and ticks one item at a time, and reports per item, so
  you can commit the items one by one. **How the prompt fits this pick** shows those choices before you start.

`TODO.md` stays Markdown. The deck reads it as it is: a `## ` heading is a section, and `- [ ]` / `- [x]` with the
lines indented under it is an item. An item's ID is a hash of its section and text, so keep that format.

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

The **full-access** permission (it reads files and runs `git`, `gh`, `curl.exe`, `powershell`, `atvprobe`, and `wt` with
`claude` for TODO mode). It
reads the MCP bearer token from `settings.json` for each call and writes it only to a temporary header file that it
deletes after the call. It never reads or shows pairing keys from the plugin configs.

## Test without installing

`harness.mjs` serves the page with a stand-in for the desktop API that uses the real files and commands:

```powershell
node tools/room-dev-deck/harness.mjs
```

Then open http://127.0.0.1:47811/. The harness runs commands that the page sends, so it listens on 127.0.0.1 only and
checks a random token. Stop it when you are done. The tray, notifications and Ask Claude work only in the installed app.

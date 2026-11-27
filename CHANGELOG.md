# Changelog

All notable changes to **Denon Desktop** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.10.10] - 2026-11-27

### Fixed
- **Hue Music Sync and the Deck meters work with internet radio.** A radio station on HEOS never passes through the
  PC, so Hue got silence and the meters were simulated (SIM). Now, when the app starts a stream (a station button, a
  Smart Scene's stream, a station from the directory, a custom URL or the MCP tools), FFmpeg decodes a copy of the same
  stream and Hue and the meters follow its real level, spectrum and beats. The clock starts when HEOS reports "play"
  for the new stream, so the beats are close to the speakers (HEOS buffers on its own connection, so not exact).
  After a pause the stream is decoded again from the live edge; when the connection drops it starts again while HEOS
  plays on. Spotify, TuneIn, AirPlay and other inputs stop it. A live stream keeps only the last 10 minutes.
- The Hue panel no longer says "LISTENING (PC AUDIO)" when it waits for audio: it says WAITING FOR AUDIO, and the
  status line names the sources Music Sync follows.

## [2.10.9] - 2026-11-27

### Added
- **Saved stations show their logo.** When the receiver plays a station that the app started (a preset, a shortcut,
  a station from the directory), the Deck and the phone remote show the station's logo in place of the empty disc.
  HEOS gives no picture for a stream that an app starts, so the app asks the TuneIn directory for the logo by the
  station's name (for example "DR P6 Beat"). It asks one time for each station, keeps the answer in
  `station-logos.json` in the app's data folder, and shows no logo when TuneIn has no station with that name.
  Only the station name goes to TuneIn.

### Changed
- **The plugin list is an even grid.** On the Plugins page the cards have the same width, fill the row, and have
  the same space between columns and between rows. The number of columns follows the window width and the skin
  size. A long list scrolls after about three rows, so the selected plugin's page stays in view.
- **The demo queue starts with the Denon Desktop promo video.** In screenshot mode (`--capture-screenshots`) the
  Up Next queue on the Music page now starts with "Denon Desktop: one app for the whole living room"
  (https://www.youtube.com/watch?v=2BL0w4gOYfA), then the first three search results.

## [2.10.8] - 2026-11-26

### Changed
- **Volume shows as the number on the receiver's display, without dB.** The MCP cards (now playing, Volume memory,
  Insights), the Volume memory page, the Insights page and the Insights notes show the 0–98 volume only, for example
  "Streaming apps play at volume 56 and live TV at 41: about 15 louder". The logs keep dB.

### Added
- **Volume memory says why it kept the volume.** When the source changes and the volume stays, the log says why, in the
  receiver's numbers: for example "DR2: left at 39, the learned 41 is within 3."

## [2.10.7] - 2026-11-26

### Changed
- **A save that does not happen now says why.** When Save Changes (Stations) or Save & Sync (Settings) is refused
  because of a shortcut collision, or fails, the app log gets a warning naming the shortcuts or the error. The same
  reason shows in red under the Stations page's Save Changes, as well as in the status line. Before, it showed only
  in the status line, so a refused save looked like nothing happened.

## [2.10.6] - 2026-11-26

### Fixed
- **The now-playing card and the room remote show the change a button made.** After a station, input, power or
  volume button they read the receiver again 2.5 s later, because the receiver reports some changes (the station's
  name, the input) only after its next poll.

## [2.10.5] - 2026-11-26

### Added
- **Five more MCP Apps (cards in the chat).** In a host that shows MCP Apps:
  - **Room remote** (`show_room_remote`): buttons for power, the Smart Scenes, the saved stations, the inputs, the
    sound modes and the sleep timer.
  - **Automation preview** (`preview_automation`): an automation as WHEN / ONLY IF / DO steps with its branches and
    any problems, before it is saved. Save it on or off from the card. With an id it shows a saved automation with an
    on/off button; with nothing it lists them all.
  - **Insights** (`show_insights`): listening time per day, the week's rhythm by hour, the top apps and inputs,
    the listening volume by time of day and by source, and automation runs, for today, 7 or 30 days.
  - **Radio search** (`show_radio_search`): Radio Browser results with Play and Save buttons and a search box.
  - **Volume memory** (`show_volume_memory`): each learned level with −/+ and Forget; the current source is marked.
- New MCP tools for the cards' buttons: `media_play_station` (a saved station by alias or name), `save_station`,
  `volume_memory_set_level` (never above the safe ceiling) and `volume_memory_forget`. Saving and changing levels
  need the FullOrchestration tier.

### Changed
- The cards share one script (the MCP Apps view protocol) and one stylesheet, which the server puts into each page.

## [2.10.4] - 2026-11-26

### Fixed
- **Now playing names the station a scene starts.** When a scene (for example Morning Jazz from the arrive-home
  automation) plays a saved station's stream, the app and the MCP card show the station (DR P8 Jazz), not
  "Local Music · Song". HEOS reports the app's own URL streams as source 1024 "Local Music" with placeholder
  metadata; that no longer clears the station or shows as Local Music.

## [2.10.3] - 2026-11-26

### Added
- **The first MCP App: a now-playing card in the chat.** The new MCP tool `show_now_playing` points at the card
  (`ui://denon/now-playing.html`, MCP Apps extension `io.modelcontextprotocol/ui`). A host that shows MCP Apps (Claude,
  VS Code, ChatGPT and others) draws it in the conversation: what plays, the input and sound mode, the volume with the
  safe ceiling marked, and buttons for volume −/+, mute and play/pause (HEOS). The buttons use the ordinary tools, so
  the permission tier and the safe-volume ceiling apply. The card follows the host's light or dark theme. Hosts without
  MCP Apps get the same data as text. Built on `ModelContextProtocol.Extensions.Apps` 2.4 (experimental in the SDK).

## [2.10.2] - 2026-11-26

### Fixed
- **The MCP server reports the app's version** (for example 2.10.2) in its server info and `/health`, not the
  Core library's old 1.13.1.

## [2.10.1] - 2026-11-26

### Changed
- **The MCP server runs on the official MCP C# SDK** (`ModelContextProtocol.AspNetCore` 2.4, ASP.NET Core Kestrel)
  instead of the hand-written HTTP and JSON-RPC layer. It speaks the MCP revision **2026-07-28** (no start handshake
  or session, version and client info on every request); clients that still start with `initialize` (2025-11-25 and
  older) get a session as before. The tools, resources, prompts, permission tiers, safe-volume ceiling and plugin
  tools are unchanged. The tool list is the SDK's tool collection: a plugin change tells sessions the list changed.
- **LAN connections need no administrator rights** any more (Kestrel instead of HttpListener's URL reservation).
- **A browser page on another site can no longer call the server**: requests with a foreign `Origin` get 403 (only
  native clients and pages on this PC). The token is compared in fixed time.
- **A missing resource is an invalid-params error (-32602)**, as 2026-07-28 asks.

## [2.10.0] - 2026-11-26

### Added
- **Volume memory** (Scenes & Acoustics → Volume memory): the app learns how loud you listen to each source (a live TV
  channel such as DR1 or TV 2 Charlie, a TV app or HDMI input, a Chromecast or Apple TV app such as Netflix or DRTV, a
  receiver input) and sets that volume when the source changes and holds for 5 s. Down at once, up in a short fade,
  never above the safe volume (`MaxSafeVolume`). It leaves the volume alone when you changed it after the switch, when a
  scene, an automation, an agent or a plugin switched the source, when the receiver is muted, or when the difference is
  under 3 steps. It learns from the time spent at each volume (a level moves about 63 % toward a new volume in 15
  minutes of listening) and starts from the last 30 days of logs. Change a level with −1/+1, forget it, or tick
  **Only turn down**. The levels are in `volume-memory.json`.
- **Automation trigger "Source changes"** (`trigger.source`, on the new `source.changed` event, optionally for one
  source) and **action "Learned volume"** (`action.learned_volume`).

## [2.9.5] - 2026-11-26

### Fixed
- **A HomePod paused long ago no longer fills "Now playing".** While the receiver sleeps, a paused HomePod shows for
  10 minutes after it last played, then the card goes back to "Receiver in Standby". A HomePod keeps its last stream
  paused for hours; before, that old stream (for example DR P4 from earlier in the day) stayed on the card. The same
  applies to the phone remote and MCP.
- **The shutdown line says "closed by an installer"** when an installer's Restart Manager closes the app. In 2.9.3 and
  2.9.4 it said "Windows shutdown".

## [2.9.4] - 2026-11-26

### Fixed
- **A light change reads "Lights: 3 of 4 on" in the activity feed** (or "Lights off"). In 2.9.3 the new events showed
  "Lights: changed"; older Hue status lines ("Lights: Stopped") are now quiet.
- **Old "chromecast: now-playing" reads are quiet in the activity feed.** 2.9.3 stopped writing them; the ones already
  in the log (about 700 from one evening) now show only with all events, as Insights already left them out.
- **The volume chart leaves out readings of 0** (the receiver switching inputs, from before 2.9.3), so its scale is not
  pulled down to −80 dB.

## [2.9.3] - 2026-11-26

### Added
- **Insights shows what the automations did**: a new Automations card with the runs that changed something, the runs
  with nothing to do and the runs that failed, and the last result of each automation. The activity feed names the
  automation and what it did; a run with nothing to do is a quiet entry.
- **Volume by app** in the Volume card, weighted by listening time, and an observation when streaming plays clearly
  louder (or quieter) than live TV.
- **An input is named by the streamer seen on it** in the Receiver card, for example "Media Player · Mostly Apple TV".

### Changed
- **The volume average is weighted by listening time**: an evening at 50 now counts more than five quick changes at
  30. Before, Insights averaged the change events. A volume of 0 no longer counts as the quietest level.
- **Episodes group under their series** in Most watched: "TVA (1397)" and "TVA (1398)" are "TVA".
- **Who's in control counts only real changes**: not automation runs with nothing to do, not the receiver changing
  sound mode with the TV's signal, not reads, and not the Hue plugin's status text.
- **An automation event says what the run did** (`outcome`: acted, nothing or failed, and `summary`), written after
  the run. A check that says yes with nothing after it now reads "nothing to do: Yes: the input is TV" instead of
  "no actions connected".

### Fixed
- **A restart is not a new session.** A session the app's restart cut in two counts as one sitting in Insights, and
  the app saves the open sessions every minute: after a crash, the next start closes them at the time they were last
  seen (reason `app_crashed`) and writes a warning to the log. Before, a crash lost them.
- **Starting and stopping the app no longer writes fake changes.** The LG TV connecting at start is not "the TV turned
  on", and its first app and channel are not changes; the plugin stopping is not "the TV turned off". The Chromecast's
  first state after a connect is not a playback change.
- **Brief flips are not written**: zone 2, mute and sound mode must hold (10, 3 and 10 seconds) before they count, so
  the receiver switching inputs or following the TV's signal writes nothing. A volume of 0 for a moment while the
  receiver switches inputs is not a volume change. Chromecast PLAYING → BUFFERING → PLAYING is no change.
- **Hue writes a light change only when the number of lights on changes**, not its status text ("Connecting…",
  "Stopped").
- **Less log noise**: no line every 93 s for the Audyssey sync, discovery lines only when the devices found change,
  the LG TV's "insufficient permissions" answer once per run, and a webhook repeated within 5 s is not written again
  (it still runs its automations).
- **A settings save no longer reconnects the receiver's live updates** (HEOS push) unless the receiver's address
  changed. Changes made in that moment were missed.
- **The shutdown line says why**: Windows sign-out, Windows shutdown, or an exit without the app's own Exit (an
  installer). Before, every such exit said "Windows session ending".
- **Reading a plugin is no longer logged as an action.** A read-only action (declared so by the plugin, or named
  `*.refresh`) writes no `plugin.action` event, no app.log line and no "used from the app" mark. Before, the Deck's
  artwork reads wrote about 700 identical Chromecast events in one evening and marked real play and pause changes as
  done from the app.
- **The Deck stops asking for cast artwork that does not come.** It tries four times, 15 s apart, per program. Prime
  Video never sends artwork, so before it asked every 15 s for as long as it played.

## [2.9.2] - 2026-11-25

### Changed
- **A webhook call answers with what each automation did.** The reply waits up to 4 seconds and says, per
  automation, "ran Scene 'Morning Jazz'", "nothing to do: the receiver is on", "skipped, in its cooldown until 11:37:05"
  or "still running" (`results` and `message` in the JSON). Add Get Dictionary Value `message` → Show Result to the
  iPhone Shortcut to see it. Before, the reply only listed the automations that listen.

### Fixed
- **The automation editor window asks before it closes with unsaved changes**: Save, Discard or Cancel. Before, closing
  the window dropped the changes without a word.

## [2.9.1] - 2026-11-25

### Fixed
- **A run that changes nothing no longer uses up an automation's cooldown.** When its checks say no (for example
  "the receiver is on"), or it only waits, the cooldown is given back, so the next real trigger runs. Before, a test of
  the iPhone "arrive home" webhook while the receiver was on blocked the automation for its whole cooldown. Such a
  run no longer counts toward the loop guard either, so a check that keeps saying no cannot pause every other event
  automation.
- **A trigger that the cooldown blocks is now in the activity log**, for example "Webhook 'arrive-home' → skipped: in
  its cooldown until 10:38:58 (last run 09:38:58)", at most once a minute per automation. Before, a blocked webhook
  looked as if it had never arrived.

## [2.9.0] - 2026-11-25

### Added
- **Automations are graphs, built in a node editor.** Scenes & Acoustics → Automation Rules → **Open automation
  editor** opens a window with a canvas: drag in a trigger, conditions and actions, and connect them. See
  `docs/automation-graphs.md`.
  - **Triggers:** a room event (`power.changed`, `input.changed`, `session.started`, `appletv.woke`, `cast.*` …, with
    an **Only when** filter such as `stream=avr, to=on`), a webhook, a schedule, or a program that starts.
  - **Conditions** with a yes and a no output: the event's data, a time window (may pass midnight), and the
    receiver right now (power, input, mute).
  - **Actions:** a Smart Scene, a plugin action with its parameters, a receiver command (power, mute, volume, input,
    sound mode) and a wait. An automation's volume is capped at the safe volume, as for AI agents.
  - **Dry run** shows what would happen now, step by step, without changing anything; **Run now** runs it. The
    editor lists the latest room events, and one click makes a trigger from an event.
  - The Automation Rules tab lists the automations with an on/off switch, the last run, Run now and Edit.
  - Four example automations, all off: media player → Movie Night, Night Guard at 22:00, receiver on in the evening
    → lights, and an iPhone "arrive home" webhook.
- **Webhook trigger for iPhone Shortcuts and other devices:** `POST /api/hooks/{name}` on the Phone Remote port
  (`18925`, owner token as `Authorization: Bearer`) starts every enabled automation with that hook name. The JSON
  body's fields become event data that filters and conditions can test (`{"focus":"Sleep"}` and `focus=Sleep`). The
  answer names the automations that listen.
- **Live room event bus** (`RoomEventBus`): every event that the event log records is also published live in the
  app, whether or not event logging is on. The automation engine is its first user.
- MCP: `create_or_update_automation` saves a validated graph (it starts off unless the call says `enabled: true`),
  and `denon://catalog/capabilities` lists the node types. It replaces `create_automation_rule`.

### Changed
- **Automation rules are replaced by automation graphs** (`automationGraphs` in settings.json). Old rules are not
  carried over.
- The event monitor now runs when event logging is off, so automations still see the events; with logging off,
  nothing is written to disk.
- Loop protection: changes made by an automation never start a room event trigger, event triggers start an
  automation at most every 10 s, and more than 20 event-started runs in a minute pause event triggers for a minute.
  Webhooks may repeat at once (cooldown 0 is allowed).
- The Insights server (`127.0.0.1`, cookie key) also serves the editor and its API. Requests that change
  automations need the header `X-Denon-Request: 1` and come only from the server's own pages.

## [2.8.0] - 2026-11-24

### Added
- **While the receiver sleeps, the now-playing hero shows the HomePod that plays** (HomePod plugin 0.2.0): the title,
  "artist • app • speaker" and the artwork. Play / Pause, Previous and Next control that HomePod.

## [2.7.0] - 2026-11-24

### Added
- **HomePod plugin (separate download, `homepod-v*` releases)** in `external-plugins/Denon.Plugin.HomePod`: finds the
  HomePods on the LAN (a stereo pair is one speaker), shows what each plays (app, title, artist, play state) over the
  AirPlay 2 remote-control channel, sends play/pause/next/previous, logs listening sessions (`kind: "homepod"`) and can
  pause the HomePods when the Denon turns on. No PIN and no stored pairing: HomePods accept a transient pairing on each
  connection. It compiles in the Apple TV plugin's protocol code, so that code exists once.
- **The app knows HomePod sessions:** Insights counts them as listening time ("HomePod" source, the app as AirPlay
  sender, Apple Music or Podcasts), in full next to the TV; the activity feed says "Køkken linked" and "… paused: the
  receiver turned on"; the update check finds `homepod-v*` releases; Help has a HomePod topic.

### Changed
- The watch-time overlap tracker keeps one open session per HomePod too (`homepodDevice`), so two HomePods that play
  at once both count.
- Shared Apple protocol code: transient HAP pair-setup (fixed PIN 3939, transient flag, `X-Apple-HKP: 4`) next to the
  stored pairing, and `atvprobe homepod --host <ip>` to read what a HomePod plays. No change for the Apple TV.

## [2.6.2] - 2026-11-24

Fixes from the first computer-use test run (`docs/testing/computer-use-test-script.md`).

### Fixed
- **Volume steps no longer drift (S1).** −5 then +5 could leave the receiver 5 dB louder: a status update with the old volume replaced the app's target before the receiver had taken the new one, and the next step counted from it. The app now keeps a volume it sent until the receiver reports it, or 4 s at most (`VolumeCommandGuard`). Steps, quick levels, the slider, the wheel and the safety low volume use it.
- **Pairing secrets stay hidden on screen.** The Cast page shows the Phone Remote QR (it holds the pairing key) only after **Show QR**, Owner QR or Guest QR, and the title bar's Phone QR. The Brain page shows the MCP bearer token, and the token inside the client config, as dots until **Show**. Both hide again when you leave the page or the window hides. Copy buttons still copy the real values.
- **Mute shows in the readout:** "MUTED • VOL 41" under the big number, and "• Muted" in the status line.
- **The volume line no longer shows the receiver's MVMAX** ("41 / 69"): it follows the volume (41 → 69, 44 → 72) and changed with every step. It now says "VOL 41" (or the dB value).
- **Esc leaves Help** for the page it was opened from (to the Deck when there is none), like "← Back".
- **An empty Health activity filter says so** ("Nothing here for this filter and search.") instead of showing an empty grey box.
- **The Automation Rules badge says "0/3 on"** (rules switched on, of all rules) instead of a bare "0".
- **Music: the Music, Videos and Playlists tabs load their own results** for the current search at once.
- **The LG webOS TV page shows the program** under NOW SHOWING ("TVA (1398) 18:30–18:50").
- **Philips Hue: no scene chips with ID names.** The bridge's temporary "recycle" scenes (for example "13n3NlxMbQ-8QYch") are skipped, and in All Lights each scene name shows once.
- **Narrow window:** the Deck's QUICK SCENES header shortens its subtitle instead of running under the buttons, and the now-playing chips wrap to a second line instead of hiding under the transport buttons.
- **Tooltips say what a click does:** a quick scene chip says it runs the scene at once and lists what it sets; a station card explains Play and Low Vol.

## [2.6.1] - 2026-11-24

### Changed
- **The meters cost about half the GPU.** The VU needles and the LED spectrum draw at most 30 frames a second instead of at the screen's refresh rate (needle ballistics keep the real time). The frame loop is attached only while the Deck shows a meter in a window on screen; minimized, in the tray or on another page, the app asks WPF for no frames. The small equalizer next to the title animates only while it shows. Measured on the Deck with the VU meters: GPU 3D 5–6 % instead of 10–14 %, CPU 1.3–1.6 % instead of 2.3–2.4 %; minimized to the tray 0 % GPU, as before.

## [2.6.0] - 2026-11-24

### Added
- **Live TV shows the program and the channel logo.** On Live TV the now-playing hero shows the program from the LG TV's guide as the title, "DR1 • Ch 1 • 16:25–17:15" below it, and the channel's logo. The LG webOS TV plugin (2.3.0) reads the channel's TV and DVB IDs, asks the TV for its guide on a channel change and when a program ends, and corrects a guide that the TV dates a day wrong (seen live: one day ahead).
- **Channel logos** come from the open tv-logos collection on GitHub: the plugin matches the names of the TV's whole channel list (folding "Ø" to "o", "TV3+" to "tv3-plus", "DR1 Synstolkning" to DR1 and a few more safe rules) and downloads them once into its data folder (about 2 MB for 125 Danish cable channels; 76 of 92 names matched), so GitHub never sees which channel plays. "Show channel logos" in the plugin turns it off; `ChannelLogoOverrides` in its config pins a logo per channel.
- **Insights: LIVE TV CHANNELS** shows the time per channel with its logo. Logos also show in TOP APPS & INPUTS, MOST WATCHED, the activity feed and RIGHT NOW, which also has a progress bar for the program and the next program. Older Live TV sessions get their logo by channel name.
- **Plugin SDK 2.1.0:** `IPluginContext.ReportStatus(text, health, details)` sends structured status details (string keys and values) that the host reads instead of parsing the status text. The host passes them on as `PluginRecordSnapshot.StatusDetails`.

### Changed
- **The watch log keeps one Live TV session per program**, titled by the program, with the channel name, number, TV and DVB IDs, the program times and the logo file in its data. Live TV counts per channel in the watch time ("DR1" instead of "Live TV"), also for older sessions ("Live TV (Ch 1 • DR1)" → DR1); those older texts no longer show as program titles.
- The MCP room state (`nowPlaying.liveTv`) has the channel, its IDs, the current and next program and the logo file.
- The plugin logs each program once ("Live TV: DR1 (Ch 1, DVB ONID 65024 / TSID 2190 / SID 101, Cable Digital TV): … 16:25–17:15; next 17:15 TVA; logo dr1-dk.png") and a summary of the logo matches; `channel.changed` events carry the channel's IDs and logo. The activity feed says "TV channel DR1 (Ch 1)".
- Insights: the top channel gets its own observation unless it already leads all apps; LONGEST SITTING no longer repeats the channel name.

## [2.5.0] - 2026-11-24

### Added
- **ROOM mode (opt-in, off by default).** Settings → YouTube & Local Audio Relay can drive the VU and RTA meters (and Hue Music Sync) from a PC microphone or line-in for TV/ARC, HDMI and Cast audio the app cannot tap. The mic opens only on such a source with the receiver on and unmuted, while the meters are visible or Hue Music Sync is on, and closes at once otherwise. A selected device is never swapped for another microphone. The meters show **ROOM**.
- **Hue Music Sync follows the meter router:** HEOS-delayed PC Cast audio, the synced YouTube/jukebox stream, ROOM mode, silence when muted. Each beat reaches Hue once.
- **PC Cast RTA from any output device:** the FFT runs on the cast capture itself (the same stereo mix the cast sends, also on 5.1/7.1 outputs), so a non-default device no longer falls back to SIM.
- **Plugin updates** (Chromecast, Apple TV) show in the update check with release notes and a release-page link.
- **Smart Scene and Automation editors** have fields for plugin action parameters (for example `entityId`), with required-field marks.
- **Open Insights** also from the Brain page and the status line.
- Badge text on scene, station and Help badges keeps at least 4.5:1 contrast in Light and Dark.

### Changed
- The activity feed hides receiver-input sessions that a Cast or Apple TV session covered.
- More Cast app names (YouTube Music, Prime Video, Viaplay, SVT Play, NRK, Yle Areena, Twitch, Plex and others).
- The Phone Remote switch, token rotation and lip-sync calibration save on their own, without reloading the settings editor or reconnecting HEOS.
- The update panel scrolls its notes between a fixed title and fixed buttons.

## [2.4.1] - 2026-11-24

### Fixed
- Settings → Desktop & Tray Behavior: **Check now** sits in the **Check for updates** row, next to the switch, instead of on a loose row of its own.

## [2.4.0] - 2026-11-24

### Added
- **Insights.** Settings → Smart Event & Habit Logging → **Open Insights** opens your watching and listening statistics in the browser. A small web server inside the app (`Services/InsightsServer.cs`) serves the page on `127.0.0.1` only, on a free port, with a new key each app start (kept in a `SameSite=Strict`, `HttpOnly` cookie; strict Content-Security-Policy; no CDN or network beyond this PC). The page uses the app's live theme and skin (`ThemeCss` writes the palette, brushes, type, radii and meter materials as CSS) and changes with them. It shows the watch and listen time for Today, 7, 30 or 90 days on a front-panel display with the change against the period before; up to six plain observations (prime time, the top app, quieter late nights, who made most changes, the longest sitting, streaks); time per day (per hour for Today) by source; a weekly-rhythm LED matrix in the meter materials; top apps and inputs; most watched titles; volume changes in dB with averages per time of day and the learned habits; receiver use, inputs and sound modes; who is in control (remotes, the app, the phone remote, automations, an AI agent) and lighting; a filterable activity feed in plain sentences; and the room right now. Time is counted without double counting (`exclusiveSec`). The report is `Denon.App.Core/Insights/InsightsReport.cs`, the feed `ActivityFeed.cs`, with tests. Help & Guide has an Insights topic.

### Fixed
- The MCP tool `get_watch_time` failed for periods that cross a summer-time change (for example 60 or 90 days in November). The read start now uses that day's own UTC offset (`WatchTimeReport.ReadFrom`).

## [2.3.0] - 2026-11-24

### Added
- **In-app updates.** Once a day the app asks GitHub for the newest app release (plugin releases are skipped). When it is newer, an **Update to X** button shows in the status line. It opens a panel with what changed and **Download and install**: the app downloads the installer, checks its SHA-256 against the release's `SHA256SUMS.txt`, starts it silently and closes; the installer updates the app and starts it again (`/LAUNCHAFTER=1`). The portable exe, a copy outside the registered install folder, or a `--data-root` start gets **Open release page** instead. Settings → Desktop & Tray Behavior has **Check for updates** (on by default) and **Check now**; the daily check holds across restarts. Nothing installs without a click. The panel is modal (Escape closes it, shortcuts wait), a stalled download stops after 30 seconds and can be canceled, and old installer downloads are cleaned up. The installer relaunches the app as the user who started the update and leaves the Windows startup setting as the app has it.
- **Website** at https://thomasrohde.github.io/DENON/ (Astro, in `site/`, deployed by `.github/workflows/pages.yml`). It uses the app's design system: the Dark and Light palettes, the type and radius scales, the icons and the meter materials are generated from the app's XAML on every build, and the guide is built from the in-app Help topics, so the site follows the app. The home page has live VU meters and the LED spectrum, a tour of every page, and the skins; it follows Windows' light or dark mode or a Dark/Light switch.

## [2.2.2] - 2026-11-24

### Fixed
From a Codex adversarial review of 2.x:
- **Settings recovery is safe again.** When `settings.json` could not be read, saving the splash marker or the window placement wrote the recovered settings over it before you chose to save. Now only Save & Sync and Import write the file during a recovery. Every other save (window, splash marker, theme, skin, a receiver found by discovery, the dB display, plugin and MCP settings) stays in memory until then. A test checks that the file stays byte for byte.
- **A settings file that cannot be written no longer stops the start.** If the splash marker cannot be saved (for example, another program locks the file), the app logs it and opens; the splash shows again next time.
- **Save & Sync keeps the window placement, the splash marker, the theme and the skin.** It changed the whole UI section from an older copy, so the next start said "Updated" again and lost the window size.
- **Theme and skin chips no longer reset unsaved settings edits or reconnect HEOS.** They save the choice on their own and report a failed save.
- **`--data-root` never opens the running app with your normal settings.** When the normal app runs, a message says so and the test start ends.
- **The window fits small, high-scaled screens.** On a screen smaller than the window's minimum size (for example 1366 × 768 at 150 %) the minimum shrinks to the screen, so the title bar and buttons stay visible. The minimum also follows the window when you move it to another monitor.

## [2.2.1] - 2026-11-24

### Fixed
- **A skin switch on the Settings page made the window too short.** The refit measured the Control Deck while it was hidden behind Settings, found nothing to fit, and shrank the window to its minimum height. The fitter now shows the deck for the moment it measures it, so the window gets the deck's size from any page.
- **The theme and skin chips in Settings → Appearance had no space between them.**

## [2.2.0] - 2026-11-24

### Added
- **Splash screen** on the first start after an install, upgrade or reinstall: the logo, "Welcome", "Updated to version …" or "Welcome back", an animated spectrum and the start-up step. It shows for at least 2.6 seconds and fades out over the main window. It follows the theme and the skin, and it does not show when the app starts minimized to the tray.
- **The window fits your screen.** The main window opens on the monitor under the mouse pointer, sized to the skin so the Control Deck shows without scrolling, as far as the monitor allows, and centered. On the first start with the Default skin, a small screen gets the Compact skin, and a very large screen at low scaling gets the Large skin; the status line says so. Until you move or resize the window, it grows when the deck grows (for example when the receiver connects). A skin switch refits the window. The app remembers the size, the position and the maximized state, also at Windows sign-out.
- **Version in the status line**, at the right.
- **Help & Guide is up to date**: new topics for Appearance (themes, skins, light meters), the window and status line, the Chromecast plugin and the Apple TV plugin. Every topic, shortcut and port was checked against the app, with the real button and page names and plain wording.
- Start option `--data-root <folder>` (a separate settings, logs and plugins folder, to test a first start), and screenshot-mode options `--fit` and `--capture-splash`.

### Fixed
- Settings and the README said `Ctrl+1..7` switches pages; it is `Ctrl+1..9`.
- The default Control Deck window (1360 × 920) scrolled the right-hand column; the window now fits it.

## [2.1.0] - 2026-11-24

### Changed
- **Light meters in Light.** In a light theme the VU dials have cream faces with black print and a black needle, and the spectrum has a light screen with deep blue to red segments. Hi-Fi gets ivory dials with an amber lamp and deep teal segments. Dark is unchanged. The meters switch live with the theme. A test keeps the print on each dial readable.

## [2.0.1] - 2026-11-24

### Fixed
- **Text on buttons and chips takes the control's color.** The default text style set every text to the primary text color, so the blue Play and Activate buttons showed dark text in Light. Now text inherits the color of its button, chip or window: white on the blue buttons in Light, dark ink on the cyan buttons in Dark, cyan on the active chip, and red on Delete and Factory Reset. A test keeps the color out of the default text style.
- **README screenshots** show the button colors.

## [2.0.0] - 2026-11-24

Themes and skins. The Chromecast (0.5.0 or later) and Apple TV (0.4.0 or later) plugin pages follow both live;
older plugin versions keep the Dark colors and the Default sizes.

### Added
- **Themes**: Settings → Appearance has *Follow Windows* (the default: light or dark as Windows apps are set), *Dark* and *Light*. The theme switches live, with no restart, and is saved at once. The VU dials and the LED spectrum screen stay dark in every theme, like the display of a real receiver.
- **Skins**: Settings → Appearance → Skin has *Default*, *Hi-Fi* (rack-receiver look: nearly square corners, Bahnschrift headings, Share Tech Mono numbers, amber-backlit VU dials and a teal fluorescent-display spectrum), *Compact* (smaller type and tighter spacing) and *Large* (larger type, controls and spacing, for the sofa or a touch screen). A skin switches live, works with every theme, and also reaches the plugin pages.
- **Screenshot mode options** for theme and skin work: `--theme <id>`, `--skin <id>`, `--switch-theme <id>` and `--switch-skin <id>` (switch live with the window open, to test the live path) and `--all-pages` (also Brain, Health, Help, Plugins and each plugin page).
- **Third-party notices** (`THIRD-PARTY-NOTICES.md`, installed next to the app) for the bundled Share Tech Mono font (SIL Open Font License).
- **README screenshots** show the Appearance card in Settings.

### Changed
- **Theme files** are in layers (see `docs/theming.md`): palettes in `Themes/Palettes/` (Dark, Light), brushes built from them in `Themes/Brushes.xaml`, skin tokens in `Themes/Skins/` (one file per skin), and the shared `Themes/Icons.xaml` and `Themes/Styles.xaml`. Views refer to theme brushes and skin tokens with `DynamicResource`; tests block `StaticResource` for them.
- **Spacing and sizes in the views** are written in Default-skin pixels with `{skin:Space …}` and `{skin:Size …}` (about 1,140 margins and paddings and 350 sizes), which the skin scales live. A test blocks literal values. The Default skin looks exactly as before.
- **Skin contract tests**: every skin has the same tokens, the type scale rises and stays at 8.5 px or more, and Compact and Large stay smaller and larger than Default.
- **Plugin pages follow the skin** with no Plugin SDK change: each plugin has a `HostSkin` helper that binds to the host's skin scales by name (built-in plugins in XAML; the Chromecast and Apple TV pages built in code).
- **Palette contract tests**: every palette has the same tokens, and text keeps a minimum contrast on every surface (primary 7:1, secondary and muted 4.5:1, faint 3:1; accent text 4.5:1).
- **Overlay tokens** are named by role: `OverlayWhite*` → `OverlayHighlight*`.
- **Meter materials**: the VU dial accents and source print, and the LED spectrum screen, come from `MeterPalette` (skin), not from the theme. The meters redraw on a skin switch.
- **Built-in plugins** (Hue, Smart TV) keep a token name instead of a brush for their connection badge, so it follows a theme switch.

## [1.22.0] - 2026-11-23

### Changed
- **Front-panel layout**: The 206-px sidebar is now a slim icon rail in three groups (listen: Deck, Music, Stations, Cast; room: Scenes, Plugins; system: Brain, Settings, Health, Help) with one badge per connected device at the bottom. The telemetry card became a status line along the bottom of the window, and Help moved from the title bar to the rail (F1 still works).
- **Control Deck in two columns**: a listening stage (now playing, meters, scenes and stations) and a control rail with everything that changes the receiver: volume, source, sound mode, and a new "Power & zones" card for the sleep timer, ECO and Zone 2.
- **Apple TV artwork in the now-playing hero**: the hero accepts an artwork file from a plugin's own data folder (and nothing else on disk), and asks again every 15 s while the artwork is missing, as for the Chromecast. Needs Apple TV plugin 0.3.0.
- **README screenshots** show the front-panel layout.

## [1.21.0] - 2026-11-23

### Removed
- **Compatibility with older plugins and hosts (Plugin SDK 2.0.0)**: The SDK version is no longer pinned at 1.16.0.0; plugins and the app must use the same SDK. `IPluginContext.ReceiverControl` and `IsAppShuttingDown` are plain members (no defaults for older hosts), and `PluginActionDescriptor.Parameters` is a list that is empty by default. MCP no longer shows a generic fallback schema for actions without declared parameters.

### Changed
- **Icon set (preparation for skins)**: Every vector icon (sidebar, title bar, transport buttons, chevrons, checkbox tick) is a named geometry in `Themes/Icons.xaml`; the views no longer contain path data. The look is unchanged.
- **Typography and shape tokens (preparation for skins)**: Font sizes, corner radii and font families come from `Themes/Typography.xaml` (a type scale of 15 sizes, a radius scale of 10 steps, three font families). Rare in-between values were merged into the nearest step (14 font sizes by at most 1 px, 55 corners by 0.5–1 px); the look is otherwise unchanged.
- **Fewer view copies (internal)**: `MainWindow.xaml` is split into one view per page; the selected-chip look is one theme style (`ChipButton`, `SuccessChipButton` with `Chip.IsActive`) instead of 31 copies; pages and sub-tabs switch through one converter. The look is unchanged.
- **Drop-down lists, lists and password fields match the app**: The ComboBoxes on the Scenes and Settings pages used the white Windows default look; the app now has dark styles for ComboBox (also editable), ListBox and PasswordBox, the same box as its text fields.
- **Built-in plugins use the app's styles**: Philips Hue, Smart TV and Home Assistant drop their own copies of the card and button styles and Hue's own color aliases. Their main buttons are now the app's cyan main button (before: amber in Hue, green in Home Assistant). The Chromecast and Apple TV plugins use the app's ComboBox and text field styles instead of their own template.
- **Theme tokens (preparation for themes in 2.0)**: Every color the app draws now comes from one theme file (`Themes/Colors.Dark.xaml`): a neutral ramp, text levels, accent families and overlays, with brushes in `Themes/Brushes.xaml`. About 640 hand-picked colors in the app and the built-in Hue, Smart TV and Home Assistant views became about 80 tokens; near-identical shades were merged (mean color difference ΔE 0.8, below what the eye sees). Status colors in code (health, diagnostics, shortcuts, logs, tray menu) and the meter materials (`MeterPalette`) follow too. The look is unchanged (screenshots compared pixel by pixel). New tests block new hard-coded colors and unknown resource keys.

## [1.20.0] - 2026-11-23

### Added
- **Apple TV in the now-playing hero**: With the new Apple TV plugin (released separately as `appletv-v0.1.0`), the hero shows what the Apple TV plays ("TVA: Skoler i hele landet ramt af bombetrusler", detail "Apple TV • DRTV • Stuen (2) • Media Player", state Playing/Paused). This works when the Apple TV is on an HDMI input of the TV (TV Audio, same rules as a cast) and when it is plugged straight into the receiver (for example Media Player); an input with its own track metadata (Bluetooth, USB) still wins. The play/pause button and the Space hotkey control the Apple TV. A playing Chromecast or Apple TV wins over a paused one. Reads the Apple TV plugin status (0.1.0+).
- **Apple TV watch time counts once**: Watch sessions with `kind: "appletv"` follow the cast rules in the overlap tracker (they cover the TV's HDMI input and the receiver input), and `get_watch_time` lists them by app.

### Changed
- The hero asks a plugin for artwork once per program for the Apple TV (it has no artwork yet); the Chromecast still retries every 15 s.

## [1.19.0] - 2026-11-23

### Changed
- **New VU meters**: A backlit stereo pair of glass-fronted analog dials (amber L, cyan R). The needle follows the true VU law (deflection proportional to amplitude, −20…+3 VU, 0 VU = −7.2 dBFS, a touch past +3 against the pin), over a printed scale with a red zone and an inner 0–100 % scale. The lamp brightens with the program; tapered needle with glow and shadow, pivot hood, glass sheen and vignette. Each dial shows its channel, a live dBFS readout with 1.5 s peak hold (about 12 Hz, readable), a PEAK lamp (+1 VU) and the source print (DENON · SYNCED / SIM / PC / MUTED / IDLE). The face widens to fill the window, so wide windows show no dead margins. Drawn as a few visuals; a frame only turns two needles.
- **New spectrum (RTA)**: A segmented LED display: bars snap to whole LED segments (crisp at any DPI), colored by height (deep blue → cyan → ice → amber → red), over dim unlit segments, with peak caps that hold 0.45 s and then fall with gravity, a blurred bloom layer, a glass-floor reflection and a source pill. Frequency labels now sit at the true band centers (before they were evenly spaced, so most were in the wrong place).
- **README screenshots** are new and show both meters. Screenshot mode (`--capture-screenshots`) now uses an empty temporary data folder, so the images show default demo content and never the user's own settings, stations, plugins or devices; it also hides the Room Brain "AVR offline" insight that the showcase triggered.

## [1.18.1] - 2026-11-23

### Added
- **Cast artwork and cast play/pause in the now-playing hero**: While the hero shows a Chromecast program (TV Audio, TV on HDMI), it shows the program artwork (read from the Chromecast plugin's read-only `now-playing` action, fetched again when the program changes), and the play/pause button and the Space hotkey send play or pause to the Chromecast instead of HEOS. The icon follows the cast state (also in the tray flyout). Stop, previous and next are unchanged. Verified live with DR TV.

### Fixed
- **The jukebox stopped its own session after 10 seconds** for native FLAC/WAV files: HEOS reports the app's relayed file as source 1024 "Local Music" with "Url Stream" placeholder metadata, and the jukebox took that for another service. The hero then dropped the track, auto-advance stopped, and the synced meters fell back to SIM. A source now counts as another service only with real metadata (`NowPlayingMedia.IsNamedServiceSource`); a real DLNA/Local Music track from the HEOS app still takes over.

## [1.18.0] - 2026-11-23

### Added
- **Real, synced meters for YouTube, YouTube Music and the jukebox (meter phase 2)**: When the app streams a YouTube track or a local jukebox file to the Denon, the VU meters and the 31-band RTA now show that track's real audio (badge **DENON**, RTA "DENON • SYNCED FFT") instead of the simulation. A separate FFmpeg process decodes a copy of the same source in the background (a 4-minute track takes a few seconds; the stream to the Denon does not change), and the meters show the frame at the position the speakers play now (playback clock minus the measured Cinema sync delay). Pause stops the meters; resume continues in sync. Without FFmpeg, before the analysis reaches the current position, or after 90 minutes of one track, the meters use the simulation. Verified live with YouTube Music tracks.
- The jukebox has a playback clock for this (starts when the Denon reads the file from the start, stops on pause), like the YouTube queue.

### Changed
- The live PC analyzer and the app-stream analyzer share one level/FFT/band implementation (`AudioMeterMath`), so DENON and PC meters look the same.

## [1.17.0] - 2026-11-23

### Changed
- **Now-playing hero during a cast**: When the Denon is on TV Audio and the LG TV shows an HDMI input, the hero now shows the cast program ("Spionhvalen: Undercover i Arktis", detail "Cast • DR TV • Stuen 2 • TV DVD Player (HDMI 1)", state Casting/Paused) instead of "DVD Player (HDMI 1)". Live TV and TV apps still win. Without the LG TV plugin, only a playing cast is shown. Reads the Chromecast plugin status (0.3.0+).

### Fixed
- **Plugins built against a newer SDK did not load on an older app** ("Unable to load one or more of the requested types"): .NET refuses a plugin that references a higher SDK assembly version. The SDK `AssemblyVersion` is now pinned at 1.16.0.0 for additive changes (the file version carries the real SDK version), and the host binds every plugin to its own SDK regardless of version. Verified: a plugin built against SDK 1.17 loads and stops cleanly on the 1.16.0 app.
- **Plugins view badge** showed "SDK v1.15"; it now reads the SDK file version ("SDK v1.17").

### Changed
- **Built-in plugins have typed MCP tools**: Philips Hue, LG webOS TV and Home Assistant declare their action parameters and hints (SDK 1.16). For example `plugin_philips_hue_hue_movie_dim` takes `roomId`, `plugin_smart_tv_tv_youtube` takes `contentId`, `plugin_smart_tv_tv_toast` takes `message`, and `hue.refresh` / `ha.refresh` are read-only (allowed in the Observer tier). Permission gates are unchanged (no built-in declares `IsDestructive = true`).

### Added
- **Plugin SDK 1.17 — `IPluginContext.IsAppShuttingDown`**: true while Denon Desktop exits (user exit, Windows logoff/shutdown, installer upgrade), so a plugin's `StopAsync` can tell an app exit from being stopped or disabled. Default `false`.
- **MCP tool `get_watch_time`** (read-only, also in the Observer tier): watch and listen time per day, kind, app and title from `session.ended` events, without double counting (`exclusiveSec`; events from before 1.15.0 fall back to `durationSec` and are counted as `legacySessions`). Reads whole event files (`EventTelemetryLog.ReadEventsSince`), not only the newest 500 events.
- **`ha.trigger_entity` is now listed** as a Home Assistant action, so MCP clients and Smart Scenes can trigger a scene, script or switch by `entityId` (required).

## [1.16.0] - 2026-11-23

### Added
- **Plugin SDK 1.16 — action metadata for MCP**: `PluginActionDescriptor` gets init-only `Parameters` (`PluginActionParameter`: name, JSON type, description, required), `IsReadOnly` and `IsDestructive`. The constructor is unchanged, so plugins built against 1.15 still load. `Parameters = null` means "not declared"; an empty list means "no parameters".

### Fixed
- **MCP plugin tools return their data**: Auto-bridged tools (`plugin_<id>_<action>`) now return the plugin's JSON (pretty text + `structuredContent` for objects) and image (for example Chromecast artwork) instead of only the message text, and set `isError` on failure. New `McpPluginToolBridge` with xUnit tests.
- **MCP plugin tool schemas and hints**: Tools use the declared parameters as their input schema (older plugins keep the generic `message`/`contentId`/`entityId`/`button` schema) and the declared read-only/destructive hints (older plugins keep the id-based guesses).
- **Read-only plugin tools work in the Observer tier**; actions a plugin declares destructive need FullOrchestration (as `power_off` already did).

## [1.15.2] - 2026-11-23

### Fixed
- **Paused or unseen casts no longer count as watching**: Live TV, or a TV app that is not on an HDMI input, now covers a cast session (the TV shows something else, so the Chromecast plays unseen). Sessions that report `watchedSec` (cast) never get more `exclusiveSec` than `watchedSec`. Before, a DR TV program paused behind live DR1 for 10 minutes counted as 10 extra minutes.

## [1.15.1] - 2026-11-23

### Added
- **`TODO.md`**: Open work in one place (meter phases 2–3, habit telemetry, Chromecast plugin, release).

### Changed
- **VU meters & 31-band RTA follow the Denon, not the PC**: A new source router picks the meter signal from what the receiver plays. A badge shows the source: **DENON** (real PC Cast audio, delayed by the measured HEOS delay `CinemaSyncOffsetMs` so it is in sync with the speakers), **SIM** (the Denon plays TV/HDMI/HEOS audio the app cannot hear; simulated from the Denon volume and fading out below volume 15), **MUTED**, **PC** (the Denon is off or idle; the PC's own output, labelled "PC AUDIO (NOT DENON)" on the RTA) and **IDLE**. A podcast on the laptop no longer shows as Denon audio.

### Fixed
- **PC Cast meters were 1.5–3.5 s ahead of the speakers**: Meter and FFT frames are now kept in a short history and shown after the HEOS buffer delay.

## [1.15.0] - 2026-11-23

### Added
- **Plugin SDK 1.15 — Receiver Control API**: New `IPluginReceiverControl` (power, input, sound mode, volume, mute, Smart Scenes) exposed as `IPluginContext.ReceiverControl`. The host grants it only to plugins that declare the new `PluginCapabilities.ReceiverControl` flag and logs every command with the plugin id. The member has a default `null` implementation, so existing plugins are not affected.
- **First standalone external plugin — Chromecast & Google Cast** (`external-plugins/Denon.Plugin.Chromecast`): mDNS discovery, live now-playing, Cast v2 media and volume control, `media` telemetry events, and per-device Denon automations (power on, Smart Scene, input, sound mode on session start; idle power-off; pause when the receiver powers off). Writes habit-ready `session.started`/`session.ended` watch sessions (`kind: "cast"`, per program, 15 s dwell, `watchedSec` excludes pauses) plus `cast.app_ended` summaries on the `media` stream. Not part of the app solution; install with `install.ps1`.
- **Standalone plugin releases**: The Chromecast plugin is released on its own (`chromecast-vX.Y.Z` tags, zip + SHA-256) by the new `release-chromecast-plugin.yml` workflow, separate from app releases. The README now covers the plugin, SDK 1.15, the MCP server, event telemetry, tests and `build-release.ps1`.

### Fixed
- **Plugins view**: The plugin card now always shows the version (a long name no longer pushes it out), and the header badge shows `SDK v1.15`.
- **Chromecast dashboard**: Dark-themed drop-downs (the host has no ComboBox theme) and a "No artwork" placeholder.
- **Local release build**: `release-out/` is now ignored by git.
- **No double counting of watch time**: Every `session.ended` on the `media` stream now has `coveredSec`, `exclusiveSec` and (when covered) `coveredBy`. One viewing makes parallel sessions (Cast program, LG TV on the HDMI input, Denon "TV Audio" via ARC); the less specific ones are marked as covered for that time. Sum `exclusiveSec` to get real time. New `MediaSessionOverlapTracker` with xUnit tests.
- **Sessions lost when Windows closes the app**: On logoff, shutdown or an installer upgrade, the app only disposed the host and never stopped plugins or hosted services, so open sessions got no `session.ended`. Both exit paths now share one ordered, bounded stop (plugins → services → dispose). LG TV sessions now end with `app_shutdown` (not `standby`) at app exit, and `plugin_stopped` when the TV plugin is stopped or disabled.
- **Exact session start times**: `dwellConfirmedSec` on `session.started` is now the real time since the session began (it was always 15, but the check runs on a timer, so the real value is often 16–20 s). `ts - dwellConfirmedSec` is the exact start.
- **Chromecast watch log**: `watchedSec` now includes play time during the 15 s dwell (it was too low by up to 15 s per program; a 15 s clip was logged as 3 s). Apps get clean names by Cast app id (e.g. DR TV instead of "AXIS 10 PROD - www.drtv-platform.dr.dk"). `receiverInput` is filled in when the Denon becomes known after the program started.

## [1.14.0] - 2026-11-22

### Changed
- **Custom Denon Obsidian & Cyan Windows Installer**:
  - Pre-selected **I accept the agreement** by default on the license step for faster installs and upgrades.
  - Branded the Inno Setup wizard with custom Hi-DPI Denon Desktop sidebar artwork (`wizard-sidebar.bmp`), glowing top-right badge (`wizard-small.bmp`), Windows 11 dark title bar (`DWMWA_USE_IMMERSIVE_DARK_MODE`), Obsidian `#0A0C10` / `#13161E` surfaces, Denon Cyan `#00D4FF` headings & accent line, and high-contrast custom radio labels.
- **CI Pull Request Test Gate**:
  - Extended `.github/workflows/release.yml` to run automated `dotnet test` verification on `pull_request` events targeting `master` (skipping installer packaging & release uploads on PRs).

### Fixed
- **Live External Remote, Smart TV & Phone App Now-Playing Sync**:
  - Added unified `NowPlayingResolver` across Desktop UI, Phone Remote, and MCP `get_room_state`: when the AVR is on `TV Audio`, Denon Desktop now displays the live LG webOS TV app, Live TV channel, or labeled HDMI input (e.g. `DVD Player (HDMI 1)`, `Netflix`, `Live TV (Ch 1 • DR1)`) and updates immediately when switching with the physical TV remote, showing `Live TV` / `Active` instead of misleading HEOS `Stop` or stale `Url Stream`.
  - Parsed HEOS `"station"` metadata and service names (`Spotify`, `TuneIn`, `Tidal`, `Deezer`, `Amazon Music`, `AirPlay`, etc.) in `HeosClient`, recognized AirPlay/Tidal/Deezer/Amazon/TuneIn/USB inputs, and surfaced live track/artist/artwork metadata for Bluetooth phone streams.
  - Auto-deactivated stale YouTube Cast, Local Hi-Res Jukebox, and radio preset highlights whenever a physical remote switches inputs/standby or a phone app (Spotify Connect, AirPlay, HEOS, TuneIn) takes over playback, and added coalesced 250ms HEOS push refreshes plus a 15-second LG webOS foreground/channel/input sync guard against dropped TV subscriptions.
  - Preserved confirmed AVR power/input/soundMode across brief port 23 collisions (up to 2 polls) so external Denon phone apps cannot transiently overwrite `TV` with synthetic `NET / HEOS`.
- **Local Relay Range Probes & Stream Isolation**:
  - `LocalAudioRelayServer` no longer invalidates local track URLs or fires `LocalTrackStreamCompleted` on metadata suffix reads (`Range: bytes=-1`); completion now fires only when a full-track transfer reaches EOF, and tokens stay registered for the active session until explicitly unregistered.
  - Clearing an inactive `YouTubeQueueService` or `LocalMediaJukeboxService` now cancels only its own tracked relay token (`CancelStream(token)`) instead of interrupting unrelated active PC Audio Cast or local streams.
- **Pause-Aware & Mute-Preserving Queue Advancement**:
  - `YouTubeQueueService` and `LocalMediaJukeboxService` now suspend their end-of-track buffer drain timers while HEOS is paused (`playState == "pause"`) and preserve the receiver's current mute state across automatic next-track transitions.
- **Atomic Settings Updates, Safe Backup Recovery & Multi-Receiver Discovery**:
  - Added atomic `AppSettingsStore.UpdateAsync(...)` merges inside the persistence gate and switched quick toggles, Remote token rotation, calibration saves, and discovery updates to it so concurrent saves never clobber independent fields; validated the existing primary file before rotating to `.bak` so saving after backup recovery never overwrites a good `.bak` with corrupt JSON; surfaced startup recovery warnings with the exact settings path.
  - `ReceiverPollingService` now auto-selects only when a single receiver is discovered on the LAN; when multiple receivers are found, it lists them clearly so the user chooses the right room in Settings.
- **Room-Scoped Undo & HEOS-Only Timeout Fallback**:
  - `RoomIntelligenceService` now scopes `CanUndo`, `LastSnapshot`, and `RestoreLastSnapshotAsync` strictly to the currently connected receiver's identity so Room A's snapshot can never be applied to Room B, and refuses to capture synthetic HEOS-only status (`IsAvrConfirmed == false`).
  - `ReceiverController` now distinguishes `DenonAvrClient`'s internal 3-second connect/command timeout from explicit caller cancellation (`!cancellationToken.IsCancellationRequested`) so HEOS-only speakers seamlessly enter HEOS fallback instead of failing.
- **Phone Remote, Desktop UI, PC Cast & Windows Startup Polish**:
  - Phone Remote (`RemoteWebUiHtml`) now retains optimistic volume steps across spaced taps until a fresh poll confirms, hides the header Power button when locked/disabled, displays a dedicated `Remote Disabled` recovery card with Retry on `403`, and auto-focuses the pairing input on `401`.
  - Split the Scene Editor toggle into separate **Power on AVR** and **Auto-unmute AVR** switches, enabled text wrapping on the Phone Remote QR caption, lowered `MainWindow.MinHeight` to `580` for compact displays, and relabeled the PC Cast WAV button honestly to **PCM 16-bit WAV**.
  - `PcAudioCastService` now supports 64-bit IEEE float WASAPI loopback streams in `ConvertToStereo16Pcm`, and `WindowsStartupService.SetEnabled` preserves the installer's `--minimized` autostart argument when saving general Settings.
  - Isolated `AppLogStore`, `EventTelemetryLog`, and `RoomIntelligenceService` file paths in `Denon.App.Core.Tests` so running `dotnet test` never reads or modifies `%LocalAppData%\DenonDesktop`, and expanded the suite to cover all follow-up review findings.

## [1.13.1] - 2026-11-22

### Fixed
- **Clean Process Exit (No Lingering Background Process on Shutdown / Upgrade)**:
  - Fixed a WPF UI-thread async-over-sync deadlock in `App.ExitApplicationAsync` where synchronous `_host.Dispose()` blocked while disposing `IAsyncDisposable` singletons (`DenonMcpHttpServer`, `PluginHostService`, `EventTelemetryLog`), leaving a hidden `Denon.App.Desktop` process running after clicking Exit and triggering Inno Setup's "application still using files" prompt.
  - Host shutdown & async disposal now run off the WPF Dispatcher thread with bounded timeouts, `YouTubeCinemaWindow` force-closes cleanly on exit instead of cancelling `OnClosing`, and Windows session/Restart Manager shutdown exits cleanly without hiding to tray.

## [1.13.0] - 2026-11-22

### Added
- **Phone Remote Pairing, Roles & Access Controls**:
  - Added switchable Phone Remote access (`Remote: ON / OFF` toggle — media fetches to Denon AVR stay online even when phone control is off), separate **Owner QR** (full room control) and **Guest QR** (Party Jukebox YouTube search & queue only), secure token pairing (`?pair=<token>` / `X-Denon-Token`), and instant **Revoke & Rotate** without interrupting active streams.
- **Automated Test Suite (`Denon.App.Core.Tests`) & CI Test Gate**:
  - Added xUnit project covering settings recovery/empty libraries/custom shortcuts/secret redaction, HTTP range & split-UTF-8 body parsing, PC Audio Cast 16/24/float/6-ch PCM conversion, Phone Remote pairing/roles/revocation, Radio Browser FLAC/256k+ union filtering, and Room Undo / telemetry origin attribution — wired into `.github/workflows/release.yml`.

### Fixed
- **Automatic Queue Transitions & Failed Track Starts**:
  - Fixed self-cancelling delay token bug in `YouTubeQueueService` and `LocalMediaJukeboxService` auto-advance transitions (`PlayNextAsync` now uses an independent cancellation lifetime after the drain delay), added rollback of active index/history if relay registration or cast fails, added clean end-of-queue completion for local files, and allowed valid short local clips (`> 0` bytes) to complete naturally.
- **YouTube Playback Cutting Off Early Mid-Song**:
  - `LocalAudioRelayServer.StreamTranscodedMp3Async` bursts the first 6 seconds (240 KB) immediately for instant HEOS playback start and then paces streaming gently at ~1.03× real-time (`41.2 KB/s`) with `-err_detect ignore_err`, preventing Denon HEOS from over-buffering 60+ seconds ahead.
  - `YouTubeQueueService.OnRelayTrackStreamCompleted` and `LocalMediaJukeboxService` wait for HEOS to naturally drain its internal audio buffer (`playState == "stop"` / remaining `track.Duration`) before advancing or stopping.
- **Quiet Scene & Room Undo Safe Volume Ordering**:
  - `ReceiverController.ApplySceneAsync`, `RoomIntelligenceService.RestoreLastSnapshotAsync`, and `DenonMcpHttpServer.ExecuteApplyAtmosphereAsync` now establish target volume **before** switching inputs or releasing mute so a muted receiver at high volume never briefly blasts when activating a quiet scene or undoing.
  - `RoomIntelligenceService` now refuses to synthesize assumed Undo snapshots when disconnected, retains snapshots on failed restore for retry, and enforces `MaxSafeVolume` on MCP Undo.
- **HEOS-Only Speaker Fallback**:
  - `ReceiverController` now tracks AVR port 23 availability independently (`IsAvrProtocolAvailable`) and degrades gracefully to HEOS-only status, volume, mute, transport, and streaming when port 23 is unavailable (automatically restoring full AVR controls when port 23 recovers).
- **PC Audio Cast Format Fidelity & Cleanup**:
  - `PcAudioCastService` now accurately converts 16-bit, 24-bit, 32-bit int, 32-bit float, and multichannel WASAPI loopback audio to stereo 16-bit PCM WAV (and passes the exact `f32le`/`s24le`/`s32le`/`s16le` format to FFmpeg MP3), paces silence keepalive 1:1 to real time (100 ms per 100 ms idle), and cancels the silence producer before awaiting on disconnect.
- **Settings Safety, Custom Shortcuts, Empty Libraries & Secret Redaction**:
  - `AppSettingsStore` preserves corrupt `settings.json` files (`.corrupt-*`), keeps a last-good `.bak` backup, publishes in-memory state only after atomic disk replacement succeeds, preserves custom `scene:*` / `plugin:*` shortcuts, honors intentionally empty station/scene libraries, and redacts MCP/Remote tokens on JSON export (restoring local secrets on import).
- **Phone Remote Web UI, Relay & Accessibility**:
  - Replaced inline JSON click handlers in `RemoteWebUiHtml` with DOM event listeners so apostrophes/quotes/Unicode titles (`"Don't Stop"`, `"Björk"`) never break Play/Queue; preserved keyboard focus across 2.2s polls; serialized rapid volume taps without dropping steps; distinguished Connecting / Standby / Receiver Unreachable / Desktop Unreachable with Retry; enabled pinch-zoom and added ARIA labels/tablist semantics.
  - Fixed `LocalAudioRelayServer` to decode UTF-8 HTTP request bodies once from raw bytes (preserving non-ASCII titles split across packets), implemented RFC suffix ranges (`bytes=-N`) + `416 Range Not Satisfiable`, prevented advertising nonzero `206` offsets on non-seekable streams, and pointed `Allow Firewall` at the actual bound port.
- **Desktop UI, Cinema Viewer, Plugins, Telemetry & Packaging**:
  - Added a translucent auto-hiding floating hover HUD (`Show Bars (H)` | `Fullscreen (F11)` | `✕ Close`) inside `YouTubeCinemaWindow` when bars are hidden, clearing `WindowChrome.CaptionHeight` so top-right clicks work cleanly.
  - Prevented quick UI/remote preference toggles from wiping unsaved Settings/Stations/Scenes edits; prevented `Space`/`Enter` from hijacking focused buttons/checkboxes; raised muted/secondary text contrast to ≥ 4.5:1 WCAG AA; ran local folder drops off the UI thread with `IgnoreInaccessible` and added/duplicate/skipped reporting; updated spectrum analyzer labels (`PC LOOPBACK FFT` / `SIMULATED (VOL)`); guarded Lyrics fetches against stale out-of-order responses and paused playback.
  - Made Hue & Home Assistant startup syncs non-blocking with 5s timeouts; retained Hue Entertainment stop identifiers before DTLS handshake so failed handshakes always release the bridge area; masked Home Assistant token with a `PasswordBox`; preserved distinct parameterized plugin steps and surfaced per-step failures in Automation; fixed Radio Browser `FLAC / 256k+` union filter; switched event telemetry files to daily granularity (`yyyy-MM-dd`) for exact day-based retention; passed `--minimized` in installer autostart and derived branch installer version from `Denon.App.Desktop.csproj`.

## [1.12.0] - 2026-11-22

### Added
- **Comprehensive Smart Event Telemetry & Habit-Ready Logging System**:
  - **Split Monthly NDJSON Event Streams (`%LocalAppData%\DenonDesktop\logs\events\`)**: Non-blocking bounded-channel writer (`EventTelemetryLog`) writes structured `.jsonl` files separated by domain and plugin (`media-yyyy-MM.jsonl`, `avr-yyyy-MM.jsonl`, `tv-yyyy-MM.jsonl`, `lighting-yyyy-MM.jsonl`, `control-yyyy-MM.jsonl`, and `plugins/<plugin-id>-yyyy-MM.jsonl`) with automatic 90-day / 50 MB retention pruning and automatic 5 MB log rotation for `app.log`.
  - **Zero-Noise Smart Delta & Session Engine (`EventTelemetryMonitorService`)**:
    - Records **only real state transitions** across Denon AVR (`power.changed`, `zone2.changed`, `input.changed`, `sound_mode.changed`, `mute.changed`, `eco.changed`, `sleep_timer.changed`), LG webOS TV (`power.changed`, `app.changed`, `channel.changed`, `audio_output.changed`, `screen.changed`), and Philips Hue (`lighting.state_changed`, `preset.applied`, `color.set`, `music_sync.changed`).
    - **3-Second Volume Coalescer**: Absorbs rapid remote/UI volume steps into a single `volume.settled` event (`from`, `to`, `delta`, `input`, `soundMode`).
    - **15-Second Media Dwell Filter**: Tracks both AVR/HEOS sessions (YouTube, Internet Radio, external Spotify Connect / AirPlay / TuneIn / HEOS phone streams, and active AVR inputs) and LG TV watching sessions (Live TV channels & streaming apps), recording `session.started` / `session.ended` (`durationSec`, `avgVolume`, `reason`) only after dwelling ≥ 15 seconds — discarding rapid channel surfing and track skips cleanly.
    - **Adaptive Background Probing & External Capture**: Polls active devices every 15s (60s in standby) plus periodic Hue bridge sync (60s active / 5m standby) to capture external physical remote, phone cast, and wall-switch/Hue-app activity.
  - **Automatic Origin Attribution**: 6-second origin watermarks accurately tag every event with its real source: `desktop_ui`, `mcp_agent`, `phone_remote`, `automation`, `smart_scene`, or `external`.
  - **Plugin SDK Event Hooks (`IPluginContext.Events`)**: First-class `IPluginEventLogger` in `Denon.App.PluginSdk` automatically routes built-in and third-party plugin events to their dedicated streams.
  - **Agent-Ready MCP Tool & Resource Template**: Added read-only MCP tool `get_activity_history` (`stream`, `hours`, `limit`, `origin` filters + live stats) and resource template `denon://logs/events/{stream}` with `stream` completions so AI agents can derive rich viewing/listening habits.
  - **Settings UI Controls**: Added a **Smart Event & Habit Logging** card in Settings with enable toggle, configurable retention days & media dwell filter, live session/disk stats, **Open Events Folder**, and **Clear Event History** (plus fixed Settings save to preserve MCP/Plugin/Automation configs cleanly).

## [1.11.1] - 2026-11-22

### Fixed
- **VU Meters & 31-Band RTA Auto-Rebind on Windows Audio Device Switch**: `LiveAudioAnalyzerService` now monitors the active Windows Default Audio Render Endpoint (`MMDeviceEnumerator`) and automatically rebinds `WasapiLoopbackCapture` when turning an HDMI TV on/off or switching speakers/headphones, preventing VU meters from freezing.
- **LG webOS 23/24 Screen Off Cascade & Accurate Display Wording**: Updated `TurnOffScreenAsync` / `TurnOnScreenAsync` with a multi-endpoint cascade (`tvpower/power/turnOffScreen` with `standbyMode`, empty `{}` payload for webOS 23/24, and `settings/setSystemSettings` `energySaving: "screen_off"` fallback); replaced blanket "OLED" labels with universal **Screen Off / TV Display** wording (only mentioning OLED burn-in on actual OLED model IDs) and prevented Room Doctor from suggesting Screen Off while watching Live TV / video apps or when AVR input is `TV`.
- **YouTube Playback Falling Back to Previous Radio Station & Source-Switching Hardening**:
  - `YouTubeMediaService.FindFfmpegPath()` now prefers the real `WinGet\Packages\...\ffmpeg.exe` binary, resolves symlinks (`ResolveLinkTarget`), and verifies FFmpeg launches cleanly (`-version`) before advertising `.mp3` relay URLs — fixing Windows *"untrusted mount point"* crashes on WinGet symlinks.
  - `LocalAudioRelayServer.StreamTranscodedMp3Async` now starts FFmpeg **before** writing HTTP `200 OK` headers and automatically falls back to Direct M4A streaming if FFmpeg ever fails to spawn (preventing 0-byte HTTP drops that caused HEOS to fall back to the previously loaded radio station).
  - `ReceiverController` now cleanly stops stale HEOS playback (`state=stop`) before switching to `SINET` for YouTube / Radio / Favorites and when switching away from `NET` to `TV` / `HDMI` / Smart Scenes so old radio stations never auto-resume during source switches.

## [1.11.0] - 2026-11-22

### Added
- **Direct LG webOS System Settings API (`get_settings` & `set_settings` in `tv_computer`)**:
  - Added `GetSystemSettingsAsync` and `SetSystemSettingsAsync` in `LgWebOsClient` (`ssap://settings/getSystemSettings` / `setSystemSettings` with `com.webos.settingsservice` fallback) for fast 100 ms reads/writes of `picture`, `sound`, `option`, or `all` (picture + sound) settings — with clear fallback guidance to on-screen Settings navigation on firmwares that restrict direct writes.
  - Read-only `get_settings` works in all MCP permission tiers (including `Observer`).
- **Smart TV Live Preview & Virtual Trackpad Polish**:
  - Added a quick **`TV Settings`** shortcut right in the Live TV Screen header bar (automatically snaps a fresh preview after launch when `Auto-Refresh` is off).
  - Added adaptive **Active-Gesture Fast Refresh (`1 s` while actively steering the Virtual Trackpad or clicking the preview, relaxing to `2.5 s` when idle)** and persisted your `Auto-Refresh` preference in `config.json`.
  - Added an inline **Quick Type** input bar directly beneath the Virtual Trackpad so you can click a TV search box and type without scrolling.
- **Smarter Living-Room Room Doctor 2.0 Checks**:
  - Added lazy background Telnet acoustic caching in `RoomIntelligenceService` (also surfaced live in `get_room_state`) powering three new zero-clutter 1-click Auto-Fix insights:
    - **Evening Cinema Display Glow (`avr.dim_panel`)**: detects Denon front panel left on `BRI` during evening movie/surround sound modes and dims it to `DAR` in 1 click.
    - **Daytime Music Night-Compression (`avr.dynvol_off`)**: detects `Dynamic Volume: HEV` left active during daytime music listening and turns it off for full dynamics.
    - **Evening Movie Lights (`hue.movie_dim`)**: detects Philips Hue lights still bright while Netflix/Disney+/Plex/Apple TV/Prime Video/SkyShowtime is playing in the evening and offers 1-click Movie Night dimming.

## [1.10.1] - 2026-11-22

### Changed
- **Denon Front-Panel Volume Scale by Default (`28.0` instead of `-52.0 dB`)**: Defaulted `DisplayVolumeInDb` to `false`, formatted the main volume display to exact `0.0` Denon front-panel steps (e.g. `28.0` / `28.5`), and persisted clicking the volume readout to `settings.json` immediately (dB mode remains 1 click away).
- **Calibrated LG webOS Pointer Targeting & Built-In `settings` App Alias**: Updated `MovePointerToNormalizedAsync` to the empirically verified `1280×720` LG webOS logical pointer plane with smaller uniform `24 px` steps to avoid high-velocity pointer acceleration drift; added `"settings"` / `"indstillinger"` → `com.palm.app.settings` alias and wired the Smart TV **Settings** remote button to launch it directly.

## [1.10.0] - 2026-11-22

### Added
- **Live TV Screen Preview & Interactive Virtual Trackpad (Smart TV Plugin UI)**:
  - Added a full-width **Live TV Screen & Virtual Trackpad** card inside the LG webOS Smart TV dashboard with **Snap Now**, **Auto-Refresh (`2s`)** (automatically paused when navigated away), **Click-to-Click** directly on the live TV preview image (with cyan target feedback), **Open in TV Browser** URL launcher, **Center Cursor** recalibration, and a laptop-style **Virtual Trackpad** supporting drag-to-move, tap-to-click, right-click Back, and mouse-wheel scrolling.
- **Unified LG webOS TV Computer-Use MCP Tool (`tv_computer`)**:
  - Added live TV screen capture (`ssap://tv/executeOneShot`) returning native MCP `image` content blocks (`image/jpeg`) alongside structured TV state (`foregroundApp`, `inputs`, `installedApps`, estimated cursor position), with cursor-wake micro-wiggle so the pink Magic Remote pointer is visible in screenshots.
  - Added Magic Remote pointer control (`move` with drag support, corner-homed chunked `move_to` & `click` on a `0..1000` normalized grid, `scroll`), LG webOS Web Browser launcher (`open_url` via `ssap://system.launcher/open`), `launch_app` (with YouTube `content_id`), `close_app`, `button` (with `repeat`), `type` (with `replace`/`press_enter`), `backspace`, `wait`, and single-round-trip `batch` + `screenshot_after: true`.
  - Extended `PluginActionResult` in `Denon.App.PluginSdk` with optional `DataJson`, `ImageBase64`, and `ImageMimeType` for multimodal plugin-to-MCP responses.

### Changed
- **Consolidated LG TV MCP Controls into `tv_computer`**: Replaced separate `tv_remote_button`, `tv_type_text`, and `tv_cast_youtube` tools with the single unified `tv_computer` tool (while keeping `screenshot` / `get_state` strictly read-only in `Observer` permission tier).

## [1.9.0] - 2026-11-22

### Added
- **2026 MCP Protocol Upgrades (Tool Annotations, Typed Structured Outputs & Resource Templates)**:
  - Added full MCP `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) across all core and auto-bridged plugin tools so AI clients like Antigravity, Claude, and Cursor know which calls are safe to auto-run vs. power-off or queue-destructive.
  - Added typed `outputSchema` definitions and dual `structuredContent` + backwards-compatible text `content` responses on core query & intelligence tools (`get_room_state`, `diagnose_room`, `get_listening_habits`, `apply_atmosphere`, `avr_get_acoustics`, `media_get_now_playing_lyrics`, `media_get_queue`, `media_search_radio`, `media_search_youtube`, `list_scenes_and_rules`).
  - Added live **Resource Templates** (`denon://plugins/{pluginId}`, `denon://scenes/{sceneId}`), **Argument Completions** (`completion/complete`), **Resource Subscriptions** (`resources/subscribe`, `resources/unsubscribe`), and `logging/setLevel` for full MCP Inspector compatibility.
- **8 New Curated Living-Room MCP Tools**:
  - `avr_get_acoustics`: Read live Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Compressed Audio Restorer, Front Panel Dimmer, Subwoofer/Dialog trims, and Tone Control over Telnet `:23`.
  - `media_get_now_playing_lyrics`: Fetch synced/plain lyrics via LRCLIB for the currently playing track (or an explicit `title`/`artist`).
  - `media_get_queue` & `media_manage_queue`: Inspect and control the YouTube Up-Next playback queue (`clear`, `toggle_shuffle`, `cycle_repeat`, `remove`, `play_index`, `next`, `previous`).
  - `hue_set_color`: Set Philips Hue lights to any custom hex color (`#RRGGBB`), white warmth in Kelvin (`2000–6500K`), and brightness (`1–100%`), optionally targeting a specific Hue room/zone (`hue.set_color` plugin action).
  - `tv_cast_youtube`, `tv_remote_button`, `tv_type_text`: Direct LG webOS 2.0 YouTube casting, Magic Remote button presses, and TV keyboard text entry over the persistent SSAP pointer socket.

### Fixed
- **MCP Inspector `resources/templates/list` Error**: Implemented `resources/templates/list` and companion protocol methods so MCP Inspector loads all tabs cleanly with zero errors.

## [1.8.1] - 2026-11-22

### Fixed
- **Persist Auto-Generated MCP Bearer Token on First Boot**: `AppSettingsStore` now immediately writes newly initialized MCP settings (including the generated `BearerToken`) to `settings.json` when upgrading from older versions so the token never rotates across restarts.

## [1.8.0] - 2026-11-22

### Added
- **Streamable HTTP MCP Server (`POST /mcp` + SSE, 2025-11-25 Spec) & Always-On Living-Room Brain**:
  - Added `DenonMcpHttpServer` hosted natively on `http://127.0.0.1:18926/mcp` (localhost-only default, optional LAN bind, auto-generated `ddn_...` Bearer token, 3 Permission Tiers: `Observer` / `SafeControl` / `FullOrchestration`, and a hard `MaxSafeVolume` ceiling enforced on every volume mutation).
  - Exposes **4 Live MCP Resources** (`denon://room/state`, `denon://diagnostics/health`, `denon://catalog/capabilities`, `denon://memory/habits`), **4 Expert Prompts** (`design_atmosphere`, `diagnose_home_theater`, `acoustic_advisor`, `architect_automation`), **22 Core Intelligence/AVR/Acoustic/Media/Scene/Rule Tools** (including semantic `apply_atmosphere`, `diagnose_room`, `fix_room_issue`, `restore_previous_state`, `create_or_update_scene`, and `create_automation_rule`), plus **zero-config auto-bridging** of every enabled `IDenonPlugin` action (Philips Hue, LG webOS TV, Home Assistant, and drop-in `.dll` plugins).
  - Added `RoomIntelligenceService` (**Self-Healing Room Doctor** detecting cross-device mismatches like AVR muted while playing, late-night loud volume, LG TV video playing while Denon isn't on TV eARC, and OLED burn-in protection during audio-only playback, with 1-click Auto-Fix + **Room State Snapshot Undo** + 100% local **Habits Memory** in `room-habits.json`).
  - Added the **MCP & Room Brain** studio dashboard (`Views/McpIntelligenceView.xaml`), a live **`MCP :18926`** status pill in the Title Bar, a zero-clutter **Room Brain Insight Strip** on Control Deck (only shown when actionable), 1-click **Copy MCP Config JSON**, a live MCP Activity Feed, and Guide #12 in the built-in Help & Guide system.

## [1.7.0] - 2026-11-22

### Added
- **Built-In Context-Aware Help & Interactive Guide System (`F1` / `? Help`)**:
  - Added a dedicated **Help & Guide** workspace (`Views/HelpGuideView.xaml`, `ViewModels/MainViewModel.Help.cs`, `Models/HelpGuideModels.cs`) accessible via the left navigation sidebar, a persistent **`? Help`** title-bar button, or pressing **`F1`** from any screen or plugin (with a **`← Back to [Page]`** button to snap straight back).
  - **Live Setup Readiness Checklist**: automatically inspects your real Denon AVR connection (`LIVE PUSH`), Phone Remote / Audio Relay (`:18925`), Philips Hue Bridge, LG webOS Smart TV, Home Assistant Bridge, and Smart Scenes with live progress bar and 1-click **`Open →`** jump buttons.
  - **11 Searchable, Category-Filtered Deep Guides**: covers Quick Start, Control Deck (31-Band RTA & Live Lyrics), YouTube Cinema & Acoustic Lip-Sync Calibration, Smart Scenes 2.0 & Automations, Couch Phone Remote & FLAC Jukebox, 40k Radio Explorer, Philips Hue 50 Hz DTLS Music Sync, LG webOS 2.0 & Magic Remote, Home Assistant Bridge, Complete Keyboard Shortcuts Reference Table, and Local Network Ports / Firewall Troubleshooting FAQ.
  - Updated `Ctrl+1..9` section shortcuts so all 9 sections (including Plugins `Ctrl+6`, Settings `Ctrl+7`, Health `Ctrl+8`, and Help `Ctrl+9`) are directly reachable.

## [1.6.0] - 2026-11-21

### Changed
- **Full-Width Plugins Workspace & Live Ecosystem Quick-Jump Sidebar**: Replaced the cramped 290px inner plugin sidebar with a sleek horizontal **Top Plugin Switcher Strip** (reclaiming +300px of horizontal screen space for Philips Hue, LG webOS TV, and Home Assistant dashboards) and added a **LIVE ECOSYSTEM** quick-jump list in the main left sidebar with live health dots for 1-click access from anywhere in the app.
- **Obsidian Dark Theme Controls Across All Plugins**: Added app-wide implicit Obsidian dark `Slider` (`IsMoveToPointEnabled`), rounded `CheckBox`, and rounded `ProgressBar` styles in `DenonTheme.xaml`, plus inherited dark focus-ring `TextBox` watermark placeholders (`Tag`) across every plugin.
- **Smart Collapsible Setup Panels (`Setup ▾`) & Instant Auto-Save**: Philips Hue, LG webOS Smart TV, and Home Assistant now automatically collapse their IP/pairing/token setup rows once connected so daily controls, telemetry, rooms, lights, apps, and Magic Remote sit front-and-center; Smart TV & Home Assistant coordination checkboxes now auto-save immediately, and Smart TV inputs support pressing `Enter` directly for YouTube Casting, TV Keyboard, and Toasts.
- **Philips Hue Responsive Multi-Column Lights Grid & Quick Header Bar**: Switched Individual Lights to a responsive multi-column card layout (`WrapPanel`) with instant `All On`, `All Off`, and `Sync` right in the top bar.
- **Home Assistant Bridge 2.0 UX Upgrade**: Added live connection badge, domain filter pills (`All`, `Scenes`, `Scripts`, `Switches`, `Lights`), entity search box, live ON/OFF entity state pills, live `sensor.denon_desktop_receiver` telemetry preview, and guided Long-Lived Access Token setup tip.

## [1.5.0] - 2026-11-21

### Added
- **Smart TV Control (LG webOS) 2.0 Overhaul (`Denon.Plugin.SmartTv`)**:
  - **Persistent, Auto-Reconnecting SSAP WebSocket Engine (`wss://:3001` & `ws://:3000`)** with multiplexed request correlation, strict SSAP error validation, and zero-lag real-time push subscriptions (`getForegroundAppInfo`, `audio/getStatus`, `audio/getSoundOutput`, `getPowerState`, `getExternalInputList`, `getCurrentChannel`).
  - **Dynamic Installed Apps & Real HDMI Inputs Discovery**: queries `listLaunchPoints` and `getExternalInputList` directly from your LG TV to show every real installed app (searchable, favorites-pinned, live active highlight) and every physical HDMI port with its actual label & live signal indicator dot.
  - **Virtual Magic Remote & PC → TV Keyboard**: connects to LG's secondary pointer input socket (`getPointerInputSocket`) for a full D-Pad (`Up`/`Down`/`Left`/`Right`/`OK`), `Home`/`Back`/`Exit`/`Settings`/`Info`/`Guide`/`Ch ±`, Media Transport (`Rew`/`Play`/`Pause`/`Stop`/`FF`), and live IME text entry (`insertText`, `sendEnterKey`, `deleteCharacters`).
  - **OLED Screen Off (Audio-Only Mode) & Audio Output Switcher**: one-click **OLED Screen Off** (`turnOffScreen`) to blank the panel while keeping music/eARC playing with zero burn-in, plus one-click switching between **HDMI ARC/eARC**, **TV Speakers**, **Optical**, and **Bluetooth**, and a debounced TV Volume slider.
  - **YouTube URL / Video ID Caster**: paste any YouTube link or 11-character video ID to deep-link directly on your LG TV (`contentId: "v=..."`).
  - **Multi-Interface SSDP Auto-Discovery + Win32 ARP MAC Lookup + Multi-NIC Wake-on-LAN**: auto-discover LG webOS TVs on the LAN, auto-fill their Wake-on-LAN MAC address via ARP, and auto-reconnect after Wake-on-LAN.
  - **Expanded Denon AVR Coordination (24 Plugin Actions)**: auto-switch LG TV audio to **HDMI ARC/eARC** on Denon Power ON, auto-mute TV speakers, optional **OLED Screen Off** on Music Smart Scenes, and full availability across Smart Scenes 2.0, Global Hotkeys, and Couch Phone Remote.

### Fixed
- **LG webOS YouTube Launch Bounce-Back & Lost Pairing Key**: Switched standard app launches to `ssap://com.webos.applicationManager/launch` over the persistent WebSocket (eliminating the short-lived socket teardown that caused apps like YouTube to briefly flash and return to Live TV), added dynamic installed app ID resolution, validated SSAP error responses instead of silently ignoring failures, and immediately persisted returned `client-key` credentials to `config.json`.

## [1.4.0] - 2026-11-21

### Added
- **Unsigned Windows x64 Installer (`DenonDesktop-Setup-win-x64.exe`)**: Added an Inno Setup 6 installer (`installer/DenonDesktop.iss`) built automatically alongside the portable single-file `.exe` in GitHub Releases, featuring per-user zero-admin installation (with optional All Users), Start Menu & optional Desktop/Startup shortcuts, the R2C5 app icon, clean Add/Remove Programs uninstaller, and SHA-256 verification.

### Fixed
- **Philips Hue "All Lights" View & Internal Group Cleanup**: Added an **All Lights** chip (default view) and a **Show All Lights** button on the Individual Lights header so every light on the Hue Bridge is visible at once instead of being filtered to only the first alphabetical room; filtered out internal `hgrp-*` Hue system groups and fixed singular/plural light count labels (`1 light` vs `3 lights`).
- **Responsive Plugin Dashboard Headers**: Converted the Philips Hue, Smart TV, and Home Assistant dashboard headers from `DockPanel` to 2-column `Grid` layouts with subtitle wrapping so action buttons (`Try Demo Mode`, `Push Status`, `Save & Sync`, `Pair with LG TV`) never overlap text at narrower window widths.

## [1.3.0] - 2026-11-21

### Added
- **Couch Phone Remote 2.0 — Lights & Plugins on Your Phone**:
  - Added a dedicated **Lights** tab to the QR-code mobile web UI (`:18925`) backed by new `/api/plugins/action` and `/api/plugins/toggle` REST endpoints so anyone on Wi-Fi can control Philips Hue lights, toggle Music Sync, switch Smart TV inputs, or trigger Home Assistant actions right from their phone.
- **True 50 Hz Philips Hue Entertainment API v2 DTLS/UDP Engine**:
  - Built `HueEntertainmentStreamClient` using `BouncyCastle.Cryptography` DTLS 1.2 PSK (`TLS_PSK_WITH_AES_128_GCM_SHA256`) over UDP port `2100` (`HueStream v2.0` binary RGB frames) — automatically activates 50 Hz zero-lag streaming on detected Hue Entertainment Areas when Music Sync starts, with seamless fallback to LAN REST & Demo Mode.
- **31-Band Live OLED FFT Spectrum Analyzer (RTA) & Synced Live Lyrics (`LRCLIB`)**:
  - Extended `LiveAudioAnalyzerService` with 31 logarithmically spaced ISO frequency bands (`25 Hz–16 kHz`) rendered at 60–144 Hz VSync in `SpectrumAnalyzerControl` with peak-hold caps and a one-click **VU Meter → RTA 31 → VU + RTA** switcher on the Hero OLED deck.
  - Added zero-API-key `LyricsService` (`lrclib.net`) with live LRC timestamp synchronization displayed in a toggleable OLED Lyrics bar right on the Hero Now Playing card.
- **Two New Official Decoupled Plugins**:
  - **`Denon.Plugin.SmartTv` (LG webOS + Wake-on-LAN)**: SSAP WebSocket (`:3000`) pairing & control for Power On/Off, HDMI 1/2/3 switching, YouTube/Plex launch, TV mute, on-screen toast alerts on Smart Scenes, and Demo Simulator mode.
  - **`Denon.Plugin.HomeAssistant` (Home Assistant REST & Webhook Bridge)**: Trigger HA scenes, scripts, blinds, and smart plugs from Denon Desktop or Phone Remote, and automatically push live `sensor.denon_desktop_receiver` telemetry & webhooks to Home Assistant.
- **1-Click Acoustic Lip-Sync Auto-Calibration (PC Mic Chirp)**:
  - Generates a precision 1,750 Hz 3-burst chirp WAV served via `/calibrate/{token}.wav`, listens on your PC microphone with a narrowband Goertzel detector (`AcousticSyncCalibrationService`), measures the exact HEOS buffer + speaker arrival latency in milliseconds, and automatically saves it to `CinemaSyncOffsetMs`.
- **Designer R2C5 Icon Pack & Dynamic Live System Tray Icons**:
  - Replaced the application, taskbar, title-bar, and executable icons with the custom-designed **R2C5 Headphones + Cyan Waveform** multi-resolution icon pack (`denon_icon.ico`, `denon_icon.png`, plus 12 themed variants & editable SVGs).
  - Upgraded `TrayIconController` to dynamically switch the Windows system tray icon in real time between **Active Playback** (`active.ico`), **Muted** (`muted.ico`), **Standby/Offline** (`power.ico`), and **Connected Idle** (`denon_tray.ico`).
- **Full Release Notes on GitHub Releases Pages**:
  - Upgraded `.github/workflows/release.yml` to extract each tag's full Markdown notes from `CHANGELOG.md` and publish them directly in the GitHub Release body along with inline SHA-256 verification.

## [1.2.0] - 2026-11-21

### Added
- **Extensible Plugin System & Official SDK (`Denon.App.PluginSdk`)**:
  - Added a standalone `Denon.App.PluginSdk` contract library (`IDenonPlugin`, `IPluginViewProvider`, `IPluginContext`, `IPluginStorage`, `IPluginReceiverBridge`, `PluginReceiverEvent`, `PluginActionDescriptor`) plus `PluginHostService` supporting both built-in plugins and external drop-in `.NET 10` plugin `.dll` assemblies loaded via isolated `AssemblyLoadContext` from `%LocalAppData%\DenonDesktop\plugins\external\`.
  - Added a dedicated **Plugins** section to the main navigation sidebar with an installed plugins manager (live health badges, per-plugin enable/disable persistence, **Rescan External DLLs**, **Open Plugins Folder**, and 1-click **Plugin Actions** runner).
  - Wired real-time Denon AVR event broadcasting (Power, Input, Sound Mode, Playback, and Smart Scene activation — including scenes triggered from the Couch Phone Remote) directly to enabled plugins.
  - Added **Audio-Reactive & Album-Art Sync SDK Extensions** (`IAudioReactivePlugin`, zero-allocation `PluginAudioFrame`, `PluginAlbumArtPalette`, `LiveAudioAnalyzerService` 512-point Radix-2 FFT + adaptive spectral onset beat detector, and `AlbumArtPaletteService` vibrant cover-art color clustering).
- **Smart Scenes 2.0 & Cross-Plugin Automation Engine**:
  - **Scene → Plugin Action Chains**: attach ordered plugin actions with configurable millisecond delays directly inside the Scene Editor (e.g., `Movie Night` → `Philips Hue: Cinema Dim`, `Late Night` → `Philips Hue: Candlelight`).
  - **Automation Rules Engine** (`AutomationEngineService` + new **Automation Rules** sub-tab under **Scenes & Acoustics**): edge-triggered **App Launch Watchers** (e.g. launching `vlc`, `Plex`, or games automatically triggers Smart Scenes & plugin actions with cooldown), **Time-of-Day / Night Guard Schedules** (`Daily` / `Weekdays` / `Weekends`), one-click **Test Run**, and a live Automation Activity Log.
  - **Global Hotkeys for Any Smart Scene or Plugin Action**: added `+ Scene Key` and `+ Plugin Key` quick-binders to the Settings Shortcut Editor so any global Windows hotkey can fire Smart Scenes or Plugin Actions (e.g., toggle Hue Music Sync or All Lights Off) with full 4-layer collision checking.
- **First-Party Plugin — Philips Hue Smart Lighting (`Denon.Plugin.PhilipsHue`)**:
  - Auto-discovers Philips Hue Bridges via Hue Cloud Discovery + offline LAN SSDP M-SEARCH, supports manual Bridge IP entry, 30-second guided Link-Button pairing over HTTPS (with local private-LAN self-signed TLS validation, Entertainment `clientkey` capture, and HTTP fallback), and a built-in **Demo Simulator** mode.
  - Full control over Master All-On/Off & brightness presets, Rooms & Zones, debounced dimmers, CIE 1931 RGB mood swatches, Hue Bridge native scenes, and individual lights (power, brightness, Kelvin warmth, color dots).
  - **Real-Time FFT Music Sync & Album-Art Ambient Engine**: 4 beat-reactive modes (**Bass Pulse**, **Spectrum Split**, **Album-Art Ambient**, **Cinema Subtle**), live 3-band **Bass / Mid / High** FFT spectrum meters + **Beat** LED, sensitivity and brightness floor/ceiling sliders, CLIP v2 Entertainment Area detection, a 1-click **Simulate Beat** tester, and callable actions (`hue.music_sync_on/off/toggle`).
  - Reactive **Denon AVR ↔ Hue Automations**: automatically sync Hue lighting to Denon Smart Scenes, optional Power ON / Standby sync, and **Cinema Pause Lights** (brighten on Pause, dim on Resume).
  - Upgraded the **Analog VU Meters** to automatically tap live Windows PC WASAPI audio via `LiveAudioAnalyzerService` even when PC Audio Cast is not active.

### Fixed
- **Embedded YouTube Player Auto-Fullscreen & Chrome Removal**: Opening YouTube Cinema now keeps the Cinema window at its normal resizable desktop size (`960×600`, with optional `F11` monitor fullscreen) and automatically maximizes YouTube's embedded video player inside the viewer (triggering YouTube's native fullscreen button / `#movie_player.requestFullscreen()` with Chromium user-gesture activation via WebView2 DevTools Protocol). Fixed an early-injection null DOM race in `PureVideoModeScript` that could prevent YouTube page/player chrome CSS from applying cleanly.

## [1.1.1] - 2026-11-21

### Added
- **Auto-Fullscreen YouTube Cinema Mode**: Opening a YouTube video now automatically launches the Cinema Companion in borderless fullscreen covering the taskbar, with `F11` / **Exit Fullscreen** toggle, `H` / **Hide Bars** for 100% pure edge-to-edge video, and `Esc` progressive exit (show bars → exit fullscreen → close).

### Changed
- **Larger Default Window & Commanding Analog VU Meters**: Increased default window dimensions to `1360×920`, expanded the dual-channel Analog VU Meters to `138 px` height with a wider high-contrast dB arc (`-20` to `+3 dB`), and gave the Settings Shortcut Editor its own dedicated internal scroll view so Desktop Behavior and Relay controls stay anchored in place.

### Fixed
- **Prevented Finished YouTube/Local Streams From Replaying or Autoplaying**: Immediately expires completed LAN relay stream tokens on EOF so HEOS firmware cannot reconnect and loop a finished track, cleanly stops HEOS when the Up-Next queue ends, and disables/blocks YouTube's built-in web Autoplay inside the Cinema Companion window.

## [1.1.0] - 2026-11-21

### Added
- **Interactive Shortcut Editor & Collision Checker** in Settings:
  - System-wide global Windows hotkeys (`RegisterHotKey` on a hidden message-only window so they work even when minimized to tray), including **Open / Toggle Main Window** (`Ctrl+Alt+D`), **Global Play/Pause** (`Ctrl+Alt+P`), **Global Mute** (`Ctrl+Alt+M`), **Global Volume ±2** (`Ctrl+Alt+Up/Down`), and **Global Next/Previous Track** (`Ctrl+Alt+Right/Left`).
  - Editable in-app shortcuts (`Space`, `M`, `Ctrl+Up/Down`, `F5`) with live key recording (`Esc` to cancel, `Backspace` or `✕` to clear) and full persistence in `settings.json`.
  - Real-time 4-layer collision detection checking internal duplicates (`CONFLICT`), live Win32 OS hotkey ownership (`OS TAKEN`), reserved Windows/navigation shortcuts (`RESERVED`), and cross-scope overlaps (`OVERLAP`).
- **Auto-Synced Pure-Video YouTube Cinema Companion**:
  - Tracks live YouTube stream playback position from the exact moment the Denon AVR begins pulling audio frames, automatically pausing/resuming with HEOS and compensating for HEOS buffer latency + page load time.
  - Added **Re-Sync to Denon** button and persistent buffer offset adjustments (`±100ms` / `±500ms`).
  - Strips all YouTube page & player chrome via injected CSS + `MutationObserver` so the Cinema window displays pure full-bleed video only (`100vw × 100vh`).

### Changed
- **Ultra-Low-Latency Analog VU Meters (~180 ms → <12 ms)**:
  - Upgraded from a 30 FPS `DispatcherTimer` with WPF property bindings to DWM VSync (`CompositionTarget.Rendering`, 60–144 Hz) calling direct transform updates (`SetLevelsImmediate`) with zero per-frame binding allocations.
  - Replaced per-frame `DropShadowEffect` shader blurs on rotating needles with hardware-native dual-stroke glow needles and 180 ms peak-LED hold.
  - Removed the 33 ms WASAPI capture throttle for unthrottled ~100 Hz lock-free Peak + RMS metering with frame-rate-independent 8 ms attack / 85 ms decay ballistics.

### Fixed
- **Preserved Receiver Volume on Source/Stream Changes**: Defaulted `UseLowVolumeForStreams` to `false` and removed automatic low-volume resets when launching YouTube tracks, PC Audio Cast, Local FLAC files, Global Radio Explorer streams, or system tray station presets.

## [1.0.0] - 2026-11-21

### Added
- **Obsidian Hi-Fi Control Deck**: Custom-chromed Windows 10/11 WPF console, OLED Now Playing deck with live artwork and 5-bar equalizer, clickable dB (`-80.0 dB` to `+18.0 dB`) / 0–98 precision master volume engine, full AVR hardware input/sound-mode/ECO/sleep/Zone 2 controls, and system tray companion with floating taskbar mini-flyout.
- **Zero-Config Auto-Discovery & Dynamic Model Detection**: Automatic SSDP + HEOS LAN receiver discovery on first launch, automatic hardware model & firmware detection (`DENON` / `MARANTZ` dynamic branding and model badge).
- **YouTube & YouTube Music Deck + Zero-Admin LAN Audio Relay**: Direct search/URL/playlist resolution (`YoutubeExplode`), zero-admin LAN HTTP relay (`:18925`) with FFmpeg 320 kbps MP3 transcoding and Direct M4A fallback, auto-advancing Up-Next queue, one-click `Allow Firewall` helper, and WebView2 Cinema Companion.
- **Smart Scenes & Live Acoustic Tuning Console**: 5 customizable AVR macro presets (`Movie Night`, `Late Night`, `Pure Hi-Fi`, `Morning Jazz`, `Party Multi-Ch`) with smooth volume ramping, Capture Live AVR snapshot, inline Scene Editor, plus live Telnet `:23` controls for Audyssey MultEQ, Dynamic EQ, Dynamic Volume, Audio Restorer, Front Panel Dimmer, Subwoofer/Dialog trims, and Tone Control.
- **Couch Phone Remote & Party Jukebox (QR Code Web UI)**: Mobile-first Obsidian web app served on port `18925` with in-app QR code for controlling volume, power, inputs, scenes, radio stations, and shared YouTube queue from any phone on Wi-Fi.
- **Cast Any PC Audio (WASAPI Loopback)**: Wireless live Windows system/device audio casting to Denon HEOS in MP3 320 kbps or Lossless WAV with silence keepalive.
- **40,000+ Global Radio Explorer & Local Hi-Res FLAC Jukebox**: Radio Browser directory search with FLAC/Hi-Res filtering and drag-and-drop bit-perfect local `.flac` / `.wav` / `.mp3` / `.m4a` streaming.
- **Zero-Lag Real-Time HEOS Push Events (`LIVE PUSH`) & Analog VU Meters**: Dedicated `register_for_change_events` push listener for `<50 ms` knob/remote sync and toggleable dual-channel illuminated VU dials.
- **Automated GitHub Releases Workflow**: `.github/workflows/release.yml` builds self-contained single-file `win-x64` executables and SHA-256 checksums on version tags.

[Unreleased]: https://github.com/ThomasRohde/DENON/compare/v1.11.1...HEAD
[1.11.1]: https://github.com/ThomasRohde/DENON/compare/v1.11.0...v1.11.1
[1.11.0]: https://github.com/ThomasRohde/DENON/compare/v1.10.1...v1.11.0
[1.10.1]: https://github.com/ThomasRohde/DENON/compare/v1.10.0...v1.10.1
[1.10.0]: https://github.com/ThomasRohde/DENON/compare/v1.9.0...v1.10.0
[1.9.0]: https://github.com/ThomasRohde/DENON/compare/v1.8.1...v1.9.0
[1.8.1]: https://github.com/ThomasRohde/DENON/compare/v1.8.0...v1.8.1
[1.8.0]: https://github.com/ThomasRohde/DENON/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/ThomasRohde/DENON/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/ThomasRohde/DENON/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/ThomasRohde/DENON/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/ThomasRohde/DENON/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/ThomasRohde/DENON/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/ThomasRohde/DENON/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/ThomasRohde/DENON/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/ThomasRohde/DENON/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/ThomasRohde/DENON/releases/tag/v1.0.0

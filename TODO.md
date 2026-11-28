# TODO

Open work for Denon Desktop and the external plugins. Only open items live here: when an item is done, remove it
and add a CHANGELOG entry (the CHANGELOGs and git history keep what was done). Keep each item to one action.

## Live checks (need you)

Each one needs the real devices, and most change them or play media, so they wait for you.

**Receiver and HEOS**
- [ ] Volume Memory: switch between the Apple TV, DR1 and TV 2 Charlie and check the volume follows; Morning Jazz
      keeps its own volume. During a Volume Memory fade, Reset Connections must not freeze the app.
- [ ] An input change on the remote gives one `input.changed`, not an extra one (seen once: TV → NET → TV, then
      TV → NET again).
- [ ] HEOS groups (#75): group a second HEOS player (it plays in another room), and check the group volume feels the
      same as the receiver's 0–98 scale.
- [ ] Jukebox with real files: the cover in Now Playing, the Hue colours from the cover, and the next track
      starting at the real end of a FLAC.
- [ ] A real arrival ("arrive home" Shortcut) with the receiver left on and HEOS stopped.
- [ ] Receiver setup check (Scenes & Acoustics → Setup check) on the real X2400H, with the receiver on and an input
      selected: "Check receiver" only reads. Look at the log (`PSREFLEV ?`, `PSCINEMA EQ. ?`, `PSDRC ?`, `PSLOM ?`,
      `PSLFE ?`, `PSDELAY ?` were never sent to this receiver) and at the rows: none should say CANNOT READ for a
      setting the X2400H has. If the receiver does not answer one, fix its read pattern in the bundled profile.
- [ ] Receiver setup check, per input: apply one safe row (Cinema EQ) on one input, switch to another input and read it
      there. The code assumes a `PS` write changes the selected input only, and that a read answers for it. If the
      write changes every input, the per-input rows in the profile become global.
- [ ] Receiver setup check, Undo: apply two rows, press Undo last apply, and check both values are back. Check the
      Reference Level Offset row (it needs Dynamic EQ on) and the Direct / Pure Direct rows (blocked by mode).

**Smart TV and casting**
- [ ] A wake from deep standby (TV off for hours) with "Chromecast on this TV" on Automatic.
- [ ] A cast from the iPhone while the TV is off: the cast stays (no switch to Live TV).
- [ ] The TV page's screen preview and trackpad still work, and "Wake TV with the receiver" without a MAC wakes the
      TV.
- [ ] Chromecast: add one by IP with Auto-Discover on, run Discover, and check the card stays connected and
      play/pause works; stop an app to Backdrop and check a media command says no app takes it; artwork shows
      (YouTube, and a LAN server such as Plex if there is one).
- [ ] Netflix on the Chromecast: check if play/pause commands work (it shares only "Netflix" as the title).

**Apple TV and HomePod**
- [ ] Pair the Apple TV again (a wrong PIN must refresh the page); now playing and artwork after wake and sleep; a
      wake automation cut short by sleep.
- [ ] Apple TV commands that change the TV (`key`, `launch-app`, `power-on/off`), and Denon automations on wake and
      sleep.
- [ ] Apple TV: the 35 s standby grace (power button → `standby`); Pause greyed out on DRTV live and enabled on DRTV
      on-demand; DRTV autoplay to the next episode (look for the log line "Playback queue shows …").
- [ ] HomePods: both speakers and the stereo pair link and show what plays; unplug one HomePod of the pair for one
      discovery pass and check the pair keeps its name and link.
- [ ] HomePod: a full listening session in Insights (play for more than 15 s), Apple Music started on the HomePod
      itself, and Play/Pause/Next.

**Hue and Home Assistant**
- [ ] Hue Music Sync on, restart the app: the log says "Started 50 Hz Hue Entertainment DTLS stream", and Hue moves
      to the station that plays on.
- [ ] Hue Entertainment area picker: with two areas, pick the other one while Music Sync streams.
- [ ] Home Assistant against a real server: the Input Booleans pill filters the tiles; a light switched in HA
      changes its tile at once (live updates); a wrong token shows "Home Assistant rejected the token" with no
      retry loop; a restarted HA server is picked up again.

**The app on the PC**
- [ ] YouTube and Cinema: Watch a track (Cinema opens once, after the cast), nudge the sync and check the next track
      keeps it; Vol + at the safe volume; "Allow in firewall" twice gives one rule. (The HUD buttons and Save Offset
      were checked on 2026-11-28.)
- [ ] Tune the Cinema Offset by ear (250 ms now; meters move slightly before the beeps with the metronome FLAC; try
      300–400 ms). It also times the lyrics and the video. If one value cannot fit all, add a separate meter trim.
- [ ] Meters: the DENON badge during PC Cast (in sync with the speakers) and the MUTED badge; ROOM mode with a real
      microphone (the mic opens and closes as the hints say, the gate and AGC on real TV audio, Hue beats).
- [ ] Accessibility: Tab through the main window in Dark and Light and check the focus ring; Narrator reads the
      symbol buttons, the command palette and the unsaved-changes banner; YouTube duration badges read well in Light.
- [ ] Uninstall with "Launch at Windows startup" on: the startup entry is gone. The next in-app update downloads,
      starts and shows notes without the download list.
- [ ] Insights with the real event log on the installed app.
- [ ] After the next release: `%TEMP%\.net\Denon.App.Desktop\` keeps only the current folder, and the log says
      "Deleted N old runtime extraction folders" (#94; tested only with a probe bundle).
- [ ] Station logos: the Deck and the phone remote show the TuneIn logo for a saved station (DR P6 Beat).
- [ ] Skins and windows: the first start on a small screen (1366 × 768, or 1920 × 1080 at 150 %) picks Compact; a
      second monitor with another scale opens at the right size; the tray flyout, Cinema window and the Chromecast
      and Apple TV pages in Light and in each skin; the plugin grid with six or more plugins in a narrow window.
- [ ] iPhone: remove the old phone-remote icon, scan the QR code, add it to the Home Screen again, and open it.
- [ ] MCP: the now-playing card's play/pause button, the cards in Claude Desktop, and which protocol version Claude
      Code and Claude Desktop choose.

## Decisions

- [ ] Should the Phone Remote's `/api/volume` (and so a Shortcut that calls it) use the safe-volume cap
      (`MaxSafeVolume`, 65)? MCP agents and automations are capped; the remote is not, because it is the owner at the
      couch.
- [ ] #63 (encrypt keys and tokens on disk): convert the saved keys once, or pair every device again? Do it with #78
      as one Plugin SDK update.
- [ ] LG TV actions `tv.button`, `tv.launch_app`, `tv.type_text` and `tv.open_url` work but are not listed in
      `GetActions` (the `tv_computer` MCP tool covers them). List them or not?
- [ ] User theme files (JSON tokens, validated on import; no loose XAML)?
- [ ] The receiver profile schema check uses JsonSchema.Net 9.7.0 (MIT source; the NuGet binary comes under the Open
      Source Maintenance Fee agreement: no fee below US$10,000 revenue, noted in THIRD-PARTY-NOTICES). Keep it, or
      replace it with a small validator of our own (the schema is simple) so no fee agreement is in the app?

## Releases

- [ ] Apple TV, HomePod and Chromecast plugins: the #49, #59 and #70 follow-ups are on master but not released
      (release only when you ask).
- [ ] Code signing for the installer and exe: removes the SmartScreen warning and lets the in-app update check who
      made the installer, not only that it arrived intact. A signed SHA256SUMS needs a signing key too (#62).
- [ ] Website and README screenshots (#73): `02-youtube-music` (the demo queue now starts with the promo video) and
      `13-plugin-smart-tv` are stale; Health, Help, Appearance and Insights have none (Insights needs made-up data,
      not the personal log). Needs the real app.

## Features (GitHub issues)

- [ ] #77: MCP audit log, per-tool and per-plugin allow/deny, a scoped webhook token, and an optional LAN-only guard.
- [ ] #78: shared plugin foundation: SDK WPF theming and skin helpers, a parameter form on the Plugins page for
      actions that need input, and shared DNS-SD and session logging for the external plugins.
- [ ] #63: encrypt tokens, pairing keys and private keys at rest with Windows DPAPI (see Decisions).
- [ ] #79 rest: artwork on the Apple TV and HomePod dashboard cards, and `includeImage` on their now-playing actions.
- [ ] #75 follow-ups: Zone 2 volume, source and mute and the group actions in the command palette (it has Zone 2 power
      only); pickers in the scene editor for the Zone 2 source and the group (a source list, the HEOS players as
      chips) instead of text fields.

## Known gaps and ideas

**Receiver**
- [ ] Remember optional queries (ECO?, SLP?, Z2?) that a model never answers, so a status read does not wait 550 ms
      for each. Some models do not answer in standby.
- [ ] The Brain reads the acoustics every 90 s (`RoomIntelligenceService`). It could read the PS/DIM pushes from
      `ReceiverStateSnapshot.Acoustics` and only check now and then.
- [ ] HEOS radio is one Volume Memory source ("HEOS Music"); stations differ in loudness. Idea: a key per station.
- [ ] Receiver setup check: profiles for other families (2018+ X-series, Marantz) from their protocol files; the
      bundled profile is checked against the X2400H manual and the protocol file only. Its per-model overrides are
      tested but unused. The forum sources behind some recommendations (Reference Level Offset by use, Restorer) could
      not be read, so they are second-hand. The sub trim has no target on purpose (sources disagree): it only warns
      at -12 and +12.
- [ ] Receiver setup check: a Smart Scene step that applies a use ("films") and an MCP tool that reads the check
      (read-only). Not built: the check holds the command gate for a few seconds, and a scene already holds it.

**Meters and radio**
- [ ] Radio meters: check the beat timing against the speakers; if needed, a radio delay setting
      (`RadioPlaybackClock.AssumedStartDelay`). A HEOS favorite, or a station another app started, has no URL for the
      app, so it stays simulated.
- [ ] A seek inside a jukebox track is not tested (the app has no seek bar).
- [ ] ROOM mode limit: a perfectly steady sound (a test tone, a hum) for more than ~8 s becomes the noise floor and
      closes the gate.

**Audio tags**
- [ ] Sort a dropped folder by disc and track number from the tags (today: by file path, which is right for
      "01 - Title" names).
- [ ] .ogg/.opus (Vorbis comments in Ogg pages), .wav (LIST INFO or an `id3 ` chunk), .wma (ASF) and raw .aac (no
      duration) give no tags yet.
- [ ] An MP3 duration from a Xing/Info header includes the encoder delay and padding (~50 ms). Subtract them with the
      LAME tag if gapless timing is ever needed.

**Telemetry and Insights**
- [ ] The external plugins' own sessions (Chromecast, Apple TV, HomePod) are lost on a crash; the app saves its own
      every minute (`open-sessions.json`). Same idea, in each plugin.
- [ ] `WatchTimeReport` (MCP `get_watch_time`) does not join restart splits; Insights does
      (`InsightsReport.JoinRestartSplits`). Share one helper.

**MCP**
- [ ] Ask instead of refusing: input-required results (`InputRequiredException` with an elicitation) to confirm a
      volume above the safe ceiling, power off, or which Chromecast. Needs a client that supports it.
- [ ] Room-state updates reach only session clients; 2026-07-28 clients get none until the SDK fans out resource
      updates over `subscriptions/listen` (csharp-sdk #1662).

**Automations and plugins**
- [ ] A node that two branches reach runs once per branch. A "join" node would need run state.
- [ ] Mute changes are not sent to plugins (volume changes are).
- [ ] The Automations editor and the Brain page save at once and have no unsaved-changes banner. If they get a Save
      button, add them to `SettingsEditorAreas`.
- [ ] Home Assistant: a slow REST poll while the live socket is down? And after a rejected token, a failing REST
      read can overwrite the host status line.
- [ ] Chromecast speaker groups (`Google Cast Group`) are skipped.
- [ ] HomePod: the pair's leader moves; the plugin follows it at the next discovery (10 min). A faster switch could
      follow the `igl` TXT change. "Interrupted" (MRP state 3) shows as paused; check what Siri, a timer or a
      disconnecting AirPlay sender report.
- [ ] HomePod next: volume (MRP output device volume), artwork for the dashboard and Insights, and the HomePod in
      Insights' RIGHT NOW.
- [ ] Apple TV: test more apps (Netflix, TV 2 Play, YouTube) for titles and series/episode fields.

**UI**
- [ ] From the computer-use run (2026-11-24): low contrast of "Synced …" and the version in the status line (Dark);
      "Normal 42" is highlighted at volume 41; QUICK INPUTS in the tray flyout marks no input; the Brain habits say
      "Top input: MPLAY" (the raw code).
- [ ] Settings, Scenes, Stations, Cast and the plugin pages have no screen-reader names yet (the main window has).
- [ ] An empty state for the YouTube, Stations and Cast pages; full dark ContextMenu templates.
- [ ] Channel logos: 16 of the 92 Danish cable channel names have no logo in tv-logos. Pin them with
      `ChannelLogoOverrides`; a UI to pin a logo is not built.
- [ ] A station with a wrong or missing TuneIn match cannot set its own logo. Idea: a logo address per station and a
      switch to turn the TuneIn lookup off. The logo comes over plain http, and the MCP now-playing card does not
      show it.

**Theming**
- [ ] Semantic aliases (Success/Warning/Danger/Info/Accent) over the hue families, so a theme can recolor states.
- [ ] A Plugin SDK theme API (`ThemeChanged`, token list) if a plugin ever draws with theme colors in code (see #78).
- [ ] The meters' drawn text sizes are literal (the dials scale as a whole, so skins do not need them).

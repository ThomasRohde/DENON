# TODO

Open work for Denon Desktop and the external plugins. Keep this file current: add items when work is found or
left open, and remove them (with a CHANGELOG entry) when they are done.

## Code review issues #28–#79 (Antigravity, 2026-11-28)

All 52 issues were checked against the code. The plan is 13 PRs, one area each, done one at a time. Feature
requests stay open for later: #63, #75, #76, #77, #78, #79 (#74 is done, see below).

- [x] PR 1: guest input security. #29 closed: a phone track gets its watch and thumbnail address from the video
      id. Part of #31: values ride in data-* attributes (no inline JavaScript), and only hex colours go into styles.
      Part of #64: the room-dev-deck harness checks Host and Origin and does not crash on bad JSON.
- [x] PR 2: receiver and HEOS. #28, #30, #38, #39 and #43 closed; the receiver parts of #66 (HEOS-only scenes and
      Fix silent audio, SSDP on every interface, the Try* cancel filter, the under-process reset).
- [x] PR 2 read-only live check (2026-11-28): status reads 1.1–1.25 s against 2.2–3.2 s before, same values,
      Zone 2 on read right with main zone off.
- [ ] Verify PR 2 in the app: a Volume Memory fade, and Reset Connections while the volume slider moves.
- [ ] Not done in PR 2: remember optional queries (ECO?, SLP?, Z2?) that a model never answers, so a status read
      does not wait 550 ms for each. Needs care: some models do not answer in standby.
- [x] PR 3: Home Assistant and Hue. #32, #46 and #48 closed; the HA part of #68, the Hue part of #64 (no HTTP
      fallback with HTTPS on, error answers on reads).
- [ ] Verify PR 3 live: Hue Music Sync on, restart the app, and check the log says "Started 50 Hz Hue Entertainment
      DTLS stream". Needs the user's OK (it changes the lights).
- [ ] PR 4: MCP tiers and limits: #61, #65, the MCP part of #66, the /health Host check in #64.
- [ ] PR 5: media relay and Core services: #31 headers, #36, #37 Jukebox 45 s cut, #40, #41, #42, #67, :18925
      items of #64.
- [ ] PR 6: plugin host: #44, #45, #69, the CreateHttpClient part of #64.
- [ ] PR 7: Smart TV plugin: #34, #47, the Smart TV part of #68.
- [ ] PR 8: YouTube and Cinema: #33, #51, #52, #56, the Cinema parts of #53 and #71. Add a Cinema guard too: open
      and navigate only http/https (defense in depth for #29).
- [ ] PR 9: desktop shell: #53, #54, #55 (tray shows dB; show the 0–98 number), #71.
- [ ] PR 10: release, CI, installer, docs: #58, #59, #60, #62, #72, #73.
- [ ] PR 11: accessibility (after PR 9): #57.
- [ ] PR 12: Apple TV and HomePod plugins: #35, #50, #70, the HomePod part of #49 (plugin release only when asked).
- [ ] PR 13: Chromecast plugin and first tests: #49, the Chromecast parts of #59 and #70 (release only when asked).
- [ ] Not in the review: the Phone Remote volume shows dB (`fmtDb` in `RemoteWebUiHtml.cs`). Show the 0–98 number.

## Live receiver updates on port 23 (#74)

The reader loop is done: `DenonAvrClient` reads every line, keeps `AvrLiveState` per link, and the controller
patches `ReceiverStateStore` at once. `ReceiverPollingService` slows to 9 × Poll Interval (30–60 s) while the link
holds and the HEOS events are connected.

- [x] Read-only live check (2026-11-28, AVR-X2400H): slow check after 7 s ("full check every 45 s"); the knob,
      mute on/off and input changes from the remote all came in as pushes, between the 45 s checks. It also showed
      that state changes could be raised out of order from two threads; fixed (one ordered delivery).
- [ ] Check in the released app that an input change on the remote leaves no extra input.changed (in the live check
      TV → NET → TV was followed by one more TV → NET; not yet known if the receiver really switched).
- [ ] #75 (HEOS groups and Zone 2 controls) builds on this: Zone 2 source and volume lines (Z2TV, Z240) are read by
      `AvrLiveState` only for power today.
- [ ] The Brain still reads the acoustics every 90 s (`RoomIntelligenceService`). With PS/DIM pushes in
      `ReceiverStateSnapshot.Acoustics`, it could read them from the state and only check now and then.

## Command palette and shortcuts (#76)

Part 1 is done: the Ctrl+K palette (`Denon.App.Desktop/CommandPalette/`). It waits for PR 8 and PR 9 (they change
MainWindow and MainViewModel), then:

- [ ] Bind Ctrl+K in `MainWindow` to `OpenCommandPaletteCommand` and delete the class handler in
      `CommandPaletteHotkey` (a temporary hook so part 1 did not touch MainWindow).
- [ ] Add Ctrl+K to the reserved gestures (the shortcut editor must refuse it) and to the Help shortcut list.
- [ ] Ctrl+1–0 in nav rail order (Brain included), read from `CommandPalettePages.All`, synced with the reserved
      gestures and Help.
- [ ] Unsaved-changes banner (`IsSettingsDirty`) for Settings, Scenes and Stations, and a scoped `LoadSettings` so
      saves of Automations or Volume Memory do not overwrite fields that are being edited.
- [ ] PR 11 (accessibility): check the palette with Narrator (the status line is a polite live region).
- [ ] Release after Ctrl+K is in the reserved gestures and Help. Until then a custom in-app shortcut on Ctrl+K
      is swallowed by the palette.

## VU meters & RTA (follow the Denon)

Phase 1 is done in 1.15.1 (source router, DENON/SIM/MUTED/PC/IDLE badge, PC Cast meters synced to the HEOS delay).
Phase 2 is done in 1.18.0: YouTube / YouTube Music and jukebox tracks are decoded by a separate FFmpeg process
(`AppStreamMeterService`) and shown at the playback position. Verified live with YouTube Music and a jukebox FLAC
(metronome test track) on 2026-11-23.

- [ ] **Tune the Cinema Offset** (Settings): with the metronome FLAC the meters move very slightly before the beeps.
      The offset is 250 ms (Auto-Calibrate with a WAV chirp, 2026-11-22); FLAC/YouTube likely buffer a bit longer in
      HEOS. Try 300–400 ms by ear. The same value times lyrics and the Cinema video. If one value cannot fit all,
      add a separate meter trim.
- [ ] A seek inside a jukebox track is not tested (the app has no seek bar; a range read does not restart the clock).
- [x] **Phase 3: opt-in "ROOM" mode (PC microphone)** for TV/ARC, HDMI and Chromecast audio that the app cannot tap.
      Implemented (`RoomMicDspProcessor` 35 Hz HPF, adaptive noise floor + soft-knee noise gate, gated smooth AGC,
      privacy-first `RoomMicGate` that releases the mic immediately on standby/mute/disconnect/hidden meters/opt-out,
      `ROOM · MIC` / `ROOM MIC FFT` badges, and Settings toggle + capture device picker, off by default).
- [x] **Philips Hue Music Sync** now follows the same meter router (`LiveAudioAnalyzerService` routed ~60 Hz
      `PluginAudioFrame` publisher: HEOS-delayed PC Cast, synced YouTube/jukebox frames, active Room Mic, silence on
      mute/simulated, and PC loopback fallback when the Denon is off or idle).
- [ ] Verify live: the **DENON** badge during PC Cast (meters in sync with the speakers) and the **MUTED** badge.
- [ ] Verify live: ROOM mode with a real microphone (only tested with synthetic audio): the mic opens and closes as
      the hints say (no mic at a start in the tray; Hue Music Sync keeps it open with the meters hidden), the gate
      and AGC on real TV audio, and Hue beats. Known limit: a perfectly steady sound (a test tone, a hum) for more
      than ~8 s becomes the noise floor and closes the gate; program audio with normal dips stays open.
- [x] RTA during PC Cast from a non-default output device no longer falls back to SIM: `PcCastAudioMeterPipeline` runs
      a 512-pt 31-band FFT directly on the PC Cast capture stream (both default and non-default output devices).

## Audio tags for the Jukebox (#79, part 2)

`AudioTagReader` (`Denon.App.Core/Media`) reads title, artist, album, track number, duration and cover art from
FLAC, MP3 and M4A/MP4 files, with no NuGet package. Nothing uses it yet.

- [ ] Wire the reader into the Jukebox (`LocalMediaJukeboxService`: track titles, sort by track number, the real
      duration for auto-advance), Now Playing (cover art) and the Hue album-art palette. Do this after Antigravity
      PR 5 merges: that PR changes the Jukebox, the relay and the YouTube queue.
- [ ] Keep the Jukebox and YouTube queues across restarts (the other half of #79 CORE-21).
- [ ] Other Jukebox formats give no tags yet: .ogg/.opus (Vorbis comments in Ogg pages), .wav (LIST INFO or an
      `id3 ` chunk), .wma (ASF) and raw .aac (ADTS: an ID3v2 tag is read, but no duration).
- [ ] An MP3 duration from a Xing/Info header includes the encoder delay and padding (about 50 ms). Subtract them
      with the LAME tag if gapless timing is ever needed.

## Habit telemetry

- [ ] Events written before 1.15.0 have no `coveredSec` / `exclusiveSec`. Readers must fall back to `durationSec`.
- [x] **Open sessions lost when the app is killed** (2.9.3): the monitor saves its open sessions (receiver, LG TV)
      every minute to `logs/events/open-sessions.json`; the next start closes them with reason `app_crashed`.
- [ ] The plugins' own sessions (Chromecast, Apple TV, HomePod) are still lost on a crash: they keep them in the
      plugins' watch logs. Same idea, in each plugin.
- [ ] `WatchTimeReport` (the MCP `get_watch_time` tool) does not join restart splits yet; Insights does
      (`InsightsReport.JoinRestartSplits`). Share one helper.
- [x] The plugin changes from 2.9.3 (discovery log lines, cast playback hold) are released: Chromecast 0.5.1, Apple TV
      0.5.1 and HomePod 0.2.1 (2026-11-26).

## Insights page

- [ ] Verify live on the installed app with the real event log (tested with the dev build on 40 days of made-up
      events, Dark/Hi-Fi and Light/Default, Today to 90 days, phone width).
- [x] Added two additional entry points to Open Insights: on the Brain page (`LEARNED LOCAL PREFERENCES` card) and in
      the `MainWindow` bottom status line.
- [ ] A website screenshot of Insights (needs made-up data, not the personal log).
- [x] Covered receiver-input sessions in the activity feed: `ActivityFeed.Describe` now marks both `session.started` and
      `session.ended` quiet once a receiver-input session ends covered by a Chromecast or Apple TV session (uncovered
      and still-running sessions stay visible).

## MCP server (C# SDK)

On the official MCP C# SDK since 2.10.1 (`DenonMcpServer`, `ModelContextProtocol.AspNetCore` 2.4, session mode
`StatefulForInitializeClients`).

- [ ] Ask instead of refusing: use input-required results (`InputRequiredException` with an elicitation) to confirm a
      volume above the safe ceiling, power off, or which Chromecast. Needs a client that supports it (`IsMrtrSupported`
      or a session with elicitation).
- [x] MCP Apps: the now-playing card (`show_now_playing`, `ui://denon/now-playing.html`, 2.10.3).
- [x] The card works in the Codex desktop app (2.10.3): it renders in standby and on; volume −/+ and mute/unmute
      from the card change the receiver and the card updates.
- [ ] Try the card's play/pause button, and the card in Claude Desktop.
- [x] Live check (2.10.4): Morning Jazz shows "DR P8 Jazz" in the app and the MCP card, and the P8 tile is ON AIR.
- [x] More MCP Apps: room remote, automation preview, Insights, radio search, Volume memory (2.10.5).
- [x] The five new cards render in Codex (2.10.5); the automation card's Open button works through the host.
- [x] The cards' buttons work in Codex (2.10.5): station, input, sleep timer, power, automation off/on, volume
      memory −/+, radio Play and Save. The cards read the receiver again 2.5 s after a button (2.10.6).
- [x] Stations page: Save Changes does not save (no "Settings updated" in the log); Save & Sync in Settings does.
      Cause not found: on 2.10.5 and 2.10.6 the button saved both from UI Automation and from a real click. A refused
      or failed save now logs a warning with its reason and shows it on the Stations page.
- [ ] Room-state updates reach only session clients (initialize, 2025-11-25). 2026-07-28 clients get none: the SDK does
      not yet fan out resource updates over `subscriptions/listen` (csharp-sdk #1662).
- [ ] Verify live with Claude Code / Claude Desktop after the release (which protocol version they choose).

## Volume memory

Learns the listening volume per source and sets it on a source change (2.10.0, `VolumeMemoryService`,
`volume-memory.json`).

- [ ] Verify live: switch between the Apple TV, DR1 and TV 2 Charlie and check the volume follows; check a Smart Scene
      (Morning Jazz) keeps its own volume.
- [ ] HEOS radio is one source ("HEOS Music") for now; stations differ in loudness too. Idea: a key per station.
- [ ] MCP: no tool reads or sets the learned levels yet.

## Automations

Automation graphs (trigger → conditions → actions), the node editor (`Denon.App.Desktop/Automations`, served by the
Insights server in a WebView2 window), webhooks and the live `RoomEventBus` are in 2.9.0. See
`docs/automation-graphs.md`. Next: Claude writing graphs through the `architect_automation` MCP
prompt is wired (`create_or_update_automation`) but not tried with a real agent.

- [x] **A real iPhone Shortcut over Wi-Fi works** (2026-11-25): "arrive home" on joining the home Wi-Fi runs Morning
      Jazz. The reply now says what each automation did; check it on the iPhone with Show Result after 2.9.2.
- [x] **Arrival plays when the receiver was left on but idle** (2.10.12): the receiver condition has "Standby or
      idle" (on with HEOS stopped or paused). On 2026-11-27 four arrivals did nothing, because a test left the receiver
      on with the stream stopped. Another input (TV, Apple TV) never counts as idle, since the app cannot see that a
      TV is on.
- [ ] Check a real arrival with the receiver left on and HEOS stopped after 2.10.12.
- [x] Phone remote (2.10.12): the pairing code stays in the address for "Add to Home Screen"; a Turn on/Turn off
      button; the picture is not reloaded on each refresh.
- [ ] Check on the iPhone: remove the old home-screen icon, scan the QR code, add it again, and open it.
- [x] TV power on (2.10.12): Wake-on-LAN every second for 15 s (broadcast and direct), then HDMI-CEC through the
      Chromecast (`wake-tv`, Chromecast plugin 0.6.0), then Live TV. On 2026-11-27, 9 taps at 14:30 got no answer.
- [x] Released Chromecast plugin 0.6.0 (`wake-tv`), so the CEC fallback works.
- [x] Checked on the real TV on 2026-11-28: Wake-on-LAN got no answer for 15 s, the Chromecast "Stuen 2" woke the TV
      over HDMI-CEC, and the TV went to Live TV. The first try failed: two Cast devices were known (one offline).
- [x] "Chromecast on this TV" on the Smart TV page; "Automatic" is the only device known, else the only one online.
- [ ] Check a wake from deep standby (TV off for hours) with "Automatic".
- [x] "TV on → Live TV" looks again 10 s after the switch (HDMI-CEC pulled the TV back to the Chromecast on
      2026-11-28) and ends with the receiver on the TV input.
- [x] Checked live on 2026-11-28: TV on → Live TV and TV audio works.
- [ ] Check live: a cast from the iPhone while the TV is off → the cast stays.
- [x] TV page tabs (Watch, Remote, Apps, Settings); the last tab is kept (`DashboardTab`). Checked in the app.
- [x] Remote tab: one card, TV screen + "Remote" or "Trackpad" (kept in `RemotePanel`) + one keyboard line. Checked in the app.
- [x] "TV on → Live TV" example and the "Recent event" condition (2.10.12). Not when a cast or the Apple TV woke the TV.
- [ ] Check "TV on → Live TV" live: TV remote on (HDMI 1 → Live TV), and a cast from the iPhone (stays on the cast).
- [x] Station logos are cached in memory and in `art-cache`; the last started station is kept in `last-station.txt`
      so a stream that plays at app start shows its name and logo (2.10.12).
- [ ] **Decide:** should the Phone Remote's `/api/volume` use the safe-volume cap (`MaxSafeVolume`, 65)? MCP agents and
      automations are capped; the Phone Remote, and so a Shortcut that calls it, only stops at the receiver's maximum.
      Left as it is because the remote is the owner at the couch.
- [ ] A node that two branches reach runs once per branch. Fine for now; a "join" node would need run state.
- [ ] `PluginReceiverEventType.VolumeChanged` is declared but never sent, and mute changes are not sent to plugins.

## MCP: plugin tools

The bridge returns JSON + images and uses declared parameters/hints since app 1.16.0 / SDK 1.16 (Chromecast plugin 0.2.0 uses it).

- [x] The **Smart Scene editor** (and Automation Rule editor) now includes a parameter editor driven by
      `PluginActionDescriptor.Parameters` (`Name`, `Type`, `Description`, `Required`) when adding steps and inline on
      existing steps, with live required-field indicators and persisted `Parameters`.
- [ ] LG TV actions `tv.button`, `tv.launch_app`, `tv.type_text`, `tv.open_url` are implemented but not listed in
      `GetActions` (the curated `tv_computer` MCP tool covers them). Decide if they should be listed.

## Chromecast plugin

- [ ] Only one Cast device (Stuen 2, 192.168.1.20) is online. The second device the user expects was not found by
      mDNS or by a port-8009 scan on 2026-11-23. Check if it is off, in deep standby, or on another network.
- [x] Documented Windows **Private vs. Public** network profile for mDNS in `external-plugins/Denon.Plugin.Chromecast/README.md`.
- [ ] Speaker groups (`Google Cast Group`) are skipped.
- [ ] Stuen 2 lost its link 3 times between 02:57:09 and 02:57:20 on 2026-11-24, with no app restart near it
      (reconnected at 02:57:32). Find out why, and whether the reconnect needs a back-off.
- [ ] Netflix shares only "Netflix" as the media title (no program or episode), so all Netflix viewing is one session
      per app run (`hasTitle: false` since plugin 0.1.1). It does report PLAYING/PAUSED, so `watchedSec` is right.
      Check if play/pause commands work for Netflix.
- [x] Extended `CastAppNames` with verified Nordic and major Cast app IDs (YouTube Music, Prime Video, Viaplay, SVT Play,
      NRK TV/Radio, Yle Areena, Twitch, Plex, Jellyfin, BBC iPlayer/Sounds, SoundCloud, etc.) plus display-name cleanup
      for dynamic-ID services (TV 2 Play, Max, SkyShowtime, Apple TV+).

## HomePod plugin

- [x] Go/no-go (2026-11-24): transient pairing and MRP on HomePod mini, HomePod software 27.2.1; read "DR P4 København"
      live from the stereo pair. Plugin 0.1.0 built: discovery, stereo pair as one speaker, now playing, commands,
      listening sessions, pause on receiver on. Verified live in the app: both speakers linked, the dashboard shows
      what plays.
- [ ] Verify live: a full listening session in Insights (play something for longer than 15 s), Apple Music started on
      the HomePod itself (no AirPlay client message), and that Play/Pause/Next work (ask first: it plays audio).
- [ ] The pair's leader moves (Left, then Right). The plugin follows it at the next discovery (10 min); a faster
      switch could follow the HomePod's `igl` TXT change. Check that a leader change keeps the listening session.
- [ ] "Interrupted" (MRP state 3) shows as paused in the status line; check what the HomePod reports after Siri, a
      timer or an AirPlay sender that disconnects, and whether it should end the session sooner.
- [ ] Next: volume (MRP volume for the output device), artwork for the dashboard and Insights, and the HomePod in
      Insights' RIGHT NOW.

## Apple TV plugin

Native .NET (no Python), protocol logic ported from pyatv (MIT). See `external-plugins/Denon.Plugin.AppleTv/README.md`.

- [x] Milestone 0: discovery. Found "Stuen (2)", Apple TV 4K (1st gen), tvOS 26.6.1: Companion :49153, AirPlay :7000,
      no `_mediaremotetv` (now playing must use the AirPlay tunnel).
- [x] Milestone 1: HAP pairing + Companion. Paired live on 2026-11-23 (first try, M6 signature verified); power
      state, app list (43 apps) and events work. 27 tests incl. an encrypted loopback session.
- [ ] Milestone 1 rest: verify live the commands that change the TV (`key`, `power on/off`, `launch`); needs the
      user's OK each time.
- [x] Milestone 2: MRP now playing over AirPlay 2 — **GO** (2026-11-23). Works with the Companion pairing (no
      second PIN). Live: DRTV app, program titles and Playing/idle, pushed within ~1 s when the stream starts or
      stops. 34 tests (plist cross-checked with Python, real DRTV message sequence).
- [ ] Milestone 2 rest: artwork (PLAYBACK_QUEUE_REQUEST), test more apps (Netflix, TV 2 Play, YouTube) for titles
      and series/episode fields, and a long-running session (reconnect after the Apple TV sleeps).
- [x] Milestone 3: full plugin — **success** (2026-11-23). Live with the dev build: pairing from the dashboard,
      now playing in the dashboard and the hero (the Apple TV is on Denon input MPLAY), MRP play/pause (DRTV
      on-demand pauses; DRTV live answers "command failed"), watch sessions, `app_shutdown` on app exit, sleep
      detected and the now-playing link closed, no wake in 3.5 min of Companion polling while asleep.
- [x] Power button on the remote: `AppleTvWatchLog` now uses a 35 s `StandbyGracePeriod` before finalizing `app_closed`
      (freezing duration/watchedSec/avgVolume at stop time) so remote power-off logs `standby` when tvOS reports
      `Asleep` within 35 s.
- [x] Disable/grey out the pause button when the app does not support pause: `MrpNowPlaying` parses `supportedCommands`
      from `SET_STATE`, exposes `canPause` / `"APPLE TV LIVE • …"`, disables Pause with tooltip on the Apple TV
      dashboard, and gates the hero & tray flyout `PlayPauseCommand` via `CanPauseTransport`. Only an explicit
      `Pause` command counts (the app sends Pause, not TogglePlayPause).
- [ ] From the computer-use test run (2026-11-24), not fixed yet: the left rail's buttons have no accessible names
      (UI Automation sees them as empty; screen readers and test agents cannot find "Help" or "Brain"); low contrast of
      "Synced …" and the version in the status line (Dark); "Normal 42" is highlighted at volume 41; QUICK INPUTS in the
      tray flyout marks no input; the Brain habits say "Top input: MPLAY" (raw code); Room Doctor gives no sign that a
      Rescan ran. F-02 (Help quick jumps do nothing) did not reproduce with real mouse clicks.
- [ ] Verify live: a Live TV channel without guide data shows the channel only (the program boundary works: seen
      2026-11-24, DR1 "TVA" 17:15 in the hero and as its own watch-log session with the logo).
- [ ] Channel logos: 16 of the 92 channel names on the Danish cable list have no logo in tv-logos (Viaplay Sport 1–3,
      CNBC, Bloomberg, France 2, Rai 1, Discovery ID and local info channels). Pin them with `ChannelLogoOverrides`, or
      add more name rules if a pattern shows up. A UI to pin a logo is not built yet.
- [ ] Verify live: the 35 s standby grace (power button → `standby`), and that Pause is greyed out on DRTV live and
      enabled on DRTV on-demand.
- [ ] Verify live with the user's OK: Denon automations on wake/sleep, `key`, `launch-app`, `power-on/off`.
- [ ] DRTV autoplay: when DRTV goes on to the next episode by itself, tvOS reports no player until the user
      pauses/resumes (seen 2026-11-23: "Løftet" -> "Straffen"; 7 stale paused avkit players, no now-playing client).
      0.3.0 asks for the playback queue once a minute while nothing is reported, and shows a player that starts
      playing without a client. Not verified live yet: check at the next autoplay (log line "Playback queue shows …").
- [x] DRTV artwork for the hero: fetched with PLAYBACK_QUEUE_REQUEST, saved in the plugin data folder (0.3.0;
      fetched live 2026-11-23 for "Maria Wern: Straffen").

## Theming and skinning (2.0.0)

Preparation done on branch `theming-prep`: every UI color is a token in `Denon.App.Desktop/Themes/Colors.Dark.xaml`
(neutral ramp `Ink00`–`Ink16`, text levels, accent families, overlays) with brushes in `Themes/Brushes.xaml`;
styles stay in `Themes/DenonTheme.xaml`. Code reads tokens through `ThemeResources` (app) and `HostTheme` (built-in
plugins); the meters read `MeterPalette`. `ThemeTokenGuardTests` blocks new hex colors and unknown keys.

Design and decisions: `docs/theming.md` (branch `theming-2.0`).

- [x] Themes: Dark, Light and Follow Windows, live switch (`DynamicResource` everywhere, ~900 references; bindings and
      code-assigned theme brushes are refreshed by `ThemeManager`). Settings → Appearance.
- [x] Palette contract tests (same keys, contrast floors) and a guard against `StaticResource` theme references.
- [x] Skin decisions (2026-11-23, `docs/theming.md`): tokens only (type, radii, spacing, icons, meter materials),
      live switch, built-in skins only, any skin with any theme, all plugin pages follow, bundled open fonts allowed.
- [x] Skins, live: Default, Hi-Fi (Bahnschrift + bundled Share Tech Mono, amber VU, teal fluorescent LEDs), Compact,
      Large. Skin tokens in `Themes/Skins/*.xaml`; spacing and sizes via `{skin:Space}`/`{skin:Size}` (two scales over
      Default pixels); `SkinChanged` redraws the meters; skin chips in Settings → Appearance; `SkinTests`.
- [x] Light meters: each skin has a dark and a light meter set (cream VU faces, light LED screen); the theme picks
      it, live (`MeterPaletteTests`).
- [x] Plugin pages follow the skin (`HostSkin` in each plugin; `HostSkin.Follow` for the code-built Chromecast and
      Apple TV pages). Screenshots checked for every skin in Dark and Large in Light, start and live switch.
- [x] Released Chromecast plugin 0.5.0 and Apple TV plugin 0.4.0 (theme brushes read at each use, `HostSkin`).
- [x] First start: splash screen, window fitted to the monitor and skin (Compact or Large picked on the first
      start), auto-grow until the user sizes the window, saved placement, version in the status line, Help updated.
- [ ] Check live on a small screen (1366 × 768 or 1920 × 1080 at 150 %) that the first start picks Compact, and on a
      second monitor with another scale that the window opens there at the right size.
- [ ] Check live in the installed app: tray flyout, YouTube cinema window, Chromecast and Apple TV pages in Light and
      in each skin (screenshot mode does not show them).
- [x] Help topic badges and scene colors on Light: added WCAG 2 luminance `BadgeContrastHelper` (>= 4.5:1 contrast) so
      solid scene/station badges choose the highest-contrast theme ink with a subtle border, and Help/Plugin accent
      badges adapt on Light surfaces while leaving Dark untouched (live refresh on `ThemeChanged`).
- [ ] Decide user theme files (JSON tokens, validated on import; no loose XAML).
- [ ] Semantic aliases (Success/Warning/Danger/Info/Accent) over the hue families, so a theme can recolor states.
- [ ] Plugin SDK theme API (`ThemeChanged`, token list) if a plugin ever draws with theme colors in code.
- [x] Split `MainWindow.xaml` (4,500 lines) into one view per page (`Views/*View.xaml`); the window keeps the
      title bar, sidebar and page wrappers.
- [x] Built-in plugins (Hue, Smart TV, Home Assistant): use the app's card and button styles (sizes stay local).
- [x] Hue Music Sync and the Deck meters follow internet radio that the app starts (FFmpeg decodes a copy; 2.10.10). Checked live
      on DR P8 Jazz: Deck meters DENON · SYNCED, Hue LIVE FFT SYNC with beats. During speech (news) the badge
      blinked to WAITING FOR AUDIO in pauses (~18% of frames below −50 dBFS); the badge now holds 2 s (2.10.11).
- [ ] Radio meters: check the beat timing against the speakers and, if needed, a radio delay setting
      (`RadioPlaybackClock.AssumedStartDelay`, the "play" event). A station HEOS already played before the app
      started (or a HEOS favorite) has no URL for the app, so it stays simulated.
- [x] App styles for ComboBox, ListBox and PasswordBox; the external plugins dropped their own ComboBox template.
- [x] One chip style (`ChipButton`/`SuccessChipButton` + `infra:Chip.IsActive`) instead of 23 inline copies; page and
      sub-tab switching via the `EqualityToVis` converter instead of 14 inline styles. Zone 2 keeps its own style.
- [x] Typography and shape tokens in `Themes/Typography.xaml`: type scale `FontSize00`–`FontSize14`, radius scale
      `Radius00`–`Radius09`, fonts `DisplayFont`/`UiFont`/`MonoFont` (610 sizes, 175 radii, 64 font names replaced).
- [x] Chromecast and Apple TV pages read font sizes and corner radii from the host tokens (`HostTheme.FontSize`/`Radius`).
- [ ] The meters' drawn text sizes are still literal (the dials scale as a whole with their height, so skins do not
      need them).
- [x] Icon set in `Themes/Icons.xaml`: 21 named geometries (sidebar, title bar, transport, chevrons, tick), 25 uses.

## Station logos & plugin list

- [ ] Verify live: the Deck and the phone remote show the TuneIn logo for a saved station (DR P6 Beat). Only tested
      with a fake directory and in the build; the receiver was not touched.
- [ ] The logo comes over plain http (the TuneIn logo CDN has no https). The MCP now-playing card does not show the
      station logo yet.
- [ ] A station with a wrong or missing TuneIn match has no way to set its own logo. Idea: a logo address per
      station on the Stations page, and a switch to turn the TuneIn lookup off.
- [ ] Verify live: the plugin grid with six or more plugins (the screenshot mode has three), in a narrow window and
      with the Large skin.

## Settings saves

- [x] Eliminated all three `_suppressNextSettingsReload` sites: dB toggle now uses `SaveUiStateAsync`, and Phone Remote
      toggle/token rotation plus acoustic lip-sync calibration use atomic `AppSettingsStore.UpdateQuietAsync`
      (respecting corrupt-file recovery with no editor reloads or HEOS reconnects on overlapping saves).

## In-app updates

- [x] Verify live: the first real update (2.3.0 → 2.4.0 on the user's PC, Update → Download and install) worked.
- [x] Plugin updates (Chromecast `chromecast-v*`, Apple TV `appletv-v*`) through the same check (`UpdateService.CheckForUpdatesAsync`
      checks app + external plugins in a single request and surfaces them with release notes and release-page links in
      the status line and update panel).
- [ ] Code signing would let the update check who made the installer, not only that it arrived intact.

## Release / build

- [ ] The installer and exe are unsigned (SmartScreen warning). Consider code signing.
- [ ] Take the README and site screenshot `02-youtube-music` again: the demo queue now starts with the promo video
      (https://www.youtube.com/watch?v=2BL0w4gOYfA). The images in `docs/screenshots` and `site/public/shots` still
      show the old queue.

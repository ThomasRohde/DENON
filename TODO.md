# TODO

Open work for Denon Desktop and the external plugins. Keep this file current: add items when work is found or
left open, and remove them (with a CHANGELOG entry) when they are done.

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
- [ ] **Phase 3: opt-in "ROOM" mode (PC microphone)** for TV/ARC, HDMI and Chromecast audio that the app cannot tap.
      Needs AGC, a noise gate, a clear "mic on" badge and a setting (off by default).
- [ ] **Philips Hue Music Sync** still reacts to the live PC output (`PluginHostService.PublishAudioFrame`).
      Decide if it should follow the same router (and the HEOS delay during PC Cast).
- [ ] Verify live: the **DENON** badge during PC Cast (meters in sync with the speakers) and the **MUTED** badge.
- [ ] RTA during PC Cast from a non-default output device falls back to SIM. Option: run the FFT on the cast capture itself.

## Habit telemetry

- [ ] Events written before 1.15.0 have no `coveredSec` / `exclusiveSec`. Readers must fall back to `durationSec`.

## MCP: plugin tools

The bridge returns JSON + images and uses declared parameters/hints since app 1.16.0 / SDK 1.16 (Chromecast plugin 0.2.0 uses it).

- [ ] The **Smart Scene editor** cannot set action parameters. A scene step for an action with a required
      parameter (for example `ha.trigger_entity` → `entityId`) fails with "Missing 'entityId' parameter". Add a
      parameter editor driven by `PluginActionDescriptor.Parameters`.
- [ ] LG TV actions `tv.button`, `tv.launch_app`, `tv.type_text`, `tv.open_url` are implemented but not listed in
      `GetActions` (the curated `tv_computer` MCP tool covers them). Decide if they should be listed.

## Chromecast plugin

- [ ] Only one Cast device (Stuen 2, 192.168.1.20) is online. The second device the user expects was not found by
      mDNS or by a port-8009 scan on 2026-11-23. Check if it is off, in deep standby, or on another network.
- [ ] Windows marks the Wi-Fi as a **Public** network. mDNS works for the installed app (it has an inbound rule),
      but consider documenting "set the network to Private".
- [ ] Speaker groups (`Google Cast Group`) are skipped.
- [ ] Netflix shares only "Netflix" as the media title (no program or episode), so all Netflix viewing is one session
      per app run (`hasTitle: false` since plugin 0.1.1). It does report PLAYING/PAUSED, so `watchedSec` is right.
      Check if play/pause commands work for Netflix.
- [ ] Extend `CastAppNames` with more app ids as they show up in the logs (for example TV 2 Play, Prime Video).

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
- [ ] Power button on the remote: the program ends ~30 s before tvOS reports Asleep, so the session ends with
      `app_closed`, not `standby`. Consider a short grace period before the end reason is chosen.
- [ ] Hide or grey out the hero's pause button when the app does not support pause (read `supportedCommands`
      from SET_STATE).
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
- [x] Plugin pages follow the skin (`HostSkin` in each plugin; `HostSkin.Follow` for the code-built Chromecast and
      Apple TV pages). Screenshots checked for every skin in Dark and Large in Light, start and live switch.
- [x] Released Chromecast plugin 0.5.0 and Apple TV plugin 0.4.0 (theme brushes read at each use, `HostSkin`).
- [ ] Check live in the installed app: tray flyout, YouTube cinema window, Chromecast and Apple TV pages in Light and
      in each skin (screenshot mode does not show them).
- [ ] Help topic badges and scene colors are content colors (not themed): check that they read on Light.
- [ ] Decide user theme files (JSON tokens, validated on import; no loose XAML).
- [ ] Semantic aliases (Success/Warning/Danger/Info/Accent) over the hue families, so a theme can recolor states.
- [ ] Plugin SDK theme API (`ThemeChanged`, token list) if a plugin ever draws with theme colors in code.
- [x] Split `MainWindow.xaml` (4,500 lines) into one view per page (`Views/*View.xaml`); the window keeps the
      title bar, sidebar and page wrappers.
- [x] Built-in plugins (Hue, Smart TV, Home Assistant): use the app's card and button styles (sizes stay local).
- [x] App styles for ComboBox, ListBox and PasswordBox; the external plugins dropped their own ComboBox template.
- [x] One chip style (`ChipButton`/`SuccessChipButton` + `infra:Chip.IsActive`) instead of 23 inline copies; page and
      sub-tab switching via the `EqualityToVis` converter instead of 14 inline styles. Zone 2 keeps its own style.
- [x] Typography and shape tokens in `Themes/Typography.xaml`: type scale `FontSize00`–`FontSize14`, radius scale
      `Radius00`–`Radius09`, fonts `DisplayFont`/`UiFont`/`MonoFont` (610 sizes, 175 radii, 64 font names replaced).
- [x] Chromecast and Apple TV pages read font sizes and corner radii from the host tokens (`HostTheme.FontSize`/`Radius`).
- [ ] The meters' drawn text sizes are still literal (the dials scale as a whole with their height, so skins do not
      need them).
- [x] Icon set in `Themes/Icons.xaml`: 21 named geometries (sidebar, title bar, transport, chevrons, tick), 25 uses.

## Release / build

- [ ] The installer and exe are unsigned (SmartScreen warning). Consider code signing.

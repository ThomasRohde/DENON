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

## Apple TV plugin (local branch `appletv-plugin`, push only if milestone 3 succeeds)

Native .NET (no Python), protocol logic ported from pyatv (MIT). See `external-plugins/Denon.Plugin.AppleTv/README.md`.

- [x] Milestone 0: discovery. Found "Stuen (2)", Apple TV 4K (1st gen), tvOS 26.6.1: Companion :49153, AirPlay :7000,
      no `_mediaremotetv` (now playing must use the AirPlay tunnel).
- [x] Milestone 1: HAP pairing + Companion. Paired live on 2026-11-23 (first try, M6 signature verified); power
      state, app list (43 apps) and events work. 27 tests incl. an encrypted loopback session.
- [ ] Milestone 1 rest: verify live the commands that change the TV (`key`, `power on/off`, `launch`); needs the
      user's OK each time.
- [ ] Milestone 2: MRP now playing over AirPlay 2 — go/no-go. Risk: tvOS 26 behavior is not confirmed.
- [ ] Milestone 3: full plugin and standalone release (`appletv-v*` tag).

## Release / build

- [ ] The installer and exe are unsigned (SmartScreen warning). Consider code signing.

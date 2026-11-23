# TODO

Open work for Denon Desktop and the external plugins. Keep this file current: add items when work is found or
left open, and remove them (with a CHANGELOG entry) when they are done.

## VU meters & RTA (follow the Denon)

Phase 1 is done in 1.15.1 (source router, DENON/SIM/MUTED/PC/IDLE badge, PC Cast meters synced to the HEOS delay).

- [ ] **Phase 2: real, synced meters for app streams.** For the YouTube / YouTube Music relay (`/yt/`) and the local
      FLAC jukebox (`/local/`), decode a copy of the stream (ffmpeg `f32le`, or NAudio for M4A), build a level + 31-band
      timeline by media time, and show the frame at the playback position the app already estimates for Cinema sync
      (`GetEstimatedPlaybackPositionSeconds(CinemaSyncOffsetMs)`). Router: `Denon` source when the Denon plays an app relay stream.
- [ ] **Phase 3: opt-in "ROOM" mode (PC microphone)** for TV/ARC, HDMI and Chromecast audio that the app cannot tap.
      Needs AGC, a noise gate, a clear "mic on" badge and a setting (off by default).
- [ ] **Philips Hue Music Sync** still reacts to the live PC output (`PluginHostService.PublishAudioFrame`).
      Decide if it should follow the same router (and the HEOS delay during PC Cast).
- [ ] Verify live: the **DENON** badge during PC Cast (meters in sync with the speakers) and the **MUTED** badge.
- [ ] RTA during PC Cast from a non-default output device falls back to SIM. Option: run the FFT on the cast capture itself.

## Habit telemetry

- [ ] Cast sessions end with `plugin_stopped` at app exit (the plugin cannot tell app exit from a plugin stop).
      Option: add a stop reason to the SDK (`IDenonPlugin.StopAsync` context or `IPluginContext.IsAppShuttingDown`).
- [ ] Events written before 1.15.0 have no `coveredSec` / `exclusiveSec`. Readers must fall back to `durationSec`.
- [ ] **Hero "now playing" during a cast** shows the TV input ("DVD Player (HDMI 1)") instead of the cast app/title
      (for example "Netflix"). When the TV is on the Chromecast's HDMI input, show the cast state from the plugin.
- [ ] A cast that stays paused for hours stays one open session (`exclusiveSec` is capped at `watchedSec`, so the
      totals are right). Option: end it with `reason: "paused"` after N minutes.

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

## Release / build

- [ ] The installer and exe are unsigned (SmartScreen warning). Consider code signing.

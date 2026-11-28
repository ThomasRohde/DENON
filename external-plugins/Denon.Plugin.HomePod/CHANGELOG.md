# Changelog — HomePod plugin

Released separately from Denon Desktop with tags `homepod-vX.Y.Z`.

## [0.3.0] - 2026-11-28

### Security
- **Bad network data can no longer crash Denon Desktop or stall the link** (#35). The shared AirPlay readers check
  every length before they allocate: binary plists (trailer, offsets, object count), data-stream frames (smaller than
  the header or over 16 MB now close the connection, which reconnects, instead of stalling it), HTTP Content-Length
  and protobuf lengths.

### Fixed
- **A PC sleep no longer adds hours of listening time** (#70). Each step counts at most 120 seconds of play, so a
  HomePod that played while the PC slept is logged with the time it was seen playing.
- **A stereo pair whose leader changes keeps one link and one session** (#49). The old link and its listening session
  are closed under the old id before the new one starts. Two single HomePods that become a pair are one speaker,
  so the same music is no longer logged twice. Speakers are matched by the HomePods' device ids, never by name, so
  two HomePods with the same name stay two speakers; a speaker saved by an earlier version still matches its pair.
- **The link comes back when it drops right after it opens** (#50). A close before the plugin listened was missed,
  and the speaker showed "Linked" on a dead connection. A command in flight fails at once when the link closes.
- **Insights: a second of play and then a pause is no session** (#70); it needs 15 seconds of play.
- **Discovery: an empty TXT string no longer hides the entries after it** (#70), and a reserved DNS label type
  drops the packet instead of misreading the rest of it.
- Connections and timers are cleaned up when the remote-control setup fails part-way or the plugin restarts (#70).

## [0.2.1] - 2026-11-26

### Fixed
- **Discovery is logged only when the speakers found change** (sorted, so the same speakers in another order are no
  change). Before, every run wrote the same "HomePod discovery: 2 speaker(s) …, 0 new." line.

## [0.2.0] - 2026-11-24

Needs Denon Desktop **2.8.0** or later. On an older version the plugin does not run: it calls a host method (ReportStatus with details) that those versions do not have. Denon Desktop now refuses a plugin built for a newer Plugin SDK, with a clear log line.

### Added
- **The now-playing hero shows the HomePod while the receiver sleeps:** the title, "artist • app • speaker", the
  artwork, and Play / Pause, Previous and Next that control the HomePod.
- **Artwork:** the plugin asks the HomePod for the artwork of what plays (as the Apple TV plugin does) and keeps the
  newest 40 images in its data folder. `now-playing` returns it as `imageUrl`.
- Structured status details (Plugin SDK 2.1) for the speaker the status line names.

## [0.1.0] - 2026-11-24

First release. Needs Denon Desktop **2.7.0** or later (Insights and the activity feed know HomePod sessions; the
update check finds `homepod-v*` releases).

### Added
- **Discovery:** HomePods on the LAN by DNS-SD, every 10 minutes and with **Discover now**. A stereo pair is one
  speaker ("HomePod Left + Right"), led by the pair's leader; a short AirPlay multi-room group does not merge speakers.
- **No PIN, nothing stored:** a transient pairing (HAP pair-setup with the fixed PIN 3939 and the transient flag) on
  each connection, then the AirPlay 2 remote-control channel with MRP. Reconnects after 5 s, doubling to 1 minute.
  Verified live with HomePod mini on HomePod software 27.2.1.
- **Now playing, live:** app (Apple Music, Podcasts, or the AirPlay sender, for example "AirMusic (AirPlay)"),
  title, artist, album and play state. System sounds (Siri, timers) do not count.
- **Controls:** play, pause, play/pause, next, previous — on the speaker card and as actions for MCP, Smart Scenes
  and hotkeys (`list-devices`, `now-playing`, `discover` too).
- **Listening sessions** (`kind: "homepod"`) on the `media` stream for Insights, one per song, episode or stream.
- **Pause the HomePods when the Denon turns on** (off by default).

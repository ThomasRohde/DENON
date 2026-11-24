# Denon Desktop: computer-use test script

A script for an AI agent with computer use (Claude or Codex) to test the core experiences of Denon Desktop on this PC,
with the real receiver, and to record the findings. The agent drives the app with the mouse and keyboard, as a user
would, and reads the screen.

Run one agent at a time. Two agents on the same app and receiver interfere with each other.

## 0. Rules for the agent (read first)

The app controls a real living room: a Denon AVR-X2400H, an LG TV, an Apple TV, a Chromecast and Hue lights. People may
be in the room.

**Never do these (tier C).** If a test seems to need one, mark it `SKIPPED (tier C)` and go on.

- Play, cast, stream or queue any media: YouTube, radio, stations, jukebox files, PC Audio Cast, HEOS favorites,
  "Stream Now", "Play", "Play All", "Watch Video", "Test" on a station.
- Send any command to the TV, the Apple TV or the Chromecast: power, keys, inputs, apps, toasts, Screen Off, audio
  output, pairing. Their plugin pages are read-only for this test.
- Turn the receiver off, or change its power, zones, ECO mode or sleep timer.
- Activate a scene, run an automation, or apply a Room Doctor fix.
- Click "Fix Silent Audio", "Reset Links", "Factory Reset", "Clear Event History", "Import JSON", "Save & Sync",
  "Save Scenes", "Save Changes", "Save & Apply", "Revoke & Rotate", "Regenerate", "Allow Firewall", "Download and
  install", or any Delete or Clear button.
- Type in the Health page's raw command boxes (AVR :23, HEOS :1255).
- Copy, read out or record a token, a QR code, a pairing key or a "Copy Paired URL" address. Do not put them in a
  finding or a screenshot. The Brain page shows the MCP bearer token in plain text, and the Phone QR / Owner QR /
  Guest QR buttons show pairing codes: keep them out of every screenshot (take the screenshot of another area, or
  skip it).

**Only with the user's OK at the start (tier B).** Ask once before the run: "May I make small, reversible receiver
changes (volume ±1 and ±5, mute, quick levels, one sound mode) and switch the app theme and skin? I put everything back."
Without a clear yes, mark tier B tests `SKIPPED (no OK)`.

- Volume: never go above the start volume + 5, never below 15 without a reason. Put the volume back after each test.
- Sound mode: try one other mode, then put the start mode back.
- Do not change the input source.

**Always allowed (tier A):** look, read, hover for tooltips, open pages and panels, search (without playing), open
editors and close them without saving, run the Health Check and Room Doctor scan (they only read).

**Evidence.** Save screenshots and the findings file outside the repository, in
`%USERPROFILE%\Documents\DenonTestRuns\<yyyy-mm-dd>-<agent>\` (for example `2026-11-25-claude`). The screenshots show
the user's own setup: never commit them, and never put them in the public README or the website.

**When something goes wrong.** If the receiver, the TV or the room changes in a way you did not intend: stop, put back
what you can (volume, sound mode), and tell the user at once. Do not try to fix it with more clicks.

## 1. Before you start

1. Open the app from the Start menu ("Denon Desktop"), or press **Ctrl+Alt+D** if it runs in the tray. Ask for computer
   use access to "Denon Desktop". If your tool cannot find it, ask the user to show the window, then continue.
2. Write down the **start state** in the findings file:
   - The app version (bottom right of the status line, for example "Version 2.6.1").
   - The receiver: online or not, the input, the sound mode, the volume (dB and number), mute.
   - The theme (Dark, Light or Follow Windows) and the skin (Settings → APPEARANCE).
   - The page that is open, and whether the window is maximized.
3. Make a copy of `findings-template.md` (next to this file) in the evidence folder and fill it in as you go.
4. Ask the tier B question (section 0).

At the end, put back the start state (section 4).

## 2. How to record a finding

For each test, write one line in the results table: `PASS`, `FAIL`, `PARTIAL`, `SKIPPED (reason)` or `BLOCKED (reason)`.
For each problem, add a finding:

- **ID** (`F-01`, `F-02`, …) and the **test ID** it came from.
- **Severity:**
  - `S1`: crash, hang, data loss, or a wrong command to a device.
  - `S2`: a core task fails or shows wrong information.
  - `S3`: works, but is confusing, slow, clipped or hard to read.
  - `S4`: cosmetic.
- **What you did**, **what you expected**, **what happened**, and a **screenshot** file name.
- Whether you can **reproduce** it (yes, once, sometimes).

Record what you see, not what the code should do. A short, exact finding is better than a long guess.

## 3. Tests

Each test has an ID, a tier, the steps and what should happen. Do them in order; each part starts from the Deck page.

### A. Start, window and navigation

| ID | Tier | Steps | Expected |
|---|---|---|---|
| A1 | A | Look at the title bar and the status line. | The model (AVR-X2400H), a green ONLINE pill with the firmware and LIVE PUSH. The status line shows the receiver address, the input, the volume, "Insights", the last sync time and the version. |
| A2 | A | Click each page in the left rail: Deck, Music, Stations, Cast, Scenes, Plugins, Brain, Settings, Health, Help. | Each page opens in under a second, with no empty or broken areas, no clipped text and no error banners. The selected item is highlighted. |
| A3 | A | Press Ctrl+1 to Ctrl+9 (1 Deck, 2 Music, 3 Scenes, 4 Cast, 5 Stations, 6 Plugins, 7 Settings, 8 Health, 9 Help). | Each key opens its page. |
| A4 | A | On three different pages, press F1. Close the help with Esc or ✕. | Help opens on the topic for that page. |
| A5 | A | Make the window small (drag a corner), then large, then maximize and restore. | The content reflows. Nothing overlaps or is cut off. The window cannot get smaller than its minimum. |
| A6 | A | Minimize the window (it goes to the tray). Wait 10 s. Press Ctrl+Alt+D. | The window hides, then comes back on the same page. |
| A7 | A | Click the tray icon (notification area). Look at the flyout. Press Esc. | The flyout shows VOLUME, QUICK INPUTS and PRESET STATIONS. Do not click an input or station. Esc closes it. |

### B. Deck: now playing and meters

| ID | Tier | Steps | Expected |
|---|---|---|---|
| B1 | A | Read the now-playing card. | The title, the detail line and the chips (input, play state, sound mode) match the room. On Live TV: the program name as the title, "DR1 • Ch 1 • 16:25–17:15" style detail, and the channel logo. On another source: what plays there, or the input name. |
| B2 | A | Hover over the meters and their source label (SIM, PC, ROOM, DENON). | The needles or LEDs move with the sound (or rest in silence). The tooltip says where the level comes from. |
| B3 | A | Click the meter button on the card to step through VU Meter, RTA 31 and VU + RTA. Then click "VU Meters: ON" twice. | Each mode draws without flicker. The meters hide and come back. Put the start mode back. |
| B4 | A | If music plays (not TV), click Lyrics. Click it again to close. | Synced lyrics, or a clear "no synced lyrics" message. Skip on TV or radio. |
| B5 | A | Watch the Deck for 2 minutes. | No frozen meters, no jumping layout, no flashing. The sync time in the status line updates. |
| B6 | A | Check QUICK SCENES & STATIONS. Hover a scene chip and a station card. Do not click Play or a scene. | Names, badges and colors are readable in the current theme. Tooltips explain what a click does. |

### C. Volume and sound (tier B)

Write down the start volume first. Put it back after each test.

| ID | Tier | Steps | Expected |
|---|---|---|---|
| C1 | B | In MASTER VOLUME, click +1, then −1. Then −5, then +5 (down first). | Each step changes the number by 1 (1 dB) or 5 (5 dB) both ways, and the receiver follows within a second. The status line and the tray flyout show the same value. |
| C2 | B | Scroll the mouse wheel one notch up and one down over the volume card. | 2 steps (2 dB) per notch, then back. |
| C3 | B | Press Ctrl+Up, then Ctrl+Down. | Same as C2. |
| C4 | B | Click MUTE, wait 3 s, click it again (or press M twice). | The readout shows muted, the meters show muted, then it returns. |
| C5 | B | Click a QUICK LEVELS button that is at most 5 above the start volume (or below it), then set the start volume back. | The volume goes to that level smoothly. |
| C6 | B | In SOUND MODE & DSP, click one other mode. Wait 5 s. Click the start mode. | The receiver changes mode; the chip on the now-playing card and the highlighted button follow. |
| C7 | A | Do not click anything in INPUT SOURCE or POWER & ZONES. Only check that the current input and zone state are highlighted correctly. | The highlights match the receiver. |

### D. Music, Stations and Cast (look, search, never play)

| ID | Tier | Steps | Expected |
|---|---|---|---|
| D1 | A | Music: type a search (for example "Miles Davis So What") and click Search / Load. Switch the Music, Videos and Playlists tabs. Do not click Play, Video, + Q or Play All. | Results load with titles and thumbnails. The UP NEXT QUEUE and RECENTLY CAST lists show. |
| D2 | A | Stations: look at PRESET STATIONS and RECEIVER HEOS FAVORITES (do not click Load from Receiver if it would start anything). In the Radio Browser directory, search "Jazz". Do not click Play, Test or + Save. | The station list and the search results show with names and bit rates. The filter chips work. |
| D3 | A | Cast: look at PHONE REMOTE, CAST PC AUDIO TO DENON and LOCAL HI-RES / FLAC JUKEBOX. Do not click a QR, Copy, Allow Firewall, ON AIR or Play. | Each card explains its state. No token or QR is copied or saved. |

### E. Scenes and acoustics (look, edit, never activate or save)

| ID | Tier | Steps | Expected |
|---|---|---|---|
| E1 | A | Scenes: read the SMART SCENES & MACROS list. | Each scene has a name, a badge and a description that fit. |
| E2 | A | Click Edit on a scene. Look at the SCENE EDITOR fields and the PLUGIN ACTION CHAIN. Change nothing, then leave without saving (go to another page and back). | The editor shows the scene's values. Leaving does not save. |
| E3 | A | Look at LIVE ACOUSTIC TUNING & AUDYSSEY. Do not change a trim or a switch. | The values match the receiver (Sync from AVR is allowed: it only reads). |
| E4 | A | Click Automation Rules. Read the rules and their triggers. Do not run or save. | The rules show with their triggers and actions. |

### F. Plugins (read-only)

| ID | Tier | Steps | Expected |
|---|---|---|---|
| F1 | A | Plugins: read the list. | Each plugin shows its version, its state (LIVE, Paired, Idle) and a status line. The badges at the bottom of the left rail (HUE, TV, HA, ATV, CAST) show the same health. |
| F2 | A | Open the LG webOS TV page. Read it. Click nothing that sends to the TV. | LIVE, the model, the current app or channel, HDMI inputs and installed apps. On Live TV: the channel and program. The "Show channel logos" box is ticked. |
| F3 | A | Open the Apple TV page. Read it. Do not press Play, Pause, keys, power or app buttons. | The device, its power state and what it plays. On a live program the Pause button is greyed out. |
| F4 | A | Open the Chromecast, Philips Hue and Home Assistant pages. Read them. Click nothing. | Each shows its devices and connection without errors. |

### G. Brain (MCP), Insights and Health

| ID | Tier | Steps | Expected |
|---|---|---|---|
| G1 | A | Brain: read the MCP server status and settings. Do not copy, regenerate or save. The BEARER AUTH TOKEN shows in plain text: do not screenshot that card, and do not write the token down. | The endpoint, the permission tier and the safety ceiling show. |
| G2 | A | Click Run Room Doctor. Read the insights. Do not click a fix. | A list of findings, or "all clear". |
| G3 | A | Click "Insights" in the status line (or Settings → Open Insights). The page opens in the browser. | The page shows the watch time, RIGHT NOW (on Live TV: the channel logo, the program, a progress bar and the next program), the observations and the charts. |
| G4 | A | In Insights, click Today, 7 days, 30 days, 90 days. Hover a bar, a slice and a channel tile. | Each period loads. Tooltips show numbers. LIVE TV CHANNELS shows logo tiles (or letters for channels without a logo). |
| G5 | A | In Insights ACTIVITY, click each filter (All, Media, Receiver, TV, Lighting, Control) and tick Connection events. | The list filters. Live TV rows show a small channel logo. |
| G6 | A | Health: click Run Health Check. Read the results. Look at ACTIVITY HISTORY with each filter. | Each check ends with a result and a clear text. No commands are sent from the raw boxes. |

### H. Settings, theme and updates

| ID | Tier | Steps | Expected |
|---|---|---|---|
| H1 | A | Settings: read each card. Change no field. | All cards load. Labels and values fit; nothing is clipped. |
| H2 | B | In APPEARANCE, switch Dark ↔ Light, then each skin (Default, Hi-Fi, Compact, Large). Visit the Deck, Settings and Insights after each. Put the start theme and skin back. | The whole app (and the Insights page after a reload) changes at once. Text stays readable; the window fits its content. |
| H3 | A | In DESKTOP & TRAY BEHAVIOR, click Check now. | "Denon Desktop X is up to date" (or "… and installed plugins are up to date"), or an "Update to X" button in the status line. If an update shows, open the panel, read WHAT CHANGED, and click Later. Never Download and install. |
| H4 | A | Help: search "insights", open a topic, follow one quick jump, and read the LIVE SETUP CHECKLIST. | The search finds topics; the steps read clearly; the quick jump opens the right page. |

### I. Overall quality (look across the whole run)

| ID | Tier | Steps | Expected |
|---|---|---|---|
| I1 | A | Note any text that is clipped, overlapping, too small or low in contrast, in either theme. | None. |
| I2 | A | Note any action that took more than 2 s without a busy sign. | None. |
| I3 | A | Note any wording that is unclear, wrong or out of date. | None. |
| I4 | A | Optional: open Task Manager (Processes), find Denon Desktop. With the Deck shown, read CPU and GPU. Minimize to the tray, wait 15 s, read again. | Deck shown: GPU about 5–6 %. In the tray: GPU 0 %, CPU near 0 %. |

## 4. After the tests

1. Put back the start state: volume, mute, sound mode, theme, skin, meter mode and the page that was open.
2. Close the Insights browser tab. Leave the app running, as it was.
3. Finish the findings file: the summary counts, the top 3 problems, and anything you could not test (with the reason).
4. Tell the user where the findings file is, and list the S1 and S2 findings in your reply.

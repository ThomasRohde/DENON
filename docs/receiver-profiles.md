# Receiver setup check and profiles

Scenes & Acoustics → **Setup check** compares the receiver with a profile of recommended settings and offers to apply
the ones the user ticks. This page describes the profile files and the rules the app keeps. Ctrl+K → "Receiver setup
check" opens the page.

## How a check works

1. **Read only.** The check sends the read command of each setting in the profile (for example `PSREFLEV ?`) and
   compares the answer with the recommendation for the chosen use (Films, Music, Late night, TV). Each row gets a
   state: OK, differs, warning, cannot read, blocked by mode, needs another setting, no advice, or advice only.
2. **Apply.** The user ticks rows and presses Apply. The app asks, saves the old values to
   `%LocalAppData%\DenonDesktop\setup-snapshots\latest.json`, writes each row in profile order and reads it back.
   **Undo last apply** writes the old values back in reverse order.
3. **Per-input settings.** The `PS` settings (Audyssey, dynamics, tone, LFE) are kept per input by the receiver. The
   check reads them for the input selected now, and an apply or undo writes for that input only: it never switches
   input (that would interrupt playback). If the input changed since the check, the per-input rows are skipped and
   the app says so. *Assumption, not yet checked on a receiver:* a `PS` write changes the selected input only, and a
   read answers for it. The test fake works this way.
4. **Model detection.** HEOS reports the model ("Denon AVR-X2400H") at the first status read. A profile lists the
   models it covers; names are matched without brand, dashes and the "AVR" prefix (`X2400H`), and a regional suffix is
   allowed (`X2400HBKE2`). A user profile for the model wins over a bundled one. No profile means no check.

## What the app will send

Only what a profile describes, with a value from the setting's own list:

- A **read** is the setting's `read` text. A **write** is the `write` template with `{value}` replaced by one of the
  setting's `values[].token`. Nothing else in the JSON is ever sent as a command.
- Commands that start with `SYRST`, `PW`, `SYPANEL` or `RC` are blocked for every profile. A command must start with
  `PS`, `ECO`, `DIM`, `MS`, `VS` or `CV`, is upper-case ASCII, is at most 135 bytes with its carriage return, and a
  read ends with `?` while a write never contains one.
- The client's setup methods take an `AvrSetupCommand`, which only `AvrCommandGate` can make. The usual 50 ms spacing
  between commands applies.
- The app never sends `SS...` (speaker setup) queries: the Denon protocol file for these receivers has none. Speaker
  size, crossover, LPF for LFE, subwoofer mode, distances, HDMI Control and ARC, the 4K signal format and the volume
  limit are `advice` rows: text only.

## The profile format

The JSON Schema is `Denon.App.Core/Setup/receiver-profile.schema.json` (also embedded in the app). A file that does not
fit the schema, or that breaks a rule beyond it, is not used; the Setup check page lists the file and the reasons.

```json
{
  "id": "my-receiver",
  "name": "My receiver family",
  "models": ["AVR-X2400H"],
  "modelOverrides": {
    "AVR-X2400H": { "exclude": ["cinemaEq"], "recommend": { "refLevel": { "films": "5" } } }
  },
  "settings": [
    {
      "id": "refLevel", "label": "Reference Level Offset", "group": "Audyssey",
      "scope": "perInput", "tier": "confirm",
      "requires": { "dynamicEq": "ON" },
      "blockedWhen": { "soundModes": ["DIRECT", "PURE DIRECT"] },
      "read": "PSREFLEV ?", "readParse": "^PSREFLEV (\\d+)$",
      "write": "PSREFLEV {value}",
      "values": [ { "token": "0", "label": "0 dB" }, { "token": "5" }, { "token": "10" }, { "token": "15" } ],
      "recommend": { "films": "0", "music": "10", "lateNight": "0", "tv": "10" },
      "why": "Shown next to the row.",
      "sources": ["Where the advice comes from"]
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `scope` | `global`, `perInput` or `perSoundMode`: what a read or write is for. |
| `tier` | `apply` (safe, ticked by default), `confirm` (changes how the sound feels, ticked by the user) or `advice` (never written). |
| `read`, `readParse` | The query and a regular expression for the answer line. Group `value` (or the first group) is the token. |
| `decode` | `none`, or `offset50` for a number the receiver sends as 50 + dB (the subwoofer trim). |
| `write`, `values` | The write template with one `{value}` and the only values it may carry. |
| `recommend` | Use (`films`, `music`, `lateNight`, `tv`) → a token, or a list of accepted tokens (the first is the one applied). A use with no entry has no recommendation. |
| `blockedWhen` | Sound modes (as the receiver reports them, upper case) in which the setting does nothing. The row is not read or applied. |
| `requires` | Another setting, listed earlier, and the token it must have. |
| `warnWhen` | Advice rows only: warn when the read value is at the ends of `atLimits` or in `values`. A warning is never applied. |
| `advice` | Text for rows the protocol cannot read or set. |
| `modelOverrides` | Per model: `exclude` setting ids, and replacement `recommend` entries. |

Profiles in `%LocalAppData%\DenonDesktop\profiles\*.json` are read when the app starts and on **Reload profiles**. A
profile with the same `id` as a bundled one replaces it.

## Sources

The bundled FY17 X-series profile is written from the Denon X2400H owner's manual and the manufacturer's protocol file
(`AVR-X6400H_X4400H_X3400H_X2400H_X1400H_S930H_S730H_PROTOCOL_V01.xlsx`, 2017-06-16). Some recommendations (Reference
Level Offset per use, Restorer) also rest on forum advice that could not be read directly; the profile's `why` and
`sources` fields say which. The subwoofer trim has no target because sources disagree: the check only warns at -12 and +12 dB.

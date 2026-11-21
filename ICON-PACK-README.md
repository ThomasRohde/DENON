# DENON Desktop — headphones + waveform icon pack

A repo-ready icon pack based on the **R2C5** choice (headphones with a cyan/blue waveform), plus eleven matching variations. Built for the existing .NET 10 / WPF DENON application at https://github.com/ThomasRohde/DENON.

## Install

1. Extract this ZIP **into the root of your `DENON` checkout** (the directory containing `DenonDesktop.slnx`).
2. Allow these two files to be replaced:
   - `Denon.App.Desktop/denon_icon.ico`
   - `Denon.App.Desktop/denon_icon.png`
3. Rebuild / republish:

   ```powershell
   dotnet build .\DenonDesktop.slnx
   dotnet publish .\Denon.App.Desktop\Denon.App.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\release-out
   ```

No `.csproj`, XAML, or C# changes are necessary for the default icon. The project already uses `<ApplicationIcon>denon_icon.ico</ApplicationIcon>`, explicitly embeds it as a WPF Resource, uses `Icon="denon_icon.ico"` in its windows, and reads the same resource via `TrayIconController`.

You can revert the new art via Git if desired. Windows Explorer and the taskbar may cache icons: the updated art will be present in a newly published executable; if Windows still shows the old art, unpin/repin the shortcut or refresh the Windows icon cache.

## Included files

- `Denon.App.Desktop/denon_icon.ico` — **drop-in Windows app icon**, with 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px images. Small sizes use a simplified vector-rendered design for clarity.
- `Denon.App.Desktop/denon_icon.png` — 1024 × 1024 transparent RGBA master of the selected artwork.
- `Denon.App.Desktop/Assets/Icons/variants/` — 12 named variants, each as transparent 512 × 512 PNG and multi-resolution ICO.
- `Denon.App.Desktop/Assets/Icons/denon_tray.ico` and `.png` — optional compact, vector-rendered tray-friendly icon.
- `Denon.App.Desktop/Assets/Icons/vector/` — two **real editable vector SVGs** (simplified symbolic and monochrome designs, not auto-traces of the detailed generated PNG artwork).
- `docs/icon-pack-preview.png` — labelled comparison sheet showing how the icons look on light and dark backgrounds.

## Variants

| Name | Purpose |
|---|---|
| `main` | App identity; headphones + sound waveform |
| `active` | Active listening / playback |
| `muted` | Muted audio |
| `volume-up` | Increase volume |
| `volume-down` | Decrease volume |
| `wireless` | Wireless / HEOS connection |
| `remote` | Mobile / remote control |
| `settings` | Configuration / tone controls |
| `speaker` | Audio output |
| `surround` | Home theatre / surround |
| `power` | Standby / power |
| `outline` | Simple line-art option |

The alternatives are supplied as **optional design assets**. They are not wired into the WPF UI by default. For example, `denon_tray.ico` would need to be included as a `<Resource>` in the project and the `TrayIconController` resource URI updated to use it. The default installer step does not make those changes.

## Transparency and quality

- All PNGs have true 8-bit alpha transparency. No checkerboard is baked into the art.
- Icons come from an AI-generated 12-option artwork sheet; their detailed masters are **raster** images, not infinitely scalable vectors. The two clearly named symbolic SVGs are separately hand-authored vector approximations.
- Keep artwork on a square transparent canvas. The `.ico` files contain resolution-specific images rather than a single embedded thumbnail.
- These icons are a third-party controller visual identity, not official Denon/Marantz brand marks.

## Validation

Run `dotnet build .\DenonDesktop.slnx` on Windows with the .NET 10 SDK, then launch a fresh build and verify: taskbar, title-bar, system tray and published EXE file icon. Asset-level validation was performed when creating this ZIP; a Windows WPF build is not performed by the pack generator.

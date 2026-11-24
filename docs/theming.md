# Theming and skins (2.0.0)

Status: built. Themes: Dark, Light and Follow Windows. Skins: Default, Hi-Fi, Compact and Large. Both switch
live from Settings → Appearance, and any skin works with any theme.

## Two layers

| Layer | What it changes | Files | Switch |
|---|---|---|---|
| **Theme** (palette) | Colors only: the neutral ramp, text levels, accent families, overlays | `Themes/Palettes/<Id>.xaml` | Live |
| **Skin** | The rest of the look, as tokens: fonts, type scale, corner radii, spacing and size scales, meter materials | `Themes/Skins/<Id>.xaml` and `MeterPalette.<Id>` | Live |

A theme never changes shapes or sizes; a skin never sets a UI color. A skin draws with the theme's tokens, so
every skin works with every theme. The meters are the one exception: their dial faces, printed ink and LEDs are
physical materials (`Controls/MeterPalette.cs`: dial faces, printed ink, needles, the LED screen), so they belong
to the skin. Each skin has two sets, like two models of a real meter: dark faces for dark themes, and light faces
for light themes (cream dials with black print and needles, a light LED screen with darker segments). The theme
picks the set (`SkinInfo.MetersFor`). Labels around the meters (the panel, the source pill) sit on the themed
panel and take theme colors.

Control templates and page layouts are the same in every skin (decision 1 below): `Themes/Styles.xaml` and
`Themes/Icons.xaml` are shared.

## Resource stack

`App.xaml` merges the layers in a fixed order. `ThemeManager` replaces a layer in its slot; the order is the
contract:

| Slot | Dictionary | Owner |
|---|---|---|
| 0 | `Themes/Palettes/Dark.xaml` (or another palette) | theme |
| 1 | `Themes/Brushes.xaml`: a brush per color token, role aliases, gradients | theme (rebuilt with the palette) |
| 2 | `Themes/Skins/Default.xaml` (or another skin): fonts, type scale, radius scale, `SpaceScale`, `SizeScale` | skin |
| 3 | `Themes/Icons.xaml`: vector icons | shared |
| 4 | `Themes/Styles.xaml`: control styles and templates | shared |

The application resources also hold `SkinMetrics` (the current spacing and size scales, see below), so plugins
can bind to it by name.

## How a live theme switch reaches every pixel

1. `ThemeManager.ApplyTheme` puts the new palette in slot 0 and a fresh `Brushes.xaml` in slot 1. The brushes are
   frozen; a switch makes new brushes instead of changing old ones.
2. XAML refers to color tokens with `{DynamicResource …Brush}` (never `StaticResource`), so every element follows
   the new dictionaries. A guard test fails on a `StaticResource` color reference.
3. `ThemeManager` then walks the open windows (visual and logical tree) and
   - reads again every binding that gives an element a brush or color, local or from a template: view models and
     converters build brushes from token names when they are read, so they return the new theme's brush. View
     models keep a token name, never a brush instance;
   - gives an element that holds an old theme brush as a plain value (code that assigned `Foreground = …`, for
     example the Chromecast and Apple TV pages, which are built in code) a live reference to the same token.
4. `ThemeManager` sets `MeterPalette.Current` to the skin's set for the new theme. Code that draws with theme
   colors or meter materials listens to `ThemeManager.ThemeChanged` (tray menu, VU dials, LED spectrum).
5. Plugins need no Plugin SDK change: built-in plugins use the same XAML rules; code-built pages read
   `HostTheme.Brush(…)` at each use (never cache a brush in a static field) or bind with `HostTheme.Bind`
   (`SetResourceReference`).

## How a live skin switch reaches every pixel

1. `ThemeManager.ApplySkin` puts the skin's dictionary in slot 2, sets `MeterPalette.Current` to the skin's meter
   materials for the current theme, updates `SkinMetrics` and raises `ThemeManager.SkinChanged`.
2. XAML refers to the skin tokens with `DynamicResource`: `FontSizeNN`, `RadiusNN`, `DisplayFont`, `UiFont`,
   `MonoFont` and the `Icon*` geometries. A guard test fails on a `StaticResource` reference to them.
3. Spacing and sizes are written in Default-skin pixels with two markup extensions
   (`Infrastructure/Theming/SkinMetrics.cs`):
   - `Margin="{skin:Space 0,0,0,12}"`, `Padding="{skin:Space 12,8}"`: one, two or four values, like a `Thickness`;
   - `Width="{skin:Size 32}"`, also for column and row definitions.
   Each is a binding to `SkinMetrics.Current`, which multiplies the value by the skin's `SpaceScale` or `SizeScale`
   (rounded to half pixels; the Default scale 1 keeps the value exact). A guard test fails on a literal non-zero
   margin or padding, or a literal size above 2 px (thinner sizes are hairlines and stay fixed). Window sizes,
   border thicknesses and stroke widths stay fixed.
4. The VU dials and the LED spectrum listen to `SkinChanged` and redraw with the new fonts and materials.
5. Plugins need no Plugin SDK change. Each plugin has a copy of `HostSkin.cs`:
   - built-in plugins (XAML) use its `{skin:Space}` and `{skin:Size}`, which bind to the host's `SkinMetrics`
     resource by name, and `DynamicResource` for type, radii and fonts;
   - pages built in code (Chromecast, Apple TV) call `HostSkin.Follow(root)` on each element tree they build: it
     turns the literal margins, padding and sizes into live skin bindings. Font sizes and radii use
     `element.Token(property, "FontSize10")` (`SetResourceReference`).

## Checking without a screen

Screenshot mode: `--capture-screenshots <dir>` with `--theme <id>`, `--skin <id>`, `--switch-theme <id>` and
`--switch-skin <id>` (start with Dark and Default, then switch live with the window open) and `--all-pages`.
A start in a theme or skin and a live switch to it give the same pixels; only the clock and the animated
"now playing" bars differ.

## Palette contract

- Every palette defines exactly the keys of `Dark.xaml` (test: `ThemePaletteTests`).
- `Ink00` is the window background; higher `Ink` steps stand out more from it (cards, controls), and the steps from
  `Ink09` up are lines and borders. A light palette runs from light to dark.
- Text levels keep a minimum contrast on every surface (`Ink01`–`Ink08`) in every palette (test): primary and
  strong 7:1, soft, secondary and muted 4.5:1, faint 3:1.
- `Accent*Text` colors are for text on the page; `Accent*SubtleBg` and `*Border` for tinted chips and panels.
  A light palette makes the text colors darker and the subtle backgrounds lighter.
- `Overlay*` colors are highlights and scrims with alpha. `OverlayHighlight*` is white on dark palettes and may
  be dark on light ones.

## Skin contract

- Every skin defines exactly the keys of `Skins/Default.xaml` (test: `SkinTests`).
- The type scale rises from `FontSize00` to `FontSize14`, and `FontSize00` is at least 8.5 px.
- `SpaceScale` and `SizeScale` are between 0.5 and 2. Compact is not larger than Default, and Large is not smaller.
- A skin sets no colors. Its meter materials are two `MeterPalette` sets in code, one for dark themes and one for
  light themes (`MeterPalette.Default` and `DefaultLight`, `HiFi` and `HiFiLight`), registered with the skin in
  `ThemeManager.Skins`. Dark sets have dark faces and LED screens, light sets light ones; the print keeps its
  contrast on its face: ink 7:1, channel accents and red-zone numbers 4.5:1, source tags and LED labels 3:1
  (test: `MeterPaletteTests`).
- A bundled font is an embedded resource in `Themes/Fonts/` with its license file, and is listed in
  `THIRD-PARTY-NOTICES.md` (the installer copies it).

To add a skin: copy `Skins/Default.xaml`, change the values, register it in `ThemeManager.Skins` (id, name,
hint, file, dark and light meter palettes), and capture all pages with `--skin <id> --all-pages` in Dark and Light.

## Skins

| Skin | Type and spacing | Corners | Fonts | Meters |
|---|---|---|---|---|
| `default` | Default scale | Rounded | Segoe UI Variable, Cascadia Mono | Dark: dark faces, amber and cyan channels, cyan-to-red LEDs. Light: cream faces, black print, deep blue-to-red LEDs on a light screen |
| `hifi` (Hi-Fi) | Default scale | Nearly square | Bahnschrift (Windows), Share Tech Mono for numbers (bundled, OFL) | Dark: amber-backlit dials, teal fluorescent-display LEDs. Light: ivory dials with an amber lamp, deep teal LEDs on a pale LCD-style screen |
| `compact` | Type about 0.9×, spacing 0.75×, sizes 0.88× | Smaller | Default | Default |
| `large` (living room) | Type about 1.2×, spacing 1.25×, sizes 1.2× | Larger | Default | Default |

## Settings

`AppSettings.Ui.Theme`: `system` (follow the Windows app mode, default), `dark`, `light`.
`AppSettings.Ui.Skin`: `default`, `hifi`, `compact`, `large`. An unknown value gives Default.

## Skin decisions (2026-11-23)

1. **Scope: tokens only.** A skin sets fonts, type scale, corner radii, spacing and sizes, and meter materials.
   It does not change control templates or page layouts.
2. **Spacing is a skin token.** Compact and Large change the room, not only the text size. It is built as two
   scales (`SpaceScale`, `SizeScale`) over the Default pixels in the views, not as a fixed step table: the views
   use 34 different spacing values and many sizes, and scales keep the Default skin pixel-exact.
3. **Live switch**, like a theme.
4. **Built-in skins only.** No plugin skins, no user skin files.
5. **Any skin with any theme.** Skin and theme are two choices in Settings → Appearance.
6. **All plugin pages follow the skin**, with no Plugin SDK change (`HostSkin`).
7. **Fonts:** a skin may bundle an open font (SIL Open Font License), listed in the third-party notices.
8. **Light meters (2026-11-24).** In light themes the VU dials have cream faces and the spectrum a light screen
   (both a second material set per skin), so the meters do not sit as dark blocks on a light page.

## Not in scope now (noted for later)

- Semantic state tokens (`Success`/`Warning`/`Danger`/`Info` over the hue families), so a theme can recolor states
  (for example a color-blind theme).
- User theme files. If wanted: JSON with color tokens only, validated against the palette contract on import.
  No loose XAML from users: `XamlReader` can create any type, so it can run code.
- The tray menu (Windows Forms) keeps the Windows font and spacing in every skin.

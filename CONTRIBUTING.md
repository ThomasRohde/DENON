# Contributing

Thank you for your help with Denon Desktop. This file tells you how to build, test and send a change.

See also: [docs/plugin-development.md](docs/plugin-development.md) and [SECURITY.md](SECURITY.md).

## What you need

- Windows 10 or Windows 11 (x64). The app is a WPF app.
- The .NET 10 SDK. CI uses `10.0.x`.
- Node.js 24, only when you change the website in `site/`.

## Build

Build the app and the built-in plugins:

```powershell
dotnet build .\DenonDesktop.slnx -c Release
```

The build must have no warnings. Fix each warning that your change causes.

Run the app from source:

```powershell
dotnet run --project .\Denon.App.Desktop\Denon.App.Desktop.csproj -- --show
```

`DenonDesktop.slnx` has the SDK, Core, the three built-in plugins (`Denon.Plugin.SmartTv`,
`Denon.Plugin.PhilipsHue`, `Denon.Plugin.HomeAssistant`), the desktop app and `Denon.App.Core.Tests`.

The external plugins are not in `DenonDesktop.slnx`. Each one has its own solution in `external-plugins/`:

| Solution | Projects |
|---|---|
| `Denon.Plugin.AppleTv.slnx` | SDK, Apple TV plugin, its tests, the probe tool |
| `Denon.Plugin.HomePod.slnx` | SDK, HomePod plugin, its tests |
| `Denon.Plugin.Chromecast.slnx` | SDK, Chromecast plugin, its tests |

To build and install an external plugin in your own app, run its `install.ps1`, for example:

```powershell
pwsh -File .\external-plugins\Denon.Plugin.Chromecast\install.ps1
```

## Test

Run the same tests as CI before you open a pull request:

```powershell
dotnet test .\DenonDesktop.slnx -c Release --nologo
dotnet test external-plugins/Denon.Plugin.AppleTv.Tests/Denon.Plugin.AppleTv.Tests.csproj -c Release --nologo
dotnet test external-plugins/Denon.Plugin.HomePod.Tests/Denon.Plugin.HomePod.Tests.csproj -c Release --nologo
dotnet test external-plugins/Denon.Plugin.Chromecast.Tests/Denon.Plugin.Chromecast.Tests.csproj -c Release --nologo
```

Run the plugin tests also when you change only Core or the SDK. A change there can break the external plugins.

When you change the website, build it:

```powershell
cd site
npm ci
npm run build
```

## CI checks

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on each pull request to `master` and on each push to
`master`. It has three jobs:

| Job | Runner | What it does |
|---|---|---|
| `app` | `windows-latest` | `dotnet test .\DenonDesktop.slnx -c Release` |
| `plugins` | `windows-latest` | `dotnet test` for the Apple TV, HomePod and Chromecast test projects |
| `site` | `ubuntu-latest` | `npm ci` and `npm run build` in `site/` |

All three jobs must pass.

## Branches and pull requests

1. Make a branch from `master`. Use a name that tells the area, for example `fix/chromecast-plugin` or
   `docs/developer-guides`.
2. Keep one area in each pull request (for example the receiver, one plugin, or the MCP server).
3. Write the change and its tests.
4. Add a line to `CHANGELOG.md` under `## [Unreleased]`, in the correct section: `### Added`, `### Changed`,
   `### Fixed`, `### Removed` or `### Security`. Tell what changed for the user. Give the issue number, for
   example `(#73)`.
5. For a change to an external plugin, also add a line to that plugin's own `CHANGELOG.md`
   (`external-plugins/<plugin>/CHANGELOG.md`).
6. Keep `TODO.md` current. Add the work that you leave open. Tick or remove the items that you finish.
7. Open the pull request to `master`. Make sure that CI passes.

## Code style

The repository has no `.editorconfig`. Use the style of the file that you change:

- Each project has `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.
- Use file-scoped namespaces (`namespace Denon.App.Core.Plugins;`).
- Use four spaces for indentation.
- In XAML, refer to theme colors with `DynamicResource`, never `StaticResource`. Write spacing and sizes with
  `{skin:Space}` and `{skin:Size}`. Guard tests check this. See [docs/theming.md](docs/theming.md).
- Show volume as the receiver's 0–98 number, never in dB.

## No hard-coded devices

The app must work for each user's equipment. Do not put your own receiver, TV, lights, speakers or IP addresses
in the code. Find devices with discovery (SSDP, mDNS) or read them from the settings. Your own devices are test
data only.

## Releases

Releases start from tags:

- `vX.Y.Z` releases the app (`release.yml`). The tag must match `<Version>` in
  `Denon.App.Desktop\Denon.App.Desktop.csproj`, and `CHANGELOG.md` must have a `## [X.Y.Z]` section.
- `appletv-vX.Y.Z`, `homepod-vX.Y.Z` and `chromecast-vX.Y.Z` release the external plugins. See
  [docs/plugin-development.md](docs/plugin-development.md#release).

`build-release.ps1` runs the app release steps on your PC: tests, the single-file exe, the Inno Setup installer
and `SHA256SUMS.txt`. It needs Inno Setup 6.

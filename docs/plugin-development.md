# Plugin development

This guide tells you how to write an external plugin for Denon Desktop. The three external plugins in
[`external-plugins/`](../external-plugins) (Apple TV, HomePod, Chromecast) are the reference. The examples below
come from them.

See also: [CONTRIBUTING.md](../CONTRIBUTING.md) and [SECURITY.md](../SECURITY.md).

## The Plugin SDK

- The SDK is the project `Denon.App.PluginSdk`. Its version is `<Version>` in
  [`Denon.App.PluginSdk.csproj`](../Denon.App.PluginSdk/Denon.App.PluginSdk.csproj) (now 2.2.0).
- The SDK has no attributes. A plugin is a public, non-abstract class that implements `IDenonPlugin` and has a
  constructor with no parameters. The host creates it with `Activator.CreateInstance`.
- One DLL can hold more than one plugin class.

### How the host decides to load a plugin

`PluginHostService` (in `Denon.App.Core/Plugins`) does these steps for each `*.dll` it finds:

1. It skips files whose names start with `Denon.App.`, `System.` or `Microsoft.`.
2. It loads the DLL in its own collectible `AssemblyLoadContext`. The plugin's other DLLs resolve through its
   `.deps.json` (`AssemblyDependencyResolver`).
3. It reads the version of `Denon.App.PluginSdk` that the DLL references. When that version is newer than the
   host's SDK, the host does not load the plugin. The log then says:
   `Plugin '<file>' needs Plugin SDK <x> or newer; this Denon Desktop has <y>. Update Denon Desktop.`
4. It creates each `IDenonPlugin` class in the DLL.
5. It skips a plugin whose `Manifest.Id` is already in use (case-insensitive). Built-in plugins register first.
6. It unloads the context when no plugin from the DLL registered (for example a dependency DLL).

The host does not refuse a plugin that was built for an older SDK. Build against the SDK of the app release that
you target, and release a plugin version for it.

## Project file

Copy the setup from an external plugin, for example
[`Denon.Plugin.Chromecast.csproj`](../external-plugins/Denon.Plugin.Chromecast/Denon.Plugin.Chromecast.csproj):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Version>0.7.0</Version>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <RootNamespace>Denon.Plugin.Chromecast</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Denon.App.PluginSdk\Denon.App.PluginSdk.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

</Project>
```

- `EnableDynamicLoading` makes a project that the host loads at run time. The build writes the `.deps.json` and
  copies the NuGet dependencies (for example `BouncyCastle.Cryptography.dll` in the Apple TV plugin).
- `UseWPF` is necessary only for a dashboard view.
- `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>` make the SDK reference compile-only.
  `Denon.App.PluginSdk.dll` must not be in the plugin's output. The host shares its own SDK assembly with every
  plugin, so `IDenonPlugin` and the other SDK types are the same types in the host and in the plugin.
  `install.ps1` and the release workflows stop with an error when the output contains `Denon.App.PluginSdk.dll`.
- The plugin's version is `<Version>` in the project file. The real plugins read it into the manifest:
  `typeof(ChromecastPlugin).Assembly.GetName().Version?.ToString(3)`.
- An external plugin is not part of `DenonDesktop.slnx`. Give it its own `.slnx` with the SDK, the plugin and its
  tests, like [`Denon.Plugin.Chromecast.slnx`](../external-plugins/Denon.Plugin.Chromecast.slnx).

## The plugin interface

`IDenonPlugin` (in `IDenonPlugin.cs`) has these members:

| Member | What it does |
|---|---|
| `PluginManifest Manifest` | Id, name, version, author, description, category, accent color, icon text and capabilities. |
| `InitializeAsync(IPluginContext, CancellationToken)` | Called once, also for a disabled plugin. Keep the context. Load the configuration. |
| `StartAsync(CancellationToken)` | Called when the plugin is enabled: at app start and when the user enables it. Start connections and loops here. |
| `StopAsync(CancellationToken)` | Called when the user disables the plugin and when the app exits. Stop everything that `StartAsync` started. |
| `GetActions()` | The actions of the plugin. The host reads them after `InitializeAsync` and after each `StartAsync`. |
| `ExecuteActionAsync(actionId, parameters, CancellationToken)` | Runs an action. Parameter values are strings. |
| `OnReceiverEventAsync(PluginReceiverEvent, CancellationToken)` | A receiver change (see below). |
| `DisposeAsync()` | From `IAsyncDisposable`. Called at app exit, after `StopAsync`. |

The manifest of the Chromecast plugin:

```csharp
public PluginManifest Manifest { get; } = new(
    Id: "chromecast",
    Name: "Chromecast & Google Cast",
    Version: typeof(ChromecastPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
    Author: "Thomas Klok Rohde",
    Description: "Finds Chromecast and Google Cast devices on the LAN (mDNS), ...",
    Category: "Streaming Sources",
    AccentHex: "#F59E0B",
    IconGlyph: "CAST",
    Capabilities: PluginCapabilities.DashboardUi
                  | PluginCapabilities.ReceiverEvents
                  | PluginCapabilities.SceneAutomation
                  | PluginCapabilities.Actions
                  | PluginCapabilities.LocalNetwork
                  | PluginCapabilities.ReceiverControl);
```

`PluginCapabilities` has `DashboardUi`, `ReceiverEvents`, `SceneAutomation`, `Actions`, `LocalNetwork`,
`AudioReactive`, `AlbumArtSync` and `ReceiverControl`. Only `ReceiverControl` changes what the host gives the
plugin (see `ReceiverControl` below). Declare the others so that they agree with what the plugin does.

### Lifecycle rules

- The host catches an exception from `InitializeAsync` or `StartAsync`. The plugin then shows "Init failed" or
  "Start failed" with the message. The app continues.
- The host can call `StartAsync` again after `StopAsync` (the user disables and enables the plugin). Start from a
  clean state each time.
- At app exit, the host calls `StopAsync` and then `DisposeAsync`. Make both safe to call more than once. The
  Chromecast plugin calls `StopAsync` from `DisposeAsync`.
- A disabled plugin is not started. Its actions fail with "Plugin '<name>' is disabled."
- Use `context.IsAppShuttingDown` in `StopAsync` to tell an app exit from a disable. The real plugins end their
  sessions with the reason `app_shutdown` or `plugin_stopped`.

## The plugin context

`IPluginContext` (in `IPluginContext.cs`) gives the plugin these services:

| Member | Use |
|---|---|
| `PluginId` | The manifest id. |
| `Logger` | `Info`, `Warning`, `Error(message, exception)`. Lines go to the app log with `[Plugin:<id>]`. |
| `Events` | `RecordEvent(eventName, data, stream, origin)` writes a structured event to the habit event log. Without `stream`, the event goes to `plugin:<id>`. The Apple TV, HomePod and Chromecast plugins write to `media`. |
| `Storage` | `PluginDataDirectory`, `LoadConfigAsync<T>(defaultFactory)`, `SaveConfigAsync<T>(config)`. |
| `Receiver` | `CurrentSnapshot`: the current receiver state as a `PluginReceiverSnapshot`. |
| `ReceiverControl` | Receiver commands. Null when the manifest does not declare `ReceiverControl`. |
| `IsAppShuttingDown` | True while the app exits. |
| `CreateHttpClient(allowLocalSelfSignedTls, timeout)` | An `HttpClient` for LAN devices. The default timeout is 8 seconds. With `allowLocalSelfSignedTls`, a self-signed certificate is accepted only from a LAN address or a `.local` name. |
| `ReportStatus(text, health)` and `ReportStatus(text, health, details)` | The status line and health on the Plugins page. `details` is a string dictionary; each report replaces the previous details. |
| `RunPluginActionAsync(pluginId, actionId, parameters)` | Runs an action of another plugin. It fails without an exception when that plugin is missing or disabled. |

### Storage

`Storage` keeps one JSON file per plugin: `config.json` in
`%LocalAppData%\DenonDesktop\plugins\data\<id>\`. `LoadConfigAsync` returns the default when the file is missing
or cannot be read. `SaveConfigAsync` writes a temporary file and then moves it over `config.json`.

```csharp
public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
{
    _context = context;
    _receiverControl = context.ReceiverControl;
    _config = await context.Storage.LoadConfigAsync(static () => new ChromecastConfig(), cancellationToken);
    _config.Devices ??= [];
    context.ReportStatus($"{_config.Devices.Count} device(s) configured.", PluginHealthState.Idle);
}
```

`PluginHealthState` has `Disabled`, `Idle`, `Connected`, `Warning` and `Error`.

### Receiver snapshot and events

`PluginReceiverSnapshot` has `IsConnected`, `IsPoweredOn`, `Volume`, `IsMuted`, `InputSource`, `SoundMode`,
`PlayState`, `NowPlayingTitle` and `ModelName`. `Volume` is the receiver's 0–98 number.

`PluginReceiverEvent` has `EventType`, `Snapshot`, `SceneId`, `SceneName` and `Timestamp`. `EventType` is one of
`PowerChanged`, `InputChanged`, `PlaybackChanged`, `SoundModeChanged`, `SceneActivated` and `VolumeChanged`.

- The host sends events only to plugins that are enabled and started.
- Each plugin has its own queue. A plugin gets its events one at a time and in order. A slow plugin does not
  delay the other plugins.
- A volume change waits 400 ms for the next change. A slider drag is one `VolumeChanged` event.
- The host drops an event that was queued before the plugin stopped.

The HomePod plugin pauses the HomePods when the receiver turns on:

```csharp
public async Task OnReceiverEventAsync(PluginReceiverEvent receiverEvent, CancellationToken cancellationToken = default)
{
    if (!_config.PauseWhenReceiverPowersOn ||
        receiverEvent.EventType != PluginReceiverEventType.PowerChanged ||
        !receiverEvent.Snapshot.IsPoweredOn)
    {
        return;
    }

    // ...
}
```

### ReceiverControl

Declare `PluginCapabilities.ReceiverControl` to get `context.ReceiverControl` (`IPluginReceiverControl`). It has
`SetPowerAsync`, `SetInputSourceAsync` (AVR source code, for example `"TV"`, `"MPLAY"`), `SetSoundModeAsync`
(for example `"MOVIE"`), `SetVolumeAsync` (0–98; the host clamps the value), `SetMuteAsync`, `GetScenes` and
`RunSceneAsync` (scene id or name). Each call returns the snapshot after the command. The host writes each
command to the app log with the plugin id.

## Actions and MCP

An action is a `PluginActionDescriptor(ActionId, DisplayName, Description, Category)`. Actions show on the Plugins
page, in Smart Scenes and automations, and as MCP tools named `plugin_<id>_<action>`. The tool name is in
lowercase, and each run of characters that are not letters or digits becomes `_`.

Give each action its MCP metadata:

- `Parameters`: a list of `PluginActionParameter(Name, Type, Description, Required)`. `Type` is `string`,
  `integer`, `number` or `boolean`. Another type becomes `string`. The list is the tool's input schema.
- `IsReadOnly`: true when the action only reads state.
- `IsDestructive`: true when the action can stop, close or switch something off.

From the Chromecast plugin:

```csharp
private static readonly PluginActionParameter DeviceParam = new(
    "device",
    "string",
    "Cast device name, id or IP (for example \"Stuen 2\"). Optional: default is the device that plays, ...");

public IReadOnlyList<PluginActionDescriptor> GetActions() =>
[
    new("list-devices", "List Cast devices", "Returns all known Cast devices and their live state as JSON.", "Cast")
        { Parameters = [], IsReadOnly = true },
    new("stop", "Stop media", "Stops the media session (the program closes; it cannot be resumed).", "Playback")
        { Parameters = [DeviceParam], IsDestructive = true },
    new("set-volume", "Set Cast volume", "Sets the Cast device volume.", "Volume")
    {
        Parameters = [new("level", "integer", "Volume 0-100.", Required: true), DeviceParam],
        IsDestructive = false,
    },
];
```

The MCP server uses the hints for its permission tiers (`McpPluginToolBridge`, `DenonMcpServer.Tools.cs`):

| Action | Observer | SafeControl | FullOrchestration |
|---|---|---|---|
| Read-only | Runs | Runs | Runs |
| Not read-only | Refused | Runs | Runs |
| `IsDestructive = true`, or an id with `power_off` | Refused | Refused | Runs |

When a hint is null, the host guesses from the action id. Read-only: the id ends with `.refresh`. Destructive:
the id contains `power_off`, `all_off` or `close_app`. Set the hints yourself. Do not depend on the guess.

Return a `PluginActionResult`:

- `PluginActionResult.Ok(message)`.
- `PluginActionResult.OkWithData(message, dataJson, imageBase64, imageMimeType)`. MCP clients get the JSON as
  text, and as structured content when it is a JSON object. An image becomes an MCP image block
  (`image/jpeg` when you give no type).
- `PluginActionResult.Fail(message)`. MCP clients get it as an error.

## Dashboard view

Implement `IPluginViewProvider` to show a page on the Plugins view:

```csharp
public sealed class ChromecastPlugin : IDenonPlugin, IPluginViewProvider
{
    public object? CreateDashboardView() => new ChromecastDashboardView(this);
}
```

- The host calls `CreateDashboardView` on the WPF UI thread. Return a `FrameworkElement`, usually a `UserControl`.
- The host creates the view once and keeps it. Subscribe to your plugin's events in `Loaded`, and unsubscribe in
  `Unloaded`, as `ChromecastDashboardView` does.
- The host catches an exception from `CreateDashboardView` and shows the message instead of the view.

### Theme and skin: HostTheme and HostSkin

A plugin page follows the app's theme and skin with no SDK change (see [theming.md](theming.md)). Copy two files
from an external plugin into your project and change the namespace:

- [`HostTheme.cs`](../external-plugins/Denon.Plugin.Chromecast/HostTheme.cs):
  - `HostTheme.Brush("TextPrimary")` returns the host brush `TextPrimaryBrush`.
  - `HostTheme.Style("PrimaryButton")` returns a host style.
  - Without a host (unit tests), they return a transparent brush and null.
- [`HostSkin.cs`](../external-plugins/Denon.Plugin.Chromecast/HostSkin.cs):
  - It binds sizes and spacing to the host's `SkinMetrics` resource.
  - For a page built in code, call `HostSkin.Follow(root)` on each element tree. It turns literal margins,
    padding and sizes (in Default-skin pixels) into live skin bindings.
  - For font sizes and radii, use `element.Token(property, "FontSize10")`.
  - In XAML, use `{skin:Space 0,0,0,12}` and `{skin:Size 32}`.

Rules for a page built in code:

- Read a brush at each use. Do not keep a brush in a static field. The Chromecast page uses properties:

  ```csharp
  private static Brush TitleBrush => HostTheme.Brush("TextPrimary");
  private static Brush MutedBrush => HostTheme.Brush("TextMuted");
  ```

- Build the layout, then call `HostSkin.Follow`:

  ```csharp
  Content = HostSkin.Follow(BuildLayout());
  ```

- Use a font size token:

  ```csharp
  new TextBlock { Text = "Google Cast devices", Foreground = TitleBrush }
      .Token(TextElement.FontSizeProperty, "FontSize11");
  ```

The HomePod plugin compiles in the Apple TV plugin's `HostTheme.cs` and `HostSkin.cs` with `<Compile Include=...
Link=...>`, so the code exists once in the repository.

## Audio-reactive plugins

Implement `IAudioReactivePlugin` (in `PluginAudioFrame.cs`) to get live audio frames:

- `IsAudioSyncActive`: true while the plugin wants frames.
- `OnAudioFrame(in PluginAudioFrame frame)`: called about 30–60 times a second. Do not block in it.
- `OnAlbumArtPaletteChangedAsync(PluginAlbumArtPalette palette)`: the colors of the current artwork.

## Where the plugin is installed

The host scans two folders, with all subfolders:

- `%LocalAppData%\DenonDesktop\plugins\external\` (`AppDataPaths.ExternalPluginsDirectory`).
- The `plugins` folder next to the app.

Put each plugin in its own folder, for example `plugins\external\Chromecast\`. Plugin data is in
`%LocalAppData%\DenonDesktop\plugins\data\<id>\`.

Each external plugin has an `install.ps1`. It does these steps:

1. It stops when Denon Desktop runs. The app keeps the plugin's files open.
2. It runs `dotnet publish` to `bin\publish`.
3. It stops when the output contains `Denon.App.PluginSdk.dll`.
4. It removes the old plugin folder and copies the new output into it.

```powershell
pwsh -File .\external-plugins\Denon.Plugin.Chromecast\install.ps1
```

Then start Denon Desktop, or rescan plugins in the Plugins view.

## Build and test

Each external plugin has its own solution and test project:

```powershell
dotnet build .\external-plugins\Denon.Plugin.Chromecast.slnx -c Release
dotnet test .\external-plugins\Denon.Plugin.Chromecast.Tests\Denon.Plugin.Chromecast.Tests.csproj -c Release
```

- A test project references the plugin project and the SDK in the normal way. The plugin opens its internals to
  the tests with `<InternalsVisibleTo Include="Denon.Plugin.Chromecast.Tests" />`.
- `HostTheme` and `HostSkin` give neutral values without a host, so view code does not fail in tests.
- CI (`.github/workflows/ci.yml`) runs the tests of all three external plugins on each pull request. A change to
  Core or the SDK can break them.

## Release

Each external plugin has its own release workflow and tag:

| Plugin | Tag | Workflow |
|---|---|---|
| Apple TV | `appletv-vX.Y.Z` | `release-appletv-plugin.yml` |
| HomePod | `homepod-vX.Y.Z` | `release-homepod-plugin.yml` |
| Chromecast | `chromecast-vX.Y.Z` | `release-chromecast-plugin.yml` |

To release:

1. Set `<Version>` in the plugin's `.csproj`.
2. Add a `## [X.Y.Z] - <date>` section to the plugin's own `CHANGELOG.md`.
3. Push the tag.

The workflow then does these steps:

1. It stops when the tag does not match `<Version>`.
2. It runs the plugin's tests. The HomePod workflow also runs the Apple TV tests (shared protocol code).
3. It publishes the plugin and stops when the output contains `Denon.App.PluginSdk.dll`.
4. It makes `Denon.Plugin.<Name>-X.Y.Z.zip` and `SHA256SUMS.txt`.
5. It makes a GitHub release with the notes from the plugin's `CHANGELOG.md` section, with `make_latest: false`.
   The latest release stays the app's.
6. It starts the website build.

The app's update check (`UpdateService`) finds new releases only for the plugins with the ids `chromecast`,
`apple-tv` and `homepod`. For these, it links to the release page. It does not install plugins.

# Denon Desktop

WPF desktop controller for a HEOS-enabled Denon receiver with a tray-resident background polling service.

## Projects

- `Denon.App.Core`: HEOS protocol client, persisted settings, receiver state store, app log store, and polling service.
- `Denon.App.Desktop`: WPF shell, tray icon controller, and the desktop UI for transport, volume, presets, settings, and logs.

## Features

- Live receiver polling in the background through a tray process
- Quick tray actions for open, refresh, transport, mute, low volume, and preset playback
- Main dashboard for now playing, transport controls, volume, and custom stream URLs
- Editable station presets and persisted receiver settings in `%LocalAppData%\DenonDesktop\settings.json`
- Runtime logs written to `%LocalAppData%\DenonDesktop\logs\app.log`

## Run

```powershell
dotnet build .\DenonDesktop.slnx
dotnet run --project .\Denon.App.Desktop\Denon.App.Desktop.csproj
```

## Publish

```powershell
dotnet publish .\Denon.App.Desktop\Denon.App.Desktop.csproj -c Release -r win-x64 --self-contained false
```

## Notes

- This is a tray application, not a Windows Service. Closing or minimizing the main window can leave the background process running in the notification area depending on the saved UI settings.
- The default receiver profile is seeded for `192.168.1.3:1255` and `Denon AVR-X2400H`, matching the existing home control setup.

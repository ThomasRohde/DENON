# Builds the Chromecast plugin and copies it to the Denon Desktop external plugin folder.
# Usage (from any folder):  pwsh ./external-plugins/Denon.Plugin.Chromecast/install.ps1
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

# The app keeps the plugin's files open while it runs: copying then fails halfway and leaves old and new files mixed.
if (Get-Process -Name "Denon.App.Desktop" -ErrorAction SilentlyContinue) {
    throw "Denon Desktop is running. Quit it (tray icon > Exit), then run this script again."
}

$project = Join-Path $PSScriptRoot "Denon.Plugin.Chromecast.csproj"
$out = Join-Path $PSScriptRoot "bin\publish"
# Start from empty folders, so no file from an older build is left behind.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c $Configuration -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
# The app loads its own SDK; a plugin with its own copy would not load.
if (Test-Path (Join-Path $out "Denon.App.PluginSdk.dll")) { throw "The build contains Denon.App.PluginSdk.dll; it must not." }

$target = Join-Path $env:LOCALAPPDATA "DenonDesktop\plugins\external\Chromecast"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $out "*") $target -Recurse -Force
Write-Host "Installed to $target"
Write-Host "Restart Denon Desktop, or rescan plugins in the Plugins view."

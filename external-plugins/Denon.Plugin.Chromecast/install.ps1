# Builds the Chromecast plugin and copies it to the Denon Desktop external plugin folder.
# Usage (from any folder):  pwsh ./external-plugins/Denon.Plugin.Chromecast/install.ps1
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "Denon.Plugin.Chromecast.csproj"
$out = Join-Path $PSScriptRoot "bin\publish"
dotnet publish $project -c $Configuration -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$target = Join-Path $env:LOCALAPPDATA "DenonDesktop\plugins\external\Chromecast"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $out "*") $target -Recurse -Force
Write-Host "Installed to $target"
Write-Host "Restart Denon Desktop, or rescan plugins in the Plugins view."

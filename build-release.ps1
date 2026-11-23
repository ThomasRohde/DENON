# Local release build. Same steps as .github/workflows/release.yml:
# test -> single-file win-x64 publish -> Inno Setup installer -> SHA256SUMS.
# Usage:  pwsh -File .\build-release.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test .\DenonDesktop.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$outDir = Join-Path $PSScriptRoot "release-out"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish .\Denon.App.Desktop\Denon.App.Desktop.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Move-Item (Join-Path $outDir "Denon.App.Desktop.exe") (Join-Path $outDir "Denon.App.Desktop-win-x64.exe") -Force
Remove-Item (Join-Path $outDir "*.xml") -Force -ErrorAction SilentlyContinue

$iscc = Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
    if (-not $iscc -and (Test-Path $candidate)) { $iscc = $candidate }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup" }

[xml]$csproj = Get-Content .\Denon.App.Desktop\Denon.App.Desktop.csproj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
$srcExe = Join-Path $outDir "Denon.App.Desktop-win-x64.exe"
& $iscc "/DMyAppVersion=$version" "/DMySourceExe=$srcExe" "/DMyOutputDir=$outDir" .\installer\DenonDesktop.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setup = Join-Path $outDir "DenonDesktop-Setup-win-x64.exe"
@(
    "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant())  DenonDesktop-Setup-win-x64.exe",
    "$((Get-FileHash $srcExe -Algorithm SHA256).Hash.ToLowerInvariant())  Denon.App.Desktop-win-x64.exe"
) | Set-Content -Path (Join-Path $outDir "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
Write-Host "BUILD-RELEASE-OK version $version" -ForegroundColor Green
Get-ChildItem $outDir | Format-Table Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}} -AutoSize

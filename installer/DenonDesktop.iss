; Denon Desktop — Unsigned Windows x64 Installer (Inno Setup 6)
; Build in CI or locally via:
;   iscc /DMyAppVersion=1.10.0 /DMySourceExe=..\release-out\Denon.App.Desktop-win-x64.exe /DMyOutputDir=..\release-out .\installer\DenonDesktop.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.10.0"
#endif

#ifndef MySourceExe
  #define MySourceExe "..\Denon.App.Desktop\AppRelease\Denon.App.Desktop.exe"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\release-out"
#endif

#define MyAppName "Denon Desktop"
#define MyAppPublisher "Thomas Rohde"
#define MyAppURL "https://github.com/ThomasRohde/DENON"
#define MyAppExeName "Denon.App.Desktop.exe"

[Setup]
AppId={{B48C17F1-4D83-4C5C-9E3A-9D5E6B2C140A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#MyOutputDir}
OutputBaseFilename=DenonDesktop-Setup-win-x64
SetupIconFile=..\Denon.App.Desktop\denon_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=Denon.App.Desktop.exe
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=Denon Desktop Installer
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Launch Denon Desktop minimized to tray when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#MySourceExe}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DenonDesktop"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--show"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

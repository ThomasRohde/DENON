; Denon Desktop — Unsigned Windows x64 Installer (Inno Setup 6)
; Build in CI or locally via:
;   iscc /DMyAppVersion=1.22.0 /DMySourceExe=..\release-out\Denon.App.Desktop-win-x64.exe /DMyOutputDir=..\release-out .\installer\DenonDesktop.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.22.0"
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
DisableWelcomePage=no
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#MyOutputDir}
OutputBaseFilename=DenonDesktop-Setup-win-x64
SetupIconFile=..\Denon.App.Desktop\denon_icon.ico
WizardImageFile=wizard-sidebar.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern dark polar
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

[Messages]
SetupWindowTitle=Denon Desktop {#MyAppVersion} — Setup
WelcomeLabel1=Welcome to Denon Desktop
WelcomeLabel2=This will install Denon Desktop {#MyAppVersion} on your computer.%n%nControl your Denon / Marantz AVR & HEOS system, stream YouTube & Hi-Res audio, coordinate LG webOS TV & Philips Hue lighting, and connect AI assistants via MCP.%n%nClick Next to continue.
FinishedHeadingLabel=Denon Desktop is Ready
FinishedLabelNoIcons=Denon Desktop {#MyAppVersion} has been installed on your computer.
FinishedLabel=Denon Desktop {#MyAppVersion} has been installed on your computer. Launch it now to connect to your receiver.

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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DenonDesktop"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--show"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // Denon Desktop Cyan Accent (Inno Setup uses $BBGGRR color order)
  DenonCyan = $FFD400; // #00D4FF

procedure CreateAccentLine(ParentCtrl: TWinControl; TopPos, WidthPx, HeightPx: Integer; LineColor: TColor);
var
  Line: TLabel;
begin
  Line := TLabel.Create(WizardForm);
  Line.Parent := ParentCtrl;
  Line.AutoSize := False;
  Line.Transparent := False;
  Line.Caption := '';
  Line.Left := 0;
  Line.Top := TopPos;
  Line.Width := WidthPx;
  Line.Height := HeightPx;
  Line.Color := LineColor;
end;

procedure InitializeWizard();
begin
  // Pre-select "I accept the agreement" by default for faster installs and upgrades
  WizardForm.LicenseAcceptedRadio.Checked := True;

  // Highlight wizard headings & header divider with Denon Cyan (#00D4FF) over native dark polar theme
  WizardForm.WelcomeLabel1.Font.Color := DenonCyan;
  WizardForm.PageNameLabel.Font.Color := DenonCyan;
  WizardForm.FinishedHeadingLabel.Font.Color := DenonCyan;
  WizardForm.StatusLabel.Font.Color := DenonCyan;

  CreateAccentLine(WizardForm.MainPanel, WizardForm.MainPanel.Height - ScaleY(2), WizardForm.MainPanel.Width, ScaleY(2), DenonCyan);
end;

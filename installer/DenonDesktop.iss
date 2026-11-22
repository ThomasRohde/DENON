; Denon Desktop — Unsigned Windows x64 Installer (Inno Setup 6)
; Build in CI or locally via:
;   iscc /DMyAppVersion=1.14.0 /DMySourceExe=..\release-out\Denon.App.Desktop-win-x64.exe /DMyOutputDir=..\release-out .\installer\DenonDesktop.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.14.0"
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
  DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
  // Denon Desktop Obsidian & Cyan Palette (Inno Setup uses $BBGGRR color order)
  DenonChassisBg   = $100C0A; // #0A0C10
  DenonCardBg      = $1E1613; // #13161E
  DenonBorderColor = $362823; // #232836
  DenonCyan        = $FFD400; // #00D4FF
  DenonTextPrimary = $F9F5F1; // #F1F5F9
  DenonTextSecond  = $CEBFB0; // #B0BFCE

function DwmSetWindowAttribute(hwnd: HWND; dwAttribute: DWORD; var pvAttribute: DWORD; cbAttribute: DWORD): Longint;
  external 'DwmSetWindowAttribute@dwmapi.dll stdcall delayload';

procedure EnableDarkTitleBar(Handle: HWND);
var
  UseDark: DWORD;
begin
  UseDark := 1;
  try
    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, UseDark, SizeOf(UseDark));
  except
    // Ignore on Windows builds without DWM dark title bar support
  end;
end;

procedure OnLicenseAcceptLabelClick(Sender: TObject);
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

procedure OnLicenseDeclineLabelClick(Sender: TObject);
begin
  WizardForm.LicenseNotAcceptedRadio.Checked := True;
end;

procedure OnPreparingYesLabelClick(Sender: TObject);
begin
  WizardForm.PreparingYesRadio.Checked := True;
end;

procedure OnPreparingNoLabelClick(Sender: TObject);
begin
  WizardForm.PreparingNoRadio.Checked := True;
end;

procedure StyleDarkRadioButton(Radio: TNewRadioButton; OnClickHandler: TNotifyEvent);
var
  Lbl: TLabel;
begin
  Lbl := TLabel.Create(WizardForm);
  Lbl.Parent := Radio.Parent;
  Lbl.Left := Radio.Left + ScaleX(20);
  Lbl.Top := Radio.Top + ScaleY(1);
  Lbl.Width := Radio.Width - ScaleX(20);
  Lbl.AutoSize := True;
  Lbl.Transparent := True;
  Lbl.Caption := Radio.Caption;
  Lbl.Font.Name := 'Segoe UI';
  Lbl.Font.Size := Radio.Font.Size;
  Lbl.Font.Color := DenonTextPrimary;
  Lbl.Cursor := crHand;
  Lbl.OnClick := OnClickHandler;

  Radio.Caption := '';
  Radio.Width := ScaleX(18);
end;

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

var
  PreparingStyled: Boolean;

procedure InitializeWizard();
begin
  PreparingStyled := False;
  // Match Denon Desktop dark title bar and obsidian surfaces
  EnableDarkTitleBar(WizardForm.Handle);

  WizardForm.Color := DenonChassisBg;
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.Font.Color := DenonTextPrimary;

  WizardForm.OuterNotebook.Color := DenonChassisBg;
  WizardForm.InnerNotebook.Color := DenonCardBg;
  WizardForm.InnerPage.Color := DenonCardBg;
  WizardForm.WelcomePage.Color := DenonChassisBg;
  WizardForm.FinishedPage.Color := DenonChassisBg;
  WizardForm.MainPanel.Color := DenonChassisBg;

  // Replace default light bevels with Denon cyan header accent + obsidian hairline
  WizardForm.Bevel1.Visible := False;
  WizardForm.Bevel.Visible := False;
  CreateAccentLine(WizardForm.MainPanel, WizardForm.MainPanel.Height - ScaleY(2), WizardForm.MainPanel.Width, ScaleY(2), DenonCyan);
  CreateAccentLine(WizardForm, WizardForm.Bevel.Top, WizardForm.ClientWidth, ScaleY(1), DenonBorderColor);

  // Header typography
  WizardForm.PageNameLabel.Font.Name := 'Segoe UI';
  WizardForm.PageNameLabel.Font.Style := [fsBold];
  WizardForm.PageNameLabel.Font.Color := DenonCyan;
  WizardForm.PageDescriptionLabel.Font.Name := 'Segoe UI';
  WizardForm.PageDescriptionLabel.Font.Color := DenonTextSecond;

  // Welcome & Finished pages
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel1.Font.Color := DenonCyan;
  WizardForm.WelcomeLabel2.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel2.Font.Color := DenonTextSecond;

  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
  WizardForm.FinishedHeadingLabel.Font.Color := DenonCyan;
  WizardForm.FinishedLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedLabel.Font.Color := DenonTextSecond;
  WizardForm.RunList.Color := DenonChassisBg;
  WizardForm.RunList.Font.Color := DenonTextPrimary;

  // License page — pre-select Accept by default + crisp light labels on dark surface
  WizardForm.LicenseLabel1.Font.Color := DenonTextSecond;
  WizardForm.LicenseMemo.Color := DenonChassisBg;
  WizardForm.LicenseMemo.Font.Color := DenonTextPrimary;
  StyleDarkRadioButton(WizardForm.LicenseAcceptedRadio, @OnLicenseAcceptLabelClick);
  StyleDarkRadioButton(WizardForm.LicenseNotAcceptedRadio, @OnLicenseDeclineLabelClick);
  WizardForm.LicenseAcceptedRadio.Checked := True;

  // Directory, Tasks, Ready, Preparing & Progress pages
  WizardForm.SelectDirLabel.Font.Color := DenonTextPrimary;
  WizardForm.SelectDirBrowseLabel.Font.Color := DenonTextSecond;
  WizardForm.DiskSpaceLabel.Font.Color := DenonTextSecond;
  WizardForm.DirEdit.Color := DenonChassisBg;
  WizardForm.DirEdit.Font.Color := DenonTextPrimary;

  WizardForm.SelectTasksLabel.Font.Color := DenonTextSecond;
  WizardForm.TasksList.Color := DenonCardBg;
  WizardForm.TasksList.Font.Color := DenonTextPrimary;

  WizardForm.ReadyLabel.Font.Color := DenonTextSecond;
  WizardForm.ReadyMemo.Color := DenonChassisBg;
  WizardForm.ReadyMemo.Font.Color := DenonTextPrimary;

  WizardForm.StatusLabel.Font.Color := DenonCyan;
  WizardForm.FilenameLabel.Font.Color := DenonTextSecond;

  WizardForm.PreparingLabel.Font.Color := DenonTextPrimary;
  WizardForm.PreparingMemo.Color := DenonChassisBg;
  WizardForm.PreparingMemo.Font.Color := DenonTextPrimary;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpPreparing) and (not PreparingStyled) then
  begin
    PreparingStyled := True;
    if WizardForm.PreparingYesRadio.Caption <> '' then
      StyleDarkRadioButton(WizardForm.PreparingYesRadio, @OnPreparingYesLabelClick);
    if WizardForm.PreparingNoRadio.Caption <> '' then
      StyleDarkRadioButton(WizardForm.PreparingNoRadio, @OnPreparingNoLabelClick);
  end;
end;

; TLAH Studio - Inno Setup Installer Script
; Supports: manual install (GUI wizard) and silent update (/VERYSILENT /NORESTART)

#define MyAppName "TLAH Studio"
#define MyAppVersion "3.3.0"
#define MyAppPublisher "KeEntropy Technology"
#define MyAppExeName "TLAHStudio.App.exe"
#define MyAppUpdaterName "TLAHStudio.Updater.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://download.matrixlabs.cn/tlah
AppSupportURL=https://download.matrixlabs.cn/tlah
AppUpdatesURL=https://download.matrixlabs.cn/tlah/windows/latest.json

; User-level install (no admin required)
DefaultDirName={localappdata}\Programs\TLAH Studio
DisableDirPage=no
PrivilegesRequired=lowest

; Output
OutputDir=.\output
OutputBaseFilename=TLAHStudioSetup-{#MyAppVersion}

; Compression
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

; Uninstall display
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\TLAHStudio.App\Assets\app.ico

; Windows version
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Main application (self-contained publish with XAML fix)
Source: "..\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

; Updater executable
Source: "..\TLAHStudio.Updater\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; \
  DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"

; App settings
Source: "..\TLAHStudio.App\appsettings.json"; \
  DestDir: "{app}"; Flags: ignoreversion

; Version info
Source: ".\version.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
  Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
  GroupDescription: "Additional icons:"; Flags: checkedonce

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent; Check: ShouldLaunchAfterInstall
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent; Check: ShouldLaunchAfterInstall

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden
Filename: "taskkill"; Parameters: "/f /im {#MyAppUpdaterName}"; Flags: runhidden

[Code]
// Create data directories and stop running processes before file copy.
procedure KillProcessByName(ImageName: String);
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM "' + ImageName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
 end;
function HasCommandLineSwitch(SwitchName: String): Boolean;
begin
  Result := Pos('/' + UpperCase(SwitchName), UpperCase(GetCmdTail)) > 0;
end;
function ShouldLaunchAfterInstall(): Boolean;
begin
  Result := not HasCommandLineSwitch('NOLAUNCH');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Auto-update is launched by TLAHStudio.Updater from the install directory in
    // older builds. Kill it before file copy so .NET runtime DLLs can be replaced.
    KillProcessByName('{#MyAppExeName}');
    if not HasCommandLineSwitch('NOLAUNCH') then
      KillProcessByName('{#MyAppUpdaterName}');
    Sleep(800);
  end;

  if CurStep = ssPostInstall then
  begin
    CreateDir(ExpandConstant('{app}\data'));
    CreateDir(ExpandConstant('{app}\logs'));
    CreateDir(ExpandConstant('{app}\cache'));
  end;
end;

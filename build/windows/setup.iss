#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\win-x64"
#endif

[Setup]
AppId={{6C1C3F3E-5E0B-4B8B-9D0B-3B7E2B9B1A11}
AppName=BDS Headless
AppVersion={#AppVersion}
AppPublisher=BDS Headless Client
DefaultDirName={autopf}\BDS Headless
DefaultGroupName=BDS Headless
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts
OutputBaseFilename=bds-headless-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\assets\icon.ico
UninstallDisplayIcon={app}\bds-headless.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "autostart"; Description: "Start at boot (runs before anyone logs in)"
Name: "trayatlogin"; Description: "Show tray icon when I log in"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BDS Headless"; Filename: "{app}\bds-headless.exe"
Name: "{userstartup}\BDS Headless tray"; Filename: "{app}\bds-headless.exe"; Parameters: "--background"; Tasks: trayatlogin

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"" -InstallDir ""{app}"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing service..."; Tasks: not autostart
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-service.ps1"" -InstallDir ""{app}"" -AutoStart"; Flags: runhidden waituntilterminated; StatusMsg: "Installing service..."; Tasks: autostart
Filename: "{app}\bds-headless.exe"; Description: "Open BDS Headless"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-service.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  // Stop a running service before files are replaced on upgrade.
  if CurStep = ssInstall then
    Exec('powershell.exe', '-NoProfile -Command "Stop-Service BdsHeadless -Force -ErrorAction SilentlyContinue; Get-Process bds-headless -ErrorAction SilentlyContinue | Stop-Process -Force"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

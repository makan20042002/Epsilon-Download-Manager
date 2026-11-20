; Epsilon Download Manager - one-file installer (Inno Setup 6.3 or newer; free: https://jrsoftware.org/isdl.php)
; Build with build-installer.ps1 (it publishes first, then runs this script).
#define MyAppName "Epsilon Download Manager"
#define MyAppVersion "2.1.0"
#define MyAppPublisher "Makan A.D."
#define MyAppURL "https://makanlab.tech"
#define MyAppExeName "EpsilonDownloadManager.exe"
#define PowerShell "{sys}\WindowsPowerShell\v1.0\powershell.exe"

[Setup]
AppId={{8E2FBD68-4B9E-4F90-9C72-9C8B5B5F14A0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=EpsilonDownloadManager-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per user by default (no administrator needed); the wizard offers "for all users".
; The browser registration lives in the registry of the person who installs, so it always runs as that person (see [Run]).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\MakanDownloadManager\Assets\epsilon.ico
WizardStyle=modern
UsedUserAreasWarning=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousPrivileges=yes
CloseApplications=force
RestartApplications=no
VersionInfoVersion=2.1.0.0

[Tasks]
Name: "startup";     Description: "Start Epsilon quietly in the tray when I sign in to Windows"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "browser";     Description: "Connect the Chrome/Firefox extensions to Epsilon (replaces the old connection automatically)"

[Files]
; The public extensions now come from their official stores. The installer only needs the desktop app,
; native host, updater and registration helpers; no unpacked-extension folders or old setup text are shipped.
Source: "..\publish\EpsilonDownloadManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\EpsilonNativeHost.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\EpsilonUpdater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\install-browser-integration.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\install-background.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName} - check the browser connection"; Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -NoExit -File ""{app}\install-browser-integration.ps1"" -Check"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
; V2 has a separate folder and never removes or replaces V1 files.

[Registry]
; the same entry that Options > General > "start with Windows" writes
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EpsilonDownloadManager"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: startup

; V2 deliberately does not take over magnet: or .torrent associations during installation.
; V1 remains the handler until the user explicitly changes the Windows default application.

[Run]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install-browser-integration.ps1"" -InstallDir ""{app}"""; StatusMsg: "Connecting the browsers to Epsilon..."; Flags: runhidden runasoriginaluser waituntilterminated; Tasks: browser
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden runasoriginaluser skipifdoesntexist; StatusMsg: "Refreshing file associations..."
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install-browser-integration.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveBrowserIntegration"; Tasks: browser

[Code]
// Epsilon lives in the tray (closing its window does not end it), so it is stopped explicitly before files are replaced or removed.
// Unfinished downloads are kept and continue the next time Epsilon starts.
procedure StopEpsilon;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im EpsilonDownloadManager.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im EpsilonNativeHost.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im EpsilonUpdater.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEpsilon;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then StopEpsilon;
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\EpsilonDownloadManager2');
    if DirExists(Data) then
      if MsgBox('Also delete Epsilon''s settings, its download list and the YouTube tools?' + #13#10 + Data + #13#10#13#10 +
                'Files you downloaded are never deleted.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;

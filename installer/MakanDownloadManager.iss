; Epsilon Download Manager - one-file installer (Inno Setup 6.3 or newer; free: https://jrsoftware.org/isdl.php)
; Build with build-installer.ps1 (it publishes first, then runs this script).
#define MyAppName "Epsilon Download Manager"
#define MyAppVersion "1.7.0"
#define MyAppPublisher "Makan A.D."
#define MyAppURL "https://makanlab.tech"
#define MyAppExeName "MakanDownloadManager.exe"
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
SetupIconFile=..\MakanDownloadManager\Assets\makan.ico
WizardStyle=modern
UsedUserAreasWarning=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousPrivileges=yes
CloseApplications=force
RestartApplications=no
VersionInfoVersion=1.7.0.0

[Tasks]
Name: "startup";     Description: "Start Epsilon quietly in the tray when I sign in to Windows (the browser extension always finds it)"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; everything build-release.ps1 put into .\publish: the app, the native host, the updater, the browser extensions and helper scripts
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName} - browser extension folder"; Filename: "{app}\browser-extension"
Name: "{autoprograms}\{#MyAppName} - check the browser connection"; Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -NoExit -File ""{app}\install-browser-integration.ps1"" -Check"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; the same entry that Options > General > "start with Windows" writes
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MakanDownloadManager"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: startup

; magnet: link protocol - lets Makan appear as an option (and, on many Windows versions, become the app that opens automatically)
; for a magnet link clicked anywhere: another program, a torrent search site the browser extension does not run on, etc.
; Per-user (HKCU) to match "no administrator needed"; Windows may still ask the person to confirm the choice once (its own
; anti-hijacking protection for URL/file associations - this is expected and cannot be skipped by an installer).
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; .torrent files - same idea, as a normal file type association (its own ProgId, so it does not disturb any other app's file types)
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "MakanDownloadManager.torrent"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\MakanDownloadManager.torrent"; ValueType: string; ValueName: ""; ValueData: "BitTorrent file"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MakanDownloadManager.torrent\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\MakanDownloadManager.torrent\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install-browser-integration.ps1"" -InstallDir ""{app}"""; StatusMsg: "Connecting the browsers to Epsilon..."; Flags: runhidden runasoriginaluser waituntilterminated
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden runasoriginaluser skipifdoesntexist; StatusMsg: "Refreshing file associations..."
Filename: "{app}\EXTENSION-SETUP.txt"; Description: "Show how to add the extension to my browser"; Flags: postinstall shellexec skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\install-browser-integration.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveBrowserIntegration"

[Code]
// Makan lives in the tray (closing its window does not end it), so it is stopped explicitly before files are replaced or removed.
// Unfinished downloads are kept and continue the next time Makan starts.
procedure StopMakan;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MakanDownloadManager.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MakanNativeHost.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im MakanUpdater.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopMakan;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then StopMakan;
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{localappdata}\MakanDownloadManager');
    if DirExists(Data) then
      if MsgBox('Also delete Epsilon''s settings, its download list and the YouTube tools?' + #13#10 + Data + #13#10#13#10 +
                'Files you downloaded are never deleted.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;

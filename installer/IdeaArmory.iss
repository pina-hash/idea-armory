; IDEA Armory: the normal installer. Inno Setup 6.3 or later.
;
; Per-user: installs to %LOCALAPPDATA%\Programs\IDEA Armory for the Windows account that runs
; it and never asks for an administrator password. tools\package-agent.ps1 builds it from the
; same "files" folder as the flash-drive zip and passes:
;   /DAppVersion=0.1.0   /DPayloadDir=<the files folder>   /DOutputDir=<dist>   /DIconFile=<.ico>
;
; Before files are replaced, files\scripts\Setup.ps1 -Mode Stop closes a running copy
; (IdeaArmory.exe --quit, a bounded wait, then Stop-Process only for this exact path).
; Uninstall runs IdeaArmory.exe --quit, then Setup.ps1 -Mode InnoUninstall, which removes
; %LOCALAPPDATA%\IDEA Armory (settings, logs, sign-in, WebView2 cache) and every per-user key
; Armory writes (docs/agent/INSTALL.md). Neither ever touches the vault folder (C:\IDEA\Armory,
; or the one chosen in Armory's settings), and nothing here writes HKLM: the optional badges
; (status on file icons) are IDEA-Armory-Badges-Setup, with their own administrator step and
; their own uninstaller. The payload carries it as badges\IDEA-Armory-Badges-Setup.exe, which the
; last page offers to run.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>. tools\package-agent.ps1 does.
#endif
#ifndef PayloadDir
  #error Pass /DPayloadDir=<folder holding IdeaArmory.exe and scripts\Setup.ps1>. tools\package-agent.ps1 does.
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
; Must match $InnoAppId in installer\scripts\Setup.ps1 (tools\package-agent.ps1 checks it).
; The doubled brace is Inno's escape for a literal "{"; the uninstall key is "{GUID}_is1".
#define AppId "{{28A1D010-82E3-4294-9676-83AC0AA1F5D3}"
; Armory's AppUserModelID (ShellIdentity.AppId, and $Aumid in Setup.ps1; tools\package-agent.ps1
; checks all three): one identity for the process, the Start menu shortcut and the registration
; that names Armory and its icon in a notification.
#define Aumid "IdeaBosco.Armory"

[Setup]
AppId={#AppId}
AppName=IDEA Armory
AppVersion={#AppVersion}
AppVerName=IDEA Armory {#AppVersion}
AppPublisher=IDEA, Don Bosco Tech
AppPublisherURL=https://ideabosco.com
AppSupportURL=https://ideabosco.com
AppCopyright=IDEA, Don Bosco Tech
VersionInfoVersion={#AppVersion}
VersionInfoCompany=IDEA, Don Bosco Tech
VersionInfoProductName=IDEA Armory
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=IDEA Armory Setup
; Per-user and never elevated. A blank PrivilegesRequiredOverridesAllowed allows no override:
; neither /ALLUSERS on the command line nor the "install for all users" dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\IDEA Armory
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=IDEA-Armory-Setup-v{#AppVersion}
#ifdef IconFile
SetupIconFile={#IconFile}
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=IDEA Armory
UninstallDisplayIcon={app}\IdeaArmory.exe
SetupMutex=IDEA-Armory-Setup
CloseApplications=yes
RestartApplications=no
; Tells Explorer (SHCNE_ASSOCCHANGED) after installing and after uninstalling, so the idea-armory:
; link scheme and Armory's right-click items come and go at once.
ChangesAssociations=yes
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
ConfirmUninstall=Remove %1 from this Windows account?%n%nThe vault folder (C:\IDEA\Armory, or the folder chosen in Armory's settings) and every file in it stay on this computer.
UninstalledAll=%1 was removed from this Windows account.%n%nThe vault folder and every file in it were not touched.
FinishedLabelNoIcons=[name] is installed and running in the tray, near the clock. It starts each time you sign in to Windows.%n%nOpen it from the Start menu and click Connect to connect this computer.
FinishedLabel=[name] is installed and running in the tray, near the clock. It starts each time you sign in to Windows.%n%nOpen it from the Start menu and click Connect to connect this computer.

[Files]
; Extracted to {tmp} before installing, to close a running copy (PrepareToInstall).
Source: "{#PayloadDir}\scripts\Setup.ps1"; DestDir: "{tmp}"; DestName: "ArmorySetup.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; The window's page is replaced as a whole: a page file an older version shipped and this one
; does not must never be served next to the new page (an upgrade keeps everything else).
Type: filesandordirs; Name: "{app}\wwwroot"

[Icons]
Name: "{userprograms}\IDEA Armory"; Filename: "{app}\IdeaArmory.exe"; WorkingDir: "{app}"; Comment: "IDEA Armory: the team and class CAD vault"; AppUserModelID: "{#Aumid}"

[Registry]
; Start at sign-in, in the tray. Left off when this person turned it off in Armory's settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IDEA Armory"; ValueData: """{app}\IdeaArmory.exe"" --background"; Flags: uninsdeletevalue; Check: StartAtSignInWanted
; Notifications and links (docs/agent/INSTALL.md, docs/agent/EXPLORER.md section 7): the
; registration that names Armory and its icon in a notification, and the idea-armory: scheme a
; notification's buttons open. Armory writes the same values again at start when one differs.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: string; ValueName: "DisplayName"; ValueData: "IDEA Armory"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\Assets\armory.ico"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\idea-armory"; ValueType: string; ValueName: ""; ValueData: "URL:IDEA Armory"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\idea-armory"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\idea-armory\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\IdeaArmory.exe"",0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\idea-armory\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\IdeaArmory.exe"" ""%1"""; Flags: uninsdeletekey
; Written by Armory itself, never here: File Explorer's right-click items (ShellVerbs) and the
; badges' heartbeat (HKCU\Software\IDEA Armory). dontcreatekey leaves them to Armory at install;
; uninsdeletekey removes them at uninstall.
Root: HKCU; Subkey: "Software\Classes\AllFilesystemObjects\shell\IDEAArmory"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\IDEAArmory.Menu"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\IDEAArmory"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\IDEAArmory.BackgroundMenu"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\IDEA Armory"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\IdeaArmory.exe"; Parameters: "--background"; WorkingDir: "{app}"; Flags: nowait
Filename: "{app}\IdeaArmory.exe"; WorkingDir: "{app}"; Description: "Open IDEA Armory now"; Flags: postinstall nowait skipifsilent
; Optional, unchecked, and only while this computer has no badges: the one administrator step,
; for every account on the computer (docs/agent/EXPLORER.md section 2.5). Windows asks for the
; password; the badges setup shows its own pages.
Filename: "{app}\badges\IDEA-Armory-Badges-Setup.exe"; Verb: "runas"; WorkingDir: "{app}\badges"; Description: "Also show Armory's status on file icons (asks once for an administrator password for this computer)"; Flags: shellexec postinstall unchecked skipifsilent skipifdoesntexist nowait; Check: BadgesNotInstalled

[UninstallRun]
; Ask the running app to quit first. Setup.ps1 then waits a bounded time, stops only this exact
; IdeaArmory.exe if it is still running, removes %LOCALAPPDATA%\IDEA Armory and every per-user
; key Armory writes, and tells Explorer. The [Registry] keys above go after it, whatever it did.
Filename: "{app}\IdeaArmory.exe"; Parameters: "--quit"; WorkingDir: "{app}"; Flags: runhidden nowait skipifdoesntexist; RunOnceId: "ArmoryQuit"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\scripts\Setup.ps1"" -Mode InnoUninstall"; Flags: runhidden waituntilterminated; RunOnceId: "ArmoryRemoveData"

[UninstallDelete]
; Only WebView2's default cache beside the exe, in case the app ever used it.
Type: filesandordirs; Name: "{app}\IdeaArmory.exe.WebView2"

[Code]
const
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  FlashDriveUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory';
  WebView2Page = 'https://developer.microsoft.com/microsoft-edge/webview2/consumer/';
  BadgesKey = 'SOFTWARE\IDEA Armory\Badges';

{ The badges setup records its version for the whole computer under HKLM, which any account may
  read. Without it, the last page offers the badges. }
function BadgesNotInstalled(): Boolean;
begin
  Result := not RegValueExists(HKLM64, BadgesKey, 'Version');
end;

function PowerShellExe(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function IsRuntimeVersion(const V: String): Boolean;
begin
  Result := (V <> '') and (V <> '0.0.0.0');
end;

{ The Evergreen WebView2 Runtime: per-machine under WOW6432Node, or per-user under HKCU. }
function WebView2Version(): String;
var
  V: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\' + WebView2Client, 'pv', V) and IsRuntimeVersion(V) then
    Result := V
  else if RegQueryStringValue(HKCU, 'Software\' + WebView2Client, 'pv', V) and IsRuntimeVersion(V) then
    Result := V;
end;

{ Start at sign-in is on unless settings.json says "startAtSignIn": false. }
function StartAtSignInWanted(): Boolean;
var
  Raw: AnsiString;
  Text: String;
begin
  Result := True;
  if LoadStringFromFile(ExpandConstant('{localappdata}\IDEA Armory\settings.json'), Raw) then
  begin
    Text := Lowercase(String(Raw));
    StringChangeEx(Text, ' ', '', True);
    StringChangeEx(Text, #9, '', True);
    StringChangeEx(Text, #13, '', True);
    StringChangeEx(Text, #10, '', True);
    if Pos('"startatsignin":false', Text) > 0 then
      Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  V: String;
begin
  V := WebView2Version();
  Result := 'For this Windows account only (' + GetUserNameString() + '). No administrator password.' + NewLine + NewLine +
    'Program folder:' + NewLine + Space + ExpandConstant('{app}') + NewLine + NewLine +
    'Starts in the tray each time you sign in to Windows.' + NewLine + NewLine;
  if V <> '' then
    Result := Result + 'Microsoft Edge WebView2 Runtime: ' + V + NewLine
  else
    Result := Result + 'Microsoft Edge WebView2 Runtime: MISSING. Armory installs, but its window cannot open until the runtime is installed from ' + WebView2Page + NewLine;
  if BadgesNotInstalled() then
    Result := Result + NewLine + 'Optional, on the last page: Armory''s status on file icons, which asks once for an administrator password for this computer.' + NewLine;
  Result := Result + NewLine + 'Setup never changes the vault folder (C:\IDEA\Armory) or the files in it.';
end;

{ Refuses any other folder (for example /DIR=), then closes a running copy of this install. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Wanted: String;
  Code: Integer;
begin
  Result := '';
  Wanted := ExpandConstant('{localappdata}\Programs\IDEA Armory');
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), Wanted) <> 0 then
  begin
    Result := 'IDEA Armory always installs to ' + Wanted + '.';
    Exit;
  end;
  ExtractTemporaryFile('ArmorySetup.ps1');
  if not Exec(PowerShellExe(), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\ArmorySetup.ps1') + '" -Mode Stop', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Result := 'IDEA Armory is running and could not be closed. Quit it from its icon near the clock, then run setup again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  V: String;
begin
  if CurStep = ssPostInstall then
  begin
    { A flash-drive install of the same folder had its own Apps entry; this install owns it now. }
    if RegKeyExists(HKCU, FlashDriveUninstallKey) then
      RegDeleteKeyIncludingSubkeys(HKCU, FlashDriveUninstallKey);
    V := WebView2Version();
    if V = '' then
    begin
      Log('WebView2 runtime: MISSING');
      SuppressibleMsgBox('IDEA Armory is installed, but the Microsoft Edge WebView2 Runtime is missing, so the Armory window cannot open.' + #13#10#13#10 +
        'Install it from ' + WebView2Page + ' (no administrator password needed).', mbInformation, MB_OK, IDOK);
    end
    else
      Log('WebView2 runtime: ' + V);
  end;
end;

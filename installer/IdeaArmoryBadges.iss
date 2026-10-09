; IDEA Armory badges: the optional, one-time administrator step that shows Armory's status on
; file icons in File Explorer (docs/agent/EXPLORER.md). Inno Setup 6.3 or later.
;
; Machine-wide: four icon overlay handlers in ArmoryBadges.dll, registered under HKLM, for every
; Windows account on the computer. Armory itself stays a per-user install that never needs an
; administrator; without Armory running for a person, the handlers show nothing for them.
; tools\package-agent.ps1 builds it from tools\build-native.ps1's output and passes:
;   /DAppVersion=0.3.3   /DNativeDir=<publish\native, holding x64\ and arm64\>   /DOutputDir=<dist>   /DIconFile=<.ico>
; arm64\ArmoryBadges.dll is there when the build computer's Visual Studio has the ARM64 C++ tools;
; without it this setup carries the x64 DLL alone and installs on x64 Windows only (Windows on ARM
; runs an ARM64 Explorer, which cannot load an x64 DLL).
;
; Each version installs into its own folder ({app}\<version>): the DLL a running Explorer has
; loaded is never overwritten, so no restart is needed. Older version folders are removed after
; the install, or when Windows next starts if one is still loaded. Silent, for IT:
;   IDEA-Armory-Badges-Setup-v<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; Each person then sees the badges after signing out of Windows and back in.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>. tools\package-agent.ps1 does.
#endif
#ifndef NativeDir
  #error Pass /DNativeDir=<folder holding x64\ArmoryBadges.dll and arm64\ArmoryBadges.dll>. tools\package-agent.ps1 does.
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#if FileExists(NativeDir + "\arm64\ArmoryBadges.dll")
  #define HasArm64
#endif
; The doubled brace is Inno's escape for a literal "{".
#define AppId "{{EA89842F-F4B2-4F97-8FB3-B90F36C40D3C}"
; The four handlers. The key names sort with one leading space, like OneDrive's, and in this
; order, so on a crowded computer Synced is the first of ours Windows leaves out and Attention
; the last. BadgeHealth.Badges and tools\check-overlays.ps1 list the same four.
#define AttentionKey " IDEAArmory1Attention"
#define AttentionClsid "{{E26E19F2-515F-472F-AD4F-1B0293728CE2}"
#define MineKey " IDEAArmory2Mine"
#define MineClsid "{{DB040D16-C118-4CDA-B616-DF9340A8BC9F}"
#define LockedKey " IDEAArmory3Locked"
#define LockedClsid "{{DF50E3A9-57B8-44AE-B690-257AFF283F97}"
#define SyncedKey " IDEAArmory4Synced"
#define SyncedClsid "{{58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0}"
#define Dll "{app}\" + AppVersion + "\ArmoryBadges.dll"
#define Overlays "Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers"
#define Approved "Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"

[Setup]
AppId={#AppId}
AppName=IDEA Armory badges
AppVersion={#AppVersion}
AppVerName=IDEA Armory badges {#AppVersion}
AppPublisher=IDEA, Don Bosco Tech
AppPublisherURL=https://ideabosco.com
AppSupportURL=https://ideabosco.com
AppCopyright=IDEA, Don Bosco Tech
VersionInfoVersion={#AppVersion}
VersionInfoCompany=IDEA, Don Bosco Tech
VersionInfoProductName=IDEA Armory
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=IDEA Armory badges Setup
; For the whole computer, so an administrator, and never anything else.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
DefaultDirName={commonpf64}\IDEA Armory Badges
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
#ifdef HasArm64
; x64compatible includes Windows 11 on ARM, which gets the ARM64 DLL (its Explorer is ARM64).
ArchitecturesAllowed=x64compatible
#else
; No ARM64 DLL in this build: x64 Windows only.
ArchitecturesAllowed=x64os
#endif
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=IDEA-Armory-Badges-Setup-v{#AppVersion}
#ifdef IconFile
SetupIconFile={#IconFile}
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=IDEA Armory badges (status on file icons)
SetupMutex=IDEA-Armory-Badges-Setup
; Explorer keeps the old DLL until each person signs out; it is never closed or restarted.
CloseApplications=no
RestartApplications=no
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabelNoIcons=Armory's status shows on file icons after each person signs out of Windows and back in.%n%nEach person also needs IDEA Armory itself, which installs without an administrator.
ConfirmUninstall=Remove Armory's status from file icons for everyone on this computer?%n%nIDEA Armory itself, the vault folder and every file in it stay as they are.
UninstalledAll=Armory's status no longer shows on file icons. Windows lets go of the last file when each person signs out.

[Files]
#ifdef HasArm64
Source: "{#NativeDir}\x64\ArmoryBadges.dll"; DestDir: "{app}\{#AppVersion}"; Check: not IsArm64; Flags: uninsrestartdelete
Source: "{#NativeDir}\arm64\ArmoryBadges.dll"; DestDir: "{app}\{#AppVersion}"; Check: IsArm64; Flags: uninsrestartdelete
#else
Source: "{#NativeDir}\x64\ArmoryBadges.dll"; DestDir: "{app}\{#AppVersion}"; Flags: uninsrestartdelete
#endif

[Registry]
; Each handler: its COM class (InprocServer32, Apartment), its overlay identifier, and its
; approval. Explorer reads only HKLM for overlays.
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AttentionClsid}"; ValueType: string; ValueData: "IDEA Armory badge: needs attention"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AttentionClsid}\InprocServer32"; ValueType: string; ValueData: "{#Dll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AttentionClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "{#Overlays}\{#AttentionKey}"; ValueType: string; ValueData: "{#AttentionClsid}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#Approved}"; ValueType: string; ValueName: "{#AttentionClsid}"; ValueData: "IDEA Armory badge: needs attention"; Flags: uninsdeletevalue

Root: HKLM; Subkey: "Software\Classes\CLSID\{#MineClsid}"; ValueType: string; ValueData: "IDEA Armory badge: checked out by you"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{#MineClsid}\InprocServer32"; ValueType: string; ValueData: "{#Dll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#MineClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "{#Overlays}\{#MineKey}"; ValueType: string; ValueData: "{#MineClsid}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#Approved}"; ValueType: string; ValueName: "{#MineClsid}"; ValueData: "IDEA Armory badge: checked out by you"; Flags: uninsdeletevalue

Root: HKLM; Subkey: "Software\Classes\CLSID\{#LockedClsid}"; ValueType: string; ValueData: "IDEA Armory badge: checked out by someone else"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{#LockedClsid}\InprocServer32"; ValueType: string; ValueData: "{#Dll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#LockedClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "{#Overlays}\{#LockedKey}"; ValueType: string; ValueData: "{#LockedClsid}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#Approved}"; ValueType: string; ValueName: "{#LockedClsid}"; ValueData: "IDEA Armory badge: checked out by someone else"; Flags: uninsdeletevalue

Root: HKLM; Subkey: "Software\Classes\CLSID\{#SyncedClsid}"; ValueType: string; ValueData: "IDEA Armory badge: synced"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{#SyncedClsid}\InprocServer32"; ValueType: string; ValueData: "{#Dll}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{#SyncedClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"
Root: HKLM; Subkey: "{#Overlays}\{#SyncedKey}"; ValueType: string; ValueData: "{#SyncedClsid}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "{#Approved}"; ValueType: string; ValueName: "{#SyncedClsid}"; ValueData: "IDEA Armory badge: synced"; Flags: uninsdeletevalue

; What Armory's Settings reads (BadgeHealth): the version, the table format this DLL reads, and
; when it was installed (FILETIME, UTC), to tell "after you sign out and back in" from "loaded".
Root: HKLM; Subkey: "Software\IDEA Armory"; Flags: uninsdeletekeyifempty
Root: HKLM; Subkey: "Software\IDEA Armory\Badges"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\IDEA Armory\Badges"; ValueType: string; ValueName: "Format"; ValueData: "1"
Root: HKLM; Subkey: "Software\IDEA Armory\Badges"; ValueType: qword; ValueName: "InstalledAt"; ValueData: "{code:InstalledAtFileTime}"

[UninstallDelete]
Type: dirifempty; Name: "{app}\{#AppVersion}"
Type: dirifempty; Name: "{app}"

[Code]
procedure GetSystemTimeAsFileTime(var Value: TFileTime);
  external 'GetSystemTimeAsFileTime@kernel32.dll stdcall';

// The current time as a FILETIME (UTC), in decimal, for the qword InstalledAt.
function InstalledAtFileTime(Param: String): String;
var
  Moment: TFileTime;
  Value: Int64;
begin
  GetSystemTimeAsFileTime(Moment);
  Value := Moment.dwHighDateTime;
  Value := (Value shl 32) or Moment.dwLowDateTime;
  Result := IntToStr(Value);
end;

// Deletes a folder now, or when Windows next starts when Explorer still has its DLL loaded
// (an administrator's setup may queue that).
procedure RemoveFolder(Folder: String);
var
  Find: TFindRec;
begin
  if DelTree(Folder, True, True, True) then
  begin
    Log('Removed the older badges in ' + Folder);
    exit;
  end;
  if FindFirst(Folder + '\*', Find) then
  try
    repeat
      if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        RestartReplace(Folder + '\' + Find.Name, '');
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  RestartReplace(Folder, '');
  Log('An older badges DLL is still loaded; ' + Folder + ' is removed when Windows next starts.');
end;

// Every version folder but this one.
procedure RemoveOlderVersions();
var
  Find: TFindRec;
  Base: String;
begin
  Base := ExpandConstant('{app}');
  if FindFirst(Base + '\*', Find) then
  try
    repeat
      if ((Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Find.Name <> '.') and (Find.Name <> '..') and
         (CompareText(Find.Name, '{#AppVersion}') <> 0) then
        RemoveFolder(Base + '\' + Find.Name);
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RemoveOlderVersions();
end;

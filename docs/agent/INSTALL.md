# Installing the IDEA Armory agent

The agent is `IdeaArmory.exe` (product "IDEA Armory", publisher "IDEA, Don Bosco Tech"). It
runs in the tray and syncs the vault folder, `C:\IDEA\Armory` by default. Both installers
put it in the profile of the Windows account that runs them and never ask for an
administrator password. Lab computers run Windows 10 with SolidWorks 2025; students' own
laptops run Windows 10 or 11 with SolidWorks 2026. Installing needs no internet. Everything
0.3.3 adds, what each piece costs (nothing) and the one optional step that needs an
administrator are listed in docs/agent/dependencies-0.3.3.md.

## What gets installed, and where

Everything in this table is per user and needs no administrator, on both routes.

| What | Where |
|---|---|
| The app (self-contained .NET, x64) | `%LOCALAPPDATA%\Programs\IDEA Armory\` with `IdeaArmory.exe`, `ArmoryShell.exe` (what File Explorer's right-click items run, 0.3.3), `Assets\armory.ico`, `wwwroot\`, `scripts\` (Setup.ps1, Uninstall.cmd, Check.cmd, payload.sha256) and `badges\IDEA-Armory-Badges-Setup.exe` (the optional badges setup, carried so Settings' Turn on and the drive can run it; carrying it installs nothing) |
| Start menu shortcut, this account only | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\IDEA Armory.lnk`, with `System.AppUserModel.ID` = `IdeaBosco.Armory` (0.3.3; setup.exe sets it, and Armory sets it at start on the flash drive's shortcut) |
| Notifications and links (0.3.3), written by both installers and again by the app at start | `HKCU\Software\Classes\AppUserModelId\IdeaBosco.Armory` and `HKCU\Software\Classes\idea-armory` (below) |
| File Explorer's right-click items, written by the app when it starts (never by an installer) | `HKCU\Software\Classes`: `AllFilesystemObjects\shell\IDEAArmory`, `IDEAArmory.Menu`, `Directory\Background\shell\IDEAArmory`, `IDEAArmory.BackgroundMenu` |
| The badges' heartbeat, only when the optional badges are installed on the computer | `HKCU\Software\IDEA Armory\Badges` (`Seen<Badge>`, `ExplorerPid`), written by File Explorer's badge handlers so Settings can tell whether Windows shows them |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `IDEA Armory` = `"<exe>" --background` |
| Apps entry (Settings > Apps) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory` (flash drive) or `...\Uninstall\{28A1D010-82E3-4294-9676-83AC0AA1F5D3}_is1` (setup.exe), with DisplayName, Publisher, DisplayVersion, DisplayIcon, UninstallString, QuietUninstallString, NoModify, NoRepair and EstimatedSize |
| Per-account data, written by the app | `%LOCALAPPDATA%\IDEA Armory\`: `settings.json`, `logs\agent.log`, `secrets\` (this computer's sign-in, protected with Windows DPAPI), `WebView2\` |
| Students' profiles, only on a computer shared by several students (0.3.3, docs/agent/PROFILES.md) | `%LOCALAPPDATA%\IDEA Armory\profiles\`: `profiles.json` (who is on the computer; no sign-in or PIN in it) and one folder per student with their sign-in and PIN (DPAPI); never made while the setting is off |
| The vault, created and synced by the app | `C:\IDEA\Armory\` (or `vaultRoot` in settings.json), with one folder per project and the agent's hidden `.armory\` folder (journal, saved copies, sync state) |

Nothing goes under `Program Files`, `HKLM` or another account's profile (the optional
badges step below is the one exception, and it is separate: its own setup, its own
administrator password, its own Apps entry). The app needs no
.NET install. Its window needs the Microsoft Edge WebView2 Runtime, which Windows 11
includes and nearly every Windows 10 computer already has. Both installers check
`pv` under `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
and the same key under `HKCU`, and report the version. When it is missing, the flash drive
install ends on FAIL with the download page, and setup.exe names it on its Ready page and
in a message after installing; the runtime installs from https://developer.microsoft.com/microsoft-edge/webview2/consumer/
without an administrator password.

The SolidWorks link (0.3.3, docs/agent/SOLIDWORKS.md) needs nothing installed or registered:
it is part of `IdeaArmory.exe`, which finds this Windows account's running SolidWorks and talks
to it from outside. No add-in DLL is loaded into SolidWorks, no COM class is registered, and
neither installer nor Armory writes anything under `HKCU\Software\SolidWorks` or
`HKLM\SOFTWARE\SolidWorks` (the one SolidWorks setting Armory changes, the student's Save to
Version option while saving a team file in an older year, it changes through SolidWorks and puts
back; docs/agent/SOLIDWORKS.md). CI checks the registry after every install, upgrade and
uninstall. Upgrading or uninstalling with SolidWorks open is the same as without it: Armory lets
go of SolidWorks when it quits, and SolidWorks keeps running.

`IdeaArmory.exe` takes three flags, and a link. `--background` starts in the tray without opening the
window (the sign-in entry uses it). `--quit` asks the running copy to exit cleanly and
waits up to 30 seconds. `--check` opens nothing, prints one JSON line
`{"version","webView2Runtime","wwwroot","vaultRoot"}`, and exits 0 when the app files and
the WebView2 Runtime are present, 1 otherwise. `"idea-armory:act?t=<token>&a=<checkout|show|savein|keeplocal>"`
is what Windows passes when a notification's button is clicked (docs/agent/EXPLORER.md section
7): with Armory running, it is handed over and the launch exits 0 (1 when the running copy can't
be reached, after bringing its window up); with none running, Armory starts with its window
open. The app writes `started <version>` to `agent.log` when it starts and `stopped` when it
exits cleanly.

Two environment variables exist for automated tests only. `ARMORY_DATA_DIR` (an absolute
folder) replaces `%LOCALAPPDATA%\IDEA Armory`, gives the single-instance guard its own
name, and stops the app from touching the Run value. `ARMORY_SITE_URL` replaces
`https://ideabosco.com`. Neither installer sets them. (The shell and SolidWorks pieces have
their own test-only names: `ARMORY_SHELL_PIPE` and `ARMORY_BADGES_SECTION` in
docs/agent/EXPLORER.md, and `ARMORY_SOLIDWORKS_PROCESS`, which the install cycle's SolidWorks
run sets for the installed Armory, in docs/agent/SOLIDWORKS.md.)

## Lab computers: the flash drive

Use `IDEA-Armory-USB-v<version>.zip` from the release. Its `README.txt` is the one-page
instruction for someone who has never done this. In short:

1. Copy the zip to the flash drive, right-click it, Extract All. The folder holds
   `Install IDEA Armory.cmd`, `Uninstall IDEA Armory.cmd`, `Check IDEA Armory.cmd`,
   `Show Armory status on file icons.cmd` (optional, below), `README.txt`, `files\` (the app
   and `files\scripts\Setup.ps1`, which does the work) and `logs\`.
2. On each computer, signed in as the Windows account that will use Armory, double-click
   `Install IDEA Armory.cmd`. There is no administrator prompt. Do not use "Run as
   administrator": setup refuses to run as a different account from the one signed in,
   because it would install for the wrong person.
3. Wait for the large green PASS line, about a minute. A red FAIL line names the one thing
   to fix; fix it and run Install again.
4. The drive's `logs\<COMPUTERNAME>.txt` gets one line per run: date, mode, PASS or FAIL,
   version, Windows account, Windows build, seconds, and the reason.

Install copies `files\` beside the old program folder and checks every file's SHA-256
against `files\scripts\payload.sha256` (a worn flash drive shows up here, before anything
changes). Then it closes a running copy, swaps the folders, writes the shortcut, the
notification and link registration (below), the Run value and the Apps entry, runs
`IdeaArmory.exe --check`, and starts the app with `--background`; the app then writes File
Explorer's right-click items and gives the shortcut its AppUserModelID. Running it again is safe
and upgrades in place. `Install IDEA Armory.cmd /quiet` skips the closing pause, for
scripts and CI.

`Check IDEA Armory.cmd` changes nothing. It reports the installed version and the one on
the drive, the Run value, the Apps entry, the shortcut, the WebView2 Runtime, the vault
folder, whether the app is running, "Notifications" and "Link scheme" (registered or MISSING),
"Right-click items" (present inside which vault, not written yet, or pointing somewhere else),
"File icons" (the optional badges: not installed, installed with their version and where they
stand among Windows' overlay handlers, or BROKEN), and the output of `IdeaArmory.exe --check`,
and it re-hashes the installed files. It ends on PASS only when everything is in place; the
right-click items and the file icons are reported, never a reason to fail (the app writes the
first when it starts, and the second are optional).

## A student's own laptop: setup.exe

`IDEA-Armory-Setup-v<version>.exe` is an Inno Setup installer with
`PrivilegesRequired=lowest`, no privilege override, and the fixed folder
`%LOCALAPPDATA%\Programs\IDEA Armory` (no folder page; any other `/DIR=` is refused).
Before installing it shows one page: the account it installs for, the folder, start at
sign-in and the WebView2 Runtime (and, when the computer has no badges, that the last page
offers them). It installs the same `files\` folder as the flash drive, creates the same shortcut
(with `AppUserModelID: "IdeaBosco.Armory"` in `[Icons]`), the notification and link
registration and the Run value, starts the app in the tray, and offers "Open IDEA Armory now"
on the last page. Silent install: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

When `HKLM\SOFTWARE\IDEA Armory\Badges` (64-bit view) has no `Version`, the last page also offers,
unchecked, "Also show Armory's status on file icons (asks once for an administrator password
for this computer)". Ticked, it runs `{app}\badges\IDEA-Armory-Badges-Setup.exe` with the
`runas` verb after Finish: Windows asks for an administrator's password and the badges setup
shows its own pages. Saying no to the password prompt changes nothing (Inno Setup then shows
Windows' "The operation was canceled by the user"). A silent install never offers it. Setup
itself stays per user and never writes `HKLM`.

The release binaries are not code-signed yet, so Windows SmartScreen may say "Windows
protected your PC" for a downloaded setup.exe: click More info, then Run anyway. The flash
drive install unblocks every file it copies, so the installed app starts without that
warning.

The two installers own the same folder. Whichever ran last owns the single Apps entry: the
flash drive install removes setup.exe's entry, and setup.exe removes the flash drive's.

## What start at sign-in does

The Run value starts `IdeaArmory.exe --background` each time that Windows account signs in:
the tray icon appears, no window opens, and syncing follows the contract's schedule. A
second launch (the Start menu shortcut) opens the window of the running copy. The setting
"Start at sign-in" in Armory's settings sheet adds or removes the same value. Reinstalling
respects a person's choice: when `settings.json` says `"startAtSignIn": false`, neither
installer writes the value again.

## What uninstall removes and keeps

`Uninstall IDEA Armory.cmd` on the drive, Settings > Apps > IDEA Armory > Uninstall, and
setup.exe's `unins000.exe` all do the same:

1. Close the app: `IdeaArmory.exe --quit`, a bounded wait, and only then `Stop-Process` for
   processes started from that exact `IdeaArmory.exe` path. Copies run by other Windows
   accounts live under their own profile and are never touched.
2. Remove the program folder, the Start menu shortcut, the Run value, the Apps entry,
   `%LOCALAPPDATA%\IDEA Armory` (settings, logs, this computer's sign-in, WebView2 cache), and
   every per-user key Armory writes: `HKCU\Software\Classes\AppUserModelId\IdeaBosco.Armory`,
   `HKCU\Software\Classes\idea-armory`, the four right-click keys
   (`HKCU\Software\Classes\AllFilesystemObjects\shell\IDEAArmory`, `IDEAArmory.Menu`,
   `Directory\Background\shell\IDEAArmory`, `IDEAArmory.BackgroundMenu`) and
   `HKCU\Software\IDEA Armory`; then tell File Explorer once (`SHCNE_ASSOCCHANGED`), so the
   right-click items go at once. Setup.ps1 does this for the flash drive (Uninstall) and for
   setup.exe's uninstaller (InnoUninstall), and setup.exe's own uninstall log removes the same
   keys after it (`uninsdeletekey`, `ChangesAssociations=yes`). The computer is signed out;
   installing again needs Connect again.
3. Keep the vault folder (`C:\IDEA\Armory`, and the `vaultRoot` in settings.json when it
   differs) and everything in it, including `.armory\`. Uninstall says so on screen.
4. Never touch `HKLM` or another account: the optional badges stay for the computer's other
   accounts until someone removes "IDEA Armory badges (status on file icons)" from Settings >
   Apps, which needs an administrator. With no Armory running for a person, the badges show
   that person nothing.

The deletion code enforces this rather than trusting the paths: it refuses any folder that
is a vault, is inside one, or holds an `.armory` folder, and it removes a junction or
symbolic link itself without following it. Saves that were not yet sent stay in the vault
as ordinary files and in `.armory\`; after a reinstall, the same Armory account picks them
up. Locks this computer held stay on the server until released or broken, so close
SolidWorks and let Armory finish sending before uninstalling.

## Shared lab computers: one run per Windows account

The install is per Windows account, so:

- A lab computer where everyone signs in with one shared account needs one run.
- A computer where students sign in with their own accounts needs one run for each account
  that will use Armory, signed in as that account. Each gets its own program copy, settings
  and sign-in, and each can uninstall only its own.
- The vault folder `C:\IDEA\Armory` is shared by every account on that computer. Its
  `.armory` state is bound to the first Armory account that syncs into it. The agent
  refuses to sync a vault bound to another Armory account and shows "this vault belongs to
  someone else" instead; that person can choose a different vault folder in Settings.
- Uninstalling for one account never touches another account's install or the shared vault.

## Optional: Armory's status on file icons (one administrator step)

Armory's right-click items in File Explorer need nothing: the app writes them itself under
`HKCU\Software\Classes` (`AllFilesystemObjects\shell\IDEAArmory`, `IDEAArmory.Menu`,
`Directory\Background\shell\IDEAArmory`, `IDEAArmory.BackgroundMenu`) when it starts, and each
item runs `ArmoryShell.exe` from the app folder. The badges on file icons are optional and need
an administrator once per computer, because Windows reads icon overlay handlers only from `HKLM`.
This is the only step of 0.3.3 that needs an administrator, on any computer; skipping it changes
nothing else.

- **What it installs.** `IDEA-Armory-Badges-Setup-v<version>.exe` (`installer/IdeaArmoryBadges.iss`,
  `PrivilegesRequired=admin`) puts `ArmoryBadges.dll` into
  `C:\Program Files\IDEA Armory Badges\<version>\` and registers its four handlers for every
  account on the computer: four classes under `HKLM\SOFTWARE\Classes\CLSID` (InprocServer32,
  Apartment), four keys under `...\Explorer\ShellIconOverlayIdentifiers`, their
  `Shell Extensions\Approved` values, and `HKLM\SOFTWARE\IDEA Armory\Badges` (`Version`, `Format`
  = `1`, `InstalledAt`). It carries the ARM64 DLL for Windows on ARM when the build had the ARM64
  C++ tools (`tools/build-native.ps1` warns otherwise, and that setup installs on x64 Windows only).
  It never closes or restarts Explorer: each person sees the badges after signing out of Windows
  and back in.
- **On the flash drive.** `Show Armory status on file icons.cmd` runs `Setup.ps1 -Mode Badges`, which
  starts `files\badges\IDEA-Armory-Badges-Setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART` as an
  administrator (Windows asks for the password), waits, checks `HKLM`, and ends on PASS or FAIL
  with one line in the drive's log, like the other three files. Saying no to the password prompt
  is a FAIL: "Nothing changed: an administrator's password is needed for this one step." README.txt
  section G says the same for whoever holds the drive.
- **With setup.exe.** The last page's unchecked "Also show Armory's status on file icons (asks
  once for an administrator password for this computer)", shown only while the computer has no
  badges, runs the same file from `{app}\badges\` with the `runas` verb.
- **From Armory.** Settings > "Status on file icons" > Turn on runs the installed
  `<app>\badges\IDEA-Armory-Badges-Setup.exe` the same way (docs/agent/EXPLORER.md 2.6), so both
  installers ship it there.
- **For IT, silently, once per computer.** From the release, or from any installed Armory's
  `badges\` folder, or the drive's `files\badges\`:
  `IDEA-Armory-Badges-Setup-v<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (exit code 0
  when done; add `/LOG="<file>"` to keep its log). A copy IT downloaded itself avoids running an
  elevated file from a folder students can write (docs/agent/EXPLORER.md 2.5).
- **Removal.** It has its own Apps entry, "IDEA Armory badges (status on file icons)", which needs an
  administrator; silently: `"C:\Program Files\IDEA Armory Badges\unins000.exe" /VERYSILENT
  /SUPPRESSMSGBOXES /NORESTART`. It removes every key above, the files and its Apps entry.
  Uninstalling Armory for one account never removes it; without Armory running for a person,
  nothing shows for them.

**The per-computer lab check.** On each lab computer model, signed in as a student, run

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-overlays.ps1
```

It changes nothing and needs no administrator. It lists every icon overlay handler in the order
Windows reads them, marks the 11 Windows shows and Armory's four, prints the badges setup's
version and install time and whether this sign-in's Explorer has loaded each badge, and ends with
one verdict in Settings' words (on, after you sign out and back in, partial or crowded because
other apps' badges come first, broken, or off). Keep its output with the lab notes
(docs/agent/EXPLORER.md section 5, lab check L3).

## Notifications and links (0.3.3): what the installers write and remove

Windows names Armory's notifications by its AppUserModelID, `IdeaBosco.Armory`, and opens their
buttons by the `idea-armory:` scheme. Both installers write these per-user values (all REG_SZ;
`<app>` is `%LOCALAPPDATA%\Programs\IDEA Armory`, `{app}` in Inno), and the installed
`IdeaArmory.exe` writes them again at start when one differs, so a missing value repairs
itself the next time Armory starts.

| Key (under `HKCU\Software\Classes`) | Value | Data |
|---|---|---|
| `AppUserModelId\IdeaBosco.Armory` | `DisplayName` | `IDEA Armory` |
| `AppUserModelId\IdeaBosco.Armory` | `IconUri` | `<app>\Assets\armory.ico` (shipped in the payload since 0.3.3) |
| `idea-armory` | (Default) | `URL:IDEA Armory` |
| `idea-armory` | `URL Protocol` | empty string |
| `idea-armory\DefaultIcon` | (Default) | `"<app>\IdeaArmory.exe",0` |
| `idea-armory\shell\open\command` | (Default) | `"<app>\IdeaArmory.exe" "%1"` |

- **setup.exe** (`installer/IdeaArmory.iss`): `[Registry]` entries for exactly these values, each
  with `uninsdeletekey` (`Software\Classes\AppUserModelId\IdeaBosco.Armory`,
  `Software\Classes\idea-armory` and its two subkeys); the `[Icons]` line of the Start menu
  shortcut carries `AppUserModelID: "IdeaBosco.Armory"`. The keys Armory writes itself (the four
  right-click keys and `HKCU\Software\IDEA Armory`) are `[Registry]` entries with
  `uninsdeletekey dontcreatekey`: setup never creates them, and its uninstaller removes them.
  `ChangesAssociations=yes` tells Explorer after installing and after uninstalling.
- **Flash drive** (`installer/scripts/Setup.ps1`): Install writes the same values when one
  differs (through .NET's registry calls, `REG_SZ`) and then tells Explorer; `WScript.Shell`
  cannot set the shortcut's AppUserModelID, and Armory sets it at start when the shortcut points
  to the installed `IdeaArmory.exe` (Install already starts Armory). Check reports
  "Notifications: registered" or "MISSING" and "Link scheme: registered" or "MISSING", and a
  missing or different value of this version's install is one of its problems.
- **Uninstall, both routes** (Setup.ps1 Uninstall and InnoUninstall, and the Inno uninstaller):
  delete `HKCU\Software\Classes\AppUserModelId\IdeaBosco.Armory`, `HKCU\Software\Classes\idea-armory`,
  the four right-click keys, and `HKCU\Software\IDEA Armory` (the badges' heartbeat), then
  send `SHCNE_ASSOCCHANGED` once. A key that will not go fails the uninstall with its name.
  `--quit` removes none of them.
- `tools/package-agent.ps1` checks that one AppUserModelID is in `ShellIdentity.cs`,
  `IdeaArmory.iss` (and on its shortcut) and `Setup.ps1`, as it does for the AppId, that the
  payload holds `Assets\armory.ico`, and that `IdeaArmory.iss` writes nothing under `HKLM`.
- `tools/test-agent-install.ps1`, after each install route: every value above exact, the
  shortcut's `System.AppUserModel.ID` is `IdeaBosco.Armory` (after Armory has started), and the
  scheme's registered command run with `idea-armory:act?t=bogus&a=show` while Armory runs exits 0,
  is logged by the running Armory, and leaves one `IdeaArmory.exe`; after uninstall, every key gone.

Since 0.3.3 a computer where several students share one Windows account can be set up for
them: Settings > Shared computer > "This computer is shared by several students". Each student
then adds themselves once (their own Google sign-in and a 4-digit PIN) and picks their name
when they sit down; they take turns in the one Armory folder. Nothing about the install
changes: still one run for the shared account. Uninstalling removes `profiles\` with the rest
of `%LOCALAPPDATA%\IDEA Armory` and keeps every Armory folder. See docs/agent/PROFILES.md.

## Artifact names

| File | What it is |
|---|---|
| `IDEA-Armory-USB-v<version>.zip` | The flash-drive folder, laid out as it sits on the drive after Extract All |
| `IDEA-Armory-USB-v<version>.zip.sha256` | Its SHA-256, in `sha256sum` format |
| `IDEA-Armory-Setup-v<version>.exe` | The normal per-user installer |
| `IDEA-Armory-Setup-v<version>.exe.sha256` | Its SHA-256, in `sha256sum` format |
| `IDEA-Armory-Badges-Setup-v<version>.exe` | The optional badges setup, for the whole computer (administrator once); both payloads carry the same file as `badges\IDEA-Armory-Badges-Setup.exe` |
| `IDEA-Armory-Badges-Setup-v<version>.exe.sha256` | Its SHA-256, in `sha256sum` format |

`tools/package-agent.ps1` builds all six into `dist\` from the publish folder
(`dotnet publish src/Armory.Agent -c Release -r win-x64 --self-contained true -o publish/agent`)
and the native build (`tools/build-native.ps1`, into `publish/native`; `-NativeDir` names
another). It checks the publish is self-contained with the right product, company and version;
that `ArmoryShell.exe`, `ArmoryBadges.dll` (x64, and ARM64 when built), the badges setup,
setup.exe and the payload's copies carry the product "IDEA Armory", the company and this version
in their version resources, as `IdeaArmory.exe` does; that no CAD file, SolidWorks DLL
(`SolidWorks.Interop*`), `settings.json` or native test tool (`BadgeProbe.exe`,
`ShellPipeTest.exe`) ships, in the publish folder or anywhere in the payload; that the payload
holds `IdeaArmory.exe`, `ArmoryShell.exe`, `Assets\armory.ico` and the badges setup; that the
installer sources agree (the AppId, the AppUserModelID, the per-user lines, no `HKLM` in
`IdeaArmory.iss`, the badges setup admin-only); and that every shipped text file is plain ASCII
with CRLF line endings. The badges setup is built first, because the payload, and so both the
zip and setup.exe, carries it. `-NoSetupExe` builds only the zip, without either setup and so
without `badges\`, on a computer without Inno Setup 6.3 or later (such a zip is for checks and
never ships). Sources: `installer/usb/` (the drive's top level), `installer/scripts/`
(Setup.ps1 and the installed Uninstall.cmd and Check.cmd), `installer/IdeaArmory.iss` and
`installer/IdeaArmoryBadges.iss`.

## How CI proves the cycle

`.github/workflows/agent.yml` runs on every push and pull request to main and on demand, on
`windows-latest`, with no secret; the runner's steps run as an administrator. The shared steps
live in `.github/actions/package-agent/action.yml`: `dotnet restore --locked-mode`,
`dotnet build -warnaserror`, the native parts with Visual Studio's compilers
(`tools/build-native.ps1`: `ArmoryBadges.dll` for x64 and, when the image has the ARM64 C++ tools,
ARM64; `ArmoryShell.exe`; the test tools `BadgeProbe.exe` and `ShellPipeTest.exe`; warnings are
errors, and imports and version resources are checked), `dotnet test tests/Armory.Agent.Tests`
(whose shell and pipe tests use that native build), the self-contained publish, Inno Setup
(installed with Chocolatey only when the runner lacks 6.3 or later), and
`tools/package-agent.ps1`. agent.yml then runs `tests/Armory.Platform.Windows.Tests` (real
NTFS, Restart Manager, DPAPI, child processes and the badge handlers, which skip on Linux) and
every other test on Windows (the right-click items in shell32's own menu, the forwarders and the
pipe, and the SolidWorks link against its test fake), so dispatching it on a branch
(`gh workflow run agent.yml --ref <branch>`) covers every Windows-only test. Then
`tools/test-agent-install.ps1` runs these cycles (installing the WebView2 Runtime on the
runner first if the image lacks it):

- **Flash drive.** Extract the zip and check its top level (the four .cmd files, README.txt,
  `files\`, `logs\`) and that `files\` holds `ArmoryShell.exe` and
  `badges\IDEA-Armory-Badges-Setup.exe` with this version's resources and no test tool or
  SolidWorks DLL. `Install IDEA Armory.cmd /quiet`
  exits 0; the exe has the right version, the Run value is exact, the Apps entry has every
  required value, the shortcut exists, `IdeaArmory.exe --check` exits 0 with the right JSON,
  the process runs from the installed path, and `agent.log` says `started <version>`. Then this
  version's wiring: the notification registration and the `idea-armory:` scheme with their exact
  values; the right-click items exactly as `ShellVerbs` lays them out, with this install's
  `ArmoryShell.exe` and `C:\IDEA\Armory` in `AppliesTo` (and no Force check in, since no project
  allows one); the shortcut's `System.AppUserModel.ID`; the program folder's payload; no
  SolidWorks registry footprint (nothing new under `HKCU\Software\SolidWorks`,
  `HKLM\SOFTWARE\SolidWorks` unchanged in both registry views, no badge class or class naming
  Armory under `HKCU\Software\Classes\CLSID`). The scheme's registered command, run with
  `idea-armory:act?t=bogus&a=show`, exits 0, the running Armory logs `shell: uri, 1 item`, and no
  second `IdeaArmory.exe` stays. Then
  `C:\IDEA\Armory\Proof\keep.txt` and a nested random file are written. Install again: exit
  0, the app closed cleanly after `--quit`, everything above still holds, and still running.
  Check exits 0 and reports the registration, the link scheme, the right-click items and the
  file icons. `Uninstall IDEA Armory.cmd /quiet` exits 0 and names the kept vault on
  screen; the program folder, Run value, Apps entry, shortcut, data folder, every per-user key
  and the process are gone, there is still no SolidWorks footprint, and both proof files have
  the same SHA-256. Check now exits non-zero. The drive's
  log has exactly five lines with PASS and FAIL where expected.
- **setup.exe.** The same checks with `IDEA-Armory-Setup-v<version>.exe /VERYSILENT
  /SUPPRESSMSGBOXES /NORESTART` run twice, the installed `scripts\Check.cmd`, then
  `unins000.exe /VERYSILENT` (waiting for its TEMP copy to finish), the proof files again,
  and the drive's Check exiting non-zero.
- **Upgrade from a published release** (`-Kind Upgrade`, both routes; `-Route Usb` or
  `-Route Setup` runs one, `-From` names the published version). CI runs it from v0.1.0 and from
  v0.3.2, the release before this one. The flash-drive
  zip and setup.exe of that version are downloaded from
  `https://github.com/pina-hash/idea-armory/releases/download/v<From>/` into `RUNNER_TEMP`
  (never `dist`, where the script picks the build under test) and each is checked against
  its `.sha256`. For each route: install the old version and wait until it runs and logs
  `started <From>` and `vault runtime started at C:\IDEA\Armory`; plant the proof files in
  the vault, a `settings.json` with a non-default theme, a sign-in in the exact
  `DpapiSecretStore` format (`ARMORY-DPAPI-1` and a newline, then a CurrentUser DPAPI blob
  with the entropy `IDEA Armory secret store v1/armory-session`, holding a session whose
  sign-in service answers nothing, so neither version can renew or end it), a read-only
  intent in the 0.1.0 format (`.armory\read-only.json`, `{"Proof/keep.txt":0}`) and a stray
  file in the program's `wwwroot\`; then install this build over it (`Install IDEA
  Armory.cmd /quiet`, which must say `Upgraded IDEA Armory <From> to <version>`, or setup.exe
  `/VERYSILENT`). It passes only when this build runs (exe version, `--check`,
  `started <version>`, a new process still running 15 seconds later), the Apps entry shows
  the new version, the sign-in, `settings.json` and the proof files keep every byte, the
  sign-in still decrypts for this Windows account, the new version's own log says
  `session loaded for upgrade.test@example.com` (it read that sign-in itself) and
  `vault runtime started at C:\IDEA\Armory` (it opened the old vault), the 0.1.0 intent did
  not make `Proof\keep.txt` (a file the server does not have) read-only, the stray page
  file is gone, and this version's wiring is in place as above. Then it uninstalls and checks
  the per-user keys, the SolidWorks registry and the proof files once more. It does not sign in
  to a real server: that the session still works there is shown by its tokens and device
  being byte for byte what the old version saved. Each source version keeps its own evidence
  (`install-cycle-upgrade-from-<From>.txt`).
- **With SolidWorks open** (`-Kind SolidWorks`). When the solution's build has the SolidWorks
  link's test fake (`tests/Armory.FakeSolidWorks`), the cycle names it to the link with the
  test-only variable `ARMORY_SOLIDWORKS_PROCESS`, installs from the flash drive, starts the fake
  (a stand-in SolidWorks 2025 in the Running Object Table, commands on its standard input, every
  event and reference it sees in `evidence\solidworks-fake-solidworks.txt`), waits for
  `solidworks link attached pid=<pid> revision=33.5.0` in agent.log, and installs again over it.
  It passes only when the fake kept running and answering, every sink the old Armory advised was
  unadvised, the new Armory linked again and holds its sinks, the fake saw no error and no
  `CloseDoc`, and there is no SolidWorks registry footprint; then it uninstalls. Without the fake
  in the build it says so and passes.
- **The optional badges** (`-Kind Badges`, last, because it installs for the whole computer).
  `IDEA-Armory-Badges-Setup-v<version>.exe /VERYSILENT` exits 0; every key and value of
  `installer/IdeaArmoryBadges.iss` has its exact data (the four classes with InprocServer32 and
  `ThreadingModel` = `Apartment`, the four overlay identifiers, the four approvals,
  `HKLM\SOFTWARE\IDEA Armory\Badges` with `Version`, `Format` = `1` and a fresh `InstalledAt`
  REG_QWORD, the Apps entry); the DLL is at the registered path with this version; one version
  folder; nothing waits for a restart. `BadgeProbe.exe --attach --com` (from the native build)
  creates all four handlers through `CoCreateInstance`, each with its icon index, priority and
  the registered DLL; `ExtractIconEx` gives icons 0 to 3; `tools/check-overlays.ps1`, run under
  Windows PowerShell 5.1, lists Armory's four and the setup's version, and its output is kept as
  `evidence\check-overlays.txt`. A second run, through the drive's
  `Show Armory status on file icons.cmd /quiet` (`Setup.ps1 -Mode Badges`), ends on PASS with one
  `BADGES PASS` line in the drive's log and leaves everything exact. The uninstaller
  (`unins000.exe /VERYSILENT`) removes every key, value, file and the Apps entry.

The `agent-dist` artifact holds the zip, both setups and their `.sha256` files;
`agent-evidence` holds every step's output, the Inno logs, `package.txt`,
`check-overlays.txt`, the fake SolidWorks' lines, and every test project's results.

`.github/workflows/release.yml` runs when a person pushes a tag `v*` (no workflow pushes
tags), and also when a release is published, in GitHub's web page or with the API
(`POST repos/pina-hash/idea-armory/releases` with `tag_name` and `target_commitish`), which
creates the tag at that commit. It checks that the tag is `v<Version of src/Armory.Agent>`,
so the version bump must be on that commit first, runs the same build, package and install
cycles (both upgrades, SolidWorks open and the badges included), and then attaches the zip,
setup.exe, the badges setup, their `.sha256` files, and the SHA-256 values in the notes to the
release; the notes say the badges setup is optional and needs an administrator once per
computer (`gh release upload --clobber` and
`gh release edit` when the release exists, `gh release create` otherwise; `GH_TOKEN` from
`github.token`, `contents: write`). A published release with a failed run has no assets
until the run is repeated.

`.github/workflows/ui.yml` runs on ubuntu-latest for a push or pull request to main that
touches `src/Armory.Agent/wwwroot/**` or `tools/agent-ui/**`, and on demand. It installs
Playwright 1.56.1 (the version the window tools were checked with) and its Chromium from npm,
outside the checkout, and runs `node tools/agent-ui/check-ui.mjs` and
`node tools/agent-ui/bbox-diff.mjs`, keeping their output.

## Self-update: not shipped, and the choice it needs

The agent ships without self-update. `pina-hash/idea-armory` is public, so its GitHub
Releases download with no token (the upgrade test above does exactly that). Where the agent
should look for updates is still Mr. Pina's choice:

- **This repository's releases.** The agent would read
  `https://api.github.com/repos/pina-hash/idea-armory/releases/latest` with no token. No
  new secret or release step is needed.
- **An update feed on ideabosco.com**, for example `GET /api/armory/agent/latest` answering
  `{version, url, sha256}`, with the installer in the same R2 storage as the blobs. This
  needs a release step that copies the assets there with the R2 credentials stored as a
  repository secret. Today's workflows need no secret.

Turning it on needs:

1. That choice, made by Mr. Pina.
2. For the feed, the release step that copies the assets.
3. Agent code that checks once a day, downloads `IDEA-Armory-Setup-v<version>.exe` to
   `%LOCALAPPDATA%\IDEA Armory\updates\`, verifies its SHA-256 against the feed (served
   over HTTPS from a host the agent trusts), and runs it with
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Setup already closes the running copy with
   `--quit` and starts the new one in the tray. A code-signing certificate, checked by the
   agent before running the download, would make this much stronger and would also remove
   the SmartScreen warning.

Until then, updating means running the new release's Install from the flash drive (or the
new setup.exe) over the old one; the settings and the sign-in file keep every byte, the new
version loads that sign-in and opens the old vault, as the upgrade cycle above checks on
every run (it does not reach a real server). The window's page is replaced as a whole: setup.exe deletes
`{app}\wwwroot` before copying (`[InstallDelete]`), and the flash drive swaps the whole
program folder. The WebView2 profile in `%LOCALAPPDATA%\IDEA Armory\WebView2` survives an
upgrade, so the page and its scripts load with `?v=<version>`, and the first start of a new
version empties that profile's cache once.

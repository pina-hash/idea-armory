# Installing the IDEA Armory agent

The agent is `IdeaArmory.exe` (product "IDEA Armory", publisher "IDEA, Don Bosco Tech"). It
runs in the tray and syncs the vault folder, `C:\IDEA\Armory` by default. Both installers
put it in the profile of the Windows account that runs them and never ask for an
administrator password. Lab computers run Windows 10 with SolidWorks 2025; students' own
laptops run Windows 10 or 11 with SolidWorks 2026. Installing needs no internet.

## What gets installed, and where

| What | Where |
|---|---|
| The app (self-contained .NET, x64) | `%LOCALAPPDATA%\Programs\IDEA Armory\` with `IdeaArmory.exe`, `wwwroot\` and `scripts\` (Setup.ps1, Uninstall.cmd, Check.cmd, payload.sha256) |
| Start menu shortcut, this account only | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\IDEA Armory.lnk` |
| Start at sign-in | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `IDEA Armory` = `"<exe>" --background` |
| Apps entry (Settings > Apps) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\IDEA Armory` (flash drive) or `...\Uninstall\{28A1D010-82E3-4294-9676-83AC0AA1F5D3}_is1` (setup.exe), with DisplayName, Publisher, DisplayVersion, DisplayIcon, UninstallString, QuietUninstallString, NoModify, NoRepair and EstimatedSize |
| Per-account data, written by the app | `%LOCALAPPDATA%\IDEA Armory\`: `settings.json`, `logs\agent.log`, `secrets\` (this computer's sign-in, protected with Windows DPAPI), `WebView2\` |
| The vault, created and synced by the app | `C:\IDEA\Armory\` (or `vaultRoot` in settings.json), with one folder per project and the agent's hidden `.armory\` folder (journal, saved copies, sync state) |

Nothing goes under `Program Files`, `HKLM` or another account's profile. The app needs no
.NET install. Its window needs the Microsoft Edge WebView2 Runtime, which Windows 11
includes and nearly every Windows 10 computer already has. Both installers check
`pv` under `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
and the same key under `HKCU`, and report the version. When it is missing, the flash drive
install ends on FAIL with the download page, and setup.exe names it on its Ready page and
in a message after installing; the runtime installs from https://developer.microsoft.com/microsoft-edge/webview2/consumer/
without an administrator password.

`IdeaArmory.exe` takes three flags. `--background` starts in the tray without opening the
window (the sign-in entry uses it). `--quit` asks the running copy to exit cleanly and
waits up to 30 seconds. `--check` opens nothing, prints one JSON line
`{"version","webView2Runtime","wwwroot","vaultRoot"}`, and exits 0 when the app files and
the WebView2 Runtime are present, 1 otherwise. The app writes `started <version>` to
`agent.log` when it starts and `stopped` when it exits cleanly.

Two environment variables exist for automated tests only. `ARMORY_DATA_DIR` (an absolute
folder) replaces `%LOCALAPPDATA%\IDEA Armory`, gives the single-instance guard its own
name, and stops the app from touching the Run value. `ARMORY_SITE_URL` replaces
`https://ideabosco.com`. Neither installer sets them.

## Lab computers: the flash drive

Use `IDEA-Armory-USB-v<version>.zip` from the release. Its `README.txt` is the one-page
instruction for someone who has never done this. In short:

1. Copy the zip to the flash drive, right-click it, Extract All. The folder holds
   `Install IDEA Armory.cmd`, `Uninstall IDEA Armory.cmd`, `Check IDEA Armory.cmd`,
   `README.txt`, `files\` (the app and `files\scripts\Setup.ps1`, which does the work) and
   `logs\`.
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
changes). Then it closes a running copy, swaps the folders, writes the shortcut, the Run
value and the Apps entry, runs `IdeaArmory.exe --check`, and starts the app with
`--background`. Running it again is safe
and upgrades in place. `Install IDEA Armory.cmd /quiet` skips the closing pause, for
scripts and CI.

`Check IDEA Armory.cmd` changes nothing. It reports the installed version and the one on
the drive, the Run value, the Apps entry, the shortcut, the WebView2 Runtime, the vault
folder, whether the app is running, and the output of `IdeaArmory.exe --check`, and it
re-hashes the installed files. It ends on PASS only when everything is in place.

## A student's own laptop: setup.exe

`IDEA-Armory-Setup-v<version>.exe` is an Inno Setup installer with
`PrivilegesRequired=lowest`, no privilege override, and the fixed folder
`%LOCALAPPDATA%\Programs\IDEA Armory` (no folder page; any other `/DIR=` is refused).
Before installing it shows one page: the account it installs for, the folder, start at
sign-in and the WebView2 Runtime. It installs the same `files\` folder as the flash drive, creates the same shortcut
and Run value, starts the app in the tray, and offers "Open IDEA Armory now" on the last
page. Silent install: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

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
2. Remove the program folder, the Start menu shortcut, the Run value, the Apps entry, and
   `%LOCALAPPDATA%\IDEA Armory` (settings, logs, this computer's sign-in, WebView2 cache).
   The computer is signed out; installing again needs Connect again.
3. Keep the vault folder (`C:\IDEA\Armory`, and the `vaultRoot` in settings.json when it
   differs) and everything in it, including `.armory\`. Uninstall says so on screen.

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

## Artifact names

| File | What it is |
|---|---|
| `IDEA-Armory-USB-v<version>.zip` | The flash-drive folder, laid out as it sits on the drive after Extract All |
| `IDEA-Armory-USB-v<version>.zip.sha256` | Its SHA-256, in `sha256sum` format |
| `IDEA-Armory-Setup-v<version>.exe` | The normal per-user installer |
| `IDEA-Armory-Setup-v<version>.exe.sha256` | Its SHA-256, in `sha256sum` format |

`tools/package-agent.ps1` builds all four into `dist\` from the publish folder
(`dotnet publish src/Armory.Agent -c Release -r win-x64 --self-contained true -o publish/agent`).
It checks the publish is self-contained with the right product, company and version, that
no CAD file, SolidWorks DLL or `settings.json` ships, and that every shipped text file is
plain ASCII with CRLF line endings. `-NoSetupExe` builds only the zip on a computer without
Inno Setup 6.3 or later. Sources: `installer/usb/` (the drive's top level),
`installer/scripts/` (Setup.ps1 and the installed Uninstall.cmd and Check.cmd) and
`installer/IdeaArmory.iss`.

## How CI proves the cycle

`.github/workflows/agent.yml` runs on every push and pull request to main and on demand, on
`windows-latest`, with no secret. The shared steps live in
`.github/actions/package-agent/action.yml`: `dotnet restore --locked-mode`,
`dotnet build -warnaserror`, `dotnet test tests/Armory.Agent.Tests`, the self-contained
publish, Inno Setup (installed with Chocolatey only when the runner lacks 6.3 or later), and
`tools/package-agent.ps1`. Then `tools/test-agent-install.ps1` runs two cycles (installing
the WebView2 Runtime on the runner first if the image lacks it):

- **Flash drive.** Extract the zip and check its top level. `Install IDEA Armory.cmd /quiet`
  exits 0; the exe has the right version, the Run value is exact, the Apps entry has every
  required value, the shortcut exists, `IdeaArmory.exe --check` exits 0 with the right JSON,
  the process runs from the installed path, and `agent.log` says `started <version>`. Then
  `C:\IDEA\Armory\Proof\keep.txt` and a nested random file are written. Install again: exit
  0, the app closed cleanly after `--quit`, everything above still holds, and still running.
  Check exits 0. `Uninstall IDEA Armory.cmd /quiet` exits 0 and names the kept vault on
  screen; the program folder, Run value, Apps entry, shortcut, data folder and process are
  gone, and both proof files have the same SHA-256. Check now exits non-zero. The drive's
  log has exactly five lines with PASS and FAIL where expected.
- **setup.exe.** The same checks with `IDEA-Armory-Setup-v<version>.exe /VERYSILENT
  /SUPPRESSMSGBOXES /NORESTART` run twice, the installed `scripts\Check.cmd`, then
  `unins000.exe /VERYSILENT` (waiting for its TEMP copy to finish), the proof files again,
  and the drive's Check exiting non-zero.

The `agent-dist` artifact holds the zip, the exe and their `.sha256` files; `agent-evidence`
holds every step's output, the Inno logs, `package.txt` and the agent test results.

`.github/workflows/release.yml` runs when a person pushes a tag `v*` (no workflow pushes
tags). It checks that the tag is `v<Version of src/Armory.Agent>`, runs the same build,
package and both cycles, and then publishes a GitHub Release with both assets, their
`.sha256` files, and the SHA-256 values in the notes (`gh release create`, `GH_TOKEN` from
`github.token`, `contents: write`).

## Self-update: not shipped, and the choice it needs

The agent ships without self-update. `pina-hash/idea-armory` is private, so downloading its
GitHub Releases from a lab computer would need a GitHub token on every computer, which is
not acceptable for student machines. Where updates are published is Mr. Pina's choice:

- **A public releases repository**, for example `pina-hash/idea-armory-releases`, holding
  only the release assets; the code stays private. The agent would read
  `https://api.github.com/repos/pina-hash/idea-armory-releases/releases/latest` with no
  token.
- **An update feed on ideabosco.com**, for example `GET /api/armory/agent/latest` answering
  `{version, url, sha256}`, with the installer in the same R2 storage as the blobs.

Turning it on needs:

1. That choice, made by Mr. Pina.
2. A release step that copies the assets there: a token scoped to the public repository,
   or the R2 credentials, stored as a repository secret. Today's workflows need no secret.
3. Agent code that checks once a day, downloads `IDEA-Armory-Setup-v<version>.exe` to
   `%LOCALAPPDATA%\IDEA Armory\updates\`, verifies its SHA-256 against the feed (served
   over HTTPS from a host the agent trusts), and runs it with
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Setup already closes the running copy with
   `--quit` and starts the new one in the tray. A code-signing certificate, checked by the
   agent before running the download, would make this much stronger and would also remove
   the SmartScreen warning.

Until then, updating means running the new release's Install from the flash drive (or the
new setup.exe) over the old one; settings, sign-in and the vault are kept.

# Install ownership and taskbar identity

Investigated November 3, 2026 for the reported October 29 incident `b1fb2576`.
The incident's actual paths and recorder attachment are not in this repository. The report
motivates this change; the code inspection and Windows smoke are separate evidence.

## Found in the tree and release artifacts

| Route | Location and identity | How another copy can survive |
| --- | --- | --- |
| 0.3.0 through 0.3.3 Inno setup | `%LOCALAPPDATA%\Programs\IDEA Armory`; AppId `{28A1D010-82E3-4294-9676-83AC0AA1F5D3}` | Different location and Apps key from Velopack. These releases have no updater. |
| Flash-drive installer, including 0.4.x | Same program folder; Apps key `IDEA Armory` | Shares the folder with Inno, with a different Apps key. Extracted `files` can also be launched or copied elsewhere. |
| 0.4.x Velopack | `%LOCALAPPDATA%\IDEAArmory\current`; package ID `IDEAArmory` | A second folder and Apps entry alongside the legacy install. A copied root retains its own updater and packages. |
| Unpacked or manually copied release | Any referenced local folder containing the release payload | It may have no Apps entry. Processes, shortcuts, or auto-start commands can still launch it. |
| Machine scope or other registry view | Found through HKLM and both registry views | The shipped agent installers are per-user. Machine scope is a discovery/cleanup case, not a second shipped agent installer. |

Checked the Inno AppId, default directory and `PrivilegesRequired=lowest` in tags `v0.3.0`
through `v0.4.3`. No additional historical Inno AppId was found. The agent's explicit AUMID
is `IdeaBosco.Armory` from 0.3.3; 0.4.0 through 0.4.2 Velopack packaging used its fallback
`velopack.IDEAArmory`. 0.4.3 adds `--aumid IdeaBosco.Armory`. The published 0.4.2 nupkg
also carries that fallback in its manifest and contains the root execution stub payload.

The package script uses one release feed/channel (`win`), `--noPortable`, and no MSI option.
The USB zip is a separate distribution. No agent scheduled-task creation was found. The
machine-wide badges have their own identity and uninstaller and are excluded from retirement.

The previous retire function checked one legacy executable only, ran only in install/update
hooks, deleted the legacy folder recursively, and skipped cleanup if the executable was
already missing. It did not invoke the uninstaller. An older 0.4.x legacy runtime can download
and launch the latest setup; an older runtime can also rewrite the shared Run value and shell
registrations when it starts. A leftover shortcut launches its target; it does not itself
recreate deleted files. Neither a scheduled-task cause nor a machine-scope cause is established
for the supplied incident.

The old pin repair targeted `current\IdeaArmory.exe`. That is a fixed folder name in the
shipped layout. The root `IDEA Armory.exe` stub is the launcher that forwards arguments through
`Update.exe start` to the current app. Velopack 1.2.161 rewrites shortcut properties and extracts
stubs after the update callback, so repairing only inside that callback can be overwritten.

## Built behavior

`InstallRetirement` runs the embedded `installer/scripts/Retire.ps1`. From 0.5.5 to 0.5.7 that
was before the single-instance handoff and could end the launch; since 0.5.8 it runs after the
window (see "0.5.8: never a silent launch" below). Install/update callbacks start an external Windows PowerShell waiter, which survives
Velopack's post-hook termination of processes inside the install root. It waits for its parent
installer/updater to exit, then starts the same reconciliation. An isolated `ARMORY_DATA_DIR` instance and a
development bin/obj build do not participate. A directly launched release carrying its packaged
manifest, assembly and forwarder participates too.

Inventory covers the current account's and machine's uninstall keys and Run/RunOnce values in
both registry views, direct scheduled-task executable actions, user/common Startup, Start menu
and desktop shortcuts, taskbar pins, current-account process paths, and the known install
folders plus `.old`/`.new` siblings. It uses product/company metadata or the remaining release
payload to identify an executable that was already removed. It does not scan whole drives.
Paths are normalized, including 8.3 names, before ownership comparisons.

A per-account mutex serializes inventories across sessions. After acquiring it, a worker checks
its initiating process, process start time and executable again. A worker whose copy was retired
while it waited exits without retiring the winner.

Competing processes are matched by full path. A clean quit is requested when it cannot signal
an already-running survivor; the fallback stops only the identified process. Retired Run values,
Startup/menu/desktop links, task actions and Apps entries are removed. A mixed task keeps its
other actions. Existing taskbar pin files are repointed before an uninstaller can remove them.

Retirement reads settings and profile vault roots to protect them. It protects the data folder,
the default team folder, configured vaults, vault markers, ancestors containing configured vaults,
and reparse points. It never recursively deletes a competing root itself. Unlisted files remain;
manifest cleanup deletes only unchanged, recorded payload bytes. Verified Armory entrypoints
are disabled separately so a retained payload cannot relaunch itself.

The known legacy uninstallers normally erase shared local data. Retirement disables the old
executable's quit hook and temporarily substitutes a no-data PowerShell hook before invoking
the uninstaller. Changed payloads or a cache targeted by recursive uninstall defer that
uninstaller. Velopack's recursive uninstaller runs only when the cached package accounts for
the root's existing files; otherwise its launchers and updater are retired and other files stay.
Manifest cleanup uses `current` for these roots, and cached-package hashes also cover the
bundled `current\Squirrel.exe` and execution stub, so those cannot re-create the retired app.
Slow uninstallers retain the safe hook until they finish. The application waits at most 90
seconds for the script; it does not kill an uninstaller midway through cleanup. Since 0.5.8 the
launch goes on when that wait runs out.

Legacy compiled uninstall records can also remove shared shell keys and local registry fields.
Those fields are preserved, then the survivor's executable/forwarder targets are repaired.
Velopack removes an Apps key by package ID across roots, so the survivor's Apps values are also
restored. No settings/sign-in file is rewritten by retirement.

Every participating runtime uses `IdeaBosco.Armory`, including the window's native property
store and relaunch command. Installed Velopack shortcuts, icons and pins use the root stub;
a flat unpacked release uses its fixed executable. Package checks reject disagreement between
the C# identity, pack ID, title, AUMID and execution-stub name. Startup registration uses the
same launcher and respects `StartAtSignIn=false`. A correct enabled canonical Run entry stays
present while aliases are removed.

Each removal/deferral goes to the retirement audit under `logs`, then to the existing
`FlightRecorder.Note` and scrubbed agent log. Atomic replay handles interrupted records. The
running process also drains late events from a second launch or an update worker.

## 0.5.8: never a silent launch

Reported November 5, 2026 (0.5.7, school lab): "It's just not opening... Fresh install. Doesn't
open. No message, nothing." Found by reading the code; not yet reproduced on a lab computer.

**What made a launch end without a word (0.5.5 to 0.5.7).**

1. `Program.Main` ran `InstallRetirement.Run` before the single-instance guard, the log, the crash
   handlers and the window, and returned 1 when it said false. It said false for any non-zero
   script exit, a 90 second timeout, and an `IOException`, `UnauthorizedAccessException`,
   `Win32Exception` or `InvalidOperationException`. The script exits non-zero when another
   inventory holds `Global\IDEA-Armory-Retire-<SID>` for 60 seconds (exit 1), when this copy was
   retired while it waited (exit 2), and on any error under `$ErrorActionPreference = 'Stop'`:
   `Get-CimInstance Win32_Process -ErrorAction Stop` where WMI is restricted, `Add-Type` under
   Constrained Language Mode, and Windows PowerShell refusing the `.ps1` in `%TEMP%` (an execution
   policy set by Group Policy wins over `-ExecutionPolicy Bypass`; AppLocker and Defender rules for
   scripts in TEMP). Those are all common on school-managed computers, which fits "some computers".
   A fresh install makes it likelier: the install hook's worker runs the same inventory while the
   first launch starts, so one of them waits on the other's lock.
2. `SingleInstance.Acquire` falls back to a copy that is not first when an elevated Armory (one a
   setup run as administrator started) owns the guard's names. That launch signaled an unnamed
   event and exited 0. A hung first copy took a Show and never opened, and the launch also exited 0.
3. Any exception before the crash handlers were attached ended the process with no trace.

The only record of case 1 was a line in the retirement audit file:
`%LOCALAPPDATA%\IDEA Armory\logs\install-retirement.jsonl` (on a lab computer,
`C:\Users\<Windows account>\AppData\Local\IDEA Armory\logs\install-retirement.jsonl`, normally
the IDEA account), one JSON string per line beginning with the UTC time, for example
`deferred: another retirement owns the inventory` or `retirement failed: <reason>`. A run that
starts renames it to `install-retirement.jsonl.replay` while it copies the lines into agent.log
as `retire: ...`, then deletes it. A 0.5.7 that never opened never got that far, so on a failing
computer the file is still there, and the first 0.5.8 run moves its lines into agent.log. Such a
computer also never downloads an update (the updater runs inside the open app), so it needs
`IDEAArmory-win-Setup.exe` from the 0.5.8 release run once.

**Built behavior.**

- The guard is taken first (`LaunchFlow`). The first copy opens its window, then runs the cleanup
  on its own STA thread 3 seconds later (`InstallRetirement.StartInBackground`), with the same
  90 second wait and lock. It never kills the script: past the wait the script keeps its lock and
  any uninstaller it started finishes. Every outcome is logged as `retire: <outcome>`, and one
  that did not finish is a `retireFailed` incident (docs/agent/TELEMETRY.md). The window and the
  engine go on either way. The script starts in TEMP, not Armory's folder: it can now outlive
  Armory (a setup closes Armory and replaces its folder; the flash-drive setup's "A file ... is in
  use" in CI was the script's inherited working directory holding the folder open). A cleanup
  that has not started when Armory begins to quit does not start. The post-update worker (`--armory-finish-update`) still exits 1 on a
  failed inventory, as AfterUpdate.ps1 expects.
- Two copies running at once during the cleanup is not unsafe here, so nothing is refused. The
  guard has had one name in every release since 0.1.0 (`Local\IDEA-Armory-Agent-<SID>`), so a
  copy that holds it is the only engine on this account; the other Armory processes the script
  can find are second launches on their way out, and it closes those only by their full path.
  One thing changed with the order: an older copy's own `--quit` signals whichever copy holds the
  guard, and that is now the survivor. The survivor passes `-CallerRunning`, and the script then
  closes older copies by path (CloseMainWindow, then stop) instead of asking them to quit.
- Only when another install's Armory holds the guard (a leftover copy started at sign-in, the
  case 0.5.5 was built for) does a launch run the cleanup before its window, because only that
  copy's end frees the guard. When it finishes, the launch waits up to 10 seconds for the guard and
  runs as the first copy. When it does not, the launch writes `logs\launch.log`, files a
  `retireFailed` incident and hands off to the copy that runs, whose window comes up.
- A second launch waits to be answered (`SecondLaunch`, `Handoff`). A 0.5.8 first copy sets
  `-Ready` once its tray listens and `-Shown` once a Show brought its window up; the second launch
  waits up to 60 seconds for Ready (a slow start) and 5 seconds for Shown. A 0.5.7 or older first
  copy has no such events; then the Show signal being taken within 15 seconds counts as answered.
  With no answer, or with an elevated copy holding the guard, the launch shows a message box:
  "Armory is already running in the background but not responding (process 4120). Restart the
  computer, or end IdeaArmory in Task Manager, then open Armory again." (or "already running as
  administrator ..."), writes `logs\launch.log`, files a `startupBlocked` incident and exits 1.
  Nothing a second launch does writes the running copy's agent.log.
- Every step of `Main` before the app runs is inside `StartupGuard`: an exception writes
  crash.log (with its pointer line in agent.log), files a `crash` incident ("Armory crashed in
  startup: ...") and shows "Armory couldn't start: <type>: <first line of the message>" with
  where the details are.
- Unchanged: `--check` and `--quit` (no window or message, ever), a notification link (forwarded
  to the running copy, or its window signaled), and `--background`, the sign-in start, which
  stays quiet: beside a running copy it exits 0, and a fatal startup error is only written down
  (crash.log and an incident). Velopack's hook launches (`--veloapp-install`, `-updated`,
  `-obsolete`, `-uninstall`) are quiet the same way.

**Tests.** `StartupTests` (every host): a cleanup that exits 1 or 2, times out, cannot find
powershell.exe, is refused by script policy or throws still starts the first copy normally and
files one `retireFailed` incident; a failed cleanup beside another install hands off; a finished
one re-takes the guard; a second launch with a first copy that never gets ready, never answers, is
older and never takes the signal, or is elevated shows its message with the process id, while the
sign-in start and a link behave as before; an early exception writes crash.log and an incident
and shows its one-line summary, and a quiet start shows nothing. On Windows the real guard's
Ready and Shown events are exercised across threads.

**Not verified on a lab computer.** Which of the causes above hit the lab's machines is not known;
the audit file above says. Group Policy, AppLocker and WMI restrictions were not reproduced.

## Validation and bounds

`tools/test-retirement-safety.ps1` executes the production safety functions in a throwaway
Windows folder, including edited/untracked files, traversal, junctions, vault markers, missing
executables and package ownership. `tools/test-armory-retirement.ps1` uses the published 0.3.3
installer and 0.4.2 package alongside this build, tests user/machine/loose/old/updater roots,
checks native shortcut and window properties and proof bytes after startup and an actual
prerelease-to-release package update, and races two participating releases. Both run in the
existing installers job and the release workflow. The older published-0.4.2 update smoke stays.
Measured results, run links and dates belong in the PR description, including failures until
resolved. These checks use a disposable Windows runner, not a lab desktop.

An unreferenced archive, an unreferenced dormant folder on an arbitrary drive, another
account's private profile, an indirect task action that runs an arbitrary wrapper script,
and access-denied machine resources cannot be completely inventoried by an ordinary account.
Protected roots and unsafe uninstallers are explicitly deferred. Copies in those locations
are not silently declared removed. Snapshot/ownership checks are bounded evidence; concurrent
external writers and machine policy enforcement need lab verification.

On a disposable lab PC:

1. Record the account, versions/paths, Apps and auto-start entries. Keep proof bytes in the
   team folder and record settings/sign-in. Install the published 0.4.2 alongside the old Inno
   or USB copy. Pin its running window and record the pin's position/icon.
2. Download `agent-dist` from this PR's successful installers run and extract it. Quit Armory.
   In PowerShell, apply its candidate package through the existing updater (replace the
   placeholder with the extracted package's absolute path):
   `& "$env:LOCALAPPDATA\IDEAArmory\Update.exe" --silent apply --package '<candidate-full.nupkg>' --norestart`
3. Open the original taskbar pin. Confirm its position/icon, one window, one Apps entry, and
   unchanged team files/settings/sign-in. Inspect its shortcut properties: the target must be
   `%LOCALAPPDATA%\IDEAArmory\IDEA Armory.exe`; the shell identity is `IdeaBosco.Armory`.
4. Unpin once and pin this candidate's running window, to check its native relaunch properties.
   Repeat the pin/update/single-copy checks when the next coordinated release downloads through
   the normal feed; quit/reopen to apply it. Record both release versions and the date.
5. Sign out/in and repeat with start-at-sign-in on and off. Inspect the recorder for removals
   and any deferrals. Explorer's live pin ordering/cache and policy-restricted machines require
   this human check.

The release version is unchanged in this lane. These changes must ship in a new release version
after the coordinated work lands; publishing an already-used tag is not an update rollout.

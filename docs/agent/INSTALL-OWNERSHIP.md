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

`InstallRetirement` runs the embedded `installer/scripts/Retire.ps1` before the single-instance
handoff. Install/update callbacks start an external Windows PowerShell waiter, which survives
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
Slow uninstallers retain the safe hook until they finish. The application waits at most 90
seconds before giving this launch up; it does not kill an uninstaller midway through cleanup.

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

On a lab PC, record the account, versions/paths, Apps and auto-start entries first. Keep proof
bytes in the team folder and record settings/sign-in. Install the candidate beside the old
copy, open it, and confirm one participating copy and unchanged data. Pin its running window,
check the root launcher target, let a newer lab release download, quit/reopen to apply it, and
confirm the same pin position/icon opens one window and one copy remains. Sign out/in and
repeat with start-at-sign-in on and off. Inspect the recorder for removals and any deferrals.
Explorer's live pin ordering/cache and policy-restricted machines require that human check.

The release version is unchanged in this lane. These changes must ship in a new release version
after the coordinated work lands; publishing an already-used tag is not an update rollout.

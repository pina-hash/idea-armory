# File Explorer: right-click items and status badges

Armory reaches into File Explorer in two ways, both free and both per Windows user:

- **Right-click items** (no administrator, always on): an "IDEA Armory" item on files and
  folders inside the vault, with Check out, Check out and open, Check in, Undo check out, Show in
  Armory, and Force check in for mentors and CAD leads. Static verbs in `HKCU`, written by
  `IdeaArmory.exe`; each click runs `ArmoryShell.exe`, which hands its path to the running Armory.
- **Status badges** (optional, one administrator step per computer): four icon overlays that
  show at a glance what Armory knows of each file. A native DLL that only reads what the running
  Armory publishes.

| Piece | Where |
|---|---|
| Badge rules, the badge table format (writer and a lookup that mirrors the DLL) | `src/Armory.Core/Badges.cs`, `BadgeTable.cs` |
| Case folding, publishing, health, change notices | `src/Armory.Platform.Windows/ShellFold.cs`, `BadgePublisher.cs`, `BadgeHealth.cs`, `ShellNotify.cs` |
| Registry layout of the menu; the pipe, its line and the batcher | `src/Armory.Agent/ShellVerbs.cs`, `ShellInbox.cs` |
| The badge handlers; the forwarder; test and lab tools | `native/badges/` (`ArmoryBadges.dll`, `BadgeTable.h`, `BadgeProbe.cpp`), `native/shell/` (`ArmoryShell.exe`, `ShellPipeTest.c`) |
| Builds | `tools/build-native.ps1` (MSVC, CI and releases), `tools/build-native.sh` (mingw, developers), `native/CMakeLists.txt` |
| Badge icons | `tools/agent-icon/make_badges.py` writes `native/badges/*.ico` |
| The administrator step | `installer/IdeaArmoryBadges.iss`, `installer/usb/Show Armory status on file icons.cmd` |
| Lab check | `tools/check-overlays.ps1` |

The host (AgentHost, TrayApp, the page) calls these pieces; none of them calls the engine.

## 1. Right-click items

### 1.1 Registry layout

Written by `ShellVerbs.Apply(vaultRoot, appFolder, forceCheckInFolders)`, never by an installer.
`<app>` is the folder of `IdeaArmory.exe` (`%LOCALAPPDATA%\Programs\IDEA Armory`), `<vault>` the
vault root from settings. All values are REG_SZ unless marked. Keys are relative to
`HKCU\Software\Classes`.

```
AllFilesystemObjects\shell\IDEAArmory
    MUIVerb                 IDEA Armory
    Icon                    "<app>\IdeaArmory.exe",0
    AppliesTo               System.ItemPathDisplay:~<"<vault>\"
    ExtendedSubCommandsKey  IDEAArmory.Menu
    MultiSelectModel        Player

IDEAArmory.Menu\shell
    01checkout      MUIVerb=Check out           MultiSelectModel=Player
                    command\(Default) = "<app>\ArmoryShell.exe" checkout "%1"
    02checkoutopen  MUIVerb=Check out and open  MultiSelectModel=Single
                    command\(Default) = "<app>\ArmoryShell.exe" checkoutopen "%1"
    03checkin       MUIVerb=Check in            MultiSelectModel=Player
                    command\(Default) = "<app>\ArmoryShell.exe" checkin "%1"
    04undo          MUIVerb=Undo check out      MultiSelectModel=Player
                    command\(Default) = "<app>\ArmoryShell.exe" undo "%1"
    05show          MUIVerb=Show in Armory      MultiSelectModel=Single   CommandFlags=0x20 (REG_DWORD)
                    command\(Default) = "<app>\ArmoryShell.exe" show "%1"
    06forcecheckin  only while the account can force a check in somewhere
                    MUIVerb=Force check in      MultiSelectModel=Player   CommandFlags=0x20 (REG_DWORD)
                    AppliesTo=System.ItemPathDisplay:="<vault>\Class 2026" OR System.ItemPathDisplay:~<"<vault>\Class 2026\" OR ...
                    command\(Default) = "<app>\ArmoryShell.exe" forcecheckin "%1"

Directory\Background\shell\IDEAArmory
    MUIVerb                 IDEA Armory
    Icon                    "<app>\IdeaArmory.exe",0
    AppliesTo               System.ItemPathDisplay:="<vault>" OR System.ItemPathDisplay:~<"<vault>\"
    ExtendedSubCommandsKey  IDEAArmory.BackgroundMenu

IDEAArmory.BackgroundMenu\shell
    01checkin   MUIVerb=Check in          command\(Default) = "<app>\ArmoryShell.exe" checkin "%V"
    02show      MUIVerb=Show in Armory    command\(Default) = "<app>\ArmoryShell.exe" show "%V"
```

- AQS string literals are quoted with a quote doubled (`ShellVerbs.Quote`); backslashes need no
  escaping; `OR` is in capitals.
- The trailing backslash in `~<"<vault>\"` keeps the vault folder itself out: a right-click on
  `C:\IDEA\Armory` never offers to check out the whole vault. The background menu includes the
  vault root, and offers only Check in and Show in Armory (a check out or undo of a whole folder
  from a click on empty space is too easy to do by accident).
- Force check in lists each allowed project's folder itself and everything in it, sorted, each
  once. The host passes the vault-relative folders of the projects where the signed-in account
  has `CanTakeBack`; none leaves the item out. The server still decides every force check in, so
  a stale entry (a role changed while Armory was not running) only earns one refusal sentence.
- `CommandFlags` 0x20 draws a separator above the item. Verbs sort by key name.
- No item hides by file state in this version. A later refinement can filter on the read-only
  attribute (Armory keeps files nobody has checked out here read-only), after lab check L5.

**Compare before write.** `ShellVerbs.Changes(existing, layout)` lists exactly what differs:
values set where missing or different (type included), values and keys under the four roots
that the layout does not name deleted. `Apply` writes only those and sends
`SHChangeNotify(SHCNE_ASSOCCHANGED)` only when there was at least one. The host calls it at
start, when the vault root changes, on sign-in and sign-out, and when the force-check-in
folders change. `ShellVerbs.Remove()` deletes the four roots; both uninstall routes delete the
same four keys plus `HKCU\Software\IDEA Armory`.

### 1.2 What each item does (host behavior)

| Item | Selection | What Armory does | Asks first? |
|---|---|---|---|
| Check out | 1 to 100 files and folders | Check out; a folder means every file under it | Yes when a folder is in the selection, with the window's own "Check out all" words |
| Check out and open | one file | Check out, then open it in SolidWorks | No |
| Check in | 1 to 100 | Check in | No |
| Undo check out | 1 to 100 | Undo; unsaved bytes become a kept copy, as in the window | No |
| Show in Armory | one item | The window on that file, or Team files at that folder; the vault root opens Home | n/a |
| Force check in | 1 to 100, allowed projects only | Resolve paths to files held by someone else, then the existing force check in | Always, with the window's words |

A path outside the vault or ignored answers "That isn't in the Armory folder." Not signed in:
the window opens on Connect with "Connect this computer first." The answer is the action's one
sentence, in the window's foot line when the window shows, otherwise as one tray notification.

### 1.3 ArmoryShell.exe and the pipe (normative)

Explorer starts one process per selected item for these verbs (up to 100 with
`MultiSelectModel=Player`). `ArmoryShell.exe` is plain C with no runtime, no window and no
console:

1. Arguments: exactly `<verb> <path>`; verb one of `checkout`, `checkoutopen`, `checkin`, `undo`,
   `show`, `forcecheckin`; path 1 to 32,767 characters with no control character and no quote
   (a trailing quote, left by `"D:\"`, stands for the backslash it escaped). Otherwise exit 2.
2. Pipe name: `\\.\pipe\IDEA-Armory-Agent-<user SID><instance suffix>-shell`, the agent's
   single-instance name (`SingleInstance.Name`) plus `-shell`. The instance suffix is empty for
   the real app; for a test instance (`ARMORY_DATA_DIR` set to a fully qualified folder) it is
   `AgentPaths.InstanceSuffix`: `-` and the first 16 lowercase hex digits of SHA-256 over the
   UTF-8 of that folder's full path in upper case. `ARMORY_SHELL_PIPE` replaces the whole name,
   and only for a test instance; the forwarder and `ShellInbox.PipeName` read it alike.
3. Connect (`SECURITY_IDENTIFICATION`). Busy: wait and retry for up to 10 seconds. Absent: start
   `"<own folder>\IdeaArmory.exe" --background` once (several forwarders may race; the
   single-instance guard keeps one), then retry every 100 ms for up to 30 seconds.
4. The server must be `IdeaArmory.exe` from the forwarder's own folder
   (`GetNamedPipeServerProcessId`, `QueryFullProcessImageNameW`, long path names compared without
   case), so a path never reaches another program. A test pipe (`ARMORY_SHELL_PIPE`) skips this
   check and never starts Armory.
5. `AllowSetForegroundWindow(server)`: the click came from the person, so Armory may bring its
   window or its question to the front.
6. Write one UTF-8 line `1<TAB>verb<TAB>path<TAB>GetTickCount64()<LF>` and read one byte: `0x06`
   means Armory took the line (exit 0); `0x15`, a closed pipe or anything else means it did not
   (exit 1). A watchdog ends the process after 60 seconds whatever happens.

The server (`ShellInbox`) creates its pipe with `CreateNamedPipeW` itself, because
`NamedPipeServerStream` does not set `PIPE_REJECT_REMOTE_CLIENTS`: overlapped, byte mode,
`PIPE_REJECT_REMOTE_CLIENTS`, a DACL that grants only the current user, and
`FILE_FLAG_FIRST_PIPE_INSTANCE` on the first instance so no other process can serve the name
first. Four instances wait at any time. Each connection carries one line of at most 100 KB,
read within 5 seconds; the server answers `0x06` or `0x15`, then waits for the forwarder to
close its end. `ShellLine.TryParse` accepts exactly the line above and nothing else.

**Batching (`ShellBatcher`).** One batch per verb. A batch closes 300 ms after its last arrival,
5 seconds after its first, or at its 100th distinct path (Explorer's limit for these items).
Different verbs never mix. `checkoutopen` and `show` go at once, alone. A path twice in one
batch counts once (case ignored) and still extends the wait. An arrival first closes every
batch that is already due. Each closed batch goes to the host's callback on a thread-pool
thread; the callback hands it to the engine and returns. 300 ms is several times the largest
gap between forwarders measured under Wine (58 ms for 100 started back to back); lab check L2
measures Explorer's own pace.

## 2. Status badges

### 2.1 The four badges

Explorer shows one overlay per icon and every handler takes one of Windows' 11 slots, so
states are combined into four:

| Badge | Key name (one leading space) | Shown when | Icon (shape and color) | GetPriority |
|---|---|---|---|---|
| Attention | `" IDEAArmory1Attention"` | can't be uploaded (a refusal, a "shares a name" refusal included), can't be read, changed without a check out, a kept copy | red triangle with "!" | 0 |
| Mine | `" IDEAArmory2Mine"` | checked out by you on this computer, changed or not; or new here and not in Armory yet (waiting or uploading, no file id) | blue disc with a pencil | 10 |
| Locked | `" IDEAArmory3Locked"` | checked out by someone else, or by you on another computer | amber padlock | 20 |
| Synced | `" IDEAArmory4Synced"` | in Armory, up to date, and not checked out by anyone | green disc with a check mark | 30 |
| (none) | | not on this computer, not in Armory and not on its way, downloading, a newer version waiting, a save of a free file uploading | | |

"Changed and not checked in" shares Mine: a change you can make is always on a file you have
checked out here; a change without a check out is Attention. `BadgeRules.For(facts)` applies
this table in this order: not on this computer is none; then Attention; then Mine; then Locked;
then Synced. Lower priority is stronger; the table holds one state per path, so two of ours
never claim one item, and the priorities matter only against other apps' badges.

**Folders** (`BadgeRules.WithFolders`): every folder below the vault root carries the strongest
Attention or Mine found anywhere under it. Locked and Synced do not climb (someone else's file
deep in a folder is nothing to act on, and a folder of synced files needs no mark). The vault
root never has a badge.

**What the engine supplies** (`BadgeFacts`, one per local file it knows): the vault-relative
path; the file's status (`synced`, `changed`, `uploading`, `downloading`, `waiting`,
`newerWaiting`, `keptCopy`, `notInArmory`, `notOnThisComputer`, the same as a file row's);
who has it checked out (`available`, `mine`, `other`, `myOtherComputer`); whether it has a file
id; whether Armory refused to upload it (dismissed or not); whether Armory could not read it.
`BadgeFacts.FromNames` maps the engine's names and throws on a name it does not know, so a
status added later cannot lose its badge silently.

**Order on a crowded computer.** Windows reads the handlers in the registry's order of key
names and uses the first 11. One leading space sorts our four after Adobe's and every name with
two or more spaces (Dropbox), and before OneDrive's (one space and an "O") and every name
without a leading space (Windows' own `EnhancedStorageShell`, `Offline Files`,
`SharingPrivate`). Our names put Attention first and Synced last, so Synced is the first of
ours to drop out and Attention the last. On a school computer with OneDrive only: 4 Armory + 7
OneDrive = 11 shown. Armory never uses more leading spaces than Windows' own OneDrive; Settings
tells the truth instead (2.6).

| Badge | CLSID | Icon index | Heartbeat value |
|---|---|---|---|
| Attention | `{E26E19F2-515F-472F-AD4F-1B0293728CE2}` | 0 | `SeenAttention` |
| Mine | `{DB040D16-C118-4CDA-B616-DF9340A8BC9F}` | 1 | `SeenMine` |
| Locked | `{DF50E3A9-57B8-44AE-B690-257AFF283F97}` | 2 | `SeenLocked` |
| Synced | `{58F5F8D8-1041-43B9-B8DE-0EBEBCC29CF0}` | 3 | `SeenSynced` |

Badges setup AppId: `{EA89842F-F4B2-4F97-8FB3-B90F36C40D3C}`.

### 2.2 The badge sections, format 1 (normative)

Armory publishes; the DLL only reads. Both sections live in the user's logon session
(`Local\`), are named for the user's SID, and are pagefile-backed
(`CreateFileMapping(INVALID_HANDLE_VALUE)`), so nothing is written to disk and nothing stale
outlives Armory. `Armory.Core.BadgeTable` writes this format and `native/badges/BadgeTable.h`
reads it; both follow this section, and BadgeTableTests and BadgeProbeTests hold them to it.
Everything is little endian.

**Header** `Local\IDEA-Armory-Badges-<user SID>` (a test instance appends its instance suffix;
the DLL reads another name only when `ARMORY_BADGES_SECTION` names it), 4096 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `0x48425241` ("ARBH") |
| 4 | 4 | version = 1 |
| 8 | 8 | generation (int64, 8-byte aligned; 0 = no table) |
| 16 | 4 | publisher process id (its exit drops every badge) |
| 20 | 4 | reserved, 0 |
| 24 | 8 | updatedAt (FILETIME UTC, diagnostics) |
| 32 | 8 | newest: the newest generation this header ever named (publishers only; kept when generation goes back to 0) |
| 40 | 4056 | zero |

**Table** `<header name>-<generation as 16 uppercase hex digits>`, immutable once published:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `0x54425241` ("ARBT") |
| 4 | 4 | version = 1 |
| 8 | 8 | generation (equals the name) |
| 16 | 4 | entryCount |
| 20 | 4 | slotCount (a power of two, at least 16, at least 2 x entryCount) |
| 24 | 4 | rootOffset (bytes, even) |
| 28 | 4 | rootUnits (folded vault root without a trailing backslash, 1 to 259) |
| 32 | 4 | stringsOffset (bytes, even) |
| 36 | 4 | stringsUnits |
| 40 | 4 | totalBytes |
| 44 | 20 | reserved, 0 |
| 64 | 16 x slotCount | slots: `uint64 hash; uint32 offset (UTF-16 units into the pool); uint16 units; uint8 state; uint8 reserved`; `units == 0` is an empty slot (hash 0 too) |
| rootOffset | 2 x rootUnits | the folded root, UTF-16 |
| stringsOffset | 2 x stringsUnits | the pool: folded vault-relative paths with `\` separators, no terminators |

State bytes: 0 none, 1 Synced, 2 Locked, 3 Mine, 4 Attention (`BadgeState`; the value is also
the strength). A reader treats any other value as none.

- **Fold**: one UTF-16 unit to one. ASCII `a` to `z` minus 32; other ASCII unchanged; any other
  unit through `LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE)` on that one unit, unchanged
  when the call does not return exactly one unit. Armory uses `ShellFold.Fold` (the same call);
  Core's tests pass `char.ToUpperInvariant`.
- **Hash**: FNV-1a 64 over the folded UTF-16 units, one step per unit (offset basis
  14695981039346656037, prime 1099511628211). Slot = `(uint32)hash & (slotCount - 1)`, linear
  probing.
- **Build**: paths with either separator, trimmed of separators, folded; entries with state none
  left out; paths that fold alike keep the strongest state; a relative path longer than 1,024
  units is left out; keys in ordinal order (so the same entries always give the same bytes).
- **Lookup** of a full path as Explorer passes it: the path ends at its first NUL; compare the
  folded path to the root unit by unit (outside the vault this ends within rootUnits steps);
  require `\` right after the root; fold the rest (more than 1,024 units: none); one trailing
  `\` is dropped (a folder named with its trailing backslash); empty: none; hash, then probe at
  most slotCount slots: an empty slot ends with none; a slot with the same hash and length whose
  `offset + units` is inside the pool and whose units equal the key gives its state.
- **Validate** before any lookup: magic, version, generation equal to the name, totalBytes within
  the mapped size, slotCount a power of two at least 16 with entryCount at most half, the slots,
  the root and the pool inside totalBytes, both offsets even, rootUnits 1 to 259.

**Publication** (`BadgePublisher`):

1. Build the bytes for generation G, create the table section by its name, copy the bytes in.
2. Write the publisher pid, updatedAt and newest = G into the header, then store G at offset 8
   with one aligned 64-bit store between full memory barriers.
3. Keep the previous two tables open for 5 seconds, then close them (a handler that already
   mapped one keeps it alive by itself).

G is the previous one plus one. The first publish of a run uses
`max(newest + 1, the current FILETIME)`, so a restarted Armory never names a section an Explorer
may still hold. If a table name is already taken, the next generation is tried.

`Clear` (badges off) and `Dispose` (quit) store generation 0, which every handler answers with
no badge at its next call. A publisher created while the header still exists (Explorer keeps it
mapped after Armory quit or crashed) takes it over: generation 0 and its own pid at once, then
its first publish as above.

**Size.** The owner wants every synced file in the table. Measured with the C# writer, paths of
about 58 characters: 5,000 files about 0.8 MB, 20,000 files about 3.4 MB (65,536 slots), built in
well under a second. Armory holds the current table plus at most two old ones for 5 seconds;
Explorer maps one. Under Wine with the native reader, a 20,012-entry table (2.4 MB) answered all
32 parity queries and took 220 ns per call inside the vault, 40 ns outside.

### 2.3 The handler (ArmoryBadges.dll)

Exports `DllGetClassObject` and `DllCanUnloadNow`; one class factory per CLSID.

- `GetOverlayInfo`: its own module path, the icon index of 2.1, `ISIOI_ICONFILE | ISIOI_ICONINDEX`;
  then once per process and badge, only inside a process whose image is `explorer.exe`, the
  heartbeat: `HKCU\Software\IDEA Armory\Badges` `Seen<Badge>` (REG_QWORD FILETIME UTC) and
  `ExplorerPid` (REG_DWORD).
- `GetPriority`: 0, 10, 20, 30 (Attention, Mine, Locked, Synced).
- `IsMemberOf(path)`: S_OK when the table's state for path is this handler's badge, else S_FALSE.
- One process-wide cache under an SRW lock: the header view, the current table's view with its
  bounds copied out of shared memory when mapped, and a handle to the publisher process.
  Every call: when the header is mapped, the 1-second liveness window is open and the header
  still names the cached generation, look up under the shared lock. Otherwise, under the
  exclusive lock: attach to the header at most every 2 seconds while Armory is not running (and
  return at once, without the lock, until then); once a second check the publisher process
  (ended: drop everything, no badges); on a new generation map and validate the new table and
  unmap the old.
- No allocation on a call, no file, no network, no Restart Manager, no wait, no USER32. Built
  `/MT`, `/guard:cf`, `/CETCOMPAT` (x64), warnings as errors. 64-bit only (x64 and ARM64); 32-bit
  programs' file dialogs show no badges.

### 2.4 What the host publishes and when

On a view change, at most every 500 ms, only while the badges are registered (`BadgeHealth`
at start and every 10 minutes): `BadgeRules.Entries(engine facts)`, then
`BadgePublisher.Publish(vaultRoot, entries)`, then
`ShellNotify.Send(ShellChangePlan.For(vaultRoot, previous, entries))` on a background thread
(SHChangeNotify can wait on Explorer). The plan names each changed item (added, removed or
changed state) when there are up to 256 (`SHCNE_UPDATEITEM`, the last with
`SHCNF_FLUSHNOWAIT`), else each folder that holds them when there are up to 256
(`SHCNE_UPDATEDIR`, a top-level item's folder being the vault root), else the vault root once.
When the vault root changes, publish for the new root and notify both. On sign-out and quit,
`Clear` or `Dispose`.

### 2.5 Installing the badges, both routes, and removal

**The badges setup** `IDEA-Armory-Badges-Setup-v<version>.exe` (`installer/IdeaArmoryBadges.iss`,
`PrivilegesRequired=admin`, no override): installs `ArmoryBadges.dll` (x64, or ARM64 on Windows
on ARM) into `C:\Program Files\IDEA Armory Badges\<version>\`, registers the four CLSIDs
(InprocServer32, ThreadingModel Apartment), the four `ShellIconOverlayIdentifiers` keys and their
`Shell Extensions\Approved` values, and `HKLM\SOFTWARE\IDEA Armory\Badges` with `Version`,
`Format` = `1` and `InstalledAt` (REG_QWORD FILETIME UTC). Each version gets its own folder, so a
DLL a running Explorer has loaded is never overwritten; older version folders are deleted after
the install, or when Windows next starts if still loaded. It never closes or restarts Explorer:
each person sees the badges after signing out of Windows and back in. Silent, for IT:
`IDEA-Armory-Badges-Setup-v<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.

- **Setup.exe route**: the per-user installer's payload carries
  `badges\IDEA-Armory-Badges-Setup.exe`, offered as an unchecked last-page option ("Also show
  Armory's status on file icons (asks once for an administrator password for this computer)")
  only when `HKLM64\SOFTWARE\IDEA Armory\Badges` has no `Version`. Settings offers "Turn on",
  which runs the same file with the `runas` verb and
  `/SILENT /SUPPRESSMSGBOXES /NORESTART`; a canceled password prompt (error 1223) answers
  "Nothing changed. This one step needs an administrator's password."
- **Flash drive route**: `Show Armory status on file icons.cmd` (optional, first comment line)
  runs `files\scripts\Setup.ps1 -Mode Badges`, which starts the badges setup as an
  administrator, waits, and ends with the usual PASS or FAIL banner and one drive log line (a
  canceled prompt is a FAIL: "Nothing changed: an administrator's password is needed for this
  one step").
- **Removal**: the badges have their own Apps entry, "IDEA Armory badges (status on file icons)",
  which needs an administrator. Uninstalling Armory for one account never removes them; with no
  Armory publishing for a person, the handlers show that person nothing.
- **Upgrades**: a newer Armory works with an older badges DLL while `Format` is 1. A new format
  bumps it, and Settings asks for the badges to be updated.
- **Security note**: the elevated setup runs from the per-user app folder or the flash drive,
  which the student account can write; Windows shows the file and "Unknown publisher". IT can
  run a copy they downloaded themselves instead.

### 2.6 Does Explorer show them? (BadgeHealth)

`BadgeHealth.Check()` reads, without an administrator: `HKLM\SOFTWARE\IDEA Armory\Badges`; the
`ShellIconOverlayIdentifiers` keys in the registry's own order (`GetSubKeyNames`, which is what
Explorer reads) with each CLSID's DLL; this person's heartbeat values; and the start time of
this session's `explorer.exe`. `BadgeHealth.Decide(facts)`, in this order:

| State | When | Settings line | Key |
|---|---|---|---|
| `off` | no badges setup and none of our keys | Armory's status isn't shown on file icons on this computer. Turning it on needs an administrator once. | Turn on |
| `broken` | one of our keys missing, another CLSID, no or a missing DLL, no recorded version, or another format | Armory's badges are installed, but a file is missing. Ask an administrator to turn them on again. | Turn on |
| `crowded` | none of our four within the first 11 | Windows isn't showing Armory's badges because 12 badges from other apps come first (Dropbox, OneDrive). Windows shows only 11. | |
| `partial` | one to three of ours within the first 11 | Windows shows only some of Armory's badges because 9 badges from other apps come first (OneDrive, Dropbox). | |
| `afterSignIn` | no explorer.exe in this session, or no heartbeat since it started and the install came after it | Armory's status shows on file icons after you sign out of Windows and back in. | |
| `on` | a heartbeat since this session's explorer.exe started, or none yet within its first 10 minutes | Armory's status shows on file icons. | |
| `broken` | explorer.exe has run 10 minutes since the install without asking for our icons (logged as "explorer.exe did not ask for Armory's badge icons") | as above | Turn on |

The count is the number of other handlers before our first one; the app names are their key
names trimmed, without trailing digits and then without a trailing `Ext` or `Ico`, each once.
`Detail` carries the reason for the log only.

## 3. Building the native parts

- **CI and releases** (windows-latest): `pwsh tools/build-native.ps1` finds Visual Studio with
  vswhere, takes each architecture's environment from `VsDevCmd.bat`, and builds into
  `publish\native\<arch>\`: `ArmoryBadges.dll` for x64 and ARM64 (`/O2 /W4 /WX /permissive- /sdl
  /GS /guard:cf /MT /EHsc /std:c++17`, linked `/guard:cf /DYNAMICBASE /NXCOMPAT`, `/CETCOMPAT` on
  x64, `/WX`), and for x64 `ArmoryShell.exe` (C, the same hardening, `/SUBSYSTEM:WINDOWS`),
  `BadgeProbe.exe` and `ShellPipeTest.exe`. It then checks that the DLL imports only KERNEL32
  and ADVAPI32 and the forwarder only KERNEL32, ADVAPI32, SHELL32, USER32 and BCRYPT (no Visual
  C++ runtime), and that both carry ProductName "IDEA Armory" and the version of
  `src/Armory.Agent` (its VERSIONINFO comes from `ArmoryVersion.h`, which the script generates
  from `native/ArmoryVersion.h.in`).
- **Developers on Linux**: `tools/build-native.sh` builds the x64 four with mingw-w64
  (`-Wall -Wextra -Werror`) for checks under Wine. Never ship these.
- **CMake** (optional): `native/CMakeLists.txt` with `-DARMORY_VERSION=<version>`.
- **Icons**: `python3 tools/agent-icon/make_badges.py` redraws the four `.ico` files
  deterministically (16 to 64 px as BMP, 256 px as PNG; the badge in the lower-left corner with
  a dark rim).

## 4. Tests

| Id | Where | What |
|---|---|---|
| T1 | `Armory.Core.Tests` `BadgeRulesTests` | every status and check out against the table of 2.1; strength order; Attention and Mine climb, Locked and Synced do not, the root never; the engine names map one to one |
| T2 | `Armory.Core.Tests` `BadgeTableTests` | header and table fields and offsets; slot count and load; the same bytes for the same entries; lookup in any case, with a trailing backslash, prefix traps, NUL, the 1,024-unit limit, forced collisions, a full ring; damaged tables answer none; 5,000 and 20,000 files fit a few megabytes |
| T3 | `Armory.Agent.Tests` `ShellBatcherTests` | 40 arrivals 20 ms apart are one batch; a 400 ms gap makes two; verbs never mix; 100 paths close at once; the 5-second cap; single verbs at once; duplicates once |
| T4 | `Armory.Agent.Tests` `ShellVerbsTests` | the exact layout and AQS strings (a vault with spaces, quotes and apostrophes in project names, the OR list); no Force check in without an allowed project; compare before write; a private registry key on Windows |
| T5 | `Armory.Agent.Tests` `ShellInboxTests` | the line parser and the pipe name everywhere; on Windows with the native build, real forwarders deliver, bad arguments exit 2, 100 forwarders all arrive, refused lines get 0x15, a second server cannot take the name |
| T6 | `Armory.Agent.Tests` `ExplorerMenuTests` (Windows, native build) | shell32's own context menu shows "IDEA Armory" and its items inside the vault only, Force check in only in an allowed project, and Check out reaches the pipe through ArmoryShell.exe |
| T7 | `Armory.Platform.Windows.Tests` `BadgeProbeTests` (Windows, native build) | a C#-built table answered by the DLL exactly as the C# lookup answers; median call time under 5 microseconds inside and outside the vault; nothing after generation 0; badges gone within 1.5 s of the publisher's end; the DLL reads what BadgePublisher publishes; a copy named explorer.exe writes the four heartbeats |
| T8 | `Armory.Platform.Windows.Tests` `BadgePublisherTests`, `ShellChangePlanTests`, `BadgeHealthTests` | publish, republish, keep-alive, clear and quit, a restart over a living header; the change plan; every health state and sentence |
| T9 | `tools/test-agent-install.ps1 -Kind Badges` (host wiring) | the badges setup's keys and files, `BadgeProbe.exe --com` creates all four, `check-overlays.ps1` lists ours, a second run is clean, uninstall removes everything |
| T10 | `-Kind Setup` and `-Kind Usb` (host wiring) | the HKCU verb keys after Armory starts, gone after uninstall; the payload holds ArmoryShell.exe and the badges setup with the right version resources |
| T11 | `tools/build-native.ps1` | warnings are errors; imports and version resources as in section 3 |

Measured here under Wine 9.0 with the mingw build (logic and parity, not Windows timing): 32 of
32 parity queries matched for 5,000 and 20,000 files; 100 forwarders started back to back all
delivered in 2.0 s with the largest gap 58 ms; a forwarder refused a pipe served by a program
other than `IdeaArmory.exe`; with no Armory running, forwarders started `IdeaArmory.exe
--background` beside them and then delivered; badges dropped 0.7 to 0.9 s after the
publisher's end.

## 5. Lab check: which badges does Windows show?

On each lab computer model, signed in as a student:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-overlays.ps1
```

It changes nothing and needs no administrator. It lists every overlay handler in the registry's
order with its position, SHOWN or HIDDEN against the limit (11, or `-Limit`), the key name in
brackets with its count of leading spaces, the CLSID, and the DLL (or "DLL MISSING"); marks
Armory's four; prints the badges setup's version and install time, this session's explorer.exe
start, and for each of Armory's badges whether this Explorer loaded it (the heartbeat); and
ends with one verdict line in the same words as Settings. Keep its output with the lab notes.

Lab checks: L1, the menu appears inside the vault only, on Windows 10 22H2 and Windows 11, on the
cascade and on Force check in's project list. L2, select 16, 40, 100 and 101 files: the menu
shows up to 100, and Armory's log shows one action per right-click. L3, this script, with
OneDrive (and Dropbox or Google Drive where installed), before and after a sign-out. L4, badges
change within a second of a check out, check in, undo and force check in, in an open Explorer
window and in SolidWorks' Open dialog. L5 (optional), read-only attribute filters on the items.

## 6. Left out, and why

**The Windows 11 first-level menu.** The Windows 11 first-level menu is left out. It only accepts
commands from apps with package identity, and identity needs a package signed by a certificate
each computer trusts. The free routes all fall short of "works with no cost and no extra step
for students": Microsoft describes self-signed and unsigned packages as development and test
tools (production needs a paid or IT-issued certificate), unsigned packages need an
administrator anyway, and a self-signed certificate created by our optional administrator step
would also have to sign the package on each computer and then be registered for every Windows
account with an external location in that account's profile, which no CI runner can prove and
which adds a second always-loaded native DLL to Explorer. The same items are one click away
under "Show more options" (or Shift+F10), work the same on Windows 10 and 11, and need nothing
signed.

**The Cloud Files API.** Left out because it would turn the vault into a cloud-files sync root
of on-demand placeholders, rewriting the engine's model of whole local files that SolidWorks
opens directly, for a status column; and overlay handlers do not even run under cloud-synced
folders.

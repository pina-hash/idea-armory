# Armory.Agent.Engine

The sync loop. Platform-neutral (`net10.0`), it composes `Armory.Core` (every decision),
`Armory.Client` (the network), `Armory.Storage` (content keys) and the platform interfaces
in `Platform.cs`. It runs on Linux in the end-to-end proof with a portable vault file
system; on Windows, `Armory.Agent` supplies `WindowsVaultFileSystem` and the durable
stores from `Armory.Platform.Windows`.

v2 is PDM-style check out (v2-design.md, decisions D1 to D4, D13, D15, D18): a file the
server has is read-only on disk unless this computer has it checked out, so SolidWorks opens
it read-only and cannot save over it; a check out takes the lock; a save while checked out
is kept on the server at once as a kept copy (a side version, "saved while checked out")
and the shared version advances only at check in; nothing takes or lets go of a lock by
itself except an add (below) and a lock taken only for a move or a removal.

## Public API

```csharp
var engine = new SyncEngine(new EngineOptions { VaultRoot = @"C:\IDEA\Armory" }, new EngineDependencies
{
    Files = vaultFileSystem,          // IVaultFileSystem
    Journal = journalStore,           // Core IJournalStore (DurableJournalStore on Windows)
    Snapshots = snapshotStore,        // ISnapshotStore (DurableSnapshotStore + OpenRead)
    State = stateStore,               // IEngineStateStore (FileStateStore)
    Sessions = sessions, Api = api, Blobs = blobs,
    ReleaseReader = null,             // no standalone saved-release reader exists yet
    Log = log.Info,                   // the raw text of each problem, once; the window gets plain words
});
engine.ViewChanged += view => bridge.Post(BridgeMessages.ViewMessage(view));
engine.Start();                       // background loop on the contract's schedule
await engine.SyncOnceAsync();         // one full pass (tests drive the engine this way)
engine.Pause(); engine.Resume(); engine.Wake();
await engine.GetFileDetailAsync(fileId);

// The window's actions. Each answers with ActionResult(Ok, Message), one plain sentence.
// Paths are vault-relative; a folder means every file under it.
await engine.CheckOutAsync(paths, open: false); // "Check out" / "Check out and open"
await engine.CheckInAsync(paths);
await engine.UndoCheckOutAsync(paths);
await engine.TakeBackAsync(fileId);   // a mentor or CAD lead: armory_break_lock
await engine.LaunchAsync(path);       // "Open": the file's own program (never programs or scripts, D14)
await engine.RenameFileAsync(path, newName);
engine.DismissNotice(key);            // a notice card's OK, or one check-out question ("prompt:...")
await engine.MoveAsync(from, to);     // a rename through armory_move_file
await engine.StopAsync();
```

`View` is the window's `AgentView` (docs/agent/BRIDGE.md); `OpenWithoutCheckOut` lists the
open files this computer has not checked out, for the tray's one quiet balloon per opened
file (D13).

## One pass

1. **Identity.** No session: the view says signed out. The state document is bound to the
   first email and device that sync into it; another account sees "this vault belongs to
   someone else" and nothing syncs.
2. **Scan and capture.** The platform scan (ignore list applied) gives every file's hash,
   its read-only bit, the folders and SolidWorks' `~$` markers. A hash that differs from the
   base and from the last capture is a save: `SaveRecorder` persists the bytes as an
   immutable snapshot and journals a Core `Upload` intent, offline too.
   `SaveRecorder.Recover` re-journals any capture a crash left unjournaled.
3. **Refresh.** `armory_my_projects`, then per project `armory_list_changes(cursor)` (the
   cursor is persisted after processing) and `armory_project_files`. A `lock_broken` change
   naming this computer records the obligation to keep its bytes; a `file_revived` change
   records when the file was revived (File detail marks the version that followed). Every
   tracked file's live check out is remembered in its state (`FileState.Holder`), so the
   window says who has it while offline, even after a restart. A check out that this
   computer itself takes or lets go of is known at once (`KnowLock`), never only at the next
   read of the server: a connection that drops right after a release or an acquire never
   leaves the read-only rule or the window acting on the lock as it was.
   A check out asked for a file this computer already holds keeps it checked out
   (`KeepCheckedOut`): a check in or an undo still waiting, an open add's automatic check in
   and a lock held only for a move give way to it. This runs only once the server was read,
   so a check out refused offline cancels nothing.
4. **Finish what a crash interrupted.** Each file's state may hold one in-flight server
   write (create, lock, commit, side version, release, tombstone, move), persisted before
   the call with its operation id and arguments. It is re-sent with the same id, so the
   server answers from its receipt, and its result is applied. A release or a removal is
   dropped instead and decided again from fresh state.
5. **Moves and earlier saves.** A file whose server folder or name changed is moved here
   (never while open). An Explorer rename is sent as `armory_move_file`. A journaled save
   whose bytes are no longer on disk is kept as a side version ("earlier save, kept").
6. **Plan with Core, Explicit mode.** For every path: `Reconciler.Plan(SyncInput)` with base,
   local hash, remote revision, lock ownership, open state (`IsOpenNow`: the platform's check
   or a `~$` marker), online state, the break obligation, the saved release, the project's
   pin and gate mode, the preserved hash, `CheckoutMode.Explicit` and the student's request
   (`CheckIn` or `Undo` from the file's state; a closed add counts as `CheckIn`). Offline
   plans only add journal intents (never a lock intent for a shared file). Online plans run
   in order; any failure stops that file until the next pass.
7. **Finish requests.** Read the server again if the plans wrote, then: a check in, an
   undo and a closed add let their lock go once the file is clean, and a lock taken only for
   a move or a removal as soon as that is done, whatever is on disk; always read-only first,
   then the release, and a read-only bit that can't be set keeps the lock until a later pass
   can set it. An asked-for check out keeps any lock this computer holds and otherwise takes
   its own (see Check out). Read the server again if anything was written.
8. **The read-only rule** (D4), every pass, offline too, from the ownership this computer
   last knew (its own lock changes of the pass included; offline since the start, the
   ownership it last applied, and none known means nobody's): every file the server has a
   live version of is read-only unless this computer has it checked out; a file it is
   letting go of is read-only already. Files the server does
   not have (not added yet, a refused name, a release-gate draft, too large) are never
   touched. A bit the scan finds cleared is set again. One batch per pass
   (`ApplyLockAttributes`, one manifest write); a download sets the bit on the staged copy
   before it is renamed into place (`Replace(readOnly)`), so new bytes are never writable.
9. **View.** The window's `AgentView` is rebuilt.

The engine re-decides nothing Core decides. It executes `Download` only after rechecking
that the file is closed and unchanged, and `SaveSideVersion` and `Upload` only from
immutable snapshot bytes.

## Check out (D1 to D4, D18; v2-design.md 4.2)

Every request is durable in the file's state before any server call, and is carried out
inside a pass under the pass gate, with operation ids derived from it; a crash finishes it
on the next pass, through the crash points every write already has (`before-lock`,
`after-lock`, `before-commit`, `after-blob`, `after-commit-rpc`, `after-commit`,
`before-release`, `after-release`, `before-side`, `after-side`).

- **Check out** (`FileState.CheckOut`, the lock's operation id derives from it). The file is
  hashed at check-out time and `CheckoutRules.NextCheckOutStep` decides: the lock is taken
  only on `TakeLock`, over a copy that is the live shared version. On `DownloadFirst` (the
  copy is behind and closed) or `KeepChangesFirst` (bytes saved without a check out) the
  pass with the lock free has already brought it up to date (or kept the bytes as a kept
  copy and put the shared version back), and the rule is asked again in one more pass. The
  rest is refused in a sentence: someone else has it ("Plate.SLDPRT is checked out by Maria
  Lopez on LAB-PC-07."), a newer version waits behind an open file, it was removed here, the
  server has no live version. Taking the lock makes the file writable at once; "Check out
  and open" then opens it, unless SolidWorks still has it open ("Close Plate.SLDPRT in
  SolidWorks first"). A folder is every live file under it: "Checked out 12 of 14 files.
  Maria Lopez has 2 of them checked out." The rest name where they are: up to three people
  ("Maria Lopez and Sam Lee have 3 of them checked out.") and my other computer ("1 is
  checked out on your other computer, LAB-PC-07."). A file SolidWorks has open read-only
  while it is checked out (not "and open") answers "Close it in SolidWorks and open it again
  to save changes." A check out that could not finish is dropped, not left to happen later
  by surprise; checking out a file that is being checked in or undone, added while open or
  held for a rename keeps it checked out (only once the server was read: offline, the check
  out is refused and the check in or undo still waits). A lock taken just before the
  connection dropped is a check out all the same, and the answer says so.
- **Check in** (`Request = CheckIn`): Core uploads the bytes on disk if they changed (a commit
  on the base, under this computer's lock); then the file is made read-only; then the lock is
  released. Offline, the request waits and finishes when the computer is back online. Bytes
  Armory can't take (the release gate, too large) can't be checked in: the request is
  dropped and the file stays checked out.
- **Undo check out** (`Request = Undo`): refused while the file is open. Core keeps unsent
  bytes as a kept copy (`SideVersionReason.UndoCheckOut`), the shared version is put back,
  the file is made read-only, then the lock is released.
- **Saves while checked out** (D1): each is kept on the server at the next online pass as a
  kept copy, "saved while checked out", so every save is on the server; the shared file
  advances only at check in.
- **Adds** (D2): a new file (no server record) is created, locked, committed and released in
  one pass, read-only afterwards. A new file that is open when added stays checked out to its
  creator (writable) and is checked in by the pass after it closes (`FileState.AutoCheckIn`).
  These are the only automatic check ins; an explicit check out is never released by itself.
- **No marker locks** (D3). A `~$` marker means only "open" (10 minutes of staleness kept). For
  a file the server has and this computer has not checked out it raises the quiet question
  (`AgentView.prompt`, the most recent open first, one per open, dismissed with its key);
  if someone else has the file, the question says who.
- **Changed without a check out**: if the attribute was cleared and the file saved anyway,
  Core keeps every capture (earlier ones as "earlier save, kept") and the latest as one kept
  copy ("changed without a check out"), never the shared version, and the shared version
  comes back once the file is closed: one grouped notice.
- **Take back** (`armory_break_lock`, for a mentor or CAD lead): the operation id derives from
  that one check out and the computer asking, so asking twice here takes it back once and a
  second mentor or CAD lead never reuses another caller's id. It is asked once and never
  resumed after a crash (no in-flight record): the mentor asks again, and the same id
  answers from the server's receipt. Asked from an older view after someone else took it
  back, it answers "Plate.SLDPRT isn't checked out any more." The holder's computer keeps
  what was not checked in (`lockBroken`) and shows one notice.
- **Moves and removals** take the lock only for themselves (`FileState.TransientLock`) and let
  it go as soon as the move or the removal is done, whatever is on disk; a removed file's
  lock is always let go. Core plans a file under such a lock as nobody's
  (`LockOwnership.Free`), so bytes saved without a check out before a rename are one kept copy
  ("changed without a check out") and the checked-in version comes back, never a shared
  version nobody checked in.

## Rules added after review

- Saves are captured before any network step; a refusal of one file (too large, removed
  from the project, an unreadable snapshot) is shown for that file and never stalls the pass.
- After any resumed write, the server snapshot is fetched again before planning.
- A file this computer tracks is planned against its server record by id, wherever it now
  lives; a server rename waits until the file is closed.
- An Explorer rename or move (NTFS file id, or the same bytes at exactly one new untracked
  path in the same project) is sent as `armory_move_file`; a refused one is renamed back,
  and the notice names who has the file checked out.
- A deletion is planned only after two consecutive scans miss the file, and the file is
  probed again right before the tombstone is sent.
- Names are never refused here for having been removed. Adding a file under a removed file's
  name (anywhere in the project, at its old path too) revives that file through
  `armory_create_file` (contract C4): same id, its history going on. Whether an id is a
  revival is read from the change feed (`file_revived`) before the first commit, and the
  revived file's current version is fetched fresh, so the new bytes are committed on top of
  it, never with no parent, even when the removal happened after this pass's refresh. The
  computer's own record of the removed file at its old path is forgotten when nothing of it
  is on disk, so the removed bytes never come back (`RevivalTests`). A live name clash
  (SQLSTATE 23505) is one "shares a name" notice item.
- Saves the release gate refuses are private drafts: never sent, never holding the lock,
  offered again if the gate later allows them.
- A `~$` marker counts as "open" while the platform corroborates it and for 10 minutes after
  it first appears; a stale marker is treated as closed, and never ends a check out.
- After a reconnect (a new device id), the old id's check outs are still this computer's;
  writes under them use the holding id.
- An in-flight release or deletion is dropped on restart and planned again from fresh state.
- A file is not renamed from the window while a write for it is being sent (its add, a
  save): "Armory is still adding Gear.SLDPRT. Try again in a moment." The write, sent again
  under the old name, would otherwise undo the rename.
- A problem reaches the window as one plain sentence (no journal, vault or lock), under
  `cantRead` for this computer's disk and `cantSend` for what the server refused; the raw
  text goes only to the log (`EngineDependencies.Log`), once per problem.

## Upgrade from 0.1.0 (D15)

`EngineState` is schema 2. Loading schema 1 keeps the ownership 0.1.0 last applied as this
computer's last knowledge of who holds each file: the v2 rule reads it (Free and someone
else are read-only now, this computer stays writable), and the first pass, online or
offline, sets the bit on every file whose bit differs (0.1.0 left a file nobody held
writable). It keeps every lock this computer holds, which is now a check out ("Checked out
by you"); sets each project's local folder to its name; and completes the journal intent of
a lock a 0.1.0 marker took. `EngineUnitTests` loads a state.json the 0.1.0 engine wrote
(`tests/Armory.Agent.Engine.Tests/Fixtures/state-0.1.0.json`); `UpgradeTests` runs the first
0.2.0 pass over a 0.1.0 vault, online and offline.

## The view (v2-design.md 4.4 to 4.6)

Notices are grouped by kind, at most one card per kind, at most 200 items each (the count is
the total): `nameShared`, `cantSend`, `cantRead` (disk problems, and a stale SolidWorks
marker), `newerWaiting`, `keptCopy`, `takenBack`, `folderPutBack` (a refused rename put
back). "SolidWorks year not checked" is never a notice, only a tag on File detail; waiting
to upload is activity, never rows. My files are the files this computer has checked out, in
any project. Every row says who has it checked out ("Checked out by you", "Checked out by
Maria Lopez on LAB-PC-07", "Checked out by you on LAB-PC-07" for my other computer,
"Available"). Offline since the start, the team's files are listed as this computer last
knew them (`FileState.Holder`, its base and its saves), each with its file id, label and
status, and "waiting" only for saves not on the server yet. A dismissed card stays
dismissed: the items the window last showed are hidden, whenever the dismissal arrives, and
items that are gone are forgotten only at the end of a whole online pass, never while a view
is built. Kept copies are one item per file (the newest), the card has OK, and it counts
toward "A few files need you" only while a checked-in version still waits for its file to
close or someone else's check in overtook it. History entries are `version` ("Added to
Armory", "Checked in", "Added again, with its history", from the `file_revived` changes,
since the server keeps no removal row once a file is revived), `keptCopy` ("Saved while
checked out", "Kept when the check out was undone", "Changed without a check out, kept as
Alex Kim's own copy", ...; `routine` for saves kept while checked out and earlier saves) and
`removed`.
The activity panel's directions and speed come with stage E3; offline and paused, its
waiting line already counts the files waiting to upload. Folder, project and import work
(renamed and deleted folders, project renames and archiving, the import summary) is stage E2.

## Operation ids (crash safety)

Every server write carries an operation id derived (SHA-256, formatted as a UUID) from a
durable value and the step: a Core journal entry id for `create` (`revive` and the removed
file's id for a revival), `lock#attempt`, `commit#parent#attempt`, `side`, `tomb#attempt`;
a check out's lock from its request id; every answer to a lock or commit spends the
attempt. A release uses the lock's holder and acquisition time, a take back the check out it
ends and the computer asking, and a move a persisted id. The in-flight record is written
before every write the engine resumes (all but a take back, which the mentor asks again);
a crash at any point replays the same id and the server returns its receipt, so a save
becomes exactly one version.

## Schedule (contract section 4)

The loop polls every 5 seconds while online and active, and backs off to 60 seconds after
two minutes with no local or remote change. A disk hint (`Wake`) runs a pass at once.
Offline, it retries on the idle interval. While paused, no pass runs and the window's
actions say so.

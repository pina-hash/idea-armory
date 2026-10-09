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
var engine = new SyncEngine(new EngineOptions { VaultRoot = @"C:\IDEA\Armory", TransferConcurrency = 6 }, new EngineDependencies
{
    Files = vaultFileSystem,          // IVaultFileSystem
    Journal = journalStore,           // Core IJournalStore (DurableJournalStore on Windows)
    Snapshots = snapshotStore,        // ISnapshotStore (DurableSnapshotStore + OpenRead)
    State = stateStore,               // IEngineStateStore (FileStateStore)
    Sessions = sessions, Api = api, Blobs = blobs,
    ReleaseReader = new SolidWorksSavedReleaseReader(), // 0.3.3: the SolidWorks year from the file itself (below)
    Log = log.Info,                   // the raw text of each problem, once; the window gets plain words
    Recorder = telemetry.Recorder,    // the flight recorder (docs/agent/TELEMETRY.md); null records nothing
    Live = new RealtimeFeed(sessions),// v0.3 live updates (below); null: the poll alone
    SolidWorks = solidWorksLink,      // 0.3.3: ISolidWorksLink (docs/agent/SOLIDWORKS.md); null: markers only
});
engine.ViewChanged += view => bridge.Post(BridgeMessages.ViewMessage(view));
engine.ActivityChanged += activity => bridge.Post(BridgeMessages.ActivityMessage(activity)); // at most 4 a second
engine.Start();                       // background loop on the contract's schedule
await engine.SyncOnceAsync();         // one full pass (tests drive the engine this way)
engine.Pause(); engine.Resume(); engine.Wake();
await engine.GetFileDetailAsync(fileId);

// The window's actions. Each answers with ActionResult(Ok, Message), one plain sentence.
// Paths are vault-relative; a folder means every file under it.
await engine.CheckOutAsync(paths, open: false); // "Check out" / "Check out and open"
await engine.CheckInAsync(paths);
await engine.UndoCheckOutAsync(paths);
await engine.TakeBackAsync(fileId);   // when the server says can_take_back (v0.3): armory_break_lock
await engine.LaunchAsync(path);       // "Open": the file's own program (never programs or scripts, D14);
                                      // a file not here yet downloads first (a pass scoped to it) and opens when it arrives
await engine.RenameFileAsync(path, newName);
// Folders (a folder is in a project: "" is its top folder, "Drivetrain/Gearbox" one inside).
await engine.CreateFolderAsync(projectId, parent, name);      // New folder (on this computer)
await engine.RenameFolderAsync(projectId, folder, newName);   // one armory_rename_folder, then one move here
await engine.DeleteFolderAsync(projectId, folder);            // one armory_delete_folder, then recovery here
await engine.AddFilesAsync(projectId, folder, sources);       // files and whole folders, copied in, never over anything
engine.DismissNotice(key);            // a notice card's OK, or one check-out question ("prompt:...")
await engine.DismissNoticeAsync(key); // the same, done once the view (or the next one) leaves it out
await engine.MoveAsync(from, to);     // a rename through armory_move_file
await engine.StopAsync();

// The SolidWorks link (0.3.3; "The SolidWorks year" below). Each marshals onto the engine thread.
engine.RecordReleaseStamp(stamp);     // after a save it watched: Core ReleaseStamp for exactly those bytes
await engine.RecordReleaseStampAsync(stamp); // the same, true once kept (false: not a SHA-256)
engine.SolidWorksAttached("34.4.1", saveDownWorks: true); // ISldWorks.RevisionNumber; false once SolidWorks would not save down
engine.SolidWorksDetached();          // (with EngineDependencies.SolidWorks the link's records do both)

// Host-facing, for Windows notifications (C5, B2; "SolidWorks opened a file" below), not in the page contract.
engine.OpenPrompts;                   // IReadOnlyList<OpenPrompt>: files SolidWorks opened that this computer hasn't checked out
engine.OpenPromptsChanged += prompts => notifications.Show(prompts); // raised on the engine thread
engine.SaveDownPrompts;               // IReadOnlyList<SaveDownPrompt>: what a save down drops, asked before the save
await engine.CheckOutAndReopenAsync(paths);   // = CheckOutAsync(paths, open: true): writable in SolidWorks, in place
await engine.KeepLocalAsync(paths);           // "Keep this file on this computer only" (keep: false undoes it)
await engine.AnswerSaveDownAsync(path, keepLocal); // a SaveDownPrompt's two buttons
await engine.SaveDownNowAsync(paths);         // "Save it in 2025 now": SolidWorks saves the open file in its pinned year
```

`View` is the window's `AgentView` (docs/agent/BRIDGE.md); `OpenWithoutCheckOut` lists the
open files this computer has not checked out, for the tray's one quiet balloon per opened
file (D13); `OpenPrompts` is the same question for Windows notifications, grouped. Every method marshals onto the engine's own thread (see Threading), so the
window's UI thread only awaits: it never scans, hashes, saves the state or waits on a pass.
`View`, `IsPaused`, `OpenWithoutCheckOut`, `OpenPrompts` and `SaveDownPrompts` are published
values, read without the engine.

## One pass

A pass has four phases. A, B and D run one step at a time; C moves files several at once
(see Threading and concurrency).

**Phase A, in order.**

1. **Identity.** No session: the view says signed out. The state document is bound to the
   first email and device that sync into it; another account sees "this vault belongs to
   someone else" and nothing syncs. Since 0.3.2 that account can take the folder over
   (`TakeOverFolderAsync`, SyncEngine.Accounts.cs) when the account it belongs to has nothing
   waiting in it: no file checked out here, no save not completed, no file on disk that
   differs from its base, no new file not in Armory yet, no move or folder change not sent.
   The state document then forgets its email, device, former devices, notices and kept-copy
   records, and the next pass binds it to the new account; the files are the team's versions,
   so nothing is downloaded again. Anything waiting keeps the folder with its account (it is
   that person's work). Two accounts in one folder at once is never allowed.
2. **Scan, folders, capture.** The platform scan (ignore list applied) gives every file's hash,
   its read-only bit, the folders, the folder moves it proved and SolidWorks' `~$` markers.
   Folder changes on this disk are read first (see Folders and projects): a folder move this
   engine was making when it stopped is finished from what the disk shows, a renamed folder's
   records follow the disk, a project folder renamed, moved or removed in Explorer is put back,
   folders waiting to go back are moved back, and known folders gone from the scan are counted. Then a hash that differs from the base and
   from the last capture is a save: `SaveRecorder` persists the bytes as an immutable
   snapshot and journals a Core `Upload` intent, offline too.
   `SaveRecorder.Recover` re-journals any capture a crash left unjournaled.
3. **Refresh** (the first of at most two reads of the server in a pass). `armory_my_projects`
   (a project renamed on the site moves its folder here; an archived one is not read), then per
   project `armory_list_changes(cursor)` (the cursor is persisted after processing) and, only
   when that feed moved, this computer wrote to the project since its files were read, its
   folder changed or a minute went by, `armory_project_files` (every write emits a change, so a
   quiet feed means the files as last read are still the files). A `lock_broken` change
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
   write (create, lock, commit, side version, release, tombstone, move), on disk before
   the call with its operation id and arguments. It is re-sent with the same id, so the
   server answers from its receipt, and its result is applied. A release or a removal is
   dropped instead and decided again from fresh state. The projects written to are read again
   (only those). Then each folder rename or removal
   made on this disk or asked for in the window is sent in the order it happened, one call
   each, with its persisted operation id, the project's files read again after each call (and
   only those projects are read again before planning).
5. **Moves and earlier saves.** A folder the team renamed moves here in one step; a file
   whose server folder or name changed otherwise is moved here (never while open); moves on
   this disk change nothing on the server, so nothing is read again for them. A bulk add
   is recorded for its import summary. An Explorer rename is sent as `armory_move_file` (the
   projects it changed are read again). A
   journaled save whose bytes are no longer on disk is kept as a side version ("earlier
   save, kept"). A known folder gone for a second scan is one `armory_delete_folder`.

**Phase B. Plan with Core, Explicit mode.** For every path, in path order:
   `Reconciler.Plan(SyncInput)` with base,
   local hash, remote revision, lock ownership, open state (`IsOpenNow`: the platform's check
   or a `~$` marker; since 0.3.2 asked once for every file on disk before planning,
   `IVaultFileSystem.OpenAmong`, never once per file: on Windows each question was a Restart
   Manager session of about 28 ms, so planning 1,500 files took 40 seconds every pass and every
   click waited behind it; a batch of check outs and the `~$` markers are asked the same way,
   and every write still asks again just before it), online state, the break obligation, the saved release (read only
   for bytes that changed: see "The SolidWorks year"), the project's
   pin and gate mode, the preserved hash, `CheckoutMode.Explicit` and the student's request
   (`CheckIn` or `Undo` from the file's state; a closed add counts as `CheckIn`). Offline
   plans only add journal intents (never a lock intent for a shared file). Online plans are
   grouped into units: one file each, except that files sharing a name in a project are one
   unit, in path order, so which of them gets the name never depends on timing. "Sharing a
   name" is decided by a key at least as coarse as the server's own rule
   (`lower(normalize(name, NFC))` in PostgreSQL): `NameKey` folds compatibility forms and
   accents away (NFKD, marks dropped, so the dotted capital I is I), makes the capital sharp s
   the small one and folds case both ways, where .NET's casing alone keeps "ẞolt" and "ßolt",
   or "İnsert" and "insert", apart. Folding more than the server only puts a few more files in
   one unit. The activity panel learns here how many files and bytes will go each way.

**Phase C. The units**, at most `EngineOptions.TransferConcurrency` at once. A unit runs its
   files' plans in order and each plan's actions in order; any failure stops that file until
   the next pass. Going offline in a unit starts no new unit (the rest are planned offline).

**Phase D, in order.**

6. **Finish requests.** Nothing under a folder being renamed, removed or put back, or in a
   project whose folder is gone, is planned file by file; in an archived project only this
   computer's own check outs are. The server is read again if anything was written (the
   second and last read of the pass). Then, file by file in path order: a check in, an
   undo and a closed add let their lock go once the file is clean, and a lock taken only for
   a move or a removal as soon as that is done, whatever is on disk; always read-only first,
   then the release, and a read-only bit that can't be set keeps the lock until a later pass
   can set it. An asked-for check out keeps any lock this computer holds and otherwise takes
   its own (see Check out). The check outs are then taken, and the releases' in-flight
   records saved together, once, and the releases sent, both `TransferConcurrency` at a
   time like the units (a crash or the connection stops them as it stops the units). The
   locks a check in or an add lets go of show in the upload direction as "Checking in 412 of
   4,900 files", so the status line and the tray never fall back to "Checking for changes."
   while a big import finishes. The read-only rule follows this computer's own lock changes
   without another read (`KnowLock`).
   The team's SolidWorks versions the server never checked are read from this computer's
   identical copies (B5, "The SolidWorks year").
   Known folders with nothing left in them are removed (D17).
7. **The read-only rule** (D4), every pass, offline too, from the ownership this computer
   last knew (its own lock changes of the pass included; offline since the start, the
   ownership it last applied, and none known means nobody's): every file the server has a
   live version of is read-only unless this computer has it checked out; a file it is
   letting go of is read-only already. Files the server does
   not have (not added yet, a refused name, a release-gate draft, too large) are never
   touched. A bit the scan finds cleared is set again. One batch per pass
   (`ApplyLockAttributes`, one manifest write); a download sets the bit on the staged copy
   before it is renamed into place (`Replace(readOnly)`), so new bytes are never writable.
8. **View and state.** The window's `AgentView` is rebuilt (during the pass, at most every
   500 ms), and everything the pass changed is saved before it returns.

The engine re-decides nothing Core decides. It executes `Download` only after rechecking
that the file is closed and unchanged, and `SaveSideVersion` and `Upload` only from
immutable snapshot bytes.

## Threading and concurrency (v2-design.md 4.1)

- **One engine thread.** `EngineThread` is a `SynchronizationContext` with its own queue and
  thread ("Armory engine"). Every public method marshals onto it (`InvokeAsync`, `Enqueue`;
  the constructor reads the state document there too), and every await inside the engine
  comes back to it, so engine state is only ever touched by that thread and needs no locks.
  The thread ends after 10 seconds with nothing to do and a new one starts on the next call,
  so an engine a test drops keeps no thread alive. Since v0.2.1 each queued item runs under
  its own small context (a `Turn`): a task completed while one item runs never runs another
  item's continuations inline, it posts them, so the stack stays as deep as one item however
  many files complete one after another (`Continuations_never_nest_on_the_engine_thread`; the
  3,000-file first sync in `FirstSyncTests` reaches 20 frames, 49 before). When several units
  stop at once after a failure, the failure thrown is the one that started it, not a
  cancellation seen first.
- **The view timer never recurses.** v0.2.0's timer called `PublishSoon` again when it fired;
  a timer that fired a fraction of a millisecond early asked `Task.Delay` for under 1 ms,
  which completes at once, and the two called each other until the stack overflowed (the
  field crash: the agent died every minute or two during a first big download, with no line
  in its log). The timer is a loop and always waits at least 1 ms
  (`The_view_timer_always_really_waits`).
- **The log says what passes did.** Every pass that moves files writes `pass: moving N of M
  files (loop|action|whole)` when phase C starts and `pass: ended after N ms (...), D
  downloaded, U uploaded, K kept copies, R refused` at its end (`failed` instead of `ended`
  after a failure); a long pass writes `pass: still going after N s, ...` at most once a
  minute. The agent writes "previous run ended unexpectedly" with the last such line when the
  run before it never logged `stopped`.
- **A view the same as the last one is not raised again** (its JSON compared): with thousands
  of files a view message is about 380 bytes a file (1.1 MB for 3,000), and the page draws
  every one it gets. Views are still built at most every 500 ms during a pass, and the page's
  long lists draw only the rows in sight.
- **File detail** never waits for a pass: it reads the server's files as last published
  (`publishedRemote`, replaced at the end of every read of the server and patched by
  `KnowLock`) and this computer's records between two steps of a pass.
- **Units.** Phase C runs units as interleaved async tasks on the engine thread, at most
  `TransferConcurrency` at once (default 6, a judgment call on the measurements in
  docs/agent/PROOF.md, not a knee: one computer alone keeps getting faster up to 24 at once,
  and six computers behind one 25 MB/s school link fill it at 6 each). A unit is one
  file's whole plan, or the files of a project that share a name (`NameKey`), in path order.
  Every crash point name fires once per file in the order it always did. A unit that finds
  the connection gone (`ArmoryOfflineException`) stops new units from starting; those already
  running end their current step and keep their in-flight record. Any failure that is not
  one file's (a `SimulatedCrash` in tests, a bug) stops all saving at the moment it is thrown
  (`StopSaving`, from the crash point itself or an exception filter, before any other unit
  runs again) and cancels the pass's units: each stops at its next step (`Checkpoint` and
  `Proceed`: checked before every named step, before every save of an in-flight record and
  right after every server call, before its answer is applied, so an answer that arrives
  after the crash is dropped with its in-flight record kept, as a real crash loses it). From
  that moment nothing is serialized: a group commit still waiting fails without serializing
  (its waiters send nothing), and the failure is thrown only after every unit has stopped and
  the writes serialized before it are on disk. The document on disk therefore never holds a
  step half done (an answered lock or a created file still in flight), which no real crash
  could leave, and a crashed engine never saves, sends or writes after it threw
  (`ConcurrencyTests.A_crash_among_files_moving_at_once_...` holds the serialization count
  across the crash and the saved records, with a 40 ms disk). A cancellation (the engine
  stopping) is not such a failure: every unit stops between two steps, so what is in memory
  may still be saved. Folder, project and move work (phase A) stays one step at a time; the
  check outs and releases of phase D go several at a time.
- **A storage refusal or timeout is one file's problem.** `BlobClient` throws
  `StorageTransferException` when file storage answers with anything but success, takes too
  long, or cuts a download off, and when ideabosco.com takes too long to sign a transfer; that
  file shows one `cantSend` item ("Plate.SLDPRT didn't go through this time") and goes again on
  the next pass. Only a connection that cannot be made (and the site's 5xx for storage, as the
  client guard tests require) is offline.
- **One request per file body.** A file goes up in one PUT and comes down in one streamed GET
  (its SHA-256 checked as it streams). There is no multipart upload: the contract signs one
  PUT URL with a signed content length (decision D11). Ranged downloads of one file in
  parallel were considered and not built (PROOF.md, "Multipart upload and ranged downloads").

## Saving state

The engine's whole state document (`EngineState`) is replaced atomically on every save
(`IEngineStateStore`). What has to be on disk before what:

- **An in-flight record before its server call.** `SendAsync` records the write in the file's
  state and awaits `FlushAsync()`, a group commit: every unit waiting at that moment (the
  ones whose answers just arrived join after one `Task.Yield`), and every unit that becomes
  ready while the save before is still being written, shares one save. The document is
  serialized once, on the engine thread, when that earlier write is done, and written off it,
  in order. A slow disk therefore means fewer, larger groups, never a queue of writes (50 ms
  saves and 36 new files: 38 saves instead of 106; `A_slow_disk_makes_fewer_larger_saves`).
  The releases of phase D are made ready together and saved once.
- **An id before anything durable carries it.** Ids (captures, removals, check outs, folder
  operations) come from blocks of 1,024: `EngineState.Sequence` as saved is the end of the
  block in use, saved before the block's first id is handed out, so a crash never reuses an
  id and 5,000 captures need five saves, not 5,000. A block whose save fails (the state file
  locked, the disk full) is given back (`ReserveIds(save)`): no id ever comes from a block
  the disk never had, so none is handed out again after a restart.
- **A folder move, or a folder operation, before it happens** (`SaveNow`, rare), as before.
- **Everything else** marks the document dirty and is saved at the end of the pass (or of the
  action) that changed it. A crash replays from the last save: in-flight records are sent
  again with the same operation id, captures not attached yet are attached again
  (`AttachEntries`), a download whose bookkeeping was lost is recognized by its bytes
  (`AdoptIdenticalBases`), cursors are read again.

Serialization costs what changed, not the document's size (`StateSerializer`): file records
are written in blocks of 128 whose bytes are rebuilt only when one of their records changed
(`FileState` setters and its lists mark the record changed; `FileTable` reports records that
come and go), each record's JSON is rebuilt only when it changed, completed journal ids are
written once each, and import summaries (replaced, never changed in place) once each. A save
with nothing changed writes nothing. The pieces go to the store as a list and are written one
after the other. With 5,000 files the document is about 4 MB; a save costs about 0.4 ms on
the engine thread (Debug build) instead of 38 ms. A unit test holds the pieces to the
reflection serializer's document and every `FileState` property to its change tracking.
The longer-term fix is a record per file (or a small log of in-flight records): on Windows
every save still writes the whole document through `FileStateStore` (write-through, then
rename), about 4 MB per group commit at 5,000 files.

**Indexes.** Journal entries by id (`OfflineJournal.TryGet`, its cache), snapshots by id and
by (path, hash) with the size counted at capture (`SizeOf` never reads a snapshot again in a
start), file records by `FileId` (kept by `FileState.FileId` and `FileTable`), each project's
live files by name (for "shares a name", built once per read). Nothing per file scans all
files.

## Activity (v2-design.md 4.4)

Since 0.3.2 the tracker also keeps the running lines (`ActivityView.log`): the last 40 things
Armory did, from the last 3 minutes, each one plain sentence ("Downloaded Plate.SLDPRT
(612 KB)", "Getting 1,400 files ready to check out", "Asking the server to check out 1,400
files", "Checked out 500 of 1,400 files", "Checked in 500 of 1,400 files", "Force checked in
160 of 200 files", "Sync finished: 94 files downloaded.", going offline and back). The window
shows them in Right now, the newest at the foot, so a long operation shows it is working.

`ActivityTracker` keeps, per direction (Uploading, Downloading, Moving), files and bytes done
and in all, the speed and the time left, the files moving now (at most 8 listed) and the
waiting line. Phase B tells it what the pass will move; each transfer is an
`IProgress<long>` that `BlobClient` reports bytes to (from any thread); a file counts once it
is where it goes, and a file that did not go leaves the totals. Speed is an exponential
average over about 5 seconds of `TimeProvider.GetTimestamp` time (bytes and files), counted
from the moment the direction's first file started (never from when the pass planned the
files) and corrected for the time it has had: both averages start from zero and are divided
by the weight they gathered (1 - e^(-t/5 s)), so a steady rate reads true from the first
seconds instead of starting at nothing and climbing (which showed about twice the true time
left for the first 10 seconds). Time left is the larger of bytes left over byte speed and
files left over file speed, shown after 3 seconds and 2 files; a steady run is within a
quarter of the truth from 3 seconds on (`Time_left_is_close_to_the_truth_from_three_seconds_on`).
Lines are the brief's words, sizes and times as the window writes them: "Downloading 412 of
1,280 files, 2.1 GB left, about 3 min", "Uploading 3 of 9 files, 48 MB left, about 20 sec",
"Checking in 412 of 4,900 files" (the locks a check in or an add lets go of after the
uploads, shown as the upload direction), "Moving 120 files to Robot 2027 › Gearbox". Moving
is one operation from its start to its end, with its own count and its one target: the
team's answer to a folder renamed here or in the window, the move on this disk and the
records following it; the team's rename made here; a folder or project folder put back; the
team's file moves of one pass (to the folder they share). A direction shows while files are
on their way; `ActivityView.Line` is the line of the one with the most files left, and the
status line (`SyncView.Line`) follows it while files move. Waiting: "3 files are waiting
to upload. They upload when this computer is back online." (offline or paused) or "2
checked-out files have changes. Check them in to share them." (never for an archived project).
`ActivityChanged` is raised from a timer, at most four times a second while a pass runs (or
a window action moves a folder) and once more when it ends; the host posts `{type: 'activity', activity}` without the whole view
(the page patches the panel and the status line in place), and the full view is rebuilt at
most every 500 ms during a pass and at its end.

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
  and open" (C5's "Check out and reopen") then opens a file that isn't open, and makes one
  SolidWorks has open writable there through the SolidWorks link, or opens it again once it
  is closed ("SolidWorks opened a file" below). A folder is every live file under it: "Checked out 12 of 14 files.
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
  what was not checked in (`lockBroken`) and shows one notice. Several files at once go in
  `armory_break_locks` calls instead (0.3.3, see "Force check in of many files" under v0.3
  below), and those calls do have a durable record.
- **Moves and removals** take the lock only for themselves (`FileState.TransientLock`) and let
  it go as soon as the move or the removal is done, whatever is on disk; a removed file's
  lock is always let go. Core plans a file under such a lock as nobody's
  (`LockOwnership.Free`), so bytes saved without a check out before a rename are one kept copy
  ("changed without a check out") and the checked-in version comes back, never a shared
  version nobody checked in.

### Every lock this computer holds is its check out (v0.2.1)

The field report: a mentor added a part; a student checked it out before it had downloaded
to his computer while he was reorganizing folders, and afterwards nobody could check it in.
What the end-to-end reproduction (`StuckCheckOutTests.Checked_out_before_it_downloaded_while_its_folder_moved`)
found, and what changed:

- **A download never makes a moved folder again.** Files planned before the student renamed
  their folder in Explorer were written to the old path, which made the old folder again;
  the next pass took those files for files the student had moved back, and sent them back to
  the old folder for the whole team (each under a lock taken for the move). A download now
  checks, just before writing, that the nearest folder the scan saw is still on disk
  (`FolderMovedAway`, `IVaultFileSystem.FolderExists`); if not, the file waits for the next
  pass, which downloads it where its folder is now.
- **A lock with no record here gets one** (`AdoptMyLocks`, after every read of the server and
  before every check in or undo): any live lock held by this device or a former device id is
  given a record at the server's path, so the row says "Checked out by you", My files lists
  it, and Check in and Undo work, downloaded or not (an undo of a file that never came down
  only lets the lock go).
- **Check in and Undo find a file by the server's path too** (the window shows the server's
  path), not only by where its record is here.
- **A local move whose file is gone from the new path stops waiting.** It kept the file out of
  every plan, and a check in of it waited forever.
- A check out that can't bring the copy up to date answers so and takes no lock (D18), as
  before; the test holds that too.

## Folders and projects (C2 to C6, D8, D16, D17; v2-design.md 4.3)

The server keeps files, not folders: a file's folder is a string, matched exactly (case
included) by `armory_rename_folder` and `armory_delete_folder`, so the engine always sends a
folder spelled as `armory_project_files` lists it, never a re-cased disk name. A refusal is
SQLSTATE 55006 with DETAIL `{reason: checked_out | target_exists, names, total}`; who has the
files checked out is named from this computer's own lock data (each file's lock holder and
device from `armory_project_files`, read again when this pass's copy names nobody), never
from DETAIL. A folder call that races a check out can deadlock; the client resends 40P01 and
40001 with the same operation id, and anything else that is not an answer is sent again with
the same id on the next pass.

- **A folder renamed on this disk** (`VaultScan.FolderMoves`, proven by directory identity
  and in an order that applies one by one; without it, a known folder that is gone whose
  every file is found byte for byte at the same place under one new folder of the same
  project) is ONE `armory_rename_folder`, never a move or a removal per file. Its records
  (file states, pending moves, known folders, imports) follow the disk at once and the
  operation is durable in `EngineState.FolderOps` before the call, which goes after any file
  write a crash left in flight (that write lands in the folder as it was, and the rename
  takes it along). Files without a server record carry on and are added at the new path.
  Until it is sent nothing under the old or new folder is planned or moved file by file.
  Renames and removals keep the paths they had when they happened and are sent in that
  order, each worked out from the server as the one before left it (read again after every
  call): a folder and a folder inside it renamed together are two renames, and a swap goes
  through the platform's temporary name ("Left (moving)"), never two refused renames. "No
  files under the old name" counts as sent only from a fresh read; when the files are under
  one other folder because the team renamed it first, the whole folder here follows the
  team's name (files not in Armory yet included) with one notice, "Gears is now Box: someone
  renamed Gearbox first". A rename onto a name whose old files still have records here (a
  folder removed here and not yet removed for the team) is put back at once, so no file's
  record or unsent saves ever pass to another file. Refused, the folder is moved back
  (`MoveFolder`, as soon as nothing inside is open) with ONE `folderPutBack` notice: "Gearbox
  was put back: Maria Lopez has 2 of its files checked out." While it waits the card says so
  ("Gearbox goes back once Housing.SLDPRT is closed: ..."), and the files Armory knows there
  keep every save and the read-only rule (a new file there waits until the folder is back).
  A folder moved out of its project (into another one, or to the top of the Armory folder)
  is put back too when it holds files the server has: folders move only inside their
  project, and its records stay at home while it is away. A folder of files Armory doesn't
  have yet goes where it was moved: into another project, its files are added there; out of
  every project, they are outside Armory (their saves already kept stay on this computer).
- **A folder deleted on this disk**: a known folder (one that held files the server has, on
  this computer) gone for two consecutive scans, every file the server has there missing and
  looked at again right before the call, is ONE `armory_delete_folder` (unsent saves under it
  are kept on the server first, as earlier saves). Nothing under it is planned file by file
  meanwhile, so one bad scan never removes anything and no per-file removal is ever sent for
  it. The project's files are read again right before the call, and the folder is removed
  only when every live file the team has in it is one this computer had at the team's
  current version: newer work (a version checked in since, a file this computer never had)
  is never removed by a folder gone from this disk, the same rule Core applies to one file.
  Otherwise the folder comes back with its files, newer ones included, and ONE notice: "Gearbox
  was put back: Maria Lopez has newer work in it." Deleted again once this computer has what
  the team has, it goes in one call. (C6 takes no expected versions, so a check in landing
  between that read and the call, milliseconds apart, is the one race left; the contract is
  frozen.) Refused, the folder is made again and its files downloaded, with ONE notice naming who.
  `armory_delete_folder` leaves the caller's own check outs on the removed files; the pass
  lets them go like any removed file's.
- **The team renamed a folder** (`folder_renamed` in the change feed, or every file of a
  folder moved under one new folder): ONE local `MoveFolder`, nothing downloaded, nothing to
  recovery. When that can't be done in one step (something inside is open, the new folder is
  already here) each file moves on its own, an open one once it closes, and the emptied
  folder goes. The team removed files: each goes to recovery as before; one that is open here
  waits and says so ("Housing.SLDPRT was removed from Robot 2027", its row "notInArmory"),
  never "a newer version is waiting".
- **Empty folders (D17)**: a known folder with no file here, no live file on the server, no
  work waiting under it and no folder a student made inside it is removed
  (`DeleteEmptyFolder`), on every computer. A folder a student makes (in the app or in File
  Explorer), empty or not, stays, and so does every known folder it is in.
- **Folder moves on this disk are durable.** Every folder the engine moves (the team's
  rename, the window's rename, a project renamed on the site, a folder or project folder put
  back) is saved as started (`EngineState.MovingFolders`) before `MoveFolder` and as finished,
  with its records following, in one save after it. A stop in between is finished on the next
  start, before the scan's files are read: when the disk shows the folder at its new place and
  not at the old, the records follow; otherwise nothing moved and the work that asked asks
  again. Crash points right after each move: `after-team-folder-move`,
  `after-app-folder-move`, `after-project-folder-move`, `after-putBack-folder-move`,
  `after-projectPutBack-folder-move`. As a second guard, a record whose server file lives at
  another path where this computer has the same bytes follows it there, and is never sent as
  a removal.
- **The project's folder** (`ProjectState.Folder`) is no longer reset to the project's name.
  A project renamed on the site moves its folder in place once nothing inside is open, and
  every record follows: no download, no removal, no second folder, check outs go on. Until
  then the project syncs in its old folder, with one `projectRenaming` notice: "Close
  Plate.SLDPRT to finish renaming Robot 2027 to Robot 2028". A project folder renamed in
  Explorer, or dragged anywhere (into another project's folder too), is moved back
  (`ProjectState.PutBackFrom`, durable), with "Project names are changed on ideabosco.com.";
  while a file inside is open it waits, the project's folder is never made again beside it,
  and none of its files is ever added to another project. Meanwhile its records stay at the
  project's folder: a file there keeps every save (recorded under the path it goes back to)
  and the read-only rule (a file taken back while it waits is read-only at once). A project
  folder removed in Explorer is made again and its files downloaded: a missing project folder
  is never a removal, per file or per folder.
- **Archived projects (D8, addendum 7)** are skipped silently: not read, not planned, no
  notices, their folders and read-only bits left as they are. A check out this computer has
  in one stays in My files and can be checked in (the project's files are read for that
  alone). Restored, the project syncs again from its change cursor.
- **Window actions.** New folder makes the folder here (the server keeps no empty folders).
  Rename folder and Delete folder go to the team first, one call each (refused while someone
  else has a file in it checked out, naming who; Delete folder also while a file in it is not
  in Armory yet), then here: one move (files not in Armory yet included), or recovery and the
  folder removed with every folder in it. Each is durable in `EngineState.FolderOps` with its
  own operation id before the call: a lost answer ("You're offline. Armory renames Gearbox to
  Gears as soon as this computer is back online.") is finished by the next pass, the same id
  answered from the server's receipt and the move here made from the same record. Before any
  pass since a start, a folder action runs one first, so it knows what is on the disk. Add files
  copies files and whole folders through `CopyIn` (never over anything there; a link, an
  ignored name or a source being written is refused), then a pass adds them.
- **The import summary.** A bulk add (at least 10 files new to this computer and to the
  server under one new folder in one pass, or one Add files) is ONE `import` notice,
  "Added 46 of 60 files to Robot 2027 › Pack", counting what is in Armory now, what shares
  a name and the rest; the files that share a name are ONE `nameShared` card with one item
  each. New files in the folder of an import that grew in the last 10 minutes join it,
  however few (an unzip seen over several passes is one import). A file removed since, or
  gone from the disk with nothing left to send, no longer counts, so an import whose folder
  was deleted shows nothing waiting. Notices are grouped by kind (one card per kind, however
  many files).

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
  is on disk, so the removed bytes never come back (`RevivalTests`). The change feed is read
  for that only when the id the server answered is one this computer has seen (a new id is a
  new file). A live name clash is one "shares a name" notice item: looked up first in the
  project's files this pass already read, so it costs no call (a Pack and Go with a hundred
  shared names sends no `armory_create_file` for them, on any pass); a clash the read could
  not show (SQLSTATE 23505) is the same item. A file never added because its name is taken,
  then deleted from the disk, is forgotten: its saves stay in this computer's safe copies,
  and nothing waits or is retried for it.
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
the total): `import` (one item per bulk add), `nameShared`, `cantSend`, `cantRead` (disk
problems, and a stale SolidWorks marker), `newerWaiting`, `keptCopy`, `takenBack`,
`folderPutBack` (a refused file or folder rename, or a refused folder removal, put back;
several in one card are titled by what they are and, when that is why, who has files in them
checked out: "2 folders were put back: Maria Lopez has files in them checked out", each item
saying its own reason),
`projectPutBack` (a project folder renamed or removed in Explorer, put back) and
`projectRenaming` (a project renamed on the site, waiting for a file to close) and, since 0.3.3,
`newerRelease` (the team's version of a file saved in a SolidWorks newer than its project's pin,
"The SolidWorks year"). "SolidWorks year not checked" is never a notice, only a tag on File detail; waiting
to upload is activity, never rows. My files are the files this computer has checked out, in
any project: a lock taken only for an add of a closed file (the pass checks it in) or only
for a move or a removal is not listed, so a 5,000-file import lists nothing there while its
files go in; a file added while it was open is, with "You added it while it was open", until
it closes. Every row says who has it checked out ("Checked out by you", "Checked out by
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
The activity panel's words are in Activity above. No view field carries a season.

## The SolidWorks year (0.3.3)

`SyncEngine.Releases.cs`; the reader and the rule are Core's (docs/core/solidworks-version-gate.md).

- **Two sources, for the exact bytes.** The file reader (`EngineDependencies.ReleaseReader`,
  `SolidWorksSavedReleaseReader` in the agent; it was `null` until 0.3.3, so every SolidWorks
  file was "release not checked" and a Warn project took anything, a 2026 part included) and
  the SolidWorks link's stamp for that content hash (`RecordReleaseStamp`). Core's
  `SavedReleaseRule.Combine` decides: one known year wins over unknown, two that differ are
  unknown, and the disagreement is a flight-recorder note ("release disagreement") and a log
  line, once per hash. The gate (Core) then decides as before: a year newer than the pin is a
  private draft in both modes, unknown is "release not checked" in Warn and a private draft
  in Enforce.
- **Read once per content hash, off the engine thread.** The reader runs on the thread pool
  (`Task.Run`); the engine thread, which the window only ever waits on, is never busy with it.
  Answers are kept by hash for the engine's life; a year read for the team's version of a
  file (B5) is also kept on the file's record (`FileState.ReleaseHash`, `ReleaseYear`), so no
  later start reads those bytes again. An unknown answer is kept only in memory (a newer reader
  may place it after an upgrade). Only bytes that changed are read for an upload (a file whose
  hash equals its base is never read), from the capture's snapshot. A stream that fails is
  not an unknown year: the path waits for the next pass with a "can't read" notice, never an
  upload marked "release not checked".
- **Stamps** (`EngineState.ReleaseStamps`, by SHA-256): kept in the state document, written
  once each (StateSerializer), never sent to the server (only the resulting year is, through
  `armory_commit_version_with_release` and `armory_save_side_version_with_release`). A stamp
  whose hash is not a SHA-256 is refused; a second stamp for the same bytes replaces the first
  unless their years differ, which makes the year unknown (`SavedReleaseRule.Merge`). When
  those bytes reach the server from here (a commit, a kept copy, an earlier save), the stamp
  is marked committed; it is pruned 30 days after that, or 90 days after it was recorded if
  they never do. A stamp wakes the loop: a file held as unknown may go now.
- **What runs here.** The SolidWorks link's `LinkAttached` and `LinkDetached` records (or, with
  no link, `SolidWorksAttached(revision, saveDownWorks)` and `SolidWorksDetached()`) say which
  SolidWorks runs here; Core's `SaveDown.Plan` and the link's `SaveToVersionSupport` then say
  whether this computer saves down to a project's pin. The words use it, and the link sets
  Save to Version by it ("The SolidWorks link" below).
- **Words** (research section 6). A file newer than the pin: "Saved in SolidWorks 2026, and
  Robot 2027 uses SolidWorks 2025. Open it in SolidWorks 2026 and click Save: Armory saves it
  as 2025, then it uploads by itself." only where saving down works; otherwise "... It stays
  on this computer only until it is saved in SolidWorks 2025.", followed, when the link says
  why, by "Update SolidWorks 2026 to Service Pack 3 or newer so Armory can save it in 2025.",
  "SolidWorks 2028 can't save files as 2025." or "SolidWorks on this computer couldn't save it
  in 2025. Ask a CAD lead or a mentor what to do." The bytes never leave this computer: the
  gate refuses them before any server call, and an earlier save of them is kept as a private
  draft, never an "earlier save, kept" (`ReleaseTests`).
- **B5: files uploaded before the reader.** At the end of each online pass (phase D), every
  SolidWorks file whose current server version has `release_checked` false and whose copy here
  has the same hash is read, once per hash. A loop pass reads for at most 2 seconds and gives
  way to a window action; the next pass goes on. A window action's own pass reads none. The view then carries the year on the row and
  File detail (`savedRelease`, `newerThanPin`), a count per project (`newerThanPinCount`) and one
  `newerRelease` notice (docs/agent/BRIDGE.md): needing the student (`look`) on a computer that
  can fix them or that no link describes, news (`info`) on one the link says cannot. The
  server's rows stay as they are (immutable): the year lives on this computer.
- **Cost, measured** (`ReleaseTests.A_pass_over_1500_synced_files_reads_nothing_and_is_not_slower`,
  on this Linux test host with the portable file system, 2026-10-09): over 1,500 synced
  SolidWorks files a steady pass reads nothing and took 73 ms with the reader and 71 ms without
  (82 ms after a restart with the reader); the one-time B5 pass that read all 1,500 (and
  downloaded them) took 2.7 s against 2.4 s for the same first pass without the reader. Real
  files cost about 0.6 ms each to read (158 public files in 94 ms, docs/spike/saved-release.md).

## The SolidWorks link (0.3.3)

`SyncEngine.Open.cs`; the link is `EngineDependencies.SolidWorks` (`ISolidWorksLink`,
`SolidWorksLink.cs`; on Windows `Armory.SolidWorks.SolidWorksLink`, docs/agent/SOLIDWORKS.md;
in tests an in-memory fake). Null-safe: without it everything below falls back to SolidWorks'
`~$` markers, as before.

- **One pump.** `Start()` starts a task that reads the link's records in order and handles
  each on the engine thread (`OnLinkRecord`), then asks the link to `Replay()` what is true now
  (an engine started after a vault root change missed the earlier records). A record that
  fails is a flight-recorder exception and a log line, never the end of the pump.
  `StopAsync` waits for it. Tests wait on `LinkSettledAsync()`.
- **What the engine keeps.** The attached SolidWorks sessions (the newest one is "the running
  SolidWorks" for the words), the ones that ran as administrator (`LinkRefused`), and each open
  vault document per session: type, read-only, a newer year (`IsFutureVersion`), whether the
  student opened it, whether it changed, its last compatibility check, whether the student
  keeps it here, and the save in progress. A document open in a linked SolidWorks counts as
  open for write safety (`IsOpenNow`) beside the platform's own answer.
- **Pins.** After every read of the server that changed them, each project's folder, pinned
  year and gate mode go to the link (`SetPins`), so it sets Save to Version for the active
  document (Core's `SaveDown.Choose`).
- **Saving holds.** `LinkSaving` holds that path: no capture and no plan for it until
  `LinkSaved`, `LinkSaveCanceled`, the document's close or two minutes (`SavingHeldFor`), so
  the bytes are decided with their stamp. A hold never stops other files.
- **Stamps.** `LinkSaved` and `LinkStamped` carry a `ReleaseStamp`, kept exactly as
  `RecordReleaseStamp` keeps one ("The SolidWorks year" above), and wake the loop.
- **An unverified save down stays here** (strengthens the gate). A stamp whose save had Save
  to Version on and whose year SolidWorks didn't confirm (`SavedReleaseRule.UnverifiedSaveDown`)
  makes the gate Enforce for those bytes in a Warn project too (`GateModeFor`): a private
  draft, never "release not checked". The refusal words come from the link when it knows
  better (`LinkGateWords`): an unverified save down, a document blocked by SolidWorks' own
  check (in its words), or one the student keeps on this computer.
- **The `solidWorks` notice** (one card, an item per document, `LinkNotices`): SolidWorks ran
  as administrator; a save down SolidWorks canceled ("isn't saved yet", `bad`); a file newer
  than this computer's SolidWorks (B5 on a 2025 computer); a blocked document; a document kept
  here (button "Save it in 2025 now", `saveDown`); a checked-out document saved in 2026 that
  this computer can save down (the same button); and, before the save, what a save down drops
  (button "Keep this file on this computer only", `keepLocal`), each drop list once (answered
  or saved with, it doesn't ask again). The words are in docs/agent/SOLIDWORKS.md section 3.
  The drops question is also `SaveDownPrompts`, for a notification with "Save in 2025" and
  "Keep this file on this computer only" (`AnswerSaveDownAsync`).
- **The actions.** `KeepLocalAsync(paths, keep)`: "Plate.SLDPRT stays on this computer only
  when you save it. Nobody else gets those changes until it is saved in SolidWorks 2025."
  `SaveDownNowAsync(paths)`: "Saved Plate.SLDPRT in SolidWorks 2025.", or why not ("Check out
  Plate.SLDPRT first: SolidWorks has it read-only.", "Open Plate.SLDPRT in SolidWorks first.",
  the cases of SOLIDWORKS.md section 3); 15 seconds per file at most.
- **Settings line** (`AgentView.solidWorks`, docs/agent/BRIDGE.md): none, attached ("Linked to
  SolidWorks 2026 SP4.1. It saves team files in 2025."), cantSaveDown (with why) or
  administrator; null with no link.
- **Tests** (`SolidWorksLinkTests`, end to end with the fake link): the C5 questions and check
  out and reopen (below), a save down uploaded by its stamp, a blocked document a private
  draft in both modes, an unverified save down never uploaded as 2026 in Warn, the drops
  question and keeping a file here, and the Settings line.

## SolidWorks opened a file (C5, 0.3.3)

Nothing is ever checked out because SolidWorks opened a file: Armory asks. Core decides what
to ask (`OpenAsk.Decide(ownership, openedReadOnly, topLevel, tracked)`): nobody has it,
CheckOut; this computer has it but SolidWorks opened it read-only, Reopen; someone else,
HeldByOther (who); the same person on another computer, HeldOnMyOtherComputer; a reference an
assembly or a drawing loaded, or a file the team doesn't have, nothing.

- **With the link**, only documents the student opened (SolidWorks' active document at the
  open, or one that became active; never a reference) ask, once per document per SolidWorks
  session (`OpenAskOnce`, keyed by path and process), grouped when asked within 1.5 seconds of
  the group's first (`OpenBursts.LinkGroups`: a multi-select open from File Explorer is one
  notification). The window's question (`AgentView.prompt`) and `OpenWithoutCheckOut` take
  their files from the link too, so opening an assembly asks about the assembly.
- **Without it**, the `~$` markers as before; for notifications, markers first seen within 3
  seconds of the previous one, for at most 60 seconds, are one burst
  (`OpenBursts.MarkerBursts`).
- **`OpenPrompts`**: `OpenPrompt(Path, Name, FileId, CheckedOutBy, ViaSolidWorks, Group, Kind)`,
  ordered by group; prompts with the same `Group` are one notification; a prompt stays while
  its file is open and not checked out here, and a host shows each (Path, Group) once.
  `OpenPromptsChanged` says when the list changed. The notifications themselves are the host's
  (not built here).
- **Check out and reopen** (`CheckOutAndReopenAsync(paths)`, the same as `CheckOutAsync(paths,
  open: true)`). The check out runs as always; then, after the action gate is let go, each
  checked-out file a linked SolidWorks has open is made writable there
  (`ISolidWorksLink.MakeWritableAsync`, 15 seconds at most each; Core's `WritablePlan`: in place
  first, keeping unsaved changes; a reload or a drawing's close and reopen only for a document
  nobody changed; never closing a document, never discarding a change). Without the link, or
  when it couldn't, a file still open is opened again once the student closes it in SolidWorks,
  once, within five minutes of the click (`ReopenWhenClosedFor`); after that nothing opens by
  surprise. A file not open opens as "Check out and open" always did. The answer is one
  sentence: "Checked out Bracket.SLDPRT. You can save it in SolidWorks now."; "Checked out
  Bracket.SLDPRT. SolidWorks still has it read-only. To save, close it in SolidWorks and open it
  again from Armory. Changes made before the check out can't be saved to it."; "Checked out
  Bracket.SLDPRT. Close it in SolidWorks and Armory opens it again, ready to save."; for
  several, "Checked out 3 files. You can save them in SolidWorks now." or which ones are still
  read-only.
- **Tests**: `OpenAskTests`, `WritablePlanTests` (Core); `SolidWorksLinkTests` (end to end):
  only the student's opens ask, once per session; check out and reopen in place with no
  launch; a changed document never reloaded, closed or discarded; an unchanged one reloaded or
  reopened without discarding; someone else's file never made writable and the question says
  who; without the link, a file closed within five minutes opens again once; a burst of markers
  is one question.

## Operation ids (crash safety)

Every server write carries an operation id derived (SHA-256, formatted as a UUID) from a
durable value and the step: a Core journal entry id for `create` (`revive` and the removed
file's id for a revival), `lock#attempt`, `commit#parent#attempt`, `side`, `tomb#attempt`;
a check out's lock from its request id; every answer to a lock or commit spends the
attempt. A release uses the lock's holder and acquisition time, a take back the check out it
ends and the computer asking, an `armory_break_locks` call the take back ids of its files (in
id order), and a move a persisted id. A folder rename or removal made on
this disk derives its id from its own durable operation (`EngineState.FolderOps`) and the
server's spelling of the folder. The in-flight record is written before every write the
engine resumes (all but a single file's take back, which the mentor asks again; an
`armory_break_locks` call has its record in `EngineState.ForceCheckIns`); a crash at any point
replays the same id and the server returns its receipt, so a save becomes exactly one
version. The window's Rename folder and Delete folder derive theirs from their own durable
operation too, so a lost answer or a stop is finished by the next pass with the same id
(`before-folder` and `after-folder` are their crash points, as for a folder renamed or
removed on this disk).

## v0.3 (idea-app 0233)

The binding spec is idea-app `docs/ARMORY.md`, "The v0.3 server contract (migration 0233)",
especially "What the Windows app must do". `SyncEngine.Purge.cs` and `SyncEngine.Batches.cs`
hold the new paths; the client half is in docs/agent/CLIENT.md section 6.

- **Live updates.** After every read of the server the engine tells `EngineDependencies.Live`
  (a `RealtimeFeed`) which projects it read (usable, not archived); the feed keeps one channel
  per project, each filtered by `project_id=eq.<id>`. An event wakes the loop
  (`OnLiveChange`), which then reads the server as it always does: an event never changes local
  state by itself. The poll (2 and 10 seconds) stays the floor, so a dropped socket costs only
  latency. The feed runs beside the loop, off the engine thread, and stops with it.
- **Force check in** shows exactly when the server's `can_take_back` is true
  (`ProjectState.TakeBack`; a server older than 0233 falls back to mentor or CAD lead).
  `armory_break_lock` is sent exactly as before (this computer's device); its refusal is still
  P0001 "only a mentor or cad_lead may break a lock", answered "Only a mentor or CAD lead can
  force a check in."
- **Force check in of many files** (0.3.1; `TakeBackAsync(IReadOnlyList<Guid>)`, the bridge's
  `takeBackAll`) is one action: the files someone else has checked out are sorted from the rest
  once, the locks are broken, and ONE pass follows for every file broken (and any the server
  would not take back as asked, to read them again). Before 0.3.1, the window sent one action
  per file and each ran a whole pass, so a few hundred files took the better part of an hour.
  - **In batches** (0.3.3, idea-app 0234, `SyncEngine.Batches.cs`): `armory_break_locks`, at
    most 500 files a call (`ArmoryApi.Chunk`: distinct, in id order, the calls in id order too).
    A call's operation id derives from the take back ids of its files (each one's check out and
    this computer), so asking again for the same check outs answers from the server's receipt
    and writes nothing. Each call's record (`PendingForceCheckIn`: the id, the device, and each
    file with the check out it ends) is saved before the call and dropped once its answer is
    applied. A stop or a lost answer in between leaves it, and the next online pass sends it
    again with the same id only while every file still has exactly that check out (the call did
    not land, or landed with only refusals, which the receipt answers again); otherwise it is
    dropped unsent, so a check out nobody asked about is never ended. Crash points:
    `before-break-batch` (after the record is saved), `after-break-batch` (before the answer is
    applied).
  - **Every answer is read.** `broken: true` is force checked in; `broken: false` means nobody
    had it checked out any more; a refusal is told by its code and words: "only a mentor or
    cad_lead may break a lock" (or a 42501) is a role refusal, "not a project member" asks
    whether the project is gone, as anywhere else, and anything else is reported in the
    server's words and read again in the pass. A whole call the server rolled back with 40P01
    or 40001 is resent by `PostgrestClient` with the same body (up to 3 times), as for the
    other batches; a file the batch answers with 40P01 or 40001 (its savepoint rolled back)
    goes again in a later call of its own files, with a new id, up to as many rounds, and is
    then "busy on the server". A whole call refused otherwise (this computer's device is not
    the caller's) counts its files as refused.
  - **One sentence.** "Force checked in 212 files. Anything that wasn't checked in is kept as
    its holder's own copy. 3 files weren't checked out any more." then, as they apply, "N files
    are in a project where only a mentor or CAD lead can force a check in.", "N files are
    checked out by you: check them in instead.", "N files are in a project you may no longer
    be in.", "N files were busy on the server: try them again in a moment.", "N files were
    refused: the server said ...", singular or plural. "No files were force checked in." when
    none was; "Force checked in 300 of 1,200 files." and "You went offline: try again once this
    computer is back online to finish the rest." when the connection dropped.
  - **A site without `armory_break_locks`** (404 PGRST202) gets one `armory_break_lock` per
    file with the same operation id as one file's Force check in, `TakeBackConcurrency` (16)
    calls at a time (0.3.1's path), and is asked for the batch again in an hour
    (`breakBatchMissingUntil`, kept apart from the other batches: 0234 is its own migration).
- **No longer a member.** A project gone from `armory_my_projects`, or whose change feed or
  files answer "not a project member" (P0001 or 42501, read alike), is asked about once with
  `armory_project_purged`. The same answer from any other call (a lock, a side version, a
  create, a move) wakes the loop so the next read asks. Null: this person was removed, handled
  as before 0.3 (not synced, its folder left as it is, `ProjectState.Departed`), asked again
  once per start in case it is deleted forever later.
- **Deleted forever.** A time from `armory_project_purged`: every record of the project is
  marked `Purged` (unsent saves are done with: there is nothing left to send them to), pending
  moves and folder operations for it go, and one line says so (notice `projectDeleted`, info:
  "Robot 2027 was deleted forever on ideabosco.com, so Armory took it off this computer.").
  Then, every pass (`DropPurged`, right after the first read of the server, offline too), each
  file in the project's folder goes to Armory's recovery folder once it is closed (moved, never
  deleted), then the empty folders, then the project. A `folder_purged` change
  (`{folder, files, file_ids, by}`) marks those files' records `Purged` the same way, quietly
  (the log says so). A purged record is never captured, planned, shown or sent: a file still
  open, even saved again, stays as it is until closed, and a new file at the same path later is
  a new file. A file of a purged project is never called "outside every project".
- **Batches.** Several check outs at once take their locks with `armory_lock_files`, and several
  releases (check ins, undos, adds) go with `armory_release_locks`, one device at a time, at
  most 500 files a call. Every file keeps its own in-flight record (its own operation id), all
  saved together before the call; the batch's id derives from them, so it is minted once per
  action and a resend (40P01 and 40001 are resent by `PostgrestClient`, up to 3 times, with the
  same body) answers from the receipt. Each file's answer is applied on its own: acquired is
  checked out, not acquired is held by someone else, refused is answered in the window with its
  message (plainer words for "not a project member" and "device is not registered to caller")
  and a `cantSend` notice, and what landed is never reported as failed. A stop before the
  answers are applied re-sends each lock alone with its own id (taking a lock this device holds
  answers true); a release in flight is dropped and decided again, as always. A site without the
  batch RPCs (404 PGRST202) gets the files one by one and is asked again in an hour. A single
  file still uses `armory_acquire_lock` and `armory_release_lock`. Crash points:
  `before-lock-batch`, `after-lock-batch`, `before-release-batch`, `after-release-batch`, and
  for Force check in of many files (`armory_break_locks`, above) `before-break-batch` and
  `after-break-batch`.

## Schedule (contract section 4)

The loop looks for the team's changes every 2 seconds while online and active
(`ActivePollInterval`), and every 10 seconds after two minutes with no local or remote
change (`IdlePollInterval`). It was 5 and 60 seconds until v0.2.1; students saw a check out
or a new version take up to a minute to reach another computer, and the requirement now is
no dead zones. A look that finds nothing new costs two small server calls
(`armory_my_projects` and one `armory_list_changes` per project); a project's files are read
again only when its change feed moved. Since v0.3 Supabase Realtime on the change feed wakes
the loop the moment a row is written (see v0.3 above); the poll stays as the floor. A disk hint
(`Wake`) runs a pass at once. Offline, it retries on the idle
interval. While paused, no pass runs and the window's actions say so.

## The loop and the window's actions (v0.2.1)

Until v0.2.1 every window action waited for the pass gate and then ran a whole pass of its
own: with a couple of thousand files downloading, a click waited minutes, and its own pass
moved everything again. Now:

- **An action never waits behind a whole pass.** An action counts itself while it waits for
  the pass gate (`EnterActionAsync`). While one waits, the loop's pass starts no new unit in
  phase C: the units in flight finish, the rest are simply left (they are planned again from
  scratch by the next pass; online, nothing of them is journaled), and phase D still runs
  (the check ins, undos and check outs already asked for, `TidyFolders`, the read-only rule).
  The gate then goes to the action.
- **An action's pass is scoped** (`PassScope`). Phases A, B and D are whole (a scan of 5,000
  files is about 60 ms, and phase D must see every lock), but phase C runs only the units that
  hold the action's files (by record, by server id, or at or under the folder it named).
  Check in, undo and check out finish in phase D from those units alone. When the action
  ends it wakes the loop, which carries on with everything else at once.
- **Time slices.** A loop pass starts no new unit after `PassSlice` (8 seconds) of phase C,
  finishes the ones in flight and its phase D, and the loop starts the next pass at once, so
  the server is read again (others' check outs and versions appear) at least every 10 seconds
  or so even during a bulk download or upload. The activity panel keeps its counts across
  these slices ("Downloading 412 of 1,280 files").
- `SyncOnceAsync` (tests, and anything that wants everything done) is still a whole pass:
  it neither gives way nor slices.

`ResponsivenessTests` holds all three: a check out answers in about a quarter of a second
while 200 files download (the whole download takes about 9 seconds), a check in answers in
about half a second while 200 files upload, and a check out made on another computer shows
on a row about 8 seconds later in the middle of a 20-second download.

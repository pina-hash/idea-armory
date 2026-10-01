# Armory.Agent.Engine

The sync loop. Platform-neutral (`net10.0`), it composes `Armory.Core` (every decision),
`Armory.Client` (the network), `Armory.Storage` (content keys) and the platform interfaces
in `Platform.cs`. It runs on Linux in the end-to-end proof with a portable vault file
system; on Windows, `Armory.Agent` supplies `WindowsVaultFileSystem` and the durable
stores from `Armory.Platform.Windows`.

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
});
engine.ViewChanged += view => bridge.Post(BridgeMessages.ViewMessage(view));
engine.Start();                       // background loop on the contract's schedule
await engine.SyncOnceAsync();         // one full pass (tests drive the engine this way)
engine.Pause(); engine.Resume(); engine.Wake();
await engine.GetFileDetailAsync(fileId);
await engine.MoveAsync(from, to);     // a rename through armory_move_file
await engine.StopAsync();
```

## One pass

1. **Identity.** No session: the view says signed out. The state document is bound to the
   first email and device that sync into it; another account sees "this vault belongs to
   someone else" and nothing syncs.
2. **Finish what a crash interrupted.** Each file's state may hold one in-flight server
   write (create, lock, commit, side version, release, tombstone, move), persisted before
   the call with its operation id and arguments. It is re-sent with the same id, so the
   server answers from its receipt, and its result is applied.
3. **Scan and capture.** The platform scan (ignore list applied) gives every file's hash.
   A hash that differs from the base and from the last capture is a save: `SaveRecorder`
   persists the bytes as an immutable snapshot and journals a Core `Upload` intent.
   `SaveRecorder.Recover` re-journals any capture a crash left unjournaled.
4. **Online check and refresh.** `armory_my_projects`, then per project
   `armory_list_changes(cursor)` (the cursor is persisted after processing) and
   `armory_project_files`. A `lock_broken` change naming this device records the
   preservation obligation. A file whose server folder or name changed is moved locally
   (never while open).
5. **Archive superseded saves.** A journaled save whose bytes are no longer on disk is
   kept as a side version ("Earlier save, kept"), through Core's release gate.
6. **Plan with Core.** For every path: `Reconciler.Plan(SyncInput)` with base, local hash,
   remote revision, lock ownership, open state (`IsOpenNow`: the platform's check or a
   `~$` marker), online state, the break obligation, the saved release, the project's
   pin and gate mode, and the preserved hash. Offline plans only add journal intents.
   Online plans are executed in order; any failure stops that file until the next pass.
7. **Locks.** A `~$<name>` marker beside a SolidWorks document takes the lock when it is
   free. A lock this device holds is released once the file is closed, its bytes are on
   the server, and nothing for it is waiting. Files someone else holds are read-only.
8. **View.** The window's `AgentView` is rebuilt.

The engine re-decides nothing Core decides. It executes `Download` only after rechecking
that the file is closed and unchanged, and `SaveSideVersion` and `Upload` only from
immutable snapshot bytes.

## Operation ids (crash safety)

Every server write carries an operation id derived (SHA-256, formatted as a UUID) from a
Core journal entry id and the step: `create`, `lock#n`, `commit`, `side`, `tomb#n`. A
release uses the lock's acquisition time, and a move a persisted id. The in-flight record
is written before the call; a crash at any point replays the same id and the server
returns its receipt, so a save becomes exactly one version.

## Schedule (contract section 4)

The loop polls every 5 seconds while online and active, and backs off to 60 seconds after
two minutes with no local or remote change. A disk hint (`Wake`) runs a pass at once.
Offline, it retries on the idle interval.

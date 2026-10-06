# Armory server contract

These SQL files are a draft, not an idea-app migration. The app's migration lane must assign a number after review. Every write is a security-definer RPC with an empty search path; clients have read-only, row-level-security access.

Every write call carries a stable `operation_id`. The first call stores its typed JSON result in `armory_operation_receipts`; a replay by the same caller and RPC returns that result without another write. Reuse by another caller or RPC is refused. Concurrent deliveries serialize on the operation ID. Receipts can be read only by the named caller through RLS, and direct writes are revoked.

A caller first registers each installation with `armory_register_device(name, operation_id)`. The returned UUID belongs to the email obtained exclusively from `current_user_email()`. Every lock-related call validates that ownership. A lock holder is the pair `(email, device_id)`, and break changes preserve that exact former pair in both the lock row and change-feed payload.

| RPC | Rule | Core action |
|---|---|---|
| `armory_register_device(name, operation_id)` | Registers and returns a caller-owned device UUID. | device enrollment |
| `armory_acquire_lock(file, device, operation_id)` | A member device atomically takes the one lock; one racer wins. | `AcquireLock` / `AcquireLockThenUpload` |
| `armory_release_lock(file, device, operation_id)` | Only the live holder pair releases. | close after `Synced` |
| `armory_break_lock(file, device, operation_id)` | Only mentor or CAD lead; records the former email and device for exact notification. | confirmed `RequestBreak` |
| `armory_commit_version(file,parent,key,hash,bytes,device,operation_id)` | The holder pair advances an expected parent. Any nonholder device or stale parent creates a side version. | `Upload` |
| `armory_save_side_version(file,parent,key,hash,bytes,reason,device,operation_id)` | A registered member device preserves work without advancing shared state. | `SaveSideVersion` |
| `armory_tombstone(file,parent,device,operation_id)` | Holder pair only, conditional on the current parent; history remains immutable. | `ProposeTombstone` |
| `armory_allocate_part_number(project,subsystem,season,operation_id)` | Atomically allocates `5669-YY-SSNN` by default; returns `subsystem_full` after 100 numbers. | part creation |
| `armory_list_changes(project,cursor)` | Member-only ordered changes strictly after the monotonic cursor. This read has no operation ID. | sync refresh |

Names have a database unique index on project plus lowercase NFC form. Both shared and side version rows reject update and delete, including byte metadata. No purge operation exists.

Identity comes only from idea-app's zero-argument, `text`-returning `public.current_user_email()`, defined by idea-app migration `0067_admin_tier.sql`. `armory_current_email()` raises when that function returns null. Production SQL has no caller-settable identity seam. The test harness installs its matching `current_user_email()` stub from `tests/Armory.Server.Tests/sql/000_test_identity.sql` before applying the production files; only that test-only stub reads `armory.test_email`. See `server/IDEA_APP_CONVENTIONS.md` for the reviewed precedent.

`authenticated` receives `select` on the eleven `armory_` tables by explicit name, and every one has row-level security enabled. Project data uses membership policies, devices use owner email, and receipts use caller email. The Armory scripts never grant privileges on unrelated `public` tables. Direct writes remain revoked and are available only through authorized RPCs.

## Lane A additions (`004_armory_agent.sql`)

`docs/agent/CONTRACT.md` section 6 names the RPCs lane B builds against. All follow the rules above:
security definer, empty search path, identity only from `current_user_email()`, a receipt and a
change-feed entry for every write. Authorization refusals use SQLSTATE `42501`, invalid names and
values `22023`, a taken name `23505` with a JSON DETAIL `{"existing_folder", "existing_name", "file_id"}`,
and the last-mentor guard `P0001`. Folders and names must pass Core's `VaultPath` name rules, and the
agent's ignore list (`~$*`, `.armory`, `desktop.ini`, `Thumbs.db`) is refused as a name.

| RPC | Rule |
|---|---|
| `armory_create_project(name, season, operation_id)` | `public.is_admin()` only; the caller becomes a mentor. Project names are unique without regard to case, because each is a vault folder. |
| `armory_add_member(project, email, role, operation_id)` | Mentor or CAD lead; only a mentor grants or changes `mentor`/`cad_lead`. Changes an existing member's role; never demotes the last mentor. |
| `armory_remove_member(project, email, operation_id)` | Mentor only; never removes the last mentor. Membership changes serialize on the project row. |
| `armory_create_file(project, folder, name, device, operation_id)` | Any member with a registered device. |
| `armory_move_file(file, folder, name, device, operation_id)` | Only the live lock holder pair; returns false otherwise. Writes `file_moved` with old and new folder and name. |
| `armory_commit_version_with_release(..., saved_release)` | Calls 002's `armory_commit_version` (one source of the lock and parent rules) under a derived operation id, then records the SolidWorks release. A commit to a file someone removed is kept as a side version with reason `file deleted` (`advanced` false), so a save made while the removal was in flight is never lost. |
| `armory_save_side_version_with_release(..., saved_release)` | Same, around `armory_save_side_version`. |
| `armory_set_release_gate(project, 'enforce' or 'warn', operation_id)` | Mentor only. New projects default to `warn`, pinned to SolidWorks 2025. |
| `armory_raise_pinned_release(project, release, operation_id)` | Mentor only; the pin must strictly increase (Core's `TryRaise`). |
| `armory_my_projects()`, `armory_project_files(project)`, `armory_file_history(file)` | Member-scoped read snapshots for the agent. |

The release gate: a SolidWorks file (`.sldprt`, `.sldasm`, `.slddrw`) whose saved release is
known to be newer than the project's pin is refused in both modes; an unknown release is refused in
`enforce` and accepted in `warn`, where `armory_version_releases.release_checked` is false ("release
not checked"). `armory_current_email()` now also refuses idea-app's empty-string identity.
The test harness stubs `public.is_admin()` in `tests/Armory.Server.Tests/sql/001_test_admin.sql`;
production SQL never defines it.

## Contract v2 (`005_v2.sql`, idea-app migration 0232)

Lane W's `supabase/migrations/0232_armory_v2.sql` reached idea-app main (afb370f) before lane A
wrote its own, so `005_v2.sql` is that file byte for byte (its lines 13 to 477, SHA-256
`cba64901279e4469edf1d7f4d91a4ad5e5359c816810f5433ce38d61f5ef48a9`) under a provenance header.
Lane A wrote no function body for C1 to C7. Section 9 at its end is the only text that is not
0232's: the grants production already got from 0231 and 001 to 004 predate. Every helper is revoked
from `public`, `anon` and `authenticated` (002 had left `authenticated` out, and 001's trigger
function was never revoked), and `armory_current_email` and `armory_is_member`, which the RLS
policies name, are granted to `authenticated`. `armory_project_checkouts` reads idea-app's
`public.profiles`; the tests stand it in with `tests/Armory.Server.Tests/sql/002_test_profiles.sql`.
C8 (check out, check in, take back) is unchanged. idea-app recorded 0232 as applied to production in
8b57bad, with the same bytes, so a change to any of its bodies now needs a new idea-app migration.

| RPC | Rule |
|---|---|
| `armory_create_project(name, season, operation_id)` | Same signature. `season` may be null; a given season is still a year from 2000 to 2100 (`22023`). `project_created` carries `season` null. Callers send `p_season` explicitly, null included. |
| `armory_allocate_part_number(project, subsystem, season, operation_id)` | The season is the call's, else the project's, else the current year in America/Los_Angeles. |
| `armory_my_projects()` | Adds `archived` (boolean) and `archived_at` (timestamp or null). `season` may be null. |
| `armory_rename_project(project, name, operation_id)` returns boolean | Mentor only (`42501`). Create's name rules (`22023`). Another project already named that in any case: `23505`, DETAIL `{"existing_name"}`. False when the exact name is unchanged; a case-only rename is true. Takes create's advisory key, so a rename and a create of one name have one winner. Change `project_renamed` `{from, to, by}`. |
| `armory_set_project_archived(project, archived, operation_id)` returns boolean | Mentor only (`42501`); null is `22023`. False when already in that state. Sets or clears `archived_at`. Change `project_archived` or `project_restored`, `{archived, by}`. Nothing is deleted and no other RPC refuses an archived project. |
| `armory_create_file(project, folder, name, device, operation_id)` | Same signature. A name whose only holder is a removed file revives that file: its tombstone row and any lock row are deleted, `deleted_at` is cleared, the requested folder and spelling are set, `current_version_id` is kept, and the same id is returned. Change `file_revived` `{folder, name, old_folder, old_name, released_checkout_of, device_id, by}` (no `file_created`). A live holder still raises `23505` with `{existing_folder, existing_name, file_id}`. Creators of one name serialize on an advisory key, so racing revivals revive once and the loser is told the winner's folder. The reviver commits with the revived file's current version as parent, read from `armory_project_files`. |
| `armory_rename_folder(project, from, to, device, operation_id)` returns int | Any member with their own device. `from` and `to` are non-empty valid folder paths (`22023`), differ (`22023`), and `to` is not inside `from` (case-insensitive, `22023`). Moves every live file whose folder is `from` or starts with `from/` (exact case, no LIKE), as one update, and writes one `folder_renamed` `{from, to, files, device_id, by}` (no `file_moved`). A case-only rename moves the folder. Zero live files: returns 0 and writes no change. Removed files keep their old folder. |
| `armory_delete_folder(project, folder, device, operation_id)` returns int | As above; `''` (the project root) is `22023`. Tombstones every live file in the subtree (tombstone rows carry each file's current version) and writes one `folder_deleted` `{folder, files, device_id, by}` (no per-file `tombstone`). Lock rows stay until a revival clears them. Removed rows keep their folder, so a folder must never be made from removed rows. |
| `armory_project_checkouts(project)` returns jsonb | Members only (`42501`). One element per live file with an unbroken lock, in check-out order: `{file_id, folder, name, holder_email, holder_name, device_name, since}`. `holder_name` is the holder's profile display name, else full name, else null. |

**Folder refusals.** Both folder RPCs refuse with SQLSTATE `55006` (PostgREST answers it with HTTP
500, so clients branch on the SQLSTATE) when any live file in the subtree has an unbroken lock
held by anyone but the pair (caller, `p_device`), so the caller's own other computer counts, and
`armory_rename_folder` also when the target folder (compared without case) already holds live
files outside the moved set. DETAIL is JSON text:

```
{"reason": "checked_out" | "target_exists", "names": [at most 10 file names, sorted without case], "total": n}
```

The message names the count, the folder and the names; the hint says what to do. A refusal stores
no receipt, so the same operation id succeeds once the files are checked in.

**Decisions D6 to D9, as open points for lane W.** Lane A's design notes proposed these before 0232
existed. 0232 is what both lanes now build on; where it differs, 0232 wins and the client reads both.

- D6, revival: matches (tombstone row and lock row deleted, folder and spelling set, history and
  `current_version_id` kept). 0232's `file_revived` payload names `released_checkout_of` and has no
  `version_id`.
- D7, null season: matches, with the year taken in America/Los_Angeles.
- D8, archive: 0232 stores `archived_at`, projects `archived` and `archived_at`, and writes
  `project_restored` when a project comes back (lane A proposed one kind with `archived` false).
- D9, refusal: same SQLSTATE and the same "someone else" rule. The DETAIL is `{reason, names,
  total}` rather than `{reason, folder, count, files: [{file_id, folder, name, holder_email,
  holder_name, device_name, since}]}`; holders come from `armory_project_checkouts`.
  `target_exists` does not cover a target equal to a live file's full path, and `holder_name` is
  the profile name or null rather than the email's local part (the agent derives a name from the
  email when it is null).

**Open for lane W, found while testing 0232 here.**

1. Deadlock. The folder RPCs lock the project row and then the files; `armory_acquire_lock`,
   `armory_commit_version`, `armory_tombstone` and `armory_move_file` lock the file row first and
   the project row second (the change feed's foreign key). A folder operation racing a check out
   in that folder deadlocks (`40P01`, one side rolled back, never a wrong result): 6 to 37 of 60
   free-running races per run in `V2RpcTests.ACheckOutNeverLandsInsideAFolderRenameOrDelete`.
   Serializing folder operations with `pg_advisory_xact_lock(hashtextextended('armory_folders:' ||
   p_project::text, 0))` instead of the project row, and locking the subtree's `armory_locks` rows
   `for update` (in `file_id` order) after `armory_folder_files`, passed all 15 v2 tests with 0
   deadlocks (`server/PROOF.md`). The agent's client resends `40P01` and `40001` meanwhile.
2. The source folder matches with case (`Drive` does not move `drive/Shaft`) while the target check
   ignores case. On Windows both spellings are one folder. `FolderRenameMovesTheWholeSubtreeInOneChange`
   pins the current behavior.
3. Not new in 0232: the change feed's cursor is taken when a row is inserted, not when it commits.
   A writer that waits on a lock can record a cursor below a change that committed before it, so a
   reader that keeps only `cursor > last` can step past a late commit. Readers should treat the
   feed as a hint and the read snapshots (`armory_project_files`, `armory_my_projects`) as the
   truth; an agent that skips a snapshot refresh because the feed did not move still needs a
   periodic full refresh (the v2 design's 60-second safety refresh).

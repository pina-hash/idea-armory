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

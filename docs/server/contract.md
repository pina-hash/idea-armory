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

# Armory server contract

These SQL files are a draft, not an idea-app migration. The app's migration lane must assign a number after review. Every write is a security-definer RPC with an empty search path; clients have read-only, project-scoped RLS access.

| RPC | Rule | Core action |
|---|---|---|
| `armory_acquire_lock(file)` | A member atomically inserts the one lock; one racer wins. Repeating as holder is idempotent. | `AcquireLock` / `AcquireLockThenUpload` |
| `armory_release_lock(file)` | Only the live holder releases. | close after `Synced` |
| `armory_break_lock(file)` | Only mentor or CAD lead; marks the old holder so its later save is side work. | confirmed `RequestBreak` |
| `armory_commit_version(file,parent,key,hash,bytes)` | Caller must hold the lock and name its edited parent. A stale parent creates a side version and never advances shared state. | `Upload` |
| `armory_save_side_version(...)` | Members preserve collision or broken-lock bytes without advancing shared state. | `SaveSideVersion` |
| `armory_tombstone(file,parent)` | Lock holder only, conditional on the current parent; history remains immutable. | `ProposeTombstone` |
| `armory_allocate_part_number(project,subsystem,season)` | Atomically allocates `5669-YY-SSNN` by default; returns `subsystem_full` after 100 numbers. The project pattern is configurable. | part creation |
| `armory_list_changes(project,cursor)` | Member-only ordered changes strictly after the monotonic cursor. | sync refresh |

Names have a database unique index on project plus lowercase NFC form. Both shared and side version rows reject update and delete, including byte metadata. No purge operation exists.

Identity comes only from idea-app's zero-argument, `text`-returning `public.current_user_email()`, defined by idea-app migration `0067_admin_tier.sql`. `armory_current_email()` raises when that function returns null. Production SQL has no caller-settable identity seam. The test harness installs its matching `current_user_email()` stub from `tests/Armory.Server.Tests/sql/000_test_identity.sql` before applying the production files; only that test-only stub reads `armory.test_email`. See `server/IDEA_APP_CONVENTIONS.md` for the reviewed precedent.

`authenticated` receives `select` on the nine `armory_` tables by explicit name, and every one has row-level security enabled with project-membership policies. The Armory scripts never grant privileges on unrelated `public` tables. Direct writes remain revoked and are available only through the authorized RPCs.

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

Identity is `current_user_email()` from idea-app. The harness substitutes `SET armory.test_email` against its isolated throwaway database. See `server/IDEA_APP_CONVENTIONS.md` for the reviewed precedent.

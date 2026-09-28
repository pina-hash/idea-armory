# Safe replacement and its concurrency boundary

`SafeFileReplace` stages downloaded bytes in the vault's hidden `.armory/downloads`
directory, on the same volume. It writes through and fully flushes the temp file before
checking the destination. A per-staging-directory ownership handle prevents two adapters
from cleaning each other's active downloads. Startup removes orphan `.pending` files.

The adapter checks Restart Manager/exclusive-open state, re-hashes the destination under
an exclusive read handle, refuses a changed hash, closes its own handle, rechecks open
state, then calls `MoveFileExW` with `MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH`.
A planned-absent destination uses write-through without replace-existing, so a newly
created destination cannot be overwritten. Native sharing violations receive bounded
retries, each with fresh checks. A known open application is refused immediately.
Other failures retain the old destination and clean the staged temp file.

Measured limitation: on Windows build 26200, MoveFileEx returned error 5 while the
destination read handle remained open, even with Read/Write/Delete sharing; closing the
handle allowed replacement. The repeatable `move-diagnostic` probe records that result.
Consequently, there is a check-to-rename interval: a noncooperating application can save
or open between the final checks and rename. This adapter meets the requested immediate
checks but is **not an atomic compare-and-replace guarantee against arbitrary external
writers**. The agent/add-in must coordinate saves/opens before using this as a fully
lossless production download executor. The core's stronger integration contract remains
unchanged, and this lane does not claim that contract is solved by MoveFileEx alone.

Real-file tests verify successful replacement, another process's no-sharing handle,
changed destination after staging, newly appearing destination, read-only native failure,
old-byte preservation, temp cleanup, and process death after staging followed by startup
cleanup. The [Defender probe](../spike/antivirus-replace.md) measures 100 real 5 MiB
replacements with final-hash verification and unchanged Defender settings.

Reference: [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).

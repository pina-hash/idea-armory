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

A read-only destination (under v2, a file this computer has not checked out) keeps its bit
at every moment. `MoveFileExW` cannot replace a read-only file, and clearing the bit first
would leave a file nobody checked out writable while a large download is staged, hashed
and checked, long enough for SolidWorks to open it with write access. Instead the staged
copy takes the read-only bit too, and the rename is
`SetFileInformationByHandle(FileRenameInfoEx)` with `FILE_RENAME_FLAG_REPLACE_IF_EXISTS |
FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE` (Windows 10 1809 and later, NTFS), the source
opened with `FILE_FLAG_WRITE_THROUGH` as `MOVEFILE_WRITE_THROUGH` does. Like `MoveFileExW`
without POSIX semantics, it fails while any program has the destination open. Only where the
file system does not support it (FAT32, exFAT, a network share, an older Windows: the call
fails with `ERROR_INVALID_FUNCTION`, `ERROR_NOT_SUPPORTED` or `ERROR_INVALID_PARAMETER`) is
the bit cleared, after every check and immediately before `MoveFileExW`, and put back if the
rename fails; a bit that cannot be put back is retried by the next scan.

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
changed destination after staging, newly appearing destination, a read-only destination
that is still read-only after staging and immediately before the rename on both rename
paths (and a rename that fails there keeps the old bytes and the bit), the bit held while
an 8 MiB body is read into staging through the vault adapter, old-byte preservation, temp
cleanup, and process death after staging followed by startup cleanup. The [Defender probe](../spike/antivirus-replace.md) measures 100 real 5 MiB
replacements with final-hash verification and unchanged Defender settings.

Reference: [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).

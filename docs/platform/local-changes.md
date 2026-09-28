# Local change detection

`LocalChangeDetector` treats FileSystemWatcher as a wake-up hint and always enumerates
the real directory tree for inventory. The cache stores canonical path, volume/file id,
size, last-write UTC time, content hash, and hash time. Hashes are reused only when the
metadata tuple is unchanged. Full scans refresh entries older than the configurable
maximum age; a real watcher overflow requests a full scan and forces fresh hashes.
The host must schedule periodic full scans even when no watcher event arrives.

Renames require an identical NTFS volume/file id. Ambiguous hard-link identities are
not guessed. Hashing holds a read handle that denies writes/deletes. Unreadable paths
are reported and retain previous cache entries, preventing a failed scan from becoming
a deletion. Reparse points are excluded and reported rather than traversed outside the
vault. Call Scan serially from the agent coordinator.

Real-disk tests create 5,000 files while a 4 KiB watcher callback is deliberately stalled,
requiring an actual `InternalBufferOverflowException`. They edit and rename all 5,000,
delete 2,500, and compare the final canonical paths, ids, sizes, timestamps, and hashes
against a fresh scanner. All 5,000 renames must be recognized. A second test edits a
file without changing its size, restores its timestamp, and proves an aged full scan
detects it. Another verifies unchanged metadata avoids hashing.

See [validation](validation.md) for observed counts. Reference:
[FileSystemWatcher buffers](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize).

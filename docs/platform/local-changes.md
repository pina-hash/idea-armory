# Local change detection

`LocalChangeDetector` treats FileSystemWatcher as a wake-up hint and always enumerates the
real directory tree for inventory, in one walk that yields files, folders and SolidWorks
`~$` markers. The cache stores canonical path, volume/file id, size, last-write UTC time,
content hash, hash time and the read-only bit.

Hashes are reused only when the metadata tuple is unchanged and the file is outside the racy
window: a file whose last-write time is within 2 seconds of the moment it was hashed is
hashed again, because a second write that close can leave the time unchanged. When only the
path changed (a rename, or a folder rename above the file), the hash is reused by NTFS id,
size and time, so renaming a folder of 5,000 files re-hashes none of them. There is no
periodic re-hash: every scan enumerates everything and re-hashes exactly what changed. A
detector built with a maximum cache age (tests) also re-hashes older entries on a full
rescan. A real watcher overflow requests a full rescan, which costs only the enumeration.

Folders are reported with their NTFS directory ids, read by opening each directory with
`CreateFileW`, `FILE_FLAG_BACKUP_SEMANTICS` and `FILE_FLAG_OPEN_REPARSE_POINT`, for
attributes only and sharing read, write and delete, so the scan never blocks a student's
Explorer rename or delete. A directory id found at a new path is a folder move. Only the
top-most moved directory is reported, in the order to apply the moves (each `Before` is the
path after the earlier moves), so a renamed Pack and Go folder is one move, never per-file
deletes and adds. A file that only rode along is not repeated as a rename; a file that also
moved on its own is reported from where the folder moves left it. A move the agent made
itself (`Absorb`) is not reported and nothing in it is re-hashed.

Renames require an identical NTFS volume/file id. Ambiguous hard-link identities are not
guessed. Hashing holds a read handle that denies writers but shares read and delete, so a
student can rename or delete the file, or a folder above it, during a scan. Unreadable paths
are reported and retain previous cache entries, preventing a failed scan from becoming a
deletion. Reparse points are excluded and reported rather than traversed outside the vault.
Call Scan serially from the agent coordinator.

The watcher buffer is 64 KB. Every change outside `.armory` wakes the engine: files,
folders, attribute changes (a cleared read-only bit), and `~$` markers (so the check out
prompt appears at once); markers stay hints and never enter the inventory. desktop.ini and
Thumbs.db churn does not wake it.

Real-disk tests create 5,000 files while a 4 KiB watcher callback is deliberately stalled,
requiring an actual `InternalBufferOverflowException`. They edit and rename all 5,000,
delete 2,500, and compare the final canonical paths, ids, sizes, timestamps, and hashes
against a fresh scanner. All 5,000 renames must be recognized. Other tests: an edit that
keeps size and timestamp is caught by an aged full scan and, inside the racy window, by any
scan; unchanged metadata avoids hashing even on a full rescan; a folder rename (and a rename
above and below at once) is reported top-most first with no re-hash; a folder can be
renamed while the scan holds a file's hashing handle; markers, attribute changes and folder
events wake the engine while `.armory` and desktop.ini do not.

See [validation](validation.md) for observed counts. Reference:
[FileSystemWatcher buffers](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize).

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
top-most moved directory is reported, so a renamed Pack and Go folder is one move, never
per-file deletes and adds. The moves come in an order that can be applied one by one: each
`Before` is the path after the earlier moves, and each `After` is free at that moment, not
inside a folder that still has to move, and not below a place another folder still has to
take. So a chain (Gearbox to "Gearbox old", then "Gearbox v2" to Gearbox, the Pack and Go
replacement) vacates Gearbox first, and a cycle (two folders swapped), or a folder in the
way of another, first moves aside under a temporary `<name> (moving)` inside the same
top-level folder. A file that only rode
along is not repeated as a rename; a file that also moved on its own is reported from where
the folder moves left it. A move the agent made itself (`Absorb`) is not reported and
nothing in it is re-hashed.

The folder map (path to directory id) survives a restart in `.armory\folder-ids.json`, so a
folder renamed while Armory was closed is still one move on the first scan. The file written
trails the scan by one: a scan's moves are written only when the next scan starts (the engine
came back for another pass), so a crash before the engine handled them reports them again
rather than losing them; the agent's own moves are written at once. `FolderMoves` is null
(cannot tell, never "none") when there is no readable map, when a folder that left its path
had no readable id, or when its id was not found while another folder's id could not be read.
`Renames` is null on the first scan after a start, which has no earlier file map.

Renames require an identical NTFS volume/file id. Ambiguous hard-link identities are not
guessed. Hashing holds a read handle that denies writers but shares read and delete, so a
student can rename or delete the file, or a folder above it, during a scan; the adapter's
`OpenRead` (captures) shares delete the same way. A problem keeps, as they were, only the
entries it could hide: everything under a directory that could not be listed, an entry whose
attributes could not be read, or a reparse point (excluded and reported, never traversed); a
file that could not be opened, at its own path; and the file or folder whose id turns up on
an entry the scan could not take in (a path over the 240-character limit, a name the vault
refuses, a file another program holds with no sharing, whose id is still read with
`FILE_READ_ATTRIBUTES`). Every other missing file or folder is reported missing, so one long
Pack and Go path, a link, or an open file never stops a deletion elsewhere from showing, and
nothing stays "present" after the problem is gone. Call Scan serially from the agent
coordinator.

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
events wake the engine while `.armory` and desktop.ini do not; a 250-character path, a file
renamed to a name that does not fit and a file held open with no sharing never hide a deleted
file or a deleted folder elsewhere, on three scans in a row; a folder that cannot be listed (a
deny ACE) keeps only what is inside it; a chain and a swap of real folders come in an order
that applies; and a folder renamed while the detector was stopped is one move from the saved
map, reported again after a crash and never after the agent's own move. `FolderMoveOrderTests`
run the ordering on every host, including 2,000 random trees of student renames, moves,
deletions and new folders, each list applied move by move with every target free.

See [validation](validation.md) for observed counts. Reference:
[FileSystemWatcher buffers](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize).

# Local change detection

`LocalChangeDetector` treats FileSystemWatcher as a wake-up hint and always enumerates the
real directory tree for inventory, in one walk that yields files, folders and SolidWorks
`~$` markers. The cache stores canonical path, volume/file id, size, last-write UTC time,
content hash, hash time, the read-only bit, and whether this scan could read the file
(`Unread`, below).

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
guessed. File renames come in path order, except that a rename onto a path another rename
leaves comes after it, so a chain (Plate to "Plate old", then "Plate v2" to Plate) applies
one by one; two files swapped have no such order and are listed next to each other. Hashing holds a read handle that denies writers but shares read and delete, so a
student can rename or delete the file itself during a scan, and the scan holds no handle once a
file's hash is taken. A folder above a file that is being hashed at that very moment cannot be
renamed (NTFS refuses to rename a folder with any file inside it open, whatever the sharing; Explorer
offers Try Again), so that window is one file's hash long. The adapter's
`OpenRead` (captures) shares delete the same way. A problem keeps, as they were, only the
entries it could hide: everything under a directory that could not be listed, an entry whose
attributes could not be read, or a reparse point (excluded and reported, never traversed); a
file that could not be opened, at its own path; and the file or folder whose id turns up on
an entry the scan could not take in (a path over the 240-character limit, a name the vault
refuses, a file another program holds with no sharing, whose id is still read with
`FILE_READ_ATTRIBUTES`). Every file kept that way is marked `Unread` (0.3.3, feedback N4):
its hash, size, time and read-only bit are the last ones read, not the disk's now, and the
first scan that can read it hashes it again, whatever its size and time say (a writer can put
both back). SolidWorks holds a part it opened while it was writable with a write handle, which
the scan's read handle (sharing read and delete, never write) conflicts with, so a checked-out
part open in SolidWorks is `Unread` on every scan until it is closed; before 0.3.3 nothing said
so, and a check in took the old hash for the disk's (docs/agent/ENGINE.md, "Check in when
closed"). Every other missing file or folder is reported missing, so one long
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
renamed after the scan returns and a file can be renamed while it is being hashed; markers, attribute changes and folder
events wake the engine while `.armory` and desktop.ini do not; a 250-character path, a file
renamed to a name that does not fit and a file held open with no sharing never hide a deleted
file or a deleted folder elsewhere, on three scans in a row; a file another handle holds for
writing (`FileAccess.ReadWrite`, `FileShare.Read`, as SolidWorks holds a part) is kept
`Unread` with its old hash and a problem, and hashed again once that handle closes though
the bytes changed with size and time put back; a folder that cannot be listed (a
deny ACE) keeps only what is inside it; a chain and a swap of real folders come in an order
that applies; and a folder renamed while the detector was stopped is one move from the saved
map, reported again after a crash and never after the agent's own move. `FolderMoveOrderTests`
run the ordering on every host, including 3,000 random trees of student renames, moves,
deletions and new folders, each list applied move by move with every target free, and the
order of file renames (a chain, a case-only rename and a swap).

See [validation](validation.md) for observed counts. Reference:
[FileSystemWatcher buffers](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize).

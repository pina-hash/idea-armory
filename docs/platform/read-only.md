# Read-only unless checked out

The v2 rule (decision D4): every vault file the server has is read-only on disk unless THIS
device holds its check out. `ReadOnlyPolicy.IsReadOnly(ownership)` is
`ownership != LockOwnership.ThisDevice`, the same rule as Armory.Core's
`CheckoutRules.IsReadOnlyOnDisk`: Free, OtherPerson and MyOtherDevice are read-only,
ThisDevice is writable. SolidWorks opens a read-only file read-only and cannot save over it,
so nobody saves edits to a file they have not checked out. Files the server does not have
(a name it refused, a release-gate draft, a file too large, one not added yet) are never
passed to the policy and stay writable. The policy keeps every other attribute, and it
changes an attribute only when the rule changes it, so applying the same rule again writes
nothing and raises no change notification. A Windows attribute is not server authorization:
the core still refuses shared uploads without the lock.

Before changing an attribute, the adapter writes and fully flushes a desired-state manifest
(`.armory\read-only.json`, version 2: `{"version": 2, "intents": {"<vault path>":
{"ownership": n, "file": "<NTFS file id>"}}}`) and atomically replaces the prior manifest.
`ApplyMany` (the engine's `ApplyLockAttributes`) writes the manifest at most once for a
whole batch, and not at all when no intent changed, so a 5,000-file import costs one flushed
write and a pass that applies the same rule again costs none; a file whose bit cannot be
changed is returned, retried by the next scan (bits only, one batch) and reported in its
problems, and never stops the others. `ApplyLockAttributesNow` (0.3.3) applies a batch the
same way, with one manifest write, and returns the files it could not change instead of
retrying them: the engine sets the bits of a whole chunk of check outs or releases in one call
(a thousand bits in one call in the Windows test), still before each release, and keeps the
lock of any file whose bit it could not set.

An intent belongs to one file, not to a path: it records the NTFS id of the file it was
made for, and a path with no file keeps no intent. When the agent moves a folder
(`MoveFolder`) or renames a file (`Move`), the intents move with it; a file moved to
recovery leaves its intent behind (`Forget`); a download that replaces the very file an
intent was made for renews the intent's id, and drops an intent made for any other file. Startup `Recover` merges the current authoritative lock view, then applies each
recorded intent only to the very file it was made for: an intent whose file is gone, or
whose path now holds another file, is dropped without a word. So a file the server does not
have (a re-added copy refused for its name, a release-gate draft, a new file saved at an old
path) never becomes read-only after a restart, and the manifest only ever holds files that
are still there. The engine applies the rule to every file the server has on its first pass,
offline too, from the bits the scan reports. A 0.1.0 manifest (`{"<path>": n}`, written when
Free meant writable) names paths, not files, so none of it is applied after an upgrade; the
next write replaces it. An intent whose path no longer fits the vault, a bit that cannot be
set, or an unreadable manifest is reported instead of stopping the agent from starting.
Unknown lock state is not guessed.

New bytes never appear writable: `Replace(readOnly: true)` (a download of a file this device
has not checked out) sets `FILE_ATTRIBUTE_READONLY` on the staged copy in
`.armory\downloads` before the rename, and the attribute travels with it. A destination that
is read-only is never made writable, not even for the replace: the staged copy takes the bit,
and the rename replaces the read-only file directly (`FileRenameInfoEx` with
`FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE`; see [safe replacement](safe-replace.md) for
the fallback on file systems without it). The scan reports each file's bit
(`LocalFile.ReadOnly`, read from the same handle as its id, size and time), and a cleared bit
wakes the engine (the watcher includes attribute changes), so the engine can put it back or
keep the changed bytes as a kept copy. A file the scan could not open (`LocalFile.Unread`:
SolidWorks or another program holds it for writing) carries the bit the scan read last, not
the disk's, so the engine neither reports nor applies anything for it until a scan can read it
(0.3.3: the `readOnlyBroken` incidents of IDEA-06 were all such stale bits).

Tests (Windows only) exercise every ownership state on a real file in both orders; a
200-file batch persisted once, applied again with no manifest write and no attribute write
(counted at the attribute call itself), and one file whose bit Windows refuses (a deny ACE)
while the other 199 change; a missing path that keeps no intent, so a file created there
later stays writable after a restart; a moved folder keeping its intents across a restart;
files moved to recovery, renamed by the agent, deleted in Explorer and replaced by a
download, where only the files the server has get their bits back after a restart and the
manifest names only them, and a checked-out file deleted in Explorer and downloaded again
read-only, whose old "writable" intent never follows the new bytes; a 0.1.0 manifest that is never applied and an unreadable one that
never stops the start; a read-only staged copy (checked inside the staging hook) and its
cleanup after a refusal or a crash; a read-only destination still read-only while a download
is staged and at the last moment before the rename; and `LocalFile.ReadOnly` before and after
the bit changes. A child sets read-only, persists the transition to ThisDevice, then is
killed before changing the attribute. A new instance recovers from the authoritative state
and clears the bit. The test verifies both the intermediate set bit and the recovered clear
bit. The end-to-end proof on Linux models the same bit in `PortableVaultFileSystem` (root
ignores file permissions there; a bit stays with its file and a new file at a path never
inherits one, the behavior the manifest now has on Windows), and `Computer.Save` refuses a
read-only file the way SolidWorks does.

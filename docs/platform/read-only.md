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
(`.armory\read-only.json`) and atomically replaces the prior manifest. `ApplyMany` (the
engine's `ApplyLockAttributes`) writes the manifest once for a whole batch, so a 5,000-file
import costs one flushed write instead of 5,000; a file whose bit cannot be changed is
returned, retried by the next scan and reported in its problems, and never stops the others.
When the agent moves a folder (`MoveFolder`), its files' intents move with it (`Rekey`).
Startup `Recover` merges the current authoritative lock view and re-applies every intent
under the current rule, so an intent recorded by 0.1.0 ("Free" meant writable then) becomes
read-only after an upgrade. An intent whose path no longer fits the vault, or whose bit
cannot be set, is reported instead of stopping the agent from starting. Unknown lock state
is not guessed.

New bytes never appear writable: `Replace(readOnly: true)` (a download of a file this device
has not checked out) sets `FILE_ATTRIBUTE_READONLY` on the staged copy in
`.armory\downloads` before `MoveFileEx` renames it into place, and the attribute travels with
the rename. A destination that was read-only is cleared only for the replace and always put
back. The scan reports each file's bit (`LocalFile.ReadOnly`, read from the same handle as
its id, size and time), and a cleared bit wakes the engine (the watcher includes attribute
changes), so the engine can put it back or keep the changed bytes as a kept copy.

Tests (Windows only) exercise every ownership state on a real file in both orders, a
200-file batch persisted once, a moved folder keeping its intents across a restart, a
read-only staged copy (checked inside the staging hook) and its cleanup after a refusal or a
crash, and `LocalFile.ReadOnly` before and after the bit changes. A child sets read-only,
persists the transition to ThisDevice, then is killed before changing the attribute. A new
instance recovers from the authoritative state and clears the bit. The test verifies both
the intermediate set bit and the recovered clear bit. The end-to-end proof on Linux models
the same bit in `PortableVaultFileSystem` (root ignores file permissions there), and
`Computer.Save` refuses a read-only file the way SolidWorks does.

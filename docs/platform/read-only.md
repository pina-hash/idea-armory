# Lock-driven read-only attributes

`ReadOnlyPolicy` sets read-only for `LockOwnership.OtherPerson` and clears it for Free,
ThisDevice, and MyOtherDevice, as requested for this user's files. It preserves other
file attributes. The core still refuses shared uploads from the user's other device;
a Windows attribute is not server authorization.

Before changing an attribute, the adapter writes and fully flushes a desired-state
manifest and atomically replaces the prior manifest. Startup `Recover` merges the
current authoritative lock view before applying attributes. This prevents an old
read-only bit from surviving a crash once the file belongs to this user again.
The host must call recovery before enabling editing. Unknown lock state is not guessed.

Tests exercise every ownership state on a real file. A child sets read-only, persists
the transition to ThisDevice, then is killed before changing the attribute. A new
instance recovers from the authoritative state and clears the bit. The test verifies
both the intermediate set bit and the recovered clear bit.

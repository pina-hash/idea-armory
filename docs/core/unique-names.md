# Unique names

`VaultIndex` indexes file name plus extension over the entire vault. Projects, folders,
and the COTS library share that single index. Names are NFC and ordinal case-insensitive.
Different extensions remain distinct. `IsFree`, `Find`, and `TryAdd` answer allocation
questions without choosing a replacement name.

`FindImportCollisions` reports every collision group, including collisions within the
import itself, sorted deterministically. `AnalyzeImport` accepts untrusted server paths
and additionally reports invalid names for a lead. Callers must not construct an import
from unchecked default `VaultPath` values; use the string import boundary for server data.

`NamingTests` verifies cross-project/COTS duplicates, extension distinction, NFC lookup,
invalid lookup, intra-import collisions, complete reporting, and unchanged invalid names.
Server-side allocation must enforce the same invariant atomically; this in-memory index
is shared decision logic, not a distributed lock or server transaction.

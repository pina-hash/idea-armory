# Windows paths and names

`WindowsPaths` validates server-relative names through the unchanged `VaultPath` rules,
then uses the extended Windows prefix (`\\?\` or `\\?\UNC\`) for disk access. Root and
maximum length are explicit configuration. The default remains 240; tests opt into a
higher limit to exercise a real 250-character full path. A large configured limit does
not promise SolidWorks itself accepts those paths.

Reparse points in the destination ancestry are refused to avoid following a junction
outside the configured vault. This implementation targets a local ordinary NTFS vault,
not cloud-placeholder directories or case-sensitive NTFS directories. Platform tests
use newly created ordinary NTFS directories.

Real-disk tests write/read an emoji filename, verify case-only spellings resolve to one
file, write/read a full path of exactly 250 characters, and confirm that a server-supplied
CON.txt is refused with the core's message without creating it. Ignore-list inventory is
tested on actual files. Invalid names are never silently renamed.

# Offline journal

`OfflineJournal` writes append-only intent frames through `IJournalStore`. Each frame
contains a little-endian payload length and its complement, a UTF-8 JSON payload,
SHA-256 payload checksum, and a final commit marker. Payloads are limited to one MiB;
file bytes live in immutable snapshots rather than in journal records.

Replay accepts only complete, validated frames in order. An incomplete final frame is
ignored. The next append removes only that incomplete suffix before appending; committed
records are never rewritten or purged. Bad complete headers, checksums, or markers fail
closed and retain evidence, because corruption is different from a torn append.

Replay delivers ids to `IIntentSink.ApplyOnce`. The sink must atomically commit the effect
and id, reject reuse with different content, and be idempotent after an acknowledgement
is lost. The journal cannot manufacture exactly-once network semantics with a local flag.
Each store has one serialized writer; `Flush` must establish durability. A real adapter
must supply crash-safe truncation of only the incomplete suffix.

`SaveRecorder` first persists bytes and ordered metadata through `ISaveSnapshotStore`,
then appends the upload intent. Recovery enumerates captures and re-journals missing
ones using the original ids. This preserves intermediate saves and the capture made
before a torn journal write. Save capture must complete before a save is acknowledged
by the sync layer; arbitrary unobserved external application writes are outside that
interface guarantee and must be handled by the real agent's watcher/spike design.

`JournalTests` cuts the second append at every byte, including zero and the complete
frame boundary, checks ordered replay twice, and appends a third entry after recovery.
It also tests crash after remote commit, flush failure, conflicting ids, corruption,
validation, and recovery of multiple snapshots. The simulation adds crashes during
journal writes and remote-effect acknowledgement loss amid live multi-client sync.

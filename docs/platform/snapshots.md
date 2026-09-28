# Durable save snapshots

`DurableSnapshotStore` implements C1's `ISaveSnapshotStore`. It streams bytes into a
private pending file, flushes them, computes SHA-256, commits the immutable blob, then
flushes and atomically commits ordered metadata. Capture returns only after both are
durable. Stable ids map to hashed filenames; reuse with different metadata/bytes fails.
An ownership file serializes access across processes.

Reopening enumerates committed metadata in capture order. `SaveRecorder.Recover` can
then rebuild missing journal intents using their original ids. Incomplete pending files
are cleaned; complete unacknowledged orphan blobs remain available for diagnosis.
`OpenRead` verifies the content hash before returning bytes. Snapshot retention has no
purge path in this lane.

The real-disk test captures two saves, rejects conflicting id reuse, disposes/reopens
the store, reads the original bytes, and recovers both upload intents into a durable
journal. Capture only guarantees saves passed through this interface; a file watcher
cannot guarantee observing every intermediate save from an arbitrary application.

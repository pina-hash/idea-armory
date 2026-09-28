# Durable journal store

`DurableJournalStore` implements `IJournalStore` with an exclusively owned writable file,
serialized calls, `FileOptions.WriteThrough`, and `Flush(true)` before Append returns.
Each transport record has a magic value, length, inverse length, and CRC-32C followed by
payload. The CRC uses the Castagnoli polynomial; the standard `123456789` check vector
is tested. Payloads contain the core's unchanged SHA-256 frames.

Open scans in order and stops at the first incomplete/invalid record. An incomplete
suffix is truncated and flushed; `Recovery.DroppedBytes` reports its size. A complete
checksum/header failure preserves the original as a `.corrupt-*` file, writes a durable
`.blocked` marker before truncation, and refuses read/replay/append, including after a
second restart. No later record is skipped past or silently accepted. Recovery requires
an explicit future lead workflow; the adapter does not guess how to repair corruption.

Core-requested truncation is allowed only for its verified incomplete suffix on a
transport boundary. Committed intents cannot be truncated through that method. A failed
write poisons the instance until it is closed and reopened. Flush establishes the OS's
durability guarantee; these tests do not simulate actual hardware power removal.

Real-disk tests include the core journal round trip, every byte cut of a small physical
record, checksum corruption in a middle record, persistent blocking/evidence, and a
child killed during a tight append loop 200 times. Recovered payloads are checked in
order byte-for-byte, with no duplicate. Results are recorded in [validation](validation.md).

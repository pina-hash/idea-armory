# Content storage protocol

Bytes are addressed by lowercase SHA-256 at `blobs/sha256/<first 2>/<next 2>/<full hash>`. Metadata, authorization, locks, and version ordering stay in PostgreSQL. Object storage is only an immutable byte store.

The server mints short-lived SigV4 URLs and the credential-free client consumes them. `SigV4Presigner` is separate for server reuse. An upload first sends HEAD and skips an existing hash. At or below 64 MiB it PUTs once. Above the configurable threshold it creates a multipart upload, uploads parts, retries a part after a dropped connection using the same upload id and part number, completes with ordered ETags, and aborts when retries are exhausted.

A download is buffered privately, hashed, and compared in fixed time with the hash in its object key. Mismatched bytes throw `HashMismatchException`; no bytes are returned to the platform layer. Callers should durably replace local files only after `DownloadAsync` succeeds.

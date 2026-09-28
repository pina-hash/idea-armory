# Content addressing

`ContentAddress.ComputeAsync` streams SHA-256 and returns 64 lowercase hexadecimal
characters. It leaves stream ownership with the caller and propagates cancellation
and stream I/O failures to the adapter. The core never opens a real file.

`ContentTests` verifies identical content, a one-byte change, the known empty digest,
cancellation, and a nonseekable generated 64 MiB stream. The generated stream records
the largest requested buffer and rejects reliance on seek/length; reads stay bounded
at 128 KiB or less. The same algorithm has no file-size-dependent allocation for a
500 MB input. The test does not allocate a 64 MiB source array.

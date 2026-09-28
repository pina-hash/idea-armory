# Antivirus and safe replacement

Measured: 2026-09-27T18:22:23.6514499-07:00

Windows: Microsoft Windows NT 10.0.26200.0; build 26200.

Defender before measurement: `{"AntivirusEnabled":true,"RealTimeProtectionEnabled":true,"AMProductVersion":"4.18.26080.4"}`. No settings changed.

Measured 100 destination files, each replaced with 5 MiB (5,242,880 bytes). Same-volume hidden staging, write-through temp file, full flush, destination re-hash, open-file checks, and atomic MoveFileEx replacement were included. Final hashes were verified outside each timed interval.

Succeeded: 100/100. Sharing violations: 0. Retries: 0.

Mean: 31.501 ms; median: 31.045 ms; p95: 35.857 ms; maximum: 38.520 ms. Total including setup and validation: 4.490 seconds.

No failed replacements. This single run does not prove Defender caused or cannot cause interference.

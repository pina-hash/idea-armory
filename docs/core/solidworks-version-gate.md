# SolidWorks version gate

`SolidWorksRelease` carries the public release year. `ISavedReleaseReader` is the
stream-based boundary that names the release a file was saved in; since 0.3.3 it has a
production implementation, `SolidWorksSavedReleaseReader` (below). A missing or unknown
release fails closed in `Enforce`.

Uploads at or below the pinned release pass. Newer releases are refused with both
years in the message, and private bytes remain retained. The reconciler recognizes
SLDPRT, SLDASM, and SLDDRW case-insensitively and applies the gate before shared or side
uploads. Generic files do not require release metadata.

Pin changes must strictly increase. `RolloverGaps` reports every installation's distance
from the newest, warns at two releases (a season ahead), and flags more than two as
beyond back-save range. It sorts by installation id for deterministic output.
The administrator must additionally ensure every installation can use a proposed pin.

`VersionAndPartTests` verifies boundary years, invalid/missing release information,
strict pin increases, empty installations, and gaps of zero, two, and three.
`ReconcilerTests` applies upload gating to every SolidWorks extension, lock disposition,
and ordinary/conflicting/tombstoned remote. The fake simulation uses generic byte files;
it does not pretend to parse or back-save real CAD documents.

The two-release capability is confirmed in [official SOLIDWORKS help](https://help.solidworks.com/2026/English/WhatsNew/c_wn2026_fundamentals_saving_previous_versions.htm).
Licensing and practical back-save behavior remain spike measurements.

## Per-project gate mode (lane A)

When lane A was built no standalone saved-release reader existed
([saved release](../spike/saved-release.md)), and refusing every SolidWorks file whose release
is unknown would have blocked every student, which Mr. Pina ruled out. `ReleaseGateMode`
therefore has two values. `Enforce` keeps the rule above: an unknown release is refused.
`Warn` uploads an unknown release and marks the action `ReleaseNotChecked`, which the agent
and the server's version record show as "release not checked". A release known to be newer
than the pin is refused in both modes, and `Warn` never overrides a missing or invalid pin.
`SyncInput.ReleaseGate` defaults to `Enforce` so the library stays fail-closed; projects
default to `Warn` on the server (`armory_projects.release_gate`). `SolidWorksVersionGate.Decide`
is the one decision both the reconciler and the agent's archive of superseded saves use.
`ReleaseGateModeTests` covers the table, every upload route and lock disposition, and the
offline path.

Until 0.3.3 the agent passed no reader at all (`ReleaseReader = null`), so every SolidWorks
file was unknown: in `Warn` every one uploaded as "release not checked", a part saved in
SolidWorks 2026 included, with no warning. Since 0.3.3 the agent reads the year of every
SolidWorks upload; `Warn` now takes only the files the reader cannot place (below), and the
recommendation is to switch projects to `Enforce` once the SolidWorks lab checklist passes.

## The saved-release reader (0.3.3)

`SolidWorksFileRelease.Read(Stream, CancellationToken)` in `SolidWorksFileRelease.cs`,
wrapped as `SolidWorksSavedReleaseReader : ISavedReleaseReader`. Pure: a seekable stream
in, `SolidWorksRelease?` out, `System.IO.Compression` only, at most 1 MiB of history and a
few small buffers in memory, cancellable. It never throws on bad bytes (any parsing failure
is unknown); a stream that cannot seek is unknown; a stream that fails (`IOException`) or a
canceled token still throws, so a failing disk is never mistaken for an unknown year.

SolidWorks 2015 and later files are a chunk container (research
`saved-release-and-savedown.md` section 1.1, and the open readers openswx and SWFormat): each
chunk is a 4-byte value, the marker `14 00 06 00 08 00`, a header with a flag, the compressed
size, the uncompressed size and the name length, then the stream name rotated left per byte
by the key at byte 7, then raw DEFLATE data. A year is returned only when all of these hold
(the precision rule, research section 1.7); anything else is unknown (null):

1. The file is not an OLE2 compound file (before 2015; unknown in 0.3.3), and the marker is
   in its first 64 bytes.
2. Walking every chunk (a name of 1 to 512 printable ASCII characters after rotation, a
   payload of at most 64 MiB inside the file, table-of-contents entries skipped, every
   payload skipped so a marker inside one is never read), the `_MO_VERSION_<code>/` stream
   names carry exactly one code `C`; a malformed one makes the file unknown.
3. There is exactly one `_MO_VERSION_C/History` stream. It inflates to exactly its declared
   size, at most 1 MiB; it is the MFC CArchive object `moVersionHistory_c` (`FF FF 01 00 12
   00` and the name); its entry count is 1 to 1000 and equals the number of major codes in it
   (each follows an empty Unicode CString `FF FE FF 00`).
4. The last major code (not the largest: a file saved down may list 19000 before 18000)
   equals `C`.
5. `C` of 8000 or more is release `2007 + ceil(C / 1000)` (`YearOf`): exactly the official
   table (8000 is 2015, 18000 is 2025, 19000 is 2026), the same pattern beyond it, and a
   pre-release code (18800, a 2026 beta) rounds up to the release it previews, so it can
   never read as 2025.

A 2025 file has 18000 in both places and reads 2025 exactly; a file reads 2026 only if
SolidWorks wrote 19000 into both its stream names and its history. `docProps/app.xml`
`AppVersion` (23.0000 in every file from 2017 to 2025), `_DL_VERSION_` (the display list,
which lags) and the bracketed build dates in the history are never used.

Measured (docs/spike/saved-release.md, 2026-10-09): 158 public files from SolidWorks 2017
to 2025, 158 read, all agreeing with the research prototype, none unknown, 94 ms in all;
6,320 truncations and bit flips of them gave no other year and no exception.

`SolidWorksFileReleaseTests` builds synthetic containers in code (`SwContainer`, never a CAD
file): every table code from 8000 to 19000, 20000, the 18800 pre-release, one code against
two, a missing, duplicated, wrongly classed, miscounted, last-not-matching, oversized,
corrupt or truncated history, an OLE2 signature, no marker, every rotation key, a chunk
header inside another stream, a stream that cannot seek, cancellation, and 4,000 seeded
truncations and bit flips (never another year, never an exception).

## Stamps and the rule that combines them (0.3.3)

The SolidWorks link (built separately) records a `ReleaseStamp` right after each save it
watches (research section 2): the bytes' SHA-256, the year (`SavedReleaseRule.StampYear`:
what SolidWorks' own `VersionHistory` reads in the bytes on disk, when that is the year the
link meant to save in, or when it meant nothing, as on open; otherwise null), the writer's
revision and year, the Save to Version target, and when. The engine keeps stamps by hash
(docs/agent/ENGINE.md, "The SolidWorks year") and decides with `SavedReleaseRule.Combine(stamp
for exactly those bytes, reader)`:

| Stamp | Reader | Saved release |
|---|---|---|
| known Y | known Y | Y |
| known Y | unknown | Y |
| none or unknown | known Y | Y |
| known Y1 | known Y2, Y1 != Y2 | unknown (`Disagree` is true: a telemetry record) |
| none or unknown | unknown | unknown (the gate: Warn uploads "release not checked", Enforce keeps a private draft) |

A year before 1995 counts as unknown. `SavedReleaseRule.Merge` keeps the newer of two stamps
for the same bytes, unless their known years differ, which makes the year unknown.
`SavedReleaseRuleTests` holds the table, `StampYear` and `Merge`.

## Revisions and saving down (0.3.3)

`SolidWorksRevision.Parse` and `SolidWorksRelease.FromRevisionNumber` read
`ISldWorks.RevisionNumber`, "major.minor.hotfix": the year is major + 1992 ("33.5.0" is
2025, "34.4.1" is 2026, "23.-3.0" is a 2015 pre-release); anything that is not three
integers with a major of 3 or more is no revision (`RevisionNumberTests`).

`SaveDown.Plan(runningYear, hasSaveToVersion, pinnedYear)` says how SolidWorks' "Save to
Version" option (2026 SP3 and later; research section 3) saves a vault document in the
pinned release: `NotNeeded` (the running release is at or below the pin), `Penultimate`
(one release back, option value 1), `Antepenultimate` (two back, value 2), or why not:
`TooFarApart` (more than two releases), `OldServicePack` (2026 before SP3, revision below
34.3) or `Unsupported` (an older release, or no valid pin). `SaveDown.Plan(revision, pin)`
reads the option from the revision; a release after 2026 is taken to have it from its first
build (reasoned). `SaveDownPlanTests` holds the table. The engine uses the plan for its words
only; the link uses it to set the option.

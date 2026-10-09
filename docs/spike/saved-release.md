# Saved release without SolidWorks

Measured: 2026-09-27T18:22:23.6514499-07:00

Windows: Microsoft Windows NT 10.0.26200.0; build 26200.

Search scope: current user's Documents and `C:\IDEA\Armory` if present, capped at 50 existing CAD files. Skipped reparse/cloud placeholders and build/vendor/artifact folders; inaccessible directories: 0.

Existing files found: 0. Generated fixture inspected: 1.

Method attempted: open the compound document read-only with Windows structured storage; enumerate streams and inspect bounded Header, VersionHistory, and OLE summary streams for explicit release information. This reader neither starts nor calls SolidWorks. Generic dates and undocumented internal version integers are not accepted as a saved release.

## generated-empty-2026.SLDPRT

SHA-256: `22bc256bc8a3a0a1d6d34e7d9cc7f0dec82e86784f9f47ca421ce254d68fa296`.

Header signature `86EEC34A00000004` is not the OLE compound-file signature. A direct read-only structured-storage open returned `STG_E_FILEALREADYEXISTS (0x80030050)`; the signature check confirms this file is not a classic compound document. Standard compound streams and OLE SummaryInformation could not establish its saved release. The signature was inspected directly after the document was closed, without calling SolidWorks.


SolidWorks confirmation for the generated/copied probe document: Application revision 34.4.1; document version history: 19000[2026/225].

Validated standalone saved-release method: none. Validated reads: 0. (Superseded on
2026-10-09: see "Measured again: the chunk reader" below. This paragraph is the 2026-09-27
record.) No production `ISavedReleaseReader` is registered on the strength of unverified metadata. The core will refuse an unknown CAD release. Next measurement: multiple known releases and a documented vendor-supported format/API, or the scope's add-in stamping fallback bound to the content hash. No customer/team files were committed; generated probe files remain under ignored artifacts.

References: [Windows structured storage](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-istorage), [SOLIDWORKS version-history API](https://help.solidworks.com/2022/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~IVersionHistory.html). The latter requires the SolidWorks application and does not establish a standalone byte parser.

[Document Manager GetVersion](https://help.solidworks.com/2024/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMDocument~GetVersion.html) is a documented candidate, but a Document Manager key was not supplied. [Vendor guidance](https://help.solidworks.com/2026/english/api/swdocmgrapi/GettingStarted-swdocmgrapi.html?id=8.2) says standard compound-file techniques cannot externally read third-party data in files from SolidWorks 2015 onward. This supports investigating the vendor API rather than inferring an undocumented release-number mapping from one file.

## Measured again: the chunk reader (2026-10-09)

This supersedes "Validated standalone saved-release method: none" above. The 2015+ file is
not a compound file but a chunk container that two maintained open-source readers describe
(openswx, MIT; SWFormat, Apache-2.0): chunks found by the marker `14 00 06 00 08 00`, stream
names rotated by the key at byte 7, raw DEFLATE payloads. Two fields inside name the
release: the one code in every `_MO_VERSION_<code>/` stream name, and the last entry of the
`_MO_VERSION_<code>/History` stream, which is the list `ISldWorks.VersionHistory` returns;
the code maps to the year through the table SolidWorks publishes (8000 is 2015, 18000 is
2025, 19000 is 2026). The method and its precision rule are in
[the version gate](../core/solidworks-version-gate.md); the research is
`saved-release-and-savedown.md` in the 0.3.3 working notes (not in the repo).

Samples: 158 public SolidWorks files of five public sources (Caltech AutoGDE, CERN-OHL-P;
four GitHub robotics projects), read on the Linux host of the 0.3.3 work, never copied into the repo:

| Saved in | Files |
|---|---|
| 2017 | 1 |
| 2020 | 1 |
| 2021 | 82 |
| 2022 | 28 |
| 2024 | 42 |
| 2025 | 4 |

Results:

- `Armory.Core.SolidWorksFileRelease.Read` (the production reader, built from the measured
  prototype): 158 read, 158 known, 0 unknown, and it agrees with the prototype on all 158; 94
  ms for all 158 in one process, the largest a 7 MB assembly. In every file both fields held
  the same code, there was exactly one History stream, and it began with the CArchive class
  `moVersionHistory_c` with a count equal to its major codes.
- 6,320 seeded truncations and bit flips of those files (40 per file): 4,289 read the true
  year, 2,031 unknown, 0 another year, 0 exceptions.
- No metadata stream holds a "last saved with" text; `docProps/app.xml` `AppVersion` is
  23.0000 from 2017 to 2025, so it is no release signal.

Validated standalone saved-release method: the chunk reader, with the precision rule (both
fields agree, else unknown). Validated reads: 158. The agent registers it as its
`ISavedReleaseReader` from 0.3.3 (`SolidWorksSavedReleaseReader`). Not yet measured: a file
saved in SolidWorks 2026 (the spike's own 2026 part stayed on the spike's Windows computer) and
a file saved down from 2026 to 2025; both are on the SolidWorks lab checklist, and the
SolidWorks link's stamp covers a saved-down file the reader cannot place.

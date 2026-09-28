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

Validated standalone saved-release method: none. Validated reads: 0. No production `ISavedReleaseReader` is registered on the strength of unverified metadata. The core will refuse an unknown CAD release. Next measurement: multiple known releases and a documented vendor-supported format/API, or the scope's add-in stamping fallback bound to the content hash. No customer/team files were committed; generated probe files remain under ignored artifacts.

References: [Windows structured storage](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-istorage), [SOLIDWORKS version-history API](https://help.solidworks.com/2022/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~IVersionHistory.html). The latter requires the SolidWorks application and does not establish a standalone byte parser.

[Document Manager GetVersion](https://help.solidworks.com/2024/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMDocument~GetVersion.html) is a documented candidate, but a Document Manager key was not supplied. [Vendor guidance](https://help.solidworks.com/2026/english/api/swdocmgrapi/GettingStarted-swdocmgrapi.html?id=8.2) says standard compound-file techniques cannot externally read third-party data in files from SolidWorks 2015 onward. This supports investigating the vendor API rather than inferring an undocumented release-number mapping from one file.

# SolidWorks version gate

`SolidWorksRelease` carries the public release year. No internal format number or
SolidWorks reference is embedded. `ISavedReleaseReader` is the future stream-based
boundary for the phase 0 detection spike; a missing release fails closed.

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

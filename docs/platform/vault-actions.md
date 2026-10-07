# Folder moves, empty folders, adding files and opening files

The v2 members of `IVaultFileSystem` (src/Armory.Agent.Engine/Platform.cs), as
`WindowsVaultFileSystem` implements them. Folder arguments are vault-relative
(`Robot 2027/Gearbox`), validated like a `VaultPath`; the vault root, `.armory` and `~$`
folders are refused.

**MoveFolder(from, to)** renames a project or folder in one `Directory.Move` (a project or
folder renamed on the site, or a refused rename put back). Before moving it refuses a
missing source, a target that exists (a case-only rename is allowed), a target inside the
source, and any resulting path over WindowsPaths' 240-character limit (checked for every
file and folder inside first, so nothing moves when one would not fit). It checks every file
inside with one Restart Manager session per 500 files plus the exclusive-open probe, and
names the open file in its refusal ("Shaft.SLDPRT is open in SLDWORKS. Close it, then try
again."). It never overwrites or merges. Its own move is absorbed by the change detector
(not reported as a student's move, nothing re-hashed) and the read-only intents move with
it. Limitation, the same one `Replace` documents (docs/platform/safe-replace.md): a file
opened between the check and the move makes Windows refuse the move itself, but an
application that opens files with delete sharing could keep a handle that moves with the
folder.

**DeleteEmptyFolder(folder)** removes a folder, with its empty subfolders, only when nothing
in it is a file except metadata Windows makes by itself (desktop.ini, Thumbs.db) or a stale
SolidWorks `~$` marker. Those are deleted one by one (a marker SolidWorks still holds cannot
be deleted, and then nothing is), then each folder deepest first with a non-recursive
delete, so a file saved into it meanwhile stops the removal instead of being lost. Any other
file, a reparse point or an `.armory` folder keeps the folder. It returns true when the
folder is gone afterwards (also when it was already gone).

**CopyIn(source, to)** adds a file from outside the vault (the window's picker or a drop):
it copies through `.armory\staging` (cleaned at startup) and renames the copy into place
with no overwrite, so a scan never hashes half-copied bytes and an existing file (compared
without case, as NTFS does) is never replaced. It refuses links (symbolic links and
junctions point somewhere else), folders (the engine walks a dropped folder itself), files
under `.armory`, ignored names, and a source someone is writing right now (it opens the
source sharing read only). Cloud placeholders such as OneDrive files on demand are reparse
points too, but they are the file itself, so they are allowed. The copy is writable: the
server does not have it yet.

**Launch(path)** (decision D14) resolves the path with `VaultLocator`, strips the `\\?\`
prefix, refuses programs, scripts, installers and shortcuts (`.exe .com .bat .cmd .ps1 .psm1
.vbs .vbe .js .jse .wsf .wsh .hta .msi .msp .scr .lnk .url .reg .cpl .jar .appref-ms` and
every type in the computer's PATHEXT), and starts the file with `UseShellExecute = true`,
verb `open` and its folder as the working directory, so SolidWorks opens SolidWorks files.
The review of 0.2.0 extended D14's list with the types the shell also runs without PATHEXT
naming them: `.pif .scf .website .settingcontent-ms .theme .themepack .deskthemepack`,
`.application .appinstaller .appx .appxbundle .msix .msixbundle .xbap .vsto .jnlp .diagcab`,
`.wsc .sct .ws .msc .gadget .inf .shb .shs .chm .xll .ade .adp`, the scripts an installed
runtime associates (`.py .pyw .pyz .pyzw .pyc .pyo .sh .pl .rb .ahk .au3`) and the
PowerShell data and console types. It stays a denylist rather than a list of allowed
documents, so every CAD exchange type (STEP, IGES, STL, DXF, DWG, Parasolid) still opens.
No association (`ERROR_NO_ASSOCIATION`, 1155) becomes "No program on this computer opens .X
files."; a failed DDE conversation or a missing DLL (1156, 1157, a program that is there
but busy starting) becomes "Windows could not open X. Wait a moment, then try again."

Tests (Windows only, real disk): `MoveFolder_moves_a_closed_folder_and_refuses_open_files_existing_targets_and_long_paths`,
`DeleteEmptyFolder_removes_only_folders_without_files`, `CopyIn_copies_through_staging_and_never_overwrites`,
`CopyIn_refuses_a_symbolic_link`, `Launch_refuses_programs_and_scripts_and_opens_documents_through_the_shell`
(with the shell call recorded, so nothing opens on the runner), and `LaunchPolicyTests`,
which run on every host.

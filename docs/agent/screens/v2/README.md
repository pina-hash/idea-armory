# Agent window screens, v2

Rendered by `node tools/agent-ui/render-screens.mjs` from the demo states in
`src/Armory.Agent/wwwroot/demo/states.js`, in both themes at 1280x800 and 420x720.
Each image is the window as it first opens (the viewport, not the whole scrolled page),
with any page-only place the state names (a folder, selected files, an open notice list,
a dialog, files held over the list) applied from the query string, so no click is needed.
File names are `<screen>-<state>-<theme>-<width>x<height>.png`. The demo clock is fixed at
`2026-10-01T15:30:00-07:00`, so the relative times hold still between runs. The v1 screens are one
folder up, in `docs/agent/screens/`, unchanged.

Home exists only after a computer is connected, so `signedOut`, `connecting`, `connectFailed`
and `vaultOwnedByOther` appear on the Connect screen. File detail is shown where a file's own
page tells the story best. The Settings sheet is shown once per theme, over Home in `synced`.

184 images.

## Connect (first run)

| File | State | What it shows | Opened at | Theme | Size |
|---|---|---|---|---|---|
| [connect-signedOut-idea-1280x800.png](connect-signedOut-idea-1280x800.png) | `signedOut` | First run, before connecting |  | IDEA | 1280x800 |
| [connect-signedOut-spaceWhite-1280x800.png](connect-signedOut-spaceWhite-1280x800.png) | `signedOut` | First run, before connecting |  | Space White | 1280x800 |
| [connect-signedOut-idea-420x720.png](connect-signedOut-idea-420x720.png) | `signedOut` | First run, before connecting |  | IDEA | 420x720 |
| [connect-signedOut-spaceWhite-420x720.png](connect-signedOut-spaceWhite-420x720.png) | `signedOut` | First run, before connecting |  | Space White | 420x720 |
| [connect-connecting-idea-1280x800.png](connect-connecting-idea-1280x800.png) | `connecting` | Waiting for the browser sign-in |  | IDEA | 1280x800 |
| [connect-connecting-spaceWhite-1280x800.png](connect-connecting-spaceWhite-1280x800.png) | `connecting` | Waiting for the browser sign-in |  | Space White | 1280x800 |
| [connect-connecting-idea-420x720.png](connect-connecting-idea-420x720.png) | `connecting` | Waiting for the browser sign-in |  | IDEA | 420x720 |
| [connect-connecting-spaceWhite-420x720.png](connect-connecting-spaceWhite-420x720.png) | `connecting` | Waiting for the browser sign-in |  | Space White | 420x720 |
| [connect-connectFailed-idea-1280x800.png](connect-connectFailed-idea-1280x800.png) | `connectFailed` | The browser sign-in didn't finish |  | IDEA | 1280x800 |
| [connect-connectFailed-spaceWhite-1280x800.png](connect-connectFailed-spaceWhite-1280x800.png) | `connectFailed` | The browser sign-in didn't finish |  | Space White | 1280x800 |
| [connect-connectFailed-idea-420x720.png](connect-connectFailed-idea-420x720.png) | `connectFailed` | The browser sign-in didn't finish |  | IDEA | 420x720 |
| [connect-connectFailed-spaceWhite-420x720.png](connect-connectFailed-spaceWhite-420x720.png) | `connectFailed` | The browser sign-in didn't finish |  | Space White | 420x720 |
| [connect-vaultOwnedByOther-idea-1280x800.png](connect-vaultOwnedByOther-idea-1280x800.png) | `vaultOwnedByOther` | The Armory folder belongs to another account |  | IDEA | 1280x800 |
| [connect-vaultOwnedByOther-spaceWhite-1280x800.png](connect-vaultOwnedByOther-spaceWhite-1280x800.png) | `vaultOwnedByOther` | The Armory folder belongs to another account |  | Space White | 1280x800 |
| [connect-vaultOwnedByOther-idea-420x720.png](connect-vaultOwnedByOther-idea-420x720.png) | `vaultOwnedByOther` | The Armory folder belongs to another account |  | IDEA | 420x720 |
| [connect-vaultOwnedByOther-spaceWhite-420x720.png](connect-vaultOwnedByOther-spaceWhite-420x720.png) | `vaultOwnedByOther` | The Armory folder belongs to another account |  | Space White | 420x720 |

## Home

| File | State | What it shows | Opened at | Theme | Size |
|---|---|---|---|---|---|
| [home-synced-idea-1280x800.png](home-synced-idea-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 1280x800 |
| [home-synced-spaceWhite-1280x800.png](home-synced-spaceWhite-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 1280x800 |
| [home-synced-idea-420x720.png](home-synced-idea-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 420x720 |
| [home-synced-spaceWhite-420x720.png](home-synced-spaceWhite-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 420x720 |
| [home-checkedOutByMe-idea-1280x800.png](home-checkedOutByMe-idea-1280x800.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | IDEA | 1280x800 |
| [home-checkedOutByMe-spaceWhite-1280x800.png](home-checkedOutByMe-spaceWhite-1280x800.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | Space White | 1280x800 |
| [home-checkedOutByMe-idea-420x720.png](home-checkedOutByMe-idea-420x720.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | IDEA | 420x720 |
| [home-checkedOutByMe-spaceWhite-420x720.png](home-checkedOutByMe-spaceWhite-420x720.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | Space White | 420x720 |
| [home-checkedOutByOther-idea-1280x800.png](home-checkedOutByOther-idea-1280x800.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | IDEA | 1280x800 |
| [home-checkedOutByOther-spaceWhite-1280x800.png](home-checkedOutByOther-spaceWhite-1280x800.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | Space White | 1280x800 |
| [home-checkedOutByOther-idea-420x720.png](home-checkedOutByOther-idea-420x720.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | IDEA | 420x720 |
| [home-checkedOutByOther-spaceWhite-420x720.png](home-checkedOutByOther-spaceWhite-420x720.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | Space White | 420x720 |
| [home-transferring-idea-1280x800.png](home-transferring-idea-1280x800.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | IDEA | 1280x800 |
| [home-transferring-spaceWhite-1280x800.png](home-transferring-spaceWhite-1280x800.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | Space White | 1280x800 |
| [home-transferring-idea-420x720.png](home-transferring-idea-420x720.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | IDEA | 420x720 |
| [home-transferring-spaceWhite-420x720.png](home-transferring-spaceWhite-420x720.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | Space White | 420x720 |
| [home-offlineWaiting-idea-1280x800.png](home-offlineWaiting-idea-1280x800.png) | `offlineWaiting` | Offline, with files waiting to upload |  | IDEA | 1280x800 |
| [home-offlineWaiting-spaceWhite-1280x800.png](home-offlineWaiting-spaceWhite-1280x800.png) | `offlineWaiting` | Offline, with files waiting to upload |  | Space White | 1280x800 |
| [home-offlineWaiting-idea-420x720.png](home-offlineWaiting-idea-420x720.png) | `offlineWaiting` | Offline, with files waiting to upload |  | IDEA | 420x720 |
| [home-offlineWaiting-spaceWhite-420x720.png](home-offlineWaiting-spaceWhite-420x720.png) | `offlineWaiting` | Offline, with files waiting to upload |  | Space White | 420x720 |
| [home-pausedWaiting-idea-1280x800.png](home-pausedWaiting-idea-1280x800.png) | `pausedWaiting` | Paused by the student, with files waiting |  | IDEA | 1280x800 |
| [home-pausedWaiting-spaceWhite-1280x800.png](home-pausedWaiting-spaceWhite-1280x800.png) | `pausedWaiting` | Paused by the student, with files waiting |  | Space White | 1280x800 |
| [home-pausedWaiting-idea-420x720.png](home-pausedWaiting-idea-420x720.png) | `pausedWaiting` | Paused by the student, with files waiting |  | IDEA | 420x720 |
| [home-pausedWaiting-spaceWhite-420x720.png](home-pausedWaiting-spaceWhite-420x720.png) | `pausedWaiting` | Paused by the student, with files waiting |  | Space White | 420x720 |
| [home-groupedNotices-idea-1280x800.png](home-groupedNotices-idea-1280x800.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | IDEA | 1280x800 |
| [home-groupedNotices-spaceWhite-1280x800.png](home-groupedNotices-spaceWhite-1280x800.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | Space White | 1280x800 |
| [home-groupedNotices-idea-420x720.png](home-groupedNotices-idea-420x720.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | IDEA | 420x720 |
| [home-groupedNotices-spaceWhite-420x720.png](home-groupedNotices-spaceWhite-420x720.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | Space White | 420x720 |
| [home-groupedNoticesExpanded-idea-1280x800.png](home-groupedNoticesExpanded-idea-1280x800.png) | `groupedNoticesExpanded` | Notices grouped by kind, one list open | open list `nameShared` | IDEA | 1280x800 |
| [home-groupedNoticesExpanded-spaceWhite-1280x800.png](home-groupedNoticesExpanded-spaceWhite-1280x800.png) | `groupedNoticesExpanded` | Notices grouped by kind, one list open | open list `nameShared` | Space White | 1280x800 |
| [home-groupedNoticesExpanded-idea-420x720.png](home-groupedNoticesExpanded-idea-420x720.png) | `groupedNoticesExpanded` | Notices grouped by kind, one list open | open list `nameShared` | IDEA | 420x720 |
| [home-groupedNoticesExpanded-spaceWhite-420x720.png](home-groupedNoticesExpanded-spaceWhite-420x720.png) | `groupedNoticesExpanded` | Notices grouped by kind, one list open | open list `nameShared` | Space White | 420x720 |
| [home-importSummary-idea-1280x800.png](home-importSummary-idea-1280x800.png) | `importSummary` | One summary after unzipping a Pack and Go | folder `CopyDesignTemp` | IDEA | 1280x800 |
| [home-importSummary-spaceWhite-1280x800.png](home-importSummary-spaceWhite-1280x800.png) | `importSummary` | One summary after unzipping a Pack and Go | folder `CopyDesignTemp` | Space White | 1280x800 |
| [home-importSummary-idea-420x720.png](home-importSummary-idea-420x720.png) | `importSummary` | One summary after unzipping a Pack and Go | folder `CopyDesignTemp` | IDEA | 420x720 |
| [home-importSummary-spaceWhite-420x720.png](home-importSummary-spaceWhite-420x720.png) | `importSummary` | One summary after unzipping a Pack and Go | folder `CopyDesignTemp` | Space White | 420x720 |
| [home-checkoutPrompt-idea-1280x800.png](home-checkoutPrompt-idea-1280x800.png) | `checkoutPrompt` | SolidWorks opened a file you have not checked out | folder `Drivetrain` | IDEA | 1280x800 |
| [home-checkoutPrompt-spaceWhite-1280x800.png](home-checkoutPrompt-spaceWhite-1280x800.png) | `checkoutPrompt` | SolidWorks opened a file you have not checked out | folder `Drivetrain` | Space White | 1280x800 |
| [home-checkoutPrompt-idea-420x720.png](home-checkoutPrompt-idea-420x720.png) | `checkoutPrompt` | SolidWorks opened a file you have not checked out | folder `Drivetrain` | IDEA | 420x720 |
| [home-checkoutPrompt-spaceWhite-420x720.png](home-checkoutPrompt-spaceWhite-420x720.png) | `checkoutPrompt` | SolidWorks opened a file you have not checked out | folder `Drivetrain` | Space White | 420x720 |
| [home-checkoutPromptTaken-idea-1280x800.png](home-checkoutPromptTaken-idea-1280x800.png) | `checkoutPromptTaken` | SolidWorks opened a file someone else has checked out | folder `Drivetrain` | IDEA | 1280x800 |
| [home-checkoutPromptTaken-spaceWhite-1280x800.png](home-checkoutPromptTaken-spaceWhite-1280x800.png) | `checkoutPromptTaken` | SolidWorks opened a file someone else has checked out | folder `Drivetrain` | Space White | 1280x800 |
| [home-checkoutPromptTaken-idea-420x720.png](home-checkoutPromptTaken-idea-420x720.png) | `checkoutPromptTaken` | SolidWorks opened a file someone else has checked out | folder `Drivetrain` | IDEA | 420x720 |
| [home-checkoutPromptTaken-spaceWhite-420x720.png](home-checkoutPromptTaken-spaceWhite-420x720.png) | `checkoutPromptTaken` | SolidWorks opened a file someone else has checked out | folder `Drivetrain` | Space White | 420x720 |
| [home-folderPutBack-idea-1280x800.png](home-folderPutBack-idea-1280x800.png) | `folderPutBack` | A folder rename was put back: someone has files in it checked out | folder `Drivetrain` | IDEA | 1280x800 |
| [home-folderPutBack-spaceWhite-1280x800.png](home-folderPutBack-spaceWhite-1280x800.png) | `folderPutBack` | A folder rename was put back: someone has files in it checked out | folder `Drivetrain` | Space White | 1280x800 |
| [home-folderPutBack-idea-420x720.png](home-folderPutBack-idea-420x720.png) | `folderPutBack` | A folder rename was put back: someone has files in it checked out | folder `Drivetrain` | IDEA | 420x720 |
| [home-folderPutBack-spaceWhite-420x720.png](home-folderPutBack-spaceWhite-420x720.png) | `folderPutBack` | A folder rename was put back: someone has files in it checked out | folder `Drivetrain` | Space White | 420x720 |
| [home-projectPutBack-idea-1280x800.png](home-projectPutBack-idea-1280x800.png) | `projectPutBack` | A project folder renamed in File Explorer was put back |  | IDEA | 1280x800 |
| [home-projectPutBack-spaceWhite-1280x800.png](home-projectPutBack-spaceWhite-1280x800.png) | `projectPutBack` | A project folder renamed in File Explorer was put back |  | Space White | 1280x800 |
| [home-projectPutBack-idea-420x720.png](home-projectPutBack-idea-420x720.png) | `projectPutBack` | A project folder renamed in File Explorer was put back |  | IDEA | 420x720 |
| [home-projectPutBack-spaceWhite-420x720.png](home-projectPutBack-spaceWhite-420x720.png) | `projectPutBack` | A project folder renamed in File Explorer was put back |  | Space White | 420x720 |
| [home-projectRenaming-idea-1280x800.png](home-projectRenaming-idea-1280x800.png) | `projectRenaming` | A project renamed on the site, waiting for a file to close |  | IDEA | 1280x800 |
| [home-projectRenaming-spaceWhite-1280x800.png](home-projectRenaming-spaceWhite-1280x800.png) | `projectRenaming` | A project renamed on the site, waiting for a file to close |  | Space White | 1280x800 |
| [home-projectRenaming-idea-420x720.png](home-projectRenaming-idea-420x720.png) | `projectRenaming` | A project renamed on the site, waiting for a file to close |  | IDEA | 420x720 |
| [home-projectRenaming-spaceWhite-420x720.png](home-projectRenaming-spaceWhite-420x720.png) | `projectRenaming` | A project renamed on the site, waiting for a file to close |  | Space White | 420x720 |
| [home-archivedProject-idea-1280x800.png](home-archivedProject-idea-1280x800.png) | `archivedProject` | An archived project: listed, no longer kept up to date; my check out in it stays in My files | project `proj-robot-2026` | IDEA | 1280x800 |
| [home-archivedProject-spaceWhite-1280x800.png](home-archivedProject-spaceWhite-1280x800.png) | `archivedProject` | An archived project: listed, no longer kept up to date; my check out in it stays in My files | project `proj-robot-2026` | Space White | 1280x800 |
| [home-archivedProject-idea-420x720.png](home-archivedProject-idea-420x720.png) | `archivedProject` | An archived project: listed, no longer kept up to date; my check out in it stays in My files | project `proj-robot-2026` | IDEA | 420x720 |
| [home-archivedProject-spaceWhite-420x720.png](home-archivedProject-spaceWhite-420x720.png) | `archivedProject` | An archived project: listed, no longer kept up to date; my check out in it stays in My files | project `proj-robot-2026` | Space White | 420x720 |
| [home-selection-idea-1280x800.png](home-selection-idea-1280x800.png) | `selection` | Three files selected, with the selection bar | folder `Drivetrain`, selected `Drivetrain.SLDDRW,Gearbox.SLDASM,Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 1280x800 |
| [home-selection-spaceWhite-1280x800.png](home-selection-spaceWhite-1280x800.png) | `selection` | Three files selected, with the selection bar | folder `Drivetrain`, selected `Drivetrain.SLDDRW,Gearbox.SLDASM,Plate-Left.SLDPRT`, scrolled to the team files | Space White | 1280x800 |
| [home-selection-idea-420x720.png](home-selection-idea-420x720.png) | `selection` | Three files selected, with the selection bar | folder `Drivetrain`, selected `Drivetrain.SLDDRW,Gearbox.SLDASM,Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 420x720 |
| [home-selection-spaceWhite-420x720.png](home-selection-spaceWhite-420x720.png) | `selection` | Three files selected, with the selection bar | folder `Drivetrain`, selected `Drivetrain.SLDDRW,Gearbox.SLDASM,Plate-Left.SLDPRT`, scrolled to the team files | Space White | 420x720 |
| [home-folderNew-idea-1280x800.png](home-folderNew-idea-1280x800.png) | `folderNew` | Making a new folder | folder `Drivetrain`, dialog `newFolder`, scrolled to the team files | IDEA | 1280x800 |
| [home-folderNew-spaceWhite-1280x800.png](home-folderNew-spaceWhite-1280x800.png) | `folderNew` | Making a new folder | folder `Drivetrain`, dialog `newFolder`, scrolled to the team files | Space White | 1280x800 |
| [home-folderNew-idea-420x720.png](home-folderNew-idea-420x720.png) | `folderNew` | Making a new folder | folder `Drivetrain`, dialog `newFolder`, scrolled to the team files | IDEA | 420x720 |
| [home-folderNew-spaceWhite-420x720.png](home-folderNew-spaceWhite-420x720.png) | `folderNew` | Making a new folder | folder `Drivetrain`, dialog `newFolder`, scrolled to the team files | Space White | 420x720 |
| [home-folderRename-idea-1280x800.png](home-folderRename-idea-1280x800.png) | `folderRename` | Renaming a folder | folder `Drivetrain/Gearbox`, dialog `renameFolder`, scrolled to the team files | IDEA | 1280x800 |
| [home-folderRename-spaceWhite-1280x800.png](home-folderRename-spaceWhite-1280x800.png) | `folderRename` | Renaming a folder | folder `Drivetrain/Gearbox`, dialog `renameFolder`, scrolled to the team files | Space White | 1280x800 |
| [home-folderRename-idea-420x720.png](home-folderRename-idea-420x720.png) | `folderRename` | Renaming a folder | folder `Drivetrain/Gearbox`, dialog `renameFolder`, scrolled to the team files | IDEA | 420x720 |
| [home-folderRename-spaceWhite-420x720.png](home-folderRename-spaceWhite-420x720.png) | `folderRename` | Renaming a folder | folder `Drivetrain/Gearbox`, dialog `renameFolder`, scrolled to the team files | Space White | 420x720 |
| [home-folderDelete-idea-1280x800.png](home-folderDelete-idea-1280x800.png) | `folderDelete` | Deleting a folder: how many files, and that history is kept | folder `Drivetrain/Gearbox`, dialog `deleteFolder`, scrolled to the team files | IDEA | 1280x800 |
| [home-folderDelete-spaceWhite-1280x800.png](home-folderDelete-spaceWhite-1280x800.png) | `folderDelete` | Deleting a folder: how many files, and that history is kept | folder `Drivetrain/Gearbox`, dialog `deleteFolder`, scrolled to the team files | Space White | 1280x800 |
| [home-folderDelete-idea-420x720.png](home-folderDelete-idea-420x720.png) | `folderDelete` | Deleting a folder: how many files, and that history is kept | folder `Drivetrain/Gearbox`, dialog `deleteFolder`, scrolled to the team files | IDEA | 420x720 |
| [home-folderDelete-spaceWhite-420x720.png](home-folderDelete-spaceWhite-420x720.png) | `folderDelete` | Deleting a folder: how many files, and that history is kept | folder `Drivetrain/Gearbox`, dialog `deleteFolder`, scrolled to the team files | Space White | 420x720 |
| [home-dragOver-idea-1280x800.png](home-dragOver-idea-1280x800.png) | `dragOver` | Files dragged from File Explorer, held over the list | folder `Intake`, files held over the list, scrolled to the team files | IDEA | 1280x800 |
| [home-dragOver-spaceWhite-1280x800.png](home-dragOver-spaceWhite-1280x800.png) | `dragOver` | Files dragged from File Explorer, held over the list | folder `Intake`, files held over the list, scrolled to the team files | Space White | 1280x800 |
| [home-dragOver-idea-420x720.png](home-dragOver-idea-420x720.png) | `dragOver` | Files dragged from File Explorer, held over the list | folder `Intake`, files held over the list, scrolled to the team files | IDEA | 420x720 |
| [home-dragOver-spaceWhite-420x720.png](home-dragOver-spaceWhite-420x720.png) | `dragOver` | Files dragged from File Explorer, held over the list | folder `Intake`, files held over the list, scrolled to the team files | Space White | 420x720 |
| [home-takeBack-idea-1280x800.png](home-takeBack-idea-1280x800.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 1280x800 |
| [home-takeBack-spaceWhite-1280x800.png](home-takeBack-spaceWhite-1280x800.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | Space White | 1280x800 |
| [home-takeBack-idea-420x720.png](home-takeBack-idea-420x720.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 420x720 |
| [home-takeBack-spaceWhite-420x720.png](home-takeBack-spaceWhite-420x720.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | Space White | 420x720 |
| [home-forceAllConfirm-idea-1280x800.png](home-forceAllConfirm-idea-1280x800.png) | `forceAllConfirm` | As a mentor, Force check in all for a folder: whose files, and that their changes are kept as their own copies | folder `Drivetrain`, dialog `forceAll`, scrolled to the team files | IDEA | 1280x800 |
| [home-forceAllConfirm-spaceWhite-1280x800.png](home-forceAllConfirm-spaceWhite-1280x800.png) | `forceAllConfirm` | As a mentor, Force check in all for a folder: whose files, and that their changes are kept as their own copies | folder `Drivetrain`, dialog `forceAll`, scrolled to the team files | Space White | 1280x800 |
| [home-forceAllConfirm-idea-420x720.png](home-forceAllConfirm-idea-420x720.png) | `forceAllConfirm` | As a mentor, Force check in all for a folder: whose files, and that their changes are kept as their own copies | folder `Drivetrain`, dialog `forceAll`, scrolled to the team files | IDEA | 420x720 |
| [home-forceAllConfirm-spaceWhite-420x720.png](home-forceAllConfirm-spaceWhite-420x720.png) | `forceAllConfirm` | As a mentor, Force check in all for a folder: whose files, and that their changes are kept as their own copies | folder `Drivetrain`, dialog `forceAll`, scrolled to the team files | Space White | 420x720 |
| [home-working-idea-1280x800.png](home-working-idea-1280x800.png) | `working` | Just pressed Check out: the key turns, its row says Checking out, and the line at the foot says what is under way | folder `Drivetrain`, scrolled to the team files | IDEA | 1280x800 |
| [home-working-spaceWhite-1280x800.png](home-working-spaceWhite-1280x800.png) | `working` | Just pressed Check out: the key turns, its row says Checking out, and the line at the foot says what is under way | folder `Drivetrain`, scrolled to the team files | Space White | 1280x800 |
| [home-working-idea-420x720.png](home-working-idea-420x720.png) | `working` | Just pressed Check out: the key turns, its row says Checking out, and the line at the foot says what is under way | folder `Drivetrain`, scrolled to the team files | IDEA | 420x720 |
| [home-working-spaceWhite-420x720.png](home-working-spaceWhite-420x720.png) | `working` | Just pressed Check out: the key turns, its row says Checking out, and the line at the foot says what is under way | folder `Drivetrain`, scrolled to the team files | Space White | 420x720 |
| [home-renameFile-idea-1280x800.png](home-renameFile-idea-1280x800.png) | `renameFile` | Renaming a file that shares a name, from its notice, in the app | open list `nameShared`, dialog `renameFile` | IDEA | 1280x800 |
| [home-renameFile-spaceWhite-1280x800.png](home-renameFile-spaceWhite-1280x800.png) | `renameFile` | Renaming a file that shares a name, from its notice, in the app | open list `nameShared`, dialog `renameFile` | Space White | 1280x800 |
| [home-renameFile-idea-420x720.png](home-renameFile-idea-420x720.png) | `renameFile` | Renaming a file that shares a name, from its notice, in the app | open list `nameShared`, dialog `renameFile` | IDEA | 420x720 |
| [home-renameFile-spaceWhite-420x720.png](home-renameFile-spaceWhite-420x720.png) | `renameFile` | Renaming a file that shares a name, from its notice, in the app | open list `nameShared`, dialog `renameFile` | Space White | 420x720 |
| [home-checkOutAll-idea-1280x800.png](home-checkOutAll-idea-1280x800.png) | `checkOutAll` | Check out all asks first: how many files, and that nobody else can save them | folder `Intake`, dialog `checkOutAll`, scrolled to the team files | IDEA | 1280x800 |
| [home-checkOutAll-spaceWhite-1280x800.png](home-checkOutAll-spaceWhite-1280x800.png) | `checkOutAll` | Check out all asks first: how many files, and that nobody else can save them | folder `Intake`, dialog `checkOutAll`, scrolled to the team files | Space White | 1280x800 |
| [home-checkOutAll-idea-420x720.png](home-checkOutAll-idea-420x720.png) | `checkOutAll` | Check out all asks first: how many files, and that nobody else can save them | folder `Intake`, dialog `checkOutAll`, scrolled to the team files | IDEA | 420x720 |
| [home-checkOutAll-spaceWhite-420x720.png](home-checkOutAll-spaceWhite-420x720.png) | `checkOutAll` | Check out all asks first: how many files, and that nobody else can save them | folder `Intake`, dialog `checkOutAll`, scrolled to the team files | Space White | 420x720 |
| [home-reportProblem-idea-1280x800.png](home-reportProblem-idea-1280x800.png) | `reportProblem` | Report a problem (from Settings): what kind, the words, and that only file names go with them | dialog `report` | IDEA | 1280x800 |
| [home-reportProblem-spaceWhite-1280x800.png](home-reportProblem-spaceWhite-1280x800.png) | `reportProblem` | Report a problem (from Settings): what kind, the words, and that only file names go with them | dialog `report` | Space White | 1280x800 |
| [home-reportProblem-idea-420x720.png](home-reportProblem-idea-420x720.png) | `reportProblem` | Report a problem (from Settings): what kind, the words, and that only file names go with them | dialog `report` | IDEA | 420x720 |
| [home-reportProblem-spaceWhite-420x720.png](home-reportProblem-spaceWhite-420x720.png) | `reportProblem` | Report a problem (from Settings): what kind, the words, and that only file names go with them | dialog `report` | Space White | 420x720 |
| [home-partialCheckOut-idea-1280x800.png](home-partialCheckOut-idea-1280x800.png) | `partialCheckOut` | A folder checked out, two of its files held by someone else: the answer at the foot | folder `Shooter`, scrolled to the team files, an action's answer at the foot | IDEA | 1280x800 |
| [home-partialCheckOut-spaceWhite-1280x800.png](home-partialCheckOut-spaceWhite-1280x800.png) | `partialCheckOut` | A folder checked out, two of its files held by someone else: the answer at the foot | folder `Shooter`, scrolled to the team files, an action's answer at the foot | Space White | 1280x800 |
| [home-partialCheckOut-idea-420x720.png](home-partialCheckOut-idea-420x720.png) | `partialCheckOut` | A folder checked out, two of its files held by someone else: the answer at the foot | folder `Shooter`, scrolled to the team files, an action's answer at the foot | IDEA | 420x720 |
| [home-partialCheckOut-spaceWhite-420x720.png](home-partialCheckOut-spaceWhite-420x720.png) | `partialCheckOut` | A folder checked out, two of its files held by someone else: the answer at the foot | folder `Shooter`, scrolled to the team files, an action's answer at the foot | Space White | 420x720 |
| [home-myOtherComputer-idea-1280x800.png](home-myOtherComputer-idea-1280x800.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | IDEA | 1280x800 |
| [home-myOtherComputer-spaceWhite-1280x800.png](home-myOtherComputer-spaceWhite-1280x800.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | Space White | 1280x800 |
| [home-myOtherComputer-idea-420x720.png](home-myOtherComputer-idea-420x720.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | IDEA | 420x720 |
| [home-myOtherComputer-spaceWhite-420x720.png](home-myOtherComputer-spaceWhite-420x720.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | Space White | 420x720 |
| [home-emptyFolder-idea-1280x800.png](home-emptyFolder-idea-1280x800.png) | `emptyFolder` | An empty folder | folder `Intake/Rollers`, scrolled to the team files | IDEA | 1280x800 |
| [home-emptyFolder-spaceWhite-1280x800.png](home-emptyFolder-spaceWhite-1280x800.png) | `emptyFolder` | An empty folder | folder `Intake/Rollers`, scrolled to the team files | Space White | 1280x800 |
| [home-emptyFolder-idea-420x720.png](home-emptyFolder-idea-420x720.png) | `emptyFolder` | An empty folder | folder `Intake/Rollers`, scrolled to the team files | IDEA | 420x720 |
| [home-emptyFolder-spaceWhite-420x720.png](home-emptyFolder-spaceWhite-420x720.png) | `emptyFolder` | An empty folder | folder `Intake/Rollers`, scrolled to the team files | Space White | 420x720 |
| [home-moreNotices-idea-1280x800.png](home-moreNotices-idea-1280x800.png) | `moreNotices` | The other notices: a file taken back, files Armory can't read, a check in that left two out |  | IDEA | 1280x800 |
| [home-moreNotices-spaceWhite-1280x800.png](home-moreNotices-spaceWhite-1280x800.png) | `moreNotices` | The other notices: a file taken back, files Armory can't read, a check in that left two out |  | Space White | 1280x800 |
| [home-moreNotices-idea-420x720.png](home-moreNotices-idea-420x720.png) | `moreNotices` | The other notices: a file taken back, files Armory can't read, a check in that left two out |  | IDEA | 420x720 |
| [home-moreNotices-spaceWhite-420x720.png](home-moreNotices-spaceWhite-420x720.png) | `moreNotices` | The other notices: a file taken back, files Armory can't read, a check in that left two out |  | Space White | 420x720 |
| [home-manyMine-idea-1280x800.png](home-manyMine-idea-1280x800.png) | `manyMine` | Everything checked out: 1,400 files in My files, in a box of their own with Check in all on top, so Team files stays right under it |  | IDEA | 1280x800 |
| [home-manyMine-spaceWhite-1280x800.png](home-manyMine-spaceWhite-1280x800.png) | `manyMine` | Everything checked out: 1,400 files in My files, in a box of their own with Check in all on top, so Team files stays right under it |  | Space White | 1280x800 |
| [home-manyMine-idea-420x720.png](home-manyMine-idea-420x720.png) | `manyMine` | Everything checked out: 1,400 files in My files, in a box of their own with Check in all on top, so Team files stays right under it |  | IDEA | 420x720 |
| [home-manyMine-spaceWhite-420x720.png](home-manyMine-spaceWhite-420x720.png) | `manyMine` | Everything checked out: 1,400 files in My files, in a box of their own with Check in all on top, so Team files stays right under it |  | Space White | 420x720 |
| [home-bigProject-idea-1280x800.png](home-bigProject-idea-1280x800.png) | `bigProject` | A folder of 5,000 files, drawn a screenful at a time | folder `CopyDesignTemp`, scrolled to the team files | IDEA | 1280x800 |
| [home-bigProject-spaceWhite-1280x800.png](home-bigProject-spaceWhite-1280x800.png) | `bigProject` | A folder of 5,000 files, drawn a screenful at a time | folder `CopyDesignTemp`, scrolled to the team files | Space White | 1280x800 |
| [home-bigProject-idea-420x720.png](home-bigProject-idea-420x720.png) | `bigProject` | A folder of 5,000 files, drawn a screenful at a time | folder `CopyDesignTemp`, scrolled to the team files | IDEA | 420x720 |
| [home-bigProject-spaceWhite-420x720.png](home-bigProject-spaceWhite-420x720.png) | `bigProject` | A folder of 5,000 files, drawn a screenful at a time | folder `CopyDesignTemp`, scrolled to the team files | Space White | 420x720 |

## File detail

| File | State | What it shows | Opened at | Theme | Size |
|---|---|---|---|---|---|
| [detail-synced-idea-1280x800.png](detail-synced-idea-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 1280x800 |
| [detail-synced-spaceWhite-1280x800.png](detail-synced-spaceWhite-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 1280x800 |
| [detail-synced-idea-420x720.png](detail-synced-idea-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 420x720 |
| [detail-synced-spaceWhite-420x720.png](detail-synced-spaceWhite-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 420x720 |
| [detail-checkedOutByMe-idea-1280x800.png](detail-checkedOutByMe-idea-1280x800.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | IDEA | 1280x800 |
| [detail-checkedOutByMe-spaceWhite-1280x800.png](detail-checkedOutByMe-spaceWhite-1280x800.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | Space White | 1280x800 |
| [detail-checkedOutByMe-idea-420x720.png](detail-checkedOutByMe-idea-420x720.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | IDEA | 420x720 |
| [detail-checkedOutByMe-spaceWhite-420x720.png](detail-checkedOutByMe-spaceWhite-420x720.png) | `checkedOutByMe` | Files you checked out, one with changes not checked in | folder `Drivetrain` | Space White | 420x720 |
| [detail-checkedOutByOther-idea-1280x800.png](detail-checkedOutByOther-idea-1280x800.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | IDEA | 1280x800 |
| [detail-checkedOutByOther-spaceWhite-1280x800.png](detail-checkedOutByOther-spaceWhite-1280x800.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | Space White | 1280x800 |
| [detail-checkedOutByOther-idea-420x720.png](detail-checkedOutByOther-idea-420x720.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | IDEA | 420x720 |
| [detail-checkedOutByOther-spaceWhite-420x720.png](detail-checkedOutByOther-spaceWhite-420x720.png) | `checkedOutByOther` | Maria has a plate checked out: you can look, not save | folder `Drivetrain`, scrolled to the team files | Space White | 420x720 |
| [detail-transferring-idea-1280x800.png](detail-transferring-idea-1280x800.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | IDEA | 1280x800 |
| [detail-transferring-spaceWhite-1280x800.png](detail-transferring-spaceWhite-1280x800.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | Space White | 1280x800 |
| [detail-transferring-idea-420x720.png](detail-transferring-idea-420x720.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | IDEA | 420x720 |
| [detail-transferring-spaceWhite-420x720.png](detail-transferring-spaceWhite-420x720.png) | `transferring` | Uploading, downloading and moving at once, each file with its own bar (detail: a file downloading) |  | Space White | 420x720 |
| [detail-notHereYet-idea-1280x800.png](detail-notHereYet-idea-1280x800.png) | `notHereYet` | A file that is not on this computer yet, while the rest come down |  | IDEA | 1280x800 |
| [detail-notHereYet-spaceWhite-1280x800.png](detail-notHereYet-spaceWhite-1280x800.png) | `notHereYet` | A file that is not on this computer yet, while the rest come down |  | Space White | 1280x800 |
| [detail-notHereYet-idea-420x720.png](detail-notHereYet-idea-420x720.png) | `notHereYet` | A file that is not on this computer yet, while the rest come down |  | IDEA | 420x720 |
| [detail-notHereYet-spaceWhite-420x720.png](detail-notHereYet-spaceWhite-420x720.png) | `notHereYet` | A file that is not on this computer yet, while the rest come down |  | Space White | 420x720 |
| [detail-groupedNotices-idea-1280x800.png](detail-groupedNotices-idea-1280x800.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | IDEA | 1280x800 |
| [detail-groupedNotices-spaceWhite-1280x800.png](detail-groupedNotices-spaceWhite-1280x800.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | Space White | 1280x800 |
| [detail-groupedNotices-idea-420x720.png](detail-groupedNotices-idea-420x720.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | IDEA | 420x720 |
| [detail-groupedNotices-spaceWhite-420x720.png](detail-groupedNotices-spaceWhite-420x720.png) | `groupedNotices` | Notices grouped by kind, one card each, lists closed |  | Space White | 420x720 |
| [detail-takeBack-idea-1280x800.png](detail-takeBack-idea-1280x800.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 1280x800 |
| [detail-takeBack-spaceWhite-1280x800.png](detail-takeBack-spaceWhite-1280x800.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | Space White | 1280x800 |
| [detail-takeBack-idea-420x720.png](detail-takeBack-idea-420x720.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | IDEA | 420x720 |
| [detail-takeBack-spaceWhite-420x720.png](detail-takeBack-spaceWhite-420x720.png) | `takeBack` | As a mentor: force check in a file someone else has checked out (on its row, its page and the folder keys) | folder `Drivetrain`, selected `Plate-Left.SLDPRT`, scrolled to the team files | Space White | 420x720 |
| [detail-takeBackConfirm-idea-1280x800.png](detail-takeBackConfirm-idea-1280x800.png) | `takeBackConfirm` | As a mentor, forcing a check in: who has it, and what happens to the changes not checked in | dialog `takeBack` | IDEA | 1280x800 |
| [detail-takeBackConfirm-spaceWhite-1280x800.png](detail-takeBackConfirm-spaceWhite-1280x800.png) | `takeBackConfirm` | As a mentor, forcing a check in: who has it, and what happens to the changes not checked in | dialog `takeBack` | Space White | 1280x800 |
| [detail-takeBackConfirm-idea-420x720.png](detail-takeBackConfirm-idea-420x720.png) | `takeBackConfirm` | As a mentor, forcing a check in: who has it, and what happens to the changes not checked in | dialog `takeBack` | IDEA | 420x720 |
| [detail-takeBackConfirm-spaceWhite-420x720.png](detail-takeBackConfirm-spaceWhite-420x720.png) | `takeBackConfirm` | As a mentor, forcing a check in: who has it, and what happens to the changes not checked in | dialog `takeBack` | Space White | 420x720 |
| [detail-myOtherComputer-idea-1280x800.png](detail-myOtherComputer-idea-1280x800.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | IDEA | 1280x800 |
| [detail-myOtherComputer-spaceWhite-1280x800.png](detail-myOtherComputer-spaceWhite-1280x800.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | Space White | 1280x800 |
| [detail-myOtherComputer-idea-420x720.png](detail-myOtherComputer-idea-420x720.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | IDEA | 420x720 |
| [detail-myOtherComputer-spaceWhite-420x720.png](detail-myOtherComputer-spaceWhite-420x720.png) | `myOtherComputer` | A file checked out on my other computer: amber, and how to get it here | folder `Drivetrain`, scrolled to the team files | Space White | 420x720 |

## Settings sheet over Home

| File | State | What it shows | Opened at | Theme | Size |
|---|---|---|---|---|---|
| [settings-synced-idea-1280x800.png](settings-synced-idea-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 1280x800 |
| [settings-synced-spaceWhite-1280x800.png](settings-synced-spaceWhite-1280x800.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 1280x800 |
| [settings-synced-idea-420x720.png](settings-synced-idea-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | IDEA | 420x720 |
| [settings-synced-spaceWhite-420x720.png](settings-synced-spaceWhite-420x720.png) | `synced` | Everything up to date: the file browser at a project's top folder |  | Space White | 420x720 |

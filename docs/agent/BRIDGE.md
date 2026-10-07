# Agent window bridge (v2)

The Armory Agent window is WebView2 hosting a local page shipped with the app
(`src/Armory.Agent/wwwroot/`). It loads nothing from the network: no CDN, no web
fonts, no remote images. The page talks to the engine only through this bridge.

Transport: `window.chrome.webview.postMessage(object)` from the page, and
`CoreWebView2.PostWebMessageAsJson(json)` from the host. Every message is one JSON
object with a `type` field. Names are camelCase; data objects use `kind` and
`direction`, never a `type` field. The C# records live in
`src/Armory.Agent.Engine/View/AgentView.cs`; `wwwroot/bridge.js` mirrors them in JSDoc.
`AgentViewContractTests` keeps the two lists of message types (`BridgeMessages.HostToPage`
and `PageToHost` in C#, `HOST_TO_PAGE` and `PAGE_TO_HOST` in bridge.js) equal, and
requires every type the page sends or reads to be on them.

Dropped files are the one exception to plain JSON: the page sends `dropFiles` with
`window.chrome.webview.postMessageWithAdditionalObjects(message, files)`, where `files`
is the drop's `FileList`, and the host reads `CoreWebView2File.Path` from each of the
event's `AdditionalObjects`.

When the page runs outside WebView2 (a plain browser, for screenshots), `bridge.js`
uses a demo transport that answers from `wwwroot/demo/states.js`. See "The demo" below.

## Screens

- **Connect** (first run): one key, Connect this computer; then the browser sign-in
  and its status plate; or, when the Armory folder belongs to another account, a
  one-click folder of the student's own.
- **Home**: on the left the status display (its line is the activity line while files
  move) with Pause sending or Resume sending under it, and This computer (how many team
  files are up to date here, who is signed in, Sign out). On the right, in one
  scrolling column: the selection bar (while files are selected), the quiet check-out
  question (`prompt`), Right now (`activity`), the notices (one card per kind), My
  files (the files this computer has checked out), and Team files: project
  tabs, then the open project's card with where you are (Project › Folder ›
  Subfolder), the folder's keys (New folder, Add files, Rename folder, Delete folder,
  Check out all, Check in all), and its folder rows and file rows. Files dragged from
  File Explorer drop into the open folder.
- **File detail**: the file's display (its state and who has it), Open as the primary
  key, then Check out, Check out and open, Check in, Undo check out or Take back as its
  state allows, Show in folder as a quiet link, Checked out (the person and computer,
  or "Available. Check it out to make changes."), and the history.
- **Settings** is a sheet over Home with exactly the folder (and Change), Start Armory
  when I sign in, and the theme.
- **The small dialog** (`<dialog id="ask">`) asks New folder, Rename folder, Delete
  folder and Take back. It is filled once when it opens and never redrawn by a host
  message, so typed words stay.

Every file row shows who has it checked out, always: "Checked out by you" or "Checked
out by Maria Lopez on LAB-PC-07" as a tag with the person's initials, or "Available" as
plain text. Every file row has an Open key and a select key (a 44px `role="checkbox"`);
a file that isn't in Armory yet shows "Not in Armory" instead of a check out and has no
select key (nothing can be checked out or in until it is added). Long lists (a folder,
My files, a notice's files) draw only the rows near the view at one fixed row height,
so a folder of 5,000 files keeps well under 150 rows in the page.

## Host to page

| `type` | fields | when |
|---|---|---|
| `view` | `view: AgentView` | on `ready` and whenever anything changes (at most every 500 ms during a pass); the page redraws from it |
| `fileDetail` | `detail: FileDetailView` | the answer to `openFile` |
| `activity` | `activity: ActivityView` | while files move, at most 4 a second; the page patches only Right now and the status line, so focus, scroll and typing never move |
| `actionResult` | `requestId`, `ok`, `message` | once for each action (see Page to host); the page shows `message` in a quiet line at the window's foot, never an alert and never a focus change |

```
AgentView {
  connection: "signedOut" | "connecting" | "signedIn" | "vaultOwnedByOther"
  connect: ConnectView
  account: AccountView | null
  sync: SyncView
  activity: ActivityView
  vaultRoot: string                 // e.g. C:\IDEA\Armory
  notices: NoticeGroupView[]        // at most one per kind
  prompt: PromptView | null
  myFiles: MyFileView[]             // the files THIS computer has checked out, in any project (archived ones too)
  projects: ProjectView[]
  settings: SettingsView
  effectiveTheme: "idea" | "spaceWhite"
}
ConnectView { phase: "idle" | "waitingForBrowser" | "finishing" | "failed", message: string | null }
AccountView { email: string, deviceName: string }
SyncView {
  state: "synced" | "syncing" | "offline" | "paused" | "attention",
  line: string,                     // e.g. "Everything is saved to Armory."
  detail: string | null,            // e.g. "Last checked 2 minutes ago."
  pendingCount: number              // files waiting; the page says so when detail is null
}

ActivityView {
  line: string | null,              // e.g. "Downloading 412 of 1,280 files, 2.1 GB left, about 3 min"
  upload: DirectionView | null,
  download: DirectionView | null,
  move: DirectionView | null,
  waiting: WaitingView | null,
  active: ActiveTransferView[]      // at most 8, each drawn with its own progress track
}
DirectionView {
  filesDone: number, filesTotal: number, bytesDone: number, bytesTotal: number,
  bytesPerSecond: number,
  secondsLeft: number | null,       // null until 3 seconds and 2 files have gone by
  line: string                      // e.g. "Uploading 3 of 9 files, 48 MB left, about 20 sec", "Moving 120 files to Gearbox"
}
WaitingView { count: number, line: string }
  // "3 files are waiting to upload. They upload when this computer is back online."
  // "2 checked-out files have changes. Check them in to share them."
ActiveTransferView { path: string, name: string, direction: "upload" | "download" | "move", bytesDone: number, bytesTotal: number }

NoticeGroupView {
  key: string,                      // dismissNotice sends it back
  kind: "import" | "nameShared" | "newerWaiting" | "keptCopy" | "takenBack" | "folderPutBack"
      | "projectPutBack" | "projectRenaming" | "cantSend" | "cantRead" | "checkInPartial",
  tone: "info" | "look" | "bad",    // the page shows info green, look amber, bad red
  title: string,                    // e.g. "14 files share a name with other files in this project"
  detail: string,
  count: number,                    // the total; items holds at most 200
  action: NoticeActionView | null,  // the card's one key
  items: NoticeItemView[]           // the card's list, opened by its own key
}
NoticeActionView { label: string, command: string, paths: string[] }
  // command is a page-to-host type the page sends for it:
  //   checkOut {paths, open: false}, checkIn {paths}, undoCheckOut {paths},
  //   launchFile {path: paths[0]}, showInFolder {path: paths[0]}, dismissNotice {key},
  //   openFile (the first item's fileId)
  // or "expand", which the page handles itself: it opens and closes the card's list.
NoticeItemView { fileId: string | null, path: string, name: string, detail: string | null }

PromptView {                        // SolidWorks opened a file this computer hasn't checked out
  key: string,                      // one per open ("prompt:<path>:<when it opened>"); dismissNotice {key} hides this one only
  fileId: string | null, path: string, name: string,
  checkout: CheckoutView,
  canCheckOut: boolean              // false when someone else has it: the card says who
}
CheckoutView {
  state: "available" | "mine" | "other" | "myOtherComputer",
  label: string,                    // "Checked out by you", "Checked out by Maria Lopez on LAB-PC-07", "Available"
  name: string | null, email: string | null, device: string | null,
  since: string | null              // ISO-8601
}

MyFileView { fileId: string | null, path: string, name: string, project: string, status: FileStatus, note: string | null, checkout: CheckoutView }
ProjectView {
  id: string, name: string,
  archived: boolean,                // shown as "Archived. It no longer updates." with no keys
  role: string,                     // student, cad_lead, mentor, instructor
  canTakeBack: boolean,             // a mentor or CAD lead
  folders: FolderView[]             // flat: every folder once, empty ones too; "" is the project's top
}
FolderView { path: string, name: string, fileCount: number, files: FileRowView[] }
  // path is in the project ("Drivetrain/Gearbox"); fileCount is the files directly in it.
  // A file's own path is vault-relative ("Robot 2027/Drivetrain/Gearbox/Shaft.SLDPRT").
FileRowView {
  fileId: string | null,            // null for a file in the folder that isn't in Armory
  name: string, path: string,
  status: FileStatus,
  checkout: CheckoutView,
  changed: boolean,                 // its bytes here differ from the last check in
  releaseNotChecked: boolean,       // shown only on File detail, as a small tag
  updatedAt: string | null, updatedBy: string | null
}
FileStatus = "synced" | "changed" | "uploading" | "downloading" | "waiting" | "newerWaiting"
           | "keptCopy" | "notInArmory" | "notOnThisComputer"
SettingsView { vaultRoot: string, startAtSignIn: boolean, theme: "system" | "idea" | "spaceWhite" }

FileDetailView {
  fileId: string, name: string, path: string, project: string, folder: string,
  status: FileStatus, checkout: CheckoutView, releaseNotChecked: boolean,
  canTakeBack: boolean,
  history: HistoryEntryView[]       // newest first
}
HistoryEntryView {
  id: string, kind: "version" | "keptCopy" | "removed",
  author: string, at: string, bytes: number,
  note: string,                     // e.g. "Checked in", "Kept as your own copy: Maria Lopez checked in first"
  releaseNotChecked: boolean, isCurrent: boolean
}
```

"SolidWorks year not checked" is never a notice: File detail shows it as a small tag
beside the file's place, and on the history entries it applies to.

## Page to host

Paths are vault-relative with forward slashes. A path in `checkOut`, `checkIn` or
`undoCheckOut` may be a folder, which means every file under it. `projectId` is a
project's id; `parent` and `folder` are paths in that project ("" for its top).

An **action** carries a `requestId` (bridge.js makes one, `r1`, `r2`, ...) and the host
answers it with exactly one `actionResult` carrying the same id and a plain sentence
("Checked out 12 of 14 files. Maria Lopez has 2 of them checked out.", "Checked in
Plate.SLDPRT.", "Close Plate.SLDPRT in SolidWorks first."). The actions are
`launchFile`, `checkOut`, `checkIn`, `undoCheckOut`, `takeBack`, `createFolder`,
`renameFolder`, `deleteFolder`, `addFiles` and `dropFiles`.

| `type` | fields | sent by | effect |
|---|---|---|---|
| `ready` | | the page, once, first | host answers with `view` |
| `connect` | | Connect this computer, Try again, Open the browser again | starts the browser sign-in for this computer |
| `cancelConnect` | | Cancel while waiting | stops waiting for the browser |
| `signOut` | | Sign out of Armory | forgets this computer's sign-in (files stay) |
| `pause` / `resume` | | Pause sending, Resume sending | stops or restarts uploading and downloading |
| `openVault` | | Open Armory folder | opens the Armory folder in File Explorer |
| `openFile` | `fileId` | a file row, a notice item, a My files row | host answers with `fileDetail` (the page shows File detail) |
| `launchFile` | `path`, `requestId` | Open (rows, File detail, a notice) | opens the file in its own program (SolidWorks for a part); refuses programs and scripts |
| `showInFolder` | `path` | Show in folder, a row for a file that isn't in Armory | opens File Explorer with the file selected |
| `checkOut` | `paths`, `open`, `requestId` | Check out (File detail, the selection bar, the question), Check out and open (`open: true`), Check out all (the folder's path) | takes each file to change it, makes it writable here, downloads a newer version first |
| `checkIn` | `paths`, `requestId` | Check in (File detail, My files, the selection bar), Check in all | uploads the changes, makes the file read-only, lets it go |
| `undoCheckOut` | `paths`, `requestId` | Undo check out (File detail, the selection bar) | puts back the version from before the check out (changes are kept in the history), lets it go |
| `takeBack` | `fileId`, `requestId` | Take back, after the small dialog asks (mentors and CAD leads) | takes a check out away from its holder; their changes are kept in the history |
| `createFolder` | `projectId`, `parent`, `name`, `requestId` | New folder, after the small dialog | makes the folder |
| `renameFolder` | `projectId`, `folder`, `newName`, `requestId` | Rename folder, after the small dialog | renames it for everyone (refused, and put back, when someone else has a file in it checked out) |
| `deleteFolder` | `projectId`, `folder`, `requestId` | Delete folder, after the small dialog | removes it and its files for everyone; their history is kept |
| `addFiles` | `projectId`, `folder`, `requestId` | Add files | host shows a file picker, then copies the files in |
| `dropFiles` | `projectId`, `folder`, `requestId` (+ the dropped files) | a drop on the open folder's list | host copies the dropped files in |
| `dismissNotice` | `key` | a notice's Done or OK (`dismissNotice` action) | the host drops that notice card |
| `saveSettings` | `vaultRoot`, `startAtSignIn`, `theme` | a setting, Use (a folder of my own) | saves settings; host answers with `view` |
| `chooseVaultRoot` | | Change, Choose another folder | host shows a folder picker, then answers with `view` |

"Not now" on the check-out question sends nothing: the page hides that question until
the host asks about another file.

## The demo

Outside WebView2, `?state=<name>` picks a demo state (`demo/states.js`), `theme=idea`,
`spaceWhite` or `space-white` the theme, `screen=home|detail|connect|settings` the
screen and `file=<fileId>` the file on File detail. The page-only places, so every
state can be drawn without a click, are `project=<projectId>`, `folder=<path in the
project>`, `select=<name>,<name>` (files in that folder), `expand=<notice key>`,
`dialog=newFolder|renameFolder|deleteFolder|takeBack`, `drag=1` (files held over the
list) and `at=browser` (Home scrolled to Team files). The demo transport answers every
page-to-host type the way the engine would (a check out changes the rows and answers
with an `actionResult`); `openVault`, `showInFolder`, `addFiles` and `dropFiles` only
log, since a browser has no File Explorer to open.

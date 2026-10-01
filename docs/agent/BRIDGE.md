# Agent window bridge

The Armory Agent window is WebView2 hosting a local page shipped with the app
(`src/Armory.Agent/wwwroot/`). It loads nothing from the network: no CDN, no web
fonts, no remote images. The page talks to the engine only through this bridge.

Transport: `window.chrome.webview.postMessage(json)` from the page, and
`CoreWebView2.PostWebMessageAsJson(json)` from the host. Every message is one JSON
object with a `type` field. Names are camelCase. The C# records live in
`src/Armory.Agent.Engine/View/AgentView.cs`; `wwwroot/bridge.js` mirrors them in
JSDoc. A test (`AgentViewContractTests`) keeps the two lists of message types equal.

When the page runs outside WebView2 (a plain browser, for screenshots), `bridge.js`
uses a demo transport that answers from `wwwroot/demo/states.js`. The query string
`?state=<name>&theme=idea|space-white` picks the state.

## Host to page

### `{"type": "view", "view": AgentView}`

Sent on load and whenever anything changes. The page re-renders from it alone.

```
AgentView {
  connection: "signedOut" | "connecting" | "signedIn" | "vaultOwnedByOther"
  connect: { phase: "idle" | "waitingForBrowser" | "finishing" | "failed", message: string | null }
  account: { email: string, deviceName: string } | null
  sync: {
    state: "synced" | "syncing" | "offline" | "paused" | "attention",
    line: string,            // plain student sentence, e.g. "Everything is saved to Armory."
    detail: string | null,   // e.g. "Last checked 2 minutes ago" or "3 changes will send when you're back online"
    pendingCount: number
  }
  vaultRoot: string          // e.g. C:\IDEA\Armory
  myFiles: MyFile[]          // what I hold and what is not yet on the server
  needsMe: Attention[]       // newer versions waiting, side versions I made, refused uploads
  projects: Project[]
  settings: { vaultRoot: string, startAtSignIn: boolean, theme: "system" | "idea" | "spaceWhite" }
  effectiveTheme: "idea" | "spaceWhite"
}

MyFile {
  fileId: string | null,     // null until the server has the file
  path: string,              // vault-relative, forward slashes, e.g. "Robot 2027/Drivetrain/Gearbox.SLDASM"
  name: string,
  project: string,
  status: FileStatus,
  note: string | null        // plain sentence, e.g. "You are editing this. It saves to Armory when you save."
}

Attention {
  kind: "newerWaiting" | "sideVersion" | "refused" | "lockBroken" | "nameTaken" | "releaseNotChecked",
  fileId: string | null,
  path: string,
  name: string,
  title: string,             // e.g. "A newer version from Maria is waiting"
  detail: string,            // e.g. "Close Gearbox.SLDASM in SolidWorks to get it."
  at: string | null          // ISO-8601
}

Project {
  id: string, name: string,
  folders: Folder[]          // root folder has path ""
}
Folder { path: string, name: string, files: FileRow[] }

FileRow {
  fileId: string, name: string, path: string,
  status: FileStatus,
  holder: Holder | null,     // who is editing it, when someone is
  releaseNotChecked: boolean,
  updatedAt: string | null, updatedBy: string | null
}
Holder { name: string, email: string, device: string, since: string, isMe: boolean, isMyOtherComputer: boolean, savedToArmory: boolean }

FileStatus = "synced" | "syncing" | "waitingToSend" | "editingByMe" | "editingByOther"
           | "newerWaiting" | "conflict" | "refused" | "notOnThisComputer"
```

### `{"type": "fileDetail", "detail": FileDetail}`

Answer to `openFile`.

```
FileDetail {
  fileId: string, name: string, path: string, project: string, folder: string,
  status: FileStatus, holder: Holder | null, releaseNotChecked: boolean,
  history: HistoryEntry[]    // newest first
}
HistoryEntry {
  id: string, kind: "version" | "sideVersion",
  author: string, at: string, bytes: number,
  note: string,              // e.g. "Saved", "Kept as Alex's own copy: someone else saved first"
  releaseNotChecked: boolean,
  isCurrent: boolean
}
```

## Page to host

| `type` | fields | effect |
|---|---|---|
| `ready` | | host answers with `view` |
| `connect` | | starts the browser sign-in for this computer |
| `cancelConnect` | | stops waiting for the browser |
| `signOut` | | forgets this computer's sign-in (files stay) |
| `pause` / `resume` | | pauses or resumes syncing |
| `openVault` | | opens the vault folder in Explorer |
| `openFile` | `fileId` | host answers with `fileDetail` |
| `showInFolder` | `path` | opens Explorer with the file selected |
| `saveSettings` | `vaultRoot`, `startAtSignIn`, `theme` | saves settings; host answers with `view` |
| `chooseVaultRoot` | | host shows a folder picker, then answers with `view` |

## Screens

Only three screens: **Connect** (first run, one button), **Home** (status line, My
files, projects and folders, Needs you), and **File detail** (history, who holds it,
Show in folder). Settings is a sheet over Home with exactly two settings plus the
theme override. Clicking a row that opens detail moves the view to that detail.

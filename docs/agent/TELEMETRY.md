# Telemetry and Report a problem

When something glitches on a student's computer, the instance is kept as data a developer
(a Claude Code session in this repository) can read directly, with no strain on the app. The
app half is built and works on its own today; the website half (sections 4 and 4b of
[website-requests-v0.3.md](website-requests-v0.3.md)) is pending, and until it is live the
incidents simply wait on the computer.

| Piece | Where |
|---|---|
| Flight recorder, glitch rules, throttle, incident files, last flight | `src/Armory.Telemetry` (no network, no UI) |
| What the RPC and storage clients record; the uploader | `src/Armory.Client` (`PostgrestClient`, `BlobClient`, `IncidentUploader`) |
| What the engine records; its snapshot | `src/Armory.Agent.Engine/SyncEngine.Telemetry.cs` and the hooks it names |
| Composition, crash handlers, Report a problem | `src/Armory.Agent` (`AgentTelemetry`, `Program`, `AgentHost`, `Bridge`) |
| The reader | `tools/read-incident/read_incident.py` |

`Armory.Core` is untouched: it stays free of network, disk and UI.

## What is collected

A **flight recorder** keeps the last 4,000 events in memory: a ring allocated once at start,
written in place under one uncontended lock. Recording never touches the disk or the network
and allocates nothing (`FlightRecorderTests` measures it: about 85 ns per event in a Debug
build, 56 ns in Release, and 40 bytes in total for a million events). Nothing leaves the
ring unless an incident is saved.

| Event | Recorded by | Fields |
|---|---|---|
| `passStart` | the engine | pass kind (`loop`, `action`, `whole`) |
| `passPhase` | the engine | phase (`scan`, `server`, `plan`, `move`, `finish`), ms since the last phase |
| `passEnd` | the engine | pass kind, ok, ms, downloaded, uploaded, kept copies, refused |
| `passYield` | the engine | why a loop pass gave way (`slice`: its 8 seconds were up; `action`: a window action waited), units left, ms |
| `rpc` | `PostgrestClient`, `BlobClient` (`blob-url`) | function name, ms, HTTP status (0 when none), error code (`offline`, `signedOut`, `canceled`, a SQLSTATE or PostgREST code) |
| `transfer` | `BlobClient` | `upload` or `download`, bytes, ms, ok, status, error |
| `windowAction` | `Bridge` | action type, how many files or folders it named, ms from the window's request to its answer (Add files: from when the picker closed), ok |
| `notice` | the engine | every problem, and the first 200 notices of a pass: kind, path, the raw text the log gets |
| `fileFailed` | the engine | path, exception type, message, stack |
| `fileRecovered` | the engine | path of a file that failed before and went through |
| `exception` | the engine (loop, engine thread), the bridge, the crash handlers | type, where, message, stack, fatal |
| `readOnlyBroken` | the engine (`ApplyReadOnly`) | a file the read-only rule had made read-only, found writable again (and made read-only) |
| `repairedCheckout` | the engine (`AdoptMyLocks`, cfb37e2) | a lock this computer holds that had no record here, now shown as checked out by you |

**Never collected:** file contents, tokens (access, refresh, the anon key), signed storage
URLs, request or response bodies. File names and vault paths are kept: they are what makes an
incident readable. Every string an incident or the last flight keeps passes the agent's
`Redactor` (JWTs, Bearer values, token parameters, long random runs) and loses this computer's
own tokens and anon key by exact match. `AgentTelemetryTests` signs in with known tokens,
scatters them through events, log lines, the snapshot and a person's words, and finds none in
the files. (The Redactor also masks any unbroken run of 32 or more letters, digits, `-` and
`_`, so a very long file name without a dot or space can read `[redacted]`.)

## Triggers

Each rule is a pure function in `GlitchRules` with its own test (`GlitchRuleTests`).

| Kind | When |
|---|---|
| `crash` | an exception nothing handled: the process (`AppDomain.UnhandledException`, saved on the spot before the process ends), the window's thread, the engine's loop or thread; and, at the next start, a run that ended without writing "stopped" in agent.log (e02d811), built from the last flight it left |
| `slowAction` | a window action whose answer took more than 10 seconds |
| `slowPass` | a pass that took more than 60 seconds |
| `repeatedFailure` | the same file failing 3 times with no success of it between (the count starts again after each incident) |
| `repairedCheckout` | any `repairedCheckout` event |
| `readOnlyBroken` | any `readOnlyBroken` event |
| `userReport` | Report a problem (below) |

**Throttles.** At most one incident per kind per 10 minutes on a computer, counted across
restarts from the files on disk; a person's own report is never held back. At most 20
incident files are kept (files the site already has go first, then the oldest). Each file is
under 200 KB compressed: the oldest events go first, then the oldest log lines, then the
stacks of events other than the trigger.

The rules run on the thread that recorded the event (a switch and a comparison); an incident is
built and written on a pool thread, never on the engine's. Its engine snapshot is asked of the
engine thread with a 3-second deadline; a late one is written as late, never waited for.

## The incident file

`%LOCALAPPDATA%\IDEA Armory\incidents\<utc>-<kind>.json.gz`, for example
`20261007T180115000Z-slowPass.json.gz`. Renamed `.sent.json.gz` once the site has it and
`.held.json.gz` if the site refused it for good. Settings has an **Open incidents folder** link,
so a person can hand the files over by hand today.

```jsonc
{
  "schemaVersion": 1,
  "id": "a local uuid",
  "createdAt": "2026-10-07T18:01:15.000Z",
  "kind": "slowPass",
  "summary": "A loop pass took 74.0 s: 12 downloaded, 0 uploaded, 0 kept copies, 0 refused.",   // at most 500 characters
  "appVersion": "0.3.0", "osVersion": "Microsoft Windows 10.0.22631 (X64)", "deviceName": "LAB-PC-07", "email": "alex.kim@students.test",
  "projectId": null,
  "feedback": null,              // { "kind": "bug", "body": "..." } for a userReport
  "feedbackId": null,            // the site's id once the words were sent
  "trigger": { "seq": 4012, "at": "...", "kind": "passEnd", ... },
  "flight": { "capacity": 4000, "recorded": 4012, "trimmed": 0, "events": [ ...oldest first... ] },
  "snapshot": {                  // SyncEngine.DescribeAsync, plus what the host knows
    "connection": "signedIn", "sync": { "state": "syncing", "line": "...", "pendingCount": 3 },
    "files": 412, "filesByStatus": { "synced": 400, "downloading": 12 },
    "checkedOutHere": 2, "checkedOutHerePaths": [ "..." ], "notices": [ ... ], "settings": { "vaultRoot": "C:\\IDEA\\Armory", ... },
    "engine": { "online": true, "paused": false, "inPass": true, "records": 412, "projects": [ ... ], ... },
    "pendingRequests": { "checkOut": 0, "checkIn": 1, "undo": 0, "savesWaiting": 0, ... },
    "lastPasses": [ ...the last three passes... ],
    "host": { "runtimeProblem": null, "connectPhase": "idle", "signedIn": true, "transfersRunning": 6 }
  },
  "log": [ "...the last 300 lines of agent.log..." ]
}
```

**The last flight.** A stack overflow ends the process with no chance to write anything. So
while passes run, the last 500 events are written to `incidents\last-flight.json.gz` at most
once a minute, on a pool thread; a clean stop deletes it. When agent.log shows the last run
ended without "stopped", the next start saves a `crash` incident from it (trigger
`previousRunEnded`, with the last log line).

## Upload

`IncidentUploader` runs in the background: one incident at a time, oldest first, at most one a
minute, and only while no upload or download is running. A report a person wrote goes first
through `armory_submit_app_feedback`, then its incident through `armory_submit_app_incident`
with `p_feedback` set to the feedback's id. A report over 900 KB as JSON is trimmed (oldest
events first) under the site's 1 MB limit.

The RPCs, exactly as the website request names them:

```
armory_submit_app_feedback(p_kind text, p_body text, p_app_version text, p_device_name text, p_context jsonb) returns uuid
armory_submit_app_incident(p_kind text, p_summary text, p_app_version text, p_device_name text, p_project uuid,
                           p_report jsonb, p_feedback uuid) returns uuid
```

**Until the site has them** PostgREST answers 404 `PGRST202` (function not found). The file
stays queued, the uploader does not ask for that RPC again for 6 hours (remembered in
`incidents\upload-wait.json`, so restarts do not ask more often), and nobody sees an error.
Offline, signed out or a busy site: tried again on the next round. A refusal for good (400,
413, invalid input): the file is kept as `.held` and never sent again.

## Report a problem

Settings, "Something not working?": **Report a problem** opens a small dialog over Home: Bug,
Idea or Other, the words (up to 8,000 characters; empty words are refused in the page), Send.
The window sends `reportProblem { kind, body }`; the host saves a fresh `userReport` incident
with the words in it, then tries to send the words at once. The answer at the window's foot:

| What happened | The sentence |
|---|---|
| the site took the words | Sent. Thank you for telling us. |
| the site's RPC is not live yet | Saved. It will be sent when the website is ready. |
| offline or signed out | Saved. It will be sent when this computer is back online. |

The incident follows on the uploader's next round, linked to the words.

## Reading one

```
python3 tools/read-incident/read_incident.py <file or folder or zip> [--events 80] [--log 40] [--slowest 10] [--kind crash] [--json]
```

It reads an incident file (`.json.gz`, `.sent.json.gz`, `.held.json.gz`), the same JSON
uncompressed, the website's per-incident `.json` export (the report plus the row's fields) or
its "Download all as zip", or a whole folder. It prints the header (kind, time, app and OS,
who), the summary, the feedback, the trigger, the last events with seconds relative to the
trigger (the trigger marked `>>`), the slowest calls, transfers, window actions and passes,
every error with its stack and every repair, the snapshot, and the end of agent.log. Python 3
standard library only. `ReadIncidentToolTests` runs it on what the app writes.

## The server half (pending)

Specified in [website-requests-v0.3.md](website-requests-v0.3.md), sections 4 (app feedback)
and 4b (automatic incidents): the two tables, the two RPCs above with their rate limits, admin
only reads, an "Armory app" tab and an "Armory incidents" tab on the site's feedback page, the
Markdown and zip exports `read_incident.py` reads, and 90 days of keeping. Nothing in the app
changes when it ships: the next round after the 6-hour wait finds the RPCs and sends what
waited. `ClientTests` plays that moment against the fake Supabase with a stand-in migration in
the test database.

## Tests

`Armory.Telemetry.Tests` (the recorder, each rule, the throttle, the files, the last flight,
the reader), `ClientTests` (the uploader against the fake network, every call recorded without
a token), `TelemetryTests` in `Armory.EndToEnd.Tests` (a pass with its phases, a read-only bit
cleared by hand, a repaired check out, a file failing three passes in a row, the snapshot) and
`AgentTelemetryTests` (no token in any file; Report a problem). The ones a change must keep
are in `tests/GUARDS.txt`. `tools/agent-ui/check-ui.mjs` sends both new window messages from
their controls and walks the dialog.

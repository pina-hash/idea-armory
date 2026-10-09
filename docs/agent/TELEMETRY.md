# Telemetry and Report a problem

When something glitches on a student's computer, the instance is kept as data a developer
(a Claude Code session in this repository) can read directly, with no strain on the app. The
website half is live since idea-app migration 0233 (applied 2026-10-08): notes land at
`/admin/feedback/armory` and incidents at `/admin/feedback/incidents`, read only by a site admin.
The binding spec is idea-app `docs/ARMORY.md`, "The v0.3 server contract", item 4.

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
| `notice` | the engine | every problem, and the first 200 notices of a pass: kind, path, the raw text the log gets. A stale `~$` marker is recorded once while it stays stale, not on every pass (0.3.3: IDEA-06's 224 markers spent every pass's 200) |
| `refusal` | the engine (`SetRefusal`, 0.3.3) | a file's refusal started or changed: path, `refusal` (`nameTaken`, `tooLarge`, `gate`, `refused`) and, for a taken name, `namesake` (the path of the file that holds it); or ended (`refusal: "ended"`). Never again while it stands, so a pass that only re-finds the same refusals records none |
| `fileFailed` | the engine | path, exception type, message, stack |
| `fileRecovered` | the engine | path of a file that failed before and went through |
| `exception` | the engine (loop, engine thread), the bridge, the crash handlers | type, where, message, stack, fatal |
| `readOnlyBroken` | the engine (`ApplyReadOnly`) | a file the read-only rule had made read-only, found writable again (and made read-only) |
| `repairedCheckout` | the engine (`AdoptMyLocks`, cfb37e2) | a lock this computer holds that had no record here, now shown as checked out by you |
| `note` `checkInWaits` | the engine (`ReadBeforeReleaseAsync`, 0.3.3) | a check in, an undo or an add's automatic check in that starts to wait, once: detail "<path>: open" (open in SolidWorks; checked in once closed) or "<path>: unreadable" (the file could not be read; checked in once it can be) |

**Never collected:** file contents, tokens (access, refresh, the anon key), signed storage
URLs, request or response bodies, passwords, and other people's email addresses (every
address but the signed-in person's own reads `[address]`, v0.3: a site admin reads these). File names and vault paths are kept: they are what makes an
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
  "machineId": "3f9c0a7e2b14d865", // 0.3.3: tells apart computers that share a deviceName (below); null off Windows
  "projectId": null,
  "feedback": null,              // { "kind": "bug", "body": "..." } for a userReport; a note adds "tried" and "area" when given
  "feedbackId": null,            // the site's id once the words were sent
  "trigger": { "seq": 4012, "at": "...", "kind": "passEnd", ... },
  "flight": { "capacity": 4000, "recorded": 4012, "trimmed": 0, "events": [ ...oldest first... ] },
  "snapshot": {                  // SyncEngine.DescribeAsync, plus what the host knows
    "connection": "signedIn", "sync": { "state": "syncing", "line": "...", "pendingCount": 3 },
    "files": 412, "filesByStatus": { "synced": 400, "downloading": 12 },
    "checkedOutHere": 2, "checkedOutHerePaths": [ "..." ], "settings": { "vaultRoot": "C:\\IDEA\\Armory", ... },
    "notices": [ { "kind": "nameShared", "title": "...", "count": 148,
                   "items": [ { "path": "...", "detail": "FRC 2026 Off-Season already has WCP-0563.SLDPRT in COTS." }, ...the first 20... ] } ],
    "engine": { "online": true, "paused": false, "inPass": true, "records": 412, "projects": [ ... ], ... },
    "pendingRequests": { "checkOut": 0, "checkIn": 1, "undo": 0, "checkInWhenClosed": 1, "savesWaiting": 0, ... },  // checkInWhenClosed: adds open when added, and (0.3.3) check ins and undos waiting for their file to close or to be read
    "refusals": { "nameTaken": 148, "tooLarge": 0 },   // 0.3.3: files not on the server, by why
    "lastPasses": [ ...the last three passes... ],
    "host": { "runtimeProblem": null, "connectPhase": "idle", "signedIn": true, "transfersRunning": 6 }
  },
  "log": [ "...the last 300 lines of agent.log..." ]
}
```

The snapshot is of the view as it is when the incident is built: the engine publishes a fresh
one first (0.3.3). Before, it read the view last published, which during a long pass could be
the one raised at its first file (IDEA-06's slowPass incidents froze "Uploading 0 of 142 files,
260.9 MB left" for files that were never going to upload).

**machineId.** Two lab computers imaged alike both report `deviceName` IDEA-06. On Windows the
incident carries the first 16 hex characters of the SHA-256 of Windows' own install id
(`HKLM\SOFTWARE\Microsoft\Cryptography`, `MachineGuid`, under Armory's own prefix), never the
id itself: the same for a computer across accounts and reinstalls of Armory, and different for
two computers with one name. Null where there is no such id (`MachineId`, `MachineIdTests`).

**The last flight.** A stack overflow ends the process with no chance to write anything. So
while passes run, the last 500 events are written to `incidents\last-flight.json.gz` at most
once a minute, on a pool thread; a clean stop deletes it. When agent.log shows the last run
ended without "stopped", the next start saves a `crash` incident from it (trigger
`previousRunEnded`, with the last log line).

## Upload

`IncidentUploader` runs in the background: one incident at a time, oldest first, at most one a
minute, and only while no upload or download is running. A report a person wrote goes first
through `armory_submit_app_feedback`, then its incident through `armory_submit_app_incident`
with `p_feedback` set to the feedback's id. The site measures as sent (`pg_column_size` of the
jsonb, larger than its JSON text for many small objects), so the uploader trims well under its
limits: a report to 640 KiB of JSON (oldest events first) under the 1 MiB limit, and a note's
context to 96 KiB (oldest log lines first, then the snapshot) under the 128 KiB limit.

The site's answers (ARMORY.md item 4), read by SQLSTATE and DETAIL.reason, never by the status:

| Answer | What the uploader does |
|---|---|
| `PT429` `{reason: rate_limited, limit, window_seconds, retry_after_seconds}` (20 notes, 30 incidents an hour per account) | keeps the file and sends nothing more for that RPC until `retry_after_seconds` have passed (remembered in `upload-wait.json` across restarts); Report a problem and Send feedback say "Saved. ... it will be sent in a little while." |
| `22023` `too_large` or `too_long` | shortens (a 24 KiB context, a 128 KiB report, a shorter summary and body) and sends once more; the file remembers it, so only the shortened payload is ever sent again; a second such answer holds the file |
| any other `22023` (`kind`, `empty`, `not_object`, `feedback_not_found`) | a bug in this app: logged here with its reason and field, the file kept as `.held`, never sent again |
| any other SQLSTATE | a refusal for good: logged, `.held` |
| 404 `PGRST202` | the RPC is not on the site: kept, asked again in 6 hours |
| offline, signed out, 502/503/504, a gateway's 429 | tried again on the next round |

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

## Send feedback (v0.3, the same as the website's since 0.3.3)

"Send feedback" (a key in the window's header, and in Settings) opens a small dialog: Bug, Idea,
Praise or Other (Idea first), the words, "What did you try?" (optional, up to 1,000
characters), the window or view it is about (filled in by the page, up to 120), an optional
picture of the Armory window, Send (or Ctrl+Enter). The window sends `sendFeedback { kind, body,
tried, area, shot }` (docs/agent/BRIDGE.md).

Without a picture the host saves a note (`<utc>-note.json.gz`, `noteOnly: true`: the words,
`feedback: {kind, body, tried, area}` with tried and area only when given, the snapshot and the
log's last lines, no flight events) and sends it at once through `armory_submit_app_feedback`,
with the app's version and the computer's name. Nothing follows a note: no incident. A note
that can't go now waits in the incidents folder like an incident and goes on a later round,
with what was tried and the area. Tried and area are scrubbed like the words: another person's
address is `[address]`, this computer's tokens and keys `[redacted]`.

**A picture is never written to the incidents folder, or anywhere on disk.** A note with a
picture is composed the same way (`IncidentReporter.ComposeNoteAsync`: the very document a saved
note would hold, scrubbed, never saved) and sent at once with its picture; when it can't go, the
window keeps the words and offers to send the note without the picture, which then takes the
saved path above. The picture itself lives only in the window's memory until it is sent or
replaced (docs/agent/CLIENT.md section 7, "The window's Send feedback"). The flight recorder
keeps each upload as a transfer named `screenshot`: its size and how it ended, never its bytes.

## Report a problem

Settings, "Something not working?": **Report a problem** opens a small dialog over Home: Bug,
Idea or Other, the words (up to 8,000 characters; empty words are refused in the page), Send.
The window sends `reportProblem { kind, body }`; the host saves a fresh `userReport` incident
with the words in it, then tries to send the words at once. The answer at the window's foot:

| What happened | The sentence |
|---|---|
| the site took the words | Sent. Thank you for telling us. (Send feedback: Sent. Thank you for the feedback.) |
| the site's RPC is not live | Saved. It will be sent when the website is ready. |
| offline or signed out | Saved. It will be sent when this computer is back online. |
| PT429, this account's limit for the hour | Saved. You've sent a lot today, so it will be sent in a little while. |

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

## The server half

Live since idea-app 0233 (2026-10-08): `armory_app_feedback` and `armory_app_incidents`, the two
RPCs above with their limits, admin-only reads, the console's "Armory app" and "Armory
incidents" tabs, the per-incident `.json` export (`{format: "idea-armory-incident/1", ...,
report}`, which `read_incident.py` reads) and 90 days of keeping (every submit deletes incidents
older than that). Files that waited on a computer before 0.3 go on the uploader's next round.

## Tests

`Armory.Telemetry.Tests` (the recorder, each rule, the throttle, the files, the last flight,
the reader), `ClientTests` (the uploader against the fake network, every call recorded without
a token), `TelemetryTests` in `Armory.EndToEnd.Tests` (a pass with its phases, a read-only bit
cleared by hand, a repaired check out, a file failing three passes in a row, the snapshot) and
`AgentTelemetryTests` (no token in any file; Report a problem), `MachineIdTests`, `RefusalEventTests` and
`HonestyTests` in `Armory.EndToEnd.Tests` (a refusal in the flight once, with its namesake; a stale
marker once; a snapshot that names the first 20 files of a card and counts refusals by kind). The ones a change must keep
are in `tests/GUARDS.txt`. `tools/agent-ui/check-ui.mjs` sends both new window messages from
their controls and walks the dialog.

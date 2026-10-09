# Armory.Client wire conventions

`src/Armory.Client` is the agent's only network code. It is platform-neutral (`net10.0`)
and runs on Linux. It speaks three things, all from `docs/agent/CONTRACT.md`.

## 1. PostgREST RPC (contract section 1)

```
POST {supabase_url}/rest/v1/rpc/{function}
apikey: {anon_key}
Authorization: Bearer {access_token}
Content-Type: application/json
Accept: application/json

{"p_file": "...", "p_device": "...", "p_operation": "..."}
```

The body is a JSON object of named arguments, using the SQL parameter names. A function
with no arguments gets `{}`. Answers follow PostgREST:

| SQL return type | JSON body |
|---|---|
| `uuid`, `boolean`, `text`, number | the bare JSON scalar, e.g. `"8b0c..."` or `true` |
| `jsonb` | the JSON value itself |
| `returns table(...)` or `setof` | an array of objects with the column names |

Errors are `{"code": sqlstate, "message": text, "details": text|null, "hint": text|null}`
with PostgREST's status mapping: `42501` 403 (401 when anonymous), `23505` and `23503`
409, `P0001` and `22xxx` 400, `42883` 404, `P0xxx` other than `P0001` 500, `08xxx` and
`53xxx` 503, a `PTxyz` code HTTP `xyz` (Armory's `PT429`: 429), and the rest of PostgREST's
table (for example `55000` and `XX000` 500, `42P01` 404). An expired JWT is 401 with code
`PGRST303` (older PostgREST: `PGRST301`). The client maps these to `ArmoryRpcException`
(with `SqlState`, `Message`, `Details`, `Detail`, `Reason`, `Hint`, `Status`), refreshes once
and retries on 401 PGRST30x, and throws `ArmoryOfflineException` for a network failure,
timeout, 502, 503 or 504, and for a 429 that is not `PT429` (a gateway's limit). Callers
branch on `SqlState` and `Reason` (DETAIL.reason), never on `Status` alone: PostgREST answers
`23505` and `23503` both with 409, and class 55 and `P0002` both with 500 (section 6).

A `40P01` (deadlock) or `40001` (serialization failure) means the server rolled the whole call
back, receipt included, so `PostgrestClient` sends the same body (the same operation id) again, up
to `MaximumResends` (3) times, 50, 100 and 150 ms apart, before raising `ArmoryRpcException` with
`IsTransient`. A refusal is an answer and is never sent again. 0232's folder rename and delete can
deadlock with a check out or check in in the same folder (`docs/server/contract.md`, v2).

Every write RPC takes `p_operation`. The caller supplies it; the client never invents one.
The agent derives each id from a Core journal entry or from durable engine state, so a
call replayed after a crash is a receipt hit (see `docs/agent/ENGINE.md`).

## 2. Supabase token refresh (contract 3g)

```
POST {supabase_url}/auth/v1/token?grant_type=refresh_token
apikey: {anon_key}
Content-Type: application/json

{"refresh_token": "..."}
```

200 answers `{"access_token", "token_type": "bearer", "expires_in", "expires_at", "refresh_token", "user": {"email", ...}}`.
`expires_at` is Unix seconds. Supabase rotates the refresh token, so the client writes the
new session to `ISecretStore` before it uses the new access token. A 400 or 401
(`{"code":400,"error_code":"refresh_token_already_used","msg"}`, `refresh_token_not_found`, or the
older `invalid_grant` form) means this
computer is signed out; the client raises `SignedOut` and never retries with the old token.
Refresh is single-flight and starts 60 seconds before `expires_at`.

**Ending a sign-in on the server (0.3.3).** `SessionManager.SignOutSessionAsync()` ends this
session on the server, then forgets it here:

```
POST {supabase_url}/auth/v1/logout?scope=local
apikey: {anon_key}
Authorization: Bearer {access_token}
```

`scope=local` ends only this session (the student's other computers stay signed in). It renews an
access token about to expire first, waits at most 5 seconds, and answers whether the server
agreed (a 2xx answer); offline or refused, the session is forgotten here all the same (`SignOut()`), so it is best
effort. A computer several students share (docs/agent/PROFILES.md) calls it when a student is
removed, when a sign-in that is not kept is dropped, and when shared mode is turned off for the
students who are forgotten. On such a computer each student has their own `SessionManager`,
`ArmoryApi`, `BlobClient`, `ConnectFlow`, `TeamHeartbeat` and `FeedbackSender` (the host's
`ProfileClients`) over their own secret store, sharing only the host's three `HttpClient`s, so a
refresh token always has exactly one owner and a call that started as one student can never
finish as another.

## 3. ideabosco.com (contract sections 2 and 3)

`POST {site}/api/armory/blob-url` with the access token, exactly as the contract says. The
client PUTs only when `exists` is false, sends the returned headers, streams the body,
and verifies a GET's bytes against the requested SHA-256 before returning them.

Connecting a computer (contract section 3) is `ConnectFlow`:

1. Bind a `TcpListener` to `127.0.0.1:0`. The OS picks a free port (always 1024-65535). A
   raw socket listener needs no URL reservation, so no admin rights on Windows.
2. `state` = base64url of 32 random bytes; `verifier` = base64url of 32 random bytes;
   `challenge` = base64url(SHA-256(ASCII bytes of the verifier string)), which is RFC 7636
   S256. The contract writes `sha256(verifier)`; the verifier travels as a JSON string in
   the exchange, so the string is what both sides hash.
3. Open `{site}/armory/connect?port=&state=&challenge=&device=` in the default browser.
4. Accept `GET /callback?state=&code=` on the listener. Any other path is 404. A wrong
   `state` is answered with an error page and ignored (the flow keeps waiting, so a stray
   request cannot cancel a real sign-in). The browser gets a plain page: "This computer is
   connected. You can close this tab."
5. `POST {site}/api/armory/connect/exchange` with `{"code", "verifier"}`. 200 answers
   `{access_token, refresh_token, expires_at, email, device_id, supabase_url, anon_key}`;
   400, 401, 410 and 429 become `ConnectException` with a plain student message.
6. Save the session through `ISecretStore` (DPAPI CurrentUser on Windows,
   `Armory.Platform.Windows.DpapiSecretStore`; in memory in tests).

Nothing secret is logged. `ArmorySession.ToString()` redacts both tokens.

## 4. Contract v2 calls (`server/sql/005_v2.sql`, idea-app 0232)

| Call | RPC | Answer |
|---|---|---|
| `CreateProjectAsync(name, int? season, op)` | `armory_create_project` | project id; `season` null makes a project without one (`p_season` is always sent, null included) |
| `RenameProjectAsync(project, name, op)` | `armory_rename_project` | true when renamed, false when it already had that exact name |
| `SetProjectArchivedAsync(project, archived, op)` | `armory_set_project_archived` | true when it changed |
| `RenameFolderAsync(project, from, to, device, op)` | `armory_rename_folder` | live files moved |
| `DeleteFolderAsync(project, folder, device, op)` | `armory_delete_folder` | live files removed |
| `ProjectCheckoutsAsync(project)` | `armory_project_checkouts` | `RemoteCheckout(FileId, Folder, Name, HolderEmail, HolderName, DeviceName, Since)` per live check out |

`MyProjectsAsync` returns `RemoteProject(Id, Name, int? Season, Role, PinnedRelease, ReleaseGate,
bool Archived)`. A null season is read as null and an answer without `archived` (a server older
than 0232) as false. `HolderName` is the holder's profile name or null; show a name derived from
`HolderEmail` when it is null. Revival needs no new call: `CreateFileAsync` on a removed name
returns that file's id. Its first commit must name the revived file's current version (from
`ProjectFilesAsync`) as parent; a null parent is kept aside as a stale parent, and the removed
bytes stay the shared version.

A folder refusal is `ArmoryRpcException` with `IsInUse` (SQLSTATE `55006`, HTTP 500). Read its
`Details` with `FolderRefusal.TryParse`, which returns `Reason` (`checked_out` or `target_exists`,
also `IsCheckedOut` and `IsTargetExists`), `Folder` when sent, `Count` (all of them, not only the
listed ones) and `Files` (at most 10 from 0232, each a `FolderRefusalFile` with `Name`, and the
holder fields when a server sends file objects). It returns null when `Details` is not a JSON
object, so a caller never fails on an unexpected DETAIL.

## 5. Transfer progress

`BlobClient.UploadAsync(..., ct, progress)` and `DownloadAsync(..., ct, progress)` take an optional
`IProgress<long>` that receives the bytes moved so far in this attempt, counted by a stream that
wraps the body. An upload reports 0 when the body starts and the running total after every read;
if the HTTP stack sends the body again from the start, the count starts again at 0. An upload that
storage already holds sends nothing and reports nothing. A download reports 0 when the response
body starts, then the running total, and every call starts at 0, so a retried download counts
from 0. Reports arrive on the thread that moves the bytes, as often as every read, so a
window throttles them itself (`Progress<T>` posts them to its captured context).

**Stalls (0.3.3, feedback N3).** A transfer that moves no bytes for `BlobClient.StallAfter`
(30 seconds unless the caller sets it), counting the wait for storage's answer, has stalled: it
is stopped and tried once more from the start with a fresh URL (a download first empties its
destination, which must be seekable; the bytes so far start again at 0). The stalled attempt is
one `transfer` flight event that ended `stalled`. A second stall throws
`StorageStalledException`, a `StorageTransferException`, so the engine treats it as that one
file's problem until its next try. A slow transfer that keeps moving is never a stall. Until
0.3.3 nothing bounded a body that stopped coming (the storage client's own timeout is two hours):
one 31.5 MB download took 83 seconds on DESKTOP-QH30N35 while the others took 2 to 5.

## 6. Contract v3 calls (idea-app 0233, ARMORY.md "The v0.3 server contract")

Every call a 0.2.x app makes keeps its signature, answers, refusal text and SQLSTATE; these are
added. The binding spec is that section of idea-app `docs/ARMORY.md`.

| Call | RPC | Answer |
|---|---|---|
| `MyProjectsAsync` | `armory_my_projects` | `RemoteProject` gains `bool? CanTakeBack` (mentor, CAD lead, or site admin); null from a server older than 0233 |
| `ProjectPurgedAsync(project)` | `armory_project_purged` | when it was deleted forever, or null (it exists, or this person was only removed) |
| `HeartbeatAsync(device, appVersion, state)` | `armory_heartbeat` | nothing; a null or empty version or state keeps the stored one |
| `LockFilesAsync(files, device, op)` | `armory_lock_files` | `BatchResult(Total, Succeeded, Refused, Results)`, each `BatchFileResult(FileId, Ok, Done, Code, Message)`: `Done` is `acquired` |
| `ReleaseLocksAsync(files, device, op)` | `armory_release_locks` | the same; `Done` is `released` |

A batch takes 1 to 500 distinct files (the client throws `ArgumentException` before sending
anything else; `ArmoryApi.Chunk` makes the calls, distinct and in id order). A replayed
operation id answers the first time, so the caller mints one per action and reuses it.
`FolderPurge.From(change)` reads a `folder_purged` change: `{folder, files, file_ids, by}`.

**Reading a refusal.** `ArmoryRpcException.Detail` is the JSON DETAIL (`RefusalDetail`:
`Reason`, `Field`, `Limit`, `Size`, `RetryAfterSeconds`, `Total`, `Names`), null when DETAIL is
not a JSON object.

| Property | When |
|---|---|
| `IsNotMember` | "no longer a member of this project": `P0001` from `armory_list_changes`, `armory_acquire_lock` and `armory_save_side_version`, and `42501` from `armory_project_files`, `armory_file_history`, `armory_create_file` and `armory_move_file`, all with the text `not a project member` (the code alone is not enough: `P0001` is plpgsql's default) |
| `IsRateLimited`, `RetryAfter` | `PT429`; `RetryAfter` is DETAIL `retry_after_seconds` |
| `IsTooLarge` | `22023` with reason `too_large` or `too_long` (shorten, then send once) |
| `IsInvalidInput` | any `22xxx` |
| `IsForbidden` | `42501` by SQLSTATE only (a bare 403 from a proxy is not Armory's refusal) |
| `IsInUse`, `IsNameTaken`, `IsTransient`, `IsFunctionMissing` | as before (55006, 23505, 40P01/40001, 404 PGRST202) |

`armory_break_lock`'s refusal is unchanged in 0233: `P0001` "only a mentor or cad_lead may
break a lock", read by its code and words.

**Live updates** (`RealtimeFeed`). One websocket to
`{supabase_url}/realtime/v1/websocket?apikey={anon_key}&vsn=1.0.0` (Phoenix protocol 1.0.0,
JSON frames). Per synced project, one channel `realtime:armory-feed-<project>` joined with:

```json
{"topic":"realtime:armory-feed-<project>","event":"phx_join","ref":"1","join_ref":"1",
 "payload":{"access_token":"<the user's access token>","config":{"broadcast":{"ack":false,"self":false},
   "presence":{"key":""},"private":false,
   "postgres_changes":[{"event":"INSERT","schema":"public","table":"armory_change_feed","filter":"project_id=eq.<project>"}]}}}
```

The filter is always there (`RealtimeFeed.FilterFor` is the only filter and `JoinMessage`
always sets it): since 0233 a site admin's session reads every project's feed rows, and an
unfiltered subscription would bring them all. A heartbeat goes every 25 seconds (an unanswered
one ends the connection), a renewed access token goes to every channel as `access_token`, and
channels follow `SetProjects` (joined or left at once). A `postgres_changes` frame raises
`Changed(project)` and nothing else: its payload is never read into local state. Any failure is
logged once per kind and retried after 1, 2, 5, 15, 30, then 60 seconds; the engine's poll is the
floor. The app makes no direct table reads (`/rest/v1/<table>`): every read is an `armory_` RPC
scoped by its own project argument, and `PostgrestClient` refuses any other function name.

**Team status** (`TeamHeartbeat`). `armory_heartbeat(device, version, state)` every 45 seconds,
at once when the state changes (`idle`, `syncing` while files move), and `offline-soon` on a
clean stop within 3 seconds. No suppression here: the server writes nothing for a call within
20 seconds that changes nothing. It runs on its own task with a 15-second deadline per call,
logs a failure once per kind, and waits 6 hours after a 404 PGRST202; it never touches a pass.

## 7. Contract v3.1 and v3.2 calls (idea-app 0234 and 0235, Armory 0.3.3)

The binding specs are idea-app `docs/ARMORY.md`, "The v0.3.1 server contract (migration 0234)"
and "The v0.3.2 server contract (migration 0235)", and those two migrations' SQL. Every earlier
call is unchanged.

| Call | RPC | Answer |
|---|---|---|
| `BreakLocksAsync(files, device, op)` | `armory_break_locks` | `BatchResult`, each `BatchFileResult` with `Done` = `broken` (`false`: nobody had it checked out any more), or `Ok` false with the per-file `Code` and `Message` (`armory_break_lock`'s refusal). 1 to 500 distinct files, sent in id order (`ArmoryApi.Chunk` makes the calls); a replayed operation answers the first time and writes nothing. 404 PGRST202 on a site before 0234: send one `BreakLockAsync` per file |
| `SubmitAppFeedbackAsync(kind, body, appVersion, deviceName, context, tried, area, screenshot)` | `armory_submit_app_feedback`, eight arguments | the note's id. All eight named arguments are always sent (the form has no defaults, so no call matches both forms), a blank `tried`, `area` or `screenshot` as null. `kind`: bug, idea, praise or other. 404 PGRST202 on a site before 0235 |
| `SubmitAppFeedbackAsync(kind, body, appVersion, deviceName, context)` | the five-argument form | unchanged; it refuses `praise` (22023 `kind`, "The kind of note is bug, idea or other.") |
| `MyAppFeedbackAsync(limit = 50)` | `armory_my_app_feedback` | the caller's own notes, newest first (`limit` clamped to 1 to 200 by the site), each an `AppFeedbackNote(Id, CreatedAt, Kind, Body, Tried, Area, HasScreenshot, AppVersion, DeviceName, Status, ReviewedAt)`; `Status` is new, seen, resolved or closed (a note the site marked spam reads closed, and a "spam" that ever arrived is read as closed too). **Null** on 404 PGRST202: the window hides "Your feedback". No session: `ArmoryRpcException` 42501. There are no replies from the team: the site has none, and the record has no field for one |

The eight-argument form's refusals are 22023 with a JSON DETAIL: `{reason: too_long, field:
tried, limit: 1000, size}`, `{reason: too_long, field: area, limit: 120, size}`, and for the
screenshot `{reason: bad_path | not_found | in_use, field: screenshot}` (not the caller's
`<auth uid>/<uuid>.png`, lowercase; not uploaded; already on another note). PT429 is shared by
both forms: 20 notes an hour per account.

**The screenshot's upload** (`FeedbackScreenshots.UploadAsync(png)`, returns the key):

```
POST {supabase_url}/storage/v1/object/armory-feedback-shots/<auth uid>/<new lowercase uuid>.png
apikey: {anon_key}
Authorization: Bearer {access_token}
Content-Type: image/png
x-upsert: false

<the PNG bytes, at most 2097152>
```

The auth uid is the access token's `sub` claim (`AccessToken.Subject`: reads the JWT payload,
checks nothing, and never logs or keeps the token; a token without a uuid `sub` refuses the
upload as `no_account`). This computer refuses first, before anything is sent, a picture over
2097152 bytes (`too_large`) or one that does not start with the PNG signature (`not_png`).
Storage answers HTTP 400 for every refusal but a 500, with the real code as text in the body's
`statusCode` (`{"statusCode": "413", "code": "EntityTooLarge", "error", "message"}`: its error
handler sends `userStatusCode`, which is 400 unless the code is 500), so the client reads the body
first and falls back to the HTTP status. The real code becomes `ScreenshotRefusedException(Reason,
Status)`: 413 `too_large`, 403 `not_allowed` (the insert policy: only the caller's own folder), 409
`exists` (nothing overwrites an object), 404 `not_available` (no bucket: the site before 0235, or
0235 without its storage half), anything else 4xx `refused`. A busy Storage is never a refusal of
the picture: a body code of 408, 423 (`ResourceLocked`), 429, 503 (`DatabaseReadOnly`,
`LockTimeout`), 544 (`DatabaseTimeout`) or any other 5xx, an HTTP 429, 502, 503 or 504, and no
connection are `ArmoryOfflineException` (the window says to try again; 0.3.2 offered to drop the
picture for a 544). An expired token (`InvalidJWT` in the body's `error` or `code`, or 401) is
renewed once and the picture goes under a new key. Each upload goes into the flight recorder as a
transfer named `screenshot` (its size, how long, how it ended; never the token or the key).

`FeedbackScreenshots.Dimensions(png)` reads a PNG's width and height from its IHDR (null for
anything else). `ScreenshotFit.NextScale(bytes, scale)` is how much smaller the window's picture
is taken again when it is over 2 MiB: aimed at 85% of the limit by area (a picture's bytes shrink
with the square of its scale), in steps of 0.05, at least 0.1 smaller than the last try, never
below `ScreenshotFit.Smallest` (0.25); null when it already fits or is already the smallest.

**Send feedback: `FeedbackSender`.** The window's one entry point (the host makes one,
`AgentHost.Feedback`):

```csharp
public sealed record FeedbackNote(string Kind, string Body, string? Tried = null, string? Area = null,
    byte[]? Screenshot = null, JsonObject? Context = null);
public Task<FeedbackResult> FeedbackSender.SendAsync(FeedbackNote note, CancellationToken ct = default);
```

It never throws but for cancellation. In order: the body is trimmed and cut to 8000 characters
(blank: `Failed("Write a few words first.")`), the kind is bug, idea, praise or other (anything
else is other), `Tried` is trimmed and cut to 1000 characters and `Area` to 120 (counted as the
site counts them, never inside a surrogate pair; blank is null), the version to 64, the context
kept under 96 KiB as JSON (its largest entries give way first); a picture over 2 MiB or not a
PNG is refused here; a wait the shared limiter holds (PT429) answers `RateLimited` without a
call; the picture is uploaded; then the eight-argument note names it. On 404 PGRST202 the
five-argument form takes the note, `praise` going as `other` (that form refuses praise), and
what it has no argument for goes in its context, which the admin's list shows whole:
`FeedbackSender.WithAsked(context, askedKind, tried, area)` adds `askedKind` ("praise", when it
went as other), `tried` and `area`, each only when there is one, kept whole while the rest of the
context gives way first. So only a picture is lost. The wide form is not asked for again for
`WideMissingRetry` (1 hour); in that hour a picture is not uploaded at all (no note could name
it). The same picture sent again
after `Offline`, `RateLimited` or `Failed` is named again rather than uploaded twice; once a note
names it, it is never reused. A context the site measures too large is shortened to 24 KiB and
sent once more, never the same payload twice.

| `FeedbackResult` | When | `Ok` | `CanSendWithoutPicture` |
|---|---|---|---|
| `Sent(Id)` | the note went with every field it was given | yes | |
| `SentWithoutNewFields(Id, Kind, KindChanged, LeftOutPicture)` | the five-argument fallback: `Kind` is what it went as (`other` for praise, `KindChanged`), `LeftOutPicture` when a picture was given and left out (tried and area went in the context). Its sentence says only what was left out: "Sent. Thank you for the feedback. The website can't take pictures yet, so it went without the picture." and, for praise, "The website doesn't take praise yet, so it went as other feedback." | yes | |
| `ScreenshotRefused(Reason)` | the note was NOT sent: `not_png`, Storage's `not_allowed`, `exists`, `not_available` or `refused`, or the site's 22023 `bad_path`, `not_found` or `in_use` (field screenshot) | | yes: send the same note with `Screenshot = null` |
| `TooLarge(Field, Size, Limit)` | `screenshot` over 2 MiB here or Storage's 413 (offer it without the picture), or a field the site measured too long | | for `screenshot` |
| `RateLimited(RetryAfter)` | PT429, or a PT429 still running: nothing goes before `RetryAfter` | | |
| `Offline` | the site or Storage could not be reached | | |
| `Failed(Reason)` | anything else, in plain words (`Message` is `Reason`): nothing to say, not connected, neither form on the site, another refusal | | |

Every result has `Message`, one plain sentence the window can show as it is, for example
"Sent. Thank you for the feedback.", "Your note wasn't sent: that screenshot is already on
another note. You can send it without the picture." or "You've sent a lot of feedback this hour.
Try again in 25 minutes.".

`FeedbackSender.SubmitAsync(kind, body, version, deviceName, context, tried, area, screenshot)`
is the same note without the upload, throwing as `ArmoryApi` does (a PGRST202 only when the site
has neither form); it returns `FeedbackSubmission(Id, NewFields, Kind)`. The `IncidentUploader`
sends a saved note's words, what was tried and the area through the sender when it has one (the
eight-argument form, never a picture: a saved note has none); without one, the five-argument
form, praise as other and the rest in the context (`WithAsked`), as the sender's fallback does.

**The window's Send feedback** (`FeedbackDesk` in `src/Armory.Agent`, wired by
`AgentHost.Feedback.cs`; the bridge half is in docs/agent/BRIDGE.md). A note **without** a picture
takes the durable path: `AgentTelemetry.SendFeedbackAsync(kind, body, tried, area)` saves it in
the incidents folder first (kind bug, idea, praise or other; tried and area scrubbed like the
words), sends it at once through the uploader and the sender, and the uploader sends it later
when it can't go now ("Saved. It will be sent when this computer is back online."). A note
**with** a picture goes now and is never written to disk with its picture:
`IncidentReporter.ComposeNoteAsync` builds the very document a saved note would hold (the same
scrubbing: other people's addresses masked, this computer's tokens gone) without saving it, its
context is `IncidentUploader.FeedbackContext` of that document, and `FeedbackSender.SendAsync`
uploads the picture and sends the note. When it can't go, the answer offers the same note without
the picture (`ActionResult.Offer` = `withoutPicture`): a refused picture (`CanSendWithoutPicture`),
`Offline` ("You're offline, so your note and its picture weren't sent. Send it without the
picture, and Armory sends it once this computer is back online.") and `RateLimited` (its sentence,
then "Or send it without the picture, and Armory sends it then."). Without the picture it takes
the durable path. So nothing a student typed is lost, and a picture of the window is never kept
on disk. A sent picture is forgotten; the window keeps only its last picture, in memory
(`WindowShots`), and a picture it no longer has is answered "That picture isn't here any more.
Add it again, or send your note without it." with the offer.

**Your feedback** (`FeedbackDesk.ReadAsync`): signed out is `signedOut` without a call; otherwise
`MyAppFeedbackAsync(50)`: a list is `shown`, null (404 PGRST202) is `missing` and is not asked
again for an hour, `ArmoryOfflineException` is `offline` ("You're offline. Your feedback shows
here once this computer is back online."), `ArmorySignedOutException` or 42501 is `signedOut`,
anything else `failed` ("Armory couldn't read your feedback. Try again in a moment."). Each note's
status in words is `AppFeedbackNote.StatusWords`: new "Not read yet", seen "Read by the IDEA
team", resolved "Done", closed (spam too) "Closed". `pictures` is true only for `shown` while the
sender knows the eight-argument form (0235 brought the list and the picture bucket together).
There are no replies in Armory: the site has none, and the window says so.

**One limiter** (`SubmitLimiter`): the PT429 and not-live waits the uploader kept, now shared
by the uploader and the sender over the incidents folder (`upload-wait.json`, across restarts).
A PT429 either one meets (`retry_after_seconds`, 5 minutes when unreadable) holds both: the
window's `SendAsync` answers `RateLimited` without a call, and the background round waits.

**Version limits.** `armory_heartbeat` refuses an `app_version` over 40 characters (22023);
feedback and incidents take 64. `TeamHeartbeat` cuts its version to
`TeamHeartbeat.MaximumVersionCharacters` (40) when made, and if the server refuses the version
anyway (22023, field `app_version`) it sends that beat again at once without one (null keeps
what the server has) and every later beat too, so the refusal never repeats. `FeedbackSender`
cuts to 64. `HostPiecesTests.The_app_version_fits_the_heartbeats_40_characters` holds the app's
own version (`AgentPaths.Version`, from `Armory.Agent.csproj`'s `<Version>`) to 40.

**Test switches** (`tests/Armory.TestSupport`). `ArmoryV3StandIn.ApplyBreakLocksAsync` and
`ApplyFeedbackV2Async` copy 0234's and 0235's SQL as it is (with `auth.uid()` and a storage
schema for 0235's bucket and policies); `DropFeedbackShotsBucketAsync` leaves 0235's functions
without its storage half (the migration's NOTICE path). `FakeSupabase` mints GoTrue-shaped access
tokens (a JWT whose `sub` is the auth uid), hands each call its JWT claims, serves Storage uploads
with Storage's rules and error bodies in Storage's own style (HTTP 400 with the real code and its
name in the body; `StorageRealStatus` makes the code the HTTP status too; a bad or expired token is
`{statusCode: "400", code: "InvalidJWT", error: "InvalidJWT"}` either way), answers the next
uploads busy with `FailStorage(statusCode, code, error, times)` (544, 503, 423, 429), and has
`HideFunction(function, argumentCount?)` (404 PGRST202 for a function or one overload) and
`FailRpc(function, sqlState, message, times)` (a call answered with that SQLSTATE without
running, such as 40P01).

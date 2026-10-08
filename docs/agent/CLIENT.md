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

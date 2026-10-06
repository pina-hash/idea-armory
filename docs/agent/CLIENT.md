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
`53xxx` 503, and the rest of PostgREST's table (for example `55000` and `XX000` 500,
`42P01` 404). An expired JWT is 401 with code `PGRST303` (older PostgREST: `PGRST301`). The client maps these to `ArmoryRpcException` (with `SqlState`,
`Message`, `Details`, `Hint`, `Status`), refreshes once and retries on 401 PGRST30x, and
throws `ArmoryOfflineException` for a network failure, timeout, 502, 503 or 504.

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

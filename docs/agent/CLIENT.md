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

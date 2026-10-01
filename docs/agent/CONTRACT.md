SHARED CONTRACT (identical in lanes A and B; neither lane may change it; a lane that finds it unworkable writes why in its report and builds to it anyway)

Written by the IDEA & FRC chat, 2026-09-29. Every statement is a claim.

1. Data and rules. The authoritative schema and RPCs are `server/sql/001-003` in `pina-hash/idea-armory` at `ec3a2bf`. That covers device-scoped locks, operation receipts, stale-parent side versions, the change feed and RLS. The agent calls the RPCs directly through Supabase PostgREST (`POST {SUPABASE_URL}/rest/v1/rpc/armory_<name>`), using the signed-in user's access token and the project's public anon key. The agent never holds a service-role key.

2. Blob URLs, served by ideabosco.com:

       POST /api/armory/blob-url
       Authorization: Bearer <access token>
       request body: {"projectId": uuid, "hash": "<64 lowercase hex>", "bytes": int, "method": "PUT" | "GET"}

   - 200 returns `{"url": string, "headers": {name: value}, "expiresAt": ISO-8601, "exists": bool}`. `exists` is true when the object is already stored; the agent then skips a PUT.
   - The server checks that the caller is a member of the project (`armory_is_member` under their identity). A GET is allowed only for a hash that appears in that project's versions or side versions. A PUT is allowed only for bytes of at most 2 GiB.
   - The object key is `blobs/sha256/<2 hex>/<2 hex>/<full hash>`. URLs live 15 minutes.
   - 401 when there is no valid token. 403 when the caller is not a member, or the hash is not in the project. 503 `{"error": "armory_storage_not_configured"}` when any of `ARMORY_R2_ACCOUNT_ID`, `ARMORY_R2_ACCESS_KEY_ID`, `ARMORY_R2_SECRET_ACCESS_KEY` or `ARMORY_R2_BUCKET` is unset.
   - Signing uses the SigV4 presigner logic proven in `idea-armory/src/Armory.Storage/SigV4Presigner.cs`, ported to TypeScript and pinned against the same published AWS test vector.

3. Connecting a computer. This is a native-app loopback flow, and no secret ever appears in a URL.
   a. The agent picks a free port on 127.0.0.1, then makes a random `state` (32 bytes) and a PKCE `verifier` (32 bytes), with `challenge = base64url(sha256(verifier))`. It opens the default browser to `https://ideabosco.com/armory/connect?port=<p>&state=<s>&challenge=<c>&device=<device name>`.
   b. The page requires the user to be signed in (the normal site Google sign-in), shows "Connect <device name> to Armory as <email>?", and on confirm POSTs to `/api/armory/connect/start` with `{port, state, challenge, device}`.
   c. The server stores a one-time `code`: 32 random bytes, kept only as a SHA-256 hash, with a 2-minute life, bound to the user, challenge, state and device. It returns a redirect to `http://127.0.0.1:<port>/callback?state=<s>&code=<code>`. Only `127.0.0.1` with a port in 1024-65535 is ever accepted as the target.
   d. The agent checks that `state` matches, then POSTs `/api/armory/connect/exchange` with `{code, verifier}`. The server checks that sha256(verifier) matches the challenge, that the code is unused and unexpired, and consumes it.
   e. The server then mints a fresh Supabase session for that user that is independent of the browser's session, so neither logs the other out. It uses the admin API server-side (`SUPABASE_SERVICE_ROLE_KEY` is already used in `src/lib/server/`): `auth.admin.generateLink({type: "magiclink", email})`, then `verifyOtp({token_hash, type: "magiclink"})` on a fresh client.
   f. The server registers the device under the user's identity through `armory_register_device(name)`. It returns `{access_token, refresh_token, expires_at, email, device_id, supabase_url, anon_key}`.
   g. The agent stores the refresh token with Windows DPAPI (CurrentUser) and refreshes through the normal Supabase token endpoint.
   h. Failure answers: 400 for bad input, 401 for a bad or used code, 410 for an expired code. Both endpoints are rate limited per IP and per user.

4. Change feed. The agent polls `armory_list_changes(project, cursor)` every 5 seconds while online and backs off to 60 seconds when idle. The website may also use Supabase realtime on `armory_change_feed`; RLS already limits it to members.

5. Local layout. The vault root is `C:\IDEA\Armory`, with one folder per project named by the project's name. The hidden `.armory` folder at the root belongs to the agent.

6. RPCs added by lane A to `idea-armory/server/sql` (lane B builds against exactly these names):
   - Files gain `folder text not null default ''`: a relative folder path with forward slashes, each segment valid under `Armory.Core`'s VaultPath rules.
     - Name uniqueness stays per project on (lower NFC name).
     - COTS vault-wide uniqueness is a later lane.
   - `armory_create_project(p_name text, p_season smallint, p_operation uuid) returns uuid`
     - Allowed when idea-app's `public.is_admin()` is true for the caller. The test harness stubs it the way `current_user_email()` is stubbed.
     - The caller becomes a `mentor` member.
   - `armory_add_member(p_project uuid, p_email text, p_role armory_member_role, p_operation uuid) returns boolean`
     - Allowed for a mentor or cad_lead. Only a mentor may grant `mentor` or `cad_lead`.
   - `armory_remove_member(p_project uuid, p_email text, p_operation uuid) returns boolean`
     - Allowed for a mentor. It never removes the last mentor.
   - `armory_create_file(p_project uuid, p_folder text, p_name text, p_device uuid, p_operation uuid) returns uuid`
     - Allowed for any member.
     - A taken name raises SQLSTATE `23505`, with a DETAIL naming the existing folder, so the agent can show where the name already lives.
   - `armory_move_file(p_file uuid, p_folder text, p_name text, p_device uuid, p_operation uuid) returns boolean`
     - Allowed only for the lock holder (email and device). Uniqueness is enforced.
     - Writes a `file_moved` change with the old and new folder and name.
   - Every one of these follows the existing rules: security definer with `search_path=''`, identity only from `current_user_email()`, an operation receipt, a change-feed entry, and no direct table writes.

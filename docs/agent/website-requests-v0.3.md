# Website requests for Armory v0.3

For lane W's next ideabosco.com (idea-app) update pass. Each item names what the Armory
app needs from the site. The app side of each is built in pina-hash/idea-armory once the
migration is live. Migration number: the next free one after 0232.

Order is priority order. Items 1 to 3 unblock the worst problems in daily use; 4 and 4b
are how problems get to the developers with the data to fix them.

## 1. Live updates (no polling dead zone)

Today every computer asks the server every 5 seconds while busy and every 60 seconds
when quiet, so a change can take a minute to show up elsewhere. Supabase Realtime can
push a change the moment it is written.

- Add `public.armory_change_feed` to the `supabase_realtime` publication:
  `alter publication supabase_realtime add table public.armory_change_feed;`
- Realtime only delivers rows the subscriber can `select` under RLS. Today `authenticated`
  has no select on the table (003 revokes it). Add:
  - `grant select on public.armory_change_feed to authenticated;`
  - `alter table public.armory_change_feed enable row level security;`
  - policy `armory_change_feed_members_read`: `for select to authenticated using
    (public.armory_is_member(project_id))`.
- Keep the payloads free of anything a member of the project should not see (they are
  today: kinds, ids, paths).
- The app subscribes per project with `postgres_changes` on `INSERT` filtered by
  `project_id=eq.<id>`, and keeps a 30-second safety poll in case the socket drops.

Acceptance: a check out on one computer shows on another within 2 seconds.

## 2. Admin can force check in (take back) any file

`armory_break_lock` today requires the caller to be a `mentor` or `cad_lead` member of the
file's project. A site admin who is not a member of that project cannot help a student
who forgot to check a file in.

- Allow `public.is_admin()` in `armory_break_lock` as well as mentor and cad_lead.
- Return the same shape as today. The former holder's computer keeps its unsent bytes as
  a kept copy (already true).
- `armory_my_projects` should return `role = 'mentor'` (or a new `can_take_back boolean`)
  for a site admin, so the app shows the Force check in button.
- Optional, for the site: a "Checked out" table per project with a Force check in button
  per row (calls the same RPC).

## 3. Admin permanent delete of a folder or a project

An archived test project (or a test folder) stays in storage forever today.

- `armory_purge_project(p_project uuid, p_confirm_name text, p_operation uuid)`:
  site admin only (`is_admin()`), project must be archived first, `p_confirm_name` must
  equal the project's name. Deletes its files, versions, side versions, tombstones, locks,
  releases, change feed rows, part number allocations, members, then the project. Returns
  the list of R2 object keys to delete (or deletes them through the existing storage
  cleanup path on the site).
- `armory_purge_folder(p_project uuid, p_folder text, p_operation uuid)`: site admin or
  project mentor. Purges every file under the folder prefix (case-sensitive, as 0232's
  folder RPCs) that is already tombstoned, plus all their history. Refuses with 55006
  if any file under it is live or checked out (same DETAIL shape as 0232).
- Emits one change feed row (`project_purged` / `folder_purged`) so every computer drops
  the files from its records without treating it as a deletion to recover.
- The R2 objects are deleted by the site after the RPC commits (the app never deletes
  storage).
- Site UI: on the archived project's page, "Delete forever" with type-the-name confirm.

## 4. App feedback, its own section on the feedback page

The app gets a Feedback button. Its reports go to the site and land in their own
section, exportable for a Claude Code session in the idea-armory repo.

- Table `public.armory_app_feedback`: `id uuid pk default gen_random_uuid()`,
  `created_at timestamptz default now()`, `email text not null` (caller),
  `device_name text`, `app_version text not null`, `kind text check (kind in
  ('bug','idea','other'))`, `body text not null check (length(body) between 1 and 8000)`,
  `context jsonb not null default '{}'` (the app sends: window page, sync state, counts,
  last 200 log lines with paths, never file contents), `status text default 'new'`.
- RPC `armory_submit_app_feedback(p_kind text, p_body text, p_app_version text,
  p_device_name text, p_context jsonb) returns uuid`, any signed-in user, rate limited to
  20 per hour per email.
- Site feedback page: a separate "Armory app" tab listing these, newest first, with status.
- Export button: downloads one Markdown file with every selected report (date, who,
  version, kind, body, context in a fenced block), titled
  `armory-feedback-YYYY-MM-DD.md`, ready to paste into a Claude Code session.

## 4b. Automatic incident reports (same pipeline as 4)

The app keeps a small in-memory flight recorder (the last few thousand events: each sync
pass with timings, each server call with its duration and answer, each transfer, each
button press and how long its answer took, every error). It writes nothing and sends
nothing until something goes wrong: a crash, an action slower than 10 seconds, a pass
slower than 60 seconds, the same file failing 3 times, a check out the app had to repair,
a file writable that should be read-only. Then it saves one incident (compressed JSON,
under 200 KB, file names and paths only, never file contents) and sends it when the
network is quiet. At most one incident per kind per 10 minutes per computer.

- Table `public.armory_app_incidents`: `id uuid pk`, `created_at timestamptz default
  now()`, `email text not null`, `device_name text`, `app_version text not null`,
  `kind text not null` (crash, slowAction, slowPass, repeatedFailure, repairedCheckout,
  readOnlyBroken, userReport), `summary text not null check (length(summary) <= 500)`,
  `project_id uuid null`, `report jsonb not null check (pg_column_size(report) <= 1048576)`,
  `feedback_id uuid null references public.armory_app_feedback` (when the person also
  wrote a note), `status text default 'new'`.
- RPC `armory_submit_app_incident(p_kind text, p_summary text, p_app_version text,
  p_device_name text, p_project uuid, p_report jsonb, p_feedback uuid) returns uuid`, any
  signed-in user, rate limited to 30 per hour per email, refuses bodies over 1 MB.
- Select only for site admins (RLS: `using (public.is_admin())`).
- Site: an "Armory incidents" tab next to "Armory app" feedback: newest first, grouped by
  kind and app version, counts per day, filter by person or project.
- Export: one `.json` file per selected incident (the `report` verbatim plus the row's
  fields) and a "Download all as zip" button. These open directly in a Claude Code session
  in idea-armory with `tools/read-incident` (built in the app repo).
- Keep 90 days, then delete (a scheduled job or a delete on submit of rows older than 90
  days).

## 5. Team status (who is online, what is checked out)

For the app's new Team page.

- Add `last_seen timestamptz` and `app_version text` to `public.armory_devices`.
- RPC `armory_heartbeat(p_device uuid, p_app_version text, p_state text)`: updates
  `last_seen = now()`, version, and a short state (`idle`, `syncing`, `offline-soon`).
  The app calls it at most once a minute.
- RPC `armory_team_status(p_project uuid)` (members only) returns per member: email,
  display name, role, devices with name, last_seen, app_version, state, and the files
  each has checked out (file id, path, since).
- The same view on the site's project page would help mentors too.

## 6. Smaller items

- `armory_break_lock` and the folder RPCs: the possible deadlock between a folder rename
  and a check out (0232 note). Take the project row lock first in both paths so they queue
  instead of deadlocking.
- A server-side `armory_lock_files(p_files uuid[], ...)` batch (one round trip for "Check
  out all" on a folder) and `armory_release_locks(p_files uuid[], ...)`. Not required;
  the app loops today.

Nothing here changes existing RPC signatures, so 0.2.x apps keep working.

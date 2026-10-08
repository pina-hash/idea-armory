# Website requests after Armory 0.3.2

**Not built yet.** Armory 0.3.2 works without any of them. Items 1 to 3 came with 0.3.1 (bulk
work); items 4 and 5 come from the feedback sent from 0.3.1 (taking turns on a lab computer,
and feedback that matches the website's).

For the next ideabosco.com (idea-app) update pass. Migration number: the next free one after
0233. The binding spec stays idea-app `docs/ARMORY.md`. Add these to it as a new contract
section once they are built.

## Why

Armory 0.3.1 fixed Force check in all. In 0.3.0 the window sent one action per file, and each
action ran a full sync of its own, so a few hundred files took the better part of an hour.
0.3.1 does it in one action: one `armory_break_lock` call per file, 16 at a time, then one sync.
A few hundred files now take seconds.

Check out all, Check in all and Undo check out already go in one batch call each
(`armory_lock_files` / `armory_release_locks`, up to 500 files, from 0233). Force check in is
the one bulk action with no batch RPC, so it still costs one HTTP request and one transaction
per file.

## 1. `armory_break_locks(p_files uuid[], p_device uuid, p_operation uuid) returns jsonb`

Same shape and rules as `armory_release_locks`:

- 1 to 500 distinct files, else 22023 with DETAIL `{reason: "count", total, limit: 500}`.
- Files handled in id order, each on its own (one refusal never stops the rest), each with
  exactly the semantics of `armory_break_lock` for that file: the same role check (`can_take_back`:
  mentor, CAD lead, or site admin), the same `lock_broken` change row with `by`,
  `former_holder` and `former_device_id`, and the same refusal text and SQLSTATE per file.
- Answer: `{total, succeeded, refused, results: [{file_id, ok, broken, code?, message?}]}`
  (`broken` false means nobody had it checked out any more).
- A replayed `p_operation` answers what the first call answered.
- Locks taken project row first, then files in id order, like the other batch RPCs, so it can't
  deadlock with them. A 40P01 or 40001 is retried by the app (bounded), as for the others.
- `grant execute ... to authenticated`, revoked from `public` and `anon`.

The app adds it the way it added the other batches: use it when present, and fall back to one
`armory_break_lock` per file on PGRST202 (it checks again after an hour).

## 2. The site's own bulk controls

Check every bulk control on ideabosco.com (Force check in on the Team or Files views, any
"select all" action on files, folder actions) for the same mistake: one request per file, run
one after another, with a page reload or full refetch after each. Each should be one batch
call (or a few at once), then one refetch at the end. Show a single progress line, then one
result sentence ("Force checked in 212 files. 3 weren't checked out any more.").

## 3. Realtime bursts

A bulk action writes one `armory_change_feed` row per file. Every open Armory computer only
re-reads on a live event (it never applies the row itself) and already folds events that
arrive together into one read, so nothing is needed for the app. If the site's own pages
listen to the feed, they should fold a burst the same way (for example, wait 250 ms after the
last event, then refetch once) instead of refetching per row.

## 4. Choosing the account at Connect (taking turns on a lab computer)

Armory 0.3.2 lets students take turns on one lab computer: Switch account signs one student
out and starts the next one's sign-in at once, and the Armory folder is handed over when the
last student has nothing waiting in it. The sign-in happens in the computer's browser, which
on a shared computer is often still signed in to ideabosco.com as the last student, so the
approval page can approve the wrong person with one click.

- The page that approves "Connect this computer" (the app's ConnectFlow, idea-app's Armory
  connect route) must say plainly who is being connected ("Connect LAB-PC-07 as Alex Kim?")
  and offer "Not you? Use another account", which signs the browser out of ideabosco.com and
  comes back to the same approval after the next sign-in.
- Nothing changes in the connect protocol the app uses (the same code, the same answer);
  only the page.

## 5. Send feedback, the same as the website's

A student asked for the app's Send feedback to have "the same features as the feedback
function on the idea website". Today the app sends `armory_submit_app_feedback(p_kind,
p_body, p_app_version, p_device_name, p_context)`: a kind (bug, idea, other), the words, the
app's version, the computer's name and a small context (no file contents, no other person's
address).

- Write down in docs/ARMORY.md what the website's own feedback form offers that this lacks
  (for example a screenshot or other attachment, a page or area the note is about, a way to
  see your earlier notes and their status, replies), so the app can match it.
- For each one the app should offer, extend the RPC (or add one) and give its limits,
  SQLSTATEs and DETAIL reasons the way 0233 did. A screenshot would be the app window only,
  PNG, at most 2 MB, with the same privacy rules (no other person's address, no file
  contents; the app crops nothing out of the picture, so the person sees what is sent).
- If the website lets a person see their notes and the team's answers, say which RPC lists
  them for the signed-in person, so the app can show "Your feedback" with each note's status.

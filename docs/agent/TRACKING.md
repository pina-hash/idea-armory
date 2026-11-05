# Desk tracking (0.5.7)

idea-app ledger 0521, migration 0369. What this computer tells ideabosco.com about team files, and
what it does at the end of a class period. Operational data only, never a score.

## What is sent

`armory_file_events(p_device, p_events)`: a list of `{file, kind, at}`, at most 200 a call.

| kind | when |
| --- | --- |
| `opened` | the student opens a team file in its own SolidWorks window (`LinkOpened` with `TopLevel`, or `LinkActivated`), or SolidWorks' `~$` lock marker for it appears without the link |
| `saved` | the link reports a SolidWorks save (`LinkSaved`) |
| `closed` | that window closes (`LinkClosed` of a top-level document), or the marker goes |
| `nudged` | the end-of-period reminder named the file |

Only files the team has (a server id) are sent. `DeskEventQueue` (Armory.Core) collapses the same
kind for the same file inside 20 seconds, keeps the newest 1,000, and puts a failed batch back in
front. `SyncEngine.Tracking.cs` fills it on the engine thread; `AgentHost.Tracking.cs` sends it
every 30 seconds on its own task. A site without 0369 answers 404 PGRST202: the queue is cleared
and the site is asked again after six hours.

The site keeps one row per file and computer (`armory_file_activity`) and shows, on each live
check out, what the HOLDER's computer last reported. A row is never readable by a policy; only
`armory_checkout_health` reads it, for members and site admins.

## The end-of-period reminder

Once an hour the host reads `GET <site>/api/armory/bells` (`PeriodEnds`): today's class period
ends from the website's bell schedule and the school calendar's A or B day. A day with no letter,
or with special times the site lacks, has no ends, and nothing is said. From five minutes before a
bell to ten after it, once per bell (`PeriodNudge.Due`), a student with files checked out on this
computer gets a Windows notification, "Class is ending", with a Check in button
(`ProtocolLink.CheckIn`, a one-time token for exactly those files), and each file is sent as
`nudged`.

## Automatic check in

`armory_checkout_rules()` carries the site admin's `auto_check_in_idle_minutes`, null (off) by
default. When set, a file checked out here, closed (`DeskEventQueue.IsOpen` false) and untouched
(no open, save or close) for that long after its check out is checked in by the host
(`AutoCheckIn.Due`), once per check out, and the student reads one sentence. A check in still
waits for a file SolidWorks holds open, and uploads saved changes first, so nothing is lost.

## Check out and open from the website

`idea-armory:open?f=<file id>&co=1` (`ProtocolLink.TryParseOpen`, at most 80 characters, nothing
else accepted). The file must be one of the projects this Armory syncs. Available: `CheckOutAsync`
with `open: true`, which downloads the newest version first when this copy is older, takes the
check out and opens it. Held by someone else, or on the student's other computer: opened
read-only, and the answer says who. Already checked out here: opened.

## Forced check in

A card on Home has said "was force checked in" since 0.3; `TakenBackAsks` now also says it once
as a Windows notification, withdrawn when the card goes.

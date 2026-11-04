# The first run (ledger 0504)

Mr. Pina, 2026-11-04: "The dumbest person should be able to understand how Armory works
pretty easily, pretty early on, and use it functionally. Armory has to be freaking tight."

The bar: a first-time student with no help goes from the website to a file checked out,
opened, changed in SolidWorks, saved and checked back in, and knows who else is editing
what, in under 10 minutes, reading one short sentence at a time, every instruction at
grade 5 or lower. `tools/agent-ui/cold-user.mjs` is the test that holds the window to it.

## What a new student does now

1. **Connect** (unchanged steps, shorter words): one click, sign in, come back.
2. **The lesson is a worked example** (`wwwroot/tutorial.js`, 8 cards, all on a practice
   part in a practice project; nothing reaches the host): open the part, check it out, open
   it, change it and save, check it in, see a part someone else has, then "now do it for
   real". Each card is a title and one sentence. The one control to press is lit (a green
   ring, `data-lit`), Next wakes when the step is done, and a dot a step shows progress.
3. **First steps on Home** (`wwwroot/first-steps.js`): the same job on the student's own
   part. It ticks itself off from what really happens (a check out the host confirms, a
   launch of a file checked out by you, a file that turns Changed, a check in the host
   confirms, a look at Checked out by others), never from a press on the list. The next
   line is lit and so is the one control that does it; when that control is out of sight,
   Show me is lit instead and brings it into view. I'm stuck opens Report a problem. Hide
   puts it away; Settings > Show me again brings back both the list and the lesson.

## The six words (`wwwroot/words.js`)

Project, Check out, Changed, Check in, Available, Version: one sentence each, an icon
beside each word, shown on the lesson card where the word is first met (Version on the
file page, above its History), and all six in Settings under Words to know. The website
says the same six (idea-app `src/lib/armory/words.ts`). There is no second name for any
of them: `first-run-check.mjs` refuses lock, reserve, grab, sync, push and vault in this
copy, and check-ui's jargon sweep already refuses lock, sync and vault on every screen.

## What the 0.5.5 lesson taught that the first run no longer does, and why

The 0.5.5 lesson was nineteen cards. Search, the quick filters, the Armory folder, Switch
student, Report a problem and its practice words do not change what a student does in a
first edit, so they left the lesson. Search and the filters are one press away on Home,
Report a problem is the First steps list's I'm stuck key and stays in Settings, and on a
shared lab computer the picker already asks who is using Armory before Home shows. Find
your project and find your folder were two cards that could not be done in a practice
project; the real version is First steps' "Find your part in your folder, then click Check
out". `check-ui.mjs` now holds the lesson to its job (find, check out, open, save, check
in, someone else's file, then the real work, in that order) and to ten cards at most,
instead of to a count of nineteen.

## Plain help instead of raw errors (`wwwroot/help.js`)

Every problem says what happened and the one thing to do, in two short sentences. The host
writes most of its answers that way already; what reached the window raw was the sign-in
screen's `error.Message` and the odd failure deep inside an action. `ArmoryHelp` maps a
technical message to its card and passes plain words through untouched. The cards are the
real ones in the Armory reports up to 2026-11-04: the school site answering 521 (the
dozens of `armory_my_projects failed with 521` crashes on 2026-10-30), a cut-off answer
(`JsonReaderException ... end of data`), a sign-in that ran out, a file SolidWorks still
holds, a folder that is not the student's, a full disk, and a generic card for anything
else technical. The save SolidWorks refuses (the 2026-11-03 lab report) keeps its card
from 0.5.5 with grade 5 words: "You have not checked out this file. Click Check out, then
save again in SolidWorks."

## The three layouts (taste: Mr. Pina picks)

`?lesson=coach|quest|bar` in a browser draws the lesson card three ways; only `coach`
ships. `node tools/agent-ui/first-run-shots.mjs` renders all three at 1280x800 and 420x720
into `docs/agent/screens/first-run/`, beside the First steps list.

- **coach** (default): the card beside the lit control, a dot a step. The smallest card
  that still says one thing; the control is never covered.
- **quest**: the same card with every step named and ticked, like a game's quest log. The
  most progress you can see, and the tallest card.
- **bar**: one big sentence docked across the window's foot. The easiest to read from a
  distance, and it covers the bottom of the window.

## The cold-user test and its model

The robot reads what a lesson card, a First steps line or a help card says and presses
what is lit. Where nothing is lit it reads the whole window and presses the first control
whose words match its task. It runs against a local stand-in for the host, the school site
and SolidWorks (`StandIn` in the test), whose save-blocked words are read out of
`SaveBlockedDetector.cs` so the card it shows is the engine's. Seconds are modeled, and
every number is in the file: reading at 115 words a minute (the 25th percentile of grade 5
oral reading fluency, Hasbrouck and Tindal 2017), 2.65 s a press (the keystroke-level
model), and fixed estimates for everything outside the window (the website 60 s, download
20 s, install with Windows' warning 45 s, browser sign-in 45 s, SolidWorks starting 60 s,
the change 60 s, Save 5 s, close 5 s: 300 s in all, the same before and after).

| Window | Build | Steps | Modeled time | Words read | Worst grade | Longest sentence | Most sentences at once |
|---|---|---|---|---|---|---|---|
| 1280x800 | 0.5.5 (main, a674c00) | 40 | 1219 s (20.3 min) | 1549 | 6.6 | 17 | 5 |
| 1280x800 | this branch | 23 | 546 s (9.1 min) | 355 | 3.7 | 13 | 2 |
| 420x720 | 0.5.5 (main, a674c00) | 40 | 1068 s (17.8 min) | 1261 | 6.6 | 17 | 5 |
| 420x720 | this branch | 24 | 549 s (9.2 min) | 355 | 3.7 | 13 | 2 |

Measured 2026-11-04 in this container. The 0.5.5 run fails the bar on time, grade,
sentence length and sentences at once; this branch passes all of them.

## Not verified

- No real student has done this. The robot is a model of one; the first real test is a
  student at a lab computer with a stopwatch.
- The outside-the-window seconds are estimates, not measurements on a lab computer.
- Real SolidWorks, real Windows notifications and the real host were not run; the stand-in
  answers as the engine does for the handful of messages a first run sends.
- WebView2 itself was not run here (Linux container); the window was driven in Chromium,
  as check-ui does.

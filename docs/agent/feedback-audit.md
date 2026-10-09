# Armory feedback and incident audit (0.3.3)

Every Armory app feedback note (15, exported 2026-10-09T06:14Z) and every incident report (77, the
whole history through 2026-10-09) was checked against the code at 0.3.2 (8d90d63), not against the
release notes, together with every claim the 0.3.0, 0.3.1 and 0.3.2 rounds made. Duplicates are
grouped: an incident row names every incident id (first 8 characters) it covers. Each row keeps the
date, version, device and note number. "Found" is the state at 0.3.2; "0.3.3" is the state now.
Statuses: Done, Partly done, Not done, Not reproducible, Won't do (only for safety or cost, with why).

Totals at 0.3.3: 105 In progress.

| Key | What | Who, device, version, date | Refs | Found | 0.3.3 | Where it is now, and how it was verified |
|---|---|---|---|---|---|---|
| [N1](#n1) | A styled tooltip on every button after about a second of hover | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:59Z | N1 | Partly done | In progress | Pending. |
| [N2](#n2) | Running lines say 'Sync finished' after every slice while hundreds of files remain | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:54Z | N2,N3,7a6c7d95,c5dd91d2 | Not done | In progress | Pending. |
| [N3](#n3) | A 1,400-file download stops and starts, showing 'Checking for changes' between batches | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:53Z | N3,N2,7a6c7d95,c5dd91d2 | Not done | In progress | Pending. |
| [N4](#n4) | Check in after editing a saved part does not save the changes; the file on disk is replaced by the previous version | Abraham Jette-Kouri; IDEA-06; 0.3.1 reported; reproduced at HEAD 8d90d63 (0.3.2); 2026-10-08 21:32Z to 23:16Z (feedback 23:16:27Z) | N4, 7352f99d, 25d938f6, e07af0ab, a6f9e941, 2cf39a10, 12ae9081, 6d145628, ee443a83 | Not done | In progress | Pending. |
| [N5](#n5) | Let people organize files others have checked out; instructor override to check in others' files | Abraham Jette-Kouri; IDEA-06; 0.3.1; 2026-10-08T19:52:23Z (sent while 15 of his own Check in row clicks were still queued, see I-checkIn-IDEA-06-0.3.1) | N5 | Partly done | In progress | Pending. |
| [N6](#n6) | No button or action should feel laggy | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T19:33:45Z | N6, c5dd91d2 (reference only), all 22 slowAction incidents | Partly done | In progress | Pending. |
| [N7](#n7) | Switching themes is slow and glitchy | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08T18:42:56Z | N7, b500df79 | Partly done | In progress | Pending. |
| [N8](#n8) | Show progress and running lines during long operations | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T18:37:27Z | N8, 58d06041, aa236105, c5dd91d2 | Partly done | In progress | Pending. |
| [N9](#n9) | Bulk keys were under the 1,400-file list; the UI must handle thousands of files without confusion | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T18:34:48Z | N9, 6d8e6ddc | Partly done | In progress | Pending. |
| [N10](#n10) | Several students in one Armory folder on one computer, and a quick account switch | MR. PINA (apina@boscotech.edu); IDEA-00; 0.3.1 (note); 0.3.2 current; 2026-10-08T18:33:04Z | N10, 2d479aea, 87fa2b3d, 428a0f6f, 968cc0cd | Partly done | In progress | Pending. |
| [N11](#n11) | After checking everything out, only a file list and no bulk keys | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T18:31:32Z | N11, ed7dc8f8 | Done | In progress | Pending. |
| [N12](#n12) | No way to bulk check in | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T18:30:02Z | N12, 809ae174 | Done | In progress | Pending. |
| [N13](#n13) | Send feedback said it could not send (feedback that had in fact been sent) | MR. PINA (IDEA-00); Abraham Jette-Kouri saw the same on IDEA-06 (N15, N5); IDEA-00, IDEA-06; 0.3.0, 0.3.1 (bug); 0.3.2 (fixed); 2026-10-08T17:52:46Z, 18:24:58Z, 19:52:23Z | N13, N14, N15, N5, 6be92c08, aa236105, 68bee25b, bcb16c24, 012e24c7, 2ab514eb, c5dd91d2 | Done | In progress | Pending. |
| [N14](#n14) | Send feedback should match the website's (0235 contract) | MR. PINA (apina@boscotech.edu); IDEA-00; 0.3.1 (note); 0.3.2 current; 2026-10-08T18:25:47Z | N14 | Not done | In progress | Pending. |
| [N15](#n15) | A thumbnail of each part | Abraham Jette-Kouri; IDEA-06; 0.3.0 (note); 0.3.2 current; 2026-10-08T17:52:46Z | N15 | Partly done | In progress | Pending. |
| [I-checkIn-IDEA-00-0.3.1](#i-checkin-idea-00-0.3.1) | Folder check in of 1,424 files waited 14.8 s | MR. PINA; IDEA-00; 0.3.1; asked 2026-10-08T19:43:39.4Z answered 19:43:54.2Z | c2d5d46c | Partly done | In progress | Pending. |
| [I-checkIn-IDEA-06-0.3.1](#i-checkin-idea-06-0.3.1) | Check in row clicks queued: 15 and 16 clicks answered one pass apart (up to 658 s) | Abraham Jette-Kouri; IDEA-06; 0.3.1; bcb16c24: 15 clicks 2026-10-08T19:47:32-19:47:37Z answered 19:48:24-19:56:33Z; ccf970d7/428a0f6f: 16 clicks 21:01:38-21:01:51Z answered 21:02:19-21:12:49Z; ee443a83: 23:09:37Z to 23:10:49Z | bcb16c24, ccf970d7, 428a0f6f, ee443a83 | Not done | In progress | Pending. |
| [I-checkOut-IDEA-00-0.3.1](#i-checkout-idea-00-0.3.1) | Folder check out of 1,424 files waited 124-133 s | MR. PINA; IDEA-00; 0.3.1; aa236105 asked 2026-10-08T18:22:57Z answered 18:25:10.6Z; 68bee25b asked 18:35:22.8Z answered 18:37:27.3Z | aa236105, 68bee25b | Partly done | In progress | Pending. |
| [I-checkOut-IDEA-06-0.3.1](#i-checkout-idea-06-0.3.1) | Check out waited 44-72 s (one file, and 16 files once) | Abraham Jette-Kouri; IDEA-06; 0.3.1; 2026-10-08T19:45:11Z, 20:17:39Z, 20:49:40Z, 22:38:35Z, 22:59:16Z | f4ea534f, ea2b2a2c, 968cc0cd, 6d145628, 12ae9081 | Partly done | In progress | Pending. |
| [I-crash-DESKTOP-F41DB2R-0.3.1](#i-crash-desktop-f41db2r-0.3.1) | DESKTOP-F41DB2R 0.3.1 two crash reports: killed right after 'quitting' | MR. PINA; DESKTOP-F41DB2R; 0.3.1; 2026-10-08T22:01Z, 2026-10-09T00:12Z | 62f377c1, e5e6e9f3 | Partly done | In progress | Pending. |
| [I-crash-IDEA-00-0.2.1](#i-crash-idea-00-0.2.1) | IDEA-00 0.2.1 'previous run ended unexpectedly' after quitting during a 39 s plan | MR. PINA; IDEA-00; 0.2.1; 2026-10-08T14:41Z | 7aa938ba | Partly done | In progress | Pending. |
| [I-crash-IDEA-06-0.3.0](#i-crash-idea-06-0.3.0) | Crash 2ab514eb: the app was ended during a ~60 s pass after 'quitting' | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08T18:56Z | 2ab514eb, 030b06da | Partly done | In progress | Pending. |
| [I-crash-IDEA-06-0.3.0-slowpa](#i-crash-idea-06-0.3.0-slowpa) | IDEA-06 0.3.0 crash report: killed 0-15 s after 'quitting' during a stale-marker scan or plan (sign-out) | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08T18:56Z | 2ab514eb, 184c9e24 | Partly done | In progress | Pending. |
| [I-crash-IDEA-06-0.3.1](#i-crash-idea-06-0.3.1) | IDEA-06 (Seraj) crash report about a 0.2.0 run with no evidence | Seraj Arteaga; IDEA-06 (Seraj's computer); 0.2.0 run, reported by 0.3.1; 2026-10-08T19:06Z-20:52Z | 87fa2b3d | Not done | In progress | Pending. |
| [I-launchFile-IDEA-06-0.3.1](#i-launchfile-idea-06-0.3.1) | Open (launchFile) waited 13-31 s | Abraham Jette-Kouri (3), Seraj Arteaga (2d479aea); IDEA-06; 0.3.1; 2026-10-08T19:31:52Z, 20:55:36Z, 21:29:29Z, 22:27:56Z | d5c6825d, 2d479aea, 1400e8b8, 4a444b9a | Partly done | In progress | Pending. |
| [I-readOnlyBroken-IDEA-06-0.3.0](#i-readonlybroken-idea-06-0.3.0) | readOnlyBroken on Hook V3.SLDPRT right after a release | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08 17:52Z | 6be92c08 | Not done | In progress | Pending. |
| [I-readOnlyBroken-IDEA-06-0.3.1](#i-readonlybroken-idea-06-0.3.1) | readOnlyBroken on Toparmredesign, Toparmredesignnoscrewpocket and SmallFlywheel V3 (12 incidents) | Abraham Jette-Kouri; IDEA-06; 0.3.1; 2026-10-08 21:12Z to 23:08Z | 1c1b1c5b, 012e24c7, a6f9e941, 7be63dcf, 40a07aa9, e0722b64, 88fc8771, 3aa1ef68, 7352f99d, 25d938f6, e07af0ab, 2cf39a10 | Not done | In progress | Pending. |
| [I-slowAction-DESKTOP-QH30N35-0.3.2](#i-slowaction-desktop-qh30n35-0.3.2) | slowAction c5dd91d2: Check out of 1 file took 15.2 s on an idle computer | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:55:56Z | c5dd91d2,N1,N3 | Not done | In progress | Pending. |
| [I-slowAction-IDEA-06-0.3.1](#i-slowaction-idea-06-0.3.1) | Slow check in and check out actions on IDEA-06 (timeline evidence for N4) | Abraham Jette-Kouri; IDEA-06; 0.3.1; 2026-10-08 19:46Z to 23:10Z | f4ea534f, bcb16c24, ea2b2a2c, 968cc0cd, 428a0f6f, ccf970d7, 6d145628, 12ae9081, ee443a83 | Partly done | In progress | Pending. |
| [I-slowPass-DESKTOP-F41DB2R-0.3.1](#i-slowpass-desktop-f41db2r-0.3.1) | DESKTOP-F41DB2R 0.3.1 passes of 62-90 s during the first full download and after a restart | MR. PINA; DESKTOP-F41DB2R; 0.3.1; 2026-10-08T21:03Z-22:09Z | N3, 4a5a4860, d3e64083, 1de30671, 8adfb590 | Partly done | In progress | Pending. |
| [I-slowPass-DESKTOP-QH30N35-0.3.2](#i-slowpass-desktop-qh30n35-0.3.2) | slowPass 7a6c7d95: a 94.5 s loop pass held by one straggling 31.5 MB download | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:50:47Z | 7a6c7d95,N3 | Not done | In progress | Pending. |
| [I-slowPass-DESKTOP-QH30N35-0.3.2-slowpa](#i-slowpass-desktop-qh30n35-0.3.2-slowpa) | DESKTOP-QH30N35 0.3.2 download pass 'still going after 94 s': one stalled 31.6 MB download held the pass | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:49Z-05:56Z | N3, N6, 7a6c7d95, c5dd91d2 | Not done | In progress | Pending. |
| [I-slowPass-IDEA-00-0.3.0](#i-slowpass-idea-00-0.3.0) | IDEA-00 0.3.0 loop pass of 67.5 s with 2 large downloads | MR. PINA; IDEA-00; 0.3.0; 2026-10-08T18:09Z | 58d36379 | Partly done | In progress | Pending. |
| [I-slowPass-IDEA-00-0.3.1](#i-slowpass-idea-00-0.3.1) | IDEA-00 0.3.1 quiet loop passes of 60-82 s (0 moved), 22:16Z to 01:54Z | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T22:16Z to 2026-10-09T01:54Z | N6, 4cd9eae5, 8cb7df1d, 3c2c8c20, 42136488, e3ead3f1, b603a79e, 14d13f99, 1e6ba2cd, 9c3bf2db, 1f4c1092, b41a2f31, 8add4e24, c681d282, 57f69630, 76cc8f4b, 2e8f7fb1, 36077bff, 71b59d78, 32118ea1, f7bc5be4, 056b7c00 | Partly done | In progress | Pending. |
| [I-slowPass-IDEA-00-0.3.1-action](#i-slowpass-idea-00-0.3.1-action) | IDEA-00 0.3.1 action passes of 92-94 s (Check out of a whole folder) | MR. PINA; IDEA-00; 0.3.1; 2026-10-08T18:23Z-18:38Z | N6, N11, 1f22e3a0, 9e2aca4c, aa236105, 68bee25b | Partly done | In progress | Pending. |
| [I-slowPass-IDEA-06-0.3.0](#i-slowpass-idea-06-0.3.0) | IDEA-06 (Abraham) 0.3.0 first pass after a restart and new sign-in took 129 s | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08T17:45Z-17:48Z | 610eed69 | Partly done | In progress | Pending. |
| [I-slowPass-IDEA-06-0.3.1](#i-slowpass-idea-06-0.3.1) | IDEA-06: the same 142 files refused on every pass are name-taken copies, re-planned forever (not the year check) | Abraham Jette-Kouri; IDEA-06; 0.2.0-0.3.1 observed; unchanged at 0.3.2 and HEAD 0ef1ae0 (reproduced); 2026-10-07 (0.2.x refusals already present) through 2026-10-09T03:21Z | 3b6e0912, 68d7a9d4, f9912084, 030b06da, 184c9e24, 2ab514eb, 610eed69, N4 (its log shows the same 142 refused every ~50 s) | Not done | In progress | Pending. |
| [I-slowPass-IDEA-06-0.3.1-slowpa](#i-slowpass-idea-06-0.3.1-slowpa) | IDEA-06 (Seraj) 0.3.1 loop pass of 3134.6 s: the process was frozen for 52 minutes | Seraj Arteaga; IDEA-06 (Seraj's computer); 0.3.1; 2026-10-08T22:00Z-22:52Z | 5da43de6, 87fa2b3d | Not done | In progress | Pending. |
| [I-slowPass-IDEA-06-142refused](#i-slowpass-idea-06-142refused) | IDEA-06 (Abraham) passes with 126-142 refused (audited by another agent); phase pointer only | Abraham Jette-Kouri; IDEA-06; 0.3.0, 0.3.1; 2026-10-08T18:00Z-2026-10-09T03:21Z | 3b6e0912, 68d7a9d4, f9912084, 030b06da, 184c9e24, 2ab514eb | Partly done | In progress | Pending. |
| [I-takeBack-IDEA-00-0.2.1](#i-takeback-idea-00-0.2.1) | takeBack waited 35.5 s (0.2.1) | MR. PINA; IDEA-00; 0.2.1; asked 2026-10-07T23:38:51Z, answered 23:39:26.9Z (uploaded 2026-10-08T14:41Z) | 9059fddb | Partly done | In progress | Pending. |
| [I-takeBack-IDEA-00-0.3.0](#i-takeback-idea-00-0.3.0) | takeBack waits of 24 s to 2,450 s: Force check in all queued 225 single-file actions | MR. PINA; IDEA-00; 0.3.0; 2026-10-08T17:10:08Z to 17:51:00Z | 80c8eb79, 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5 | Partly done | In progress | Pending. |
| [C-0.2.1-action-never-waits](#c-0.2.1-action-never-waits) | ENGINE.md 712-737: 'An action never waits behind a whole pass' | 0.2.1 to 0.3.2 | N6, all slowAction groups | Not done | In progress | Pending. |
| [C-0.2.1-file-detail-never-waits](#c-0.2.1-file-detail-never-waits) | ENGINE.md 212-214: 'File detail never waits for a pass' | 0.3.2 | c5dd91d2 (reference), N6 | Not done | In progress | Pending. |
| [C-0.3.0-batches](#c-0.3.0-batches) | 0.3.0 batch check out/in (armory_lock_files, armory_release_locks) | 0.3.0 | aa236105 | Done | In progress | Pending. |
| [C-0.3.0-cantakeback](#c-0.3.0-cantakeback) | 0.3.0 can_take_back decides who sees Force check in | 0.3.0 | N11 | Done | In progress | Pending. |
| [C-0.3.0-deleteforever](#c-0.3.0-deleteforever) | 0.3.0 delete forever (project and folder purges) | 0.3.0 |  | Done | In progress | Pending. |
| [C-0.3.0-live](#c-0.3.0-live) | 0.3.0 live updates (RealtimeFeed) | 0.3.0 | N12, 809ae174 | Done | In progress | Pending. |
| [C-0.3.0-send-feedback](#c-0.3.0-send-feedback) | 0.3.0: Send feedback goes straight to the IDEA team | 0.3.0 to 0.3.2 | N13, N14, 6be92c08, aa236105, 68bee25b, f9912084, c2d5d46c, bcb16c24, c5dd91d2 | Done | In progress | Pending. |
| [C-0.3.0-teamstatus](#c-0.3.0-teamstatus) | 0.3.0 team status (armory_heartbeat) | 0.3.0 | N7 | Done | In progress | Pending. |
| [C-0.3.1-force-all-fast](#c-0.3.1-force-all-fast) | Release notes 0.3.1: Force check in all is one go | 0.3.1, 0.3.2 | 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5 | Done | In progress | Pending. |
| [C-0.3.1-forcemany](#c-0.3.1-forcemany) | 0.3.1 Force check in of many files is one action and one pass | 0.3.1 | 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5 | Done | In progress | Pending. |
| [C-0.3.2-batched-open-checks](#c-0.3.2-batched-open-checks) | 0.3.2 claim: batched open-file checks end the 40 s passes, a quiet sync takes a second or two, clicks answer right away | 0.3.2 | N6, 7a6c7d95, c5dd91d2, 1f22e3a0, 4cd9eae5 | Partly done | In progress | Pending. |
| [C-0.3.2-fast-passes](#c-0.3.2-fast-passes) | 0.3.2 release note: a sync with nothing to do takes a second or two and clicks answer right away | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09 | c5dd91d2,7a6c7d95,N1,N3 | Partly done | In progress | Pending. |
| [C-0.3.2-feedback-sent](#c-0.3.2-feedback-sent) | 0.3.2: Send feedback no longer says 'couldn't send' for notes that were sent | 0.3.2 | N13, N14, N15, N5 | Done | In progress | Pending. |
| [C-0.3.2-folder-handover](#c-0.3.2-folder-handover) | 0.3.2: Use this folder hand-over plus Switch account | 0.3.2 | N10 | Partly done | In progress | Pending. |
| [C-0.3.2-minekeys](#c-0.3.2-minekeys) | 0.3.2 Check in all and Undo all in My files, with its own scroll box | 0.3.2 | N11, N12 | Done | In progress | Pending. |
| [C-0.3.2-pinnedkeys](#c-0.3.2-pinnedkeys) | 0.3.2 folder keys pinned while scrolling | 0.3.2 | N9 | Done | In progress | Pending. |
| [C-0.3.2-quick-quit](#c-0.3.2-quick-quit) | 0.3.2 claim: quitting is quick again, so Windows no longer forces Armory closed (crash reports) | 0.3.2 | 7aa938ba, 2ab514eb, 62f377c1, e5e6e9f3 | Partly done | In progress | Pending. |
| [C-0.3.2-responds-right-away](#c-0.3.2-responds-right-away) | Release notes 0.3.2: 'Everything responds right away... a sync with nothing to do takes a second or two and your clicks answer right away' | 0.3.2 | c5dd91d2 (reference), N6 | Partly done | In progress | Pending. |
| [C-0.3.2-runninglines](#c-0.3.2-runninglines) | 0.3.2 'What Armory is doing' running lines | 0.3.2 | N8, c5dd91d2 | Partly done | In progress | Pending. |
| [C-0.3.2-slice-counts](#c-0.3.2-slice-counts) | ENGINE.md: the activity panel keeps its counts across slices; the server is read again about every 10 s | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09 | N3,N2 | Partly done | In progress | Pending. |
| [C-0.3.2-theme](#c-0.3.2-theme) | 0.3.2 instant theme switching | 0.3.2 | N7 | Partly done | In progress | Pending. |
| [C-0.3.2-thumbnails](#c-0.3.2-thumbnails) | 0.3.2: pictures of parts from Windows' thumbnail handlers | 0.3.2 | N15 | Partly done | In progress | Pending. |
| [X-142-ui-uploading-0-of-142](#x-142-ui-uploading-0-of-142) | Window: status cycles 'Checking for changes.' / flash of 'Uploading 0 of 142 files, 260.9 MB left' / 'A few files need you', and the incident snapshot froze the flash | Abraham Jette-Kouri; IDEA-06; 0.3.0, 0.3.1; same code at HEAD; 2026-10-08..09 | 68d7a9d4, 184c9e24, f9912084, N2, N8 | Not done | In progress | Pending. |
| [X-38-uploading-forever](#x-38-uploading-forever) | 38 files read 'uploading' forever on every computer (IDEA-00, IDEA-06, DESKTOP-F41DB2R, DESKTOP-QH30N35) | everyone in FRC 2026 Off-Season; all four; 0.2.1-0.3.2; 2026-10-08..09 | 58d36379, 4a5a4860, 7a6c7d95, c5dd91d2, 184c9e24 and every snapshot after 18:10Z (45 on IDEA-00 at 0.2.1) | Not done | In progress | Pending. |
| [X-6-extra-nameShared-archived](#x-6-extra-nameshared-archived) | 148 nameShared items vs 142 refused per pass: refusals in a project that is later archived are never cleared; never-added files there read 'uploading' | Abraham Jette-Kouri; IDEA-06; 0.3.0-HEAD; 2026-10-08..09 | 68d7a9d4, 184c9e24, 1c1b1c5b (nameShared 148, refused 142) | Not done | In progress | Pending. |
| [X-N5a-organize-checked-out](#x-n5a-organize-checked-out) | N5(a): moving or renaming a file or folder someone else has checked out | 0.3.2 (HEAD 8d90d63) | N5 | Not done | In progress | Pending. |
| [X-N5b-force-check-in](#x-n5b-force-check-in) | N5(b): instructor override (Force check in) for single files, folders and selections, and how it reads for the holder | 0.3.2 | N5 | Partly done | In progress | Pending. |
| [X-break-locks-batch-unused](#x-break-locks-batch-unused) | armory_break_locks (migration 0234) not adopted | 0.3.2 | 8bbe9bd5 | Not done | In progress | Pending. |
| [X-checkin-release-guard](#x-checkin-release-guard) | PrepareRelease lets a lock go over bytes it never read and ignores an open file for check in, undo and Check in all | IDEA-06; 0.3.0 to 0.3.2 (unchanged since 0bc573f; git diff 0bc573f..HEAD touches only KnowOpen batching) | N4, 7352f99d, a6f9e941, 1c1b1c5b | Not done | In progress | Pending. |
| [X-checkout-rehash](#x-checkout-rehash) | Check out reads and hashes every target file again | 0.3.2 | aa236105, 68bee25b | Not done | In progress | Pending. |
| [X-checkout-rehash-slowpa](#x-checkout-rehash-slowpa) | Checking out many files re-hashes every one of them |  | N11, 1f22e3a0, 9e2aca4c | Not done | In progress | Pending. |
| [X-download-cycle](#x-download-cycle) | Downloads run only about 45% of the time: each 8 s slice is followed by a full rescan and 10.5 s plan (N3) |  | N3, c5dd91d2, 7a6c7d95 | Not done | In progress | Pending. |
| [X-feedback-snapshot-lost](#x-feedback-snapshot-lost) | Feedback and incident snapshots lost when the engine is busy | 0.3.1, 0.3.2 | N4, N5, N7, N9, N13, 12ae9081, 428a0f6f, 6d145628, 968cc0cd, bcb16c24, ccf970d7, ea2b2a2c, ee443a83, f4ea534f | Not done | In progress | Pending. |
| [X-full-render](#x-full-render) | Every view re-renders all of Home; slow with thousands of files and thumbnails blink | 0.3.2 | N6, N7, N9, c5dd91d2 | Not done | In progress | Pending. |
| [X-hash-cache-not-persisted](#x-hash-cache-not-persisted) | First scan after every start re-hashes the whole vault (10-38 s) |  | 8adfb590, 030b06da, 610eed69, 1f22e3a0, 68bee25b | Not done | In progress | Pending. |
| [X-kept-save-every-pass](#x-kept-save-every-pass) | A checked-out file whose save is already kept counts as 'moving 1' and flashes 'Uploading 0 of 1 file' every pass | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:57:45Z onward | N1 | Not done | In progress | Pending. |
| [X-long-passes-1108s](#x-long-passes-1108s) | The 1,108.8 s and 369.9 s passes were the computer asleep or frozen, not sync work | Abraham Jette-Kouri; IDEA-06; 0.3.0; 2026-10-08T18:24-18:49Z | 68d7a9d4, f9912084 | Not reproducible | In progress | Pending. |
| [X-no-version-uploading](#x-no-version-uploading) | 38 team files with no current version show 'Uploading' forever on a computer that uploads nothing | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09 | N2,N3 | Not done | In progress | Pending. |
| [X-open-files-question](#x-open-files-question) | 'Restart Manager did not answer within 10 s': the open-files question over every file blocks the engine thread every pass | MR. PINA; DESKTOP-QH30N35; 0.3.2; 2026-10-09T05:51:37Z | N3,N2,N1,7a6c7d95,c5dd91d2 | Not done | In progress | Pending. |
| [X-openamong-blocks-engine](#x-openamong-blocks-engine) | Windows open-file question blocks the engine thread up to 10 s every pass | DESKTOP-QH30N35; 0.3.2 | c5dd91d2 (reference), N6, N5 context | Not done | In progress | Pending. |
| [X-owner-name](#x-owner-name) | The folder-taken screen never names the real owner, and flashes the new student as the owner | 0.3.1, 0.3.2 | N10 | Not done | In progress | Pending. |
| [X-parked-writable](#x-parked-writable) | While another account (or nobody) is signed in, the owner's checked-out files stay writable and unwatched | 0.3.2 | N10 | Not done | In progress | Pending. |
| [X-pass-48-60s](#x-pass-48-60s) | Why each IDEA-06 pass took 48-60 s: per-file open checks in the plan phase, not the refusals; 0.3.2 only partly fixes it | Abraham Jette-Kouri; MR. PINA (0.3.2 data); IDEA-06; DESKTOP-QH30N35 for 0.3.2 data; 0.3.0, 0.3.1 (slow); 0.3.2 (better, still ~16 ms per local file); 2026-10-08..09 | 68d7a9d4, f9912084, 184c9e24, 2ab514eb, 030b06da, 7a6c7d95 (0.3.2 field data) | Partly done | In progress | Pending. |
| [X-pending-count](#x-pending-count) | Folder-wide actions overcount and mark every row as being worked on | 0.3.2 | N9, N11, N12 | Not done | In progress | Pending. |
| [X-readonly-manifest-per-file](#x-readonly-manifest-per-file) | Bulk check out/in rewrites the durable read-only manifest once per file | 0.3.1, 0.3.2 | aa236105, 68bee25b, c2d5d46c | Not done | In progress | Pending. |
| [X-recovery](#x-recovery) | Abraham's work is recoverable from server kept copies (and local snapshots); the app has no restore | Abraham Jette-Kouri; IDEA-06; 0.3.1; 2026-10-08 22:40Z to 23:11Z | N4, 12ae9081, 184c9e24 | Partly done | In progress | Pending. |
| [X-responsiveness-test-gap](#x-responsiveness-test-gap) | No window action is latency-tested on a large vault | 0.3.2 | N6 | Not done | In progress | Pending. |
| [X-signed-out-as-refusal](#x-signed-out-as-refusal) | A sign-out during a pass is recorded as a permanent-looking refusal with wrong words (explains cantSend 574 -> 344 -> 0 on IDEA-06) | Abraham Jette-Kouri; IDEA-06; 0.2.0-HEAD; 2026-10-08 | 610eed69, 6be92c08, 3b6e0912 | Not done | In progress | Pending. |
| [X-stale-markers](#x-stale-markers) | 224 stale ~$ markers on IDEA-06 are asked about every pass |  | 184c9e24, 2ab514eb | Partly done | In progress | Pending. |
| [X-stale-unread-entry](#x-stale-unread-entry) | The Windows scan reuses the previous hash and read-only flag for a file it cannot open, with no marker | IDEA-06; all through 0.3.2 | N4, all 13 readOnlyBroken incidents | Not done | In progress | Pending. |
| [X-sticky-focus](#x-sticky-focus) | Arrow keys move focus onto rows hidden under the pinned folder keys | 0.3.2 | N9 | Not done | In progress | Pending. |
| [X-telemetry-refusals](#x-telemetry-refusals) | Incidents cannot name the refused files: refusals are never in the flight, the 200-notice cap is spent on stale ~$ markers, snapshots carry only notice counts | IDEA-06; 0.3.0-HEAD; 2026-10-08..09 | 68d7a9d4, f9912084, 184c9e24, 2ab514eb | Not done | In progress | Pending. |
| [X-test-doubles-blind](#x-test-doubles-blind) | Neither the end-to-end file system nor the Core checkout simulation can see this bug |  | N4 | Not done | In progress | Pending. |
| [X-two-computers-named-IDEA-06](#x-two-computers-named-idea-06) | Two different computers report the device name IDEA-06 |  | N10, 5da43de6, 87fa2b3d, 25d938f6, 428a0f6f, 2d479aea | Not done | In progress | Pending. |
| [X-two-idea-06](#x-two-idea-06) | IDEA-06 is two different computers with the same name | Abraham Jette-Kouri, Seraj Arteaga; IDEA-06; 0.3.0, 0.3.1; 2026-10-08T20:42Z to 21:02Z | 2d479aea, 87fa2b3d, 968cc0cd, 428a0f6f, 2ab514eb | Not done | In progress | Pending. |
| [X-two-idea06-engines](#x-two-idea06-engines) | Two engines report device IDEA-06 at the same time with separate vaults | Seraj Arteaga, Abraham Jette-Kouri; IDEA-06; 0.3.1 | 87fa2b3d, 2d479aea, 5da43de6 | Not reproducible | In progress | Pending. |
| [X-ui-checks-coverage](#x-ui-checks-coverage) | UI checks miss the N7 to N12 behaviors and do not run in CI | 0.3.2 | N7, N8, N9, N11, N12 | Not done | In progress | Pending. |
| [X-uploading-vs-saved](#x-uploading-vs-saved) | Rows say Uploading while the status says Everything is saved | IDEA-00; 0.3.1 | N11, N12, ed7dc8f8, 809ae174, aa236105 | Not done | In progress | Pending. |
| [X-version-40](#x-version-40) | armory_heartbeat refuses app_version over 40; nothing holds Armory's version to 40 | 0.3.0 to 0.3.2 |  | Not done | In progress | Pending. |
| [X-view-open-per-file](#x-view-open-per-file) | Files added while open cost one Restart Manager session each on every view build and twice more per pass |  | N6, 610eed69 | Not done | In progress | Pending. |
| [X-wrong-words](#x-wrong-words) | The window says 'Checked in' when nothing was shared, and then blames the student for saving without a check out | IDEA-06; 0.3.1, 0.3.2 | N4 | Not done | In progress | Pending. |
| [X-year-check-docs](#x-year-check-docs) | Docs, screens and UI text that imply the year check works (to correct) | docs at HEAD | user statement | Not done | In progress | Pending. |
| [X-year-check-off](#x-year-check-off) | The SolidWorks year check is off in the shipped app: ReleaseReader = null, so warn uploads 2026 files unchecked and enforce refuses every SolidWorks file | user report; all; 0.1.0-HEAD; since 0.1.0 | user statement; no incident | Not done | In progress | Pending. |
| [X-year-find-2026](#x-year-find-2026) | Can Armory find files already uploaded as SolidWorks 2026? Not today; the server stores null for every version | 0.1.0-HEAD | user question | Not done | In progress | Pending. |

## Notes, one per row

### N1

**A styled tooltip on every button after about a second of hover** (note; refs N1).

- Found at 0.3.2: **Partly done**. Only native HTML title tooltips (plain OS tooltip, browser delay, no styling, no keyboard trigger), on 22 of 68 page controls; 3 row overlays have one only for files with no file id; 43 have none. key() helper emits title only when a caller passes o.title (src/Armory.Agent/wwwroot/app.js:362-376). WITH a title: header Open Armory folder / Send feedback / Settings (app.js:551,555,559; Settings title just repeats its word), Pause/Resume (743,747), Check out and reopen (838), Rename on a name-shared item (1089-1090), My files Check in all / Undo all (1249-1250), My files row Check in (1296-1297), row Open (openKey 1190-1195), row Check out / Check in / Force check in (stateKey 1206-1218), folder keys New folder, Add files, Rename folder, Delete folder, Check out all, Check in all, Force check in all (1420-1430), Back (1632), detail Open (1608). PARTIAL: row-hit overlay (rowHtml 1124-1128) has title only when hit.hint is set (files with no file id: Show in folder, or the namesake hint), so notice item rows, My files rows and Team files rows with a file id have none. WITHOUT: Connect: Open the browser again, Cancel, Connect this computer/Try again (595-598); folder taken: Use this folder, Use <path>, Choose another folder, Sign out (658-662); Switch account, Sign out of Armory (812-813); prompt Not now/OK (839-840); notice action, Show/Hide the files, See the file (1032-1040); empty My files Open Armory folder (1239); project tabs (1333-1335); breadcrumbs (1396); folder rows (rowHtml via browserRow 1453-1465); select checkbox (1472, aria-label only); selection bar Check out, Check in, Undo check out, Force check in, Clear (1519-1523); detail Check out, Check out and open, Check in, Undo check out, Force check in (1611-1617), Show in folder (1619); Settings Done (1728), Change (1733), Start at sign in switch (1740), 3 theme segments (1750), Report a problem, Send feedback, Open incidents folder (1760-1762); dialog OK/Send and Cancel (1891-1892); 3 report-kind segments (1907). index.html has no static controls (header keys and dialogs are filled by app.js). Host: tray menu Open Armory, Open Armory folder, Pause/Resume, Connect/Sign out, Quit (src/Armory.Agent/TrayApp.cs:44-49) have no ToolTipText and ShowItemToolTips is never set; the tray icon's hover text is the sync line (TrayApp.cs:122-123); the WebView2-missing 'Get WebView2 Runtime' button (src/Armory.Agent/MainWindow.cs:443-452) has no ToolTip. Totals: 75 controls, 23 with a tooltip, 3 partial, 49 without.
- How it was verified then: Read code: enumerated all 41 '<button' literals (one is inside key()) and all 23 key({...}) calls in app.js with grep -n and read each site's attributes; index.html read whole; TrayApp.cs and MainWindow.cs read for WinForms controls; app.css 1705-1730 and 4114-4135 read for which keys hide their words in a narrow window (all icon-only keys do have a title). No custom tooltip code exists (grep tooltip/data-tip in app.js, app.css, bridge.js: comments only).
- Cause: Tooltips were added ad hoc via the title attribute only where a key could become icon-only in a narrow window; there is no tooltip component and no rule or check requiring one per control.
- Missing at 0.3.2: A designed tooltip (theme tokens, consistent delay about 700-800 ms, shows on keyboard focus, readable width, positioned inside the window) and plain-words text for all 75 controls; tips on disabled keys (Chromium sends no pointer events to a disabled button, so a custom tip needs aria-disabled or a wrapper); tray menu tooltips; the WebView2 fallback button tooltip; a UI check that fails when a control has no tip. key()'s comment (app.js:360-361) promises the word 'stays in the tooltip' but 11 key() calls pass no title.
- 0.3.3: **In progress**. 

### N2

**Running lines say 'Sync finished' after every slice while hundreds of files remain** (note; refs N2,N3,7a6c7d95,c5dd91d2).

- Found at 0.3.2: **Not done**. SyncEngine.LogPass (src/Armory.Agent.Engine/SyncEngine.cs:571-586) runs in PassLockedAsync's finally for every pass and writes activity.Log("Sync finished: " + ...) whenever the pass downloaded, uploaded or kept anything (line 579), with no test of cutShort. Only the agent log line gets the suffix ', the rest continues at once' (line 585). A loop pass is cut short after PassSlice = 8 s of phase C (SyncEngine.cs:23, 1128, 1131) and the loop starts the next pass at once (244-249), so a 1,429-file download produced 18 'Sync finished: N files downloaded.' running lines. ActivityTracker.Log (ActivityTracker.cs:110-118) keeps 40 lines for 3 minutes and Finish adds one 'Downloaded X (size)' line per file (line 126), so at each pass end the 'Sync finished' line is the newest line at the foot of 'What Armory is doing', exactly during the 3-12 s gap when nothing moves. ENGINE.md:297-302 documents the per-pass line as intended. The words also break decision D5 (SyncEngine.Folders.cs:1301 'keeps sync out of the window') and the UI check's jargon rule '\bsync(s/ed/ing)?\b' (tools/agent-ui/check-ui.mjs:257); the check never sees it because no demo state contains the line.
- How it was verified then: Read code (LogPass, LoopAsync, RunUnitsAsync, PassLockedAsync, ActivityTracker). N2 Context: lastPasses all cutShort true with 83/80/78 downloaded, snapshot sync.line 'Checking for changes.', activity null, 28 files notOnThisComputer and 106 still moving at the pass that preceded the report. Reproduced the tracker sequence in a throwaway test N123ProbeTests.Between_continuation_passes_the_download_lane_is_hidden in worktree scratchpad/wt-n123 (dotnet test tests/Armory.Agent.Engine.Tests --filter N123ProbeTests: passed): after a cut-short pass the newest line is 'Sync finished: 20 files downloaded.' while 80 of 100 files are left and Line/Download are null.
- Cause: LogPass treats every pass as a complete unit of work; the slice design (PassSlice) made a long download many passes, but the running line was never made aware of cutShort.
- Missing at 0.3.2: A finished line only when the whole run is done (not cut short, nothing left), one line for the whole run instead of one per slice, progress lines in its place, wording without 'sync', a test.
- 0.3.3: **In progress**. 

### N3

**A 1,400-file download stops and starts, showing 'Checking for changes' between batches** (note; refs N3,N2,7a6c7d95,c5dd91d2).

- Found at 0.3.2: **Not done**. Mechanism in HEAD: (1) A loop pass starts no new transfer after PassSlice 8 s (SyncEngine.cs:23, 1128); in-flight transfers finish, the not-started units are dropped from the activity (1133), cutShort is set (1131) and LoopAsync starts a new pass at once (244-249). (2) Each new pass runs phase A and B before any transfer: fs.Scan (456), RefreshAsync (481: armory_my_projects, armory_list_changes, often armory_project_files), then PlanAllAsync, whose KnowOpen(local.Values...) (964, 1240-1249) asks the open-files question for EVERY file on disk; WindowsVaultFileSystem.OpenAmong (109-127) calls OpenFileDetector.OpenAmong (Armory.Platform.Windows/OpenFileDetector.cs:52-78): an exclusive-open probe on each file, then Restart Manager in batches of 500, waited synchronously on the engine thread up to OpenBudget = 10 s (WindowsVaultFileSystem.cs:129). Field cost: about 14.6 ms per file on disk, so the plan phase grew 0.49, 1.8, 2.8, 3.9, 5.4, 6.7, 7.6, 8.8, 9.6 s and then sat at 10.4-10.5 s for every later pass. (3) Visible status: after Drop, the download lane has FilesDone == FilesTotal so ActivityTracker.Snapshot skips it (ActivityTracker.cs:240): no Downloading direction, and the status line falls back to 'Checking for changes.' (SyncEngine.View.cs:43; app.js:736, 984), readout 'Updating'; at the pass end syncing=false and PublishLocked runs (SyncEngine.cs:403, 412), so a view saying 'Everything is saved to Armory.' (readout 'All saved') can reach the window and the tray (TrayApp.OnViewChanged posts every view, TrayApp.cs:93-127, icon and hover text flip) before the next pass publishes syncing again; plus the 'Sync finished' running line (N2). The comment at SyncEngine.cs:404-406 and ENGINE.md:729-733 say the counts are kept across slices; they are kept in the lane but hidden until the next pass's ExpectTransfers (1121). (4) Tail: after the slice no new unit starts, so one slow file holds the pass with idle lanes (85.7 s move phase in 7a6c7d95).
- How it was verified then: Read code (LoopAsync, PassLockedAsync, PassAsync, PlanAllAsync, KnowOpen, RunUnitsAsync, RunConcurrentlyAsync, ActivityTracker, BuildView, TrayApp, MainWindow view coalescing, OpenFileDetector, WindowsVaultFileSystem). Flight recorder of c5dd91d2 (whole download) and 7a6c7d95 analyzed with scratchpad/tools/n123-passes.py (per-pass phase ms) and n123-gaps.py (time with zero downloads running): 1,429 downloads, 2.08 GB, wall 421.2 s, no download running for 170.7 s (41%), 18 gaps growing from 3.1 s to 11-12 s, mean concurrency 2.41 of 6; plan phases total 151.7 s, move phases total 251.9 s. Reproduced the hidden lane and fallback line with throwaway N123ProbeTests in worktree wt-n123 (passed).
- Cause: The engine's unit of work is a whole pass (scan, server read, plan of every path, transfers, finish). Slicing a long download into 8 s passes makes every boundary pay the full fixed cost, and 0.3.2's batched open-files question made that fixed cost grow with the files already downloaded (about 14.6 ms per file, capped at the 10 s Restart Manager budget), all on the engine thread. The activity tracker drops the not-started work at each boundary and the pass end publishes a non-syncing view, so the window shows the stop.
- Missing at 0.3.2: A transfer pipeline that keeps running across pass boundaries; planning that does not ask the open-files question for files whose plan cannot depend on it; the open-files question off the engine thread; a status that stays 'Downloading N of M' between continuation passes; no synced/All saved view between continuation passes; no rescan or re-plan of everything when nothing changed; refill of idle lanes during a slow file; stall detection for a stuck download.
- 0.3.3: **In progress**. 

### N4

**Check in after editing a saved part does not save the changes; the file on disk is replaced by the previous version** (note; refs N4, 7352f99d, 25d938f6, e07af0ab, a6f9e941, 2cf39a10, 12ae9081, 6d145628, ee443a83).

- Found at 0.3.2: **Not done**. Check in (src/Armory.Agent.Engine/SyncEngine.Checkout.cs:181-198) sets Request=CheckIn and runs one pass. The plan uses the scan's hash (SyncEngine.cs:463, 1065). FinishRequestsAsync -> PrepareRelease (SyncEngine.Checkout.cs:640-680) treats the file as clean when `file?.Hash == st.BaseHash` (line 671), sets read-only (675) and releases. There is no open check for a CheckIn request (only AutoCheckIn adds check IsOpenNow, line 656) and no fresh read. On Windows, when SolidWorks holds the part open for writing, LocalChangeDetector cannot open it (line 153 opens with FileShare.Read/Delete, which conflicts with SolidWorks' write handle) and keeps the PREVIOUS cached entry with no flag (LocalChangeDetector.cs:172-178; WindowsVaultFileSystem.cs:91 maps it to LocalFile unchanged). So the stale hash equals the base and the check in releases the lock with nothing uploaded, and the window says "Checked in <name>." (SyncEngine.Checkout.cs:242). SolidWorks keeps its write handle and keeps saving in place. When SolidWorks closes the file, the next pass sees changed bytes on a file nobody has checked out: Explicit mode plans Keep(ChangedWithoutCheckOut) + Refresh (src/Armory.Core/Reconciler.cs:88), so PreserveAsync uploads the edits as a side version (SyncEngine.Actions.cs:192-213) and DownloadAsync writes the shared version over the student's file (SyncEngine.Actions.cs:103-152, Replace at 123 with the fresh local hash as expected hash). The kept-copy card then tells the student "Saved without a check out, so the checked-in version was put back" (SyncEngine.View.cs:195). The same thing happens through a check out (CheckOutStep.KeepChangesFirst, SyncEngine.Checkout.cs:739-744) as at 23:00:27. Preservation itself works: SaveSideVersion commits before the Download (Reconciler.cs:24, SyncEngine.cs:1217 stops the plan on failure), so bytes reach the server before the disk is overwritten.
- How it was verified then: Read the code paths above. Reconstructed the field timeline from all 13 readOnlyBroken incidents, the 9 slowAction incidents and the N4 log (per-person flight merge; see notes). Reproduced in a private worktree (scratchpad/wt-n4, detached at 8d90d63) with a throwaway PostgreSQL 16 cluster: added a SolidWorks write-hold model to tests/Armory.EndToEnd.Tests/PortableVaultFileSystem.cs (scan keeps the previous entry and reports 'being used by another process', OpenRead throws, IsOpen true, mirroring LocalChangeDetector.cs:172-178) and N4ReproTests.Check_in_while_SolidWorks_holds_the_file_never_loses_the_saved_edits. At HEAD it FAILS: check in answer ok=True 'Checked in Plate.SLDPRT.'; server current still v1, live locks 0, read-only set; after close and one pass disk='v1', side version 'alex.kim@students.test/changed without a check out', edits on server=True. With a prototype fix (scratchpad/n4-prototype-fix.patch) both repro tests pass and all 120 other EndToEnd tests pass (103 + 17).
- Cause: PrepareRelease decides 'clean' from the scan's hash, and the Windows scan silently reuses the previous hash (and read-only flag) for a file it cannot open. A part that SolidWorks holds open for writing is therefore always 'unchanged' to Armory, so its check in releases the lock without uploading. SolidWorks then saves in place through its existing write handle (the read-only bit is only checked at open), and when the document is closed the first readable pass treats the saves as 'changed without a check out': it keeps them as a side version and downloads the old shared version over the file.
- Missing at 0.3.2: (1) A check in (and Check in all, undo, an add's automatic check in) must never let go of a lock on bytes it did not read at that moment, and must wait while the file is open in SolidWorks. (2) The scan must say which entries it could not read. (3) The answer must not say 'Checked in' when nothing was shared; the kept-copy card must not blame the student. (4) Test doubles and the Core simulation do not model a file held open for writing, so no existing test can see this.
- 0.3.3: **In progress**. 

### N5

**Let people organize files others have checked out; instructor override to check in others' files** (note; refs N5).

- Found at 0.3.2: **Partly done**. (b) the override exists as Force check in for mentors, CAD leads and site admins: row key src/Armory.Agent/wwwroot/app.js:1215-1218, file page app.js:1617, folder 'Force check in all' app.js:1429-1430, selection bar app.js:1513-1522, confirmation app.js:1875-1882, one takeBack or ONE takeBackAll app.js:2008-2011; Bridge.cs:195-209; engine SyncEngine.Checkout.cs:266-311 (one file) and 324-399 (many: armory_break_lock 16 at a time, then one pass). Shown only when the server's can_take_back is true (SyncEngine.cs:649-650). (a) organizing a file or folder someone else has checked out is refused everywhere: window rename of a file Checkout.cs:507-508; Explorer move/rename put back Actions.cs:821-858 and 877-894; window rename/delete folder Folders.cs:1087-1088 and 1183-1184; Explorer folder rename/delete put back Folders.cs:664-685 with words at Folders.cs:372/681. There is no 'Move to folder' action in the window at all (Bridge.cs:10-33; renameFile keeps the folder, Checkout.cs:466-467; renameFolder keeps the parent, Folders.cs:1074).
- How it was verified then: Read app.js, Bridge.cs, SyncEngine.Checkout/Actions/Folders and the server SQL (0232:265-350, 0233:718-742, 819-850, 1007) and ARMORY.md 381-386 and 542-592; existing tests CheckOutTests.A_mentor_takes_back_a_check_out_and_nothing_is_lost and Force_check_in_of_many_files_is_one_action_and_one_pass cover (b) functionally. N5's own context snapshot is 'the engine did not answer within 3 seconds' (engine thread busy).
- Cause: The server contract requires the lock holder for armory_move_file and refuses folder rename/delete over anyone else's lock, with no role bypass; the app mirrors that. Force check in follows can_take_back, which excludes the instructor role.
- Missing at 0.3.2: (a) entirely: see X-N5a-organize-checked-out. (b) gaps: the 'instructor' member role cannot force a check in (server can_take_back is mentor/cad_lead/admin only); the holder's notice does not say who forced it, says 'took it back' and 'force checked in' for the same thing, and the app offers no way to get the kept copy back; see X-N5b-force-check-in.
- 0.3.3: **In progress**. 

### N6

**No button or action should feel laggy** (note; refs N6, c5dd91d2 (reference only), all 22 slowAction incidents).

- Found at 0.3.2: **Partly done**. Instant acknowledgment in the page: the pressed key turns busy with a spinner, the foot line says 'Checking out Bracket.SLDPRT...' and rows say 'Checking out...' (app.js:2110-2295, act at 2254-2271). 0.3.2 asks the open-file question once per pass (SyncEngine.cs:964 KnowOpen, 1240-1249; OpenFileDetector.OpenAmong 52-78) instead of one Restart Manager session per file. Force check in all is one action (0.3.1). Bulk Check in all/Undo all keys exist (0.3.2). Theme shown from host settings (AgentHost.cs:465-471).
- How it was verified then: Read Bridge.cs, AgentHost.cs, EngineThread.cs, SyncEngine.cs (EnterActionAsync 301-306, LoopPassAsync 267-280, PassLockedAsync 373-415, PassAsync 423-549, PlanAllAsync 958-985, RunUnitsAsync 1118-1134), Checkout.cs, Batches.cs, OpenFileDetector.cs, ReadOnlyPolicy.cs. Field: 0.3.2 DESKTOP-QH30N35 plan phase median 10,443 ms, p90 10,515, max 10,526 over 24 passes, with the log line 'open files: Restart Manager did not answer within 10 s; exclusive-open probe used.' at 2026-10-09T05:51:37Z; c5dd91d2 single check out 15.2 s; armory_file_history recorded 10,586 ms behind a plan. Reproduced in a private worktree (throwaway N56saProbeTests, PortableVaultFileSystem with a 2 s whole-vault open-question cost, 301-file vault, real PostgreSQL 16): idle single check out 2.09 s and check in 2.07 s each with one 301-path open question; launchFile 1.90 s and file detail 1.91 s while the loop planned; 5 concurrent row Check in clicks answered at 3.56, 5.61, 7.65, 9.69, 11.74 s with 5 whole-vault questions.
- Cause: Design: an action is 'take the pass gate, run a pass' rather than 'record the request, finish it in the next pass'; the plan (whole vault) is the expensive phase and is neither scoped for actions nor interruptible for the loop; open-file detection is synchronous on the single engine thread and, on Windows, Restart Manager's halving search exceeds its 10 s budget on a 1,467-file vault every pass.
- Missing at 0.3.2: 1) Every gated action (checkOut, checkIn, undoCheckOut, takeBack, takeBackAll, renameFile, create/rename/deleteFolder, addFiles, dropFiles, takeOverFolder, launch of a file not on disk) waits for the loop pass's phases A and B (the loop only yields in phase C, SyncEngine.cs:1128) and then runs its own pass whose scan and PLAN are whole-vault (SyncEngine.cs:724-727 doc, 958-985). 2) The plan is synchronous on the engine thread and OpenAmong blocks it up to the 10 s budget (OpenFileDetector.cs:71, WindowsVaultFileSystem.cs:129), so launchFile, file detail, notice OK, Pause/Resume and feedback snapshots (3 s deadline, IncidentReporter.cs:18, 70-81) all wait; 5 of 15 feedback notes lost their snapshot this way. 3) Waiting actions never coalesce: N row clicks are N passes. 4) Bulk check out/in write the durable read-only manifest once per file (ReadOnlyPolicy.cs:42-48, 209-237) and check out rehashes every file (Checkout.cs:716-724). 5) Notice OK and Pause have no optimistic page update (app.js:2318-2319, 2927-2931). 6) No test measures any window action on a large synced vault with a realistic open-file cost.
- 0.3.3: **In progress**. 

### N7

**Switching themes is slow and glitchy** (note; refs N7, b500df79).

- Found at 0.3.2: **Partly done**. 0.3.2 applies the theme in the page on click: app.js:2897-2907 sets html[data-theme] synchronously and calls saveSettings; app.js:512-514 (ui.themeWanted) keeps an older view from flipping it back; AgentHost.cs:463-471 WithSettings puts the host's just-saved settings on every view, so a busy engine thread can no longer delay or revert the theme (the 0.3.0 cause). Colors are CSS variables on :root and :root[data-theme='spaceWhite'] (app.css:156, 352); there is no WebView reload or navigation on a theme change. BUT a full Home re-render still follows every switch: Bridge.cs:391-398 awaits host.SaveSettingsAsync, which calls ApplySettingsToEngine and RaiseView (AgentHost.cs:362-363, coalesced FlushView, MainWindow.cs:295-330), then Bridge posts another view directly (Bridge.cs:397 PostView -> MainWindow.cs:368-373), and the engine republishes after ApplySettings (SyncEngine.cs ApplySettings -> RequestPublish). Neither MainWindow nor app.js drops an identical or settings-only view; each 'view' runs buildIndex plus render() (app.js:3127-3147, 509-544) which replaces #main.innerHTML. app.css:589-602 eases color/background/box-shadow over 120 ms on .key/.switch/.pad/.row-main only, while every other surface switches at once.
- How it was verified then: Read code (app.js, AgentHost.cs, Bridge.cs, MainWindow.cs, app.css) and git history (0.3.0 app.js 'theme' case only sent saveSettings and waited for the host view). Ran check-ui.mjs on a private worktree (PASS; its flow asserts the theme is worn before the host answers). Playwright probe (scratchpad/tools/uibulk-probe.mjs, uibulk-probe4.mjs) on demo states: at 1x CPU a theme click is followed by one full render 86 to 114 ms later costing about 100 to 134 ms with 1,401 to 5,000 files; with CDP CPU throttle 4x the full render is a 497 to 538 ms long task and the first painted frame after the click came at 523 to 569 ms (the demo answers with setTimeout 0, so the view lands before the next frame).
- Cause: 0.3.0: the page waited for the host's view and the host took the theme from the engine, whose thread was busy in 57 s and 1,108 s passes (N7 log 18:41:35, 18:42:28-29 'settings saved'), so the switch waited for the pass and flickered. That is fixed. Residual: settings changes go through the same full-view pipeline as everything else, views are never de-duplicated, render() rebuilds the whole DOM, and CSS transitions apply to only some elements.
- Missing at 0.3.2: (1) No settings-only fast path: a theme click causes 2 to 3 full Home re-renders on the real host (Bridge PostView + RaiseView + the engine's republish), each about 0.5 s on a slow lab CPU with 1,400+ files. (2) If a view message lands before the next frame, the new theme is not painted until the full render finishes, so 'instant' is not guaranteed. (3) Mixed-state frames: keys, pads and rows ease for 120 ms while panels switch instantly. (4) Every full render recreates row thumbnails; the host serves them with Cache-Control: no-cache (MainWindow.cs:229), so they blink back to the glyph until reloaded. (5) No check-ui assertion on re-render count or switch latency with a big view.
- 0.3.3: **In progress**. 

### N8

**Show progress and running lines during long operations** (note; refs N8, 58d06041, aa236105, c5dd91d2).

- Found at 0.3.2: **Partly done**. ActivityTracker keeps the last 40 lines from the last 3 minutes (ActivityTracker.cs:31-32, Log at 110, Finish logs 'Uploaded/Downloaded X (size)'), sent in every ActivityView; the engine pushes activity at most 4 times a second from a timer that runs only while a pass or a folder move runs (SyncEngine.cs:1516-1558, StartActivity at 390 and Folders.cs:131). Lines exist for: check out 'Getting N files ready to check out' (Checkout.cs:589), 'Asking the server to check out N files' and 'Checked out X of N files' per 500 (Batches.cs:51,101); check in/undo 'Checked in/Undid X of N files' per 500 (Batches.cs:206) plus a check-in lane shown as the upload direction with a bar and the status line (ActivityTracker.cs:253,275; Checkout.cs:607,617); downloads and uploads per file plus bars (Actions.cs:114,494,532); Force check in 'Force checking in N files' and 'Force checked in X of N' per 16 (Checkout.cs:347,381); 'Sync finished: ...', offline/online (SyncEngine.cs:579-581). The window shows them in Right now under 'What Armory is doing' (app.js:865-867, 909-944), newest at the foot, in a 9.6rem box (app.css:3774).
- How it was verified then: Read code. Ran a throwaway end-to-end test in a private worktree against a private PostgreSQL 16 cluster (tests/Armory.EndToEnd.Tests/UiBulkProbeTests.cs in scratchpad/wt-uibulk, School latency, 160 files, 3 s idle before each action): Check out with the 0233 stand-in got live events at +268 ms 'Asking the server to check out 160 files' and +767 ms 'Checked out 160 of 160 files', activity.Line null throughout and the status line 'Checking for changes.'; Force check in of 160 files got ONE activity event at +1,085 ms, after all 160 armory_break_lock calls and after the action answered (1,051 ms), carrying all 11 Force lines at once. Playwright probe: on 'transferring' at 1280x800 the Right now panel is at y 201 to 707 at open and at y -629 to -123 after scrolling the column 1,500 px. Incident timelines: aa236105 (0.3.1, Check out all of 1,424 files, 133 s) and c5dd91d2 (0.3.2, plan phase 10.5 s on every pass with 1,467 files; snapshot activity null).
- Cause: Running lines were added as log entries on top of a transfer-oriented tracker: only uploads, downloads, moves and check-in releases have lanes, and the publishing timer is tied to passes and folder moves, not to actions that do their work before their pass.
- Missing at 0.3.2: (1) Force check in lines are not live: TakeBackAsync(list) logs outside a pass and never starts the activity timer, so nothing reaches the window until the closing pass. (2) Check out and Undo have no progress lane: no bar, no 'X of N' count and the big status line says 'Checking for changes.' (SyncEngine.View.cs:43) for the whole check out. (3) Silent stretches before the first line: waiting for the loop pass, scan, server read and plan (10.5 s per pass on DESKTOP-QH30N35 0.3.2) and per-file hashing in FinishCheckOutAsync (Checkout.cs:721) emit no line. (4) No lines at all for folder rename/move (only a lane), Delete folder, Add files' copy phase (Folders.cs:1250-1290), scans or server reads. (5) The panel lives at the top of the scrolling column, so it scrolls out of sight while the student works in a long folder (the left status shows only lane lines). (6) patchActivity rebuilds the list and jumps to the end on every change (app.js:972-977), so older lines cannot be read while lines arrive. (7) 'Sync finished: N files downloaded.' is logged after a cut-short loop pass that continues at once (SyncEngine.cs:579; see N2). (8) No demo state shows check-out running lines (demo/states.js only has transfer lines in TRANSFERS) and check-ui never asserts #act-log.
- 0.3.3: **In progress**. 

### N9

**Bulk keys were under the 1,400-file list; the UI must handle thousands of files without confusion** (note; refs N9, 6d8e6ddc).

- Found at 0.3.2: **Partly done**. The reported problem is fixed: My files has Check in all and Undo all above its list when it has more than one file (app.js:1243-1252) and a list over 6 rows scrolls in its own box (app.js:1253-1276, app.css:3583-3590, max-height min(352px, 44vh)), so Team files and its keys sit right under it; the open folder's crumbs and keys are sticky while no file is picked (app.css:3571-3576, narrow offsets 4041-4054) and the selection bar is sticky while files are picked (app.css:3597-3600). Long lists are windowed (app.js:2349-2536; My files, notice lists, the browser), notice lists are capped at 280 px (app.css:3826-3832) and 200 items with 'Showing X of N' (SyncEngine.View.cs NoticeItemsShown, app.js:1058). Check out all asks first with the right count (app.js:1837-1845, 2016-2048); Undo all asks first (app.js:1846-1852).
- How it was verified then: Read app.js, index.html, app.css. Ran check-ui.mjs (PASS: 184 pages, 44 flows). Playwright probes on demo states: manyMine at 1280x800 has Check in all/Undo all at y 237 to 281 (in view at open) and Team files' label at y 702; at 420x720 the keys are at y 336 to 380. bigProject: after scrolling 3,000 px the folder keys are at y 238 (1280) and y 146 (420), position sticky. Shift-select of a whole 5,000-file folder works ('5,000 selected', about 100 to 170 ms per click). Pending-label probes (uibulk-probe2.mjs, uibulk-probe3.mjs) and keyboard probe (uibulk-probe5.mjs) listed under the X items.
- Cause: 0.3.1: My files listed every check out as a page-height row (no own box, no keys) above Team files, whose keys were at the top of a list that scrolled away. Fixed in 0.3.2. The remaining items come from the window treating a folder path as 'every file under it' when it reports work in progress, from the sticky head not being taken into account by keyboard scrolling, and from render() rebuilding everything on every view.
- Missing at 0.3.2: Concrete remaining problems with thousands of files (each detailed as an X item): (a) the working line and the row labels during a folder-wide or 'all my files' action count and mark every file under the folder: My files Check in all says 'Checking in 5,018 files...' when 1,401 are mine, folder Check in all says 'Checking in 5,000 files...' with one file mine, Check out all's dialog says 6 files but the foot says 'Checking out 8 files...', and other people's rows read 'Checking in...' with their holder hidden (X-pending-count); (b) arrow keys move focus onto rows hidden under the pinned folder keys (X-sticky-focus); (c) every view re-renders the whole Home, about 0.5 s on a slow CPU with 1,400 to 5,000 files, up to twice a second during passes, and thumbnails blink (X-full-render); (d) Right now scrolls away (N8); (e) rows/sync disagree: 38 files 'uploading' while the status says 'Everything is saved to Armory.' in the N11/N12 snapshots (X-uploading-vs-saved); (f) no Select all; (g) two keys both labeled 'Check in all' with different scopes on one screen (My files vs the folder); (h) a long action's final answer fades after 8 s (app.js:2302-2304) and is kept nowhere; (i) Force check in all names every holder in one sentence (app.js:2089-2090), unbounded with many students; (j) no CI runs the UI checks.
- 0.3.3: **In progress**. 

### N10

**Several students in one Armory folder on one computer, and a quick account switch** (note; refs N10, 2d479aea, 87fa2b3d, 428a0f6f, 968cc0cd).

- Found at 0.3.2: **Partly done**. 0.3.2 added two pieces. (1) Switch account, under This computer (app.js:812, app.js:2911-2913, Bridge.cs:157-159, AgentHost.cs:251-257): SignOut() forgets the saved session (AgentHost.cs:244-249), then ConnectAsync() runs the full browser Google sign-in (AgentHost.cs:274-321). No account is remembered. (2) Use this folder (folderTakenHtml app.js:637-668, action app.js:2908-2910, Bridge.cs:160-163, AgentHost.cs:259, SyncEngine.Accounts.cs:16-57). The folder's owner is one email, state.Email, in <vault>\.armory\state.json (AgentHost.cs:629). When the signed-in email differs, BuildView answers vaultOwnedByOther (SyncEngine.View.cs:30-33) and PassAsync returns before any scan, download, upload or read-only work (SyncEngine.cs:432). Use this folder scans the disk and counts what the owner still has waiting (WaitingIn, SyncEngine.Accounts.cs:61-90: files checked out here, saves not sent, files changed and not saved, new files not in Armory, folder changes not sent). Anything waiting refuses with '<Name> still has <what> in this folder. <First> can sign in to Armory here to finish them, or you can use a folder of your own.' Nothing waiting clears owner, device, former devices, notices, imports and kept-copy markers (SyncEngine.Accounts.cs:37-51); the next pass binds the folder to the new account and downloads nothing again (AccountTests.cs:60-68). The page's other keys are 'Use C:\IDEA\Armory-<first name>' (a second folder: SaveSettings restarts the runtime there, full download) and Choose another folder (app.js:658-660). Settings are per Windows user (%LOCALAPPDATA%\IDEA Armory\settings.json, AgentSettings.cs:27-34) and default to C:\IDEA\Armory for every Windows user; owner.lock and read-only.lock are opened FileShare.None (SafeFileReplace.cs:35, ReadOnlyPolicy.cs:33), so only one Armory process can run a folder. In 0.3.1 (git show v0.3.1:app.js:625-651) the page offered only the second folder and Choose another folder: exactly the N10 complaint.
- How it was verified then: Read code (engine, host, bridge, page, settings, lock files). Compared the 0.3.1 page with git show v0.3.1. Read the guarded Postgres tests tests/Armory.EndToEnd.Tests/AccountTests.cs:24-106 (not run: no PostgreSQL cluster reachable from the sandbox; the local cluster is down and the scratchpad is not readable by the postgres user). Read the N10 context: IDEA-00, vault C:\IDEA\Armory, checkedOutHere 1423, files 1471; its log tail has no account switch (only restarts at 18:20:58Z and 18:29:49Z, both 'session loaded for apina@boscotech.edu').
- Cause: The folder belongs to exactly one account (state.Email in .armory\state.json) and the engine has no notion of a second account's work in the same folder: a different signed-in email makes PassAsync return at once (SyncEngine.cs:432), and the only transition is a whole-folder hand-over that requires the owner to have nothing waiting (SyncEngine.Accounts.cs:32-33). SessionManager holds one session, so a switch can never be quick.
- Missing at 0.3.2: (a) Shared use while the first student has anything waiting. On IDEA-00 itself (N10 snapshot: 1,423 files checked out here by apina) Use this folder is refused ('Apina still has 1,423 files checked out in this folder...'), so a student is still sent to a second folder and a second full download, which is N10's complaint. (b) Remembered profiles: one session per Windows user, so every switch is a full browser sign-in, and every connect registers a new device row. (c) A PIN (or Windows Hello) to protect a remembered profile on a shared Windows login. (d) A picker ('Who is using Armory?') listing remembered accounts and what each has waiting. (e) Hand-over with waiting work: parking the first student's check outs and unsent saves (read-only, kept, sent under their own name when they return) instead of refusing. (f) The folder owner's name is never shown in production (see X-owner-name). (g) Nothing protects the parked owner's writable files (see X-parked-writable). (h) On the folder-taken screen Send feedback and Settings are unreachable (header key only when signedIn, app.js:554; openSettings returns unless signedIn, app.js:1769-1770). (i) Separate Windows users: a second Windows user whose Armory starts while the first user's Armory still runs (fast user switching) gets runtimeProblem 'Armory can't use the folder C:\IDEA\Armory. Choose another folder in Settings.' (AgentHost.cs:427-432) with no word that another Windows user holds it. (j) Switching does not ask the student to save and close SolidWorks files first.
- 0.3.3: **In progress**. 

### N11

**After checking everything out, only a file list and no bulk keys** (note; refs N11, ed7dc8f8).

- Found at 0.3.2: **Done**. My files: Check in all and Undo all above the list when there is more than one file (app.js:1243-1252), list in its own scroll box over 6 rows (app.js:1253-1271, app.css:3583-3590); the selection bar (app.js:1504-1526) and the pinned folder keys (app.css:3571) also offer bulk check in.
- How it was verified then: Playwright probe on demo state manyMine (1,401 files mine): keys in view at open at both window sizes (y 237 to 281 at 1280x800, y 336 to 380 at 420x720), My files box 370 px, Team files label at y 702. check-ui flow (check-ui.mjs:1348-1360) asserts the box, that Check in all sends checkIn of the project folder and Undo all asks first; check-ui PASS on HEAD. Engine side: CheckInAsync takes only this computer's check outs under the paths (Checkout.cs:181-196, MyCheckOuts).
- Cause: 0.3.1 listed 1,400 rows in My files before any key; fixed in 0.3.2 (commit 6a9779f).
- 0.3.3: **In progress**. 

### N12

**No way to bulk check in** (note; refs N12, 809ae174).

- Found at 0.3.2: **Done**. Three ways now: My files Check in all (app.js:1249, 2845-2847, sends every project folder with a file of mine, mineFolders at 1280-1286), the folder's Check in all (app.js:1427, 2841-2844, pinned at the top of a long folder by app.css:3571) and the selection bar's Check in (app.js:1520, 2791-2800). The engine checks in only this computer's check outs under the paths, in batches of 500 (Checkout.cs:181-196, Batches.cs:157-240).
- How it was verified then: Read code; Playwright probe (keys in view at open in manyMine); check-ui PASS; end-to-end test Check_out_check_in_and_undo_of_many_files_each_take_one_batch_and_one_pass passed on a private PostgreSQL cluster.
- Cause: 0.3.1: the only bulk check in keys were below 1,400 My files rows; fixed in 0.3.2.
- 0.3.3: **In progress**. 

### N13

**Send feedback said it could not send (feedback that had in fact been sent)** (note; refs N13, N14, N15, N5, 6be92c08, aa236105, 68bee25b, bcb16c24, 012e24c7, 2ab514eb, c5dd91d2).

- Found at 0.3.2: **Done**. IncidentUploader.SendFeedbackNowAsync (src/Armory.Client/IncidentUploader.cs:104-119) now checks feedbackSent first (line 112): a note the background round already sent answers Sent. SendAsync adds the file to feedbackSent right after the RPC answers (IncidentUploader.cs:175-178), before Rewrite and Mark(.sent) (179-186). AgentTelemetry maps Sent to 'Sent. Thank you for the feedback.' (AgentTelemetry.cs:150). Guarded test tests/Armory.Client.Tests/V3ClientTests.cs:417-439 A_note_the_background_round_already_sent_is_sent_not_held.
- How it was verified then: Logs and flights of the 77 incidents and the 15 notes (script n1015-grep.py): every armory_submit_app_feedback RPC answered 200 (11 calls, 98 to 287 ms); three sends answered Held right after a successful RPC (IDEA-06 0.3.0 17:52:46Z = N15, IDEA-00 0.3.1 18:24:58Z local = N14, IDEA-06 0.3.1 19:52:23Z = N5), each with 'could not be read (FileNotFoundException); it is kept as held'. Reproduced in throwaway worktrees (removed afterward) with a live race test (RunAsync running, note saved, Wake, SendFeedbackNowAsync, 60 rounds): v0.3.1 code answered Held 42 times and Sent 18 times with exactly 60 feedback RPCs (every note was sent once); HEAD 8d90d63 answered Sent 60 of 60, one RPC each, no 'held' line. Also ran the guarded A_note_the_background_round_already_sent_is_sent_not_held and A_note_on_its_own_sends_its_words_and_no_incident: pass.
- Cause: A race inside the app, not the server. Saving the note fires Reporter.Saved, which wakes the background uploader (AgentTelemetry.cs:44, IncidentReporter.cs:157). When the uploader was idle (no transfer running and its one-a-minute slot free), its round took the gate first, sent the note-only file and renamed it .sent (IncidentUploader.cs:182-186). SendFeedbackNowAsync then read the old path, got FileNotFoundException, tried to mark it held and returned Held (0.3.1 lines equivalent to 148-163), so the window said 'Armory couldn't send that feedback. It is kept in the incidents folder.' The first note after a quiet minute hit it; later ones in the same minute won the gate (TooSoon) and said Sent. Timeline on IDEA-00: N14 at 18:24:58Z local said Held; 46 s later Mr. Pina wrote N13 ('i have not been able to send any feedback'), which itself said Sent. The 0.3.2 fix addresses this exact cause.
- 0.3.3: **In progress**. 

### N14

**Send feedback should match the website's (0235 contract)** (note; refs N14).

- Found at 0.3.2: **Not done**. 0.3.0's Send feedback only: header key when signed in (app.js:554-557) and Settings (app.js:1761); a dialog with Bug, Idea, Other (app.js:1898-1911) and one textarea up to 8000 characters (app.js:1913-1918); askOk sends sendFeedback {kind, body} (app.js:1968-1977, bridge.js:339); Bridge.cs:276-279 -> AgentHost.SendFeedbackAsync (AgentHost.cs:170-182) -> AgentTelemetry.SendFeedbackAsync (AgentTelemetry.cs:128-160; unknown kinds become other, ReportKinds at AgentTelemetry.cs:17) -> note saved in the incidents folder -> IncidentUploader -> ArmoryApi.SubmitAppFeedbackAsync, the five-argument form only (ArmoryApi.cs:123-127): p_kind, p_body, p_app_version, p_device_name, p_context {incidentId, osVersion, snapshot, last 200 log lines} capped at 96 KiB then 24 KiB (IncidentUploader.cs:46-47, 291-311). PGRST202 waits 6 h (IncidentUploader.cs:198-203), PT429 waits retry_after_seconds (204-211), 22023 too_large/too_long is shortened and sent once (238-252), any other 22023 holds the note for good (216-227).
- How it was verified then: Read code (ArmoryApi.cs, IncidentUploader.cs, AgentTelemetry.cs, Bridge.cs, bridge.js, app.js); grep for p_tried, p_area, p_screenshot, praise, armory_my_app_feedback, storage/v1, armory-feedback-shots across src, tests and docs: the only hit is a 0233 stand-in test that expects praise to be refused (V3StandInTests.cs:58). Read the contract (ARMORY.md 'The v0.3.2 server contract (migration 0235)', item 5) and the migration (0235_armory_app_feedback_v2.sql:59-90, 112-201, 260-293), and the website's form (src/lib/feedback/FeedbackBox.svelte, feedback.ts:27-32, 81, screenshot.ts:44).
- Cause: Not built: 0235 was written for 0.3.2 but the 0.3.2 client still calls only 0233's five-argument form; nothing in Armory.Client, Armory.Agent or the page calls the eight-argument form, armory_my_app_feedback or Supabase Storage.
- Missing at 0.3.2: Field by field against 0235: p_kind praise not offered (app kinds bug/idea/other; AgentTelemetry would turn praise into other). p_tried (What did you try?, up to 1000): no field, not in the note record, not in the API. p_area (window or view name, up to 120): not sent; the page knows its screen and project/folder. p_screenshot: no capture, no Storage client, no upload to armory-feedback-shots at <auth uid>/<uuid>.png (PNG, at most 2 MiB); the session has SupabaseUrl, AnonKey and AccessToken (Session.cs:8-10) but no user id (it is the JWT sub). Refusals 22023 bad_path / not_found / in_use and too_long for tried/area: not handled; today any of them would hold the whole note (IncidentUploader.cs:216-227) instead of dropping or redoing the screenshot. Eight-to-five fallback on PGRST202: absent (only the six-hour wait for the five-argument form). armory_my_app_feedback 'Your feedback' list with status new/seen/resolved/closed: absent, and must hide on PGRST202. Website extras with no 0235 field: horizon (now / long term), dictation (Ctrl+Shift+Space with a level meter; in WebView2 Windows voice typing Win+H is the practical route), Ctrl+Enter to send, paste or drop a picture, Send another / Done. Privacy for a screenshot: File detail shows another person's address (app.js:1681 who-email), which 0235 forbids in a screenshot. Send feedback is unreachable on the folder-taken screen.
- 0.3.3: **In progress**. 

### N15

**A thumbnail of each part** (note; refs N15).

- Found at 0.3.2: **Partly done**. ShellThumbnails (src/Armory.Agent/ShellThumbnails.cs): SHCreateItemFromParsingName(plain path) -> IShellItemImageFactory.GetImage(192x192, SIIGBF_THUMBNAILONLY / SIIGBF_BIGGERSIZEOK) (lines 75-88), so Windows answers from its thumbnail cache or the registered thumbnail handler (IThumbnailProvider behind the shell), never the type icon; HBITMAP read with GetDIBits as 32-bit BGRA, premultiplied alpha undone, PNG written in-process (93-151). One background STA thread (28-30) takes requests one at a time from an unbounded BlockingCollection (18, 54-72); results, null included, are cached by (path upper-cased, length, last write) for the last 600 (17, 21-22, 64-69). The page asks only inside https://armory.local for .sldprt/.sldasm/.slddrw and pictures (app.js:1147-1148), never for status notOnThisComputer (app.js:1153), with loading=lazy for rows and eager for File detail (app.js:1162-1164, 1641-1642); the URL carries updatedAt so a new version asks again (app.js:1152-1158). MainWindow serves /thumb/ with a deferral, 404 when there is no picture (MainWindow.cs:121-126, 215-241); AgentHost resolves only existing files inside the vault, never .armory (AgentHost.cs:135-138, WindowsVaultFileSystem.cs:398-406). The glyph stays until a picture loads; an error removes the img (app.js:2995-3011). Tests: ShellThumbnailsTests (a BMP gets a PNG, a text file and a missing file get none; PNG encoder bytes and CRCs).
- How it was verified then: Read code and tests. Not run on Windows here. Never checked with real SolidWorks files: the only Windows test uses a generated BMP (ShellThumbnailsTests.cs:9-31), and AGENTS.md forbids committing team CAD files.
- Cause: Not a bug report: the feature exists but is unverified. Design risks: one serial STA thread with no timeout, no logging, and negative answers cached by path, size and time.
- Missing at 0.3.2: A lab check with real SolidWorks files (it was never done). Robustness: (1) No timeout: one hung handler stops every later picture and leaves WebView2 deferrals open forever (ShellThumbnails.cs:54-72). (2) The queue is unbounded and never drops requests for rows already scrolled away. (3) It is constructed without a log (AgentHost.cs:138 'new()'), so handler failures never reach agent.log or incidents. (4) A null answer is cached for that (path, size, time), so a file that was locked by SolidWorks keeps its glyph until it changes. (5) The cache key is taken after Make (line 64), so a picture made from old bytes can be stored under the new key. (6) Crash risk: if SolidWorks' handler opts out of process isolation it runs inside IdeaArmory.exe, and an access violation there ends Armory (not catchable in .NET). (7) No picture on a computer without SolidWorks' handler (eDrawings only or nothing), and unknown for 2026-saved files on a 2025 computer. (8) Long paths over 260 characters through the shell API are untested.
- 0.3.3: **In progress**. 

### I-checkIn-IDEA-00-0.3.1

**Folder check in of 1,424 files waited 14.8 s** (incident-group; refs c2d5d46c).

- Found at 0.3.2: **Partly done**. Releases go 500 per armory_release_locks call (Batches.cs:141-216); running lines 'Checked in 500 of 1,424 files'.
- How it was verified then: Flight: no wait (loop pass had just ended at 19:43:37.7); action pass plan 7,502 ms; then 6.1 s from move (19:43:47.288) to the first armory_release_locks (19:43:53.437): PrepareRelease's per-file SetAttribute (Checkout.cs:675) for 1,424 files; three release_locks 340-396 ms; finish 6,891 ms. Read current code.
- Cause: Per-file ReadOnlyPolicy.Apply persists the whole manifest with WriteThrough each time; whole-vault plan per action.
- Missing at 0.3.2: 0.3.2 keeps the per-file durable manifest write in PrepareRelease and the whole plan per action; estimated 18-29 s on DESKTOP-QH30N35 (10.5 s plan, possible loop wait, about 6 s attributes).
- 0.3.3: **In progress**. 

### I-checkIn-IDEA-06-0.3.1

**Check in row clicks queued: 15 and 16 clicks answered one pass apart (up to 658 s)** (incident-group; refs bcb16c24, ccf970d7, 428a0f6f, ee443a83).

- Found at 0.3.2: **Not done**. 0.3.2 adds Check in all / Undo all at the top of My files and the selection bar Check in, which send one action for many files (app.js:2795-2799). Row keys still send one checkIn each (app.js:1211-1212, 1296-1297).
- How it was verified then: Logs: 'window: check in done' every 34.6-35.3 s from 19:48:24 to 19:56:33 (15) and every 38-40 s from 21:02:19 to 21:12:49 (16), each after its own 'action' pass (plan 29-33 s). N5 was written at 19:52:22 while this queue ran. ee443a83: single check in, 26 s wait plus a 45 s action pass. Reproduced on HEAD: 5 concurrent single-file check ins with a 2 s whole-vault open question answered at 3.56, 5.61, 7.65, 9.69, 11.74 s, with 5 whole-vault questions.
- Cause: Action model 'gate then pass' per click; check-in requests are durable flags that one pass could finish together.
- Missing at 0.3.2: No coalescing of waiting actions: each click enters the pass gate alone (SyncEngine.cs:301-306) and runs its own pass (Checkout.cs:194); phase D already finishes every flagged request in state, but each action waits for its own pass.
- 0.3.3: **In progress**. 

### I-checkOut-IDEA-00-0.3.1

**Folder check out of 1,424 files waited 124-133 s** (incident-group; refs aa236105, 68bee25b).

- Found at 0.3.2: **Partly done**. 0.3.2: the per-file open check in FinishRequestsAsync and CheckOutAnswer is one OpenAmong each (Checkout.cs:594, 130); locks go 500 per armory_lock_files call (Batches.cs:36-105); running lines 'Getting 1,424 files ready to check out', 'Checked out 500 of 1,424 files' (Checkout.cs:589, Batches.cs:51, 101).
- How it was verified then: Flight aa236105: waited 33 s for the loop pass's plan (ended 18:23:30.5); action pass: plan 41,284 ms, then 44 s before the first armory_lock_files (per-file IsOpenNow plus rehash), lock_files at 18:24:56.1, 18:24:58.7, 18:25:01.2 (about 2.5 s apart = 500 per-file SetAttribute durable manifest writes each), finish 50,746 ms, pass 92,400 ms, then 7.6 s more before the answer (per-file open check in CheckOutAnswer). 68bee25b: same shape (plan 42,340 ms, finish 51,177 ms, lock_files 18:37:12.2/14.7/17.2, 8.3 s after passEnd). Snapshot checkedOutHere 1424. Read current code.
- Cause: Whole-vault work per action; per-file durable attribute writes; rehash of every target; open checks bounded only by a 10 s synchronous budget.
- Missing at 0.3.2: In 0.3.2 the same click still costs: wait for the loop plan (up to about 10.5 s), its own whole plan (about 10.5 s), a 1,424-file OpenAmong in FinishRequestsAsync (up to the 10 s budget), a full rehash of all 1,424 files (Checkout.cs:716-724), 1,424 durable manifest rewrites (Batches.cs:95 -> Actions.cs:1004-1019 -> ReadOnlyPolicy.cs:42-48, 209-237, measured about 5 ms each in these logs) and another 1,424-file OpenAmong in CheckOutAnswer (Checkout.cs:130). Estimated 30-50 s on that PC.
- 0.3.3: **In progress**. 

### I-checkOut-IDEA-06-0.3.1

**Check out waited 44-72 s (one file, and 16 files once)** (incident-group; refs f4ea534f, ea2b2a2c, 968cc0cd, 6d145628, 12ae9081).

- Found at 0.3.2: **Partly done**. Same path as N6. 0.3.2 batches the plan's open question and the ~$ marker question (SyncEngine.cs:1272).
- How it was verified then: Flights: each waited for the running loop pass's plan (16-23 s), then an action pass: scan 4.96-5.97 s (about 200 'Armory is treating X as closed' stale ~$ marker notices per scan, each marker asked per file in 0.3.1), plan 29.3-37.8 s, finish 0.18-0.85 s (armory_acquire_lock or armory_lock_files about 100 ms). 12ae9081 also downloaded one file and kept one copy before locking. 6d145628 was refused after 43.7 s (reason not in the data). Every loop pass refused 142 files (another audit's topic). 7 of these IDEA-06 incidents have no snapshot (engine busy). c5dd91d2 on 0.3.2 (reference only) shows the remaining shape: 3.8 s wait plus an 11.2 s action pass with a 10.5 s plan.
- Cause: As N6: actions run whole-vault plans and wait behind the loop's; open checks are synchronous and slow on Windows.
- Missing at 0.3.2: Per click in 0.3.2: up to one loop plan of wait plus a whole-vault plan of its own, about 11-22 s on a 1,500-file vault where Restart Manager hits its budget.
- 0.3.3: **In progress**. 

### I-crash-DESKTOP-F41DB2R-0.3.1

**DESKTOP-F41DB2R 0.3.1 two crash reports: killed right after 'quitting'** (incident-group; refs 62f377c1, e5e6e9f3).

- Found at 0.3.2: **Partly done**. Per-path cancellation in planning (SyncEngine.cs:970).
- How it was verified then: 62f377c1: pass ended 22:00:32.619, next pass due 22:00:42 with plans of 28-30 s on this computer; 'quitting' 22:01:06.398 (inside that plan); no 15 s warning, no 'stopped'; next start 22:07:31. e5e6e9f3: pass ended 00:12:11.163; 'quitting' 00:12:18.490 (in the 10 s idle wait or in a pass a file hint started early); no 'stopped'; next start 00:13:28 (70 s, like a restart). Last flights end 21:59:53 and 00:11:29 (written once a minute). On IDEA-00 a quit during the idle wait took 4.1 s (18:29:25 -> 18:29:29 'stopped'), so e5e6e9f3 shows even a short quit is cut off at session end.
- Cause: Windows ended the session (restart or sign-out) and terminated the process before the asynchronous Quit finished; not an Armory crash.
- Missing at 0.3.2: Same as C-0.3.2-quick-quit: the session-end path is unchanged at HEAD.
- 0.3.3: **In progress**. 

### I-crash-IDEA-00-0.2.1

**IDEA-00 0.2.1 'previous run ended unexpectedly' after quitting during a 39 s plan** (incident-group; refs 7aa938ba).

- Found at 0.3.2: **Partly done**. 0.3.2 checks cancellation per path in planning (SyncEngine.cs:970).
- How it was verified then: Log: pass ended 14:41:06.414; next loop pass due ~14:41:16; 'quitting' 14:41:47.553; no 'the sync engine did not stop within 15 seconds' (due 14:42:02.5, AgentHost.cs:454) and no 'stopped'; next start 14:42:39. Last flight (written 14:41:06.380, once a minute by LastFlight.cs:13) shows plans of 28.4 s and 39.1 s. No managed crash incident (CrashNow) exists for that run.
- Cause: Not a crash: the process was terminated within 15 s of 'quitting' while the engine thread was inside a plan (0.2.1 had no cancellation there). The kill within seconds and the restart 52 s later match Windows ending the session (restart or sign-out): TrayApp only posts Quit on SessionEnding and nothing holds WM_ENDSESSION.
- Missing at 0.3.2: Session end still kills the app (see C-0.3.2-quick-quit), and the incident still calls it a crash.
- 0.3.3: **In progress**. 

### I-crash-IDEA-06-0.3.0

**Crash 2ab514eb: the app was ended during a ~60 s pass after 'quitting'** (incident-group; refs 2ab514eb, 030b06da).

- Found at 0.3.2: **Partly done**. Log: 18:55:50.016Z pass ended (142 refused); 18:56:10.730Z quitting; no 'stopped', and no 'the sync engine did not stop within 15 seconds' (AgentHost.cs:454), so the process was killed within 15 s (Windows sign-out or shutdown, or a forced close). In 0.3.0 the plan loop had no cancellation check and spent ~54 s in per-file open checks; HEAD checks ct per path (SyncEngine.cs:970) and OpenAmong has a 10 s budget.
- How it was verified then: Read the log tail in 2ab514eb/030b06da; read AgentHost stop path; diffed v0.3.0..HEAD.
- Cause: Slow, uncancellable plan phase (see X-pass-48-60s), not a code crash.
- Missing at 0.3.2: Stop latency is still bounded by OpenAmong (up to 10 s) and a pass in progress.
- 0.3.3: **In progress**. 

### I-crash-IDEA-06-0.3.0-slowpa

**IDEA-06 0.3.0 crash report: killed 0-15 s after 'quitting' during a stale-marker scan or plan (sign-out)** (incident-group; refs 2ab514eb, 184c9e24).

- Found at 0.3.2: **Partly done**. 0.3.2 batches marker checks (SyncEngine.cs:1275) and checks cancellation per planned path (:970).
- How it was verified then: Log: pass ended 18:55:50.016 (62.2 s, 142 refused); 'quitting' 18:56:10.730, about 10 s into the next pass (scan 8.2 s of stale-marker RM checks, then a 53 s plan); no 15 s stop warning, no 'stopped'; next start 19:25:01 (29 min later, the same student). Last flight written 18:55:50.056.
- Cause: Windows ended the session (most likely Abraham signing out) and terminated Armory while its quit waited behind uninterruptible open-file checks.
- Missing at 0.3.2: Session end still kills the process; at HEAD the engine thread can still be blocked up to ~10 s in each OpenAmong (markers, plan) and cannot see the stop until it returns.
- 0.3.3: **In progress**. 

### I-crash-IDEA-06-0.3.1

**IDEA-06 (Seraj) crash report about a 0.2.0 run with no evidence** (incident-group; refs 87fa2b3d).

- Found at 0.3.2: **Not done**. 0.2.1+ logs pass lines and keeps a last flight; 0.2.0 did neither.
- How it was verified then: Trigger lastLogLine '19:06:43.587Z vault runtime started' (0.2.0), lastFlightWrittenAt null, 0 flight events; reported by the first 0.3.1 run at 20:52:02 (just installed). Log also shows 8 starts of 0.2.0 between 20:53 and 20:59 on 10-07 with no 'quitting' or 'stopped'.
- Cause: Unknown (0.2.0, ended between 19:06:43 and 20:52:02 without 'quitting').
- Missing at 0.3.2: Cause unknowable from the incident: it carries no crash.log tail, and quiet 0.2.0 passes wrote nothing. A hard kill (power, forced restart, Task Manager, or the 0.3.1 installer's Stop-Process after --quit, Setup.ps1:283-300) cannot be told apart from a native crash.
- 0.3.3: **In progress**. 

### I-launchFile-IDEA-06-0.3.1

**Open (launchFile) waited 13-31 s** (incident-group; refs d5c6825d, 2d479aea, 1400e8b8, 4a444b9a).

- Found at 0.3.2: **Partly done**. LaunchAsync takes no pass gate and launches off the engine thread (Checkout.cs:406-419), but it is posted to the engine thread (EngineThread.cs:64-70). 0.3.2 shortened the plan from about 30-42 s to about 0.5-10.5 s.
- How it was verified then: Each answer came 30-100 ms after the loop pass's plan phase ended: plan end 19:32:05.445 / answer 19:32:05.548; 20:56:07.965 / 20:56:08.039; 21:29:53.691 / 21:29:53.721; 22:28:24.374 / 22:28:24.439 (plans 29-42 s). In 6d145628 a launch answered 39 ms after a scan phase ended. Reproduced on HEAD: with a 2 s whole-vault open question, launchFile took 1.90-1.91 s in 3 of 3 rounds while the loop planned.
- Cause: Single engine thread runs the plan synchronously; LaunchAsync needs that thread only to resolve the path and check Exists.
- Missing at 0.3.2: The engine thread is still blocked for the whole plan (synchronous loop over AllPaths with no await; OpenAmong waits synchronously up to 10 s), so Open still waits up to about 10.5 s on DESKTOP-QH30N35, and file detail, notice OK and Pause likewise.
- 0.3.3: **In progress**. 

### I-readOnlyBroken-IDEA-06-0.3.0

**readOnlyBroken on Hook V3.SLDPRT right after a release** (incident-group; refs 6be92c08).

- Found at 0.3.2: **Not done**. Same ApplyReadOnly rule. Hook V3 was among the 53 files checked out at 17:48 (610eed69 snapshot), a release_lock ran at 17:52:04, the 17:52:08 scan could not read Hook V3 ('being used by another process'), and readOnlyBroken fired at 17:52:53.
- How it was verified then: Abraham's flight and log around 17:51-17:53; no later kept copy or download for Hook V3 in any incident.
- Cause: Stale cached flag for a file unreadable in one scan (SolidWorks or another program held it just after the release).
- Missing at 0.3.2: Same as the 0.3.1 group; no evidence that Hook V3 bytes were changed or lost.
- 0.3.3: **In progress**. 

### I-readOnlyBroken-IDEA-06-0.3.1

**readOnlyBroken on Toparmredesign, Toparmredesignnoscrewpocket and SmallFlywheel V3 (12 incidents)** (incident-group; refs 1c1b1c5b, 012e24c7, a6f9e941, 7be63dcf, 40a07aa9, e0722b64, 88fc8771, 3aa1ef68, 7352f99d, 25d938f6, e07af0ab, 2cf39a10).

- Found at 0.3.2: **Not done**. ApplyReadOnly (SyncEngine.Actions.cs:955-999) reports ReadOnlyBroken when AppliedOwnership==desired and the scan's ReadOnly flag is false (980). For these files the flag came from the detector cache (last read while checked out and writable), not from disk. The bit was in fact set (Armory set it via a FILE_READ_ATTRIBUTES handle, NativeMethods.ExistingFileId, which a SolidWorks write handle does not block).
- How it was verified then: Per-pass table from Abraham's merged flight: every readOnlyBroken path was held by SolidWorks in the same pass, each run starts in the pass after a release_lock while held and ends when the file becomes readable (Toparmredesign 21:12:09-21:29:54, Toparmredesignnoscrewpocket 21:34:37-23:10:49 (102 events), SmallFlywheel V3 22:32:10-23:04:50 (26 events)). Reproduced by the second repro test. SolidWorks temp-file-plus-rename saving is not supported by the data: the files stayed continuously 'being used' and no fresh read ever showed the bit cleared.
- Cause: Stale cached ReadOnly flag for an unreadable file (X-stale-unread-entry), present only because the lock had been released while the file was held (X-checkin-release-guard).
- Missing at 0.3.2: These are false alarms about the bit, but they are a reliable signature of the N4 release. After the fix they should stop; a real cleared bit must still be reported.
- 0.3.3: **In progress**. 

### I-slowAction-DESKTOP-QH30N35-0.3.2

**slowAction c5dd91d2: Check out of 1 file took 15.2 s on an idle computer** (incident-group; refs c5dd91d2,N1,N3).

- Found at 0.3.2: **Not done**. windowAction checkOut ms 15,197 ending 05:55:56.536 (started about 05:55:41.34). The loop pass that began 05:55:33.523 was in its plan phase (10,515 ms, engine thread blocked) so the action could not register; that pass ended 05:55:45.184; the action's own pass (05:55:45.214 to 05:55:56.372) spent 10,526 ms in plan asking about all 1,467 files for a one-file action, then armory_acquire_lock 145 ms. In the same window armory_file_history took 10,586 ms (file detail waits for the engine thread). After the download, every idle pass took 11-12 s (plan 10.5 s).
- How it was verified then: Flight recorder of c5dd91d2 with scratchpad/tools/n123-passes.py; read EnterActionAsync (SyncEngine.cs:301-306), PassLockedAsync with a scope (phase B is whole, SyncEngine.cs:373-415, 958-985), KnowOpen, GetFileDetailAsync.
- Cause: X-open-files-question: the synchronous 10 s Restart Manager budget on the engine thread, paid once by the loop pass the action waits for and again by the action's own pass.
- Missing at 0.3.2: An action that does not wait behind or repeat the all-files open question.
- 0.3.3: **In progress**. 

### I-slowAction-IDEA-06-0.3.1

**Slow check in and check out actions on IDEA-06 (timeline evidence for N4)** (incident-group; refs f4ea534f, bcb16c24, ea2b2a2c, 968cc0cd, 428a0f6f, ccf970d7, 6d145628, 12ae9081, ee443a83).

- Found at 0.3.2: **Partly done**. Each window action runs its own pass; in 0.3.1 the plan phase took 34-37 s per pass for 1,607 files (passPhase plan ms 34,386-37,379), so a single click waited about 40-46 s and consecutive clicks queued (21:01:40-21:12:49: 17 single-file check ins, waits growing 41 s to 658 s, ccf970d7). 6d145628 (22:39:19, refused) is the check out of SmallFlywheel V3 whose fresh hash could not open the held file: CheckOutOutcome.CantRead, 'Armory couldn't read ... Close any program using it, then try again.' (SyncEngine.Checkout.cs:99, 724); closing it as told let the next pass revert the file. 12ae9081 (23:00:27) is the check out that kept 103,841 B and downloaded 101,906 B. ee443a83 (23:10:49) is the DXF check in (1 uploaded). 0.3.2 batches the open-file question (SyncEngine.cs:964 KnowOpen, OpenFileDetector.OpenAmong).
- How it was verified then: Flight passPhase timings and windowAction ms from the incidents; 0.3.2 change read in code only, not measured here.
- Cause: Per-file Restart Manager sessions in the plan phase (0.3.1) plus one pass per action.
- Missing at 0.3.2: Not measured on 0.3.2 hardware (a 0.3.2 slowAction c5dd91d2 still waited 15.2 s for a check out). Slowness is for the responsiveness auditor; the N4-relevant part is that the CantRead answer pushes the student to close a file whose lock Armory already let go of.
- 0.3.3: **In progress**. 

### I-slowPass-DESKTOP-F41DB2R-0.3.1

**DESKTOP-F41DB2R 0.3.1 passes of 62-90 s during the first full download and after a restart** (incident-group; refs N3, 4a5a4860, d3e64083, 1de30671, 8adfb590).

- Found at 0.3.2: **Partly done**. Plan batched (SyncEngine.cs:967). PassSlice 8 s stops starting new units (SyncEngine.cs:23, :1131).
- How it was verified then: Flight per incident: 4a5a4860 plan 1.5 s, move 67.5 s, one 45.9 MB download took 62.3 s after the slice; d3e64083 plan 23.8 s, move 65.1 s (135.7 MB file in 56 s); 1de30671 plan 34.2 s, move 27.3 s (317.5 MB in 23.3 s); 8adfb590 scan 38.4 s (first pass after the 22:07:31 restart), plan 26.0 s. Plan grew 22-30 ms per local read-only file as downloads landed (regression over 36 passes, 0 -> 1,413 local files, 0.4 -> 34 s).
- Cause: Three causes: per-file RM in plan; a cut-short pass waits for every in-flight download (RunConcurrentlyAsync only stops starting units); the hash cache of LocalChangeDetector lives only in memory (LocalChangeDetector.cs:47; only the folder map is persisted, :77), so the first scan after every start re-hashes the vault.
- Missing at 0.3.2: At HEAD: d3e64083 would still be ~73 s and 4a5a4860 ~73 s (move phase unchanged); 8adfb590 would still spend 38 s re-hashing on the first scan after a start.
- 0.3.3: **In progress**. 

### I-slowPass-DESKTOP-QH30N35-0.3.2

**slowPass 7a6c7d95: a 94.5 s loop pass held by one straggling 31.5 MB download** (incident-group; refs 7a6c7d95,N3).

- Found at 0.3.2: **Not done**. Pass 05:49:12.772 to 05:50:47.275: scan 242 ms, server 829 ms, plan 7,644 ms (open-files question over about 480 files), move 85,737 ms, 69 downloads. passYield slice at 85,735 ms with 880 units left. One download (31,555,430 bytes) took 83,492 ms and ended at 05:50:47.138, while other 31 MB files took 1.6-4.6 s; the other 68 had finished within the first 8 s slice, so 5 of 6 lanes sat idle about 77 s. The slowPass glitch (pass over 60 s) fired correctly. BlobClient.DownloadUnrecordedAsync (BlobClient.cs:144-170) has no idle or stall timeout; storageHttp timeout is 2 hours (AgentHost.cs:56) and does not bound a body read after ResponseHeadersRead.
- How it was verified then: Flight recorder of 7a6c7d95 summarized with scratchpad/tools/n123-passes.py (phases, yield, transfers, slowest transfers); read RunUnitsAsync/RunConcurrentlyAsync (SyncEngine.cs:1118-1177) and BlobClient.
- Cause: startNoMore (SyncEngine.cs:1128) stops new units after PassSlice and the pass awaits every in-flight unit, so one slow transfer holds the pass, the server re-read and the next pass; nothing detects a stalled body.
- Missing at 0.3.2: Refill of idle lanes while a straggler runs (or a continuous queue), stall detection and retry.
- 0.3.3: **In progress**. 

### I-slowPass-DESKTOP-QH30N35-0.3.2-slowpa

**DESKTOP-QH30N35 0.3.2 download pass 'still going after 94 s': one stalled 31.6 MB download held the pass** (incident-group; refs N3, N6, 7a6c7d95, c5dd91d2).

- Found at 0.3.2: **Not done**. PassSlice 8 s (SyncEngine.cs:23) only stops starting units (:1131); RunConcurrentlyAsync waits for running ones. Downloads: BlobClient.DownloadUnrecordedAsync read loop (BlobClient.cs:160) with no stall timeout; storage HttpClient timeout 2 hours (AgentHost.cs:56). 'still going' is logged only when a unit finishes (Heartbeat() at SyncEngine.cs:1203).
- How it was verified then: Flight of 7a6c7d95: scan 0.24 s, server 0.83 s, plan 7.6 s, move 85.7 s. 68 downloads ended by 05:49:29.8 (slice over), then one 31,555,430-byte download ended at 05:50:47.1 after 83,492 ms while two other ~31 MB files took 1.8 s and 2.5 s. Log: 'pass: still going after 94 s' at 05:50:47.220, 54 ms before the end. Same run (c5dd91d2 flight): every download cycle was plan ~10.4 s + 8.5 s of downloading, then quiet passes of 11.0-12.3 s with plan 10.505-10.526 s.
- Cause: A stalled or throttled connection on one presigned GET (a per-connection problem: the same size took 2 s on other connections) combined with a pass that cannot end before its in-flight units.
- Missing at 0.3.2: No stall detection or resume; a pass, and any click waiting for the gate, waits for the slowest in-flight transfer (up to 2 hours). The window shows nothing about it.
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-00-0.3.0

**IDEA-00 0.3.0 loop pass of 67.5 s with 2 large downloads** (incident-group; refs 58d36379).

- Found at 0.3.2: **Partly done**. Plan open checks batched with a 10 s budget (SyncEngine.cs:967, OpenFileDetector.cs:52-78).
- How it was verified then: Flight: scan 0.35 s, server 0.6 s, plan 40.2 s, move 26.3 s (downloads of 127.6 MB in 14.5 s and 317.5 MB in 25.7 s), finish 0.04 s. All 142 IDEA-00 0.3.0 passes: plan median 22.5 s.
- Cause: Per-file RM open checks (40 s) plus bandwidth-bound large downloads (26 s).
- Missing at 0.3.2: Plan would be ~10.5 s at HEAD, so this pass would be ~37 s (under the 60 s rule). The 317 MB download still holds the pass, and a click waits for it (actions do not start until running units end).
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-00-0.3.1

**IDEA-00 0.3.1 quiet loop passes of 60-82 s (0 moved), 22:16Z to 01:54Z** (incident-group; refs N6, 4cd9eae5, 8cb7df1d, 3c2c8c20, 42136488, e3ead3f1, b603a79e, 14d13f99, 1e6ba2cd, 9c3bf2db, 1f4c1092, b41a2f31, 8add4e24, c681d282, 57f69630, 76cc8f4b, 2e8f7fb1, 36077bff, 71b59d78, 32118ea1, f7bc5be4, 056b7c00).

- Found at 0.3.2: **Partly done**. 0.3.2 asks once per pass: SyncEngine.cs:967 KnowOpen(all local files) -> WindowsVaultFileSystem.OpenAmong (WindowsVaultFileSystem.cs:109-128, OpenBudget 10 s at :129) -> OpenFileDetector.OpenAmong (OpenFileDetector.cs:52-78: share-none probe per file :58-63, Restart Manager per 500 files :69, bisection :80-87, synchronous attribute.Wait(budget) :71). IsOpenNow answers from the batch (SyncEngine.cs:1232). Line numbers are at HEAD a8795a7; none of the commits after 8d90d63 touch these paths.
- How it was verified then: Read flight passPhase events of all 21 incidents (scan 0.10-0.14 s, server 0.55-1.35 s, plan 59.4-81.8 s, move/finish under 0.7 s). Aggregated all 577 IDEA-00 0.3.1 passes found in the flight recorders: 571 quiet, median 47.3 s, plan median 46.6 s, p90 70.2 s. Read 0.3.1 code (git show 0bc573f): PlanPathAsync called IsOpenNow -> fs.IsOpen -> OpenFileDetector.Inspect, one RM session plus a probe per file. Compared with 0.3.2 field data from DESKTOP-QH30N35 (plan plateau 10.42-10.53 s). The ~10 minute spacing is the incident throttle (Glitches.cs:134), not the pass cadence: every pass was slow.
- Cause: Plan phase = one Restart Manager session + exclusive probe per file (0.3.1), 40-55 ms per read-only file on IDEA-00 (1,476 files -> 60-82 s; varied with machine load over the evening). The same machine planned in 7.2-7.9 s (about 5 ms per file) whenever the files were checked out (writable): 18:37:27 to 19:43:54, and 18:29:50 to 18:34:18, back to 41-43 s minutes after each check in. RM is roughly 5x slower on read-only files, and Armory makes every file not checked out read-only (D4). 0.3.2's batching keeps that per-file cost (about 14.7 ms per read-only file batched), so it only bounds the pass by the 10 s budget.
- Missing at 0.3.2: At HEAD a quiet pass on ~1,500 files still takes about 11-12 s, almost all of it the 10 s Restart Manager budget expiring on the engine thread, and every pass leaves the abandoned RM query running on a thread-pool thread. No IDEA-00 0.3.2 data exists yet; there the per-file RM cost was 2-3x DESKTOP-QH30N35's, so the abandoned attributions of consecutive passes would overlap.
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-00-0.3.1-action

**IDEA-00 0.3.1 action passes of 92-94 s (Check out of a whole folder)** (incident-group; refs N6, N11, 1f22e3a0, 9e2aca4c, aa236105, 68bee25b).

- Found at 0.3.2: **Partly done**. Plan open checks batched (SyncEngine.cs:967). Check-out finish batched: FinishRequestsAsync collects pending check outs and wraps them in KnowOpen(onDisk) (SyncEngine.Checkout.cs:598-600). Locks taken in batches of 500 (armory_lock_files).
- How it was verified then: Flight: 1f22e3a0 scan 0.10 s, server 0.26 s, plan 41.3 s, move 0.01 s, finish 50.7 s with 3 armory_lock_files calls (1.6 s); 9e2aca4c plan 42.3 s, finish 51.2 s. The window waited 133.3 s (aa236105) = the in-progress loop pass (42.7 s, uninterruptible plan) + the 92.4 s action pass. Read 0.3.1 FinishCheckOutAsync: per check out a full SHA-256 of the file (SyncEngine.Checkout.cs:722-727 at HEAD) and an IsOpenNow RM session (:731 area, NextCheckOutStep).
- Cause: Finish phase 50.7 s = ~1,400 x (RM session ~28 ms + full re-hash ~8 ms). Plan 41 s = per-file RM over 1,473 read-only files. An action pass is a whole pass (PassLockedAsync with a scope only filters phase C).
- Missing at 0.3.2: At HEAD an action pass still runs phases A and B for every file (plan ~10.5 s with the RM budget), and FinishCheckOutAsync still re-hashes every file being checked out (IDEA-00 re-hashed its 1,473 files in 10.9 s on the 18:29:50 first scan), plus a second KnowOpen (up to another ~10.5 s). Estimated 1,400-file check out at HEAD: about 30 s, not instant.
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-06-0.3.0

**IDEA-06 (Abraham) 0.3.0 first pass after a restart and new sign-in took 129 s** (incident-group; refs 610eed69).

- Found at 0.3.2: **Partly done**. Plan batched with a 10 s budget at HEAD.
- How it was verified then: Flight: started 0.3.0 at 17:44:59, signed in 17:45:52; first real pass: scan 25.5 s, server 24.2 s (only 4 RPCs totaling 0.7 s), plan 32.5 s, move 23.3 s (no transfers, 1 heartbeat RPC, yielded with 1,461 units left), finish 23.6 s (2 release_locks calls 0.5 s). Snapshot: 1,624 files, 703 notInArmory, 714 savesWaiting, 55 checkInWhenClosed.
- Cause: First scan after a start re-hashes every file (hash cache not persisted); plan = per-file RM; server phase = local capture/import work for 703 new files (the 142-refused audit owns why they were never accepted); move/finish = local per-unit and per-view work on the engine thread (AutoCheckIn files cost one RM session each per view build, reproduced in X-view-open-per-file).
- Missing at 0.3.2: At HEAD the plan drops to ~10.5 s, but the first-scan re-hash (25 s) and the local work in server/move/finish phases remain; per-file IsOpen for the 55 files added while open still runs on every view build and in PrepareRelease/DesiredOwnership.
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-06-0.3.1

**IDEA-06: the same 142 files refused on every pass are name-taken copies, re-planned forever (not the year check)** (incident-group; refs 3b6e0912, 68d7a9d4, f9912084, 030b06da, 184c9e24, 2ab514eb, 610eed69, N4 (its log shows the same 142 refused every ~50 s)).

- Found at 0.3.2: **Not done**. Name refusal path, unchanged since 0.2.0 (SyncEngine.Actions.cs has no diff from v0.3.0 to HEAD 0ef1ae0): SyncEngine.cs:1012-1082 PlanPathAsync plans a local file with no server record as an add every pass (Reconciler.cs explicit add -> Commit, AcquireLockThenUpload); SyncEngine.cs:1216 ExecutePlannedAsync clears st.Refusal/RefusalKind before running the plan; SyncEngine.Actions.cs:167-174 UploadAsync -> :297-315 EnsureServerFileAsync -> :328-339 LiveNameHolder finds the live file holding the name in another folder -> :603-609 RefuseName sets the same refusal again and does refused++ (no server call). The unit was already counted as moving (SyncEngine.cs:1125 LogPassStart) and expected as an upload (SyncEngine.cs:1124 ExpectTransfers -> ActivityTracker.cs:74-84). The card: SyncEngine.View.cs:160-163 (NameTaken -> nameShared), :226-228 words; app.js:1087-1090 a Rename key per item. Server rule: unique index on (project_id, lower(normalize(name, NFC))), 0231_armory.sql:112-113, i.e. one file per name per project in any folder.
- How it was verified then: Read code at HEAD and v0.3.0/v0.3.1 (git show). Incident flight data (tools/i142-*.py): every IDEA-06 rpc and transfer in 47,005 distinct events is 200 OK except 2 offline heartbeats and 1 offline incident upload, so no 23505, PT429, 22023 release error, not-a-member, blob 4xx or rate limit; steady passes (68d7a9d4, f9912084, 184c9e24) make zero armory_create_file calls and the move phase takes 9-26 ms; log 'pass: moving 142 of 1,604 files' with '142 refused' every pass, so moving == refused == 142; snapshots from 18:42 on have no cantSend card (gate, too-large and server refusals all land in cantSend), only nameShared 148, so every refusal is a name refusal. Reproduced at HEAD in a throwaway E2E test (scratchpad/i142-repro-tests.cs, A_name_taken_file_is_planned_expected_and_refused_again_on_every_pass): 4 passes, each Refused=1, log 'pass: moving 1 of 3 files' each pass, activity at the step 'Uploading 0 of 1 file, 9 bytes left', 0 create calls, 4 state saves.
- Cause: The name check runs only at execution time (EnsureServerFileAsync) and the refusal is wiped at the start of every execution (SyncEngine.cs:1216), so a refusal that cannot change until a person acts (rename or delete the copy, or the namesake is renamed or removed) is re-derived every pass and reported as fresh work: 'moving 142', '142 refused', an expected upload of 260.9 MB, and the record dirtied twice per file (Refusal set to null then back). It cost no server calls and almost no time; it is noise that hides real state and keeps the window in 'syncing'. The user is right that it is not the year check: with ReleaseReader = null and the project in warn (server default, 0231_armory.sql:82; the website has no switch), SolidWorksVersionGate.Decide returns Allowed+ReleaseNotChecked, and a gate refusal would be a cantSend item, which was absent.
- Missing at 0.3.2: A standing refusal: nothing remembers that this file is blocked by a live namesake, so it is planned, counted as moving, expected as an upload, cleared, refused and re-saved every pass forever (since 0.2.0; 132 such items already existed when 0.3.0 started at 17:48). The exact 148 paths are not in any incident (see X-telemetry-refusals); on IDEA-06 they are the records with "refusalKind":"nameTaken" in C:\IDEA\Armory\.armory\state.json (camelCase, EngineState.cs:181), each refusal text naming the folder of the file that holds the name. Folder names in the data (Full Assembly, MISC/Prototypes/Francis, 2nd Climb, Turret V3, IntakeCYCLODIALREDESIGN) and COTS names (WCP-xxxx, REV-11-1850, TTB-0300, Thunderhex bearings, spur gears, MK5n) point to copied subassembly folders and vendor parts that already exist elsewhere in FRC 2026 Off-Season (inference).
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-06-0.3.1-slowpa

**IDEA-06 (Seraj) 0.3.1 loop pass of 3134.6 s: the process was frozen for 52 minutes** (incident-group; refs 5da43de6, 87fa2b3d).

- Found at 0.3.2: **Not done**. Nothing excludes suspended time: pass time is deps.Clock.GetElapsedTime(passStarted) (SyncEngine.cs:379 area, RecordPass) and GlitchRules.SlowPass files any pass over 60 s (Glitches.cs:22, :39-41).
- How it was verified then: Flight: scan 0.1 s, server 0.6 s, plan 3,133.9 s. No flight event of any kind from 22:00:06.7 to 22:52:02, although the heartbeat runs on its own thread-pool task every ~45 s (AgentHost.cs:126, TeamHeartbeat.cs RunAsync) and beat at 21:57:43, 21:58:28, 21:59:13, 21:59:58, then 22:52:02. The log shows only 'live updates: WebSocketException: The remote party closed the WebSocket connection' at 22:51:48, then the pass ended at 22:52:20. The 15 passes before took 48.4-50.2 s (plan 48-49.6 s).
- Cause: The whole process stopped (no thread recorded anything), almost certainly the computer sleeping, and the stopwatch includes the suspended time (3,134,550 ms equals the wall-clock span exactly). Note: Abraham's IDEA-06 agent was active during the same 52 minutes on a C:\IDEA\Armory vault with a different file count, so Seraj's IDEA-06 is a different physical computer (see X-two-computers-named-IDEA-06).
- Missing at 0.3.2: Sleep is counted as pass time, so a sleeping laptop or lab PC files a false slowPass. The real cost of this pass (~49 s of per-file RM) drops to ~11 s at HEAD.
- 0.3.3: **In progress**. 

### I-slowPass-IDEA-06-142refused

**IDEA-06 (Abraham) passes with 126-142 refused (audited by another agent); phase pointer only** (incident-group; refs 3b6e0912, 68d7a9d4, f9912084, 030b06da, 184c9e24, 2ab514eb).

- Found at 0.3.2: **Partly done**. Plan and ~$ marker open checks batched at HEAD (SyncEngine.cs:967, :1275).
- How it was verified then: Flight phases only: 3b6e0912 scan 6.4 s, plan 39.9 s, move 13.5 s (26 uploads); 68d7a9d4 move 1,050 s (owned by the refusals audit); f9912084 scan 8.9 s, plan 55.3 s; 030b06da scan 37.1 s (first pass after the 19:25 restart, re-hash), plan 26.5 s; 184c9e24 scan 6.3 s, plan 62.2 s. 184c9e24 snapshot: 'SolidWorks may have closed unexpectedly with 224 files open'. 2ab514eb flight shows the stale-marker notices 43 ms apart (two RM sessions per marker).
- Cause: Per-file RM in plan, 224 stale ~$ markers checked twice each per pass in the scan phase, re-hash after restarts.
- Missing at 0.3.2: The refusals themselves: see the 142-refused audit. Speed part: stale markers are still asked every pass (448 paths, up to its own 10 s budget), plan ~10.5 s, first-scan re-hash 37 s.
- 0.3.3: **In progress**. 

### I-takeBack-IDEA-00-0.2.1

**takeBack waited 35.5 s (0.2.1)** (incident-group; refs 9059fddb).

- Found at 0.3.2: **Partly done**. 0.3.1+ sends ONE takeBackAll for many files (app.js:2008-2011, Checkout.cs:324-399, one pass). 0.3.2 no longer asks Restart Manager per file in the plan (SyncEngine.cs:964).
- How it was verified then: Flight: loop pass 23:38:30.456-23:38:59.019 (plan 28,275 ms); asked 23:38:51.4, so 7.6 s waiting; armory_break_lock 138 ms; then an 'action' pass 27,715 ms (plan 27,085 ms) on 917 records. Snapshot engine.actionsWaiting = 98: the 0.2.1 window had queued one takeBack per file. Read current code.
- Cause: Then: per-file Restart Manager in the plan (28 s per pass) plus one pass per file. Now: whole-vault plan per action plus OpenAmong hitting the 10 s budget.
- Missing at 0.3.2: A single Force check in still waits for the loop's plan and runs its own whole-vault plan (Checkout.cs:305 PassLockedAsync(PassScope.File)), about 11 to 22 s on a 1,467-file vault like DESKTOP-QH30N35 (estimated from its 10.5 s plans).
- 0.3.3: **In progress**. 

### I-takeBack-IDEA-00-0.3.0

**takeBack waits of 24 s to 2,450 s: Force check in all queued 225 single-file actions** (incident-group; refs 80c8eb79, 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5).

- Found at 0.3.2: **Partly done**. Fixed at the root since 0.3.1: Force check in all and the selection bar send ONE takeBackAll (app.js:2008-2011); the engine breaks locks 16 at a time and runs ONE pass (Checkout.cs:316-399); test CheckOutTests.Force_check_in_of_many_files_is_one_action_and_one_pass.
- How it was verified then: Flight of 8bbe9bd5: 225 takeBack window actions asked between 17:10:08.566 and 17:10:09 (one per file); 104 answered ok one after another, each armory_break_lock about 110 ms plus an action pass about 22.5 s (plan 21.3-23.3 s on 917 records), so the n-th answer waited about n x 22.6 s (24.1, 631.4, 1246.7, 1849.5, 2450.1 s); 121 were answered refused in bursts at 17:47-17:50 (no longer checked out). Read current code and test.
- Cause: 0.3.0 window sent one takeBack per file and each ran a whole pass whose plan asked Restart Manager per file.
- Missing at 0.3.2: The one remaining pass of a takeBackAll is whole-vault (plan) and waits behind the loop's plan; armory_break_locks (0234) not used; a mentor clicking Force check in on many single rows still queues one pass per click.
- 0.3.3: **In progress**. 

### C-0.2.1-action-never-waits

**ENGINE.md 712-737: 'An action never waits behind a whole pass'** (claim; refs N6, all slowAction groups).

- Found at 0.3.2: **Not done**. The loop pass stops starting phase C units when an action waits (SyncEngine.cs:1128) and an action's phase C is scoped (SyncEngine.cs:1120).
- How it was verified then: Code: phases A, B and D of the loop pass run whole before the gate is handed over, and the action's own pass runs whole A, B, D (ENGINE.md 724 says so). Field: every 0.3.1 slowAction waited for the loop's plan; c5dd91d2 waited 3.8 s for it. Reproduced: the first of 5 row clicks answered at 3.56 s with a 2 s plan cost.
- Cause: Only phase C was made interruptible/scoped; phase B became the expensive phase.
- Missing at 0.3.2: Yielding before or during phase B; a scoped plan for actions.
- 0.3.3: **In progress**. 

### C-0.2.1-file-detail-never-waits

**ENGINE.md 212-214: 'File detail never waits for a pass'** (claim; refs c5dd91d2 (reference), N6).

- Found at 0.3.2: **Not done**. DetailAsync reads publishedRemote (SyncEngine.View.cs:512-514) but runs on the engine thread (View.cs:509-510).
- How it was verified then: Field: armory_file_history recorded 10,586 ms on 0.3.2 (sent 05:55:34.6Z, completed 05:55:45.19Z, right after the plan ended). Reproduced: 1.91 s of a 2 s blocked plan, 3 rounds out of 3. ConcurrencyTests.File_detail_answers_while_a_pass_moves_files only covers phase C.
- Cause: Engine-thread affinity of DetailAsync plus a synchronous plan.
- Missing at 0.3.2: Detail during a plan phase.
- 0.3.3: **In progress**. 

### C-0.3.0-batches

**0.3.0 batch check out/in (armory_lock_files, armory_release_locks)** (claim; refs aa236105).

- Found at 0.3.2: **Done**. ArmoryApi.LockFilesAsync/ReleaseLocksAsync (ArmoryApi.cs:151-161, 500 files per call); SyncEngine.Batches.cs LockBatchAsync (36-107) and ReleaseBatchAsync (157-240) with per-file answers, crash-safe in-flight records and a one-by-one fallback on PGRST202 (BatchesMissing, retried hourly). Server functions in 0233 (migration lines 1283, 1309).
- How it was verified then: Read code; ran CheckOutTests.Check_out_check_in_and_undo_of_many_files_each_take_one_batch_and_one_pass, V3Tests.Several_check_outs_go_in_one_batch_and_each_file_answers_on_its_own and V3Tests.Without_the_batch_rpcs_the_files_go_one_by_one (all passed). Field: incident aa236105 shows three armory_lock_files calls for 1,424 files.
- 0.3.3: **In progress**. 

### C-0.3.0-cantakeback

**0.3.0 can_take_back decides who sees Force check in** (claim; refs N11).

- Found at 0.3.2: **Done**. ArmoryApi.cs:138 reads can_take_back (null from an older server); EngineState.cs:265 CanTakeBack => TakeBack ?? role mentor/cad_lead; refused in TakeBackAsync (Checkout.cs:276, 340); window shows Force keys only when project.canTakeBack (app.js:1215, 1429, 1512-1522, 1617).
- How it was verified then: Read code; ran V3Tests.Force_check_in_shows_exactly_when_the_server_says_can_take_back and V3ClientTests.My_projects_carries_can_take_back_and_an_older_server_does_not (passed). N11 snapshot: projects canTakeBack true for a mentor.
- 0.3.3: **In progress**. 

### C-0.3.0-deleteforever

**0.3.0 delete forever (project and folder purges)** (claim; refs ).

- Found at 0.3.2: **Done**. SyncEngine.Purge.cs (armory_project_purged asked once per start, folder_purged change moves files aside), ArmoryApi.cs:143 ProjectPurgedAsync, notice kind projectDeleted rendered as one line (app.js:99, 1026).
- How it was verified then: Read code; ran V3Tests.A_folder_deleted_forever_leaves_every_computer_and_is_never_sent_again, A_project_deleted_forever_leaves_this_computer_with_one_line, Removed_from_a_project_is_handled_as_before_and_asked_once_per_start, A_not_a_member_answer_from_the_change_feed_asks_whether_it_was_deleted and V3ClientTests.Purged_is_a_time_or_null_and_folder_purged_names_its_files (passed).
- 0.3.3: **In progress**. 

### C-0.3.0-live

**0.3.0 live updates (RealtimeFeed)** (claim; refs N12, 809ae174).

- Found at 0.3.2: **Done**. Armory.Client/RealtimeFeed.cs (phx_join per project on armory_change_feed filtered by project_id, lines 75-90; Changed raised on postgres_changes at 204-205); engine wakes a pass on a change (SyncEngine.cs:188-194 OnLiveChange); host creates it per runtime (AgentHost.cs RestartRuntimeAsync, new RealtimeFeed).
- How it was verified then: Read code; ran V3Tests.Live_updates_wake_another_computer_and_every_subscription_is_filtered (passed). Field evidence: N12 snapshot engine.live {events: 1426, joined: 1, connections: 1}.
- 0.3.3: **In progress**. 

### C-0.3.0-send-feedback

**0.3.0: Send feedback goes straight to the IDEA team** (claim; refs N13, N14, 6be92c08, aa236105, 68bee25b, f9912084, c2d5d46c, bcb16c24, c5dd91d2).

- Found at 0.3.2: **Done**. Header key and Settings key, dialog, sendFeedback bridge message, note saved then sent through armory_submit_app_feedback (five arguments) with version and context, no incident after it (app.js:554-557, 1761, 1831-1836, 1968-1977; Bridge.cs:276-279; AgentTelemetry.cs:128-160; IncidentReporter.cs:126-131; IncidentUploader.cs:182-187; ArmoryApi.cs:125-127).
- How it was verified then: Read code; field evidence: 11 armory_submit_app_feedback RPCs, all 200, and all 15 notes in the export arrived, including the three the app called Held.
- 0.3.3: **In progress**. 

### C-0.3.0-teamstatus

**0.3.0 team status (armory_heartbeat)** (claim; refs N7).

- Found at 0.3.2: **Done**. Armory.Client/TeamHeartbeat.cs (state syncing/idle, PGRST202 retry), ArmoryApi.cs:149, AgentHost.cs:39 and TeamState (476-482) set 'syncing' while files move.
- How it was verified then: Read code; ran V3ClientTests.Heartbeats_carry_the_device_version_and_state_go_at_once_on_a_change_and_never_throw (passed). Field: N7 log '2026-10-08T18:24:05.890Z team status: offline'.
- 0.3.3: **In progress**. 

### C-0.3.1-force-all-fast

**Release notes 0.3.1: Force check in all is one go** (claim; refs 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5).

- Found at 0.3.2: **Done**. app.js:2008-2011 sends one takeBackAll; Checkout.cs:324-399 breaks 16 at a time then one pass.
- How it was verified then: Read code; existing test CheckOutTests.Force_check_in_of_many_files_is_one_action_and_one_pass asserts 40 break_lock calls and 1-2 list_changes reads.
- 0.3.3: **In progress**. 

### C-0.3.1-forcemany

**0.3.1 Force check in of many files is one action and one pass** (claim; refs 80a6bce8, e77b8de4, 9ed90e42, 8bbe9bd5).

- Found at 0.3.2: **Done**. Window sends one takeBackAll with only the held files (app.js:2079-2106, 2004-2011); Bridge.cs:199-208 (up to 20,000 ids) calls host.TakeBackAsync(list); SyncEngine.Checkout.cs:324-398 breaks locks 16 at a time (TakeBackConcurrency, 318) with per-file operation ids and then runs ONE scoped pass.
- How it was verified then: Read code; ran CheckOutTests.Force_check_in_of_many_files_is_one_action_and_one_pass (passed: 40 armory_break_lock calls, 1 to 2 list_changes reads). My throwaway probe: 160 files answered in about 1.05 s with School latency. Incidents 80a6bce8 to 8bbe9bd5 (0.3.0) show the old per-file behavior waiting 631 to 2,450 s.
- Cause: 0234 landed after the 0.3.1 client change; the client was not updated to adopt it.
- 0.3.3: **In progress**. 

### C-0.3.2-batched-open-checks

**0.3.2 claim: batched open-file checks end the 40 s passes, a quiet sync takes a second or two, clicks answer right away** (claim; refs N6, 7a6c7d95, c5dd91d2, 1f22e3a0, 4cd9eae5).

- Found at 0.3.2: **Partly done**. Batch per pass: SyncEngine.cs:967 (plan), Checkout.cs:600 (pending check outs), SyncEngine.cs:1275 (markers). OpenFileDetector.OpenAmong (OpenFileDetector.cs:52-78): share-none probe on every file first (:58-63, not just a fallback), then Restart Manager in sessions of 500 (:69), not one session for all; a batch with any holder is bisected (:80-87); the engine thread blocks synchronously in attribute.Wait(10 s) (:71); past the budget the answer is the probe alone and the diagnostic is logged at most every 10 min (WindowsVaultFileSystem.cs:121). The background RM task is never canceled and nothing stops a second one from starting.
- How it was verified then: 0.3.2 field data (same run, 7a6c7d95 and c5dd91d2): plan rose linearly by 14.7 ms per local read-only file (490 ms at 38 files -> 9.6 s at 656) then sat at 10.42-10.53 s from ~712 files on; log '05:51:37.363Z open files: Restart Manager did not answer within 10 s; exclusive-open probe used.'; quiet passes 10.97-12.31 s; an action pass 11.16 s (plan 10.53 s); Check out waited 15.2 s. Probe + resolve + Core for 1,467 files = plan minus budget = 0.42-0.53 s (about 0.3 ms per file). Compared with 0.3.1 on DESKTOP-F41DB2R: 22-30 ms per local file. Read the code; ran the existing reasoning against the throwaway EndToEnd test SpcAuditTests (OpenAmong once per plan confirmed).
- Cause: The 0.3.2 change assumed per-session overhead dominated; field data show RM costs per file (and about 5x more on read-only files), so batching halved the cost and the 10 s budget became the floor of every pass on a full vault.
- Missing at 0.3.2: Quiet passes are ~11-12 s, not 1-2 s; every click runs a full plan (~10.5 s) after waiting for the loop's plan; RM work continues after the budget on a thread-pool thread (about 21.6 s of RM per 1,467 files, so it runs nearly continuously); the existing Windows test uses 40 writable files with a 30 s budget and never covers read-only files, the budget path or the leak. Probe side effect: an exclusive handle on every vault file every pass, so SolidWorks or Explorer opening that file at that instant gets a sharing violation (also true in 0.3.1).
- 0.3.3: **In progress**. 

### C-0.3.2-fast-passes

**0.3.2 release note: a sync with nothing to do takes a second or two and clicks answer right away** (claim; refs c5dd91d2,7a6c7d95,N1,N3).

- Found at 0.3.2: **Partly done**. docs/agent/release-notes/v0.3.2.md:5 and ENGINE.md:126-131 claim it. The per-file Restart Manager session is gone (one OpenAmong call), so a 1,467-file pass no longer costs 40 s, but it costs 10.5 s (the budget), every pass, on the engine thread.
- How it was verified then: Flight data from DESKTOP-QH30N35 at 1,467 files: idle passes 10,966-12,310 ms with plan 10,505-10,526 ms; check out 15.2 s; code as in X-open-files-question.
- Cause: Batching reduced the per-file cost from about 28 ms to about 14.6 ms but kept the question over every file, and the budget turns the excess into a fixed 10 s wait.
- Missing at 0.3.2: The claim holds only below about 600 files on disk on this machine.
- 0.3.3: **In progress**. 

### C-0.3.2-feedback-sent

**0.3.2: Send feedback no longer says 'couldn't send' for notes that were sent** (claim; refs N13, N14, N15, N5).

- Found at 0.3.2: **Done**. IncidentUploader.cs:109-112 (feedbackSent checked first), 175-178 (set right after the RPC answers); guarded test V3ClientTests.cs:417-439.
- How it was verified then: Reproduced the field race in a throwaway worktree: v0.3.1 Held 42 of 60 with 60 RPCs (every note sent); HEAD Sent 60 of 60 with one RPC each. Ran the guarded test: pass.
- Cause: Background round and Send feedback raced for the same note file (see N13).
- 0.3.3: **In progress**. 

### C-0.3.2-folder-handover

**0.3.2: Use this folder hand-over plus Switch account** (claim; refs N10).

- Found at 0.3.2: **Partly done**. Engine hand-over SyncEngine.Accounts.cs:16-90; page app.js:637-668, 812, 2908-2913; bridge Bridge.cs:157-163; host AgentHost.cs:251-259; guarded Postgres tests AccountTests.cs:24-106.
- How it was verified then: Read code and tests (Postgres tests not run here).
- Cause: One owner per folder and one session per Windows user (see N10).
- Missing at 0.3.2: Works only when the owner has nothing waiting (N10 (a)); Switch account is a full browser sign-in, not quick; the owner's name never shows (X-owner-name); the owner's checked-out files stay writable and unwatched while another account is signed in (X-parked-writable).
- 0.3.3: **In progress**. 

### C-0.3.2-minekeys

**0.3.2 Check in all and Undo all in My files, with its own scroll box** (claim; refs N11, N12).

- Found at 0.3.2: **Done**. app.js:1243-1276 (keys when more than one file, own box over 6 rows, data-scroll-own), app.css:3580-3590; Undo all asks with the count (app.js:1846-1852, 2848-2853).
- How it was verified then: Playwright probe (manyMine: keys in view at open, box 370 px, Team files directly below) and check-ui PASS (flow at check-ui.mjs:1348-1360).
- 0.3.3: **In progress**. 

### C-0.3.2-pinnedkeys

**0.3.2 folder keys pinned while scrolling** (claim; refs N9).

- Found at 0.3.2: **Done**. app.css:3568-3576 '.recess-scroll:not(:has(.sel-bar)) .browser-head { position: sticky; top: -19px }', narrow offsets at 4041-4054; while files are picked the sticky selection bar takes the place (app.css:3597).
- How it was verified then: Playwright probe on bigProject: after scrolling 3,000 px the folder keys are still at the top (y 238 at 1280x800, y 146 at 420x720).
- Cause: n/a for the claim; see X-sticky-focus.
- 0.3.3: **In progress**. 

### C-0.3.2-quick-quit

**0.3.2 claim: quitting is quick again, so Windows no longer forces Armory closed (crash reports)** (claim; refs 7aa938ba, 2ab514eb, 62f377c1, e5e6e9f3).

- Found at 0.3.2: **Partly done**. PlanAllAsync checks ct per path (SyncEngine.cs:970). TrayApp.OnSessionEnding posts Quit asynchronously (TrayApp.cs:64, :113); Quit logs 'quitting' and awaits host.DisposeAsync (TrayApp.cs:190-200). AgentHost.StopAsync awaits the heartbeat goodbye (up to 3 s) before stopping the engine (AgentHost.cs:387-396), then waits up to 15 s twice (:18, :454, DisposeAsync). SyncEngine.StopAsync marshals the cancel onto the engine thread (SyncEngine.cs:179-181).
- How it was verified then: Read code; reproduced in a throwaway EndToEnd test (SpcQuitTests, patch saved at scratchpad/spc-repro.patch, worktree scratchpad/wt-spc): with the fake file system's OpenAmong blocking 6 s, Engine.StopAsync asked 1 s into it took 5,007 ms. All four quitting-then-killed incidents above show no 15 s warning, i.e. the kill came within 15 s.
- Cause: Quit is asynchronous and cooperative, and the engine thread runs long synchronous steps; Windows does not wait for an async quit at session end.
- Missing at 0.3.2: 1) The cancel cannot run while the engine thread is blocked: in OpenAmong (up to 10 s + probe, plan, markers, check outs), in fs.Scan (25-38 s re-hash on the first pass after a start, no ct). 2) Session end: nothing blocks WM_ENDSESSION (no SessionEnded handler, no ShutdownBlockReasonCreate), so Windows terminates the process right after the end-session messages regardless of engine speed. 3) AgentLog.UncleanEnd (AgentLog.cs:100-119) ignores 'quitting' and reports the last pass line, so every such kill is still a 'crash'. 4) LastFlight is written at most once a minute (LastFlight.cs:13) and not at quit, so the last minute is missing from these incidents.
- 0.3.3: **In progress**. 

### C-0.3.2-responds-right-away

**Release notes 0.3.2: 'Everything responds right away... a sync with nothing to do takes a second or two and your clicks answer right away'** (claim; refs c5dd91d2 (reference), N6).

- Found at 0.3.2: **Partly done**. Per-file Restart Manager removed from the plan (SyncEngine.cs:964, OpenFileDetector.cs:52-78).
- How it was verified then: Field 0.3.2 DESKTOP-QH30N35: quiet loop passes 10,966-12,310 ms with plan 10,505-10,526 ms; 'Restart Manager did not answer within 10 s' logged 05:51:37Z; single check out 15.2 s; file history 10.6 s. Reproduction on HEAD as in N6.
- Cause: Batch Restart Manager query exceeds its 10 s budget every pass and the wait is synchronous on the engine thread; actions still plan the whole vault.
- Missing at 0.3.2: Quiet passes are about 11 s on that PC, not 1-2 s; clicks wait 11-22 s.
- 0.3.3: **In progress**. 

### C-0.3.2-runninglines

**0.3.2 'What Armory is doing' running lines** (claim; refs N8, c5dd91d2).

- Found at 0.3.2: **Partly done**. Lines for check out, check in, undo, download and upload exist and arrive live during the pass (ActivityTracker.cs:31-32, 110-118; Checkout.cs:589; Batches.cs:51, 101, 206; app.js:936-956).
- How it was verified then: Throwaway end-to-end probe (live check-out lines at +268 ms and +767 ms; Force check in lines delivered only once, after the action answered); code read; incident c5dd91d2 (0.3.2) shows a 10.5 s plan phase per pass with no line.
- Cause: Timer tied to passes; only transfers and check-in releases have lanes.
- Missing at 0.3.2: Not live for Force check in; nothing during the wait, scan, server read, plan and hashing that precede a check out; no count or bar for check out/undo; panel scrolls out of sight; see N8.
- 0.3.3: **In progress**. 

### C-0.3.2-slice-counts

**ENGINE.md: the activity panel keeps its counts across slices; the server is read again about every 10 s** (claim; refs N3,N2).

- Found at 0.3.2: **Partly done**. ENGINE.md:729-733 and the comment at SyncEngine.cs:404-406. The lane is not Reset on a cut-short pass, so totals carry over ('Downloading 1,160 of 1,429 files' in the N3 snapshot), but RunUnitsAsync Drops the not-started units (SyncEngine.cs:1133), which hides the lane (ActivityTracker.cs:240) from the end of each slice until the next pass's ExpectTransfers, 3-12 s later. Passes ended every 19.4-22.8 s in the second half of the download, not about every 10 s.
- How it was verified then: Code read plus throwaway N123ProbeTests in wt-n123 (passed: Line and Download null between passes, back after Expect); N2 snapshot (sync.line 'Checking for changes.', activity null while 106 files were still moving).
- Cause: Drop is used both for files that will never move and for files merely postponed to the next slice.
- Missing at 0.3.2: Visible counts between slices; a test of the in-between state.
- 0.3.3: **In progress**. 

### C-0.3.2-theme

**0.3.2 instant theme switching** (claim; refs N7).

- Found at 0.3.2: **Partly done**. Click handler wears the theme at once (app.js:2897-2907); themeWanted guard (app.js:512-514); host WithSettings (AgentHost.cs:463-471); no reload.
- How it was verified then: check-ui flow (check-ui.mjs:1588-1599, PASS); Playwright with 4x CPU throttle on manyMine/bigProject: a 500 ms full render follows the click and, when the view lands before the next frame, the new theme is first painted after it (523 to 569 ms).
- Cause: No settings-only path or view de-duplication; partial CSS transitions.
- Missing at 0.3.2: Settings-only views still re-render all of Home 2 to 3 times; transitions on keys/pads/rows make a mixed frame; thumbnails blink. See N7.
- 0.3.3: **In progress**. 

### C-0.3.2-thumbnails

**0.3.2: pictures of parts from Windows' thumbnail handlers** (claim; refs N15).

- Found at 0.3.2: **Partly done**. ShellThumbnails.cs:14-183; MainWindow.cs:121-126, 215-241; AgentHost.cs:135-138; app.js:1142-1164, 2995-3011; BRIDGE.md:279-292.
- How it was verified then: Read code and tests; the only Windows test uses a BMP. Never checked with real SolidWorks files.
- Cause: Unverified, not known broken.
- Missing at 0.3.2: Lab check with SLDPRT, SLDASM and SLDDRW; timeout, bounded queue, logging, short-lived null cache (N15).
- 0.3.3: **In progress**. 

### X-142-ui-uploading-0-of-142

**Window: status cycles 'Checking for changes.' / flash of 'Uploading 0 of 142 files, 260.9 MB left' / 'A few files need you', and the incident snapshot froze the flash** (other; refs 68d7a9d4, 184c9e24, f9912084, N2, N8).

- Found at 0.3.2: **Not done**. SyncEngine.cs:1124 ExpectTransfers for every planned unit before any runs (ActivityTracker.cs:74-84 seeds 'Uploading 0 of N'); SyncEngine.cs:1202 activity.Drop in each unit's finally; SyncEngine.cs:1205-1206 PublishSoon after each unit builds a view if 500 ms passed (with a 40-60 s plan phase, the first unit always does, while all 142 are still expected); SyncEngine.View.cs:43 syncing line = activity line or 'Checking for changes.'; View.cs:44 attention line 'Everything else is saved. A few files need you.'; app.js:736 and :982-984 show activity.line first. SyncEngine.cs:406-412: activity.Reset, then await SettleAsync (state write), then the final PublishLocked. SyncEngine.Telemetry.cs:64 DescribeNow reads the last published View.
- How it was verified then: Read code; timing reasoning from flight phases (plan 41-62 s, move 9-26 ms); both snapshots taken at passEnd show sync.line and activity 'Uploading 0 of 142 files, 260.9 MB left' with state syncing, which is the view published at the first unit, still current while the engine awaited SettleAsync (the incident's DescribeAsync ran on the engine thread at that await). The repro test shows views alternating syncing 'Checking for changes.' and attention 'Everything else is saved. A few files need you.' every pass and the activity at the refused step reading 'Uploading 0 of 1 file, 9 bytes left'.
- Cause: Files that will be refused without a call are planned and expected as uploads; the status line and running lines have no notion of 'blocked, waiting for you'; the incident snapshot uses a stale published view.
- Missing at 0.3.2: What a student saw on IDEA-06 in 0.3.0/0.3.1: about 50 s 'Checking for changes.', a brief 'Uploading 0 of 142 files, 260.9 MB left', about 10 s of 'Everything else is saved. A few files need you.', repeat, so the app never looked settled and never said why. Nothing in the running lines (SyncEngine.cs:582 logs only moved files) mentions the 148 blocked files.
- 0.3.3: **In progress**. 

### X-38-uploading-forever

**38 files read 'uploading' forever on every computer (IDEA-00, IDEA-06, DESKTOP-F41DB2R, DESKTOP-QH30N35)** (other; refs 58d36379, 4a5a4860, 7a6c7d95, c5dd91d2, 184c9e24 and every snapshot after 18:10Z (45 on IDEA-00 at 0.2.1)).

- Found at 0.3.2: **Not done**. SyncEngine.View.cs:449: a server file with no current version and no local copy is 'uploading'; nothing in the engine ever removes or finishes a server record that has no version and no bytes anywhere (Reconciler: local null and remote revision null -> None).
- How it was verified then: Snapshot filesByStatus across all 77 incidents (tools/i142-status.py): uploading 38 on all four computers, including a fresh 0.3.2 install that has only downloaded (DESKTOP-QH30N35), so they are server records, not local files. Read code for the status rule.
- Cause: Create and first commit are separate calls; an interrupted add leaves an empty record that every computer shows as uploading and that holds the name (unique index 0231_armory.sql:112-113).
- Missing at 0.3.2: The list of the 38 (server query: select folder, name, created_at from armory_files where project_id='e7db2770-7680-4f67-98cb-2d0839e18589' and deleted_at is null and current_version_id is null). Hypothesis to check with that query: they are adds whose create landed but whose first version never did (a stop or sign-out between armory_create_file and the commit, then the local copy renamed or deleted), and each one still holds its name, so it can also cause name refusals.
- 0.3.3: **In progress**. 

### X-6-extra-nameShared-archived

**148 nameShared items vs 142 refused per pass: refusals in a project that is later archived are never cleared; never-added files there read 'uploading'** (other; refs 68d7a9d4, 184c9e24, 1c1b1c5b (nameShared 148, refused 142)).

- Found at 0.3.2: **Not done**. SyncEngine.cs:1025 PlanPathAsync returns null for an archived project's files (decision D8), so SyncEngine.cs:1216 never clears their refusal; SyncEngine.View.cs:160-163 turns any st.Refusal into a card item, archived or not; View.cs:441-445 StatusOf returns 'uploading' for a local file with no server record and no refusal, with no archived check (View.cs:63 Unsent does check archived, so the sync line says everything is saved).
- How it was verified then: Reproduced (throwaway E2E An_archived_projects_never_added_files_keep_old_refusals_and_read_uploading): a copy refused for its name, then the project archived: after two passes the nameShared card still has it ('1 file shares a name...'), the window stays 'attention'; a file written offline and never added reads 'uploading' while the line says the project is saved. Applying this to IDEA-06 (projects 'test' archived and 'FRC 2026 Off-Season') is an inference: 6 nameShared items are never re-planned, and the archived 'test' project is the one place the code leaves a refusal standing.
- Cause: Archived projects are skipped by planning but not by the notice and status builders.
- Missing at 0.3.2: Proof of which 6 items (state.json on IDEA-06 would show their paths).
- 0.3.3: **In progress**. 

### X-N5a-organize-checked-out

**N5(a): moving or renaming a file or folder someone else has checked out** (other; refs N5).

- Found at 0.3.2: **Not done**. Server (read only): armory_move_file (pina-hash/idea-app supabase/migrations/0233_feedback_round_and_armory_v3.sql:819-850) answers true only when the file is live AND the caller holds an unbroken lock on it from p_device; otherwise it returns false with no error and no role bypass (not even a site admin). armory_rename_folder and armory_delete_folder (0232_armory_v2.sql:281-322, 324-350) call armory_refuse_checked_out (0232:265-279), which raises 55006 {reason: checked_out, names, total} when any file under the folder has an unbroken lock held by anyone other than the caller on this device; no role bypass. armory_break_lock (0233:718-742) is the only way past a lock: mentor, cad_lead or site admin. App: RenameFileAsync refuses 'Maria Lopez on LAB-PC has Plate.SLDPRT checked out, so it can't be renamed now.' (SyncEngine.Checkout.cs:507-508); an Explorer move/rename takes a transient lock first (Actions.cs:835 AcquireTransientAsync), is refused, and is put back with 'X was put back: Maria has it checked out. A file can be renamed only while nobody else has it checked out.' (Actions.cs:877-894); window Rename/Delete folder refuse before calling the server with HoldersWords (Folders.cs:713-724, 1087-1088, 1183-1184); Explorer folder renames/deletes are put back and their files come back (Folders.cs:664-685, 619-621). None of these words mention that a mentor or CAD lead can force a check in, and a mentor gets the same refusal with no way to proceed in one step.
- How it was verified then: Read the SQL and ARMORY.md; read the engine paths named; traced the Explorer path DetectLocalMoves (Actions.cs:796-819) to ExecutePendingMovesAsync and FinishLocalMove.
- Cause: Server rule (holder-only moves, folder ops refused over others' locks). The app cannot move a file someone else holds without first breaking their lock; doing it silently would turn the holder's unsaved check-in into a kept copy and break their open SolidWorks document's path.
- Missing at 0.3.2: Any way to organize around someone else's check out: no lead path (force then rename), no queued 'rename when they check in', no window Move action (only Explorer can move a file between folders), no hint that a mentor can help.
- 0.3.3: **In progress**. 

### X-N5b-force-check-in

**N5(b): instructor override (Force check in) for single files, folders and selections, and how it reads for the holder** (other; refs N5).

- Found at 0.3.2: **Partly done**. Window: row key when the row is checked out by someone else and project.canTakeBack (app.js:1215-1218), file page (app.js:1617), folder key 'Force check in all' (app.js:1429-1430), selection bar 'Force check in' (app.js:1513-1522), confirmation naming holders and saying their changes are kept (app.js:1875-1882), one takeBack for one file and ONE takeBackAll otherwise (app.js:2008-2011, Bridge.cs:199-209, cap 20,000 ids Bridge.cs:91). Engine: one file Checkout.cs:266-311 ('Force checked in Plate.SLDPRT from Alex Kim. Anything they hadn't checked in is kept as their own copy.'); many Checkout.cs:324-399. Holder's side: the change feed's lock_broken with former_device_id of this computer sets BreakNotice (SyncEngine.cs:705-707); Core keeps changed bytes as a 'lock broken' side version and puts the shared version back (Reconciler.cs:55-83); notice card 'Plate.SLDPRT was force checked in' / 'A mentor or CAD lead took it back. Your changes that weren't checked in are kept in its history.' (SyncEngine.View.cs:179-184, 256-259); a check in that was waiting says '...was force checked in by a mentor before it was checked in. Your changes are kept in its history.' (Checkout.cs:243); history note 'Kept as Abraham's own copy: the file was force checked in' (View.cs:557).
- How it was verified then: Read app.js, Bridge.cs, engine and Core code; existing EndToEnd tests CheckOutTests.A_mentor_takes_back_a_check_out_and_nothing_is_lost (holder notice title and kept copy) and Force_check_in_of_many_files_is_one_action_and_one_pass; server SQL 0233:729-731 and 1007.
- Cause: Role policy is the server's; the app's holder-side wording was written before 'Force check in' replaced 'take back' and before lock_broken carried 'by'.
- Missing at 0.3.2: 1) A project member with role 'instructor' never gets can_take_back (0233:1007 'm.role in (mentor, cad_lead) or v_admin') and armory_break_lock refuses them (0233:729-731); Mr. Pina works because he is 'mentor' on FRC 2026 Off-Season. 2) The holder is never told who forced it although lock_broken carries {by} (the app reads only former_device_id). 3) 'took it back' and 'force checked in' name the same event. 4) Between the lock_broken read and the next pass, a holder with NO changes briefly sees 'Armory is keeping your changes that weren't checked in' (View.cs:182-184). 5) No word that a file still open in SolidWorks is now read-only (saves fail; Save As is the way out). 6) No way in the app to get the kept copy back (history shows it, nothing downloads it). 7) Force check in of many files does not use armory_break_locks (0234), and single-row clicks queue one whole pass each.
- 0.3.3: **In progress**. 

### X-break-locks-batch-unused

**armory_break_locks (migration 0234) not adopted** (other; refs 8bbe9bd5).

- Found at 0.3.2: **Not done**. TakeBackAsync(list) calls armory_break_lock per file, 16 at a time (Checkout.cs:316-383); ARMORY.md 546-592 asks apps to call armory_break_locks and fall back on PGRST202.
- How it was verified then: grep: no armory_break_locks in src/ (only docs/agent/website-requests-v0.3.2.md:23).
- Cause: Batch RPC arrived after 0.3.1's design.
- Missing at 0.3.2: Client method and engine use.
- 0.3.3: **In progress**. 

### X-checkin-release-guard

**PrepareRelease lets a lock go over bytes it never read and ignores an open file for check in, undo and Check in all** (other; refs N4, 7352f99d, a6f9e941, 1c1b1c5b).

- Found at 0.3.2: **Not done**. src/Armory.Agent.Engine/SyncEngine.Checkout.cs:640-680 PrepareRelease: clean = file?.Hash == st.BaseHash (671) from TryLocal (666), open checked only for AutoCheckIn (656), SetAttribute(Free) (675) then the release in flight. Used by single check in (181-198), undo (203-228; UndoCheckOutAsync refuses open files at 214-216 but relies on the same stale hash afterwards), Check in all (0.3.2 key, same FinishRequestsAsync path) and the batch release (SyncEngine.Batches.cs:141+). Answer 'Checked in <name>.' at SyncEngine.Checkout.cs:242 whenever the release went through. DesiredOwnership (SyncEngine.Actions.cs:945-947) makes any file with Request!=None read-only even while open. FinishCheckOutAsync already hashes fresh (SyncEngine.Checkout.cs:715-724), so check out is safe; check in is not.
- How it was verified then: Code read; field evidence: release_lock at 22:31:27.756Z, 22:45:15.326Z, 23:04:06.500Z (SmallFlywheel V3) and 21:33:57.025Z (Toparmredesignnoscrewpocket), each in an action pass whose own scan logged 'being used by another process' for that file and uploaded 0. Repro test fails at HEAD; prototype passes all EndToEnd tests.
- Cause: Release readiness trusts the pass's scan hash, which can be stale (see X-stale-unread-entry), and a check in has no 'close first' rule.
- Missing at 0.3.2: Fresh read and open check before every requested release; a waiting state and answer for an open file; writable while a check in waits on an open file.
- 0.3.3: **In progress**. 

### X-checkout-rehash

**Check out reads and hashes every target file again** (other; refs aa236105, 68bee25b).

- Found at 0.3.2: **Not done**. FinishCheckOutAsync hashes each file with ContentAddress.ComputeAsync (Checkout.cs:716-724) though the pass's scan just hashed it.
- How it was verified then: Read code.
- Cause: Guard against bytes changing between scan and lock, applied to every file.
- Missing at 0.3.2: Reuse of the scan hash when unchanged.
- 0.3.3: **In progress**. 

### X-checkout-rehash-slowpa

**Checking out many files re-hashes every one of them** (other; refs N11, 1f22e3a0, 9e2aca4c).

- Found at 0.3.2: **Not done**. FinishCheckOutAsync hashes the whole file for each pending check out (SyncEngine.Checkout.cs:722-727).
- How it was verified then: Read code; IDEA-00 hashes its vault in about 11 s (first-scan timing), included in the 50.7 s finish of 1f22e3a0.
- Cause: Defensive re-hash for a race that a stat comparison can detect.
- Missing at 0.3.2: Reuse of the scan's hash when the file is provably unchanged.
- 0.3.3: **In progress**. 

### X-download-cycle

**Downloads run only about 45% of the time: each 8 s slice is followed by a full rescan and 10.5 s plan (N3)** (other; refs N3, c5dd91d2, 7a6c7d95).

- Found at 0.3.2: **Not done**. PassSlice 8 s (SyncEngine.cs:23); a cut-short pass is followed at once by a whole new pass (SyncEngine.cs:242-247 loop).
- How it was verified then: c5dd91d2 flight 05:50:47-05:54:27: each pass scan 0.2-0.8 s, server 0.2-0.9 s, plan 10.4-10.5 s, move 8.3-8.6 s (69-89 files each).
- Cause: The slice re-runs phases A and B, whose cost is dominated by the open-file budget.
- Missing at 0.3.2: Continuous downloading while the server is re-read.
- 0.3.3: **In progress**. 

### X-feedback-snapshot-lost

**Feedback and incident snapshots lost when the engine is busy** (other; refs N4, N5, N7, N9, N13, 12ae9081, 428a0f6f, 6d145628, 968cc0cd, bcb16c24, ccf970d7, ea2b2a2c, ee443a83, f4ea534f).

- Found at 0.3.2: **Not done**. IncidentReporter waits SnapshotDeadline 3 s for engine.DescribeAsync (IncidentReporter.cs:18, 70-81), which runs on the engine thread.
- How it was verified then: 5 of 15 feedback notes and 9 of 23 slowAction incidents carry {'unavailable': 'the engine did not answer within 3 seconds'}; sendFeedback window actions took 1.8-3.1 s in those flights.
- Cause: Engine thread blocked by the synchronous plan.
- Missing at 0.3.2: A snapshot that does not need the engine thread.
- 0.3.3: **In progress**. 

### X-full-render

**Every view re-renders all of Home; slow with thousands of files and thumbnails blink** (other; refs N6, N7, N9, c5dd91d2).

- Found at 0.3.2: **Not done**. bridge.onMessage 'view' always runs buildIndex and render() (app.js:3127-3147), which replaces header, #main and the Settings sheet innerHTML (app.js:509-544); the engine publishes a view at most every 500 ms during a pass whenever its JSON changes (SyncEngine.cs:1442, 1458-1508); there is no de-duplication in MainWindow (295-330, 368-373) or the page; thumbnails are new img elements each time and served with Cache-Control: no-cache (MainWindow.cs:229).
- How it was verified then: Playwright timing: one full render with manyMine/bigProject costs about 100 to 134 ms at 1x and 497 to 538 ms (a single long task) at 4x CPU throttle. Code read for publishing cadence and thumbnail headers.
- Cause: The page was designed for a whole-view model; with 1,400 to 5,000 rows the index rebuild and DOM rebuild dominate.
- Missing at 0.3.2: Incremental updates for big views.
- 0.3.3: **In progress**. 

### X-hash-cache-not-persisted

**First scan after every start re-hashes the whole vault (10-38 s)** (other; refs 8adfb590, 030b06da, 610eed69, 1f22e3a0, 68bee25b).

- Found at 0.3.2: **Not done**. LocalChangeDetector keeps file hashes in memory only (LocalChangeDetector.cs:47 cache = []); only the folder map is loaded and saved (:77, LoadFolderMap/SaveFolderMap).
- How it was verified then: Read code; flight: first-pass scan after a start 38.4 s (8adfb590, DESKTOP-F41DB2R), 37.1 s (030b06da), 25.5 s (610eed69), 11.1 s and 10.9 s (IDEA-00 18:20:59 and 18:29:50) versus 0.05-0.4 s on later scans.
- Cause: Design: cache rebuilt per process.
- Missing at 0.3.2: A durable hash cache keyed by path, NTFS file id, size and last-write time.
- 0.3.3: **In progress**. 

### X-kept-save-every-pass

**A checked-out file whose save is already kept counts as 'moving 1' and flashes 'Uploading 0 of 1 file' every pass** (other; refs N1).

- Found at 0.3.2: **Not done**. N1 log: after '1 kept copies' at 05:57:46, every pass logs 'pass: moving 1 of 1,467 files (loop)' and 'ended ... 0 kept copies' (7 times to 05:59:02); N1 snapshot sync.line 'Uploading 0 of 1 file, 355.6 KB left' with 0 transfers running. Core plans SaveSideVersion (SavedWhileCheckedOut) every pass for a changed checked-out file; ExpectTransfers adds it to the upload lane (SyncEngine.cs:1091-1105) and LogPassStart counts it (1122), then PreserveAsync returns at once because st.Preserved == hash (SyncEngine.Actions.cs:192-203).
- How it was verified then: Read code; N1 Context log lines and snapshot.
- Cause: ExpectTransfers and LogPassStart count every non-None action, not actions that will send bytes.
- Missing at 0.3.2: No expectation or 'moving' count for a preservation that is already met.
- 0.3.3: **In progress**. 

### X-long-passes-1108s

**The 1,108.8 s and 369.9 s passes were the computer asleep or frozen, not sync work** (other; refs 68d7a9d4, f9912084).

- Found at 0.3.2: **Not reproducible**. 68d7a9d4: plan ended 18:24:05.790Z, heartbeat failed 'offline' at 18:24:05.890Z, then no event at all until 18:41:35.903Z (heartbeat every 45 s, TeamHeartbeat.cs:14), then a WebSocket close; the move phase that 'took' 1,050 s is normally 9-26 ms. Glitches.cs:39-42 counts wall time.
- How it was verified then: Flight events read in full for that window.
- Cause: Machine sleep (network dropped, no heartbeats); the slowPass rule counts suspended time.
- Missing at 0.3.2: 
- 0.3.3: **In progress**. 

### X-no-version-uploading

**38 team files with no current version show 'Uploading' forever on a computer that uploads nothing** (other; refs N2,N3).

- Found at 0.3.2: **Not done**. Every snapshot shows filesByStatus uploading 38 with pendingCount 0, and passes moved 1,429 of 1,467. SyncEngine.View.cs:449 returns FileStatuses.Uploading when the file is not on disk and remote.Current is null; app.js STATUS shows an 'Uploading' chip (app.js:80) and UP_TO_DATE counts it as up to date (app.js:122).
- How it was verified then: Read StatusOf (SyncEngine.View.cs:435-454) and Unsent (58-66): pendingCount 0 rules out local new files, journal entries and in-flight writes, which leaves server rows with no current version not on this disk. The 38 rows themselves were not inspected (no server access).
- Cause: StatusOf maps 'no current version' to Uploading regardless of which computer, if any, is uploading it.
- Missing at 0.3.2: An honest status for a team file whose first upload never finished elsewhere.
- 0.3.3: **In progress**. 

### X-open-files-question

**'Restart Manager did not answer within 10 s': the open-files question over every file blocks the engine thread every pass** (other; refs N3,N2,N1,7a6c7d95,c5dd91d2).

- Found at 0.3.2: **Not done**. OpenFileDetector.OpenAmong (Armory.Platform.Windows/OpenFileDetector.cs:52-78): probe loop with FileShare.None on every file (58-63, IsBlocked 89-96), then Task.Run over Restart Manager batches of 500 (67-70) and attribute.Wait(budget) (71); on timeout it returns the probe answer with the diagnostic 'Restart Manager did not answer within 10 s; exclusive-open probe used.' (73). The background query is never canceled and keeps running after the budget. WindowsVaultFileSystem.OpenAmong logs the diagnostic at most once per 10 minutes (WindowsVaultFileSystem.cs:121-125), with OpenBudget 10 s (129). It is called synchronously from the engine thread by KnowOpen (SyncEngine.cs:1240-1249) for every file on disk in every pass (964), in batch check outs (SyncEngine.Checkout.cs:130, 214, 594), folder moves (SyncEngine.Folders.cs:1453) and markers (SyncEngine.cs:1272). EngineThread is one dedicated thread (EngineThread.cs), so while it waits nothing else runs: window actions cannot even register as waiting (EnterActionAsync runs there), views are not published, and file detail (GetFileDetailAsync, SyncEngine.View.cs:509-510, runs on the engine thread) waits. No flight event records the question.
- How it was verified then: Read code. Flight data: plan phase rose by about 14.6 ms per file on disk ((9620-1818) ms over 534 files) and is 10,422-10,526 ms in every pass from 05:51:25 to 05:55:56, including the 6 passes with nothing to move (12.3, 11.7, 11.0, 11.7, 11.2 s total) and the check out's own action pass; the one log line at 05:51:37.363 is the 10-minute rate limit hiding the rest. armory_file_history in c5dd91d2 measured 10,586 ms (its continuation waits for the engine thread; PostgrestClient has no ConfigureAwait(false)). Searched all 77 incidents: the line appears only in this device's 0.3.2 log. Existing tests never see the cost: PortableVaultFileSystem.OpenAmong answers instantly (tests/Armory.EndToEnd.Tests/PortableVaultFileSystem.cs:177-185) and the Windows test uses 40 files with a 30 s budget (tests/Armory.Platform.Windows.Tests/ReplaceAndLockTests.cs:290-307).
- Cause: 0.3.2 replaced one Restart Manager session per file with batches of 500 but still asks about every file every pass; RmGetList cost grows with the files registered (about 14.6 ms each here), so 1,467 files need about 21 s and always hit the 10 s budget, which the engine thread waits out synchronously. The abandoned query overlaps the next pass's query, which slows both. The per-file exclusive probe on every file every pass also briefly takes an exclusive handle on files SolidWorks may be opening (not observed in these logs, a risk).
- Missing at 0.3.2: Narrow question, off-thread execution, cancellation of the abandoned query, a shorter budget for planning, telemetry for every overrun, a scale test.
- 0.3.3: **In progress**. 

### X-openamong-blocks-engine

**Windows open-file question blocks the engine thread up to 10 s every pass** (other; refs c5dd91d2 (reference), N6, N5 context).

- Found at 0.3.2: **Not done**. OpenFileDetector.OpenAmong: exclusive-open probe of every file (lines 57-62), then Restart Manager 500 files a session with a halving search per held file (69, 80-89) on Task.Run, waited synchronously with attribute.Wait(budget) (71); budget 10 s (WindowsVaultFileSystem.cs:129). Called from PlanAllAsync over every local file (SyncEngine.cs:964), from ReadMarkers (1272), FinishRequestsAsync (Checkout.cs:594) and CheckOutAnswer (130), all on the engine thread.
- How it was verified then: Field plan phase pinned at 10.4-10.5 s for 23 of 24 passes on 0.3.2 plus the 'did not answer within 10 s' log line; code read. Consequences reproduced with a simulated cost (N6).
- Cause: A held file costs about 2 x log2(500) Restart Manager sessions in the halving search, so a SolidWorks assembly with dozens of parts open (or a thumbnail handler holding files, unverified hypothesis) exceeds 10 s; the query also keeps running on the pool after the budget, one more per pass.
- Missing at 0.3.2: Async, bounded and minimal open checks; diagnostics naming the holders.
- 0.3.3: **In progress**. 

### X-owner-name

**The folder-taken screen never names the real owner, and flashes the new student as the owner** (other; refs N10).

- Found at 0.3.2: **Not done**. folderTakenHtml takes the owner by matching an address in v.connect.message (app.js:638-641). The engine's connect message comes only from the host's connect phases: 'Signed in as <new account>. Getting your files list.' while finishing, then null when idle (AgentHost.cs:284-295, 492-494; SyncEngine.cs:209-215). The demo state carries a made-up message with the owner's address (demo/states.js:445-456), so UI checks pass with text the real engine never produces.
- How it was verified then: Read code: the vaultOwnedByOther view is published during the finishing phase (OnEngineView settles on VaultOwnedByOther, AgentHost.cs:453-458), so for a moment the page parses the new account's own address and says 'This folder belongs to <you>', then shows 'someone else' once the phase goes idle.
- Cause: The page parses a free-text sentence for the owner, and the engine never puts the owner in the view.
- Missing at 0.3.2: The owner's identity and waiting summary in the view.
- 0.3.3: **In progress**. 

### X-parked-writable

**While another account (or nobody) is signed in, the owner's checked-out files stay writable and unwatched** (other; refs N10).

- Found at 0.3.2: **Not done**. PassAsync returns before any scan or rule when there is no session or the folder belongs to another account (SyncEngine.cs:430-432). Read-only intents are applied only in passes, so files the owner checked out here stay writable, nothing downloads, and no readOnlyBroken repair runs. AccountTests.cs:36-44 shows the folder is left untouched in that state.
- How it was verified then: Read code (SyncEngine.cs:423-441, SyncEngine.Accounts.cs header comment lines 6-13).
- Cause: The engine only acts for the folder's owner, and protection (read-only rule) is part of the pass it skips.
- Missing at 0.3.2: Protection of the parked owner's files. A second student who opens one of those files in SolidWorks can save into it; when the owner signs back in, the pass sees the changed bytes of the owner's own check out and captures them as the owner's save, sent under the owner's name at check in. The comment says two accounts in one folder is never allowed because the second would see the first one's work as its own; the actual hazard is the reverse.
- 0.3.3: **In progress**. 

### X-pass-48-60s

**Why each IDEA-06 pass took 48-60 s: per-file open checks in the plan phase, not the refusals; 0.3.2 only partly fixes it** (other; refs 68d7a9d4, f9912084, 184c9e24, 2ab514eb, 030b06da, 7a6c7d95 (0.3.2 field data)).

- Found at 0.3.2: **Partly done**. 0.3.1: IsOpenNow -> fs.IsOpen per path (v0.3.1 SyncEngine.cs:1216-1217) -> OpenFileDetector.Inspect: one Restart Manager session plus an exclusive-open probe per existing file. HEAD: SyncEngine.cs:967 KnowOpen(local files) once per plan -> WindowsVaultFileSystem.OpenAmong -> OpenFileDetector.OpenAmong (probe every file, Restart Manager per 500, 10 s budget); SyncEngine.cs:970 ct check per path; ReadMarkers asks all ~$ markers at once.
- How it was verified then: Flight passPhase events: on IDEA-06 (1,604 paths) scan 6.0-8.9 s, server 0.2-0.8 s, plan 41.4-55.3 s, move 9-26 ms: about 26 ms per local file in plan, matching the ~28 ms per Restart Manager session the 0.3.2 comment cites. ReleaseReader is null so no file is read for its release. 0.3.2 field data (DESKTOP-QH30N35, 7a6c7d95): plan grows with local files: 1,818 ms at ~84, 2,782 at ~163, 3,897 at ~238, 5,354 at ~329, 6,695 at ~402, 7,644 at ~480, i.e. still about 16 ms per local file (computed by tools/i142 script from passEnd downloaded counts).
- Cause: Every pass asks the open state of every local file, though Core's plan only depends on IsOpen for files with something to do (Refresh, Recover, removal of an open file).
- Missing at 0.3.2: At 0.3.2 rates a 1,600-file vault like IDEA-06's would still spend roughly 25 s per pass in plan (estimate, not measured on IDEA-06). The remaining per-file cost is inside OpenAmong (exclusive probe or Restart Manager), not separated by measurement.
- 0.3.3: **In progress**. 

### X-pending-count

**Folder-wide actions overcount and mark every row as being worked on** (other; refs N9, N11, N12).

- Found at 0.3.2: **Not done**. workingOf() (app.js:2218-2252) words the foot line with filesWords(paths) (app.js:2203-2215), which counts every indexed path under a folder path; pendingOf() (app.js:2142-2150) marks every row at or under a folder path; startWorking() (app.js:2161-2180) inserts 'Checking in...' into those rows and the CSS hides who has them.
- How it was verified then: Playwright probes with the demo holding the answer (press param): manyMine + My files Check in all: foot 'Checking in 5,018 files...' while 1,401 are mine; bigProject + folder Check in all: 'Checking in 5,000 files...' with one file mine and 38 drawn rows labeled; checkedOutByOther: Check out all dialog 'Check out 6 files in Drivetrain...' then foot 'Checking out 8 files...'; folder Check in all labels Maria's Plate-Left 'Checking in...' with her 'Checked out by Maria Lopez' chip hidden.
- Cause: The page expands folder paths to all files instead of filtering by state per action type (check in/undo: state mine; check out: available; take back: other/myOtherComputer).
- Missing at 0.3.2: Counts and labels limited to the files the action will actually touch.
- 0.3.3: **In progress**. 

### X-readonly-manifest-per-file

**Bulk check out/in rewrites the durable read-only manifest once per file** (other; refs aa236105, 68bee25b, c2d5d46c).

- Found at 0.3.2: **Not done**. SetAttribute (SyncEngine.Actions.cs:1004-1019) -> fs.ApplyLockAttribute (WindowsVaultFileSystem.cs:215-223) -> ReadOnlyPolicy.Apply (ReadOnlyPolicy.cs:42-48) -> Persist: whole manifest, WriteThrough, Flush(true), rename (209-237). Called per file from LockBatchAsync (Batches.cs:95), PrepareRelease (Checkout.cs:675), FinishCheckOutAsync (710, 731), LockOneByOneAsync (Batches.cs:116). A batch API exists: ApplyLockAttributes -> ApplyMany with at most one write (ReadOnlyPolicy.cs:50-55).
- How it was verified then: Read code; field gaps of about 2.5 s between 500-file lock_files calls and 6.1 s before a 1,424-file release batch.
- Cause: Single-file helper reused inside bulk loops.
- Missing at 0.3.2: Batching in the check out and check in paths.
- 0.3.3: **In progress**. 

### X-recovery

**Abraham's work is recoverable from server kept copies (and local snapshots); the app has no restore** (other; refs N4, 12ae9081, 184c9e24).

- Found at 0.3.2: **Partly done**. Server side versions (reason 'changed without a check out', author Abraham): SmallFlywheel_0.55lbsV3.SLDPRT at about 22:40:05Z (size unknown, flight gap), 23:00:26Z (103,841 B) and 23:05:34Z (104,566 B, the newest); Toparmredesignnoscrewpocket.SLDPRT at about 23:11:35Z (confirmed by the 03:21 keptCopy notice). The website serves any version or side version as a download: /home/user/pina-hash/idea-app/src/routes/armory/[project]/file/[file]/version/[version]/+server.ts, named '<name> (side <stamp>).SLDPRT'. Local copies: C:\IDEA\Armory\.armory\snapshots on IDEA-06 (DurableSnapshotStore never deletes). The app's history is display only (src/Armory.Agent/wwwroot/app.js:1696-1715). SmallFlywheel_0.55lbsV4.SLDPRT was a Save As from the reverted V3 at about 23:09, so it likely lacks the earlier V3 edits.
- How it was verified then: Flight transfers and passEnd counts; 184c9e24 snapshot notices; website route read; Reconciler ordering (SaveSideVersion before Download) and the repro ('edits on server=True').
- Cause: N4 mechanism; preservation worked, the working copy was reverted 3 times for SmallFlywheel V3 (22:40:05, 23:00:27, 23:05:36) and once for Toparmredesignnoscrewpocket (about 23:11:35, about 1.5 hours of work). Toparmredesign and Hook V3 show no kept copy, so nothing of theirs changed.
- Missing at 0.3.2: No in-app way to bring a kept copy back; intermediate saves made while SolidWorks held the file were never captured separately (only the bytes at close), which is the student's own later save anyway.
- 0.3.3: **In progress**. 

### X-responsiveness-test-gap

**No window action is latency-tested on a large vault** (other; refs N6).

- Found at 0.3.2: **Not done**. Latency asserted: checkOut and checkIn under 3 s during 200-file transfers on a 201-file vault with free open checks (ResponsivenessTests.cs:35-136); launchFile of a file not yet on disk under 2 s on a 61-file vault (CheckOutTests.cs:330-360); file detail under 2 s during phase C (ConcurrencyTests.cs:44-66); Windows shell open under 2 s (ShellOpenerTests.cs:90). Counted, not timed: open questions per pass (CheckOutTests.cs:241-268), break_lock calls (CheckOutTests.cs:156-198). ThroughputTests and ImportScaleTests time transfers and passes only.
- How it was verified then: Read every test that uses Stopwatch in tests/Armory.EndToEnd.Tests and tests/Armory.Agent.Tests.
- Cause: Tests model the open check as free (PortableVaultFileSystem.OpenAmong) and use small vaults.
- Missing at 0.3.2: Not measured at all: undoCheckOut, takeBack, takeBackAll, createFolder, renameFolder, deleteFolder, renameFile, addFiles, dropFiles, takeOverFolder, reportProblem, sendFeedback, dismissNotice, pause/resume, launchFile of an on-disk file, openFile during a plan; none of the 29 bridge types is measured on a 1,000+ file synced vault, with LatencyProfile.School, or with a costly open-file question.
- 0.3.3: **In progress**. 

### X-signed-out-as-refusal

**A sign-out during a pass is recorded as a permanent-looking refusal with wrong words (explains cantSend 574 -> 344 -> 0 on IDEA-06)** (other; refs 610eed69, 6be92c08, 3b6e0912).

- Found at 0.3.2: **Not done**. SyncEngine.Actions.cs:409-422 SendDurableAsync catches ArmoryClientException; ArmorySignedOutException (Session.cs:56, thrown by SessionManager.cs:47/52 'This computer is not connected to Armory.') falls to the else branch: st.Refusal = PlainRefusal(...), RefusedKind, refused++; PlainRefusal's default (Actions.cs:445) says 'This save couldn't be read back from this computer's safe copy. It stays on this computer.' Refusal is a serialized field (EngineState.cs:385), so it survives restarts until the file is planned again.
- How it was verified then: Reproduced (throwaway E2E A_sign_out_mid_upload_is_recorded_as_a_refusal): report Refused=1, card 'Roller.SLDPRT can't be uploaded / This save couldn't be read back from this computer's safe copy.' Field: IDEA-06 log 15:03Z 'signed out' x2, 16:01:47Z 'sync: ...: This computer is not connected to Armory.', then 0.3.0's first snapshots show cantSend 574, 558, 344 shrinking as slices re-planned files, and none after 18:42Z.
- Cause: Sign-out is treated as a server refusal instead of a stop like offline.
- Missing at 0.3.2: 
- 0.3.3: **In progress**. 

### X-stale-markers

**224 stale ~$ markers on IDEA-06 are asked about every pass** (other; refs 184c9e24, 2ab514eb).

- Found at 0.3.2: **Partly done**. ReadMarkers asks one OpenAmong for every marker and its document (SyncEngine.cs:1275); stale markers stay ignored but are asked every pass; files are deleted only in otherwise empty folders.
- How it was verified then: Snapshot 184c9e24 'SolidWorks may have closed unexpectedly with 224 files open'; IDEA-06 0.3.1 scan median 5.2 s versus 0.1 s elsewhere; 2ab514eb notices 43 ms apart (two RM sessions per marker in 0.3.0).
- Cause: Stale markers are re-checked every pass and never cleaned up.
- Missing at 0.3.2: At HEAD 448 paths per pass through the same RM path (about 6.6 s at 14.7 ms per file, capped at 10 s).
- 0.3.3: **In progress**. 

### X-stale-unread-entry

**The Windows scan reuses the previous hash and read-only flag for a file it cannot open, with no marker** (other; refs N4, all 13 readOnlyBroken incidents).

- Found at 0.3.2: **Not done**. src/Armory.Platform.Windows/LocalChangeDetector.cs:172-178: on IOException the previous LocalFileState is copied into the new scan ('Something is at this path: never a deletion'). LocalFileState and LocalFile (src/Armory.Agent.Engine/Platform.cs:7) have no field saying the entry was not read. WindowsVaultFileSystem.cs:91 maps it as is. The engine adds only a cantRead notice (SyncEngine.cs:467, 1420-1438). Consumers that act on the stale data: PrepareRelease (data loss), ApplyReadOnly (false readOnlyBroken, SyncEngine.Actions.cs:977-980), plan input (harmless today because Replace and EnsureSnapshot re-read), and the view status (shows Synced for a file being edited).
- How it was verified then: Code read; the 13 readOnlyBroken incidents name only files that were 'being used by another process' at that time (Hook V3 17:52, Toparmredesign 21:03-21:29, Toparmredesignnoscrewpocket 21:33-23:10, SmallFlywheel V3 22:30-23:04) and stop as soon as the file is readable again; repro test An_unreadable_file_reports_readOnlyBroken_every_pass_though_its_bit_is_set fails at HEAD with 3 events in 3 passes while the bit is set.
- Cause: The 'never a deletion' safety rule was implemented by reusing the whole cached entry, so 'present but unreadable' is indistinguishable from 'read and unchanged'.
- Missing at 0.3.2: A per-entry 'not read this scan' flag, honored by the engine, and a forced re-hash once readable.
- 0.3.3: **In progress**. 

### X-sticky-focus

**Arrow keys move focus onto rows hidden under the pinned folder keys** (other; refs N9).

- Found at 0.3.2: **Not done**. moveInList() (app.js:2515-2536) keeps the focused row clear of the selection bar only (shade = sel-bar height + 8, else 8 px); the sticky .browser-head (app.css:3571) is ignored; back() uses scrollIntoView({block:'nearest'}) (app.js:2665) with no scroll padding.
- How it was verified then: Playwright probe uibulk-probe5.mjs on bigProject: after 40 ArrowDown then ArrowUp, rows 28 to 34 (1280x800) and 28 to 35 (420x720) are focused with their top above the head's bottom (head bottom 346 and 254 px), i.e. hidden under the keys.
- Cause: The sticky head was added in 0.3.2 without updating the list's scroll margin logic.
- Missing at 0.3.2: Scrolling that respects the pinned head.
- 0.3.3: **In progress**. 

### X-telemetry-refusals

**Incidents cannot name the refused files: refusals are never in the flight, the 200-notice cap is spent on stale ~$ markers, snapshots carry only notice counts** (other; refs 68d7a9d4, f9912084, 184c9e24, 2ab514eb).

- Found at 0.3.2: **Not done**. RefuseName/Refuse/TooLarge set st.Refusal with no flight event (Actions.cs:37-44, 215-222, 603-609); SyncEngine.cs:1378 records Notice events up to MaximumNoticesRecorded=200 per pass (Telemetry.cs:13); ReadMarkers (SyncEngine.cs:1263-1287) emits one notice per stale marker every pass; Telemetry.cs:152 snapshot notices are {kind,title,count} only.
- How it was verified then: Flight per pass on IDEA-06: exactly 200 'Armory is treating X as closed' notices every pass from 224-243 ~$ markers (Full Assembly 56, MISC/Prototypes/Francis 125, ...), zero other notices; no event names any of the 148.
- Cause: Telemetry records notices, not refusal transitions, and repeats unchanged notices each pass.
- Missing at 0.3.2: 
- 0.3.3: **In progress**. 

### X-test-doubles-blind

**Neither the end-to-end file system nor the Core checkout simulation can see this bug** (other; refs N4).

- Found at 0.3.2: **Not done**. tests/Armory.EndToEnd.Tests/PortableVaultFileSystem.cs:130-153 reads open files normally (Open only adds a marker), and OpenRead (187) never fails. tests/Armory.Core.Tests/CheckoutSimulationTests.cs:506-532 FinishCheckOut decides with the true file.Hash (518); its Save/Close model (288-304) already drops an editor buffer that cannot be saved over a read-only file, i.e. it accepts a loss when a check in releases an open file.
- How it was verified then: Code read; the repro needed a new hold model in the double before it could fail.
- Cause: The doubles model Windows' read-only bit but not its sharing violations.
- Missing at 0.3.2: A model of a file held open for writing (scan keeps the old entry, reads fail) and invariants that catch a release over unread bytes.
- 0.3.3: **In progress**. 

### X-two-computers-named-IDEA-06

**Two different computers report the device name IDEA-06** (other; refs N10, 5da43de6, 87fa2b3d, 25d938f6, 428a0f6f, 2d479aea).

- Found at 0.3.2: **Not done**. Incidents identify a computer by device_name (hostname) only.
- How it was verified then: Seraj's 0.3.1 runtime started on C:\IDEA\Armory at 20:52:03 while Abraham's agent kept syncing C:\IDEA\Armory (log lines 20:53-20:59); a second runtime on one folder is impossible because owner.lock is held FileShare.None (SafeFileReplace.cs:35). Snapshots at the same hour: Seraj 1,463-1,465 files, Abraham 1,605-1,632. Abraham's agent recorded heartbeats 22:32-22:54 while Seraj's process recorded nothing 22:00-22:52.
- Cause: Lab computers imaged with the same hostname (inference).
- Missing at 0.3.2: A stable machine id in incidents and heartbeats.
- 0.3.3: **In progress**. 

### X-two-idea-06

**IDEA-06 is two different computers with the same name** (other; refs 2d479aea, 87fa2b3d, 968cc0cd, 428a0f6f, 2ab514eb).

- Found at 0.3.2: **Not done**. Device name is the session's DeviceName (Environment.MachineName at connect); nothing tells two computers with the same name apart in check-out lines, team status or reports.
- How it was verified then: Incident logs and snapshots: Seraj's Armory 0.3.1 started a vault runtime at C:\IDEA\Armory at 20:52:03Z, signed in, 'pass: moving 267 of 1,463 files' at 20:52:47Z (snapshot files 1463, signedIn, device a0307df4); at the same time Abraham's Armory 0.3.1 ran C:\IDEA\Armory with 1,605 files ('pass: moving 142 of 1,605 files' 20:42:01Z, slowAction 20:50:38Z, sync errors on C:\IDEA\Armory paths 20:53:36Z, slowAction 21:02:23Z; device 8c26f8ac). owner.lock is opened FileShare.None (SafeFileReplace.cs:35), so two runtimes on one folder of one computer cannot both start; the file counts differ too. So these are two computers, both named IDEA-06 (both Windows 10.0.26200).
- Cause: Lab computers share a name (likely imaging).
- Missing at 0.3.2: A way to tell same-named computers apart; other auditors must not treat IDEA-06 incidents as one disk, and N10 should not be read as evidence of two Windows users sharing one folder there.
- 0.3.3: **In progress**. 

### X-two-idea06-engines

**Two engines report device IDEA-06 at the same time with separate vaults** (other; refs 87fa2b3d, 2d479aea, 5da43de6).

- Found at 0.3.2: **Not reproducible**. Seraj's engine (records 1,463, vault C:\IDEA\Armory) and Abraham's (records 1,625-1,634, vault C:\IDEA\Armory) ran concurrently. Seraj's SolidWorks held Toparmredesignnoscrewpocket 21:22-21:28 and his engine created it at 21:29:04 (480,955 B); Abraham's engine never saw it held then and downloaded those bytes at 21:29:54. state.json and the exclusive read-only.lock live in each vault's .armory (AgentHost.cs:623-629, ReadOnlyPolicy.cs:33), so the two cannot share one folder.
- How it was verified then: Per-person flight merge and logs.
- Cause: Either two PCs named IDEA-06 or a reused device name; not determinable from the data.
- Missing at 0.3.2: Nothing for N4: the mechanism is entirely within Abraham's engine. Device names alone do not identify a computer in incidents.
- 0.3.3: **In progress**. 

### X-ui-checks-coverage

**UI checks miss the N7 to N12 behaviors and do not run in CI** (other; refs N7, N8, N9, N11, N12).

- Found at 0.3.2: **Not done**. check-ui.mjs (184 pages, 44 flows, PASS) covers windowing of 5,000 rows, the manyMine box and key messages, theme wearing before the host answers, and an activity patch with bars; render-screens.mjs renders each demo state (viewport only, not scrolled); bbox-diff.mjs proves equal geometry across themes. .github/workflows has no node/Playwright step.
- How it was verified then: Read tools/agent-ui/*.mjs and the workflows; ran check-ui.mjs.
- Cause: Tests were written for the 0.3.0 design and extended by example only.
- Missing at 0.3.2: No assertions for: pinned folder keys after scrolling; keys visible at open (clicks auto-scroll); #act-log content and visibility; check-out running lines (no demo state has log-only activity); working-line counts; focus vs the pinned head; re-render count and latency on theme switch with a big view; selection of thousands.
- 0.3.3: **In progress**. 

### X-uploading-vs-saved

**Rows say Uploading while the status says Everything is saved** (other; refs N11, N12, ed7dc8f8, 809ae174, aa236105).

- Found at 0.3.2: **Not done**. StatusOf() returns Uploading for a local file the server does not have whenever online, with no regard to archived projects or whether anything will send it (SyncEngine.View.cs:440-444); Unsent() skips archived projects and refused files (SyncEngine.View.cs:59-66), so the sync line can be 'Everything is saved to Armory.' (SyncEngine.View.cs:46) while rows read 'New, uploading now' (app.js:1186).
- How it was verified then: Snapshots of N11, N12 and incident aa236105: filesByStatus {synced: 1433, uploading: 38}, sync 'Everything is saved to Armory.', pendingCount 0, with an archived project 'test' in the list. Code read. Not reproduced: which 38 files these were is not in the snapshot.
- Cause: Likely the archived project's new local files (archived rows are not drawn, app.js:1339-1354) or files without a record; needs the engine owner to confirm.
- Missing at 0.3.2: A status for 'not going to upload' (archived project, or no record) consistent with the sync line.
- 0.3.3: **In progress**. 

### X-version-40

**armory_heartbeat refuses app_version over 40; nothing holds Armory's version to 40** (other; refs ).

- Found at 0.3.2: **Not done**. AgentPaths.Version = AssemblyInformationalVersion with any +suffix removed (src/Armory.Agent/AgentPaths.cs:48-50), from <Version>0.3.2</Version> with IncludeSourceRevisionInInformationalVersion false (Armory.Agent.csproj:14-15), so today it is '0.3.2' (5 characters). TeamHeartbeat takes it unchanged (AgentHost.cs:66, TeamHeartbeat.cs:29-36) and sends it on every beat (TeamHeartbeat.cs:74-83). A 22023 refusal is logged once as 'team status: refused (22023 too_long)' and never thrown (TeamHeartbeat.cs:93-98). The uploader caps feedback and incident versions at 64, 32 when shortened (IncidentUploader.cs:48, 280-285). Server: armory_devices.app_version check 1 to 40 and armory_heartbeat raises 22023 {reason: too_long, field: app_version, limit: 40} (0233 migration lines 527-528, 1257-1266); ARMORY.md 'The two version limits do not match' proposes raising to 64, not written.
- How it was verified then: Read code; grep of tests: only V3ClientTests.cs:250 checks a heartbeat carries '0.3.0' and AgentTelemetryTests.cs:160 compares with AgentPaths.Version; no test bounds the length or format.
- Cause: The two server limits differ (40 vs 64) and the client only guards the 64 one.
- Missing at 0.3.2: A test holding AgentPaths.Version to 1 to 40 characters and a clamp in TeamHeartbeat.
- 0.3.3: **In progress**. 

### X-view-open-per-file

**Files added while open cost one Restart Manager session each on every view build and twice more per pass** (other; refs N6, 610eed69).

- Found at 0.3.2: **Not done**. MyFiles calls OpenHere -> IsOpenNow for every AutoCheckIn file (SyncEngine.View.cs:343, :369); PrepareRelease (SyncEngine.Checkout.cs:662) and DesiredOwnership (SyncEngine.Actions.cs:946) do the same per pass; outside a KnowOpen scope IsOpenNow falls to fs.IsOpen = OpenFileDetector.Inspect (one RM session + probe). Views are built up to every 500 ms during a pass (SyncEngine.cs:1445).
- How it was verified then: Reproduced in the throwaway EndToEnd test SpcAuditTests: with 20 files added while open, a quiet pass made 80 single IsOpen calls (plus 3 batches) and 20 view builds made 400 IsOpen calls (20 per view). IDEA-06 had 55 such files (610eed69 snapshot checkInWhenClosed 55).
- Cause: Views and phase D ask the platform directly instead of the pass's batch.
- Missing at 0.3.2: A per-pass open answer reused by views and phase D.
- 0.3.3: **In progress**. 

### X-wrong-words

**The window says 'Checked in' when nothing was shared, and then blames the student for saving without a check out** (other; refs N4).

- Found at 0.3.2: **Not done**. SyncEngine.Checkout.cs:242 'Checked in {name}.' for any Released outcome; SyncEngine.View.cs:192-195 kept-copy card 'Saved without a check out, so the checked-in version was put back. Your change is in its history.' (flavor forced) and history note 'Changed without a check out, kept as <name>'s own copy' (SyncEngine.View.cs:556). At 03:21 the window still showed 'Your change to Toparmredesignnoscrewpocket.SLDPRT was kept as your own copy' (184c9e24 snapshot).
- How it was verified then: Code read; repro test captured the answer 'Checked in Plate.SLDPRT.' while the server kept v1.
- Cause: The answer is derived from the release, not from what was shared; the card assumes a kept copy means the student skipped check out.
- Missing at 0.3.2: Truthful answers; a pointer to how to get the kept copy back.
- 0.3.3: **In progress**. 

### X-year-check-docs

**Docs, screens and UI text that imply the year check works (to correct)** (other; refs user statement).

- Found at 0.3.2: **Not done**. See notes_markdown section 'Year check: every statement to correct' for 26 places with quotes. Most important: docs/core/solidworks-version-gate.md:4-5 ('a missing release fails closed'), :7-8 ('Newer releases are refused with both years in the message'), :35-37 ('Enforce is for when the SolidWorks add-in stamps releases'); docs/agent/PROOF.md:33 (row h, 2026 file refused, fake reader); PROOF.md:134; docs/agent/ENGINE.md:131, :171, :365-366, :570-571, :606; docs/agent/BRIDGE.md:185, :212-213; docs/spike/saved-release.md:22 ('The core will refuse an unknown CAD release.'); src/Armory.Agent/wwwroot/demo/states.js:912-923 (groupedNotices card 'They were saved in SolidWorks 2026...', rendered into docs/agent/screens/v2 home-/detail-groupedNotices-*, README.md:67-74, 196-199); demo/states.js:483, 491-492; docs/agent/screens/README.md:11, 65-68, 86-89; SyncEngine.Actions.cs:79, :81, :439-442; SyncEngine.View.cs:232; app.js:1637, :1710; outside this repo pina-hash/idea-app/docs/ARMORY.md 'Two SolidWorks versions at once' item 4. Accurate already: ENGINE.md:26, README.md:44, docs/platform/audit.md:24, docs/server/contract.md:49-52. No release note (v0.2.0-v0.3.2) or CONTRACT.md says anything either way; INSTALL.md:6-7 states lab PCs run 2025 and laptops 2026 without warning that 2026 saves are not caught.
- How it was verified then: grep over docs/, README.md, wwwroot and engine strings; each quote checked at HEAD 0ef1ae0.
- Cause: Docs describe Core's gate and the fake-reader proof as product behavior.
- Missing at 0.3.2: 
- 0.3.3: **In progress**. 

### X-year-check-off

**The SolidWorks year check is off in the shipped app: ReleaseReader = null, so warn uploads 2026 files unchecked and enforce refuses every SolidWorks file** (other; refs user statement; no incident).

- Found at 0.3.2: **Not done**. AgentHost.cs:646-647 'ReleaseReader = null' (comment: no standalone saved-release reader exists yet). SyncEngine.cs:1312-1314 ReadReleaseAsync and Actions.cs:687-692 ReadReleaseFromSnapshotAsync return null at once. SyncEngine.cs:1071-1075 passes saved=null, pin from the project (EngineState.cs:253 default 2025), mode from ps.Enforce (SyncEngine.cs:648-649). SolidWorksRelease.cs Decide: unknown + Warn -> Allowed, ReleaseNotChecked; unknown + Enforce -> refused 'The saved SolidWorks release is unknown...'; the 'newer than the pin' branch is unreachable. Reconciler.cs:59-64 applies it; Actions.cs:653-667 the same for earlier saves (Enforce turns them into drafts). Commits send SavedRelease null (Actions.cs:187, 210, 500-501, 538-539) -> server armory_check_release (0231_armory.sql:669-684) accepts null in warn and raises 22023 in enforce -> armory_record_release stores saved_release null, release_checked false (0231_armory.sql:201-208, 686-692). Window: FileRowView/FileDetailView releaseNotChecked = remote.Current?.ReleaseChecked == false (View.cs:400, 546); app.js:1637 chip 'SolidWorks year not checked' on File detail, app.js:1710 'Year not checked' on each history entry; never on Home (tools/agent-ui/check-ui.mjs:895). Projects default to warn (0231_armory.sql:82); the website only displays release_gate_changed events (idea-app src/lib/armory/view.ts:606-608), it has no switch.
- How it was verified then: Read code and migrations; reproduced (throwaway E2E Without_a_release_reader_warn_marks_every_SolidWorks_version_and_enforce_refuses_every_one, with the app's real wiring, ReleaseReader null): warn: a part whose bytes the fake reader would read as 2026 uploads; server row 'null/false'; row and detail releaseNotChecked true, history all true; no notice. Enforce (set by RPC): check in refused 'Plate.SLDPRT can't be checked in. The SolidWorks year it was saved in is unknown, and Robot 2027 only takes files whose year Armory can check. It stays on this computer. It stays checked out by you.'; a new Gear.SLDPRT never reaches the server; card 'They were saved in a SolidWorks year the project can't take. Each one says what to do, then it uploads by itself.'
- Cause: The phase 0 spike found no validated standalone reader (docs/spike/saved-release.md:22); the gate was shipped with warn as the default instead.
- Missing at 0.3.2: Any reader. ARMORY.md rule 4 ('A 2026-format file can never slip in') is not true today: a SolidWorks 2026 save from a student laptop is uploaded and becomes the team's version that 2025 lab PCs cannot edit. The 'year not checked' tag appears on every SolidWorks file and version, so it carries no information.
- 0.3.3: **In progress**. 

### X-year-find-2026

**Can Armory find files already uploaded as SolidWorks 2026? Not today; the server stores null for every version** (other; refs user question).

- Found at 0.3.2: **Not done**. armory_version_releases (0231_armory.sql:201-208): one row per SolidWorks version or side version, saved_release null and release_checked false for everything Armory ever uploaded (no reader in any version); the table is immutable (trigger armory_version_releases_immutable, 0231_armory.sql:244-246) and keyed by version_id, written once with 'on conflict do nothing' (:686-692). Per version the server also has content_sha256, object_key (bytes retrievable), byte_length, author_email, created_at (0231_armory.sql:116-124, 139-149), and the change feed records device_id for every version and side version (0231_armory.sql:384-414, 0233:758-791) with armory_devices.name (IDEA-xx vs personal computers) and app_version (0233:527). No installed-SolidWorks release is reported per device.
- How it was verified then: Read migrations 0231-0235 and the client parse (ArmoryApi.cs:15, 219, 227).
- Cause: No reader, and the release table was designed write-once at commit time.
- Missing at 0.3.2: Any saved release per version; any way to record one later without a migration.
- 0.3.3: **In progress**. 


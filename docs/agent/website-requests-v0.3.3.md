# Website requests after Armory 0.3.3

**Not built yet.** Armory 0.3.3 works without any of them. They come from the feedback and
incidents sent from 0.3.0 and 0.3.1 (the audit is `docs/agent/feedback-audit.md`).

For the next ideabosco.com (idea-app) update pass. Migration number: the next free one after
the last one Armory uses. The binding spec stays idea-app `docs/ARMORY.md`. Add these to it as
a new contract section once they are built.

## 1. Let an instructor force a check in

Note N5 (Abraham, IDEA-06, 0.3.1): "create a checkout override for the instructor so that they
are able to check in files that different people have checked out."

Force check in is `armory_break_lock` (one file) and `armory_break_locks` (up to 500, 0234).
Both allow the caller only when `can_take_back` is true, and 0233 computes it as
`m.role in ('mentor', 'cad_lead') or v_admin`. A project member whose role is `instructor`
can't force a check in, and the app never shows them the key: it shows Force check in exactly
when the server says `can_take_back` (`armory_my_projects`).

Mr. Pina can force a check in today only because he is a `mentor` on FRC 2026 Off-Season.

- Add `instructor` to `can_take_back` in `armory_my_projects`, and to the role check of
  `armory_break_lock` and `armory_break_locks` (one shared function, so the three can never
  disagree).
- Or, if instructors are always meant to be added as mentors, say so in `docs/ARMORY.md` and
  on the website's member page, and Armory changes nothing.

Armory needs no change for either: it follows `can_take_back`.

## 2. Let a lead remove a file that has no version

When an add stops between `armory_create_file` and its first `armory_commit_version` (a stop,
a sign-in that ended, the student deleting the file meanwhile), the server keeps a file record
with `current_version_id` null. Every computer showed it as "uploading" forever (38 of them in
FRC 2026 Off-Season, on all four computers that sent incidents), and it holds its name in the
project, so a copy of the same part elsewhere can't be added.

Armory 0.3.3 shows such a file as "No first version" ("Added without its first version"), and
the computer that made the add removes the empty record by itself once its file is gone
(`armory_tombstone` with no parent, which the contract already allows). A record whose computer
never comes back (its state lost, the computer reimaged) stays.

- On the website's Files view, show a file with no version as "No first version", not as an
  ordinary file.
- Let a mentor, CAD lead or site admin remove it there (or a new
  `armory_remove_empty_file(p_file, p_operation)`: refused unless the file has no version and
  nobody holds a live lock on it; one `tombstone` change row like any removal; replay by
  operation id). Its name is then free in the project, as for any removed file.
- To find them now:

  ```sql
  select folder, name, created_at from armory_files
  where project_id = '<project id>' and deleted_at is null and current_version_id is null;
  ```

## 3. Organize files someone else has checked out

Note N5: "Allow other people to organize files while other people have those files checked
out."

The server lets only the lock holder move a file (`armory_move_file`), and refuses to rename or
delete a folder while anyone else has a file in it checked out (`armory_refuse_checked_out`,
55006), with no role allowed past. Armory 0.3.3 builds the closest safe version without
changing that: a mentor or CAD lead can Force check in the files in the way and rename or
delete in one action ("Force checked in 3 files from Maria Lopez, then renamed Gearbox to
Gearbox v2."), and a student's refusal says to ask a mentor or CAD lead.

If moving a checked-out file without ending its check out is wanted:

- Let `armory_move_file` and `armory_rename_folder` proceed over another person's lock for a
  caller with `can_take_back`, keeping the lock (it belongs to the file id, so it survives the
  move), and write the usual `file_moved` and `folder_renamed` rows. The holder's check in
  still lands by file id.
- Keep it to leads: SolidWorks assemblies find parts by path, so a move under someone's open
  assembly breaks its references until they reopen it.

## 4. Tell apart computers that share a name

Two lab computers are both named IDEA-06 (imaged alike), and every report, check out and team
status line reads the same for both. Armory 0.3.3 adds a `machineId` to its incident reports
(a short hash of Windows' own install id), and its window writes "IDEA-06 (a030)", with the
first four characters of the device id, wherever two computers it knows of share a name.

- On the website's team status and check-out lists, do the same: when two devices in a project
  share a name, show each with the first four characters of its device id.
- On the incident and feedback views, show the incident's `machineId` beside its device name.
- Ask school IT to give the two IDEA-06 computers different names (Mr. Pina's decision); the
  app needs nothing for that.

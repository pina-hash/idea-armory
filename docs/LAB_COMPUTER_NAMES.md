# Renaming the lab computers in bulk

For Mr. Pina (ledger 0434, 2026-10-23: "there's a lot of them, so ... if there's a way to do that
in bulk, that would be nice"). Lab computers copied from one Windows image all carry the image's
name, so several report as IDEA-06. Armory already tells them apart without the name (each
sign-in is its own device, and the app and the website add a 4-character device tag when two
computers share a name; docs/agent/INSTALL.md, "Computers that share a name"). Renaming them makes
every list readable at a glance.

The simplest way that works on any lab: one flash drive, one double-click per computer, one
restart. The script is `tools/lab-names/` in this repository.

## What you need

- A flash drive.
- The four files in `tools/lab-names/`: `Rename this computer.cmd`, `List this computer.cmd`,
  `Rename-LabComputer.ps1`, `names.csv`. (On GitHub: open https://github.com/pina-hash/idea-armory/tree/main/tools/lab-names,
  click each file, then the **Download raw file** button at the top right of the file.)
- An administrator account on the lab computers. Renaming a computer always needs one.

## Steps (the one-pass way: type each name at the computer)

1. Copy the four files to the top of the flash drive (for example `E:\`).
2. At the first lab computer, sign in to Windows with an administrator account (or with the lab
   account, if Windows asks for an administrator password in step 4 and you know it).
3. Open the flash drive in File Explorer and double-click **Rename this computer.cmd**.
4. Windows asks "Do you want to allow this app to make changes to your device?". Click **Yes**
   (type the administrator's password if it asks for one).
5. A blue window shows the computer's current name, serial number and network address, then
   asks for its new name and suggests one (IDEA-01 for the first computer, then IDEA-02, and so
   on). Press **Enter** to take the suggestion, or type the name you want (for example the
   number on the computer's label, `IDEA-07`) and press **Enter**.
6. It says "Renamed IDEA-06 to IDEA-07" and restarts the computer 10 seconds later. Take the
   flash drive out once the screen goes dark.
7. Go to the next computer and repeat steps 2 to 6. The suggestion counts up by itself, because
   every rename is written to `names-done.csv` on the drive. It refuses a name the drive already
   gave to a different computer.
8. When the last one is done, `names-done.csv` on the drive lists every computer: date, serial
   number, network address, old name and new name. Keep it; it is how you find a computer by
   its serial number later.

## Faster when you know the serial numbers (no typing at each computer)

If the school's asset list has each computer's serial number (or you collected them with
**List this computer.cmd**, which records a computer and renames nothing), fill in `names.csv`
before you start, one line per computer:

```
Serial,MAC,NewName
5CD1234ABC,,IDEA-01
5CD1234ABD,,IDEA-02
,B4-2E-99-00-00-03,IDEA-03
```

Either the serial number or the network address is enough. Open the file in Notepad or Excel
and save it as CSV. Then steps 2 to 6 above are a double-click, **Yes**, and nothing typed: the
script finds the computer on the list, renames it and restarts it.

## After renaming

- **Armory:** a computer's name is sent to the website when a student signs in to Armory, so the
  website keeps the old name for that sign-in until the student signs in again. To update it at
  once on a computer: open Armory, **Settings**, **Sign out**, then **Connect this computer**.
  Nothing else in Armory changes: the files, check outs and the Armory folder stay as they are.
  Settings' "About this Armory" shows the computer's new name straight away.
- **Check one:** on any renamed computer, open Start, type `about`, open **About your PC**: the
  **Device name** is the new name.

## Other ways, and when they fit

- **One at a time with no script:** Start, Settings, System, About, **Rename this PC**, type the
  name, Next, Restart now. The same result, with more clicks per computer.
- **From your own desk, if the lab is joined to the school's domain and remote management is on:**
  in PowerShell as an administrator, `Rename-Computer -ComputerName 10.0.0.57 -NewName IDEA-07
  -DomainCredential SCHOOL\admin -Restart`, once per computer. It has to name each computer by its
  network address, not by its name, because the copies all share one name. Most labs that were
  copied from one image without Sysprep cannot be on the domain under one name anyway, so check
  this before relying on it.

## What could not be checked from here

- **Whether the lab computers are joined to a domain.** The script handles both: on a domain it
  asks for a domain account that may rename computers (the school's IT office has one); off a
  domain it needs only a local administrator.
- **Which administrator account the lab has, and its password.** Without one, Windows refuses
  the rename, and the script says so and changes nothing.
- **Whether the lab undoes changes at restart** (Deep Freeze, Reboot Restore or a similar
  program). If it does, the new name disappears at the restart: thaw the computer first, rename
  it, then freeze it again.
- **Whether the flash drive is allowed to run files.** If Windows blocks it ("This app has been
  blocked by your system administrator"), copy the four files to the computer's Desktop and run
  them from there; `names-done.csv` is then written on that Desktop, so copy it back.
- **Whether the images were made with Sysprep.** Copies made without it also share Windows' own
  install id. Renaming does not change that, and Armory does not need it to: its problem reports
  tell such computers apart by their network hardware from 0.4.3.

The script was run on a Windows CI runner in its two modes that change nothing (`-ListOnly`,
`-DryRun`: it finds the runner by serial number and by network address, and refuses a taken name
and a name Windows would refuse; `tools/test-lab-names.ps1`). It has not renamed a real lab
computer.

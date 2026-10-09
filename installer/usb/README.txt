IDEA Armory __VERSION__ - flash drive installer
=========================================

IDEA Armory keeps the team's and the class's CAD files in one shared vault,
the folder C:\IDEA\Armory. It runs in the tray, near the clock.
Installing it needs no administrator password and no internet.


A. PUT IT ON THE FLASH DRIVE (once)
-----------------------------------
1. Copy IDEA-Armory-USB-v__VERSION__.zip onto the flash drive.
2. Right-click the ZIP on the flash drive. Click "Extract All...".
3. Click "Extract". A folder named IDEA-Armory-USB-v__VERSION__ appears.
   Open it. You should see:
      Install IDEA Armory.cmd
      Uninstall IDEA Armory.cmd
      Check IDEA Armory.cmd
      Show Armory status on file icons.cmd   (optional, see G)
      README.txt
      files   (folder)
      logs    (folder)
   Do not run anything from inside the ZIP itself.


B. INSTALL ON ONE COMPUTER (about a minute)
-------------------------------------------
1. Sign in to Windows with the account that will use Armory.
2. Plug in the flash drive. Open the IDEA-Armory-USB-v__VERSION__ folder.
3. Double-click "Install IDEA Armory.cmd".
   No password is needed. Do not choose "Run as administrator".
   (If Windows asks whether to run the file, click Run.)
4. Wait for a large green line that starts with PASS.
   Press any key to close the window.
   - A red line that starts with FAIL tells you why. Fix that one thing
     and double-click Install again.
5. Armory is now running (a small icon near the clock). It starts by
   itself every time this Windows account signs in.

Running Install again is safe. It also upgrades an older version.
Install never changes the files in C:\IDEA\Armory.


C. ONE RUN FOR EACH WINDOWS ACCOUNT
-----------------------------------
Armory installs for one Windows account at a time: the one that is signed
in when you double-click Install.
- If everyone on a lab computer signs in with one shared account,
  one run on that computer is enough.
- If students sign in with their own accounts, each account that will use
  Armory needs one run, signed in as that account.
- C:\IDEA\Armory is shared by every account on the computer. Armory syncs
  it for one Armory account at a time. If another person's account already
  uses it, Armory says so and does not sync.


D. CONNECT (first time, for each account)
-----------------------------------------
1. Open IDEA Armory from the Start menu.
2. Click Connect. The browser opens ideabosco.com.
3. Sign in there, then confirm. Armory starts syncing.


E. COUNT THE COMPUTERS YOU HAVE DONE
------------------------------------
Open the "logs" folder on the flash drive. Each computer gets its own file,
named after the computer, with one line for every run: the date, the
Windows account, the version, PASS or FAIL, and the seconds it took.


F. CHECK OR REMOVE
------------------
- "Check IDEA Armory.cmd" shows the installed version, whether it starts
  at sign-in, the WebView2 part Windows needs to show the Armory window,
  the vault folder, Armory's items on File Explorer's right-click menu,
  and whether file icons show Armory's status. It changes nothing.
- "Uninstall IDEA Armory.cmd" removes Armory, its settings and this
  computer's sign-in from the Windows account that runs it. It never
  deletes C:\IDEA\Armory or any file in it. Armory can also be removed
  from Settings > Apps > IDEA Armory.


G. OPTIONAL: ARMORY'S STATUS ON FILE ICONS (once per computer)
---------------------------------------------------------------
Armory can mark each file's icon in File Explorer: synced, checked out by
you, checked out by someone else, or needs attention. This one step needs
an administrator's password, once for the whole computer, and works for
every Windows account on it. Everything else in this folder needs none.
1. Double-click "Show Armory status on file icons.cmd".
2. Windows asks for an administrator's password. Type it, or ask someone
   who has one.
3. Wait for the green PASS line. Each person sees the marks after signing
   out of Windows and back in.
   - Without the password, the red FAIL line says nothing changed.
     Armory works the same without the marks.
IT can run the same setup silently on each computer instead:
   files\badges\IDEA-Armory-Badges-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
To remove it: Settings > Apps > "IDEA Armory badges (status on file icons)".
Uninstalling Armory for one account leaves it for the others.

@echo off
rem IDEA Armory: the installed uninstaller (Settings > Apps > IDEA Armory > Uninstall).
rem It never deletes the vault folder (C:\IDEA\Armory) or any file in it.
rem /quiet: no pause at the end (QuietUninstallString).
rem Setup runs from a temporary copy because the uninstall deletes this folder. The last
rem lines are one block, which Windows reads whole before this file is deleted.
setlocal
set "PAUSEIT=1"
if /i "%~1"=="/quiet" set "PAUSEIT="
set "TMPSETUP=%TEMP%\IdeaArmorySetup-%RANDOM%%RANDOM%.ps1"
copy /y "%~dp0Setup.ps1" "%TMPSETUP%" >nul
if errorlevel 1 (
  echo   FAIL   Could not copy the uninstaller to the TEMP folder. Nothing was changed.
  if defined PAUSEIT pause
  exit /b 1
)
cd /d "%TEMP%"
setlocal EnableDelayedExpansion
(
  "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "!TMPSETUP!" -Mode Uninstall
  set "RESULT=!errorlevel!"
  del "!TMPSETUP!" >nul 2>&1
  if defined PAUSEIT pause
  exit /b !RESULT!
)

@echo off
rem IDEA Armory __VERSION__: checks the installation for the Windows account that runs it.
rem It changes nothing. Keep this file next to the "files" and "logs" folders.
rem /quiet: no pause at the end (for scripts and CI).
setlocal
set "HERE=%~dp0"
set "QUIET="
if /i "%~1"=="/quiet" set "QUIET=1"
if not exist "%HERE%files\scripts\Setup.ps1" (
  echo.
  echo ================================================================
  echo    FAIL   The "files" folder is not next to this file, so nothing was changed.
  echo           Right-click the ZIP, choose Extract All, and run this file from the new folder.
  echo ================================================================
  echo.
  if not defined QUIET pause
  exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%HERE%files\scripts\Setup.ps1" -Mode Check -Source "%HERE%files" -LogDir "%HERE%logs"
set "RESULT=%errorlevel%"
if not defined QUIET pause
exit /b %RESULT%

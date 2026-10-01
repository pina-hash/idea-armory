@echo off
rem IDEA Armory: the installed copy of Check. It changes nothing.
rem /quiet: no pause at the end.
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" -Mode Check
set "RESULT=%errorlevel%"
if /i not "%~1"=="/quiet" pause
exit /b %RESULT%

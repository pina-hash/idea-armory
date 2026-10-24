@echo off
rem Records this computer's serial number, network address and name in names-done.csv on this
rem flash drive, and renames nothing (docs/LAB_COMPUTER_NAMES.md, "Collecting serial numbers first").
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Rename-LabComputer.ps1" -ListOnly

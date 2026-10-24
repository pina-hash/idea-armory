@echo off
rem Renames this lab computer from names.csv on this flash drive (docs/LAB_COMPUTER_NAMES.md).
rem Windows asks "Do you want to allow this app to make changes?": click Yes.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0Rename-LabComputer.ps1\"'"

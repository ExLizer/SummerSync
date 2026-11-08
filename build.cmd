@echo off
rem Runs build.ps1 without requiring a change to the PowerShell execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*

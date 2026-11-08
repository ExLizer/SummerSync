@echo off
rem Starts a second copy of the game on this PC. See second-instance.ps1 for what it needs first.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0second-instance.ps1" %*
exit /b %ERRORLEVEL%

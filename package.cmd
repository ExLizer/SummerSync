@echo off
rem Builds the zip to send to another player. See package.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" %*
exit /b %ERRORLEVEL%

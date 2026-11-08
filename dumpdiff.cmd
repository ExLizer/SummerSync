@echo off
rem Runs the dump tool built by build.cmd. Example: dumpdiff diff before.json after.json
set "TOOL=%~dp0tools\SummerSync.DumpDiff\bin\Release\net10.0\dumpdiff.dll"
if not exist "%TOOL%" (
    echo dumpdiff has not been built yet. Run build.cmd first.
    exit /b 1
)
dotnet "%TOOL%" %*

@echo off
rem Runs the network probe built by build.cmd. Example: netprobe udpjoin --game-dir "C:\Program Files (x86)\Steam\steamapps\common\My Summer Car"
set "TOOL=%~dp0tools\SummerSync.NetProbe\bin\Release\net10.0\netprobe.dll"
if not exist "%TOOL%" (
    echo netprobe has not been built yet. Run build.cmd first.
    exit /b 1
)
dotnet "%TOOL%" %*

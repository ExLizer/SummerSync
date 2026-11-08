<#
.SYNOPSIS
    Starts a second copy of My Summer Car on this PC, next to the one started from Steam, so that two
    players can be tested without a second machine.

.DESCRIPTION
    The game is built with Unity's "Force Single Instance": a second copy normally exits at once. MSCLoader's
    preloader can switch that off with its -mscloader-sipatch launch option, which every copy must be started
    with (a copy started without it switches the protection back on).

    Before running this:
      1. In Steam, open the game's Properties and put  -mscloader-sipatch  in Launch Options.
      2. Start the game from Steam as usual and wait for the main menu.

    This script then starts the second copy with the same option, and with the Steam application id in its
    environment so that Steam also works in it. Nothing in the game folder is modified by this script.

.PARAMETER Force
    Start the copy even if the checks say it will not work.

.EXAMPLE
    .\second-instance.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'

$steamAppId = '516750'
$launchOption = '-mscloader-sipatch'
$processName = 'mysummercar'
$modProject = Join-Path $PSScriptRoot 'src\SummerSync.Mod\SummerSync.Mod.csproj'

$gamePath = (dotnet msbuild $modProject -getProperty:GamePath | Out-String).Trim()
$exe = Join-Path $gamePath "$processName.exe"
if ([string]::IsNullOrEmpty($gamePath) -or -not (Test-Path $exe)) {
    throw "The game was not found at '$gamePath'. Check GamePath in GamePaths.user.props."
}

# Reads the "Force Single Instance" player setting. The position, relative to the company and product
# names stored in mainData, is the one MSCLoader's preloader patches. Returns $null if it cannot tell.
function Get-SingleInstanceSetting {
    param([string]$GameFolder)

    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $GameFolder 'mysummercar_Data\mainData'))
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $marker = 'Amistech' + [char]13 + [char]0 + [char]0 + [char]0 + 'My Summer Car'
    $at = $text.IndexOf($marker, [System.StringComparison]::Ordinal)
    if ($at -lt 0) {
        return $null
    }

    return $bytes[$at + 117] -eq 1
}

$problems = @()
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) {
    $problems += "The game is not running. Start it from Steam first, with $launchOption in its Launch Options, and wait for the main menu."
}

$singleInstance = Get-SingleInstanceSetting -GameFolder $gamePath
if ($null -eq $singleInstance) {
    Write-Host 'Could not read the single-instance setting from mainData; trying anyway.' -ForegroundColor Yellow
}
elseif ($singleInstance) {
    $problems += "The game still allows only one copy, so it was last started without $launchOption. Add that in Steam (Properties > Launch Options) and start the game again."
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
    if (-not $Force) {
        Write-Host 'Nothing was started. Use -Force to try anyway.'
        exit 1
    }
}

# Steam gives these to the games it starts; with them a copy started by hand can use Steam too.
$env:SteamAppId = $steamAppId
$env:SteamGameId = $steamAppId

# The working directory matters: MSCLoader reads doorstop_config.ini and mainData relative to it.
Start-Process -FilePath $exe -ArgumentList $launchOption -WorkingDirectory $gamePath
Write-Host "Started a second copy with $launchOption. Waiting to see whether it stays open..."

# Unity's configuration window counts as open, so this only detects a copy that quit straight away.
Start-Sleep -Seconds 12
$now = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($now.Count -gt $running.Count) {
    Write-Host "Two copies are running ($($now.Count) processes). In the first: Host on local UDP. In the second: Join local UDP." -ForegroundColor Green
    Write-Host 'The second copy logs to SummerSync.2.log. Do not save the game in the second copy.'
}
else {
    Write-Host 'The second copy did not stay open.' -ForegroundColor Yellow
    Write-Host "See the end of MSCLoader_Preloader.txt in the game folder: it says whether $launchOption was applied."
    exit 1
}

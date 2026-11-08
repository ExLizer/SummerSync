<#
.SYNOPSIS
    Builds SummerSync, runs the tests, checks the mod assemblies against the game's own assemblies
    and copies the mod to the MSCLoader Mods folder.

.DESCRIPTION
    The game location comes from GamePaths.user.props (see GamePaths.user.props.example).
    Nothing is deployed unless the build, the tests and the reference check all succeed.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER NoTest
    Skip the unit tests.

.PARAMETER NoDeploy
    Build and verify only; do not copy anything to the Mods folder.

.PARAMETER WithDumper
    Also deploy SummerSyncDumper.dll.

.PARAMETER WithBridge
    Also deploy SummerSyncBridge.dll, the test bridge (tools\bridge). Development machines only.

.PARAMETER Public
    Build the public edition of the mod (what publicar.cmd publishes): test aids for the host only, the players' own
    settings only, the overlay off at first, and only a notice of a newer build. The developers' edition otherwise.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -NoDeploy
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$NoTest,
    [switch]$NoDeploy,
    [switch]$WithDumper,
    [switch]$WithBridge,
    [switch]$Public
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$root = $PSScriptRoot
$solution = Join-Path $root 'SummerSync.sln'
$modProject = Join-Path $root 'src\SummerSync.Mod\SummerSync.Mod.csproj'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)

    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE)."
    }
}

# Ask MSBuild for the paths so the script and the projects can never disagree.
$evaluated = dotnet msbuild $modProject -getProperty:GamePath -getProperty:ManagedPath -getProperty:ModsPath | ConvertFrom-Json
$gamePath = $evaluated.Properties.GamePath
$managedPath = $evaluated.Properties.ManagedPath
$modsPath = $evaluated.Properties.ModsPath
if ([string]::IsNullOrEmpty($gamePath)) {
    throw 'GamePath is not set. Copy GamePaths.user.props.example to GamePaths.user.props and point it at your My Summer Car installation.'
}

$modDll = Join-Path $root "src\SummerSync.Mod\bin\$Configuration\net35\SummerSync.dll"
$dumperDll = Join-Path $root "src\SummerSync.Dumper\bin\$Configuration\net35\SummerSyncDumper.dll"
$bridgeDll = Join-Path $root "src\SummerSync.Bridge\bin\$Configuration\net35\SummerSyncBridge.dll"
$coreDll = Join-Path $root "src\SummerSync.Core\bin\$Configuration\net35\SummerSync.Core.dll"
$asmInspect = Join-Path $root "tools\SummerSync.AsmInspect\bin\$Configuration\net10.0\asminspect.dll"

$edition = if ($Public) { 'Public' } else { 'Developer' }
Invoke-Step "Build ($edition edition)" {
    dotnet build $solution -c $Configuration --nologo -v minimal "-p:SummerSyncEdition=$edition"
}

if (-not $NoTest) {
    Invoke-Step 'Test' {
        dotnet test $solution -c $Configuration --no-build --nologo -v minimal
    }
}

# Our assemblies compile against the .NET 3.5 reference assemblies; the game ships a smaller BCL.
Invoke-Step 'Verify references against the game' {
    $verified = @($coreDll, $modDll, $dumperDll)
    if (Test-Path $bridgeDll) {
        # The test bridge is a development project that not every checkout has.
        $verified += $bridgeDll
    }

    dotnet $asmInspect verify-refs --against $managedPath @verified
}

if ($NoDeploy) {
    Write-Host 'Done (not deployed).' -ForegroundColor Green
    return
}

Invoke-Step 'Deploy' {
    if (-not (Test-Path $modsPath)) {
        throw "Mods folder '$modsPath' does not exist. Install MSCLoader first, or set ModsPath in GamePaths.user.props."
    }

    $files = @($modDll)
    if ($WithDumper) {
        $files += $dumperDll
    }

    if ($WithBridge) {
        $files += $bridgeDll
    }

    foreach ($file in $files) {
        try {
            Copy-Item -Path $file -Destination $modsPath -Force
        }
        catch [System.IO.IOException] {
            throw "Could not copy '$(Split-Path $file -Leaf)' to '$modsPath'. Close the game and run the build again. ($($_.Exception.Message))"
        }

        Write-Host "    $(Split-Path $file -Leaf) -> $modsPath"
    }
}

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($modDll).ProductVersion
Write-Host "Done. SummerSync $version ($edition edition) deployed." -ForegroundColor Green

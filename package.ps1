<#
.SYNOPSIS
    Builds the zip that is published: the mod exactly as it is deployed in the local game, and the instructions in
    English (README.txt) and Spanish (LEEME.txt). MSCLoader is not in it: each player installs it on their own.
    Neither is the updater (actualizar.cmd): the public edition only says there is a newer build
    and where to get it; the updaters already installed keep working, since the zip keeps its "2-Mod" folder.

.DESCRIPTION
    The mod is taken from the game's Mods folder, not from the build output, so the other player gets the
    very file this machine runs. Run build.cmd first if the game should get a newer build.

    The host's world reaches the guests through the mod itself (HostWorld). With -WithSave the package also
    carries this machine's save and the scripts that swap it in and out, the way it was done before (developers
    only: never published).

.PARAMETER WithSave
    Put this machine's save in the package (folder 3-Partida), with the scripts to use it.

    The result goes to dist\, which is not committed.

.EXAMPLE
    .\package.ps1
#>
[CmdletBinding()]
param(
    [switch]$WithSave
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'

$unityVersion = '5.0.0f4'

$root = $PSScriptRoot
$modProject = Join-Path $root 'src\SummerSync.Mod\SummerSync.Mod.csproj'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$evaluated = dotnet msbuild $modProject -getProperty:ManagedPath -getProperty:ModsPath | ConvertFrom-Json
$managedPath = $evaluated.Properties.ManagedPath
$modsPath = $evaluated.Properties.ModsPath

# The mod, as deployed.
$deployed = Join-Path $modsPath 'SummerSync.dll'
if (-not (Test-Path $deployed)) {
    throw "There is no SummerSync.dll in '$modsPath'. Run build.cmd first."
}

$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($deployed).ProductVersion
$plus = $productVersion.IndexOf('+')
if ($plus -ge 0 -and $productVersion.Length -gt $plus + 9) {
    # Same short form the overlay shows.
    $productVersion = $productVersion.Substring(0, $plus + 9)
}

$built = Join-Path $root 'src\SummerSync.Mod\bin\Release\net35\SummerSync.dll'
if ((Test-Path $built) -and (Get-Sha256 $built) -ne (Get-Sha256 $deployed)) {
    Write-Host 'Note: the game has a different build than the last one compiled here. Packaging the one in the game.' -ForegroundColor Yellow
}

# The build of the game both players must share, computed the way the mod does. The Core assembly is loaded
# from memory so that this process does not keep the file locked.
$coreDll = Join-Path $root 'src\SummerSync.Core\bin\Release\net35\SummerSync.Core.dll'
if (-not (Test-Path $coreDll)) {
    throw 'SummerSync.Core has not been built yet. Run build.cmd first.'
}

$core = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($coreDll))
$gameBuildType = $core.GetType('SummerSync.Core.Net.GameBuild')
$mainAssembly = [System.IO.File]::ReadAllBytes((Join-Path $managedPath 'Assembly-CSharp.dll'))
$gameBuild = $gameBuildType.GetMethod('Identify').Invoke($null, @($unityVersion, $mainAssembly))

# Stage and zip. The folders keep the names the updaters already installed look for ("2-Mod", "3-Partida").
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $stage '2-Mod') | Out-Null
Copy-Item -Path $deployed -Destination (Join-Path $stage '2-Mod')

# Batch files need Windows line endings and no byte order mark, whatever git checked out.
function Write-Batch {
    param([string]$Source, [string]$Destination)
    $text = [System.IO.File]::ReadAllText($Source, [System.Text.Encoding]::UTF8)
    $text = $text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText($Destination, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$savedFiles = @()
if ($WithSave) {
    # This machine's save: the three files that hold the world. Options and trophies are each player's own.
    $saveFolder = Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'
    $saveStage = Join-Path $stage '3-Partida'
    New-Item -ItemType Directory -Force -Path $saveStage | Out-Null
    foreach ($name in 'defaultES2File.txt', 'items.txt', 'meshsave.txt') {
        $source = Join-Path $saveFolder $name
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination $saveStage
            $savedFiles += $name
        }
    }

    if ($savedFiles -notcontains 'defaultES2File.txt') {
        throw "There is no save in '$saveFolder' (defaultES2File.txt is missing). Save a game first."
    }

    foreach ($script in Get-ChildItem -Path (Join-Path $root 'packaging\partida') -Filter '*.cmd') {
        Write-Batch $script.FullName (Join-Path $saveStage $script.Name)
    }
}

# The instructions, with Windows line endings and a byte order mark, so that any version of Notepad shows the accents.
foreach ($name in 'README.txt', 'LEEME.txt') {
    $readme = [System.IO.File]::ReadAllText((Join-Path $root "packaging\$name"), [System.Text.Encoding]::UTF8)
    $readme = $readme.Replace('{MOD_VERSION}', $productVersion).Replace('{GAME_BUILD}', $gameBuild)
    $readme = $readme.Replace("`r`n", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText((Join-Path $stage $name), $readme, (New-Object System.Text.UTF8Encoding($true)))
}

# No '+' in the file name: some chat programs mangle it when the file is shared.
$zip = Join-Path $dist "SummerSync-$($productVersion.Replace('+', '-')).zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

# Written entry by entry with '/' in the names. Compress-Archive in Windows PowerShell stores '\', which
# Explorer accepts but other unzip programs complain about.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -Path $stage -Recurse -File) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $level = [System.IO.Compression.CompressionLevel]::Optimal
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, $level) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)" -ForegroundColor Green
Write-Host "  mod $productVersion, game $gameBuild (MSCLoader not included: each player installs it)"
if ($WithSave) {
    Write-Host "  save: $($savedFiles -join ', ') as on disk now. Do not save in the game before playing, or send the package again."
}
else {
    Write-Host "  no save: the mod brings the host's world (package.ps1 -WithSave puts this machine's in)."
}

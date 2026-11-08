<#
.SYNOPSIS
    Runs the two save scripts of the package against a made-up save folder and checks what they leave behind.

.DESCRIPTION
    The scripts in packaging\partida run on another player's machine and move their save around: they are
    tried here first. Nothing touches the real save; SUMMERSYNC_SAVE_DIR points the scripts at a folder made
    for the occasion, whose path has parentheses, spaces and an ampersand, as a Windows user name can have.

    Ends with "ALL OK" and exit code 0, or with the number of failed checks and exit code 1.

.EXAMPLE
    .\tools\Test-SaveScripts.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scripts = Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\partida'
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) 'SummerSync save test (Ana & co)'
if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
$save = Join-Path $sandbox 'My Summer Car'
$package = Join-Path $sandbox 'zip\3-Partida'
$backup = "$save - mi partida SummerSync"
New-Item -ItemType Directory -Force -Path $save, $package | Out-Null

# The scripts as the package ships them: with Windows line endings and no byte order mark.
foreach ($name in '1-usar-la-partida-de-mi-amigo.cmd', '2-volver-a-mi-partida.cmd') {
    $text = [System.IO.File]::ReadAllText((Join-Path $scripts $name), [System.Text.Encoding]::UTF8)
    $text = $text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    [System.IO.File]::WriteAllText((Join-Path $package $name), $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-Files($folder, $files) {
    foreach ($pair in $files.GetEnumerator()) { Set-Content -Path (Join-Path $folder $pair.Key) -Value $pair.Value -NoNewline -Encoding ASCII }
}

function Read-Files($folder) {
    $result = [ordered]@{}
    Get-ChildItem $folder -File | Sort-Object Name | ForEach-Object { $result[$_.Name] = Get-Content $_.FullName -Raw }
    return ($result.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; '
}

function Run($script) {
    $env:SUMMERSYNC_SAVE_DIR = $save
    $output = & cmd.exe /c "`"$(Join-Path $package $script)`" < nul" 2>&1
    $code = $LASTEXITCODE
    Remove-Item Env:\SUMMERSYNC_SAVE_DIR
    return [pscustomobject]@{ Code = $code; Text = ($output -join "`n") }
}

$failures = 0
function Check($what, $condition) {
    if ($condition) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ }
}

$mine = [ordered]@{ 'defaultES2File.txt' = 'MINE-main'; 'items.txt' = 'MINE-items'; 'meshsave.txt' = 'MINE-mesh'; 'options.txt' = 'MINE-options'; 'trophies.txt' = 'MINE-trophies' }
Write-Files $save $mine
Write-Files $package ([ordered]@{ 'defaultES2File.txt' = 'HOST-main'; 'items.txt' = 'HOST-items' })
$mineBefore = Read-Files $save

'1. First use: my save is kept, the host''s is put in place'
$run = Run '1-usar-la-partida-de-mi-amigo.cmd'
Check 'exit code 0' ($run.Code -eq 0)
Check 'backup holds my five files' ((Read-Files $backup) -eq $mineBefore)
Check 'save has the host''s files, my options, and no mesh of mine' ((Read-Files $save) -eq 'defaultES2File.txt=HOST-main; items.txt=HOST-items; options.txt=MINE-options; trophies.txt=MINE-trophies')

# Built from a character code: Windows PowerShell reads this file as ANSI, and a literal accent would not survive.
Check 'accents come out right' ($run.Text.Contains('Abr' + [char]237 + ' el juego desde Steam'))

'2. Second use with a newer host save: the backup is not touched'
Write-Files $package ([ordered]@{ 'defaultES2File.txt' = 'HOST-main-2'; 'items.txt' = 'HOST-items-2'; 'meshsave.txt' = 'HOST-mesh-2' })
$run = Run '1-usar-la-partida-de-mi-amigo.cmd'
Check 'exit code 0' ($run.Code -eq 0)
Check 'backup still holds my five files' ((Read-Files $backup) -eq $mineBefore)
Check 'save has the newer host files' ((Read-Files $save) -eq 'defaultES2File.txt=HOST-main-2; items.txt=HOST-items-2; meshsave.txt=HOST-mesh-2; options.txt=MINE-options; trophies.txt=MINE-trophies')

'3. Going back: my save is exactly as it was, and the backup is gone'
$run = Run '2-volver-a-mi-partida.cmd'
Check 'exit code 0' ($run.Code -eq 0)
Check 'save is mine again' ((Read-Files $save) -eq $mineBefore)
Check 'backup folder removed' (-not (Test-Path $backup))

'4. Going back twice: refused, nothing changed'
$run = Run '2-volver-a-mi-partida.cmd'
Check 'exit code 1' ($run.Code -eq 1)
Check 'save is still mine' ((Read-Files $save) -eq $mineBefore)

'5. The package without the host''s save: refused, nothing changed'
Remove-Item (Join-Path $package 'defaultES2File.txt')
$run = Run '1-usar-la-partida-de-mi-amigo.cmd'
Check 'exit code 1' ($run.Code -eq 1)
Check 'save is still mine' ((Read-Files $save) -eq $mineBefore)
Check 'no backup was made' (-not (Test-Path $backup))

'6. A player who has never saved: an empty save folder'
Write-Files $package ([ordered]@{ 'defaultES2File.txt' = 'HOST-main'; 'items.txt' = 'HOST-items' })
Remove-Item (Join-Path $package 'meshsave.txt')
Get-ChildItem $save -File | Remove-Item
$run = Run '1-usar-la-partida-de-mi-amigo.cmd'
Check 'exit code 0' ($run.Code -eq 0)
Check 'save has the host''s files' ((Read-Files $save) -eq 'defaultES2File.txt=HOST-main; items.txt=HOST-items')
$run = Run '2-volver-a-mi-partida.cmd'
Check 'going back: exit code 0' ($run.Code -eq 0)
Check 'going back: the folder is empty again' (@(Get-ChildItem $save -File).Count -eq 0)

Remove-Item $sandbox -Recurse -Force
if ($failures -eq 0) {
    'ALL OK'
    exit 0
}

"$failures FAILED"
exit 1

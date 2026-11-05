<#
.SYNOPSIS
    Removes CMS21 Together+ from Car Mechanic Simulator 2021. Saves are kept.

.DESCRIPTION
    Removes the mod's files from Mods and UserLibs and asks whether to remove MelonLoader as well. Saves (the game's and
    Together+'s, under AppData\LocalLow) and UserData settings are never touched.
#>
[CmdletBinding()]
param(
    # The game folder. Found through Steam when omitted.
    [string]$GamePath,

    # Also remove MelonLoader (without asking).
    [switch]$RemoveMelonLoader,

    # Keep MelonLoader (without asking).
    [switch]$KeepMelonLoader
)

$ErrorActionPreference = 'Stop'
$GameExe = 'Car Mechanic Simulator 2021.exe'

function Find-Game {
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $item = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($item -and $item.SteamPath) { $steam = $item.SteamPath; break }
        if ($item -and $item.InstallPath) { $steam = $item.InstallPath; break }
    }
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $current = $null
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"(.+)"') { $current = $Matches[1] -replace '\\\\', '\' }
            if ($line -match '"1190000"' -and $current) { $libraries = @($current) + $libraries }
        }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\Car Mechanic Simulator 2021'
        if (Test-Path (Join-Path $candidate $GameExe)) { return $candidate }
    }
    return $null
}

if (-not $GamePath) { $GamePath = Find-Game }
if (-not $GamePath) { $GamePath = Read-Host 'Car Mechanic Simulator 2021 was not found. Paste the game folder' }
$GamePath = $GamePath.Trim('"', ' ')
if (-not (Test-Path (Join-Path $GamePath $GameExe))) { throw "'$GamePath' is not the game folder: '$GameExe' is missing." }
$GamePath = (Resolve-Path $GamePath).Path
if (Get-Process -Name 'Car Mechanic Simulator 2021' -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path.StartsWith($GamePath, [StringComparison]::OrdinalIgnoreCase) }) { throw 'The game is running. Close it first.' }

$removed = 0
foreach ($file in 'Mods\CMS21-Together.dll', 'Mods\CMS21-Together.version', 'Mods\TogetherPlus-README.md', 'Mods\TogetherPlus-NOTICE.md',
                  'Plugins\TogetherPlus.GcFix.dll', 'Plugins\TogetherPlus.Updater.dll',
                  'UserLibs\Facepunch.Steamworks.Win64.dll', 'UserLibs\steam_api64.dll') {
    $path = Join-Path $GamePath $file
    if (Test-Path $path) { Remove-Item $path -Force; $removed++; Write-Host "Removed $file" }
}
Write-Host "Together+ removed ($removed files). Saves and settings were kept." -ForegroundColor Green
if (Test-Path (Join-Path $GamePath 'Mods\_disabled')) {
    Write-Host 'Mods moved aside by the installer are still in Mods\_disabled (move them back to use them).'
}

$melon = Join-Path $GamePath 'MelonLoader'
if (Test-Path $melon) {
    $remove = $RemoveMelonLoader
    if (-not $RemoveMelonLoader -and -not $KeepMelonLoader) {
        $answer = Read-Host 'Also remove MelonLoader (other mods need it)? [y/N]'
        $remove = $answer -match '^(y|yes)$'
    }
    if ($remove) {
        Remove-Item $melon -Recurse -Force
        foreach ($file in 'version.dll', 'NOTICE.txt') {
            $path = Join-Path $GamePath $file
            if ((Test-Path $path) -and ($file -eq 'version.dll' -or (Get-Content $path -Raw) -match 'MelonLoader')) { Remove-Item $path -Force }
        }
        Write-Host 'MelonLoader removed. The Mods, UserLibs and UserData folders were kept.' -ForegroundColor Green
    }
}
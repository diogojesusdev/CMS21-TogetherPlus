<#
.SYNOPSIS
    Installs or updates CMS21 Together+ (multiplayer mod for Car Mechanic Simulator 2021).

.DESCRIPTION
    Finds the game through Steam, installs MelonLoader 0.5.7 if needed (official release, SHA-512 checked), and copies the
    mod into the game folder. Running it again updates the mod. Saves are never touched.

.EXAMPLE
    irm https://raw.githubusercontent.com/diogojesusdev/CMS21-TogetherPlus/main/install.ps1 | iex
    .\install.ps1 -GamePath 'D:\Steam\steamapps\common\Car Mechanic Simulator 2021'
#>
[CmdletBinding()]
param(
    # The game folder. Found through Steam when omitted.
    [string]$GamePath,

    # A TogetherPlus-<version>.zip to install instead of downloading the newest release.
    [string]$Zip,

    # A MelonLoader.x64.zip (0.5.7) to use instead of downloading it. Its SHA-512 is still checked.
    [string]$MelonLoaderZip,

    # Never ask anything (fails instead of prompting).
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = 'diogojesusdev/CMS21-TogetherPlus'
$GameExe = 'Car Mechanic Simulator 2021.exe'
$SteamAppId = '1190000'
$MelonVersion = '0.5.7'
$MelonUrl = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip'
# Official SHA-512 of MelonLoader.x64.zip v0.5.7 (MelonLoader.x64.sha512 of the LavaGang release).
$MelonSha512 = 'A4A41CB6EAE1D927FFA835D24634B7F749E76090CF218EC38BC54C77ABEEAA1A22916601723E95046D5420AAE6028C7326B03236E4D6EB83635814F5C61CDED9'
$ModDll = 'CMS21-Together.dll'

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail([string]$text) { throw $text }

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
            if ($line -match ('"' + $SteamAppId + '"') -and $current) { $libraries = @($current) + $libraries }
        }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\Car Mechanic Simulator 2021'
        if (Test-Path (Join-Path $candidate $GameExe)) { return $candidate }
    }
    return $null
}

function Get-Download([string]$url, [string]$file) {
    Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'TogetherPlus-installer' } -Uri $url -OutFile $file
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('TogetherPlus-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    Say ''
    Say 'CMS21 Together+ installer' 'Cyan'

    # 1. The game.
    if (-not $GamePath) { $GamePath = Find-Game }
    if (-not $GamePath) {
        if ($NoPrompt) { Fail 'Car Mechanic Simulator 2021 was not found. Run again with -GamePath.' }
        $GamePath = Read-Host 'Car Mechanic Simulator 2021 was not found. Paste the game folder (the one with "Car Mechanic Simulator 2021.exe")'
    }
    $GamePath = $GamePath.Trim('"', ' ')
    if (-not (Test-Path (Join-Path $GamePath $GameExe))) { Fail "'$GamePath' is not the game folder: '$GameExe' is missing." }
    $GamePath = (Resolve-Path $GamePath).Path
    Say "Game: $GamePath"
    if (Get-Process -Name 'Car Mechanic Simulator 2021' -ErrorAction SilentlyContinue) { Fail 'The game is running. Close it and run the installer again.' }

    # 2. MelonLoader 0.5.7 x64.
    $melonDll = Join-Path $GamePath 'MelonLoader\MelonLoader.dll'
    $installed = if (Test-Path $melonDll) { (Get-Item $melonDll).VersionInfo.FileVersion } else { $null }
    if ($installed -and $installed.StartsWith($MelonVersion)) {
        Say "MelonLoader $installed is installed."
    } else {
        if ($installed) { Say "MelonLoader $installed is installed; Together+ needs $MelonVersion. Replacing it (the old one is kept as a backup)." 'Yellow' }
        else { Say "Installing MelonLoader $MelonVersion..." }
        $melonZip = $MelonLoaderZip
        if (-not $melonZip) {
            $melonZip = Join-Path $work 'MelonLoader.x64.zip'
            Get-Download $MelonUrl $melonZip
        }
        $hash = (Get-FileHash $melonZip -Algorithm SHA512).Hash
        if ($hash -ne $MelonSha512) { Fail 'The MelonLoader download does not match the official SHA-512. Nothing was installed.' }
        if ($installed) {
            $backup = Join-Path $GamePath ('MelonLoader_backup_' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            New-Item -ItemType Directory -Force $backup | Out-Null
            Move-Item (Join-Path $GamePath 'MelonLoader') $backup
            if (Test-Path (Join-Path $GamePath 'version.dll')) { Move-Item (Join-Path $GamePath 'version.dll') $backup }
            Say "Old MelonLoader moved to $backup"
        }
        Expand-Archive -Path $melonZip -DestinationPath $GamePath -Force
        Say "MelonLoader $MelonVersion installed." 'Green'
    }

    # 3. The mod package: -Zip, an unzipped package around this script, a zip next to it, or the newest release.
    $package = $null
    if (-not $Zip -and $PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "Mods\$ModDll"))) { $package = $PSScriptRoot }
    if (-not $package -and -not $Zip -and $PSScriptRoot) {
        $local = Get-ChildItem -Path $PSScriptRoot -Filter 'TogetherPlus-*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($local) { $Zip = $local.FullName }
    }
    if (-not $package -and -not $Zip) {
        Say 'Downloading the newest Together+ release...'
        $releases = Invoke-RestMethod -Headers @{ 'User-Agent' = 'TogetherPlus-installer' } -Uri "https://api.github.com/repos/$Repo/releases?per_page=10"
        $asset = $null
        foreach ($release in $releases) {
            if ($release.draft) { continue }
            $asset = $release.assets | Where-Object { $_.name -like 'TogetherPlus-*.zip' } | Select-Object -First 1
            if ($asset) { Say "Release: $($release.tag_name)"; break }
        }
        if (-not $asset) { Fail "No Together+ release was found on github.com/$Repo." }
        $Zip = Join-Path $work $asset.name
        Get-Download $asset.browser_download_url $Zip
    }
    if (-not $package) {
        Say "Package: $(Split-Path $Zip -Leaf)"
        $package = Join-Path $work 'package'
        Expand-Archive -Path $Zip -DestinationPath $package -Force
    } else { Say "Package: $package" }
    if (-not (Test-Path (Join-Path $package "Mods\$ModDll"))) { Fail "'$Zip' is not a Together+ package (Mods\$ModDll is missing)." }

    # 4. Other Together builds would load twice: they are moved aside, not deleted.
    $mods = Join-Path $GamePath 'Mods'
    $userLibs = Join-Path $GamePath 'UserLibs'
    New-Item -ItemType Directory -Force $mods, $userLibs | Out-Null
    $disabled = Join-Path $mods '_disabled'
    foreach ($file in Get-ChildItem $mods -Filter '*.dll' -File) {
        if ($file.Name -eq $ModDll) { continue }
        if ($file.Name -match '(?i)together|fixfortogether') {
            New-Item -ItemType Directory -Force $disabled | Out-Null
            Move-Item $file.FullName (Join-Path $disabled $file.Name) -Force
            Say "Moved $($file.Name) to Mods\_disabled (it conflicts with Together+)." 'Yellow'
        }
    }

    # 5. The mod files.
    $existing = Join-Path $mods $ModDll
    $update = Test-Path $existing
    Copy-Item (Join-Path $package "Mods\$ModDll") $mods -Force
    Copy-Item (Join-Path $package 'UserLibs\Facepunch.Steamworks.Win64.dll') $userLibs -Force
    $steamApi = Join-Path $GamePath 'Car Mechanic Simulator 2021_Data\Plugins\x86_64\steam_api64.dll'
    if (Test-Path $steamApi) { Copy-Item $steamApi $userLibs -Force }
    else { Say 'steam_api64.dll was not found in the game folder: Steam play will not work (Direct IP still does).' 'Yellow' }
    foreach ($doc in 'README.md', 'NOTICE.md') {
        if (Test-Path (Join-Path $package $doc)) { Copy-Item (Join-Path $package $doc) (Join-Path $mods ("TogetherPlus-" + $doc)) -Force }
    }

    Say ''
    if ($update) { Say 'Together+ updated.' 'Green' } else { Say 'Together+ installed.' 'Green' }
    Say ''
    Say 'Next steps:' 'Cyan'
    Say '  1. Start Car Mechanic Simulator 2021 from Steam. The first launch takes a few minutes while MelonLoader'
    Say '     prepares the game (a console window shows its progress). Later launches are fast.'
    Say '  2. In the main menu, open the new multiplayer button: host a save, or join a friend.'
    Say '  3. Every player needs the same Together+ version: share this installer with your friends.'
    Say '  Saves are kept on the host. Run this installer again any time to update.'
    Say ''
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

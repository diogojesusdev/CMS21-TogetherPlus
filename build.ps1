<#
.SYNOPSIS
    Builds the Together+ mod and runs the headless tests.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -SkipTests
    .\build.ps1 -Soak                      # plus a one-hour simulated 8-player soak on a bad network
    .\build.ps1 -SaveKill                  # plus 200 process kills while saving (Phase 4 exit test)
    .\build.ps1 -GamePath 'C:\Games\Steam\steamapps\common\Car Mechanic Simulator 2021'   # game not found by Steam
    .\build.ps1 -Deploy                    # also copy the DLL into <game>\Mods
    .\build.ps1 -Package                   # plus dist\TogetherPlus-<version>.zip (Mods, UserLibs, docs, licences)
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    # Runs the bot harness: 8 players, one simulated hour, 80+20 ms latency, 5% loss, churn.
    [switch]$Soak,

    # Kills a saving process 200 times and checks that a valid generation always loads.
    [switch]$SaveKill,

    # Copies the built DLL into <GamePath>\Mods. Opt-in only.
    [switch]$Deploy,

    # Writes dist\TogetherPlus-<version>.zip: the mod, its Steam library, README, NOTICE and licences.
    [switch]$Package,

    # The game folder (with MelonLoader 0.5.7 run once): its MelonLoader\Managed assemblies are compiled against.
    # Found through Steam when omitted.
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    $fallback = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $fallback) { return $fallback }

    throw 'MSBuild not found. Install Visual Studio 2022 or Build Tools with the .NET desktop workload.'
}

function Find-GamePath {
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $item = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($item.SteamPath) { $steam = $item.SteamPath; break }
        if ($item.InstallPath) { $steam = $item.InstallPath; break }
    }
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $current = $null
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+)"') { $current = $Matches[1] -replace '\\\\', '\' }
                if ($line -match '"1190000"' -and $current) { $libraries = @($current) + $libraries }
            }
        }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\Car Mechanic Simulator 2021'
        if (Test-Path (Join-Path $candidate 'MelonLoader\Managed\Assembly-CSharp.dll')) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)." }
}


$msbuild = Find-MSBuild
if (-not $GamePath -and ($Deploy -or -not (Test-Path (Join-Path $root 'Libs\Assembly-CSharp.dll')))) { $GamePath = Find-GamePath }
if ($Deploy -and -not $GamePath) { throw 'The game was not found; pass -GamePath.' }
$gameArgs = @()
if ($GamePath) {
    if (-not (Test-Path (Join-Path $GamePath 'MelonLoader\Managed\Assembly-CSharp.dll'))) {
        throw "'$GamePath' has no MelonLoader\Managed assemblies. Install MelonLoader 0.5.7 and start the game once."
    }
    $gameArgs = @("-p:GamePath=$GamePath")
    Write-Host "Game: $GamePath" -ForegroundColor DarkGray
}
$modProject = Join-Path $root 'CMS21-Together.csproj'
$testProject = Join-Path $root 'Tests\Tests.csproj'
$harnessProject = Join-Path $root 'Tools\BotHarness\BotHarness.csproj'
$harnessExe = Join-Path $root "Tools\BotHarness\bin\$Configuration\net472\BotHarness.exe"
$modDll = Join-Path $root "bin\$Configuration\CMS21-Together.dll"

Invoke-Step "Build mod ($Configuration)" {
    & $msbuild $modProject -restore "-p:Configuration=$Configuration" @gameArgs -nologo -v:minimal
}

Invoke-Step "Build bot harness ($Configuration)" {
    dotnet build $harnessProject -c $Configuration --nologo -v q
}

if (-not $SkipTests) {
    Invoke-Step 'Run tests' {
        dotnet test $testProject -c $Configuration --nologo -v q
    }
}

if ($Soak) {
    Invoke-Step 'Soak (bot harness)' {
        & $harnessExe --players 8 --seconds 3600 --latency 80 --jitter 20 --loss 0.05 --churn 1
    }
}

if ($SaveKill) {
    Invoke-Step 'Save kill test (bot harness)' {
        & $harnessExe --save-kill 200
    }
}

if ($Package) {
    $version = (Select-String -Path (Join-Path $root 'MainMod.cs') -Pattern 'PLUS_VERSION = "([^"]+)"').Matches[0].Groups[1].Value
    $stage = Join-Path $root "dist\TogetherPlus-$version"
    $zip = "$stage.zip"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'Mods'), (Join-Path $stage 'UserLibs'), (Join-Path $stage 'LICENSES') | Out-Null
    Copy-Item $modDll (Join-Path $stage 'Mods')
    Copy-Item (Join-Path $root 'Libs\Facepunch.Steamworks.Win64.dll'), (Join-Path $root 'Libs\steam_api64.dll') (Join-Path $stage 'UserLibs')
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'NOTICE.md'), (Join-Path $root 'LICENSE') $stage
    Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'Install.bat'), (Join-Path $root 'uninstall.ps1') $stage
    Copy-Item (Join-Path $root 'LICENSES\*') (Join-Path $stage 'LICENSES')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zip" -ForegroundColor Green
}

if ($Deploy) {
    $mods = Join-Path $GamePath 'Mods'
    if (-not (Test-Path $mods)) { throw "'$mods' not found. Is MelonLoader installed in '$GamePath'?" }
    Copy-Item $modDll $mods -Force
    Write-Host "Deployed $modDll -> $mods" -ForegroundColor Green
}

Write-Host "Done: $modDll" -ForegroundColor Green

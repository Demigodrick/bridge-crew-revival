# Bridge Crew Revival installer for Windows.
#
#   irm https://demigodrick.github.io/bridge-crew-revival/install.ps1 | iex
#   & ([scriptblock]::Create((irm https://demigodrick.github.io/bridge-crew-revival/install.ps1))) -Uninstall
#
# or from the release zip: double-click install.cmd  (install.cmd "D:\Games\Star Trek Bridge Crew", install.cmd -Uninstall)
#
# Finds the game in your Steam libraries, installs BepInEx 5 (from its GitHub release, checksum
# verified), sets the BepInEx entrypoint this game needs, and copies the mod (from the zip it's in,
# or the latest GitHub release). Run it again to update. $env:STBC_VERSION pins a release.
# It then offers to copy your settings from your Ubisoft cloud save (avatar, options) by starting the game
# once; -ImportSave does that without asking, -NoImport skips the question.
param(
    [string]$GameDir = "",
    [switch]$Uninstall,
    [switch]$ImportSave,
    [switch]$NoImport
)

$Repo = "Demigodrick/bridge-crew-revival"
$BepInExVersion = "5.4.23.5"
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
$AppId = 527100

# Run through "irm | iex" this is the user's own PowerShell session: no "exit", errors are thrown
# and reported at the bottom.
function Fail([string]$Message) { throw $Message }

function Find-Game {
    $roots = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($props.SteamPath) { $roots += $props.SteamPath }
        if ($props.InstallPath) { $roots += $props.InstallPath }
    }
    $roots += "${env:ProgramFiles(x86)}\Steam"
    $libraries = @()
    foreach ($root in ($roots | Select-Object -Unique)) {
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (-not (Test-Path $vdf)) { continue }
        $libraries += $root
        # "path"  "D:\\SteamLibrary"
        foreach ($match in Select-String -Path $vdf -Pattern '^\s*"path"\s*"(.*)"\s*$') {
            $libraries += $match.Matches[0].Groups[1].Value -replace '\\\\', '\'
        }
    }
    foreach ($library in ($libraries | Select-Object -Unique)) {
        $manifest = Join-Path $library "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path $manifest)) { continue }
        $match = Select-String -Path $manifest -Pattern '^\s*"installdir"\s*"(.*)"\s*$' | Select-Object -First 1
        if (-not $match) { continue }
        $dir = Join-Path $library ("steamapps\common\" + $match.Matches[0].Groups[1].Value)
        if (Test-Path (Join-Path $dir "stbc.exe")) { return $dir }
    }
    return $null
}

function Get-Download([string]$Url, [string]$File) {
    $ProgressPreference = "SilentlyContinue" # Invoke-WebRequest is very slow with the progress bar
    Invoke-WebRequest -Uri $Url -OutFile $File -UseBasicParsing
}

# ---- Ubisoft save import ------------------------------------------------------------------------

function Set-ImportFlag([string]$GameDir, [bool]$On) {
    $cfg = Join-Path $GameDir "BepInEx\config\stbc.revival.cfg"
    $value = if ($On) { "true" } else { "false" }
    if ((Test-Path $cfg) -and (Select-String -Path $cfg -Pattern '^FetchUbisoftSave' -Quiet)) {
        $lines = foreach ($line in Get-Content $cfg) { if ($line -match '^FetchUbisoftSave') { "FetchUbisoftSave = $value" } else { $line } }
        Set-Content -Path $cfg -Value $lines -Encoding UTF8
    } elseif ($On) {
        Add-Content -Path $cfg -Value @("", "[Import]", "", "FetchUbisoftSave = true") -Encoding UTF8
    }
}

function Import-UbisoftSave([string]$GameDir) {
    $result = Join-Path $GameDir "BepInEx\config\stbc.revival.import.txt"
    Write-Host ""
    Write-Host "Importing your Ubisoft save: the game starts once, signs in to Ubisoft, copies your settings and"
    Write-Host "quits by itself. Ubisoft Connect must be installed and signed in; put your headset on if it asks."
    Remove-Item -LiteralPath $result -ErrorAction SilentlyContinue
    Set-ImportFlag $GameDir $true
    Write-Host "Starting Star Trek: Bridge Crew..."
    Start-Process "steam://rungameid/$AppId"
    $waited = 0
    while (-not (Test-Path $result) -and $waited -lt 420) { Start-Sleep -Seconds 5; $waited += 5 }
    if (Test-Path $result) {
        Write-Host ((Get-Content $result)[1])
    } else {
        Set-ImportFlag $GameDir $false
        Write-Host "No result after 7 minutes; the import didn't run. Check BepInEx\LogOutput.log, then run the installer again with -ImportSave."
    }
}

function Install-BridgeCrewRevival([string]$GameDir, [bool]$Uninstall, [string]$Here, [string]$Import) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    if (-not $GameDir) {
        $GameDir = Find-Game
        if (-not $GameDir) { Fail "couldn't find Star Trek: Bridge Crew in your Steam libraries. Run install.cmd with the game folder, e.g. install.cmd `"D:\Games\Star Trek Bridge Crew`"" }
    }
    if (-not (Test-Path (Join-Path $GameDir "stbc.exe"))) { Fail "`"$GameDir`" isn't the Bridge Crew folder (no stbc.exe there)." }
    Write-Host "Game folder: $GameDir"
    if (Get-Process -Name "stbc" -ErrorAction SilentlyContinue) { Fail "the game is running. Quit it first." }

    $plugins = Join-Path $GameDir "BepInEx\plugins"

    # ---- Uninstall --------------------------------------------------------------------------

    if ($Uninstall) {
        foreach ($name in "StbcRevival.dll", "StbcRevival.pdb", "StbcCore.dll", "StbcCore.pdb") {
            Remove-Item -LiteralPath (Join-Path $plugins $name) -ErrorAction SilentlyContinue
        }
        # Without winhttp.dll BepInEx doesn't load at all; the BepInEx folder is left for you to delete.
        foreach ($name in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") {
            Remove-Item -LiteralPath (Join-Path $GameDir $name) -ErrorAction SilentlyContinue
        }
        Write-Host "Removed the mod and disabled BepInEx. You can delete `"$GameDir\BepInEx`" too."
        return
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("BridgeCrewRevival-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        # ---- The mod's files: next to this script (release zip), or the GitHub release --------

        if ($Here -and (Test-Path (Join-Path $Here "plugins\StbcRevival.dll"))) {
            $source = Join-Path $Here "plugins"
        } else {
            $base = "https://github.com/$Repo/releases/latest/download"
            if ($env:STBC_VERSION) { $base = "https://github.com/$Repo/releases/download/v" + $env:STBC_VERSION.TrimStart("v") }
            Write-Host "Downloading Bridge Crew Revival $(if ($env:STBC_VERSION) { $env:STBC_VERSION } else { '(latest release)' })..."
            $zip = Join-Path $tmp "mod.zip"
            Get-Download "$base/BridgeCrewRevival.zip" $zip
            Get-Download "$base/BridgeCrewRevival.zip.sha256" "$zip.sha256"
            $expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
            if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $expected) { Fail "Bridge Crew Revival download checksum mismatch." }
            Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "mod") -Force
            $source = (Get-ChildItem -Path (Join-Path $tmp "mod") -Recurse -Filter StbcRevival.dll | Select-Object -First 1).DirectoryName
        }
        if (-not $source -or -not (Test-Path (Join-Path $source "StbcCore.dll"))) { Fail "the mod's files are missing." }

        # ---- BepInEx ----------------------------------------------------------------------------

        if ((Test-Path (Join-Path $GameDir "winhttp.dll")) -and (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) {
            Write-Host "BepInEx is already installed."
        } else {
            Write-Host "Downloading BepInEx $BepInExVersion..."
            $zip = Join-Path $tmp "bepinex.zip"
            Get-Download $BepInExUrl $zip
            if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $BepInExSha256) { Fail "BepInEx download checksum mismatch." }
            Expand-Archive -Path $zip -DestinationPath $GameDir -Force
            Write-Host "BepInEx installed."
        }

        # Unity 5.4 needs BepInEx to start from MonoBehaviour's static constructor: the default
        # (Application) loads plugins during the script-domain reload and the game hangs on a black screen.
        $configDir = Join-Path $GameDir "BepInEx\config"
        New-Item -ItemType Directory -Force -Path $configDir, $plugins | Out-Null
        $cfg = Join-Path $configDir "BepInEx.cfg"
        if (Test-Path $cfg) {
            $section = ""
            $lines = foreach ($line in Get-Content $cfg) {
                if ($line -match '^\[(.*)\]') { $section = $Matches[1] }
                if ($section -eq "Preloader.Entrypoint" -and $line -match '^Type = ') { "Type = MonoBehaviour" } else { $line }
            }
            Set-Content -Path $cfg -Value $lines -Encoding UTF8
        } else {
            # BepInEx fills in every other setting on first launch.
            Set-Content -Path $cfg -Encoding UTF8 -Value @(
                "[Preloader.Entrypoint]", "", "Assembly = UnityEngine.dll", "", "Type = MonoBehaviour", "", "Method = .cctor")
        }
        if (-not (Select-String -Path $cfg -Pattern '^Type = MonoBehaviour' -Quiet)) { Fail "couldn't set the BepInEx entrypoint in $cfg." }

        # ---- Install ------------------------------------------------------------------------------

        Copy-Item -Path (Join-Path $source "Stbc*") -Destination $plugins -Force
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
    Write-Host "Bridge Crew Revival installed." -ForegroundColor Green
    Write-Host ""
    Write-Host "Launch the game from Steam as usual; no launch options are needed on Windows."
    Write-Host "Settings: BepInEx\config\stbc.revival.cfg (created on first launch). Log: BepInEx\LogOutput.log"

    $hasLocalSave = Test-Path (Join-Path $GameDir "BepInEx\config\stbc.revival.save.json")
    $doImport = $Import -eq "yes"
    if (-not $Import -and -not $hasLocalSave) {
        $doImport = (Read-Host "Copy your avatar and settings from your Ubisoft save? (starts the game once) [y/N]") -match '^[yY]'
    }
    if ($doImport) { Import-UbisoftSave $GameDir }
}

try {
    $import = if ($ImportSave) { "yes" } elseif ($NoImport) { "no" } else { "" }
    Install-BridgeCrewRevival -GameDir $GameDir -Uninstall:$Uninstall.IsPresent -Here $PSScriptRoot -Import $import
} catch {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
}

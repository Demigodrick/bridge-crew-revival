# Bridge Crew Revival installer for Windows.
#
#   irm https://demigodrick.github.io/bridge-crew-revival/install.ps1 | iex
#   irm https://demigodrick.github.io/bridge-crew-revival/uninstall.ps1 | iex     (uninstall)
#
# or from the release zip: double-click install.cmd  (install.cmd "D:\Games\Star Trek Bridge Crew", install.cmd -Uninstall)
#
# Finds the game in your Steam libraries, installs BepInEx 5 (from its GitHub release, checksum
# verified), sets the BepInEx entrypoint this game needs, and copies the mod (from the zip it's in,
# or the latest GitHub release), plus offline speech recognition for voice commands (Vosk and its small
# English model, checksum verified). Run it again to update or remove it (it asks). $env:STBC_VERSION pins a release.
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
$VoskUrl = "https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-win64-0.3.45.zip"
$VoskSha256 = "f1dcc9cca460630f81ea8f71794f69c80bed6556d2a4e6237b5785e1d2dff34b"
$VoskModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"
$VoskModelSha256 = "30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498"
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

# Voice commands: Vosk (libvosk.dll and the three runtime DLLs it needs) and the model, in BepInEx\vosk.
# Not essential, so a failed download is only a warning.
function Install-Vosk([string]$GameDir, [string]$Tmp) {
    $vosk = Join-Path $GameDir "BepInEx\vosk"
    if ((Test-Path (Join-Path $vosk "libvosk.dll")) -and (Test-Path (Join-Path $vosk "model"))) {
        Ok "Voice recognition (Vosk) already installed"
        return
    }
    try {
        Info "Downloading voice recognition (Vosk and its English model, about 55 MB)..."
        $zip = Join-Path $Tmp "vosk.zip"
        Get-Download $VoskUrl $zip
        if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $VoskSha256) { throw "the Vosk download was damaged (checksum mismatch)" }
        $modelZip = Join-Path $Tmp "vosk-model.zip"
        Get-Download $VoskModelUrl $modelZip
        if ((Get-FileHash -Algorithm SHA256 $modelZip).Hash -ne $VoskModelSha256) { throw "the voice model download was damaged (checksum mismatch)" }
        Expand-Archive -Path $zip -DestinationPath (Join-Path $Tmp "vosk") -Force
        Expand-Archive -Path $modelZip -DestinationPath (Join-Path $Tmp "vosk-model") -Force
        New-Item -ItemType Directory -Force -Path $vosk | Out-Null
        foreach ($dll in "libvosk.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libwinpthread-1.dll") {
            $file = Get-ChildItem -Path (Join-Path $Tmp "vosk") -Recurse -Filter $dll | Select-Object -First 1
            if (-not $file) { throw "$dll is missing from the Vosk download" }
            Copy-Item -LiteralPath $file.FullName -Destination $vosk -Force
        }
        $model = Get-ChildItem -Path (Join-Path $Tmp "vosk-model") -Directory | Select-Object -First 1
        Remove-Item -Recurse -Force (Join-Path $vosk "model") -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $model.FullName -Destination (Join-Path $vosk "model")
        # 0.7.2 wrote VoiceCommands = false (then off by default); with Vosk here for the first time, switch it on.
        $cfg = Join-Path $GameDir "BepInEx\config\stbc.revival.cfg"
        if ((Test-Path $cfg) -and (Select-String -Path $cfg -Pattern '^VoiceCommands = false' -Quiet)) {
            $lines = foreach ($line in Get-Content $cfg) { if ($line -match '^VoiceCommands = ') { "VoiceCommands = true" } else { $line } }
            Set-Content -Path $cfg -Value $lines -Encoding UTF8
        }
        Ok "Voice recognition (Vosk) installed"
    } catch {
        Warn "Voice commands won't work: $($_.Exception.Message). Everything else does; run the installer again to retry."
    }
}

function Step([string]$Text) { Write-Host ""; Write-Host $Text -ForegroundColor Cyan }
function Ok([string]$Text) { Write-Host "  OK   $Text" -ForegroundColor Green }
function Info([string]$Text) { Write-Host "       $Text" }
function Warn([string]$Text) { Write-Host "  !!   $Text" -ForegroundColor Yellow }

function Import-UbisoftSave([string]$GameDir) {
    $result = Join-Path $GameDir "BepInEx\config\stbc.revival.import.txt"
    $log = Join-Path $GameDir "stbc_Data\output_log.txt"
    Remove-Item -LiteralPath $result -ErrorAction SilentlyContinue
    Set-ImportFlag $GameDir $true
    $seen = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
    Info "Starting Star Trek: Bridge Crew. When it asks you to choose a controller, choose one:"
    Info "the game only connects to Ubisoft after that. It closes by itself when it's done."
    Start-Process "steam://rungameid/$AppId"
    $waited = 0
    while (-not (Test-Path $result) -and $waited -lt 420) {
        Start-Sleep -Seconds 5; $waited += 5
        # Progress from the game's log (the mod's messages are copied there too).
        if (Test-Path $log) {
            $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
            if ($lines.Count -lt $seen) { $seen = 0 }  # the log was restarted
            foreach ($line in $lines[$seen..($lines.Count - 1)]) {
                if ($line -like "Initialize engine version*") { Info "- game starting" }
                elseif ($line -like "*Bridge Crew Revival* loaded.*") { Info "- mod loaded" }
                elseif ($line -like "*Ubisoft save import: this run*") { Info "- waiting for your save: choose your controller in the game if it asks" }
                elseif ($line -like "*Ubisoft save import: quitting*") { Info "- closing the game" }
            }
            $seen = $lines.Count
        }
        if ($waited % 30 -eq 0) { Info ("... still waiting ({0}m{1}s)" -f [int][Math]::Floor($waited / 60), ($waited % 60)) }
    }
    if (-not (Test-Path $result)) {
        Set-ImportFlag $GameDir $false
        Warn "Your save didn't arrive within 7 minutes, so nothing was imported."
        Info "Run the installer again to retry. (Details: BepInEx\LogOutput.log in the game folder.)"
        return
    }
    $outcome = @(Get-Content $result)
    switch ($outcome[0]) {
        "ok" { Ok $outcome[1] }
        "none" { Ok "Your Ubisoft account has no Bridge Crew save, so there was nothing to import." }
        default { Warn $outcome[1]; Info "Run the installer again to retry." }
    }
}

function Install-BridgeCrewRevival([string]$GameDir, [bool]$Uninstall, [string]$Here, [string]$Import) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    Write-Host "Bridge Crew Revival installer" -ForegroundColor Cyan
    Write-Host "Adds online multiplayer over Steam to Star Trek: Bridge Crew. Nothing to set up: just answer"
    Write-Host "the questions as they come."

    Step "1. Finding the game"
    if (-not $GameDir) {
        $GameDir = Find-Game
        if (-not $GameDir) {
            Fail ("couldn't find Star Trek: Bridge Crew in your Steam libraries.`n" +
                  "  If it's installed somewhere unusual, run install.cmd with its folder, e.g.`n" +
                  "    install.cmd `"D:\Games\Star Trek Bridge Crew`"")
        }
    }
    if (-not (Test-Path (Join-Path $GameDir "stbc.exe"))) { Fail "`"$GameDir`" isn't the Bridge Crew folder (there's no stbc.exe in it)." }
    Ok "Found it: $GameDir"
    if (Get-Process -Name "stbc" -ErrorAction SilentlyContinue) { Fail "Star Trek: Bridge Crew is running. Quit it, then run the installer again." }

    $plugins = Join-Path $GameDir "BepInEx\plugins"

    # Already installed, and not told to uninstall: update (Enter), or remove?
    $installed = Join-Path $plugins "StbcRevival.dll"
    if (-not $Uninstall -and (Test-Path $installed) -and [Environment]::UserInteractive) {
        $have = ([Diagnostics.FileVersionInfo]::GetVersionInfo($installed).ProductVersion -split '\+')[0]
        Info "Bridge Crew Revival $have is installed."
        $answer = try { Read-Host "       Update it, or remove it? [U]pdate / [R]emove (Enter = update)" } catch { "" }
        if ($answer -match '^\s*[rR]') { $Uninstall = $true }
    }

    # ---- Uninstall --------------------------------------------------------------------------

    if ($Uninstall) {
        Step "2. Removing Bridge Crew Revival"
        foreach ($name in "StbcRevival.dll", "StbcRevival.pdb", "StbcCore.dll", "StbcCore.pdb") {
            Remove-Item -LiteralPath (Join-Path $plugins $name) -ErrorAction SilentlyContinue
        }
        # Without winhttp.dll BepInEx doesn't load at all; the BepInEx folder is left for you to delete.
        foreach ($name in "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") {
            Remove-Item -LiteralPath (Join-Path $GameDir $name) -ErrorAction SilentlyContinue
        }
        Ok "Removed the mod and switched off its loader (BepInEx)"
        Info "Your settings and the voice-command files (BepInEx\vosk, so a reinstall needn't download them again)"
        Info "are left in `"$GameDir\BepInEx`"; delete that folder too if you like."
        Write-Host ""
        Write-Host "Done. Star Trek: Bridge Crew is back to normal." -ForegroundColor Green
        return
    }

    Step "2. Installing"
    $version = ""
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("BridgeCrewRevival-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        # ---- The mod's files: next to this script (release zip), or the GitHub release --------

        # A zip's own copy, unless the website has a newer release (running an old zip's installer again
        # would otherwise put the same old version back).
        $zipped = $Here -and (Test-Path (Join-Path $Here "plugins\StbcRevival.dll"))
        if ($zipped -and -not $env:STBC_VERSION) {
            $mine = if ((Split-Path -Leaf $Here) -match '^BridgeCrewRevival-([0-9][0-9.]*)$') { $Matches[1] } else { "" }
            try {
                $ProgressPreference = "SilentlyContinue"
                $latest = (Invoke-WebRequest -Uri "https://demigodrick.github.io/bridge-crew-revival/version.txt" -UseBasicParsing -TimeoutSec 15).Content.Trim()
                if ($mine -and $latest -match '^[0-9][0-9.]*$' -and [version]$latest -gt [version]$mine) {
                    Info "This zip has Bridge Crew Revival $mine; $latest is out, so that's what goes in."
                    $zipped = $false
                }
            } catch {
                Info "Couldn't check for a newer version; installing this zip's $mine."
            }
        }
        if ($zipped) {
            $source = Join-Path $Here "plugins"
        } else {
            $base = "https://github.com/$Repo/releases/latest/download"
            if ($env:STBC_VERSION) { $base = "https://github.com/$Repo/releases/download/v" + $env:STBC_VERSION.TrimStart("v") }
            Info "Downloading Bridge Crew Revival..."
            $zip = Join-Path $tmp "mod.zip"
            Get-Download "$base/BridgeCrewRevival.zip" $zip
            Get-Download "$base/BridgeCrewRevival.zip.sha256" "$zip.sha256"
            $expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
            if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $expected) { Fail "the Bridge Crew Revival download was damaged (checksum mismatch). Try again." }
            Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "mod") -Force
            $source = (Get-ChildItem -Path (Join-Path $tmp "mod") -Recurse -Filter StbcRevival.dll | Select-Object -First 1).DirectoryName
        }
        if (-not $source -or -not (Test-Path (Join-Path $source "StbcCore.dll"))) { Fail "the mod's files are missing." }
        $folder = Split-Path -Leaf (Split-Path -Parent $source)   # BridgeCrewRevival-<version>
        if ($folder -match '^BridgeCrewRevival-([0-9].*)$') { $version = $Matches[1] }

        # ---- BepInEx ----------------------------------------------------------------------------

        if ((Test-Path (Join-Path $GameDir "winhttp.dll")) -and (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) {
            Ok "Mod loader (BepInEx $BepInExVersion) already installed"
        } else {
            Info "Downloading the mod loader (BepInEx $BepInExVersion)..."
            $zip = Join-Path $tmp "bepinex.zip"
            Get-Download $BepInExUrl $zip
            if ((Get-FileHash -Algorithm SHA256 $zip).Hash -ne $BepInExSha256) { Fail "the BepInEx download was damaged (checksum mismatch). Try again." }
            Expand-Archive -Path $zip -DestinationPath $GameDir -Force
            Ok "Mod loader (BepInEx $BepInExVersion) installed"
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
        if (-not (Select-String -Path $cfg -Pattern '^Type = MonoBehaviour' -Quiet)) { Fail "couldn't set up the mod loader (in $cfg)." }

        # ---- Install ------------------------------------------------------------------------------

        Copy-Item -Path (Join-Path $source "Stbc*") -Destination $plugins -Force
        Install-Vosk $GameDir $tmp
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
    $versionText = if ($version) { " $version" } else { "" }
    Ok "Bridge Crew Revival$versionText installed"
    Info "Voice commands: as captain, hold V (or the voice button) and give an order, e.g. `"red alert`"."

    # ---- Ubisoft save -------------------------------------------------------------------------

    Step "3. Your avatar and settings (optional)"
    $hasLocalSave = Test-Path (Join-Path $GameDir "BepInEx\config\stbc.revival.save.json")
    if ($Import -ne "yes" -and $hasLocalSave) {
        Ok "Already kept on this PC; nothing to do"
    } elseif ($Import -eq "no") {
        Info "Skipped"
    } else {
        Info "Bridge Crew only saved your avatar and settings on Ubisoft's servers. They can be copied to"
        Info "this PC once: the game starts, signs in with Ubisoft Connect (which must be installed and"
        Info "signed in), copies your save and closes again. It takes a minute or two."
        $doImport = ($Import -eq "yes") -or ((Read-Host "       Copy them now? [y/N]") -match '^[yY]')
        if ($doImport) { Import-UbisoftSave $GameDir }
        else { Info "Skipped. To do it later, run the installer again and answer yes." }
    }

    # ---- Summary ------------------------------------------------------------------------------

    Write-Host ""
    Write-Host "All done: Bridge Crew Revival$versionText is installed." -ForegroundColor Green
    Write-Host "Launch Star Trek: Bridge Crew from Steam as usual. To update later, run the installer again."
}

try {
    $import = if ($ImportSave) { "yes" } elseif ($NoImport) { "no" } else { "" }
    Install-BridgeCrewRevival -GameDir $GameDir -Uninstall:$Uninstall.IsPresent -Here $PSScriptRoot -Import $import
} catch {
    Write-Host ""
    Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
}

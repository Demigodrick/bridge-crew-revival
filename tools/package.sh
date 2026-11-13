#!/usr/bin/env bash
# Build the mod and pack the release zip: dist/BridgeCrewRevival-<version>.zip
#   install.sh / install.cmd / install.ps1, plugins/StbcRevival.dll + StbcCore.dll, native/rnnoise.dll + speexdsp.dll, README.txt,
#   designer/ (the bridge designer, an optional program of its own for Windows and Linux; the installers put it in BepInEx/bridges)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/StbcRevival.csproj")"
name="BridgeCrewRevival-$version"

dotnet build "$root/src" -c Release --nologo -v quiet
out="$root/dist/$name"
rm -rf "$out" "$root/dist/$name.zip"
mkdir -p "$out/plugins"
cp "$root/src/bin/Release/net35/StbcRevival.dll" "$root/src/bin/Release/net35/StbcCore.dll" "$out/plugins/"
# Revival voice's native libraries, committed in tools/<name>/out/ (tools/<name>/build.sh rebuilds one from
# source, to upgrade it): RNNoise (noise suppression) and speexdsp (echo cancellation). Not under plugins/, which
# BepInEx scans. Each must match the SHA-256 pinned here, the build that was tested, so a DLL replaced by
# accident or otherwise isn't shipped; after an intended rebuild, test it and update its pin.
declare -A native_sha256=(
  [rnnoise]=dd15796f87cf62e9f5a647d7e9328e2847b171e06173e71452c636ecffe53506
  [speexdsp]=c40c469f35edc491bb6ddbc827da07340fd0a8aa363cabfbe61c662d6ac3350f
)
mkdir -p "$out/native"
for lib in rnnoise speexdsp; do
  dll="$root/tools/$lib/out/$lib.dll"
  [ -f "$dll" ] || "$root/tools/$lib/build.sh"
  actual="$(sha256sum "$dll" | cut -d" " -f1)"
  if [ "$actual" != "${native_sha256[$lib]}" ]; then
    echo "$lib.dll doesn't match its pinned SHA-256 in tools/package.sh (expected ${native_sha256[$lib]}, got $actual)." >&2
    echo "If you rebuilt it on purpose, test it in game, then update the pin." >&2
    exit 1
  fi
  cp "$dll" "$out/native/"
done
# The models of our own ship classes and bridge sets (BepInEx/ships, BepInEx/bridges) and the Quest controllers' SteamVR
# layout (BepInEx/vr), beside plugins/ in the zip.
for d in ships bridges vr; do [ -d "$root/assets/$d" ] && mkdir -p "$out/$d" && cp -r "$root/assets/$d/." "$out/$d/"; done
# The bridge designer: one program each for Windows and Linux with .NET inside (nothing to install first), trimmed
# to what it uses.
mkdir -p "$out/designer"
for rid in win-x64 linux-x64; do
  dotnet publish "$root/tools/bridge-designer" -c Release -r "$rid" --self-contained --nologo -v quiet \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:DebugType=none \
    -o "$root/tools/bridge-designer/bin/publish/$rid"
done
cp "$root/tools/bridge-designer/bin/publish/win-x64/bridge-designer.exe" "$out/designer/Bridge Designer.exe"
cp "$root/tools/bridge-designer/bin/publish/linux-x64/bridge-designer" "$out/designer/bridge-designer"
chmod +x "$out/designer/bridge-designer"
cp "$root/docs/install.sh" "$out/"
chmod +x "$out/install.sh"
# Windows files with CRLF line endings.
for f in install.cmd install.ps1; do sed 's/\r$//; s/$/\r/' "$root/docs/$f" > "$out/$f"; done
sed 's/\r$//; s/$/\r/' > "$out/uninstall.cmd" <<'EOF'
@echo off
rem Bridge Crew Revival: double-click to remove the mod. Your settings stay in the game's BepInEx folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
EOF
sed 's/\r$//; s/$/\r/' > "$out/README.txt" <<EOF
Bridge Crew Revival $version - multiplayer for Star Trek: Bridge Crew (Steam, PC) after the Ubisoft shutdown.

Website: https://demigodrick.github.io/bridge-crew-revival/

Windows: double-click install.cmd (uninstall.cmd removes the mod).
Linux:   run ./install.sh. (If the game has never been started under Proton, start it once and quit
         first, or set its Steam launch options to WINEDLLOVERRIDES="winhttp=n,b" %command%.)

Keeping your avatar and settings: the game only saved them on Ubisoft's servers. The installer offers
to copy them once (Ubisoft Connect must be signed in). It starts the game. At the controller selection
prompt, choose your controller: the game only connects to Ubisoft after that. Your save is then
copied and the game closes by itself. Run it later with --import-save (Linux) / -ImportSave (Windows).

Both find the game in your Steam libraries (or pass the game folder as an argument), install
BepInEx 5.4.23.5 from its GitHub release, and copy the mod. Run again to update or remove it (it
asks); --uninstall (Linux), -Uninstall or uninstall.cmd (Windows) removes it straight away.

Bridge designer (optional): move, turn and hide the pieces of a bridge, or add parts, in your browser. The
installer puts it in the game's BepInEx/bridges folder: run "Bridge Designer.exe" (Windows) or bridge-designer
(Linux) there. Layouts are saved beside it; in a crew, everyone sees the host's bridge.

Quest controllers through SteamVR (Virtual Desktop, Steam Link): the installer asks whether to use Quest-style
buttons (-QuestControls yes on Windows, --quest-controls=yes on Linux answers it). SteamVR must be closed.

Voice commands: the installer asks whether the orders are in English or German (-VoiceLanguage German on
Windows, --voice-language=german on Linux answers it).

Everyone you play with needs the mod. Host with Quick match or Custom lobby; friends join from
the shuttle's Steam friends panel, Steam's Join Game / invites, or the F7 room browser (desktop).
Settings: BepInEx/config/stbc.revival.cfg. Log: BepInEx/LogOutput.log.
EOF
(cd "$root/dist" && zip -qr "$name.zip" "$name")
echo "$root/dist/$name.zip"

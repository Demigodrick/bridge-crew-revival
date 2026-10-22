#!/usr/bin/env bash
# Build the mod and pack the release zip: dist/BridgeCrewRevival-<version>.zip
#   install.sh / install.cmd / install.ps1, plugins/StbcRevival.dll + StbcCore.dll, README.txt
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/StbcRevival.csproj")"
name="BridgeCrewRevival-$version"

dotnet build "$root/src" -c Release --nologo -v quiet
out="$root/dist/$name"
rm -rf "$out" "$root/dist/$name.zip"
mkdir -p "$out/plugins"
cp "$root/src/bin/Release/net35/StbcRevival.dll" "$root/src/bin/Release/net35/StbcCore.dll" "$out/plugins/"
cp "$root/docs/install.sh" "$out/"
chmod +x "$out/install.sh"
# Windows files with CRLF line endings.
for f in install.cmd install.ps1; do sed 's/\r$//; s/$/\r/' "$root/docs/$f" > "$out/$f"; done
sed 's/\r$//; s/$/\r/' > "$out/README.txt" <<EOF
Bridge Crew Revival $version - multiplayer for Star Trek: Bridge Crew (Steam, PC) after the Ubisoft shutdown.

Website: https://demigodrick.github.io/bridge-crew-revival/

Windows: double-click install.cmd.
Linux:   run ./install.sh. (If the game has never been started under Proton, start it once and quit
         first, or set its Steam launch options to WINEDLLOVERRIDES="winhttp=n,b" %command%.)

Keeping your avatar and settings: the game only saved them on Ubisoft's servers. The installer offers
to copy them once (Ubisoft Connect must be signed in). It starts the game. At the controller selection
prompt, choose your controller: the game only connects to Ubisoft after that. Your save is then
copied and the game closes by itself. Run it later with --import-save (Linux) / -ImportSave (Windows).

Both find the game in your Steam libraries (or pass the game folder as an argument), install
BepInEx 5.4.23.5 from its GitHub release, and copy the mod. Run again to update;
--uninstall (Linux) or -Uninstall (Windows) removes it.

Everyone you play with needs the mod. Host with Quick match or Custom lobby; friends join from
the shuttle's Steam friends panel, Steam's Join Game / invites, or the F7 room browser (desktop).
Settings: BepInEx/config/stbc.revival.cfg. Log: BepInEx/LogOutput.log.
EOF
(cd "$root/dist" && zip -qr "$name.zip" "$name")
echo "$root/dist/$name.zip"

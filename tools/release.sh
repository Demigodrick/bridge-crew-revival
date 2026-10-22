#!/usr/bin/env bash
# Publish a GitHub release of the version in src/StbcRevival.csproj:
#   tools/release.sh ["release notes"]
# Builds locally (the mod compiles against the game's own DLLs, which can't go in the repo or CI),
# then uploads BridgeCrewRevival.zip and its .sha256. The installers fetch
# releases/latest/download/BridgeCrewRevival.zip, so the asset names must stay fixed.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/StbcRevival.csproj")"
notes="${1:-Bridge Crew Revival $version}"

zip="$("$root/tools/package.sh" | tail -n1)"
out="$root/dist/release"
rm -rf "$out" && mkdir -p "$out"
cp "$zip" "$out/BridgeCrewRevival.zip"
(cd "$out" && sha256sum BridgeCrewRevival.zip > BridgeCrewRevival.zip.sha256)

gh release create "v$version" "$out/BridgeCrewRevival.zip" "$out/BridgeCrewRevival.zip.sha256" \
  --title "Bridge Crew Revival $version" --notes "$notes"

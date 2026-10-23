#!/usr/bin/env bash
# Publish a GitHub release of the version in src/StbcRevival.csproj:
#   tools/release.sh ["release notes"]
# Builds locally (the mod compiles against the game's own DLLs, which can't go in the repo or CI),
# then uploads BridgeCrewRevival.zip and its .sha256. The installers fetch
# releases/latest/download/BridgeCrewRevival.zip, so the asset names must stay fixed.
# It also updates docs/version.txt (the in-game update check reads it from the website), commits and
# pushes that, so the site announces the new version once the release exists.
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

# Announce it to players' games only after the release (and its download) exists.
printf '%s\n' "$version" > "$root/docs/version.txt"
if ! git -C "$root" diff --quiet -- docs/version.txt; then
  git -C "$root" commit -q -m "Site: latest version is $version" -- docs/version.txt
  git -C "$root" push -q
fi

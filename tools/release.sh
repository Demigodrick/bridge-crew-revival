#!/usr/bin/env bash
# Publish a GitHub release of the version in src/StbcRevival.csproj:
#   tools/release.sh ["release notes"]
# Builds locally (the mod compiles against the game's own DLLs, which can't go in the repo or CI),
# then uploads BridgeCrewRevival.zip and its .sha256. The installers fetch
# releases/latest/download/BridgeCrewRevival.zip, so the asset names must stay fixed.
# The two DLLs, rnnoise.dll, speexdsp.dll, the ship models' files and their SHA256SUMS go up too: the in-game updater
# (UpdateCheck.cs) fetches those, as Unity 5.4's .NET can't open a zip.
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
cp "$root/dist/BridgeCrewRevival-$version/plugins/StbcRevival.dll" "$root/dist/BridgeCrewRevival-$version/plugins/StbcCore.dll" "$out/"
# Revival voice's native libraries too: the in-game updater puts them in BepInEx/native (on update, or when missing).
cp "$root/dist/BridgeCrewRevival-$version/native/rnnoise.dll" "$root/dist/BridgeCrewRevival-$version/native/speexdsp.dll" "$out/"
(cd "$out" && sha256sum StbcRevival.dll StbcCore.dll rnnoise.dll speexdsp.dll > SHA256SUMS)
assets=("$out/BridgeCrewRevival.zip" "$out/BridgeCrewRevival.zip.sha256" "$out/StbcRevival.dll" "$out/StbcCore.dll" "$out/rnnoise.dll" "$out/speexdsp.dll")
# Our ship models (BepInEx/ships) too, each file an asset of its own named with dots for the slashes
# ("ships.Nova.Nova.obj") and listed in SHA256SUMS by its path ("ships/Nova/Nova.obj"): the updater fetches the
# missing ones at launch and the changed ones on an update.
ships="$root/dist/BridgeCrewRevival-$version/ships"
if [ -d "$ships" ]; then
  while IFS= read -r -d '' f; do
    rel="ships/${f#"$ships"/}"
    name="${rel//\//.}"
    cp "$f" "$out/$name"
    printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$rel" >> "$out/SHA256SUMS"
    assets+=("$out/$name")
  done < <(find "$ships" -type f -print0 | sort -z)
fi
assets+=("$out/SHA256SUMS")

# The tag goes on the commit released: pushed first (docs/version.txt isn't changed yet, so players aren't told
# before the release exists), and named as the target. Without that GitHub tagged whatever main was before the push.
git -C "$root" push -q
gh release create "v$version" "${assets[@]}" --target "$(git -C "$root" rev-parse HEAD)" \
  --title "Bridge Crew Revival $version" --notes "$notes"

# Announce it to players' games only after the release (and its download) exists.
printf '%s\n' "$version" > "$root/docs/version.txt"
if ! git -C "$root" diff --quiet -- docs/version.txt; then
  git -C "$root" commit -q -m "Site: latest version is $version" -- docs/version.txt
  git -C "$root" push -q
fi

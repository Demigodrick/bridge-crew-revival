#!/usr/bin/env bash
# Publish a GitHub release of the version in src/StbcRevival.csproj:
#   tools/release.sh ["release notes"]
# Builds locally (the mod compiles against the game's own DLLs, which can't go in the repo or CI),
# then uploads BridgeCrewRevival.zip and its .sha256. The installers fetch
# releases/latest/download/BridgeCrewRevival.zip, so the asset names must stay fixed.
# The in-game updater (UpdateCheck.cs, ReleaseZip.cs) reads the files it needs out of the zip with range requests,
# checked against SHA256SUMS, which lists every file it may fetch (the DLLs, the native libraries, the ship models
# and the second sector). The two DLLs and SHA256SUMS also go up as assets of their own: updaters from 0.7.46 and
# older fetch those, and the new version they install then fetches the rest from the zip.
# It also updates docs/version.txt (the in-game update check reads it from the website), commits and
# pushes that, so the site announces the new version once the release exists.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/StbcRevival.csproj")"
notes="${1:-Bridge Crew Revival $version}"

# The second sector (Jagged Sword) as it is in the game folder now, where the galaxy designer saves it: copied into
# assets/galaxies and committed when it changed, so the release ships the latest and the repo records it.
game="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Star Trek Bridge Crew}"
sector="$root/assets/galaxies/jagged-sword.json"
[ -f "$game/BepInEx/galaxies/jagged-sword.json" ] && cp "$game/BepInEx/galaxies/jagged-sword.json" "$sector"
# Checked against the game's data as the game will: a file with problems would be refused, the second sector off.
# The galaxy designer is a local development tool (its own repo in tools/galaxy-designer, ignored here, not published).
[ -f "$root/tools/galaxy-designer/galaxy-designer.csproj" ] ||
  { echo "The galaxy designer isn't in tools/galaxy-designer, so jagged-sword.json can't be checked." >&2; exit 1; }
dotnet run --project "$root/tools/galaxy-designer" -c Release -- --game "$game" --no-open --check "$sector" ||
  { echo "jagged-sword.json has problems (above): fix them in the galaxy designer, then release again." >&2; exit 1; }
if ! git -C "$root" diff --quiet -- "$sector"; then
  git -C "$root" commit -q -m "Galaxy: the second sector as released in $version" -- "$sector"
fi

zip="$("$root/tools/package.sh" | tail -n1)"
out="$root/dist/release"
rm -rf "$out" && mkdir -p "$out"
cp "$zip" "$out/BridgeCrewRevival.zip"
(cd "$out" && sha256sum BridgeCrewRevival.zip > BridgeCrewRevival.zip.sha256)
cp "$root/dist/BridgeCrewRevival-$version/plugins/StbcRevival.dll" "$root/dist/BridgeCrewRevival-$version/plugins/StbcCore.dll" "$out/"
(cd "$out" && sha256sum StbcRevival.dll StbcCore.dll > SHA256SUMS)
# Revival voice's native libraries: the in-game updater puts them in BepInEx/native (on update, or when missing).
(cd "$root/dist/BridgeCrewRevival-$version/native" && sha256sum rnnoise.dll speexdsp.dll >> "$out/SHA256SUMS")
assets=("$out/BridgeCrewRevival.zip" "$out/BridgeCrewRevival.zip.sha256" "$out/StbcRevival.dll" "$out/StbcCore.dll")
# Our ship models (BepInEx/ships) and the second sector's galaxy file (BepInEx/galaxies), listed by their path in the
# zip's folder ("ships/Nova/Nova.obj"): the updater fetches the missing ones at launch and the changed ones on an update.
for d in ships galaxies; do
  dir="$root/dist/BridgeCrewRevival-$version/$d"
  [ -d "$dir" ] || continue
  while IFS= read -r -d '' f; do
    rel="$d/${f#"$dir"/}"
    printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$rel" >> "$out/SHA256SUMS"
  done < <(find "$dir" -type f -print0 | sort -z)
done
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

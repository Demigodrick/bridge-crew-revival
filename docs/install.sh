#!/usr/bin/env bash
# Bridge Crew Revival installer for Linux (Steam + Proton).
#
#   curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash
#   curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash -s -- --uninstall
#
# or from the release zip:  ./install.sh  [game folder]  [--uninstall]
#
# Finds the game in your Steam libraries, installs BepInEx 5 (from its GitHub release, checksum
# verified), sets the BepInEx entrypoint this game needs, and copies the mod (from the zip it's in,
# or the latest GitHub release). Run it again to update.
set -euo pipefail

REPO="Demigodrick/bridge-crew-revival"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
BEPINEX_SHA256="82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
APP_ID=527100
LAUNCH_OPTIONS='WINEDLLOVERRIDES="winhttp=n,b" %command%'

say()  { printf '%s\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Bridge Crew Revival installer (Linux)
  install.sh [game folder] [--uninstall]
Environment: STBC_VERSION=0.6.1 installs that release instead of the latest.
EOF
}

download() { # url file
  if command -v curl >/dev/null; then curl -fL --progress-bar -o "$2" "$1"
  elif command -v wget >/dev/null; then wget -q --show-progress -O "$2" "$1"
  else fail "need curl or wget to download."; fi
}

extract() { # zip dir
  if command -v unzip >/dev/null; then unzip -oq "$1" -d "$2"
  elif command -v python3 >/dev/null; then python3 -I -m zipfile -e "$1" "$2"
  else fail "need unzip or python3 to extract zip files."; fi
}

# ---- Find the game ----------------------------------------------------------------------------

find_game() {
  local roots=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.steam/root"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"   # Flatpak
    "$HOME/snap/steam/common/.local/share/Steam"                   # Snap
  )
  local root lib libs=()
  for root in "${roots[@]}"; do
    [ -f "$root/steamapps/libraryfolders.vdf" ] || continue
    libs+=("$root")
    # "path"  "/mnt/games/SteamLibrary"
    while IFS= read -r lib; do libs+=("$lib"); done < <(
      sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$root/steamapps/libraryfolders.vdf")
  done
  for lib in "${libs[@]}"; do
    local manifest="$lib/steamapps/appmanifest_${APP_ID}.acf"
    [ -f "$manifest" ] || continue
    local dir
    dir="$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$manifest" | head -n1)"
    if [ -n "$dir" ] && [ -f "$lib/steamapps/common/$dir/stbc.exe" ]; then
      printf '%s\n' "$lib/steamapps/common/$dir"
      return 0
    fi
  done
  return 1
}

main() {
  local uninstall=0 game="" arg
  for arg in "$@"; do
    case "$arg" in
      --uninstall) uninstall=1 ;;
      -h|--help) usage; exit 0 ;;
      *) game="$arg" ;;
    esac
  done

  if [ -z "$game" ]; then
    game="$(find_game)" || fail "couldn't find Star Trek: Bridge Crew in your Steam libraries.
Pass the game folder:  bash install.sh \"/path/to/Star Trek Bridge Crew\""
  fi
  [ -f "$game/stbc.exe" ] || fail "\"$game\" isn't the Bridge Crew folder (no stbc.exe there)."
  say "Game folder: $game"

  if pgrep -f 'stbc\.exe' >/dev/null 2>&1; then
    fail "the game is running. Quit it first."
  fi

  # ---- Uninstall ------------------------------------------------------------------------------

  if [ "$uninstall" = 1 ]; then
    rm -f "$game/BepInEx/plugins/StbcRevival.dll" "$game/BepInEx/plugins/StbcRevival.pdb" \
          "$game/BepInEx/plugins/StbcCore.dll" "$game/BepInEx/plugins/StbcCore.pdb"
    # Without winhttp.dll BepInEx doesn't load at all; the BepInEx folder is left for you to delete.
    rm -f "$game/winhttp.dll" "$game/doorstop_config.ini" "$game/.doorstop_version" "$game/changelog.txt"
    say "Removed the mod and disabled BepInEx. You can delete \"$game/BepInEx\" too."
    say "Also remove the launch options from the game's Steam properties."
    exit 0
  fi

  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT

  # ---- The mod's files: next to this script (release zip), or the GitHub release --------------

  local here="" plugins
  if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  fi
  if [ -n "$here" ] && [ -f "$here/plugins/StbcRevival.dll" ]; then
    plugins="$here/plugins"
  else
    local base="https://github.com/$REPO/releases/latest/download"
    [ -n "${STBC_VERSION:-}" ] && base="https://github.com/$REPO/releases/download/v${STBC_VERSION#v}"
    say "Downloading Bridge Crew Revival ${STBC_VERSION:-(latest release)}..."
    download "$base/BridgeCrewRevival.zip" "$tmp/mod.zip"
    download "$base/BridgeCrewRevival.zip.sha256" "$tmp/mod.zip.sha256"
    echo "$(cut -d' ' -f1 "$tmp/mod.zip.sha256")  $tmp/mod.zip" | sha256sum -c --quiet - \
      || fail "Bridge Crew Revival download checksum mismatch."
    extract "$tmp/mod.zip" "$tmp/mod"
    plugins="$(dirname "$(find "$tmp/mod" -name StbcRevival.dll | head -n1)")"
  fi
  [ -f "$plugins/StbcRevival.dll" ] && [ -f "$plugins/StbcCore.dll" ] || fail "the mod's files are missing."

  # ---- BepInEx --------------------------------------------------------------------------------

  if [ -f "$game/winhttp.dll" ] && [ -f "$game/BepInEx/core/BepInEx.dll" ]; then
    say "BepInEx is already installed."
  else
    say "Downloading BepInEx $BEPINEX_VERSION..."
    download "$BEPINEX_URL" "$tmp/bepinex.zip"
    echo "$BEPINEX_SHA256  $tmp/bepinex.zip" | sha256sum -c --quiet - || fail "BepInEx download checksum mismatch."
    extract "$tmp/bepinex.zip" "$game"
    say "BepInEx installed."
  fi

  # Unity 5.4 needs BepInEx to start from MonoBehaviour's static constructor: the default
  # (Application) loads plugins during the script-domain reload and the game hangs on a black screen.
  local cfg="$game/BepInEx/config/BepInEx.cfg"
  mkdir -p "$game/BepInEx/config" "$game/BepInEx/plugins"
  if [ -f "$cfg" ]; then
    # Only the Type key inside [Preloader.Entrypoint]; keeps BepInEx's CRLF line endings.
    sed -i '/^\[Preloader\.Entrypoint\]/,/^\[/ s/^Type = [^\r]*/Type = MonoBehaviour/' "$cfg"
  else
    # BepInEx fills in every other setting on first launch.
    printf '[Preloader.Entrypoint]\n\nAssembly = UnityEngine.dll\n\nType = MonoBehaviour\n\nMethod = .cctor\n' > "$cfg"
  fi
  grep -q '^Type = MonoBehaviour' "$cfg" || fail "couldn't set the BepInEx entrypoint in $cfg."

  # ---- Install ----------------------------------------------------------------------------------

  cp "$plugins/"Stbc*.dll "$game/BepInEx/plugins/"
  cp "$plugins/"Stbc*.pdb "$game/BepInEx/plugins/" 2>/dev/null || true
  say "Bridge Crew Revival installed."
  say ""
  say "One more step, in Steam: right-click Star Trek: Bridge Crew > Properties > General >"
  say "Launch Options, and set:"
  say ""
  say "    $LAUNCH_OPTIONS"
  say ""
  say "Proton then loads BepInEx. Settings: BepInEx/config/stbc.revival.cfg (created on first launch)."
  say "Log: BepInEx/LogOutput.log"
}

# Everything runs from here, so a partly downloaded script (curl | bash) does nothing.
main "$@"

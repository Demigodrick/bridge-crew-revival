#!/usr/bin/env bash
# Bridge Crew Revival installer for Linux (Steam + Proton).
#
#   curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash
#   curl -fsSL https://demigodrick.github.io/bridge-crew-revival/install.sh | bash -s -- --uninstall
#
# or from the release zip:  ./install.sh  [game folder]  [--uninstall]  [--import-save | --no-import]
#
# Finds the game in your Steam libraries, installs BepInEx 5 (from its GitHub release, checksum
# verified), sets the BepInEx entrypoint this game needs, and copies the mod (from the zip it's in,
# or the latest GitHub release). Run it again to update. It then offers to copy your settings from your
# Ubisoft cloud save (avatar, options) by starting the game once.
set -euo pipefail

REPO="Demigodrick/bridge-crew-revival"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
BEPINEX_SHA256="82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
APP_ID=527100
LAUNCH_OPTIONS='WINEDLLOVERRIDES="winhttp=n,b" %command%'

say()   { printf '%s\n' "$*"; }
step()  { printf '\n%s\n' "$*"; }        # a numbered step heading
ok()    { printf '  OK   %s\n' "$*"; }  # something done
info()  { printf '       %s\n' "$*"; }  # detail under a step
warn()  { printf '  !!   %s\n' "$*"; }  # needs the player's attention
fail()  { printf '\n  Error: %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Bridge Crew Revival installer (Linux)
  install.sh [game folder] [--uninstall] [--import-save | --no-import]
  --import-save  copy your Ubisoft cloud save (starts the game once; Ubisoft Connect must be signed in)
  --no-import    don't ask about it
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

# ---- Ubisoft save import ------------------------------------------------------------------------

ask() { # question -> 0 for yes; reads the terminal even when the script is piped into bash
  local answer=""
  { : < /dev/tty; } 2>/dev/null || return 1   # no terminal (automated run): don't ask
  printf '%s [y/N] ' "$1" > /dev/tty
  read -r answer < /dev/tty || return 1
  case "$answer" in [yY]*) return 0 ;; *) return 1 ;; esac
}

set_import_flag() { # game value
  local cfg="$1/BepInEx/config/stbc.revival.cfg"
  if [ -f "$cfg" ] && grep -q '^FetchUbisoftSave' "$cfg"; then
    sed -i "s/^FetchUbisoftSave = .*/FetchUbisoftSave = $2/" "$cfg"
  elif [ "$2" = true ]; then
    printf '\n[Import]\n\nFetchUbisoftSave = true\n' >> "$cfg"
  fi
}

launch_option_set() { # the WINEDLLOVERRIDES launch option, in any Steam user's settings
  local root
  for root in "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam"; do
    grep -qs 'winhttp=n,b' "$root"/userdata/*/config/localconfig.vdf && return 0
  done
  return 1
}

# ---- Proton prefix --------------------------------------------------------------------------------

# Load winhttp.dll (BepInEx's loader) from the game folder in the game's own Wine prefix. Unlike the
# WINEDLLOVERRIDES launch option this also covers the game when Ubisoft Connect restarts it, and nobody
# has to edit Steam settings. The prefix exists once the game has been started under Proton.
prefix_reg() { # game -> path of the prefix's user.reg
  printf '%s\n' "$(cd "$1/../.." && pwd)/compatdata/$APP_ID/pfx/user.reg"
}

set_prefix_override() { # game on|off -> 0 if the prefix now has the override (or, for off, doesn't)
  local reg
  reg="$(prefix_reg "$1")"
  [ -f "$reg" ] || return 1
  [ -f "$reg.stbc-backup" ] || cp "$reg" "$reg.stbc-backup"
  # Drop any winhttp line in [Software\\Wine\\DllOverrides], then (for on) add ours right after the
  # section header and its #time line; add the section if the prefix has none.
  awk -v on="$2" '
    function add() { if (on == "on") print "\"winhttp\"=\"native,builtin\""; added = 1 }
    index($0, "[Software\\\\Wine\\\\DllOverrides]") == 1 { print; insection = 1; found = 1; next }
    insection && /^#time=/ { print; next }
    insection && !added { add() }
    insection && /^"winhttp"=/ { next }
    insection && (/^\[/ || /^$/) { insection = 0 }
    { print }
    END {
      if (!found && on == "on") { print ""; print "[Software\\\\Wine\\\\DllOverrides]"; add() }
    }' "$reg" > "$reg.stbc-tmp" && mv "$reg.stbc-tmp" "$reg"
  if [ "$2" = on ]; then grep -q '^"winhttp"="native,builtin"' "$reg"; else return 0; fi
}

start_game() {
  local url="steam://rungameid/$APP_ID"
  if command -v steam >/dev/null; then steam "$url" >/dev/null 2>&1 &
  elif command -v flatpak >/dev/null && flatpak info com.valvesoftware.Steam >/dev/null 2>&1; then
    flatpak run com.valvesoftware.Steam "$url" >/dev/null 2>&1 &
  else xdg-open "$url" >/dev/null 2>&1 &
  fi
}

# Report what the game is doing, from its Unity log (the mod's messages are copied there too).
watch_log() { # log from-line -> prints new milestones, echoes the new line count
  local log="$1" from="$2" total line
  total=$( { wc -l < "$log"; } 2>/dev/null || echo 0)
  [ "$total" -lt "$from" ] && from=0   # the log was restarted
  if [ "$total" -gt "$from" ]; then
    tail -n +"$((from + 1))" "$log" | head -n "$((total - from))" | while IFS= read -r line; do
      case "$line" in
        "Initialize engine version"*) info "- game starting" >&2 ;;
        *"Bridge Crew Revival"*" loaded."*) info "- mod loaded" >&2 ;;
        *"Ubisoft save import: this run"*) info "- waiting for your save: choose your controller in the game if it asks" >&2 ;;
        *"Ubisoft save import: quitting"*) info "- closing the game" >&2 ;;
      esac
    done
  fi
  echo "$total"
}

# The import starts Ubisoft Connect inside the game's Proton prefix, and it stays running afterwards,
# which keeps Steam showing the game as running. Close it, but only the copy in this game's prefix:
# other Ubisoft games have their own prefixes and their own copies.
close_ubisoft_connect() {
  local pid closed=0 waited=0
  while pgrep -f '\\stbc\.exe' >/dev/null 2>&1 && [ "$waited" -lt 30 ]; do sleep 2; waited=$((waited + 2)); done
  for pid in $(pgrep -f 'Ubisoft Game Launcher' 2>/dev/null); do
    tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -q "compatdata/$APP_ID" || continue
    kill "$pid" 2>/dev/null && closed=1
  done
  [ "$closed" = 1 ] && ok "Closed Ubisoft Connect (it was only needed for the import)"
  return 0
}

import_save() { # game -> prints the outcome
  local game="$1" result="$1/BepInEx/config/stbc.revival.import.txt" log="$1/stbc_Data/output_log.txt"
  local waited=0 seen loaded=0
  if ! grep -qs '^"winhttp"="native,builtin"' "$(prefix_reg "$game")" && ! launch_option_set; then
    warn "Can't import yet: the mod won't load until the game has been started once."
    info "Start the game from Steam, quit it, then run this installer again."
    return
  fi
  rm -f "$result"
  set_import_flag "$game" true
  seen=$( { wc -l < "$log"; } 2>/dev/null || echo 0)
  info "Starting Star Trek: Bridge Crew. When it asks you to choose a controller, choose one:"
  info "the game only connects to Ubisoft after that. It closes by itself when it's done."
  start_game
  while [ ! -f "$result" ] && [ "$waited" -lt 420 ]; do
    sleep 5; waited=$((waited + 5))
    seen=$(watch_log "$log" "$seen")
    grep -qs 'Bridge Crew Revival.* loaded\.' "$log" && loaded=1
    if [ $((waited % 30)) -eq 0 ]; then
      info "... still waiting ($((waited / 60))m$((waited % 60))s)"
      [ "$loaded" = 0 ] && [ "$waited" -ge 90 ] && info "    The mod hasn't loaded yet. If the game is at its menu, quit it: it started without the mod."
    fi
  done
  if [ ! -f "$result" ]; then
    set_import_flag "$game" false
    warn "Your save didn't arrive within 7 minutes, so nothing was imported."
    info "Run the installer again to retry. (Details: BepInEx/LogOutput.log in the game folder.)"
    return
  fi
  case "$(sed -n 1p "$result")" in
    ok)   ok "$(sed -n 2p "$result")"; close_ubisoft_connect ;;
    none) ok "Your Ubisoft account has no Bridge Crew save, so there was nothing to import."; close_ubisoft_connect ;;
    *)    warn "$(sed -n 2p "$result")"; info "Run the installer again to retry." ;;
  esac
}

main() {
  local uninstall=0 game="" import="" arg
  for arg in "$@"; do
    case "$arg" in
      --uninstall) uninstall=1 ;;
      --import-save) import=yes ;;
      --no-import) import=no ;;
      -h|--help) usage; exit 0 ;;
      *) game="$arg" ;;
    esac
  done

  say "Bridge Crew Revival installer"
  say "Adds online multiplayer over Steam to Star Trek: Bridge Crew. Nothing to set up: just answer"
  say "the questions as they come."

  step "1. Finding the game"
  if [ -z "$game" ]; then
    game="$(find_game)" || fail "couldn't find Star Trek: Bridge Crew in your Steam libraries.
  If it's installed somewhere unusual, run the installer with its folder:
    bash install.sh \"/path/to/Star Trek Bridge Crew\""
  fi
  [ -f "$game/stbc.exe" ] || fail "\"$game\" isn't the Bridge Crew folder (there's no stbc.exe in it)."
  ok "Found it: $game"

  # The game itself runs as "S:\...\stbc.exe"; Proton's launcher wrapper (".../stbc.exe") lingers while
  # anything else, e.g. Ubisoft Connect, still runs in the game's prefix, so match the backslash.
  if pgrep -f '\\stbc\.exe' >/dev/null 2>&1; then
    fail "Star Trek: Bridge Crew is running. Quit it, then run the installer again."
  fi

  # ---- Uninstall ------------------------------------------------------------------------------

  if [ "$uninstall" = 1 ]; then
    step "2. Removing Bridge Crew Revival"
    rm -f "$game/BepInEx/plugins/StbcRevival.dll" "$game/BepInEx/plugins/StbcRevival.pdb" \
          "$game/BepInEx/plugins/StbcCore.dll" "$game/BepInEx/plugins/StbcCore.pdb"
    # Without winhttp.dll BepInEx doesn't load at all; the BepInEx folder is left for you to delete.
    rm -f "$game/winhttp.dll" "$game/doorstop_config.ini" "$game/.doorstop_version" "$game/changelog.txt"
    set_prefix_override "$game" off || true
    ok "Removed the mod and switched off its loader (BepInEx)"
    info "Your settings are left in \"$game/BepInEx\"; delete that folder too if you like."
    info "If you ever set launch options for the game in Steam, you can clear them."
    say ""
    say "Done. Star Trek: Bridge Crew is back to normal."
    exit 0
  fi

  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT

  step "2. Installing"

  # ---- The mod's files: next to this script (release zip), or the GitHub release --------------

  local here="" plugins version
  if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  fi
  if [ -n "$here" ] && [ -f "$here/plugins/StbcRevival.dll" ]; then
    plugins="$here/plugins"
  else
    local base="https://github.com/$REPO/releases/latest/download"
    [ -n "${STBC_VERSION:-}" ] && base="https://github.com/$REPO/releases/download/v${STBC_VERSION#v}"
    info "Downloading Bridge Crew Revival..."
    download "$base/BridgeCrewRevival.zip" "$tmp/mod.zip"
    download "$base/BridgeCrewRevival.zip.sha256" "$tmp/mod.zip.sha256"
    echo "$(cut -d' ' -f1 "$tmp/mod.zip.sha256")  $tmp/mod.zip" | sha256sum -c --quiet - \
      || fail "the Bridge Crew Revival download was damaged (checksum mismatch). Try again."
    extract "$tmp/mod.zip" "$tmp/mod"
    plugins="$(dirname "$(find "$tmp/mod" -name StbcRevival.dll | head -n1)")"
  fi
  [ -f "$plugins/StbcRevival.dll" ] && [ -f "$plugins/StbcCore.dll" ] || fail "the mod's files are missing."
  version="$(basename "$(dirname "$plugins")")"   # BridgeCrewRevival-<version>
  version="${version#BridgeCrewRevival-}"
  case "$version" in [0-9]*) ;; *) version="" ;; esac

  # ---- BepInEx --------------------------------------------------------------------------------

  if [ -f "$game/winhttp.dll" ] && [ -f "$game/BepInEx/core/BepInEx.dll" ]; then
    ok "Mod loader (BepInEx $BEPINEX_VERSION) already installed"
  else
    info "Downloading the mod loader (BepInEx $BEPINEX_VERSION)..."
    download "$BEPINEX_URL" "$tmp/bepinex.zip"
    echo "$BEPINEX_SHA256  $tmp/bepinex.zip" | sha256sum -c --quiet - \
      || fail "the BepInEx download was damaged (checksum mismatch). Try again."
    extract "$tmp/bepinex.zip" "$game"
    ok "Mod loader (BepInEx $BEPINEX_VERSION) installed"
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
  grep -q '^Type = MonoBehaviour' "$cfg" || fail "couldn't set up the mod loader (in $cfg)."

  # ---- Install ----------------------------------------------------------------------------------

  cp "$plugins/"Stbc*.dll "$game/BepInEx/plugins/"
  cp "$plugins/"Stbc*.pdb "$game/BepInEx/plugins/" 2>/dev/null || true
  ok "Bridge Crew Revival${version:+ $version} installed"
  local loader_on=1
  if set_prefix_override "$game" on; then
    ok "Mod loader switched on for the game (no Steam launch options needed)"
  elif launch_option_set; then
    ok "Mod loader switched on by the game's Steam launch options"
  else
    loader_on=0
  fi

  # ---- Ubisoft save ---------------------------------------------------------------------------

  step "3. Your avatar and settings (optional)"
  if [ "$import" != yes ] && [ -f "$game/BepInEx/config/stbc.revival.save.json" ]; then
    ok "Already kept on this PC; nothing to do"
  elif [ "$import" = no ]; then
    info "Skipped"
  else
    info "Bridge Crew only saved your avatar and settings on Ubisoft's servers. They can be copied to"
    info "this PC once: the game starts, signs in with Ubisoft Connect (which must be installed and"
    info "signed in), copies your save and closes again. It takes a minute or two."
    if [ "$import" = yes ] || ask "       Copy them now?"; then
      import_save "$game"
    else
      info "Skipped. To do it later, run the installer again and answer yes."
    fi
  fi

  # ---- Summary ----------------------------------------------------------------------------------

  say ""
  if [ "$loader_on" = 1 ]; then
    say "All done: Bridge Crew Revival${version:+ $version} is installed."
    say "Launch Star Trek: Bridge Crew from Steam as usual. To update later, run the same command again."
  else
    say "Installed, with one step left: the game has never been started on this PC, so the mod loader"
    say "can't be switched on yet. Start the game once from Steam, quit it, and run this installer again."
    say "(Or set the game's Steam launch options, Properties > General, to: $LAUNCH_OPTIONS)"
  fi
}

# Everything runs from here, so a partly downloaded script (curl | bash) does nothing.
main "$@"

#!/usr/bin/env bash
# Melvor2Tweaks installer for Linux, Steam Deck and macOS.
#
#   Install or update:  curl -fsSL https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.sh | bash
#   Uninstall:          curl -fsSL https://raw.githubusercontent.com/ZeldoKavira/Melvor2Tweaks/main/install/install.sh | bash -s -- --uninstall
#   Game elsewhere:     ... | bash -s -- --game-dir "/path/to/Melvor Idle 2"
#
# Finds Melvor Idle 2 in your Steam libraries (native, Flatpak and Snap Steam; on a Steam Deck also the SD
# card), downloads the latest release from GitHub, copies it into the game folder and patches the game once.
# After that the mod keeps itself up to date and re-applies its patch by itself after game updates.
# If you run the Windows version through Proton, this installs the Windows files and the game patches
# itself on its first start (it restarts once).
set -euo pipefail

REPO="ZeldoKavira/Melvor2Tweaks"
APPID="3218350"
GAME_DIR=""
UNINSTALL=0

while [ $# -gt 0 ]; do
  case "$1" in
    --uninstall) UNINSTALL=1 ;;
    --game-dir) GAME_DIR="${2:-}"; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

say() { echo "Melvor2Tweaks: $*"; }
fail() { echo "Melvor2Tweaks: $*" >&2; exit 1; }

steam_roots() {
  for r in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" \
           "$HOME/snap/steam/common/.local/share/Steam" "$HOME/Library/Application Support/Steam"; do
    [ -d "$r" ] && echo "$r"
  done
}

steam_libraries() {
  steam_roots | while IFS= read -r root; do
    echo "$root"
    vdf="$root/steamapps/libraryfolders.vdf"
    [ -f "$vdf" ] && grep -E '^\s*"path"' "$vdf" | sed -E 's/.*"path"[[:space:]]+"([^"]+)".*/\1/'
  done | awk '!seen[$0]++'
}

find_game() {
  steam_libraries | while IFS= read -r lib; do
    manifest="$lib/steamapps/appmanifest_$APPID.acf"
    [ -f "$manifest" ] || continue
    dir="$(grep -E '"installdir"' "$manifest" | sed -E 's/.*"installdir"[[:space:]]+"([^"]+)".*/\1/')"
    [ -n "$dir" ] || dir="Melvor Idle 2"
    [ -d "$lib/steamapps/common/$dir" ] && { echo "$lib/steamapps/common/$dir"; break; }
  done
}

# The folder holding the game's data_GamesByMalcs.Melvor2_* folder: mods/ and override.cfg go here
# (the game folder; "<name>.app/Contents/Resources" on macOS).
find_data_dir() {
  find "$1" -maxdepth 4 -type f -name 'GamesByMalcs.Melvor2.dll' -path '*data_GamesByMalcs.Melvor2_*' 2>/dev/null | head -n1 | xargs -I{} dirname "{}"
}

unzip_to() {
  if command -v unzip >/dev/null 2>&1; then unzip -oq "$1" -d "$2"
  elif command -v bsdtar >/dev/null 2>&1; then bsdtar -xf "$1" -C "$2"
  elif command -v ditto >/dev/null 2>&1; then ditto -x -k "$1" "$2"
  elif command -v python3 >/dev/null 2>&1; then python3 -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$1" "$2"
  else fail "need unzip, bsdtar or python3 to unpack the download"; fi
}

remove_our_override() {
  cfg="$1/override.cfg"
  [ -f "$cfg" ] || return 0
  grep -v '^[[:space:]]*Melvor2TweaksBoot=' "$cfg" > "$cfg.tmp" || true
  if grep -qvE '^[[:space:]]*(\[autoload\])?[[:space:]]*$' "$cfg.tmp"; then mv "$cfg.tmp" "$cfg"; else rm -f "$cfg.tmp" "$cfg"; fi
}

merge_override() {
  cfg="$1/override.cfg"; ours="$2"
  line="$(grep -m1 '^Melvor2TweaksBoot=' "$ours")"
  if [ ! -f "$cfg" ]; then cp "$ours" "$cfg"; return; fi
  grep -q 'Melvor2TweaksBoot=' "$cfg" && return
  if grep -q '^\[autoload\]' "$cfg"; then
    awk -v l="$line" '{print} /^\[autoload\]/{print l}' "$cfg" > "$cfg.tmp" && mv "$cfg.tmp" "$cfg"
  else
    printf '\n[autoload]\n\n%s\n' "$line" >> "$cfg"
  fi
}

# ---------------------------------------------------------------------------------------------------
[ -n "$GAME_DIR" ] || GAME_DIR="$(find_game || true)"
[ -n "$GAME_DIR" ] && [ -d "$GAME_DIR" ] || fail "couldn't find Melvor Idle 2 in your Steam libraries. Run again with --game-dir \"<game folder>\"."
DATA_DIR="$(find_data_dir "$GAME_DIR")"
[ -n "$DATA_DIR" ] || fail "no data_GamesByMalcs.Melvor2_* folder under $GAME_DIR - is that the Melvor Idle 2 folder?"
ROOT="$(dirname "$DATA_DIR")"
say "game found at $ROOT"

if pgrep -if 'Melvor2\.(exe|x86_64|arm64)|Contents/MacOS/Melvor' >/dev/null 2>&1; then
  fail "close Melvor Idle 2 first, then run this again."
fi

case "$(basename "$DATA_DIR")" in
  *windows*) PLATFORM=windows ;;   # the Windows build under Proton
  *macos*) PLATFORM=macos ;;
  *) PLATFORM=linux ;;
esac
OS="$(uname -s)"
MODS="$ROOT/mods"

patcher_path() {
  case "$PLATFORM" in
    windows) echo "$MODS/patcher/Melvor2Patcher.exe" ;;
    macos) [ "$(uname -m)" = "arm64" ] && echo "$MODS/patcher/Melvor2Patcher-arm64" || echo "$MODS/patcher/Melvor2Patcher-x64" ;;
    *) echo "$MODS/patcher/Melvor2Patcher" ;;
  esac
}
# A Windows patcher can't run here: under Proton the game patches itself on its first start instead.
can_run_patcher() { [ "$PLATFORM" != windows ]; }

if [ "$UNINSTALL" = 1 ]; then
  P="$(patcher_path)"
  if [ -f "$P" ] && can_run_patcher; then chmod +x "$P"; "$P" "$ROOT" --restore || true
  elif [ "$PLATFORM" = windows ]; then
    # No native patcher for the Proton build: put the original DLL back ourselves.
    [ -f "$DATA_DIR/GamesByMalcs.Melvor2.dll.orig" ] && cp -f "$DATA_DIR/GamesByMalcs.Melvor2.dll.orig" "$DATA_DIR/GamesByMalcs.Melvor2.dll" && rm -f "$DATA_DIR/GamesByMalcs.Melvor2.dll.orig"
  fi
  for f in Melvor2Loader.dll Melvor2Loader.pdb Melvor2Loader.log 0Harmony.dll Melvor2Tweaks.dll Melvor2Tweaks.pdb \
           melvor2_boot.gd melvor2_boot.log patch.stamp THIRD-PARTY-NOTICES.txt; do rm -f "$MODS/$f"; done
  rm -f "$MODS"/*.old* 2>/dev/null || true
  rm -rf "$MODS/patcher"
  rmdir "$MODS" 2>/dev/null || true
  remove_our_override "$ROOT"
  [ "$OS" = Darwin ] && remove_our_override "$(dirname "$ROOT")/MacOS"
  say "uninstalled. (Your settings in the game's user-data folder were kept.)"
  exit 0
fi

say "looking up the latest release..."
API_JSON="$(curl -fsSL -H 'User-Agent: Melvor2Tweaks-Installer' -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/releases/latest")"
TAG="$(printf '%s' "$API_JSON" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')"
URL="$(printf '%s' "$API_JSON" | grep '"browser_download_url"' | sed -E 's/.*"browser_download_url"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' | grep -- "-$PLATFORM.zip" | head -n1)"
[ -n "$URL" ] || fail "release $TAG has no $PLATFORM download."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
say "downloading $(basename "$URL") ($TAG)..."
curl -fsSL -H 'User-Agent: Melvor2Tweaks-Installer' -o "$TMP/release.zip" "$URL"
mkdir -p "$TMP/files"
unzip_to "$TMP/release.zip" "$TMP/files"

mkdir -p "$MODS"
( cd "$TMP/files" && find . -type f ! -name override.cfg ) | while IFS= read -r rel; do
  rel="${rel#./}"
  mkdir -p "$ROOT/$(dirname "$rel")"
  cp -f "$TMP/files/$rel" "$ROOT/$rel"
done
merge_override "$ROOT" "$TMP/files/override.cfg"
# macOS: Godot may read override.cfg beside the executable (Contents/MacOS) too.
[ "$OS" = Darwin ] && [ -d "$(dirname "$ROOT")/MacOS" ] && merge_override "$(dirname "$ROOT")/MacOS" "$TMP/files/override.cfg"
chmod +x "$MODS"/patcher/Melvor2Patcher* 2>/dev/null || true
[ "$OS" = Darwin ] && xattr -dr com.apple.quarantine "$MODS" 2>/dev/null || true

if can_run_patcher; then
  say "patching the game..."
  "$(patcher_path)" "$ROOT" | sed 's/^/Melvor2Tweaks: /'
  say "installed $TAG. Start the game from Steam; the mod's buttons are in the top bar."
else
  say "installed $TAG (Windows build under Proton). The first start patches the game and restarts it once."
fi

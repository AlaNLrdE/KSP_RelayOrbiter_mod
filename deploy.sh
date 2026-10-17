#!/usr/bin/env bash
# Builds the RelayOrbiter plugin and copies GameData/RelayOrbiter into the
# GameData folder of the Steam install of KSP, found automatically.
#
# Usage: ./deploy.sh [--no-build] [--dry-run] [KSP_DIR]
#   --no-build  copy what is already staged in GameData/RelayOrbiter
#   --dry-run   only show where it would deploy
#   KSP_DIR     KSP install to use instead of detecting the Steam one
#
# Works from WSL (Windows Steam) and from Linux (native Steam).
set -euo pipefail

KSP_APP_ID=220200
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MOD_DIR="$ROOT/GameData/RelayOrbiter"

build=true
dry_run=false
ksp_dir=""
for arg in "$@"; do
    case "$arg" in
        --no-build) build=false ;;
        --dry-run)  dry_run=true ;;
        -h|--help)  sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*)         echo "Unknown option: $arg" >&2; exit 2 ;;
        *)          ksp_dir="$arg" ;;
    esac
done

# ── Detection ─────────────────────────────────────────────────────────────────

# Windows path (D:\SteamLibrary) to a path usable here (/mnt/d/SteamLibrary).
to_local_path() {
    local p="${1//\\\\/\\}"   # libraryfolders.vdf escapes backslashes
    if [[ "$p" =~ ^[A-Za-z]: ]] && command -v wslpath >/dev/null; then
        wslpath -u "$p"
    else
        printf '%s\n' "$p"
    fi
}

steam_roots() {
    # Steam's own record of where it is installed (Windows registry, via WSL interop).
    if command -v reg.exe >/dev/null 2>&1; then
        local win
        win=$(reg.exe query 'HKCU\Software\Valve\Steam' /v SteamPath 2>/dev/null \
              | tr -d '\r' | sed -n 's/.*SteamPath[[:space:]]*REG_SZ[[:space:]]*//p')
        [[ -n "$win" ]] && to_local_path "${win//\//\\}"
    fi
    local d
    for d in /mnt/?/"Program Files (x86)"/Steam /mnt/?/"Program Files"/Steam \
             "$HOME/.steam/steam" "$HOME/.local/share/Steam"; do
        [[ -d "$d" ]] && printf '%s\n' "$d"
    done
}

steam_libraries() {
    local root vdf
    while IFS= read -r root; do
        printf '%s\n' "$root"
        for vdf in "$root/steamapps/libraryfolders.vdf" "$root/config/libraryfolders.vdf"; do
            [[ -f "$vdf" ]] || continue
            sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf" | tr -d '\r' |
                while IFS= read -r p; do to_local_path "$p"; done
        done
    done < <(steam_roots)
}

find_steam_ksp() {
    local lib acf installdir dir
    while IFS= read -r lib; do
        acf="$lib/steamapps/appmanifest_$KSP_APP_ID.acf"
        if [[ -f "$acf" ]]; then
            installdir=$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)".*/\1/p' "$acf" | tr -d '\r' | head -n1)
            dir="$lib/steamapps/common/${installdir:-Kerbal Space Program}"
        else
            dir="$lib/steamapps/common/Kerbal Space Program"
        fi
        if [[ -d "$dir/GameData" ]]; then
            printf '%s\n' "$dir"
            return 0
        fi
    done < <(steam_libraries | awk '!seen[$0]++')
    return 1
}

if [[ -z "$ksp_dir" ]]; then
    if ! ksp_dir=$(find_steam_ksp); then
        echo "Could not find a Steam install of KSP (app $KSP_APP_ID)." >&2
        echo "Pass its folder explicitly: $0 \"/mnt/d/SteamLibrary/steamapps/common/Kerbal Space Program\"" >&2
        exit 1
    fi
fi
if [[ ! -d "$ksp_dir/GameData" ]]; then
    echo "Not a KSP install (no GameData folder): $ksp_dir" >&2
    exit 1
fi
echo "KSP install: $ksp_dir"

if $dry_run; then
    echo "Dry run: would deploy $MOD_DIR -> $ksp_dir/GameData/RelayOrbiter"
    exit 0
fi

# Windows locks the plugin DLLs while the game runs.
if command -v tasklist.exe >/dev/null 2>&1 && tasklist.exe 2>/dev/null | grep -qi '^KSP_x64.exe'; then
    echo "KSP is running. Close it before deploying." >&2
    exit 1
fi

# ── Build ─────────────────────────────────────────────────────────────────────

if $build; then
    dotnet=dotnet
    if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export DOTNET_ROOT="$HOME/.dotnet"
        dotnet="$HOME/.dotnet/dotnet"
    fi
    # Without KspPaths.props, compile against the install being deployed to.
    extra=()
    [[ -f "$ROOT/KspPaths.props" ]] || extra=("-p:KSPDir=$ksp_dir")
    echo "Building (Release)..."
    "$dotnet" build "$ROOT/src/RelayOrbiter.Plugin" -c Release --nologo -v quiet "${extra[@]}"
fi

shopt -s nullglob
dlls=("$MOD_DIR"/Plugins/*.dll)
if (( ${#dlls[@]} == 0 )); then
    echo "Nothing to deploy: $MOD_DIR/Plugins has no DLLs. Run without --no-build." >&2
    exit 1
fi

# ── Deploy ────────────────────────────────────────────────────────────────────

dest="$ksp_dir/GameData/RelayOrbiter"
mkdir -p "$dest"
cp -rf "$MOD_DIR/." "$dest/"

echo "Deployed to $dest:"
find "$dest" -type f -printf '  %P  (%s bytes, %TY-%Tm-%Td %TH:%TM)\n' | sort

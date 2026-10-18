#!/usr/bin/env bash
# Builds the mod in Release and packs the release zip:
#   dist/RelayOrbiter-<version>.zip
#     GameData/RelayOrbiter/Plugins/RelayOrbiter.dll, RelayOrbiter.Core.dll
#     GameData/RelayOrbiter/LICENSE, README.md
# The version comes from Directory.Build.props.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)
[[ -n "$VERSION" ]] || { echo "No <Version> in Directory.Build.props" >&2; exit 1; }

dotnet=dotnet
if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    dotnet="$HOME/.dotnet/dotnet"
fi

echo "Building RelayOrbiter $VERSION (Release)..."
rm -rf "$ROOT/GameData/RelayOrbiter/Plugins"
"$dotnet" build "$ROOT/src/RelayOrbiter.Plugin" -c Release --nologo -v quiet
"$dotnet" test "$ROOT/tests/RelayOrbiter.Core.Tests" --nologo -v quiet

mkdir -p "$ROOT/dist"
ZIP="$ROOT/dist/RelayOrbiter-$VERSION.zip"
rm -f "$ZIP"
python3 -I - "$ROOT" "$ZIP" <<'PY'
import sys, zipfile
from pathlib import Path
root, out = Path(sys.argv[1]), sys.argv[2]
mod = root / "GameData" / "RelayOrbiter"
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(mod.rglob("*")):
        if f.is_file() and "PluginData" not in f.parts:  # never ship a local config.xml
            z.write(f, f.relative_to(root))
    for extra in ("LICENSE", "README.md"):
        z.write(root / extra, f"GameData/RelayOrbiter/{extra}")
    for info in z.infolist():
        print(f"  {info.filename}  ({info.file_size} bytes)")
PY
echo "Wrote $ZIP"

#!/usr/bin/env bash
# Builds the plugin and assembles a deployable folder for a Jellyfin 10.11.x
# plugin directory (e.g. /config/plugins/Colorist_<version> in the container).
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
OUT="artifacts/Colorist_${VERSION}"

dotnet build Jellyfin.Plugin.Colorist/Jellyfin.Plugin.Colorist.csproj -c Release -p:Version="${VERSION%.*}"

rm -rf "$OUT"
mkdir -p "$OUT"
cp Jellyfin.Plugin.Colorist/bin/Release/net9.0/Jellyfin.Plugin.Colorist.dll "$OUT/"

cat > "$OUT/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Samples the dominant colour of frames across a video and renders them as a vertical-stripe movie barcode.",
  "guid": "1dd662e3-27c3-4e43-bbfe-108509a0b84f",
  "name": "Colorist",
  "overview": "Movie barcodes for your library.",
  "owner": "nitramivel",
  "targetAbi": "${TARGET_ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": ""
}
EOF

echo "Packaged: $OUT"

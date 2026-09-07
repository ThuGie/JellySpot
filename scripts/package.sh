#!/usr/bin/env bash
set -euo pipefail

OUT_DIR="${1:-artifacts}"
VERSION="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/Jellyfin.Plugin.JellySpot/Jellyfin.Plugin.JellySpot.csproj"
BUILD_DIR="$ROOT/Jellyfin.Plugin.JellySpot/bin/Release/net9.0"
STAGE="$ROOT/.package-stage"

if [[ "$OUT_DIR" != /* ]]; then
  OUT_DIR="$ROOT/$OUT_DIR"
fi

if [[ -z "$VERSION" ]]; then
  VERSION="$(jq -r '.version // "1.0.0.0"' "$ROOT/Jellyfin.Plugin.JellySpot/meta.json")"
fi

dotnet build "$PROJECT" -c Release \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="$VERSION" \
  -p:FileVersion="$VERSION"

rm -rf "$STAGE"
mkdir -p "$STAGE" "$OUT_DIR"

# Plugin zip: managed plugin DLLs only.
# Do NOT ship runtimes/**/e_sqlite3.dll — Jellyfin PluginManager loads every .dll
# as a managed assembly and disables the plugin (BadImageFormatException).
# SQLite comes from the Jellyfin host.
cp "$BUILD_DIR"/Jellyfin.Plugin.JellySpot.dll "$STAGE/"
cp "$ROOT/Jellyfin.Plugin.JellySpot/meta.json" "$STAGE/"
cp "$BUILD_DIR"/YoutubeExplode.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/YoutubeExplode.Converter.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/TagLibSharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/FuzzySharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/AngleSharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/CliWrap.dll "$STAGE/" 2>/dev/null || true

# Plugin icon (local install thumb) + keep catalog asset out of zip
if [[ -f "$ROOT/Jellyfin.Plugin.JellySpot/thumb.png" ]]; then
  cp "$ROOT/Jellyfin.Plugin.JellySpot/thumb.png" "$STAGE/"
elif [[ -f "$ROOT/assets/thumb.png" ]]; then
  cp "$ROOT/assets/thumb.png" "$STAGE/"
fi

# Ensure meta.json points at packaged thumb
jq --arg v "$VERSION" '.version = $v | .imagePath = "thumb.png"' "$STAGE/meta.json" > "$STAGE/meta.tmp"
mv "$STAGE/meta.tmp" "$STAGE/meta.json"

ZIP_NAME="JellySpot_${VERSION}.zip"
ZIP_PATH="$OUT_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"
(
  cd "$STAGE"
  zip -r "$ZIP_PATH" .
)

echo "Created $ZIP_PATH"
md5sum "$ZIP_PATH" || md5 -q "$ZIP_PATH"

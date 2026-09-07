#!/usr/bin/env bash
set -euo pipefail

OUT_DIR="${1:-artifacts}"
VERSION="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/Jellyfin.Plugin.JellySpot/Jellyfin.Plugin.JellySpot.csproj"
BUILD_DIR="$ROOT/Jellyfin.Plugin.JellySpot/bin/Release/net9.0"
STAGE="$ROOT/.package-stage"

if [[ -z "$VERSION" ]]; then
  VERSION="$(jq -r '.version // "1.0.0.0"' "$ROOT/Jellyfin.Plugin.JellySpot/meta.json")"
fi

dotnet build "$PROJECT" -c Release \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="$VERSION" \
  -p:FileVersion="$VERSION"

rm -rf "$STAGE"
mkdir -p "$STAGE" "$OUT_DIR"

# Plugin zip contents: DLLs + meta.json + native runtimes
cp "$BUILD_DIR"/Jellyfin.Plugin.JellySpot.dll "$STAGE/"
cp "$ROOT/Jellyfin.Plugin.JellySpot/meta.json" "$STAGE/"
cp "$BUILD_DIR"/YoutubeExplode.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/YoutubeExplode.Converter.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/TagLibSharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/FuzzySharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/Microsoft.Data.Sqlite.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/SQLitePCLRaw.*.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/AngleSharp.dll "$STAGE/" 2>/dev/null || true
cp "$BUILD_DIR"/CliWrap.dll "$STAGE/" 2>/dev/null || true

if [[ -d "$BUILD_DIR/runtimes" ]]; then
  cp -a "$BUILD_DIR/runtimes" "$STAGE/"
fi

# Refresh version inside staged meta.json
jq --arg v "$VERSION" '.version = $v' "$STAGE/meta.json" > "$STAGE/meta.tmp"
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

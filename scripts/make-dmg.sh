#!/bin/bash
# Packs build/MDEditor.app into a compressed, drag-to-install DMG: build/MDEditor-<version>.dmg
#
#   ./scripts/make-dmg.sh              # builds the app first if needed
#   VERSION=1.2.0 ./scripts/make-dmg.sh
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="MDEditor"
VERSION="${VERSION:-1.1.0}"
APP="build/$APP_NAME.app"
DMG="build/$APP_NAME-$VERSION.dmg"

# Always rebuild so the DMG can never contain a stale app.
VERSION="$VERSION" ./scripts/build.sh

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "▸ Staging DMG contents"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
cp scripts/dmg-readme.txt "$STAGE/Read Me 安装说明.txt"

echo "▸ Creating $DMG"
rm -f "$DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO -imagekey zlib-level=9 "$DMG" >/dev/null
hdiutil verify "$DMG" >/dev/null

echo "✔ $DMG  ($(du -h "$DMG" | cut -f1))"
shasum -a 256 "$DMG"

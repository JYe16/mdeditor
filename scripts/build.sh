#!/bin/bash
# Builds a universal (Apple silicon + Intel) MDEditor.app into ./build
#
#   ./scripts/build.sh                 # version 1.1.0
#   VERSION=1.2.0 ./scripts/build.sh
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="MDEditor"
VERSION="${VERSION:-1.1.0}"
BUILD="${BUILD:-$(date +%Y%m%d%H%M)}"
APP="build/$APP_NAME.app"

echo "▸ Compiling release build (arm64 + x86_64)…"
swift build -c release --arch arm64 --arch x86_64
BIN_DIR="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)"

echo "▸ Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$APP_NAME" "$APP/Contents/MacOS/$APP_NAME"
cp -R Resources/preview "$APP/Contents/Resources/preview"
cp -R Resources/en.lproj Resources/zh-Hans.lproj "$APP/Contents/Resources/"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
sed -e "s/@VERSION@/$VERSION/" -e "s/@BUILD@/$BUILD/" Resources/Info.plist > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
find "$APP" -name .DS_Store -delete
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Ad-hoc signature: lets the app run on Apple silicon without an Apple Developer account.
echo "▸ Signing (ad-hoc)"
codesign --force --deep --sign - --timestamp=none "$APP"
codesign --verify --deep --strict "$APP"

echo "✔ Built $APP  (version $VERSION, build $BUILD)"
echo "  architectures: $(lipo -archs "$APP/Contents/MacOS/$APP_NAME")"

#!/bin/bash
# Bumps the Homebrew cask in the tap repository to a published GitHub release.
#
#   ./scripts/update-tap.sh 1.0.1
#
# Prerequisites: the release v<version> exists in REPO with an MDEditor-<version>.dmg asset
# (the macOS workflow creates it when you push the tag), and `gh` is logged in with push
# access to the tap. Nothing is pushed unless the cask actually changes.
set -euo pipefail

VERSION="${1:?usage: update-tap.sh <version>   e.g. update-tap.sh 1.0.1}"
REPO="${REPO:-JYe16/mdeditor}"
TAP="${TAP:-JYe16/homebrew-tap}"
ASSET="MDEditor-$VERSION.dmg"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "▸ Downloading $ASSET from $REPO v$VERSION"
gh release download "v$VERSION" --repo "$REPO" --pattern "$ASSET" --dir "$WORK"
SHA="$(shasum -a 256 "$WORK/$ASSET" | cut -d' ' -f1)"
echo "  sha256 $SHA"

echo "▸ Updating Casks/mdeditor.rb in $TAP"
gh repo clone "$TAP" "$WORK/tap" -- --depth 1 --quiet
CASK="$WORK/tap/Casks/mdeditor.rb"
sed -i '' -E \
  -e "s/^  version \".*\"/  version \"$VERSION\"/" \
  -e "s/^  sha256 \".*\"/  sha256 \"$SHA\"/" \
  "$CASK"

if git -C "$WORK/tap" diff --quiet; then
  echo "✔ Cask already at $VERSION — nothing to do"
  exit 0
fi
git -C "$WORK/tap" diff
git -C "$WORK/tap" commit --quiet -am "mdeditor $VERSION"
git -C "$WORK/tap" push --quiet
echo "✔ Tap updated: brew upgrade --cask mdeditor"

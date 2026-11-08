#!/bin/bash
# Runs the headless editing-command tests (list continuation, Tab, formatting shortcuts).
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
swiftc -O -o "$OUT/editing-tests" Sources/MDEditor/MarkdownTextView.swift Tests/EditingTests/main.swift
"$OUT/editing-tests"

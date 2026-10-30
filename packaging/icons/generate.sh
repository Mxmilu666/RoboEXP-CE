#!/usr/bin/env bash
# Regenerates RoboExp.icns / RoboExp.ico / RoboExp-256.png from RoboExp.svg (macOS only: AppKit + iconutil).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

swift "$here/render-icons.swift" "$here/RoboExp.svg" "$tmp/RoboExp.iconset" "$here/RoboExp.ico" "$here/RoboExp-256.png"
iconutil -c icns -o "$here/RoboExp.icns" "$tmp/RoboExp.iconset"
mkdir -p "$here/../../src/RoboExp.App/Assets"
cp "$here/RoboExp.ico" "$here/../../src/RoboExp.App/Assets/RoboExp.ico"
cp "$here/RoboExp-256.png" "$here/../../src/RoboExp.App/Assets/RoboExp.png"
echo "wrote $here/RoboExp.icns $here/RoboExp.ico $here/RoboExp-256.png"

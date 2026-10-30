#!/usr/bin/env bash
# Self-contained publish of the desktop app and the `roboexp` CLI, one folder + archive per runtime identifier.
#
# usage: packaging/publish.sh <rid>...      e.g. packaging/publish.sh linux-x64 linux-arm64 win-x64
#
# Output per rid: dist/RoboExp-<version>-<rid>/ containing
#   RoboExp.App[.exe]   desktop app
#   roboexp[.exe]       CLI (the RoboExp.Cli apphost, renamed; it loads RoboExp.Cli.dll)
#   RoboExp.png         256px icon (for .desktop launchers)
# and dist/RoboExp-<version>-<rid>.tar.gz (Linux/macOS, keeps exec bits) or .zip (Windows).
# Both apps share one self-contained runtime folder (same SDK, same runtime pack).
# For a macOS .app bundle use packaging/macos/build-app.sh instead.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="$(tr -d '[:space:]' < "$repo/packaging/VERSION")"
dist="$repo/dist"

[ $# -gt 0 ] || { sed -n '2,13p' "$0"; exit 2; }
mkdir -p "$dist"

for rid in "$@"; do
    case "$rid" in
        win-x64|win-arm64) exe=".exe" ;;
        linux-x64|linux-arm64|osx-x64|osx-arm64) exe="" ;;
        *) echo "unsupported rid '$rid'" >&2; exit 2 ;;
    esac
    name="RoboExp-$version-$rid"
    out="$dist/$name"
    rm -rf "$out"

    echo "==> $rid: RoboExp.App"
    dotnet publish "$repo/src/RoboExp.App" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=false -p:Version="$version" \
        -p:ApplicationIcon="$repo/packaging/icons/RoboExp.ico" -o "$out"
    echo "==> $rid: RoboExp.Cli"
    dotnet publish "$repo/src/RoboExp.Cli" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=false -p:Version="$version" -o "$out"
    mv "$out/RoboExp.Cli$exe" "$out/roboexp$exe"
    cp "$repo/packaging/icons/RoboExp-256.png" "$out/RoboExp.png"
    cp "$repo/THIRD-PARTY-NOTICES" "$out/THIRD-PARTY-NOTICES"

    echo "==> bundling robot assets and GCC for $rid"
    dotnet run --project "$repo/packaging/BundleAssets" -c Release -- "$repo" "$out" "$rid"

    echo "==> archiving $name"
    case "$rid" in
        win-*)
            rm -f "$dist/$name.zip"
            if command -v zip >/dev/null; then
                (cd "$dist" && zip -qr "$name.zip" "$name")
            else
                (cd "$dist" && python3 -m zipfile -c "$name.zip" "$name")
            fi
            echo "$dist/$name.zip" ;;
        *)
            # COPYFILE_DISABLE: macOS bsdtar would otherwise add ._* AppleDouble entries for extended attributes.
            COPYFILE_DISABLE=1 tar -C "$dist" -czf "$dist/$name.tar.gz" "$name"
            echo "$dist/$name.tar.gz" ;;
    esac
done

#!/usr/bin/env bash
# Builds the macOS app bundle (and DMG) for RoboExp CE.
#
# usage: packaging/macos/build-app.sh [--arch arm64|x64|both] [--no-dmg]
#
#   --arch    target CPU (default: the host CPU). A self-contained .NET app is a folder of per-architecture
#             runtime files, so `lipo` cannot merge two publishes into one universal bundle. `both` therefore
#             builds two separate bundles, dist/RoboExp-arm64.app and dist/RoboExp-x64.app (one DMG each).
#             A single architecture produces dist/RoboExp.app.
#   --no-dmg  skip the DMG.
#
# Output: dist/RoboExp[-<arch>].app (ad-hoc signed), dist/RoboExp-<version>-<arch>.dmg.
# The version comes from packaging/VERSION and is passed to `dotnet publish -p:Version=...` and into Info.plist.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(tr -d '[:space:]' < "$repo/packaging/VERSION")"
dist="$repo/dist"
executable="RoboExp.App"

case "$(uname -m)" in
    arm64) archs="arm64" ;;
    *) archs="x64" ;;
esac
make_dmg=1
while [ $# -gt 0 ]; do
    case "$1" in
        --arch)
            [ $# -ge 2 ] || { echo "--arch needs a value" >&2; exit 2; }
            case "$2" in
                arm64|x64) archs="$2" ;;
                both) archs="arm64 x64" ;;
                *) echo "unknown arch '$2' (arm64|x64|both)" >&2; exit 2 ;;
            esac
            shift 2 ;;
        --no-dmg) make_dmg=0; shift ;;
        -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
        *) echo "unknown option '$1'" >&2; exit 2 ;;
    esac
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$dist"

# Highest LC_BUILD_VERSION minos of the bundled Mach-O files: the oldest macOS the runtime can load on.
min_macos() {
    local dir="$1" best="0" f v info
    for f in "$dir"/*; do
        [ -f "$f" ] || continue
        if ! info="$(vtool -show-build "$f" 2>/dev/null)"; then continue; fi
        v="$(printf '%s\n' "$info" | awk '$1 == "minos" { print $2; exit }')"
        [ -n "$v" ] || continue
        best="$(printf '%s\n%s\n' "$best" "$v" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)"
    done
    [ "$best" != "0" ] || { echo "no Mach-O files with a minos in $dir" >&2; exit 1; }
    echo "$best"
}

for arch in $archs; do
    rid="osx-$arch"
    if [ "$archs" = "$arch" ]; then app="$dist/RoboExp.app"; else app="$dist/RoboExp-$arch.app"; fi
    publish="$work/publish-$arch"

    echo "==> dotnet publish ($rid, version $version)"
    dotnet publish "$repo/src/RoboExp.App" -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None -p:Version="$version" -o "$publish"
    [ -x "$publish/$executable" ] || { echo "publish output lacks $executable" >&2; exit 1; }

    echo "==> assembling $app"
    rm -rf "$app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$publish/." "$app/Contents/MacOS/"
    cp "$repo/packaging/icons/RoboExp.icns" "$app/Contents/Resources/RoboExp.icns"
    cp "$repo/THIRD-PARTY-NOTICES" "$app/Contents/Resources/THIRD-PARTY-NOTICES"
    cp "$repo/LICENSE" "$app/Contents/Resources/LICENSE"
    dotnet run --project "$repo/packaging/BundleAssets" -c Release -- "$repo" "$app/Contents/Resources"
    # Remove native NuGet PDBs too, before signing or creating the DMG.
    find "$app" -type f -iname '*.pdb' -delete
    sed -e "s|@VERSION@|$version|g" \
        -e "s|@EXECUTABLE@|$executable|g" \
        -e "s|@MIN_MACOS@|$(min_macos "$publish")|g" \
        -e "s|@YEAR@|$(date +%Y)|g" \
        "$repo/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
    plutil -lint "$app/Contents/Info.plist"
    printf 'APPL????' > "$app/Contents/PkgInfo"

    echo "==> ad-hoc signing"
    codesign --force --deep --sign - "$app"
    codesign --verify --deep --strict "$app"
    codesign -dv "$app" 2>&1 | grep -E '^(Identifier|Format|Signature)='

    if [ "$make_dmg" = 1 ]; then
        dmg="$dist/RoboExp-$version-$arch.dmg"
        echo "==> $dmg"
        staging="$work/dmg-$arch"
        mkdir -p "$staging"
        ditto "$app" "$staging/RoboExp.app"
        ln -s /Applications "$staging/Applications"
        hdiutil create -volname "RoboExp CE" -srcfolder "$staging" -ov -format UDZO "$dmg"
    fi
done
echo "done: $dist"

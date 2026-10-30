#!/usr/bin/env bash
# Windows x64/ARM64: self-contained app + CLI, portable zip and per-user NSIS installer.
# Requires .NET 10 SDK, Python 3 and NSIS (makensis). Cross-builds on macOS/Linux.
set -euo pipefail
if [[ $# -gt 1 ]]; then
    echo "Usage: $0 [win-arm64|win-x64]" >&2
    exit 2
fi
rid="${1-win-arm64}"
case "$rid" in
    win-arm64) arch=arm64; EXPECTED_PE=0xaa64; arch_name=ARM64 ;;
    win-x64) arch=x64; EXPECTED_PE=0x8664; arch_name=x64 ;;
    *) echo "Usage: $0 [win-arm64|win-x64]" >&2; exit 2 ;;
esac
repo="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(tr -d '[:space:]' < "$repo/packaging/VERSION")"
command -v makensis >/dev/null || { echo "NSIS is required (macOS: brew install nsis)." >&2; exit 1; }
command -v python3 >/dev/null || { echo "Python 3 is required." >&2; exit 1; }

bash "$repo/packaging/publish.sh" "$rid"
payload="$repo/dist/RoboExp-$version-$rid"
output="$repo/dist/RoboExp-$version-$rid-setup.exe"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Reject mixed-architecture native dependencies before putting them in the installer.
# Managed AnyCPU assemblies may have an x86 PE header: inspect the CLR directory before rejecting those.
python3 - "$payload" "$work/remove-files.nsh" "$EXPECTED_PE" "$arch_name" <<'PY'
from pathlib import Path
import struct
import sys
root=Path(sys.argv[1]); uninstall=Path(sys.argv[2])
expected_pe=int(sys.argv[3],0); arch_name=sys.argv[4]
required=['RoboExp.App.exe','roboexp.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll',
          'libSkiaSharp.dll','libHarfBuzzSharp.dll','av_libglesv2.dll']
for name in required:
    if not (root/name).is_file(): raise SystemExit(f'Missing {arch_name} dependency: '+name)
native=[]
for f in sorted(root.iterdir()):
    if not f.is_file() or f.suffix.lower() not in ('.exe','.dll'): continue
    b=f.read_bytes()
    if b[:2]!=b'MZ': continue
    pe=struct.unpack_from('<I',b,0x3c)[0]
    if b[pe:pe+4]!=b'PE\0\0': raise SystemExit('Invalid PE: '+str(f))
    machine=struct.unpack_from('<H',b,pe+4)[0]
    opt=pe+24; magic=struct.unpack_from('<H',b,opt)[0]
    directories=opt+(112 if magic==0x20b else 96)
    clr=struct.unpack_from('<II',b,directories+14*8)
    if clr==(0,0):
        if machine!=expected_pe: raise SystemExit(f'Non-{arch_name} native file: {f.name} (0x{machine:04x})')
        native.append(f.name)
print(f'Verified {arch_name} native files:',len(native))
print(f'Application and CLI: {arch_name}; runtime and graphics native dependencies: {arch_name}')
def nsis(s): return s.replace('$','$$').replace('"','$\\"')
files=[p for p in root.rglob('*') if p.is_file()]
dirs=sorted((p for p in root.rglob('*') if p.is_dir()), key=lambda p:len(p.parts), reverse=True)
lines=['; Generated from the exact published payload.']
for f in files: lines.append('Delete "$INSTDIR\\'+nsis(str(f.relative_to(root)).replace('/','\\'))+'"')
for d in dirs: lines.append('RMDir "$INSTDIR\\'+nsis(str(d.relative_to(root)).replace('/','\\'))+'"')
uninstall.write_text('\n'.join(lines)+'\n')
PY

makensis -V3 -DVERSION="$version" -DARCH="$arch" -DPAYLOAD="$payload" -DOUTPUT="$output" \
    -DREMOVE_FILES="$work/remove-files.nsh" -DAPP_ICON="$repo/packaging/icons/RoboExp.ico" \
    "$repo/packaging/windows/installer.nsi"
python3 - "$output" <<'PY'
from pathlib import Path
import hashlib,sys
p=Path(sys.argv[1])
h=hashlib.sha256(p.read_bytes()).hexdigest()
p.with_name(p.name+'.sha256').write_text(h+'  '+p.name+'\n')
print(f'Installer: {p} ({p.stat().st_size:,} bytes)')
print('SHA-256:',h)
PY

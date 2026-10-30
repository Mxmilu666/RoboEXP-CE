#!/usr/bin/env python3
"""Validate stable release tags and hash the exact assets uploaded to a Release."""
import argparse
import hashlib
from pathlib import Path
import re


def validate(tag: str, version_file: Path) -> str:
    if not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag):
        raise ValueError("Release tag must be vMAJOR.MINOR.PATCH, e.g. v0.1.0")
    version = version_file.read_text().strip()
    if tag != "v" + version:
        raise ValueError(f"Tag {tag} does not match packaging/VERSION ({version})")
    return version


def checksums(directory: Path, version: str) -> None:
    # Refuse missing matrix outputs or unexpected files instead of publishing a partial release.
    expected = {
        f"RoboExp-{version}-{rid}.{ext}"
        for rid, ext in [
            ("win-x64", "zip"), ("win-arm64", "zip"),
            ("linux-x64", "tar.gz"), ("linux-arm64", "tar.gz"),
            ("arm64", "dmg"), ("x64", "dmg"),
        ]
    } | {f"RoboExp-{version}-win-{arch}-setup.exe" for arch in ("x64", "arm64")}
    actual = {p.name for p in directory.iterdir() if p.is_file() and p.name != "SHA256SUMS.txt"}
    if actual != expected:
        raise ValueError(f"Release assets mismatch: missing={sorted(expected - actual)}, unexpected={sorted(actual - expected)}")
    lines = []
    for name in sorted(expected):
        path = directory / name
        if path.stat().st_size == 0:
            raise ValueError(f"Empty release asset: {name}")
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        lines.append(f"{digest.hexdigest()}  {name}\n")
    (directory / "SHA256SUMS.txt").write_text("".join(lines))
    print(f"Verified {len(expected)} release assets; wrote SHA256SUMS.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    tag_parser = sub.add_parser("validate")
    tag_parser.add_argument("tag")
    tag_parser.add_argument("--version-file", type=Path, default=Path("packaging/VERSION"))
    hash_parser = sub.add_parser("checksums")
    hash_parser.add_argument("directory", type=Path)
    hash_parser.add_argument("version")
    args = parser.parse_args()
    if args.command == "validate":
        print(validate(args.tag, args.version_file))
    else:
        checksums(args.directory, args.version)

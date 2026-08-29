#!/usr/bin/env python3
"""Verify an installer archive against its committed allowlisted sources."""

from __future__ import annotations

import argparse
import re
import tarfile
import tempfile
from pathlib import Path, PurePosixPath

from build_installer import (
    MAXIMUM_ARCHIVE_BYTES,
    ROOT,
    VERSION_TOKEN,
    build_archive,
    collect_assets,
    read_package_version,
    sha256_file,
)


EXPECTED_CHECKSUMS = ROOT / "Installer~" / "SHA256SUMS"
SHA256_LINE = re.compile(
    r"^(?P<sha256>[0-9a-f]{64})  "
    r"GitSubmoduleManagerInstaller-(?P<version>[^/\r\n]+)\.unitypackage\n$"
)


def read_expected_sha256(version: str) -> str:
    if not EXPECTED_CHECKSUMS.is_file() or EXPECTED_CHECKSUMS.is_symlink():
        raise ValueError("committed installer checksum is missing or linked")
    checksum_text = EXPECTED_CHECKSUMS.read_text(encoding="ascii", errors="strict")
    match = SHA256_LINE.fullmatch(checksum_text)
    if match is None or match.group("version") != version:
        raise ValueError("committed installer checksum does not match the package version")
    return match.group("sha256")


def verify_archive(archive: Path, version: str) -> None:
    archive = archive.resolve()
    if not archive.is_file() or archive.is_symlink():
        raise ValueError("installer archive is not a regular file")
    if archive.stat().st_size <= 0 or archive.stat().st_size > MAXIMUM_ARCHIVE_BYTES:
        raise ValueError("installer archive is empty or exceeds 2 MiB")
    actual_bytes = archive.read_bytes()
    with tempfile.TemporaryDirectory() as temporary:
        canonical = Path(temporary) / "canonical.unitypackage"
        build_archive(version, canonical)
        if actual_bytes != canonical.read_bytes():
            raise ValueError(
                "installer archive is not byte-for-byte canonical for the committed sources"
            )
    actual_sha256 = sha256_file(archive)
    if actual_sha256 != read_expected_sha256(version):
        raise ValueError(
            "installer archive does not match the committed Unity-tested SHA-256"
        )

    raw_header = actual_bytes[:10]
    if len(raw_header) != 10 or raw_header[:2] != b"\x1f\x8b" or raw_header[4:8] != b"\0\0\0\0":
        raise ValueError("installer must use deterministic gzip metadata")

    assets = collect_assets(version)
    expected: dict[str, tuple[bytes | None, str]] = {}
    for asset in assets:
        prefix = asset.guid
        expected[prefix] = (None, "directory")
        expected[f"{prefix}/pathname"] = (str(asset.path).encode("utf-8"), "file")
        expected[f"{prefix}/asset.meta"] = (asset.meta, "file")
        if asset.asset is not None:
            expected[f"{prefix}/asset"] = (asset.asset, "file")

    actual: dict[str, tuple[bytes | None, str]] = {}
    total_size = 0
    with tarfile.open(archive, "r:gz") as bundle:
        for member in bundle.getmembers():
            raw = member.name.rstrip("/") if member.isdir() else member.name
            path = PurePosixPath(raw)
            if (
                not raw
                or path.is_absolute()
                or ".." in path.parts
                or "\\" in raw
                or any(ord(character) < 32 for character in raw)
                or path.as_posix() != raw
                or raw in actual
            ):
                raise ValueError(f"unsafe or duplicate installer member: {member.name!r}")
            if member.isdir():
                actual[raw] = (None, "directory")
                continue
            if not member.isfile():
                raise ValueError(f"unsupported installer member type: {raw}")
            if member.size < 0 or member.size > MAXIMUM_ARCHIVE_BYTES or \
                    total_size + member.size > MAXIMUM_ARCHIVE_BYTES:
                raise ValueError("installer uncompressed content exceeds 2 MiB")
            source = bundle.extractfile(member)
            if source is None:
                raise ValueError(f"could not read installer member: {raw}")
            payload = source.read()
            if len(payload) != member.size:
                raise ValueError(f"truncated installer member: {raw}")
            total_size += len(payload)
            actual[raw] = (payload, "file")

    if actual.keys() != expected.keys():
        missing = sorted(expected.keys() - actual.keys())
        extra = sorted(actual.keys() - expected.keys())
        raise ValueError(f"installer member allowlist mismatch: missing={missing} extra={extra}")
    for name, expected_value in expected.items():
        if actual[name] != expected_value:
            raise ValueError(f"installer member differs from committed source: {name}")
    token = VERSION_TOKEN.encode("utf-8")
    if any(payload is not None and token in payload for payload, _ in actual.values()):
        raise ValueError("installer archive still contains an unresolved version token")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--version", help="exact version; defaults to package.json")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    version = args.version or read_package_version()
    verify_archive(args.archive, version)
    print(f"Verified installer {args.archive} ({sha256_file(args.archive)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

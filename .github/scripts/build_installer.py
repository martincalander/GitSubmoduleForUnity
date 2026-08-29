#!/usr/bin/env python3
"""Build a deterministic Git Submodule Manager bootstrap .unitypackage."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import re
import tarfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[2]
SOURCE_ROOT = ROOT / "Installer~" / "Assets" / "GitSubmoduleManagerInstaller"
ASSET_ROOT = PurePosixPath("Assets/GitSubmoduleManagerInstaller")
VERSION_TOKEN = "__PACKAGE_VERSION__"
MAXIMUM_ARCHIVE_BYTES = 2 * 1024 * 1024
GUID_PATTERN = re.compile(r"(?m)^guid:\s*([0-9a-f]{32})$")
SEMVER_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)

EXPECTED_ASSETS = (
    ASSET_ROOT,
    ASSET_ROOT / "README.txt",
    ASSET_ROOT / "InstallerMarker.txt",
    ASSET_ROOT / "Editor",
    ASSET_ROOT / "Editor/MartinCalander.GitSubmoduleManager.Installer.asmdef",
    ASSET_ROOT / "Editor/StrictJson.cs",
    ASSET_ROOT / "Editor/GitSubmoduleManagerInstallerManifest.cs",
    ASSET_ROOT / "Editor/GitSubmoduleManagerInstaller.cs",
)


@dataclass(frozen=True)
class InstallerAsset:
    path: PurePosixPath
    guid: str
    asset: bytes | None
    meta: bytes


def read_package_version() -> str:
    package = json.loads((ROOT / "package.json").read_text(encoding="utf-8"))
    version = package.get("version")
    if not isinstance(version, str) or not SEMVER_PATTERN.fullmatch(version):
        raise ValueError("package.json does not contain a valid SemVer version")
    return version


def _source_for_asset(path: PurePosixPath) -> Path:
    relative = path.relative_to(ASSET_ROOT)
    return SOURCE_ROOT.joinpath(*relative.parts)


def _render(payload: bytes, version: str) -> tuple[bytes, int]:
    token = VERSION_TOKEN.encode("utf-8")
    count = payload.count(token)
    return payload.replace(token, version.encode("utf-8")), count


def collect_assets(version: str) -> list[InstallerAsset]:
    if not SEMVER_PATTERN.fullmatch(version):
        raise ValueError(f"invalid installer version: {version!r}")

    actual_paths: set[PurePosixPath] = {ASSET_ROOT}
    for source in SOURCE_ROOT.rglob("*"):
        if source.is_symlink():
            raise ValueError(f"installer source cannot contain a link: {source}")
        if source.name.endswith(".meta"):
            continue
        relative = PurePosixPath(source.relative_to(SOURCE_ROOT).as_posix())
        actual_paths.add(ASSET_ROOT / relative)

    expected_paths = set(EXPECTED_ASSETS)
    if actual_paths != expected_paths:
        missing = sorted(str(path) for path in expected_paths - actual_paths)
        extra = sorted(str(path) for path in actual_paths - expected_paths)
        raise ValueError(f"installer asset allowlist mismatch: missing={missing} extra={extra}")

    assets: list[InstallerAsset] = []
    token_count = 0
    seen_guids: set[str] = set()
    for asset_path in sorted(EXPECTED_ASSETS, key=str):
        source = _source_for_asset(asset_path)
        meta_path = Path(f"{source}.meta")
        if not meta_path.is_file() or meta_path.is_symlink():
            raise ValueError(f"missing regular metadata file: {meta_path}")
        meta = meta_path.read_bytes()
        try:
            meta_text = meta.decode("utf-8", errors="strict")
        except UnicodeDecodeError as error:
            raise ValueError(f"metadata is not strict UTF-8: {meta_path}") from error
        matches = GUID_PATTERN.findall(meta_text)
        if len(matches) != 1:
            raise ValueError(f"metadata must contain exactly one Unity GUID: {meta_path}")
        guid = matches[0]
        if guid in seen_guids:
            raise ValueError(f"duplicate installer GUID: {guid}")
        seen_guids.add(guid)

        if source.is_dir():
            asset_bytes = None
        elif source.is_file() and not source.is_symlink():
            asset_bytes, replacements = _render(source.read_bytes(), version)
            token_count += replacements
        else:
            raise ValueError(f"installer asset is not a regular file or directory: {source}")

        assets.append(InstallerAsset(asset_path, guid, asset_bytes, meta))

    if token_count != 2:
        raise ValueError(
            f"expected exactly two installer version tokens, found {token_count}"
        )
    return assets


def _tar_info(name: str, size: int, mode: int, is_directory: bool = False) -> tarfile.TarInfo:
    normalized = name.rstrip("/") + ("/" if is_directory else "")
    info = tarfile.TarInfo(normalized)
    info.size = 0 if is_directory else size
    info.mode = mode
    info.mtime = 0
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.type = tarfile.DIRTYPE if is_directory else tarfile.REGTYPE
    return info


def _add_bytes(bundle: tarfile.TarFile, name: str, payload: bytes) -> None:
    bundle.addfile(_tar_info(name, len(payload), 0o644), io.BytesIO(payload))


def build_archive(version: str, destination: Path) -> Path:
    assets = collect_assets(version)
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".tmp")
    if destination.exists() or temporary.exists():
        raise FileExistsError(
            f"refusing to overwrite installer output or staging path: {destination}"
        )

    try:
        with temporary.open("xb") as raw:
            with gzip.GzipFile(
                filename="",
                mode="wb",
                fileobj=raw,
                compresslevel=9,
                mtime=0,
            ) as zipped:
                with tarfile.open(
                    fileobj=zipped,
                    mode="w",
                    format=tarfile.USTAR_FORMAT,
                ) as bundle:
                    for asset in assets:
                        prefix = asset.guid
                        bundle.addfile(
                            _tar_info(prefix, 0, 0o755, is_directory=True)
                        )
                        _add_bytes(
                            bundle,
                            f"{prefix}/pathname",
                            str(asset.path).encode("utf-8"),
                        )
                        _add_bytes(bundle, f"{prefix}/asset.meta", asset.meta)
                        if asset.asset is not None:
                            _add_bytes(bundle, f"{prefix}/asset", asset.asset)

        if temporary.stat().st_size > MAXIMUM_ARCHIVE_BYTES:
            raise ValueError("installer archive exceeds the 2 MiB safety limit")
        os.link(temporary, destination)
        temporary.unlink()
    finally:
        if temporary.exists():
            temporary.unlink()
    return destination


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", help="exact package version; defaults to package.json")
    parser.add_argument("--output", type=Path, help="output .unitypackage path")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    version = args.version or read_package_version()
    output = args.output or (
        ROOT / "dist" / f"GitSubmoduleManagerInstaller-{version}.unitypackage"
    )
    archive = build_archive(version, output)
    print(f"{sha256_file(archive)}  {archive}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

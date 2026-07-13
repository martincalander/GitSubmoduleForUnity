#!/usr/bin/env python3
"""License-free sanity checks for the Git Package Manager UPM repository."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[2]
ERRORS: list[str] = []


def fail(message: str) -> None:
    ERRORS.append(message)


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"Invalid JSON: {path.relative_to(ROOT)}: {exc}")
        return {}


def check_package_json() -> None:
    package = read_json(ROOT / "package.json")
    required = {
        "name",
        "version",
        "displayName",
        "description",
        "unity",
        "license",
        "author",
    }
    missing = sorted(required - package.keys())
    if missing:
        fail(f"package.json missing fields: {', '.join(missing)}")

    if package.get("name") != "com.martincalander.gitpackagemanager":
        fail("package.json name must be com.martincalander.gitpackagemanager")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", str(package.get("version", ""))):
        fail("package.json version is not valid SemVer")
    if package.get("license") != "MIT":
        fail("package.json license must be MIT")
    author = package.get("author") or {}
    if author.get("name") != "Martin Calander":
        fail("package.json author.name must attribute Martin Calander")

    dependencies = package.get("dependencies") or {}
    if dependencies != {"com.unity.modules.imgui": "1.0.0"}:
        fail("package.json contains unexpected dependencies")


def check_required_files() -> None:
    required = [
        "README.md",
        "LICENSE.md",
        "NOTICE.md",
        "AUTHORS.md",
        "CHANGELOG.md",
        ".github/CONTRIBUTING.md",
        ".github/CODE_OF_CONDUCT.md",
        ".github/SECURITY.md",
        ".github/SUPPORT.md",
        ".github/GOVERNANCE.md",
        ".github/MAINTAINERS.md",
        ".github/RELEASING.md",
        "Third Party Notices.md",
        "Documentation~/index.md",
        "Documentation~/installation.md",
        "Documentation~/user-guide.md",
        "Documentation~/troubleshooting.md",
        "Documentation~/architecture.md",
        "Documentation~/roadmap.md",
        "GPMIcon.png",
        "Editor/MartinCalander.GitPackageManager.Editor.asmdef",
        "Editor/GitEditorWindowIcon.png",
        "Editor/GitEditorWindowIconLight.png",
        "Tests/Editor/MartinCalander.GitPackageManager.Editor.Tests.asmdef",
        ".github/workflows/ci.yml",
        ".github/workflows/release.yml",
        ".github/ISSUE_TEMPLATE/support_request.yml",
        ".github/REPOSITORY_SETUP.md",
        ".npmignore",
    ]
    for relative in required:
        path = ROOT / relative
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"Required file missing or empty: {relative}")

    license_text = (ROOT / "LICENSE.md").read_text(encoding="utf-8")
    if "MIT License" not in license_text or "Martin Calander" not in license_text:
        fail("LICENSE.md must contain the MIT license and Martin Calander attribution")
    if "Proprietary License" in license_text or "All rights reserved" in license_text:
        fail("LICENSE.md still contains proprietary licensing language")

    github_meta_files = sorted((ROOT / ".github").rglob("*.meta"))
    for meta in github_meta_files:
        fail(f"GitHub-only file must not have Unity metadata: {meta.relative_to(ROOT)}")


def ignored_by_unity(path: Path) -> bool:
    relative = path.relative_to(ROOT)
    return any(part.startswith(".") or part.endswith("~") for part in relative.parts)


def check_unity_meta_files() -> None:
    guids: dict[str, Path] = {}
    for path in ROOT.rglob("*"):
        if path == ROOT / ".git" or ".git" in path.parts:
            continue
        if ignored_by_unity(path) or path.name.endswith(".meta"):
            continue

        meta = Path(f"{path}.meta")
        if not meta.is_file():
            fail(f"Unity asset is missing .meta file: {path.relative_to(ROOT)}")

    for meta in ROOT.rglob("*.meta"):
        if ignored_by_unity(meta):
            continue
        match = re.search(r"^guid:\s*([0-9a-f]{32})$", meta.read_text(encoding="utf-8"), re.MULTILINE)
        if not match:
            fail(f"Invalid or missing GUID: {meta.relative_to(ROOT)}")
            continue
        guid = match.group(1)
        if guid in guids:
            fail(
                "Duplicate Unity GUID "
                f"{guid}: {guids[guid].relative_to(ROOT)} and {meta.relative_to(ROOT)}"
            )
        guids[guid] = meta


def check_editor_only_layout() -> None:
    for assembly_path in ROOT.rglob("*.asmdef"):
        assembly = read_json(assembly_path)
        if "Editor" not in (assembly.get("includePlatforms") or []):
            fail(f"Assembly is not Editor-only: {assembly_path.relative_to(ROOT)}")

    for source in ROOT.rglob("*.cs"):
        relative = source.relative_to(ROOT)
        if relative.parts[0] not in {"Editor", "Tests"}:
            fail(f"C# source exists outside Editor/ or Tests/: {relative}")


MARKDOWN_LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
HTML_SOURCE = re.compile(r"\b(?:src|href)=[\"']([^\"']+)[\"']")


def check_markdown_links() -> None:
    for document in ROOT.rglob("*.md"):
        if ".git" in document.parts:
            continue
        text = document.read_text(encoding="utf-8")
        targets = MARKDOWN_LINK.findall(text) + HTML_SOURCE.findall(text)
        for raw_target in targets:
            target = raw_target.strip().split()[0].strip("<>")
            if not target or target.startswith(("#", "http://", "https://", "mailto:")):
                continue
            target = unquote(target.split("#", 1)[0].split("?", 1)[0])
            resolved = (document.parent / target).resolve()
            if not resolved.is_relative_to(ROOT.resolve()):
                fail(f"Markdown link escapes repository: {document.relative_to(ROOT)} -> {raw_target}")
            elif not resolved.exists():
                fail(f"Broken local link: {document.relative_to(ROOT)} -> {raw_target}")


def main() -> int:
    check_package_json()
    check_required_files()
    check_unity_meta_files()
    check_editor_only_layout()
    check_markdown_links()

    if ERRORS:
        for error in ERRORS:
            print(f"ERROR: {error}", file=sys.stderr)
        print(f"Repository validation failed with {len(ERRORS)} error(s).", file=sys.stderr)
        return 1

    print("Repository validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

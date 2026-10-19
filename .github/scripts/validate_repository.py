#!/usr/bin/env python3
"""Validate the Git Submodule Manager UPM repository and release metadata."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[2]
ERRORS: list[str] = []

# SemVer 2.0.0 requires plain ASCII digits, forbids leading zeroes in core
# numbers and numeric pre-release identifiers, and permits leading zeroes in
# build identifiers. Keep release tag validation on this same implementation.
SEMVER_PATTERN = re.compile(
    r"^(?P<major>0|[1-9][0-9]*)\."
    r"(?P<minor>0|[1-9][0-9]*)\."
    r"(?P<patch>0|[1-9][0-9]*)"
    r"(?:-(?P<prerelease>"
    r"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*"
    r"))?"
    r"(?:\+(?P<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)


def parse_semver(version: str) -> re.Match[str] | None:
    return SEMVER_PATTERN.fullmatch(version)


def semver_self_check_errors() -> list[str]:
    valid = (
        "0.0.0",
        "1.2.3",
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-0.3.7",
        "1.0.0-x.7.z.92",
        "1.0.0-x-y-z.--",
        "1.0.0+build.01",
        "1.0.0-beta+exp.sha.5114f85",
    )
    invalid = (
        "1",
        "1.2",
        "1.2.3.4",
        "01.2.3",
        "1.02.3",
        "1.2.03",
        "1.0.0-",
        "1.0.0-01",
        "1.0.0-alpha..1",
        "1.0.0+",
        "1.0.0+build..1",
        "1.0.0-alpha_beta",
        "v1.0.0",
        "1.0.0\n",
        "１.0.0",
    )

    errors = [f"accepted invalid version {value!r}" for value in invalid if parse_semver(value)]
    errors.extend(f"rejected valid version {value!r}" for value in valid if not parse_semver(value))
    return errors


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
        "unityRelease",
        "license",
        "author",
    }
    missing = sorted(required - package.keys())
    if missing:
        fail(f"package.json missing fields: {', '.join(missing)}")

    if package.get("name") != "com.martincalander.gitsubmodulemanager":
        fail("package.json name must be com.martincalander.gitsubmodulemanager")
    if package.get("displayName") != "Git Submodule Manager":
        fail("package.json displayName must be Git Submodule Manager")
    version = str(package.get("version", ""))
    if not parse_semver(version):
        fail("package.json version is not valid SemVer 2.0.0")
    else:
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        release_heading = re.compile(
            rf"(?m)^## \[{re.escape(version)}\] - [0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}$"
        )
        if len(release_heading.findall(changelog)) != 1:
            fail("CHANGELOG.md must contain one dated section for package.json version")
        if f"[{version}]:" not in changelog:
            fail("CHANGELOG.md must define a link for package.json version")
    if package.get("unity") != "6000.3":
        fail("package.json unity must declare the exact validated 6000.3.22f1 minimum")
    if package.get("unityRelease") != "22f1":
        fail("package.json unityRelease must declare the 6000.3.22f1 minimum")
    if package.get("license") != "MIT":
        fail("package.json license must be MIT")
    author = package.get("author") or {}
    if author.get("name") != "Martin Calander":
        fail("package.json author.name must attribute Martin Calander")

    dependencies = package.get("dependencies") or {}
    expected_dependencies = {
        "com.unity.modules.uielements": "1.0.0",
    }
    if dependencies != expected_dependencies:
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
        "Documentation~/Images/GitSubmoduleManagerCover.png",
        "Documentation~/Images/GitSubmoduleManagerIcon.png",
        "Documentation~/Images/GitSubmoduleManagerIcon.svg",
        "Editor/MartinCalander.GitSubmoduleManager.Editor.asmdef",
        "Editor/GitEditorWindowIcon.png",
        "Editor/GitEditorWindowIconLight.png",
        "ThirdParty.meta",
        "ThirdParty/GitSubmoduleManager.Harmony.dll",
        "ThirdParty/GitSubmoduleManager.Harmony.dll.meta",
        "ThirdParty/CoreCLR.meta",
        "ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll",
        "ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll.meta",
        "Tests/Editor/MartinCalander.GitSubmoduleManager.Editor.Tests.asmdef",
        ".github/workflows/ci.yml",
        ".github/workflows/release.yml",
        ".github/scripts/coreclr_harmony_artifacts.json",
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

    npmignore_lines = {
        line.strip()
        for line in (ROOT / ".npmignore").read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    }
    for attribution_file in ("AUTHORS.md", "LICENSE.md", "NOTICE.md", "Third Party Notices.md"):
        if attribution_file in npmignore_lines or f"/{attribution_file}" in npmignore_lines:
            fail(f".npmignore must not exclude required attribution file: {attribution_file}")


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
        if ignored_by_unity(source):
            continue
        relative = source.relative_to(ROOT)
        if relative.parts[0] not in {"Editor", "Tests"}:
            fail(f"C# source exists outside Editor/ or Tests/: {relative}")


def check_assembly_identities() -> None:
    editor_name = "MartinCalander.GitSubmoduleManager.Editor"
    tests_name = f"{editor_name}.Tests"
    editor = read_json(ROOT / "Editor" / f"{editor_name}.asmdef")
    tests = read_json(ROOT / "Tests" / "Editor" / f"{tests_name}.asmdef")

    if editor.get("name") != editor_name or editor.get("rootNamespace") != editor_name:
        fail("Editor assembly name and rootNamespace must use the GitSubmoduleManager identity")
    if tests.get("name") != tests_name or tests.get("rootNamespace") != tests_name:
        fail("Test assembly name and rootNamespace must use the GitSubmoduleManager identity")
    if editor_name not in (tests.get("references") or []):
        fail("Test assembly must reference the renamed editor assembly")


def check_harmony_plugin_contract() -> None:
    variants = (
        (
            "ThirdParty/GitSubmoduleManager.Harmony.dll",
            "Harmony 2.4.1",
            "353daafec180bb8e7bbe4da78f2a7cdc78067392e3a4e79dc8e7af295f2371e6",
            "!ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll",
            "HarmonyX 2.16.1",
            "70e7647876b6c3d874ea99b56627a95d1dce2e1dd791981a002578a2febc9bbd",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.RuntimeDetour.dll",
            "MonoMod.RuntimeDetour 25.3.4",
            "c186cc37e3cc3368540d0c2857b20c4f59cd1ddfe5c58c460f69a76a3e68c42d",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Core.dll",
            "MonoMod.Core 1.3.4",
            "6b06ad52612efd0534660f9ff4b68dcee685f5d604ff6ebe281f9041329c4376",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Iced.dll",
            "MonoMod.Iced 1.3.4",
            "42d9fc0cc12b37a61c832fca5768f62bd4d429890d34ba5c1acc30e150a99ffe",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Utils.dll",
            "MonoMod.Utils 25.0.12",
            "19f8df7d0d8ab311ad7f3a134beb82a87d4f1d26f9708b0bdd653f787dbf21a0",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Backports.dll",
            "MonoMod.Backports 1.1.2",
            "b3fc7aac52ffe6579ae1009a285a3d892b167151b0e18b5c1066ccb88e8b44a4",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.ILHelpers.dll",
            "MonoMod.ILHelpers 1.1.0",
            "a13590452f2bdf72718f43b410f43e110f273fce57fabd58ac5043a59b9be049",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.dll",
            "Mono.Cecil 0.11.6",
            "831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Mdb.dll",
            "Mono.Cecil.Mdb 0.11.6",
            "28cb367972bdc1cd43e4006306af2fd96d37f4ed4b239ee90e1dc7237a93af7f",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Pdb.dll",
            "Mono.Cecil.Pdb 0.11.6",
            "a332332633fbcb20e8d50e49b4db7bd1557721417122cf0c5f4c42f2332391d0",
            "ENABLE_CORECLR",
        ),
        (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Rocks.dll",
            "Mono.Cecil.Rocks 0.11.6",
            "bf992f3dce364ebcc3200fa7832ef07e20b4e2dbc3a8a6213ce44e3d239db984",
            "ENABLE_CORECLR",
        ),
    )
    for relative, version, expected_sha256, constraint in variants:
        check_harmony_variant(relative, version, expected_sha256, constraint)

    provenance = read_json(ROOT / ".github/scripts/coreclr_harmony_artifacts.json")
    artifacts = provenance.get("artifacts", [])
    if not isinstance(artifacts, list) or not all(isinstance(item, dict) for item in artifacts):
        fail("CoreCLR artifact provenance must contain an artifact list")
        artifacts = []
    expected = {path: digest for path, _, digest, define in variants if define == "ENABLE_CORECLR"}
    actual = {artifact.get("plugin"): artifact.get("sha256") for artifact in artifacts}
    if len(artifacts) != len(expected) or actual != expected:
        fail("CoreCLR artifact provenance must identify every audited DLL exactly once")
    for artifact in artifacts:
        package = artifact.get("package", "")
        version = artifact.get("version", "")
        expected_url = (
            f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/"
            f"{package}.{version}.nupkg"
        )
        if artifact.get("url") != expected_url or not re.fullmatch(
            r"[0-9a-f]{64}", artifact.get("archive_sha256", "")
        ):
            fail("CoreCLR artifact provenance must identify the exact NuGet archive and hash")

    editor = read_json(
        ROOT / "Editor" / "MartinCalander.GitSubmoduleManager.Editor.asmdef"
    )
    if editor.get("overrideReferences") is not True:
        fail("Editor assembly must explicitly override precompiled references")
    if "GitSubmoduleManager.Harmony.dll" not in (editor.get("precompiledReferences") or []):
        fail("Editor assembly must explicitly reference the bundled Harmony plugin")


def check_harmony_variant(
    relative: str, version: str, expected_sha256: str, constraint: str
) -> None:
    dll_path = ROOT / relative
    meta_path = Path(f"{dll_path}.meta")
    if not dll_path.is_file() or not meta_path.is_file():
        fail(f"Required Harmony plugin or metadata missing: {relative}")
        return

    try:
        dll_bytes = dll_path.read_bytes()
    except OSError as exc:
        fail(f"Could not read Harmony binary {relative}: {exc}")
        return

    if not dll_bytes.startswith(b"MZ"):
        fail(f"Bundled Harmony plugin {relative} is not a valid managed PE image")
    if hashlib.sha256(dll_bytes).hexdigest() != expected_sha256:
        fail(f"Bundled {version} binary {relative} does not match the audited artifact")

    try:
        meta_text = meta_path.read_text(encoding="utf-8")
    except OSError as exc:
        fail(f"Could not read Harmony importer metadata {relative}: {exc}")
        return

    constraints = re.search(r"(?m)^  defineConstraints:\n((?:  - [^\n]+\n)+)", meta_text)
    expected_constraints = {
        f"  - {constraint}\n",
        f"  - '{constraint}'\n",
        f'  - "{constraint}"\n',
    }
    if constraints is None or constraints.group(1) not in expected_constraints:
        fail(f"Harmony importer {relative} must have only the {constraint} constraint")

    required_importer_patterns = {
        "explicit-reference": r"(?m)^  isExplicitlyReferenced: 1$",
        "non-overridable": r"(?m)^  isOverridable: 0$",
        "Editor/AnyOS/AnyCPU": (
            r"(?ms)^    Editor:\n"
            r"      enabled: 1\n"
            r"      settings:\n"
            r"        CPU: AnyCPU\n"
            r"        DefaultValueInitialized: true\n"
            r"        OS: AnyOS$"
        ),
    }
    for label, pattern in required_importer_patterns.items():
        if not re.search(pattern, meta_text):
            fail(f"Harmony importer {relative} must preserve {label} settings")

    enabled_platforms = re.findall(r"(?m)^    ([^\n:]+):\n      enabled: 1$", meta_text)
    if enabled_platforms != ["Editor"]:
        fail(f"Harmony importer {relative} must enable only the Editor platform")

    for platform in (
        "Any",
        "Linux64",
        "OSXUniversal",
        "Win",
        "Win64",
        "WindowsStoreApps",
    ):
        if (
            constraint == "ENABLE_CORECLR"
            and platform != "Any"
            and not re.search(rf"(?m)^    {re.escape(platform)}:$", meta_text)
        ):
            # Unity omits disabled overrides when Any Platform is disabled.
            continue
        if not re.search(
            rf"(?m)^    {re.escape(platform)}:\n      enabled: 0$",
            meta_text,
        ):
            fail(f"Harmony importer {relative} must remain disabled for {platform} players")

    notices_path = ROOT / "Third Party Notices.md"
    if notices_path.is_file():
        notices = notices_path.read_text(encoding="utf-8")
        if (
            version not in notices
            or relative not in notices
            or expected_sha256 not in notices
        ):
            fail(f"Third Party Notices.md must attribute and identify {version} at {relative}")


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


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check-release-tag",
        metavar="TAG",
        help=(
            "validate one v-prefixed SemVer 2.0.0 release tag and print whether "
            "it is a pre-release"
        ),
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    self_check_errors = semver_self_check_errors()
    if self_check_errors:
        for error in self_check_errors:
            print(f"ERROR: Internal SemVer self-check {error}.", file=sys.stderr)
        return 1

    if args.check_release_tag is not None:
        tag = args.check_release_tag
        match = parse_semver(tag[1:]) if tag.startswith("v") else None
        if not match:
            print(f"ERROR: Invalid SemVer 2.0.0 release tag: {tag!r}", file=sys.stderr)
            return 1
        print("true" if match.group("prerelease") is not None else "false")
        return 0

    check_package_json()
    check_required_files()
    check_unity_meta_files()
    check_editor_only_layout()
    check_assembly_identities()
    check_harmony_plugin_contract()
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

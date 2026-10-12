from __future__ import annotations

import hashlib
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

import validate_repository as validator  # noqa: E402


class HarmonyArtifactTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.relative = "ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll"
        self.dll = self.root / self.relative
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b"MZfixture")
        self.digest = hashlib.sha256(self.dll.read_bytes()).hexdigest()
        self.meta = Path(f"{self.dll}.meta")
        self.original_meta = Path(f"{validator.ROOT / self.relative}.meta").read_text()
        self.meta.write_text(self.original_meta)
        (self.root / "Third Party Notices.md").write_text(
            f"HarmonyX 2.16.1\n{self.relative}\n{self.digest}\n"
        )
        self.errors: list[str] = []
        root_patch = patch.object(validator, "ROOT", self.root)
        errors_patch = patch.object(validator, "ERRORS", self.errors)
        root_patch.start()
        errors_patch.start()
        self.addCleanup(root_patch.stop)
        self.addCleanup(errors_patch.stop)

    def validate(self, constraint: str = "ENABLE_CORECLR") -> None:
        validator.check_harmony_variant(
            self.relative, "HarmonyX 2.16.1", self.digest, constraint
        )

    def test_editor_only_variant_accepts_omitted_disabled_platforms(self) -> None:
        self.validate()
        self.assertEqual(self.errors, [])

    def test_rejects_changed_binary(self) -> None:
        self.dll.write_bytes(b"MZchanged")
        self.validate()
        self.assertTrue(any("audited artifact" in error for error in self.errors))

    def test_rejects_missing_binary(self) -> None:
        self.dll.unlink()
        self.validate()
        self.assertTrue(any("missing" in error for error in self.errors))

    def test_rejects_wrong_or_extra_runtime_constraints(self) -> None:
        for replacement in (
            "  defineConstraints: []\n",
            "  defineConstraints:\n  - '!ENABLE_CORECLR'\n",
            "  defineConstraints:\n  - ENABLE_CORECLR\n  - UNITY_EDITOR\n",
        ):
            with self.subTest(replacement=replacement):
                self.errors.clear()
                self.meta.write_text(self.original_meta.replace(
                    "  defineConstraints:\n  - ENABLE_CORECLR\n", replacement
                ))
                self.validate()
                self.assertTrue(any("constraint" in error for error in self.errors))

    def test_rejects_enabled_player_platform(self) -> None:
        self.meta.write_text(self.original_meta.replace(
            "  platformData:\n", "  platformData:\n    Android:\n      enabled: 1\n"
        ))
        self.validate()
        self.assertTrue(any("only the Editor" in error for error in self.errors))

    def test_rejects_automatic_references(self) -> None:
        self.meta.write_text(self.original_meta.replace(
            "isExplicitlyReferenced: 1", "isExplicitlyReferenced: 0"
        ))
        self.validate()
        self.assertTrue(any("explicit-reference" in error for error in self.errors))


class HarmonyCecilDependencyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        source_root = validator.ROOT
        manifest_path = ".github/scripts/coreclr_harmony_artifacts.json"
        artifacts = json.loads((source_root / manifest_path).read_text())["artifacts"]
        coreclr_meta = (source_root / (
            "ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll.meta"
        )).read_text()
        for artifact in artifacts:
            relative = artifact["plugin"]
            destination = self.root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source_root / relative, destination)
            Path(f"{destination}.meta").write_text(coreclr_meta)
        for relative in (
            manifest_path,
            "Third Party Notices.md",
            "ThirdParty/GitSubmoduleManager.Harmony.dll",
            "ThirdParty/GitSubmoduleManager.Harmony.dll.meta",
            "Editor/MartinCalander.GitSubmoduleManager.Editor.asmdef",
        ):
            destination = self.root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source_root / relative, destination)
        self.errors: list[str] = []
        root_patch = patch.object(validator, "ROOT", self.root)
        errors_patch = patch.object(validator, "ERRORS", self.errors)
        root_patch.start()
        errors_patch.start()
        self.addCleanup(root_patch.stop)
        self.addCleanup(errors_patch.stop)

    def test_complete_audited_dependency_set_is_valid(self) -> None:
        validator.check_harmony_plugin_contract()
        self.assertEqual(self.errors, [])

    def test_rejects_each_missing_cecil_companion(self) -> None:
        for companion in ("Mdb", "Pdb", "Rocks"):
            with self.subTest(companion=companion):
                self.errors.clear()
                relative = (
                    "ThirdParty/CoreCLR/GitSubmoduleManager."
                    f"Mono.Cecil.{companion}.dll"
                )
                dll = self.root / relative
                original = dll.read_bytes()
                dll.unlink()
                validator.check_harmony_plugin_contract()
                self.assertEqual(self.errors, [
                    f"Required Harmony plugin or metadata missing: {relative}"
                ])
                dll.write_bytes(original)


if __name__ == "__main__":
    unittest.main()

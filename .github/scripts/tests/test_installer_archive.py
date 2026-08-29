from __future__ import annotations

import gzip
import io
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

from build_installer import build_archive, read_package_version  # noqa: E402
from verify_installer import verify_archive  # noqa: E402


class InstallerArchiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.version = read_package_version()

    def test_build_is_deterministic_and_verifies(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            first = Path(temporary) / "first.unitypackage"
            second = Path(temporary) / "second.unitypackage"
            build_archive(self.version, first)
            build_archive(self.version, second)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            verify_archive(first, self.version)

    def test_builder_refuses_to_overwrite_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "existing.unitypackage"
            archive.write_bytes(b"preserve me")
            with self.assertRaises(FileExistsError):
                build_archive(self.version, archive)
            self.assertEqual(archive.read_bytes(), b"preserve me")

    def test_verifier_rejects_an_extra_member(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "bad.unitypackage"
            with archive.open("wb") as raw:
                with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as zipped:
                    with tarfile.open(fileobj=zipped, mode="w") as bundle:
                        payload = b"unexpected"
                        info = tarfile.TarInfo("extra/asset")
                        info.size = len(payload)
                        bundle.addfile(info, io.BytesIO(payload))
            with self.assertRaisesRegex(ValueError, "byte-for-byte canonical"):
                verify_archive(archive, self.version)

    def test_verifier_rejects_trailing_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "trailing.unitypackage"
            build_archive(self.version, archive)
            archive.write_bytes(archive.read_bytes() + b"unexpected trailing bytes")
            with self.assertRaisesRegex(ValueError, "byte-for-byte canonical"):
                verify_archive(archive, self.version)

    def test_verifier_rejects_changed_gzip_header(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "header.unitypackage"
            build_archive(self.version, archive)
            payload = bytearray(archive.read_bytes())
            payload[9] ^= 0x01
            archive.write_bytes(payload)
            with self.assertRaisesRegex(ValueError, "byte-for-byte canonical"):
                verify_archive(archive, self.version)


if __name__ == "__main__":
    unittest.main()

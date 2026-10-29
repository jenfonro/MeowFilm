"""Repository output path contract; no build or network."""
import importlib.util
from pathlib import Path
import stat
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("repository_paths", ROOT / "scripts/repository-paths.py")
paths = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paths)


class RepositoryPathsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()

    def test_outputs_are_fixed_repository_directories_and_resolution_does_not_create_them(self):
        for name, expected in (("build", self.root / "build" / "ios"), ("tmp", self.root / "tmp")):
            with self.subTest(name=name):
                output = paths.output_path(name, self.root)
                self.assertEqual(output, expected)
                self.assertFalse(output.exists())

    def test_ios_build_resolves_inside_an_existing_shared_build_directory(self):
        (self.root / "build" / "windows").mkdir(parents=True)
        self.assertEqual(paths.output_path("build", self.root), self.root / "build" / "ios")

    def test_unknown_output_is_rejected(self):
        for name in ("", "cache", "../tmp", "tmp/test-results"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                paths.output_path(name, self.root)

    def test_file_output_is_rejected(self):
        (self.root / "tmp").write_text("fixture", encoding="utf-8")
        with self.assertRaises(ValueError):
            paths.output_path("tmp", self.root)

    def test_link_or_windows_reparse_output_is_rejected(self):
        original = Path.lstat
        (self.root / "build").mkdir()
        link = self.root / "build" / "ios"
        for mode, flags in ((stat.S_IFLNK, 0), (stat.S_IFDIR, 0x400)):
            def lstat(path):
                return (SimpleNamespace(st_mode=mode, st_file_attributes=flags)
                        if path == link else original(path))
            with self.subTest(flags=flags), patch.object(Path, "lstat", lstat), self.assertRaises(ValueError):
                paths.output_path("build", self.root)


if __name__ == "__main__":
    unittest.main()

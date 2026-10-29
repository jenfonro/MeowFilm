"""Source membership and app composition contracts; no SDK or device use."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ios_project_layout", ROOT / "scripts/generate-ios-project.py")
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class IOSSourceLayoutTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()

    def write(self, path):
        path = self.root / "apps/ios" / path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("import Foundation\n", encoding="utf-8")
        return path

    def test_recursive_collection_uses_only_owned_source_directories(self):
        self.write("Application/Main.swift")
        self.write("Application/Lifetime/Owner.swift")
        self.write("UITests/Tests.swift")
        self.write("Resources/Reference.swift")
        self.assertEqual(generator.source_paths(self.root, ("Application",)),
                         ["Application/Lifetime/Owner.swift", "Application/Main.swift"])
        self.assertEqual(generator.source_paths(self.root, ("UITests",)), ["UITests/Tests.swift"])

    def test_empty_and_missing_responsibility_directories_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "Missing source directory"):
            generator.source_paths(self.root, ("Application",))
        (self.root / "apps/ios/Application").mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, "Empty source directory"):
            generator.source_paths(self.root, ("Application",))

    def test_linked_sources_are_rejected_without_following_them(self):
        linked = self.write("Application/Main.swift")
        original = Path.is_symlink
        # Deterministic on hosts lacking symlink privileges. The production
        # collector also compares resolved paths to reject directory junctions.
        with patch.object(Path, "is_symlink", lambda path: path == linked or original(path)):
            with self.assertRaisesRegex(ValueError, "Linked or escaped source"):
                generator.source_paths(self.root, ("Application",))

    def test_unowned_app_sources_are_rejected_before_project_writes(self):
        for directory in generator.APPLICATION_DIRECTORIES:
            self.write(directory + "/Fixture.swift")
        self.write("UITests/Fixture.swift")
        self.write("Resources/Unexpected.swift")
        with patch.object(generator, "ROOT", self.root):
            with self.assertRaisesRegex(ValueError, "responsibility"):
                generator.generate()
        self.assertFalse((self.root / "apps/ios/Meowfilm.xcodeproj").exists())


if __name__ == "__main__":
    unittest.main()

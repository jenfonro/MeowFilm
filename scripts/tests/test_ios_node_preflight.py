"""Validate the prepared JS payload contract without Xcode or a Node process."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("node_preflight", ROOT / "scripts/ios-node-preflight.py")
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


class NodePreflightTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.payload = Path(self.temporary.name) / "runtime"
        (self.payload / "app").mkdir(parents=True)
        data = b"export const fixture = true;\n"
        (self.payload / "app/bootstrap.mjs").write_bytes(data)
        self.manifest = {
            "schemaVersion": 1, "status": "complete", "assemblyProfile": "ios-embedded",
            "platform": "ios", "transport": "ios-socketpair-v1",
            "dependencies": [{"name": "@jitl/quickjs-asmjs-mjs-release-sync", "version": "0.32.0"}],
            "files": [{"path": "app/bootstrap.mjs", "bytes": len(data),
                       "sha256": hashlib.sha256(data).hexdigest()}],
        }
        self.save()

    def save(self):
        (self.payload / "runtime-manifest.json").write_text(json.dumps(self.manifest))

    def test_accepts_matching_ios_payload_inventory(self):
        self.assertEqual(preflight.validate_runtime(self.payload), self.manifest)

    def test_rejects_wrong_profile_or_incomplete_packaging(self):
        for field, value in [("assemblyProfile", "windows-native"), ("status", "failed"), ("status", "platform-pending"),
                             ("platform", "windows"), ("transport", "pipe")]:
            with self.subTest(field=field):
                original = self.manifest[field]
                self.manifest[field] = value
                self.save()
                with self.assertRaises(ValueError):
                    preflight.validate_runtime(self.payload)
                self.manifest[field] = original

    def test_rejects_missing_or_unreviewed_backend(self):
        for dependencies in [[], [{"name": "@jitl/quickjs-asmjs-mjs-release-sync", "version": "0.33.0"}],
                             self.manifest["dependencies"] + [
                                 {"name": "@jitl/quickjs-wasmfile-release-sync", "version": "0.32.0"}]]:
            with self.subTest(dependencies=dependencies):
                self.manifest["dependencies"] = dependencies
                self.save()
                with self.assertRaises(ValueError):
                    preflight.validate_runtime(self.payload)

    def test_rejects_modified_or_missing_file(self):
        path = self.payload / "app/bootstrap.mjs"
        path.write_text("altered")
        with self.assertRaisesRegex(ValueError, "inventory mismatch"):
            preflight.validate_runtime(self.payload)
        path.unlink()
        with self.assertRaisesRegex(ValueError, "Missing"):
            preflight.validate_runtime(self.payload)

    def test_rejects_unlisted_or_linked_payload_files(self):
        path = self.payload / "experimental.mjs"
        path.write_text("not in the inventory")
        with self.assertRaisesRegex(ValueError, "Unlisted"):
            preflight.validate_runtime(self.payload)
        path.unlink()
        path.symlink_to(self.payload / "app/bootstrap.mjs")
        with self.assertRaisesRegex(ValueError, "Linked"):
            preflight.validate_runtime(self.payload)

    def test_rejects_empty_unsafe_or_duplicate_inventory(self):
        original = self.manifest["files"]
        for files in [[], [original[0], original[0]],
                      [{**original[0], "path": "../outside.mjs"}],
                      [{**original[0], "path": "/outside.mjs"}]]:
            with self.subTest(files=files):
                self.manifest["files"] = files
                self.save()
                with self.assertRaises(ValueError):
                    preflight.validate_runtime(self.payload)


if __name__ == "__main__":
    unittest.main()

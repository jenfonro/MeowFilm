"""Build-input preparation: local fixtures only, no network or Node download."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prepare_ios_runtime", ROOT / "scripts/prepare-ios-runtime.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class PrepareIOSRuntimeTests(unittest.TestCase):
    def test_current_release_license_matches_the_pinned_policy(self):
        value = prepare.policy()
        self.assertEqual(value["version"], "18.20.4")
        self.assertEqual(value["license"]["path"], "LICENSES/nodejs-mobile-LICENSE.txt")

    def test_archive_is_verified_before_reusing_or_extracting_framework(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            archive = cache / "fixture.zip"
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr("NodeMobile.xcframework/Info.plist", b"fixture")
            pinned = {"archive": {"url": "https://download.invalid/fixture.zip", "sha256": prepare.sha256(archive)}}
            with patch.dict(prepare.node_mobile.__globals__, NODE_MOBILE=cache):
                self.assertEqual(prepare.node_mobile(pinned), cache / "ios/NodeMobile.xcframework")
                self.assertEqual(prepare.node_mobile(pinned), cache / "ios/NodeMobile.xcframework")
                archive.write_bytes(b"modified")
                with self.assertRaisesRegex(ValueError, "SHA-256"):
                    prepare.node_mobile(pinned)

    def test_failed_payload_packaging_preserves_previous_complete_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / ".cache/ios-runtime/Runtime"
            target.mkdir(parents=True)
            (target / "runtime-manifest.json").write_text(json.dumps({"status": "complete"}))
            (root / "node_modules/@meowfilm/script-host").mkdir(parents=True)
            def run(command, **kwargs):
                if "--version" in command:
                    class Result:
                        stdout = "v24.21.0\n"
                    return Result()
                raise prepare.subprocess.CalledProcessError(1, command)
            with patch.dict(prepare.payload.__globals__, ROOT=root, RUNTIME=target), patch.object(prepare.subprocess, "run", run):
                with self.assertRaises(prepare.subprocess.CalledProcessError):
                    prepare.payload("node")
                self.assertEqual(json.loads((target / "runtime-manifest.json").read_text()), {"status": "complete"})
                self.assertEqual([p.name for p in target.parent.iterdir()], ["Runtime"])


if __name__ == "__main__":
    unittest.main()

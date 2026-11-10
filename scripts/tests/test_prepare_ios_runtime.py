"""Build-input preparation: local fixtures only, no network or Node download."""
import importlib.util
import json
import plistlib
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

    def test_reused_extraction_cannot_hide_modified_missing_extra_or_linked_headers(self):
        for change in ("modified", "missing", "extra", "linked"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                cache = Path(temporary)
                archive = cache / "fixture.zip"
                with zipfile.ZipFile(archive, "w") as package:
                    package.writestr("NodeMobile.xcframework/Info.plist", b"fixture")
                    package.writestr("include/node/node_api.h", b"pinned-header")
                pinned = {"archive": {"url": "https://download.invalid/fixture.zip", "sha256": prepare.sha256(archive)}}
                with patch.dict(prepare.node_mobile.__globals__, NODE_MOBILE=cache):
                    prepare.node_mobile(pinned)
                    header = cache / "ios/include/node/node_api.h"
                    if change == "modified":
                        header.write_text("changed")
                    elif change == "missing":
                        header.unlink()
                    elif change == "extra":
                        header.with_name("extra.h").write_text("unlisted")
                    else:
                        header.unlink()
                        header.symlink_to(archive)
                    with self.assertRaises(ValueError):
                        prepare.node_mobile(pinned)

    def test_target_mapping_keeps_node_x64_identity_and_xcode_x86_64_paths(self):
        selected = prepare.target("iphonesimulator", "x86_64")
        self.assertEqual(selected, {"sdk": "iphonesimulator", "arch": "x64", "minimumOS": "17.0"})
        self.assertEqual(prepare.runtime_path(selected), ROOT / ".cache/ios-runtime/iphonesimulator-x86_64/Runtime")
        self.assertEqual(prepare.target("iphonesimulator", "arm64")["arch"], "arm64")
        self.assertEqual(prepare.target("iphoneos", "arm64")["sdk"], "iphoneos")
        for sdk, arch in (("macosx", "arm64"), ("iphoneos", "x86_64"), ("iphonesimulator", "ia32")):
            with self.assertRaises(ValueError):
                prepare.target(sdk, arch)

    def test_verified_official_archive_headers_and_slice_are_returned_as_build_inputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / ".cache/nodejs-mobile"
            cache.mkdir(parents=True)
            archive = cache / "fixture.zip"
            info = {"AvailableLibraries": [
                {"LibraryIdentifier": "ios-arm64", "SupportedPlatform": "ios", "SupportedArchitectures": ["arm64"]},
                {"LibraryIdentifier": "ios-arm64_x86_64-simulator", "SupportedPlatform": "ios",
                 "SupportedPlatformVariant": "simulator", "SupportedArchitectures": ["arm64", "x86_64"]},
            ]}
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr("NodeMobile.xcframework/Info.plist", plistlib.dumps(info))
                for item in info["AvailableLibraries"]:
                    package.writestr("NodeMobile.xcframework/" + item["LibraryIdentifier"] + "/NodeMobile.framework/NodeMobile", b"fixture")
                package.writestr("include/node/node_api.h", b"pinned-header")
            license_path = root / "LICENSES/nodejs-mobile-LICENSE.txt"
            license_path.parent.mkdir()
            license_path.write_text("fixture-license")
            pinned = {"version": "18.20.4", "license": {"path": "LICENSES/nodejs-mobile-LICENSE.txt"},
                      "archive": {"url": "https://download.invalid/fixture.zip", "sha256": prepare.sha256(archive)}}
            with patch.dict(prepare.node_mobile_inputs.__globals__, ROOT=root, NODE_MOBILE=cache):
                value = prepare.node_mobile_inputs(pinned, prepare.target("iphonesimulator", "x64"))
            self.assertEqual(value["identity"]["slice"], "ios-arm64_x86_64-simulator")
            self.assertEqual(value["headers"], [{"path": ".cache/nodejs-mobile/ios/include/node/node_api.h",
                                                "sha256": prepare.hashlib.sha256(b"pinned-header").hexdigest()}])
            self.assertTrue(any(item["path"].endswith("NodeMobile.framework/NodeMobile") for item in value["inputs"]))

    def test_failed_payload_packaging_preserves_previous_complete_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            selected = prepare.target("iphonesimulator", "x86_64")
            target = root / ".cache/ios-runtime/iphonesimulator-x86_64/Runtime"
            target.mkdir(parents=True)
            (target / "runtime-manifest.json").write_text(json.dumps({"status": "complete"}))
            (root / "node_modules/@meowfilm/script-host").mkdir(parents=True)
            def run(command, **kwargs):
                if "--version" in command:
                    class Result:
                        stdout = "v24.21.0\n"
                    return Result()
                raise prepare.subprocess.CalledProcessError(1, command)
            with patch.dict(prepare.payload.__globals__, ROOT=root), patch.object(prepare.subprocess, "run", run):
                with self.assertRaises(prepare.subprocess.CalledProcessError):
                    prepare.payload("node", selected)
                self.assertEqual(json.loads((target / "runtime-manifest.json").read_text()), {"status": "complete"})
                self.assertEqual([p.name for p in target.parent.iterdir()], ["Runtime"])

    def test_payload_requires_the_exact_five_module_app_before_replacing_previous_payload(self):
        expected_modules = ["music", "cat", "source", "tmdb", "netease"]
        for modules in [
            ["music", "cat", "source", "tmdb"],
            expected_modules, ["music", "cat", "source"], ["music", "cat", "source", "douban"],
            ["music", "cat", "source", "pan"], ["music", "cat", "source", "unknown"],
            expected_modules + ["douban"], expected_modules + ["pan"], expected_modules + ["tmdb"],
            ["tmdb", "music", "cat", "source"],
        ]:
            with self.subTest(modules=modules), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                selected = prepare.target("iphonesimulator", "x86_64")
                target = root / ".cache/ios-runtime/iphonesimulator-x86_64/Runtime"
                target.mkdir(parents=True)
                previous = {"status": "previous-complete"}
                (target / "runtime-manifest.json").write_text(json.dumps(previous))
                (root / "node_modules/@meowfilm/script-host").mkdir(parents=True)
                manifest = {"status": "complete", "platform": "ios", "assemblyProfile": "common-native",
                            "target": selected, "transport": "runtime.http.v1", "modules": modules,
                            "trustedNative": [{"platform": "ios"}]}
                commands = []
                def run(command, **kwargs):
                    commands.append(command)
                    if "--version" in command:
                        return prepare.subprocess.CompletedProcess(command, 0, stdout="v24.21.0\n")
                    staging = Path(command[command.index("--output") + 1])
                    staging.mkdir()
                    (staging / "runtime-manifest.json").write_text(json.dumps(manifest))
                    return prepare.subprocess.CompletedProcess(command, 0)
                with patch.dict(prepare.payload.__globals__, ROOT=root), patch.object(prepare.subprocess, "run", run):
                    if modules == expected_modules:
                        self.assertEqual(prepare.payload("node", selected), target)
                        self.assertEqual(json.loads((target / "runtime-manifest.json").read_text()), manifest)
                    else:
                        with self.assertRaisesRegex(ValueError, "payload is not complete"):
                            prepare.payload("node", selected)
                        self.assertEqual(json.loads((target / "runtime-manifest.json").read_text()), previous)
                self.assertEqual(commands[1][1:],
                                 [str(root / "scripts/package-ios-runtime.mjs"), "--product", "aggregate",
                                  "--sdk", "iphonesimulator", "--arch", "x64",
                                  "--output", str(target.parent / "Runtime.partial")])
                self.assertEqual([path.name for path in target.parent.iterdir()], ["Runtime"])


if __name__ == "__main__":
    unittest.main()

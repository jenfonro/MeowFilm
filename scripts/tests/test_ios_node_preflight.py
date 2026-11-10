"""Validate the prepared JS payload contract without Xcode or a Node process."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

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
            "schemaVersion": 1, "status": "complete", "assemblyProfile": "common-native",
            "platform": "ios", "transport": "runtime.http.v1",
            "entry": "app/ios/entries/aggregate.mjs", "modules": ["music", "cat", "source", "pan", "tmdb", "douban", "film"],
            "target": {"sdk": "iphonesimulator", "arch": "x64", "minimumOS": "17.0"},
            "trustedNative": [{"platform": "ios", "arch": "x64", "abi": 1, "napi": 8,
                               "path": "Frameworks/MeowScriptHost.framework/MeowScriptHost",
                               "bindingIdentity": "a" * 64,
                               "target": {"sdk": "iphonesimulator", "arch": "x64", "minimumOS": "17.0"}}],
            "files": [{"path": "app/bootstrap.mjs", "bytes": len(data),
                       "sha256": hashlib.sha256(data).hexdigest()}],
        }
        self.save()

    def save(self):
        (self.payload / "runtime-manifest.json").write_text(json.dumps(self.manifest))

    def test_accepts_matching_ios_payload_inventory(self):
        self.assertEqual(preflight.validate_runtime(self.payload), self.manifest)

    def test_rejects_missing_duplicate_extra_unknown_or_reordered_modules(self):
        for modules in [
            ["music", "cat", "source"], ["music", "cat", "source", "douban"],
            ["music", "cat", "source", "pan"], ["music", "cat", "source", "unknown"],
            ["music", "cat", "source", "tmdb", "douban"], ["music", "cat", "source", "tmdb", "pan"],
            ["music", "cat", "source", "tmdb", "tmdb"], ["tmdb", "music", "cat", "source"],
        ]:
            with self.subTest(modules=modules):
                self.manifest["modules"] = modules
                self.save()
                with self.assertRaisesRegex(ValueError, "prepared common-native"):
                    preflight.validate_runtime(self.payload)

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

    def test_rejects_inexact_app_module_assemblies(self):
        for modules in [
            ["music", "cat", "source"],
            ["music", "cat", "source", "pan", "tmdb", "douban"],
            ["music", "cat", "source", "pan", "tmdb", "douban", "film", "unknown"],
            ["music", "cat", "source", "pan", "tmdb", "douban", "douban"],
            ["music", "cat", "source", "tmdb", "pan", "douban", "film"],
        ]:
            with self.subTest(modules=modules):
                self.manifest["modules"] = modules
                self.save()
                with self.assertRaises(ValueError):
                    preflight.validate_runtime(self.payload)

    def test_rejects_missing_unreviewed_or_mismatched_native_framework(self):
        original = self.manifest["trustedNative"][0]
        for entries in ([], [{**original, "abi": 2}], [{**original, "platform": "darwin"}],
                        [{**original, "path": "/arbitrary/framework"}],
                        [{**original, "arch": "x86_64"}]):
            with self.subTest(entries=entries):
                self.manifest["trustedNative"] = entries
                self.save()
                with self.assertRaises(ValueError):
                    preflight.validate_runtime(self.payload)
        self.manifest["trustedNative"] = [original]
        self.save()
        with self.assertRaisesRegex(ValueError, "target"):
            preflight.validate_runtime(self.payload, {"sdk": "iphoneos", "arch": "arm64", "minimumOS": "17.0"})

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


class NodePreflightBuildTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.work = self.root / "tmp/ios-node-preflight"
        self.inputs = self.root / "scripts/ios-node-preflight"
        self.inputs.mkdir(parents=True)
        (self.inputs / "probe.mjs").write_text("export {};")
        self.dependency = self.root / "NodeMobile.xcframework"
        self.prepare = {**preflight.PREPARE,
                        "policy": Mock(return_value={}),
                        "node_mobile": Mock(return_value=self.dependency),
                        "runtime_path": self.runtime_path}
        for sdk, arch, slice_name in (("iphonesimulator", "x86_64", "ios-arm64_x86_64-simulator"),
                                      ("iphoneos", "arm64", "ios-arm64")):
            selected = self.prepare["target"](sdk, arch)
            mobile = self.dependency / slice_name / "NodeMobile.framework"
            mobile.mkdir(parents=True)
            (mobile / "NodeMobile").write_bytes(b"official-fixture")
            native = self.root / f".cache/ios-native/{sdk}-{arch}/MeowScriptHost.framework"
            native.mkdir(parents=True)
            executable = native / "MeowScriptHost"
            executable.write_bytes(b"native-fixture")
            receipt = native.parent / "build/Release/script-host-native.json"
            receipt.parent.mkdir(parents=True)
            receipt.write_text(json.dumps({
                "target": selected, "bindingIdentity": "a" * 64,
                "artifact": {"path": executable.relative_to(self.root).as_posix(),
                             "sha256": hashlib.sha256(executable.read_bytes()).hexdigest()},
            }))
            payload = self.runtime_path(selected)
            (payload / "app").mkdir(parents=True)
            data = b"export {};"
            (payload / "app/bootstrap.mjs").write_bytes(data)
            (payload / "runtime-manifest.json").write_text(json.dumps({
                "schemaVersion": 1, "status": "complete", "assemblyProfile": "common-native",
                "platform": "ios", "transport": "runtime.http.v1",
                "entry": "app/ios/entries/aggregate.mjs", "modules": ["music", "cat", "source", "pan", "tmdb", "douban", "film"],
                "target": selected,
                "trustedNative": [{"platform": "ios", "arch": selected["arch"], "abi": 1, "napi": 8,
                                  "path": "Frameworks/MeowScriptHost.framework/MeowScriptHost",
                                  "bindingIdentity": "a" * 64, "target": selected}],
                "files": [{"path": "app/bootstrap.mjs", "bytes": len(data),
                           "sha256": hashlib.sha256(data).hexdigest()}],
            }))
        for name, value in (("ROOT", self.root), ("WORK", self.work), ("INPUTS", self.inputs),
                            ("PREPARE", self.prepare)):
            patcher = patch.object(preflight, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        for patcher in (patch.object(preflight.sys, "platform", "darwin"),
                        patch.object(preflight.platform, "machine", return_value="x86_64")):
            patcher.start()
            self.addCleanup(patcher.stop)

    def runtime_path(self, selected):
        arch = "x86_64" if selected["arch"] == "x64" else selected["arch"]
        return self.root / f".cache/ios-runtime/{selected['sdk']}-{arch}/Runtime"

    def prepare_commands(self):
        return [[preflight.sys.executable, "-I", "-B", str(self.root / "scripts/prepare-ios-runtime.py"),
                 "--sdk", sdk, "--arch", arch]
                for sdk, arch in (("iphonesimulator", "x86_64"), ("iphoneos", "arm64"))]

    def test_build_prepares_both_actual_targets_before_reading_or_linking_payloads(self):
        events = []
        def run(arguments, **options):
            command = list(map(str, arguments))
            events.append(("run", command))
            if command in self.prepare_commands():
                self.assertEqual(options, {"cwd": self.root})
            return subprocess.CompletedProcess(command, 0)
        validate = preflight.validate_runtime
        def checked(directory, selected=None):
            events.append(("validate", directory))
            return validate(directory, selected)
        with patch.object(preflight, "run", side_effect=run), \
                patch.object(preflight, "output", return_value="/fixture/SDK"), \
                patch.object(preflight, "validate_runtime", side_effect=checked):
            preflight.build()
        self.assertEqual(events[:2], [("run", command) for command in self.prepare_commands()])
        compiles = [command for kind, command in events if kind == "run" and "clang" in command]
        self.assertEqual([command[command.index("-target") + 1] for command in compiles],
                         ["x86_64-apple-ios17.0-simulator", "arm64-apple-ios17.0"])
        self.assertEqual(len([event for event in events if event[0] == "validate"]), 4)
        self.assertLess(events.index(("validate", self.runtime_path(self.prepare["target"]("iphonesimulator", "x64")))),
                        events.index(("run", compiles[0])))
        self.assertLess(events.index(("validate", self.runtime_path(self.prepare["target"]("iphoneos", "arm64")))),
                        events.index(("run", compiles[1])))
        for name in ("simulator", "device"):
            app = self.work / name / "NodePreflight.app"
            self.assertEqual((app / "Frameworks/MeowScriptHost.framework/MeowScriptHost").read_bytes(), b"native-fixture")
            self.assertEqual((app / "Runtime/probe.mjs").read_text(), "export {};")

    def test_either_prepare_failure_stops_before_using_old_complete_payloads(self):
        for failed_sdk in ("iphonesimulator", "iphoneos"):
            with self.subTest(sdk=failed_sdk):
                commands = []
                def run(arguments, **options):
                    command = list(map(str, arguments))
                    commands.append(command)
                    if command[command.index("--sdk") + 1] == failed_sdk:
                        raise subprocess.CalledProcessError(7, command)
                    return subprocess.CompletedProcess(command, 0)
                with patch.object(preflight, "run", side_effect=run), \
                        patch.object(preflight, "output") as output, \
                        patch.object(preflight, "validate_runtime") as validate:
                    with self.assertRaises(subprocess.CalledProcessError) as caught:
                        preflight.build()
                self.assertEqual(caught.exception.returncode, 7)
                count = 1 if failed_sdk == "iphonesimulator" else 2
                self.assertEqual(commands, self.prepare_commands()[:count])
                self.prepare["node_mobile"].assert_not_called()
                output.assert_not_called()
                validate.assert_not_called()
                self.assertFalse(self.work.exists())


if __name__ == "__main__":
    unittest.main()

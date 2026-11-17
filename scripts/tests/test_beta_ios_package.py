"""Only the new CI packaging contract; no Xcode, network or app execution."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("beta_package", ROOT / "scripts/package-beta-ios.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class BetaPackageTests(unittest.TestCase):
    def info(self):
        return {"CFBundleIdentifier": "dev.meowfilm.aggregate",
                "CFBundlePackageType": "APPL", "CFBundleSupportedPlatforms": ["iPhoneOS"],
                "DTPlatformName": "iphoneos"}

    def test_fixed_device_identity_is_accepted(self):
        package.validate_info(self.info())

    def test_changed_identity_is_rejected(self):
        info = self.info()
        info["CFBundleIdentifier"] = "dev.meowfilm.aggregate.beta.123"
        with self.assertRaises(ValueError):
            package.validate_info(info)

    def test_simulator_is_rejected(self):
        info = self.info()
        info["CFBundleSupportedPlatforms"] = ["iPhoneSimulator"]
        info["DTPlatformName"] = "iphonesimulator"
        with self.assertRaises(ValueError):
            package.validate_info(info)

    def app(self):
        (ROOT / "tmp").mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=ROOT / "tmp")
        self.addCleanup(temporary.cleanup)
        app = Path(temporary.name) / "Meowfilm.app"
        app.mkdir()
        return app

    def test_bundle_metadata_is_removed_after_macho_signatures(self):
        app = self.app()
        framework = app / "Frameworks" / "Example.framework"
        framework.mkdir(parents=True)
        executables = [app / "Meowfilm", framework / "Example", framework / "Extra.dylib"]
        for path in executables:
            path.write_bytes(b"\xcf\xfa\xed\xfe" + b"fixture")
        signatures = [bundle / "_CodeSignature" for bundle in (app, framework)]
        for path in signatures:
            path.mkdir()
            (path / "CodeResources").write_bytes(b"signature metadata")
        resource = framework / "resource.txt"
        resource.write_bytes(b"preserved resource")
        signed = {str(path) for path in executables[:2]}

        def codesign(command, **kwargs):
            path = command[-1]
            if command[1] == "--remove-signature":
                self.assertTrue(all(signature.is_dir() for signature in signatures))
                signed.remove(path)
                return subprocess.CompletedProcess(command, 0)
            self.assertEqual(command[1], "-d")
            return subprocess.CompletedProcess(command, 0 if path in signed else 1,
                                               stderr="" if path in signed else "code object is not signed at all")

        with mock.patch.object(package.subprocess, "run", side_effect=codesign) as run:
            package.strip_signatures(app)
        commands = [call.args[0] for call in run.call_args_list]
        self.assertCountEqual([command[-1] for command in commands if command[1] == "--remove-signature"],
                              [str(path) for path in executables[:2]])
        for path in executables:
            self.assertEqual(commands.count(["/usr/bin/codesign", "-d", str(path)]), 2)
            self.assertTrue(path.is_file())
        self.assertFalse(signed)
        self.assertFalse(list(app.rglob("_CodeSignature")))
        self.assertEqual(resource.read_bytes(), b"preserved resource")

    def test_provisioning_profile_is_rejected_before_cleanup(self):
        app = self.app()
        framework = app / "Frameworks" / "Example.framework"
        framework.mkdir(parents=True)
        profile = framework / "embedded.mobileprovision"
        profile.write_bytes(b"fixture")
        with mock.patch.object(package.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "must not embed provisioning profiles"):
                package.strip_signatures(app)
        run.assert_not_called()
        self.assertTrue(profile.is_file())

    def test_signed_or_unverifiable_macho_is_rejected(self):
        for returncode, stderr in ((0, ""), (1, "unable to read code object")):
            with self.subTest(returncode=returncode):
                app = self.app()
                (app / "Meowfilm").write_bytes(b"\xcf\xfa\xed\xfe")
                signature = app / "_CodeSignature"
                signature.mkdir()
                probe = subprocess.CompletedProcess([], returncode, stderr=stderr)
                with mock.patch.object(package.subprocess, "run", return_value=probe):
                    with self.assertRaisesRegex(ValueError, "Unable to verify an unsigned Mach-O"):
                        package.strip_signatures(app)
                self.assertTrue(signature.is_dir())

    def test_signature_removal_failure_is_not_ignored(self):
        app = self.app()
        (app / "Meowfilm").write_bytes(b"\xcf\xfa\xed\xfe")
        signature = app / "_CodeSignature"
        signature.mkdir()
        failure = subprocess.CalledProcessError(1, ["/usr/bin/codesign", "--remove-signature"])
        with mock.patch.object(package.subprocess, "run",
                               side_effect=[subprocess.CompletedProcess([], 0), failure]):
            with self.assertRaises(subprocess.CalledProcessError):
                package.strip_signatures(app)
        self.assertTrue(signature.is_dir())

    def test_bundle_cleanup_failure_is_not_ignored(self):
        app = self.app()
        signature = app / "_CodeSignature"
        signature.mkdir()
        with mock.patch.object(package.shutil, "rmtree", side_effect=OSError("cleanup failed")):
            with self.assertRaisesRegex(OSError, "cleanup failed"):
                package.strip_signatures(app)
        self.assertTrue(signature.is_dir())

    def test_unexpected_signature_file_or_symlink_is_rejected(self):
        for symlink in (False, True):
            with self.subTest(symlink=symlink):
                app = self.app()
                external = app.parent / "external-signature"
                external.mkdir()
                resource = external / "CodeResources"
                resource.write_bytes(b"must not be deleted")
                signature = app / "_CodeSignature"
                if symlink:
                    signature.symlink_to(external, target_is_directory=True)
                else:
                    signature.write_bytes(b"unexpected file")
                with self.assertRaisesRegex(ValueError, "Unexpected bundle signature"):
                    package.strip_signatures(app)
                self.assertTrue(signature.exists())
                self.assertEqual(resource.read_bytes(), b"must not be deleted")

    def test_extensions_and_nested_apps_are_rejected_before_stripping(self):
        for name in ("NewIdentity.appex", "Nested.app"):
            with self.subTest(name=name):
                app = self.app()
                (app / "PlugIns" / name).mkdir(parents=True)
                staging = app.parent / "staging"
                staging.mkdir()
                with mock.patch.dict(package.os.environ, {"GITHUB_SHA": "fixture-commit"}), \
                     mock.patch.object(package, "output", side_effect=["fixture-commit", ""]), \
                     mock.patch.object(package, "strip_signatures") as strip:
                    with self.assertRaisesRegex(ValueError, "New app identities/extensions"):
                        package.main(app, staging)
                strip.assert_not_called()

    def test_build_versions_are_monotonic_and_bounded(self):
        numbers = [1, 2, 99, 100, 9999, 10000, 99_989_999]
        versions = [tuple(map(int, package.build_version(n).split("."))) for n in numbers]
        self.assertEqual(versions, sorted(set(versions)))
        for major, minor, patch in versions:
            self.assertTrue(1 <= major <= 9999 and 0 <= minor <= 99 and 0 <= patch <= 99)
        for invalid in (0, -1, 99_990_000):
            with self.assertRaises(ValueError):
                package.build_version(invalid)

    def test_workflow_keeps_release_only_and_no_signing_secrets(self):
        text = (ROOT / ".github/workflows/beta-ios.yml").read_text(encoding="utf-8")
        for workflow in ("beta-ios.yml", "native-code-gate.yml"):
            trigger = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8").split("\npermissions:", 1)[0]
            self.assertIn("workflow_dispatch:", trigger)
            for automatic in ("push:", "pull_request", "schedule:", "workflow_run:"):
                self.assertNotIn(automatic, trigger)
        self.assertNotIn("upload-artifact", text)
        self.assertNotIn("secrets.", text)
        self.assertIn("bash build-ios.sh", text)
        self.assertIn("publish-beta-ios.py build/ios", text)
        build = (ROOT / "build-ios.sh").read_text(encoding="utf-8")
        self.assertIn("CODE_SIGNING_ALLOWED=NO", build)
        self.assertIn('mv "$staging/build" "$build"', build)
        self.assertIn('"make_latest": "false"',
                      (ROOT / "scripts/publish-beta-ios.py").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()

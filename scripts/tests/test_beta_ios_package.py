"""Only the new CI packaging contract; no Xcode, network or app execution."""
import importlib.util
from pathlib import Path
import unittest

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

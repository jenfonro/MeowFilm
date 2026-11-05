"""Deterministic product-policy checks; no Xcode, runtime or network execution."""
import pathlib
import plistlib
import json
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


class ProductConfigurationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.root = pathlib.Path(cls.temporary.name)
        (cls.root / "scripts").mkdir()
        (cls.root / "apps/ios").mkdir(parents=True)
        shutil.copyfile(ROOT / "scripts/generate-ios-project.py",
                        cls.root / "scripts/generate-ios-project.py")
        shutil.copyfile(ROOT / "apps/ios/Info.plist", cls.root / "apps/ios/Info.plist")
        for directory in ("Application", "Composition", "Navigation", "Presentation", "UITests", "RuntimeTests"):
            if (ROOT / "apps/ios" / directory).exists():
                shutil.copytree(ROOT / "apps/ios" / directory, cls.root / "apps/ios" / directory)
        subprocess.run([sys.executable, "-I", "-B", str(cls.root / "scripts/generate-ios-project.py")],
                       check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def project(self, product):
        return (self.root / "apps/ios" / (product + ".xcodeproj/project.pbxproj")).read_text()

    def test_single_meowfilm_product_links_its_native_capabilities(self):
        project = self.project("Meowfilm")
        for library in ("MeowUI", "MeowMusicFeature", "MeowMusicPlatform", "MeowMusicNativeService", "MeowFilmFeature"):
            self.assertIn(f"productName = {library};", project)
        self.assertEqual(project.count('path = "Navigation/MeowfilmCoordinator.swift";'), 1)
        self.assertEqual({p.name for p in (self.root / "apps/ios").glob("*.xcodeproj")}, {"Meowfilm.xcodeproj"})

    def test_kugou_http_exception_is_exact_in_the_app_info(self):
        base = plistlib.loads((self.root / "apps/ios/Info.plist").read_bytes())
        app = plistlib.loads((self.root / "apps/ios/Info-Meowfilm.plist").read_bytes())
        self.assertNotIn("NSAppTransportSecurity", base)
        self.assertNotIn("NSLocalNetworkUsageDescription", base)
        self.assertIs(app["UIDesignRequiresCompatibility"], True)
        self.assertEqual(app["NSAppTransportSecurity"], {
            "NSExceptionDomains": {"d.kugou.com": {
                "NSExceptionAllowsInsecureHTTPLoads": True,
                "NSIncludesSubdomains": False,
            }},
        })
        self.assertEqual(app["UIApplicationSceneManifest"], {
            "UIApplicationSupportsMultipleScenes": False,
            "UISceneConfigurations": {
                "UIWindowSceneSessionRoleApplication": [{
                    "UISceneConfigurationName": "Meowfilm",
                    "UISceneDelegateClassName": "$(PRODUCT_MODULE_NAME).MeowfilmSceneDelegate",
                }],
            },
        })
        self.assertEqual(self.project("Meowfilm").count("INFOPLIST_FILE = Info-Meowfilm.plist;"), 2)
        del app["NSAppTransportSecurity"]
        del app["UIApplicationSceneManifest"]
        self.assertEqual(app, base)

    def test_app_embeds_node_mobile_complete_payload_and_license_and_hosts_runtime_tests(self):
        project = self.project("Meowfilm")
        self.assertIn('path = "../../.cache/nodejs-mobile/ios/NodeMobile.xcframework";', project)
        self.assertIn('path = "../../.cache/ios-runtime/Runtime";', project)
        self.assertIn('path = "../../LICENSES/nodejs-mobile-LICENSE.txt";', project)
        self.assertIn("CodeSignOnCopy", project)
        self.assertIn("productName = MeowRuntime;", project)
        host = next(line for line in project.splitlines()
                    if "isa = PBXNativeTarget;" in line and "name = MeowfilmRuntimeTests;" in line)
        self.assertNotIn("packageProductDependencies", host, "Hosted tests use the App's linked products")
        self.assertIn("BUNDLE_LOADER", project)
        self.assertIn("TEST_HOST", project)
        scheme = (self.root / "apps/ios/Meowfilm.xcodeproj/xcshareddata/xcschemes/MeowfilmAcceptance.xcscheme").read_text()
        self.assertIn('BlueprintName="MeowfilmRuntimeTests"', scheme)

    def test_platform_component_tests_share_the_runner_but_not_the_product_assembly(self):
        project = self.project("Meowfilm")
        self.assertEqual(project.count('path = "UITests/MeowUIPlatformTests.swift";'), 1)
        app = next(line for line in project.splitlines()
                   if "isa = PBXNativeTarget;" in line and "name = Meowfilm;" in line)
        runner = next(line for line in project.splitlines()
                      if "isa = PBXNativeTarget;" in line and "name = MeowfilmAcceptanceUITests;" in line)
        def dependencies(target):
            values = re.search(r"packageProductDependencies = \(([^)]*)\)", target)[1]
            return re.findall(r"[A-F0-9]{24}", values)
        expected, actual = dependencies(app), dependencies(runner)
        self.assertTrue(expected)
        self.assertEqual(set(actual), set(expected))
        self.assertEqual(len(actual), len(set(actual)))
        self.assertEqual(project.count('productType = "com.apple.product-type.application";'), 1)
        self.assertEqual(
            project.count("PRODUCT_BUNDLE_IDENTIFIER = dev.meowfilm.aggregate.acceptance-tests;"), 2
        )

    def test_keychain_signing_entitlements_are_simulator_only_not_device_or_beta(self):
        filename = "Meowfilm-Simulator.entitlements"
        entitlements = plistlib.loads((self.root / "apps/ios" / filename).read_bytes())
        self.assertEqual(entitlements, {
            "application-identifier": "$(PRODUCT_BUNDLE_IDENTIFIER)",
            "get-task-allow": True,
        })
        project = self.project("Meowfilm")
        self.assertEqual(project.count(f'"CODE_SIGN_ENTITLEMENTS[sdk=iphonesimulator*]" = {filename};'), 2)
        self.assertNotIn("CODE_SIGN_ENTITLEMENTS =", project)
        self.assertNotIn("DEVELOPMENT_TEAM", project)
        self.assertNotIn("PROVISIONING_PROFILE", project)

    def test_generation_is_reproducible_without_changing_base_info(self):
        before = {path.relative_to(self.root).as_posix(): path.read_bytes()
                  for path in (self.root / "apps/ios").rglob("*") if path.is_file()}
        subprocess.run([sys.executable, "-I", "-B", str(self.root / "scripts/generate-ios-project.py")],
                       check=True, capture_output=True)
        after = {path.relative_to(self.root).as_posix(): path.read_bytes()
                 for path in (self.root / "apps/ios").rglob("*") if path.is_file()}
        self.assertEqual(before, after)
        self.assertEqual((ROOT / "apps/ios/Info.plist").read_bytes(),
                         (self.root / "apps/ios/Info.plist").read_bytes())

    def test_app_embeds_the_complete_opaque_iphone_icon(self):
        icon = ROOT / "apps/ios/Assets.xcassets/AppIcon.appiconset"
        content = json.loads((icon / "Contents.json").read_text())
        expected = {("iphone", f"{size}x{size}", f"{scale}x")
                    for size in (20, 29, 40, 60) for scale in (2, 3)}
        expected.add(("ios-marketing", "1024x1024", "1x"))
        self.assertEqual(len(content["images"]), len(expected))
        self.assertEqual({(image["idiom"], image["size"], image["scale"])
                          for image in content["images"]}, expected)
        for image in content["images"]:
            filename = image["filename"]
            self.assertEqual(pathlib.Path(filename).name, filename)
            png = (icon / filename).read_bytes()
            self.assertEqual(png[:8], b"\x89PNG\r\n\x1a\n")
            self.assertEqual(png[12:16], b"IHDR")
            size = int(image["size"].split("x")[0]) * int(image["scale"][:-1])
            self.assertEqual(struct.unpack(">II", png[16:24]), (size, size))
            self.assertEqual(png[25], 2)  # RGB, no transparency or alpha channel.
        project = self.project("Meowfilm")
        self.assertEqual(project.count("path = Assets.xcassets;"), 1)
        self.assertEqual(project.count("ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon;"), 2)
        self.assertNotIn("PBXResourcesBuildPhase; buildActionMask = 2147483647; files = ();", project)


if __name__ == "__main__":
    unittest.main()

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

    def test_app_allows_arbitrary_http_without_scoped_ats_overrides(self):
        base = plistlib.loads((self.root / "apps/ios/Info.plist").read_bytes())
        app = plistlib.loads((self.root / "apps/ios/Info-Meowfilm.plist").read_bytes())
        self.assertNotIn("NSAppTransportSecurity", base)
        self.assertNotIn("NSLocalNetworkUsageDescription", base)
        self.assertIs(app["UIDesignRequiresCompatibility"], True)
        self.assertEqual(app["NSAppTransportSecurity"], {"NSAllowsArbitraryLoads": True})
        committed = plistlib.loads((ROOT / "apps/ios/Info-Meowfilm.plist").read_bytes())
        self.assertEqual(committed["NSAppTransportSecurity"], app["NSAppTransportSecurity"])
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
        self.assertIn('path = "../../.cache/ios-runtime/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/Runtime";', project)
        self.assertIn('path = "../../LICENSES/nodejs-mobile-LICENSE.txt";', project)
        self.assertIn('path = "../../LICENSES/quickjs-native-MIT.txt";', project)
        self.assertIn('path = "../../LICENSES/Unicode-3.0.txt";', project)
        resources = next(line for line in project.splitlines() if "isa = PBXResourcesBuildPhase;" in line)
        for license_name in ("quickjs-native-MIT.txt", "Unicode-3.0.txt", "NOTICE.md", "GPL-3.0-only.txt"):
            reference = next(line.split()[0] for line in project.splitlines()
                             if f'path = "../../LICENSES/{license_name}";' in line)
            build = next(line.split()[0] for line in project.splitlines()
                         if f"isa = PBXBuildFile; fileRef = {reference};" in line)
            self.assertIn(build, resources)
        self.assertIn('path = "../../.cache/ios-native/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/MeowScriptHost.framework";', project)
        reference = next(line.split()[0] for line in project.splitlines()
                         if "name = MeowScriptHost.framework;" in line)
        builds = [line for line in project.splitlines() if f"isa = PBXBuildFile; fileRef = {reference};" in line]
        self.assertEqual(len(builds), 1, "The dynamic loader owns admission; the App only embeds the framework")
        self.assertIn("CodeSignOnCopy", builds[0])
        embed = next(line for line in project.splitlines() if "isa = PBXCopyFilesBuildPhase;" in line)
        self.assertIn(builds[0].split()[0], embed)
        self.assertIn("dstSubfolderSpec = 10;", embed)
        self.assertEqual(project.count('"MEOW_RUNTIME_SDK[sdk=iphonesimulator*]" = iphonesimulator;'), 2)
        self.assertEqual(project.count('"MEOW_RUNTIME_ARCH[sdk=iphoneos*]" = arm64;'), 2)
        self.assertIn('MEOW_RUNTIME_ARCH = "$(NATIVE_ARCH_ACTUAL)";', project)
        self.assertIn("CodeSignOnCopy", project)
        self.assertIn("productName = MeowRuntime;", project)
        host = next(line for line in project.splitlines()
                    if "isa = PBXNativeTarget;" in line and "name = MeowfilmRuntimeTests;" in line)
        self.assertNotIn("packageProductDependencies", host, "Hosted tests use the App's linked products")
        self.assertEqual(project.count('BUNDLE_LOADER = "$(TEST_HOST)";'), 2)
        self.assertEqual(project.count('TEST_HOST = "$(BUILT_PRODUCTS_DIR)/Meowfilm.app/Meowfilm";'), 2)
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

    def test_video_kernel_licenses_are_shipped_once_as_app_resources(self):
        project = self.project("Meowfilm")
        resources = next(line for line in project.splitlines() if "isa = PBXResourcesBuildPhase;" in line)
        for filename in ("NOTICE.md", "GPL-3.0-only.txt", "LGPL-2.1-only.txt", "LGPL-3.0-only.txt",
                         "FFmpeg-LICENSE.md", "FFmpeg-IJG-NOTICE.txt"):
            references = [line.split()[0] for line in project.splitlines()
                          if f'path = "../../LICENSES/{filename}";' in line]
            self.assertEqual(len(references), 1, filename)
            builds = [line.split()[0] for line in project.splitlines()
                      if f"isa = PBXBuildFile; fileRef = {references[0]};" in line]
            self.assertEqual(len(builds), 1, filename)
            self.assertIn(builds[0], resources)
            self.assertTrue((ROOT / "LICENSES" / filename).is_file())

    def test_synthetic_video_is_a_host_test_resource_not_an_app_resource(self):
        project = self.project("Meowfilm")
        reference = next(line.split()[0] for line in project.splitlines()
                         if 'path = "../../native/VideoKernel/Tests/VideoKernelTests/Fixtures/ordinary.mp4";' in line)
        build = next(line.split()[0] for line in project.splitlines()
                     if f"isa = PBXBuildFile; fileRef = {reference};" in line)
        phases = [line for line in project.splitlines() if "isa = PBXResourcesBuildPhase;" in line]
        used = [line for line in phases if build in line]
        self.assertEqual(len(used), 1)
        phase = used[0].split()[0]
        targets = [line for line in project.splitlines() if "isa = PBXNativeTarget;" in line and phase in line]
        self.assertEqual(len(targets), 1)
        self.assertIn("name = MeowfilmRuntimeTests;", targets[0])
        self.assertTrue((ROOT / "native/VideoKernel/Tests/VideoKernelTests/Fixtures/ordinary.mp4").is_file())

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

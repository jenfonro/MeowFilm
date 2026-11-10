"""Boundary checker fixtures. No SDK, network or reference execution."""
import importlib.util
import hashlib
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("source_scope", ROOT / "scripts/check-source-scope.py")
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)


class SourceScopeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        targets = []
        for folder, names in (("Sources", {"MeowUI", "MeowJSON", "MeowAccounts", "MeowMusicPlatform"}), ("Tests", {"MeowUITests"})):
            for name in sorted(names):
                self.write("native/MeowKit/{}/{}/Fixture.swift".format(folder, name), "import Foundation\n")
                kind = "target" if folder == "Sources" else "testTarget"
                targets.append('.{}(name: "{}", dependencies: [])'.format(kind, name))
        products = ['.library(name: "{}", targets: ["{}"])'.format(name, name) for name in sorted({"MeowUI", "MeowJSON", "MeowAccounts", "MeowMusicPlatform"})]
        self.write("native/MeowKit/Package.swift", "\n".join(products + targets))
        objects = ["{{ isa = XCSwiftPackageProductDependency; productName = {}; }}".format(name)
                   for name in sorted({"MeowUI", "MeowJSON", "MeowAccounts", "MeowMusicPlatform"})]
        identity = lambda value: hashlib.sha256(value.encode()).hexdigest()[:24].upper()
        target_sources = [
            ("Meowfilm", [directory + "/Fixture.swift"
                          for directory in sorted(scope.APPLICATION_DIRECTORIES)]),
            ("MeowfilmAcceptanceUITests", ["UITests/Fixture.swift"]),
            ("MeowfilmRuntimeTests", ["RuntimeTests/Fixture.swift"]),
        ]
        reference, embed, embed_phase = identity("native:ref"), identity("native:embed"), identity("native:phase")
        objects.extend([
            f'{reference} = {{ isa = PBXFileReference; name = MeowScriptHost.framework; path = "../../.cache/ios-native/$(MEOW_RUNTIME_SDK)-$(MEOW_RUNTIME_ARCH)/MeowScriptHost.framework"; }};',
            f'{embed} = {{ isa = PBXBuildFile; fileRef = {reference}; settings = {{ ATTRIBUTES = (CodeSignOnCopy,); }}; }};',
            f'{embed_phase} = {{ isa = PBXCopyFilesBuildPhase; dstSubfolderSpec = 10; files = ({embed},); }};',
        ])
        for target, paths in target_sources:
            builds = []
            for path in paths:
                self.write("apps/ios/" + path, "import Foundation\n")
                ref, build = identity(path), identity("build:" + path)
                objects.append(f'{ref} = {{ isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = "{path}"; }};')
                objects.append(f"{build} = {{ isa = PBXBuildFile; fileRef = {ref}; }};")
                builds.append(build)
            phase = identity(target + ":sources")
            objects.append(f"{phase} = {{ isa = PBXSourcesBuildPhase; files = ({','.join(builds)},); }};")
            phases = f"{phase},{embed_phase}," if target == "Meowfilm" else f"{phase},"
            objects.append(f"{identity(target)} = {{ isa = PBXNativeTarget; name = {target}; buildPhases = ({phases}); }};")
        self.write("apps/ios/Meowfilm.xcodeproj/project.pbxproj", "\n".join(objects))

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_valid_current_closure(self):
        self.assertEqual(scope.check(self.root), [])

    def test_current_repository(self):
        self.assertEqual(scope.check(ROOT), [])

    def test_windows_sources_and_tools_are_outside_ios_scope(self):
        self.write("apps/windows/Unlisted.swift", "import MeowUnknown\n")
        self.write("scripts/windows/probe.mjs", "import './missing-runtime.mjs';\n")
        self.write("native/JavaScript/Unlisted.swift", "import MeowUnknown\n")
        self.assertEqual(scope.check(self.root), [])

    def test_ios_source_link_cannot_import_a_windows_file(self):
        self.write("apps/windows/Fixture.swift", "import Foundation\n")
        source = self.root / "native/MeowKit/Sources/MeowUI/Fixture.swift"
        source.unlink()
        source.symlink_to(self.root / "apps/windows/Fixture.swift")
        self.assertTrue(any("Linked source:" in error for error in scope.check(self.root)))

    def test_dormant_target_directory_is_not_hidden_by_package_filter(self):
        self.write("native/MeowKit/Sources/Unlisted/Fixture.swift", "")
        self.assertTrue(any("extra=" in e for e in scope.check(self.root)))

    def test_undeclared_import_is_rejected(self):
        self.write("native/MeowKit/Sources/MeowMusicPlatform/Fixture.swift", "import MeowJSON")
        self.assertTrue(any("Undeclared imports" in e for e in scope.check(self.root)))

    def test_nested_sources_keep_the_target_nonempty(self):
        source = self.root / "native/MeowKit/Sources/MeowUI/Fixture.swift"
        nested = source.parent / "Layout/Fixture.swift"
        nested.parent.mkdir()
        source.rename(nested)
        self.assertEqual(scope.check(self.root), [])

    def test_nested_undeclared_import_is_not_hidden(self):
        self.write("native/MeowKit/Sources/MeowUI/Navigation/Fixture.swift", "import MeowMusicFeature")
        self.assertTrue(any("Undeclared imports for MeowUI" in e for e in scope.check(self.root)))

    def test_nested_test_import_is_checked(self):
        self.write("native/MeowKit/Tests/MeowUITests/Navigation/Fixture.swift", "@testable import MeowAccounts")
        self.assertTrue(any("Undeclared imports for MeowUITests" in e for e in scope.check(self.root)))

    def test_new_app_source_must_be_in_the_actual_sources_phase(self):
        self.write("apps/ios/Composition/Unlisted.swift", "import Foundation")
        self.assertTrue(any("Source membership for Meowfilm:" in e for e in scope.check(self.root)))

    def test_unowned_app_sources_are_rejected(self):
        self.write("apps/ios/Legacy.swift", "import Foundation\n")
        self.assertIn("Unowned App source: Legacy.swift", scope.check(self.root))

    def test_duplicate_and_cross_target_membership_are_rejected(self):
        path = self.root / "apps/ios/Meowfilm.xcodeproj/project.pbxproj"
        text = path.read_text(encoding="utf-8")
        app_build = hashlib.sha256(b"build:Application/Fixture.swift").hexdigest()[:24].upper()
        test_build = hashlib.sha256(b"build:UITests/Fixture.swift").hexdigest()[:24].upper()
        # Only phase references change; both PBXFileReference objects still exist.
        text = text.replace("files = (" + test_build + ",);",
                            "files = (" + app_build + "," + app_build + ",);")
        path.write_text(text, encoding="utf-8")
        errors = scope.check(self.root)
        self.assertTrue(any("Source membership for MeowfilmAcceptanceUITests:" in e for e in errors))
        self.assertIn("Orphaned or duplicate Swift source reference", errors)

    def test_dependency_cycles_are_rejected(self):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text(encoding="utf-8")
        text = text.replace('.target(name: "MeowJSON", dependencies: [])',
                            '.target(name: "MeowJSON", dependencies: ["MeowUI"])')
        text = text.replace('.target(name: "MeowUI", dependencies: [])',
                            '.target(name: "MeowUI", dependencies: ["MeowJSON"])')
        path.write_text(text, encoding="utf-8")
        self.assertTrue(any("Dependency cycle" in e for e in scope.check(self.root)))

    def test_unknown_local_product_is_rejected(self):
        path = self.root / "native/MeowKit/Package.swift"
        original = path.read_text()
        updated = original.replace('.target(name: "MeowJSON", dependencies: [])',
            '.target(name: "MeowJSON", dependencies: [.product(name: "MeowForeign", package: "foreign")])')
        path.write_text('.package(path: "../Foreign")\n' + updated)
        errors = scope.check(self.root)
        self.assertIn("Unapproved local product: MeowForeign", errors)
        self.assertIn("Unapproved package path", errors)

    def test_shared_ui_cannot_depend_on_business(self):
        path = self.root / "native/MeowKit/Package.swift"
        declared = path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
            '.target(name: "MeowUI", dependencies: ["MeowMusicPlatform"])')
        path.write_text(declared)
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "import MeowMusicPlatform\n")
        self.assertIn("Shared target depends on business: MeowUI", scope.check(self.root))

    def test_video_kernel_requires_its_exact_local_package_and_explicit_dependency(self):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
            '.target(name: "MeowUI", dependencies: [.product(name: "MeowVideoKernel", package: "videokernel")])')
        path.write_text('.package(path: "../VideoKernel")\n' + text)
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "import MeowVideoKernel\n")
        self.assertEqual(scope.check(self.root), [])
        path.write_text('.package(path: "../Foreign")\n' + text)
        self.assertTrue(any("Unapproved" in error for error in scope.check(self.root)))
        path.write_text('.package(path: "../VideoKernel")\n' + text.replace(
            'name: "MeowVideoKernel", package: "videokernel"',
            'name: "MeowVideoKernel", package: "foreign"'))
        self.assertTrue(any("Unapproved local product" in error for error in scope.check(self.root)))
        path.write_text('.package(path: "../VideoKernel")\n' + text)
        self.write("native/MeowKit/Sources/MeowJSON/Fixture.swift", "import MeowVideoKernel\n")
        self.assertTrue(any("Undeclared imports for MeowJSON" in error for error in scope.check(self.root)))



if __name__ == "__main__":
    unittest.main()

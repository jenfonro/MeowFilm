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
            objects.append(f"{identity(target)} = {{ isa = PBXNativeTarget; name = {target}; buildPhases = ({phase},); }};")
        self.write("apps/ios/Meowfilm.xcodeproj/project.pbxproj", "\n".join(objects))

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_valid_current_closure(self):
        self.assertEqual(scope.check(self.root), [])

    def test_current_repository(self):
        self.assertEqual(scope.check(ROOT), [])

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

    def test_only_the_declared_local_native_engine_product_is_admitted(self):
        path = self.root / "native/MeowKit/Package.swift"
        original = path.read_text()
        updated = original.replace('.target(name: "MeowJSON", dependencies: [])',
            '.target(name: "MeowJSON", dependencies: [.product(name: "MeowJavaScript", package: "javascript")])')
        path.write_text('.package(path: "../JavaScript")\n' + updated)
        self.write("native/MeowKit/Sources/MeowJSON/Fixture.swift", "import MeowJavaScript\n")
        self.assertEqual(scope.check(self.root), [])
        path.write_text('.package(path: "../Foreign")\n' + updated)
        self.assertTrue(any("Unapproved" in error for error in scope.check(self.root)))

    def test_local_rule_resource_is_an_explicit_dependency(self):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text().replace('.target(name: "MeowMusicPlatform", dependencies: [])',
            '.target(name: "MeowMusicPlatform", dependencies: [.product(name: "FilmRuleProgram", package: "film-rules")])')
        path.write_text('.package(path: "../../packages/film-rules")\n' + text)
        self.write("native/MeowKit/Sources/MeowMusicPlatform/Fixture.swift", "import FilmRuleProgram\n")
        self.assertEqual(scope.check(self.root), [])
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "import FilmRuleProgram\n")
        self.assertTrue(any("Undeclared imports for MeowUI" in error for error in scope.check(self.root)))



if __name__ == "__main__":
    unittest.main()

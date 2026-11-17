"""Boundary checker fixtures use SwiftPM manifest evaluation, not SDK builds or network."""
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
        self.write("native/MeowKit/Package.swift", '''// swift-tools-version: 5.10
import PackageDescription
let package = Package(name: "Fixture", products: [
''' + ",\n".join(products) + '''
], dependencies: [], targets: [
''' + ",\n".join(targets) + "\n])\n")
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

    def add_package(self, text, path):
        return text.replace("dependencies: [], targets:", f'dependencies: [.package(path: "{path}")], targets:')

    def test_equivalent_manifest_syntax_uses_evaluated_targets_and_products(self):
        path = self.root / "native/MeowKit/Package.swift"
        # Products and dependencies may be computed rather than inline literals.
        text = path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
            '.target(\n    name: "MeowUI"\n)')
        text = text.replace('let package =', 'let uiName = "MeowUI"\nlet package =')
        text = text.replace('.library(name: "MeowUI", targets: ["MeowUI"])',
            '.library(name: uiName, targets: [uiName])')
        path.write_text(text)
        self.assertEqual(scope.check(self.root), [])

    def test_missing_and_wrong_evaluated_targets_are_rejected(self):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text()
        missing = text.replace('.target(name: "MeowUI", dependencies: []),', '')
        missing = missing.replace('.library(name: "MeowUI", targets: ["MeowUI"])', '')
        path.write_text(missing)
        self.assertTrue(any("Sources directories:" in error for error in scope.check(self.root)))
        wrong = text.replace('.target(name: "MeowUI", dependencies: [])', '.target(name: "OtherUI")')
        wrong = wrong.replace('.library(name: "MeowUI", targets: ["MeowUI"])',
            '.library(name: "MeowUI", targets: ["OtherUI"])')
        path.write_text(wrong)
        self.assertTrue(any("Sources directories:" in error for error in scope.check(self.root)))

    def test_valid_current_closure(self):
        self.assertEqual(scope.check(self.root), [])

    def test_current_repository(self):
        self.assertEqual(scope.check(ROOT), [])

    def test_generated_app_build_cache_is_not_source(self):
        self.write("apps/ios/.cache/DerivedData/Meowfilm/Build/DerivedSources/GeneratedAssetSymbols.swift",
                   "import Foundation\n")
        self.assertEqual(scope.check(self.root), [])

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

    def remove_app_sources(self, directories):
        project = self.root / "apps/ios/Meowfilm.xcodeproj/project.pbxproj"
        text = project.read_text()
        for directory in directories:
            path = directory + "/Fixture.swift"
            (self.root / "apps/ios" / path).unlink()
            reference = hashlib.sha256(path.encode()).hexdigest()[:24].upper()
            build = hashlib.sha256(("build:" + path).encode()).hexdigest()[:24].upper()
            text = "\n".join(line for line in text.splitlines() if not line.startswith((reference, build)))
            text = text.replace(build + ",", "")
        project.write_text(text)
        return text

    def test_app_membership_allows_unused_responsibility_directories(self):
        text = self.remove_app_sources(("Composition", "Navigation"))
        (self.root / "apps/ios/Navigation").rmdir()
        self.assertEqual(scope.application_membership(self.root / "apps/ios", text), [])

    def test_app_target_requires_real_source_input(self):
        text = self.remove_app_sources(scope.APPLICATION_DIRECTORIES)
        self.assertIn("Empty App source target: Meowfilm", scope.application_membership(self.root / "apps/ios", text))

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
        path.write_text(self.add_package(updated, "../Foreign"))
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

    def test_shared_ui_cannot_depend_on_novel_business(self):
        path = self.root / "native/MeowKit/Package.swift"
        declared = path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
            '.target(name: "MeowUI", dependencies: ["MeowNovelFeature"])')
        path.write_text(declared.replace('targets: [\n',
            'targets: [\n.target(name: "MeowNovelFeature", dependencies: []),\n'))
        self.write("native/MeowKit/Sources/MeowNovelFeature/Fixture.swift", "")
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "import MeowNovelFeature\n")
        self.assertIn("Shared target depends on business: MeowUI", scope.check(self.root))

    def test_video_kernel_requires_its_exact_local_package_and_explicit_dependency(self):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
            '.target(name: "MeowUI", dependencies: [.product(name: "MeowVideoKernel", package: "videokernel")])')
        path.write_text(self.add_package(text, "../VideoKernel"))
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "import MeowVideoKernel\n")
        self.assertEqual(scope.check(self.root), [])
        with_foreign_package = self.add_package(text, "../VideoKernel").replace(
            'dependencies: [.package(path: "../VideoKernel")], targets:',
            'dependencies: [.package(path: "../VideoKernel"), .package(path: "../Foreign")], targets:')
        path.write_text(with_foreign_package)
        self.assertTrue(any("Unapproved" in error for error in scope.check(self.root)))
        wrong_identity = self.add_package(text.replace(
            'name: "MeowVideoKernel", package: "videokernel"',
            'name: "MeowVideoKernel", package: "foreign"'), "../VideoKernel").replace(
                'dependencies: [.package(path: "../VideoKernel")], targets:',
                'dependencies: [.package(path: "../VideoKernel"), .package(path: "../Foreign")], targets:')
        path.write_text(wrong_identity)
        self.assertTrue(any("Unapproved local product" in error for error in scope.check(self.root)))
        path.write_text(self.add_package(text, "../VideoKernel"))
        self.write("native/MeowKit/Sources/MeowJSON/Fixture.swift", "import MeowVideoKernel\n")
        self.assertTrue(any("Undeclared imports for MeowJSON" in error for error in scope.check(self.root)))

    def test_audio_kernel_has_an_explicit_native_dependency_and_no_video_closure(self):
        path = self.root / "native/MeowKit/Package.swift"
        audio = '.target(name: "MeowAudio", dependencies: [.product(name: "CMeowAudioKernel", package: "audiokernel")])'
        text = path.read_text().replace("targets: [\n", "targets: [\n" + audio +
            ',\n.target(name: "MeowPlayback", dependencies: ["MeowAudio"]),\n')
        text = self.add_package(text, "../AudioKernel")
        path.write_text(text)
        self.write("native/MeowKit/Sources/MeowAudio/Fixture.swift", "import CMeowAudioKernel\n")
        self.write("native/MeowKit/Sources/MeowPlayback/Fixture.swift", "import MeowAudio\n")
        self.assertEqual(scope.check(self.root), [])
        wrong = text.replace('package: "audiokernel"', 'package: "foreign"')
        wrong = wrong.replace('.package(path: "../AudioKernel")',
            '.package(path: "../AudioKernel"), .package(path: "../Foreign")')
        path.write_text(wrong)
        self.assertTrue(any("Unapproved local product" in error for error in scope.check(self.root)))
        video = text.replace(audio, audio[:-2] +
            ', .product(name: "MeowVideoKernel", package: "videokernel")])')
        video = video.replace('.package(path: "../AudioKernel")',
            '.package(path: "../AudioKernel"), .package(path: "../VideoKernel")')
        path.write_text(video)
        self.assertTrue(any("depends on video" in error for error in scope.check(self.root)))

    def sqlite_target(self, consumer="MeowMusicNativeService", module="sqlite3"):
        path = self.root / "native/MeowKit/Package.swift"
        text = path.read_text().replace("targets: [\n", 'targets: [\n.systemLibrary(name: "CSQLite", pkgConfig: "sqlite3"),\n'
            f'.target(name: "{consumer}", dependencies: ["CSQLite"]),\n')
        path.write_text(text)
        self.write(f"native/MeowKit/Sources/{consumer}/Fixture.swift", "import CSQLite\n")
        self.write("native/MeowKit/Sources/CSQLite/module.modulemap",
            f'module CSQLite [system] {{ header "shim.h" link "{module}" export * }}\n')
        self.write("native/MeowKit/Sources/CSQLite/shim.h", "#include <sqlite3.h>\n")

    def test_system_sqlite_has_an_explicit_declared_native_consumer(self):
        self.sqlite_target()
        self.assertEqual(scope.check(self.root), [])

    def test_system_sqlite_rejects_other_linked_libraries(self):
        self.sqlite_target(module="foreign")
        self.assertIn("Invalid system module: CSQLite", scope.check(self.root))

    def test_system_sqlite_allows_other_declared_consumers(self):
        self.sqlite_target(consumer="SQLiteConsumer")
        self.assertEqual(scope.check(self.root), [])

    def test_system_sqlite_accepts_equivalent_declared_paths_providers_and_headers(self):
        self.sqlite_target()
        path = self.root / "native/MeowKit/Package.swift"
        path.write_text(path.read_text().replace('.systemLibrary(name: "CSQLite", pkgConfig: "sqlite3")',
            '.systemLibrary(name: "CSQLite", path: "SystemInterfaces/../SystemInterfaces/SQLite", pkgConfig: "sqlite3", '
            'providers: [.brew(["sqlite3"]), .apt(["libsqlite3-dev"])])'))
        directory = self.root / "native/MeowKit/SystemInterfaces/SQLite"
        directory.parent.mkdir()
        (self.root / "native/MeowKit/Sources/CSQLite").rename(directory)
        (directory / "shim.h").rename(directory / "SDK.h")
        (directory / "SDK.h").write_text('#pragma once\n/* SDK declarations */\n# include "SQLite.h"\n')
        (directory / "SQLite.h").write_text('#pragma once\n#include "SDK.h"\n# include <sqlite3.h>\n')
        (directory / "Helpers.h").write_text("#pragma once\n")
        (directory / "module.modulemap").write_text('''// The SDK is linked by this system interface.
module "CSQLite" [system] {
    header "../SQLite/SDK.h"
    textual header "Helpers.h"
    link "sqlite3"
    export *
}
''')
        self.assertEqual(scope.check(self.root), [])

    def test_system_sqlite_accepts_umbrella_headers_and_sdk_imports(self):
        self.sqlite_target()
        self.write("native/MeowKit/Sources/CSQLite/include/SQLite.h", '#pragma once\n#import "sqlite3.h"\n')
        for declaration in ('umbrella "include"', 'umbrella header "include/SQLite.h"'):
            with self.subTest(declaration=declaration):
                self.write("native/MeowKit/Sources/CSQLite/module.modulemap",
                    'module CSQLite [system] { ' + declaration + ' link "sqlite3" export * }\n')
                self.assertEqual(scope.check(self.root), [])
        self.write("native/MeowKit/Sources/CSQLite/include/sqlite3.h", "// Local file is not the SDK header.\n")
        self.assertIn("Invalid system module: CSQLite", scope.check(self.root))

    def test_system_sqlite_requires_the_actual_sdk_header_and_link(self):
        self.sqlite_target()
        self.write("native/MeowKit/Sources/CSQLite/shim.h", "// #include <sqlite3.h>\n")
        self.assertIn("Invalid system module: CSQLite", scope.check(self.root))
        self.write("native/MeowKit/Sources/CSQLite/shim.h", "#include <sqlite3.h>\n")
        self.write("native/MeowKit/Sources/CSQLite/module.modulemap",
                   'module CSQLite { header "shim.h" /* link "sqlite3" */ export * }\n')
        self.assertIn("Invalid system module: CSQLite", scope.check(self.root))

    def foreign_target(self):
        path = self.root / "native/MeowKit/Package.swift"
        path.write_text(path.read_text().replace("targets: [\n", 'targets: [\n.systemLibrary(name: "Foreign"),\n'))
        self.write("native/MeowKit/Sources/Foreign/module.modulemap", 'module Foreign { header "shim.h" export * }\n')
        self.write("native/MeowKit/Sources/Foreign/shim.h", "#include <sqlite3.h>\n")

    def test_system_modules_use_their_manifest_identity_and_declared_dependencies(self):
        self.foreign_target()
        path = self.root / "native/MeowKit/Package.swift"
        path.write_text(path.read_text().replace('.target(name: "MeowUI", dependencies: [])',
                                                '.target(name: "MeowUI", dependencies: ["Foreign"])'))
        self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", "@preconcurrency import Foreign\n")
        self.assertEqual(scope.check(self.root), [])

    def test_unfamiliar_system_imports_cannot_bypass_dependency_checks(self):
        self.foreign_target()
        for imported in ("import Foreign", "  @preconcurrency import Foreign", "import func Foreign.sqlite3_initialize"):
            with self.subTest(imported=imported):
                self.write("native/MeowKit/Sources/MeowUI/Fixture.swift", imported + "\n")
                self.assertIn("Undeclared imports for MeowUI: ['Foreign']", scope.check(self.root))

    def test_every_system_module_requires_its_declared_interface_and_real_headers(self):
        self.foreign_target()
        for text in ('module Other { header "shim.h" export * }',
                     '/* module Foreign {} */ module Other { header "shim.h" export * }',
                     'module Foreign { header "missing.h" export * }'):
            with self.subTest(modulemap=text):
                self.write("native/MeowKit/Sources/Foreign/module.modulemap", text + "\n")
                self.assertIn("Invalid system module: Foreign", scope.check(self.root))
        (self.root / "native/MeowKit/Sources/Foreign/module.modulemap").unlink()
        self.assertIn("Invalid system module: Foreign", scope.check(self.root))

    def test_system_paths_require_real_members_within_the_package(self):
        self.foreign_target()
        path = self.root / "native/MeowKit/Package.swift"
        original = path.read_text()
        for declared in ("Sources/Missing", "../Foreign"):
            with self.subTest(path=declared):
                path.write_text(original.replace('.systemLibrary(name: "Foreign")',
                    '.systemLibrary(name: "Foreign", path: "' + declared + '")'))
                self.assertTrue(scope.check(self.root))
        path.write_text(original)
        header = self.root / "native/MeowKit/Sources/Foreign/shim.h"
        header.unlink()
        header.symlink_to(self.root / "native/MeowKit/Sources/MeowUI/Fixture.swift")
        self.assertIn("Invalid system module: Foreign", scope.check(self.root))

    def test_declared_system_paths_do_not_hide_unowned_source_members(self):
        self.foreign_target()
        path = self.root / "native/MeowKit/Package.swift"
        path.write_text(path.read_text().replace('.systemLibrary(name: "Foreign")',
            '.systemLibrary(name: "Foreign", path: "Sources/Interfaces/Foreign")'))
        directory = self.root / "native/MeowKit/Sources/Interfaces/Foreign"
        directory.parent.mkdir()
        (self.root / "native/MeowKit/Sources/Foreign").rename(directory)
        self.write("native/MeowKit/Sources/Interfaces/Orphan.swift", "import Foundation\n")
        self.assertIn("Unowned package source: native/MeowKit/Sources/Interfaces/Orphan.swift", scope.check(self.root))

if __name__ == "__main__":
    unittest.main()

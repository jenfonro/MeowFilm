"""Pinned-input and Mach-O fixtures; no Swift build, Mac connection or network."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[2]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


prepare = module("prepare_aether", "prepare-ios-aether-kernel.py")
scope = module("scope_aether", "check-source-scope.py")


def image(name="AetherLibavcodec", platform=2, minimum=16 << 16, cpu=0x100000C,
          dependencies=(), code=b"locked machine code", namespace=True, constant=b""):
    def dylib(command, value):
        text = value.encode() + b"\0"
        size = (24 + len(text) + 7) // 8 * 8
        return struct.pack("<6I", command, size, 24, 0, 0, 0) + text.ljust(size - 24, b"\0")
    commands = [
        struct.pack("<6I", 0x32, 24, platform, minimum, (27 << 16) | (2 << 8), 0),
        dylib(0xD, "@rpath/" + name + ".framework/" + name),
        *[dylib(0xC, value) for value in dependencies]
    ]
    contents = [(b"__text", code)] + ([(b"__const", constant)] if constant else [])
    segment_size = 72 + 80 * len(contents)
    code_offset = 32 + sum(map(len, commands)) + segment_size
    length = code_offset + sum(len(value) for _, value in contents)
    segment = struct.pack("<II16sQQQQIIII", 0x19, segment_size, b"__TEXT", 0, length,
                          0, length, 7, 5, len(contents), 0)
    sections = b""
    for section_name, value in contents:
        sections += struct.pack("<16s16sQQ8I", section_name, b"__TEXT", code_offset, len(value),
                                code_offset, 0, 0, 0, 0, 0, 0, 0)
        code_offset += len(value)
    commands.append(segment + sections)
    header = struct.pack("<8I", 0xFEEDFACF, cpu, 0, 6, len(commands), sum(map(len, commands)),
                         0x80 if namespace else 0, 0)
    return header + b"".join(commands) + b"".join(value for _, value in contents)


def universal(images):
    offset = 8 + 20 * len(images)
    header = struct.pack(">II", 0xCAFEBABE, len(images))
    for cpu, data in images:
        header += struct.pack(">5I", cpu, 0, offset, len(data), 0)
        offset += len(data)
    return header + b"".join(data for _, data in images)


def static_archive(cpu):
    data = bytearray(image(cpu=cpu))
    struct.pack_into("<I", data, 12, 1)
    header = b"file.o/".ljust(16) + b"0".ljust(12) + b"0".ljust(6) + b"0".ljust(6)
    header += b"644".ljust(8) + str(len(data)).encode().ljust(10) + b"`\n"
    return b"!<arch>\n" + header + data + (b"\n" if len(data) % 2 else b"")


class AetherLockTests(unittest.TestCase):
    def test_exact_sources_and_complete_native_targets(self):
        lock = prepare.read_lock()
        self.assertEqual(lock["sources"]["AetherEngine"]["revision"], "0de31d0b9478b99dd49baa2bd8318604a49f99a3")
        self.assertEqual(lock["sources"]["FFmpegBuild"]["version"], "3.15.0")
        self.assertEqual(lock["sources"]["LibDovi"]["version"], "2.2.0")
        self.assertEqual(len(lock["libraries"]), 9)
        self.assertEqual(set(lock["targets"]), {"ios-arm64", "ios-simulator-arm64", "ios-simulator-x86_64",
                                               "macos-arm64", "macos-x86_64"})
        self.assertEqual({value["minimum"] for key, value in lock["targets"].items() if key.startswith("ios")}, {"18.0"})
        self.assertEqual(len(lock["preparedInventorySHA256"]), 64)

    def test_unknown_target_fails_before_preparation(self):
        result = subprocess.run([sys.executable, "-I", "-B", str(ROOT / "scripts/prepare-ios-aether-kernel.py"),
                                 "--target", "ios-other"], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("unknown locked Aether target", result.stderr)


class AetherPrepareTests(unittest.TestCase):
    def setUp(self):
        base = ROOT / "tmp/test-results/aether-script-fixtures"
        base.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=base)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def write(self, name, value):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value)
        return path

    def test_selected_closure_excludes_optional_targets_and_other_platforms(self):
        select = lambda repo, path: prepare.selected_path(repo, Path(path), ["AetherLibavcodec"])
        self.assertEqual(str(select("AetherEngine", "Sources/AetherEngine/Engine.swift")), "AetherEngine/Engine.swift")
        self.assertIsNone(select("AetherEngine", "Sources/AetherEngineSMB/Client.swift"))
        self.assertIsNone(select("AetherEngine", "Examples/Player.swift"))
        self.assertIsNone(select("FFmpegBuild", "Sources/AetherLibavcodec.xcframework/tvos-arm64/Info.plist"))
        self.assertIsNone(select("FFmpegBuild", "Sources/AetherLibavcodec.xcframework/ios-arm64/dSYMs/File"))
        self.assertIsNone(select("LibTrueHD", "TrueHD.xcframework/ios-arm64/libtruehd.a"))

    def test_exact_download_hash_rejects_modified_existing_archive(self):
        checksum = hashlib.sha256(b"archive").hexdigest()
        source = {"sha256": checksum, "url": "https://invalid.example"}
        file = self.write("downloads/" + checksum + ".tar.gz", b"archive")
        with patch.object(prepare, "SHARED", self.root):
            self.assertEqual(prepare.archive_for(source), file)
            file.write_bytes(b"tampered")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                prepare.archive_for(source)

    def test_large_download_preserves_bytes_and_rejects_hash_mismatch(self):
        chunks = [b'a' * (1024 * 1024)] * 256 + [b'tail']
        digest = hashlib.sha256()
        for chunk in chunks:
            digest.update(chunk)
        source = {"sha256": digest.hexdigest(), "url": "https://invalid.example/source.tar.gz"}
        response = MagicMock()
        response.__enter__.return_value = response
        response.read.side_effect = [*chunks, b""]
        with patch.object(prepare, "SHARED", self.root), \
                patch.object(prepare.urllib.request, "build_opener") as opener:
            opener.return_value.open.return_value = response
            archive = prepare.archive_for(source)
            self.assertEqual(archive.stat().st_size, sum(map(len, chunks)))
            self.assertEqual(prepare.digest(archive), source["sha256"])
            response.read.side_effect = [b"changed", b""]
            source["sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                prepare.archive_for(source)
            rejected = self.root / "downloads" / (source["sha256"] + ".tar.gz")
            self.assertFalse(rejected.exists())
            self.assertFalse(rejected.with_suffix(".partial").exists())

    def test_failed_or_interrupted_download_removes_partial_archive(self):
        source = {"sha256": "0" * 64, "url": "https://invalid.example/source.tar.gz"}
        for error in (OSError("connection closed"), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__), \
                    patch.object(prepare, "SHARED", self.root), \
                    patch.object(prepare.urllib.request, "build_opener") as opener:
                response = MagicMock()
                response.__enter__.return_value = response
                response.read.side_effect = [b"partial", error, b"partial", error]
                opener.return_value.open.return_value = response
                with self.assertRaises(type(error)):
                    prepare.archive_for(source)
                rejected = self.root / "downloads" / (source["sha256"] + ".tar.gz")
                self.assertFalse(rejected.exists())
                self.assertFalse(rejected.with_suffix(".partial").exists())
                response.__exit__.assert_called()

    def test_archive_path_and_symlink_cannot_escape_selected_inputs(self):
        for filename, link in (("repo/../../escape", None),
                               ("repo/Sources/AetherEngine/link", "/absolute/escape"),
                               ("repo/Sources/AetherEngine/link", "../../../escape")):
            archive = self.root / "bad.tar.gz"
            with tarfile.open(archive, "w:gz") as stream:
                member = tarfile.TarInfo(filename)
                if link:
                    member.type, member.linkname = tarfile.SYMTYPE, link
                    stream.addfile(member)
                else:
                    member.size = 4
                    stream.addfile(member, io.BytesIO(b"data"))
            with self.subTest(filename=filename, link=link), self.assertRaisesRegex(ValueError, "unsafe|escaping"):
                prepare.extract_closure(archive, "AetherEngine", self.root / "inputs", {"licenses": {}}, [])

    def test_inventory_rejects_added_modified_removed_and_escaping_inputs(self):
        file = self.write("inputs/Engine.swift", b"source")
        directory = file.parent
        expected = prepare.inventory(directory)
        for action in ("added", "modified", "removed", "linked"):
            with self.subTest(action=action):
                if action == "added":
                    self.write("inputs/unexpected.swift", b"extra")
                elif action == "modified":
                    file.write_bytes(b"changed")
                elif action == "removed":
                    file.unlink()
                else:
                    file.unlink()
                    file.symlink_to(self.write("outside.swift", b"outside"))
                with self.assertRaises(ValueError):
                    prepare.verify_inventory(directory, expected)
                if action == "added":
                    (directory / "unexpected.swift").unlink()
                elif action in ("removed", "linked"):
                    file.unlink(missing_ok=True)
                file.write_bytes(b"source")

    def test_forged_receipt_and_unrecorded_patch_changes_fail_locked_baseline(self):
        original = {"AetherEngine/Engine.swift": {"sha256": "original"}}
        lock = {"patches": [], "preparedInventorySHA256": prepare.identity(original)}
        prepare.verify_receipt_files(original, lock)
        with self.assertRaisesRegex(ValueError, "pinned compile closure"):
            prepare.verify_receipt_files({"AetherEngine/Engine.swift": {"sha256": "forged"}}, lock)
        lock["patches"] = [{"files": {"AetherEngine/Engine.swift": {"upstream": "original", "patched": "reviewed"},
                                    "AetherEngine/New.swift": {"upstream": None, "patched": "added"}}}]
        patched = {"AetherEngine/Engine.swift": {"sha256": "reviewed"}, "AetherEngine/New.swift": {"sha256": "added"}}
        prepare.verify_receipt_files(patched, lock)
        patched["unrecorded.swift"] = {"sha256": "hidden"}
        with self.assertRaisesRegex(ValueError, "pinned compile closure"):
            prepare.verify_receipt_files(patched, lock)

    def test_private_package_cache_is_not_overwritten_when_modified(self):
        shared = self.root / "shared"
        shared.mkdir()
        (shared / "Engine.swift").write_bytes(b"source")
        receipt = {"inputs": {}, "files": prepare.inventory(shared)}
        package = self.root / "package"
        package.mkdir()
        with patch.object(prepare, "PACKAGE", package):
            prepare.package_inputs(shared, receipt)
            (package / ".cache/Engine.swift").write_bytes(b"user change")
            with self.assertRaisesRegex(ValueError, "modified"):
                prepare.package_inputs(shared, {"inputs": {"new": True}, "files": receipt["files"]})
            self.assertEqual((package / ".cache/Engine.swift").read_bytes(), b"user change")

    def test_native_metadata_correction_does_not_modify_machine_code(self):
        name = "AetherLibavcodec"
        for library in (name, "Dovi"):
            entries = []
            for slice_name, (platform, arches, minimum) in prepare.SLICES.items():
                cpus = [(0x100000C if arch == "arm64" else 0x1000007) for arch in sorted(arches)]
                if library == "Dovi":
                    parts = [(cpu, static_archive(cpu)) for cpu in cpus]
                    path = "libdovi.a"
                else:
                    parts = [(cpu, image(platform=platform, minimum=(14 if platform == 1 else 16) << 16, cpu=cpu)) for cpu in cpus]
                    path = library + ".framework/" + library
                    metadata_path = library + ".framework/" + ("Versions/A/Resources/Info.plist" if platform == 1 else "Info.plist")
                    self.write("native/binaries/" + library + ".xcframework/" + slice_name + "/" + metadata_path,
                               plistlib.dumps({"CFBundleExecutable": library, "CFBundlePackageType": "FMWK",
                                               "MinimumOSVersion": minimum if platform == 1 else "26.0"}))
                self.write("native/binaries/" + library + ".xcframework/" + slice_name + "/" + path,
                           universal(parts) if len(parts) > 1 else parts[0][1])
                entry = {"LibraryIdentifier": slice_name, "LibraryPath": path if library == "Dovi" else library + ".framework",
                         "BinaryPath": path, "SupportedArchitectures": sorted(arches),
                         "SupportedPlatform": "macos" if platform == 1 else "ios"}
                if platform == 7:
                    entry["SupportedPlatformVariant"] = "simulator"
                entries.append(entry)
            self.write("native/binaries/" + library + ".xcframework/Info.plist",
                       plistlib.dumps({"AvailableLibraries": entries + [{"LibraryIdentifier": "tvos-unused"}]}))
        native = self.root / "native"
        lock = {"libraries": [name]}
        before = {path: prepare.digest(path) for path in native.rglob(name) if path.is_file()}
        with self.assertRaisesRegex(ValueError, "uncorrected"):
            prepare.normalize_and_verify(native, lock)
        prepare.normalize_and_verify(native, lock, normalize=True)
        prepare.normalize_and_verify(native, lock)
        after = {path: prepare.digest(path) for path in native.rglob(name) if path.is_file()}
        self.assertEqual(before, after)
        metadata = plistlib.loads((native / "binaries" / (name + ".xcframework") / "ios-arm64" /
                                  (name + ".framework/Info.plist")).read_bytes())
        self.assertEqual(metadata["MinimumOSVersion"], "16.0")

    def test_macho_checker_accepts_only_own_dynamic_namespace_and_platform(self):
        name = "AetherLibavcodec"
        file = self.write(name, image(dependencies=["/usr/lib/libSystem.B.dylib"]))
        prepare.verify_image(file, 2, {"arm64"}, "18.0", [name], name)
        invalid = [
            image(namespace=False), image(platform=7), image(minimum=26 << 16),
            image(cpu=0x1000007), image(dependencies=["@rpath/libavcodec.63.dylib"])
        ]
        for data in invalid:
            file.write_bytes(data)
            with self.assertRaises(ValueError):
                prepare.verify_image(file, 2, {"arm64"}, "18.0", [name], name)

    def test_code_fingerprint_ignores_linkedit_tail_but_rejects_changed_code(self):
        data = image()
        self.assertEqual(prepare.section_fingerprint(data), prepare.section_fingerprint(data + b"signature"))
        self.assertNotEqual(prepare.section_fingerprint(data), prepare.section_fingerprint(image(code=b"modified code")))

    def app_fixture(self, target="ios-arm64"):
        lock = prepare.read_lock()
        config = lock["targets"][target]
        platform, arches, minimum = prepare.SLICES[config["slice"]]
        cpus = {arch: cpu for cpu, arch in prepare.CPU.items()}
        package = self.root / "package"
        app = self.root / "Meowfilm.app"
        for name in lock["libraries"]:
            parts = [(cpus[arch], image(name, platform=platform, cpu=cpus[arch],
                                       code=(name + arch).encode(), constant=("data " + arch).encode(),
                                       dependencies=["/usr/lib/libSystem.B.dylib"] + (
                                           ["@rpath/AetherLibavutil.framework/AetherLibavutil"]
                                           if name != "AetherLibavutil" else [])))
                     for arch in sorted(arches)]
            data = universal(parts) if len(parts) > 1 else parts[0][1]
            original = self.write("package/.cache/binaries/" + name + ".xcframework/" + config["slice"] +
                                  "/" + name + ".framework/" + name, data)
            (original.parents[2] / "Info.plist").write_bytes(plistlib.dumps({
                "AvailableLibraries": [{"LibraryIdentifier": config["slice"],
                                        "BinaryPath": name + ".framework/" + name,
                                        "LibraryPath": name + ".framework"}]
            }))
            self.write("Meowfilm.app/Frameworks/" + name + ".framework/" + name, data)
            self.write("Meowfilm.app/Frameworks/" + name + ".framework/Info.plist",
                       plistlib.dumps({"MinimumOSVersion": minimum}))
            (original.parent / "PrivacyInfo.xcprivacy").write_bytes(b"framework privacy")
            self.write("Meowfilm.app/Frameworks/" + name + ".framework/PrivacyInfo.xcprivacy", b"framework privacy")
        name = lock["libraries"][0]
        self.write("Meowfilm.app/Frameworks/MeowAetherKernel.framework/MeowAetherKernel",
                   image("MeowAetherKernel", platform=platform, cpu=cpus[config["arch"]], minimum=18 << 16,
                         dependencies=["@rpath/" + name + ".framework/" + name]))
        self.write("Meowfilm.app/notice.txt", b"license")
        for filename in ("silent-atmos.mp4", "PrivacyInfo.xcprivacy"):
            self.write("package/.cache/AetherEngine/Resources/" + filename, b"source resource")
            self.write("Meowfilm.app/AetherKernel_AetherEngine.bundle/" + filename, b"source resource")
        lock["licenses"] = {"notice.txt": hashlib.sha256(b"license").hexdigest()}
        return app, package, lock

    def test_app_accepts_locked_simulator_universal_and_target_only_slices(self):
        cpus = {arch: cpu for cpu, arch in prepare.CPU.items()}
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        for target in ("ios-simulator-x86_64", "ios-simulator-arm64"):
            app, package, lock = self.app_fixture(target)
            with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
                for trimmed in (False, True):
                    with self.subTest(target=target, trimmed=trimmed):
                        for name in lock["libraries"]:
                            binary = app / "Frameworks" / (name + ".framework") / name
                            parts = [(cpus[arch], data + b"signing LINKEDIT tail")
                                     for arch, data in prepare.binary_slices(binary.read_bytes())
                                     if not trimmed or arch == lock["targets"][target]["arch"]]
                            binary.write_bytes(universal(parts) if len(parts) > 1 else parts[0][1])
                        prepare.verify_app(app, target, lock)

    def test_app_rejects_missing_target_and_arches_not_locked_for_that_slice(self):
        target = "ios-simulator-x86_64"
        app, package, lock = self.app_fixture(target)
        binary = app / "Frameworks/AetherLibavcodec.framework/AetherLibavcodec"
        original = binary.read_bytes()
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            for data in (image(platform=7), universal([])):
                with self.subTest(missing_target=len(data)), self.assertRaisesRegex(ValueError, "architecture mismatch"):
                    binary.write_bytes(data)
                    prepare.verify_app(app, target, lock)
            binary.write_bytes(original)
            # arm64 still exists in the device/macOS lock, but not in this platform slice.
            del lock["targets"]["ios-simulator-arm64"]
            with self.assertRaisesRegex(ValueError, "architecture mismatch"):
                prepare.verify_app(app, target, lock)
        app, package, lock = self.app_fixture()
        binary.write_bytes(universal([(0x100000C, image()), (0x1000007, image(cpu=0x1000007))]))
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            with self.assertRaisesRegex(ValueError, "architecture mismatch"):
                prepare.verify_app(app, "ios-arm64", lock)

    def test_app_wrapper_still_requires_exact_target_architecture(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        binary = app / "Frameworks/MeowAetherKernel.framework/MeowAetherKernel"
        for data in (image("MeowAetherKernel", platform=7),
                     universal([(cpu, image("MeowAetherKernel", platform=7, cpu=cpu)) for cpu in prepare.CPU])):
            with self.subTest(universal=data[:4] == b"\xca\xfe\xba\xbe"):
                binary.write_bytes(data)
                with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output") as nm:
                    with self.assertRaisesRegex(ValueError, "architecture mismatch"):
                        prepare.verify_app(app, "ios-simulator-x86_64", lock)
                    nm.assert_not_called()

    def test_app_validates_metadata_and_dependencies_on_every_retained_slice(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        binary = app / "Frameworks/AetherLibzvbi.framework/AetherLibzvbi"
        parts = dict(prepare.binary_slices(binary.read_bytes()))
        invalid = [
            ({"platform": 2}, "deployment mismatch"), ({"platform": 1}, "deployment mismatch"),
            ({"minimum": 19 << 16}, "deployment mismatch"), ({"namespace": False}, "two-level"),
            ({"name": "ForeignLibrary"}, "identity mismatch"),
            ({"dependencies": ["@rpath/libavcodec.63.dylib"]}, "unapproved"),
            ({"dependencies": ["@rpath/AetherLibunknown.framework/AetherLibunknown"]}, "unapproved")
        ]
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            for arch, cpu in (("arm64", 0x100000C), ("x86_64", 0x1000007)):
                for changes, message in invalid:
                    with self.subTest(arch=arch, changes=changes), self.assertRaisesRegex(ValueError, message):
                        values = {"name": "AetherLibzvbi", "platform": 7, "cpu": cpu, **changes}
                        data = {**parts, arch: image(**values)}
                        binary.write_bytes(universal([(0x100000C, data["arm64"]), (0x1000007, data["x86_64"])]))
                        prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_app_compares_all_code_and_data_sections_on_every_retained_slice(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        binary = app / "Frameworks/AetherLibavcodec.framework/AetherLibavcodec"
        parts = dict(prepare.binary_slices(binary.read_bytes()))
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            for arch in ("arm64", "x86_64"):
                invalid = [
                    (parts[arch].replace(("AetherLibavcodec" + arch).encode(),
                                         b"x" * len("AetherLibavcodec" + arch)), "code differs"),
                    (parts[arch].replace(("data " + arch).encode(), b"x" * (5 + len(arch))), "code differs"),
                    (parts[arch].replace(b"__text", b"__gone"), "missing Aether machine code"),
                    (parts[arch][:-1], "truncated Aether Mach-O section")
                ]
                for changed, message in invalid:
                    with self.subTest(arch=arch, message=message), self.assertRaisesRegex(ValueError, message):
                        data = {**parts, arch: changed}
                        binary.write_bytes(universal([(0x100000C, data["arm64"]), (0x1000007, data["x86_64"])]))
                        prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_universal_app_still_checks_library_presence_resources_and_licenses(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        paths = [
            ("Frameworks/AetherLibavcodec.framework/PrivacyInfo.xcprivacy", "privacy manifest"),
            ("AetherKernel_AetherEngine.bundle/PrivacyInfo.xcprivacy", "source resource"),
            ("AetherKernel_AetherEngine.bundle/silent-atmos.mp4", "source resource"),
            ("notice.txt", "license")
        ]
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            for relative, message in paths:
                file = app / relative
                original = file.read_bytes()
                with self.subTest(relative=relative), self.assertRaisesRegex(ValueError, message):
                    file.write_bytes(b"changed")
                    prepare.verify_app(app, "ios-simulator-x86_64", lock)
                file.write_bytes(original)
            file = app / "Frameworks/AetherLibzvbi.framework/AetherLibzvbi"
            file.unlink()
            with self.assertRaises(FileNotFoundError):
                prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_universal_app_preserves_import_ownership_and_framework_deployment_gate(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        for symbols in ("", " (undefined) external _avcodec_open2 (from libavcodec)\n",
                        " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
                        " (undefined) external _avformat_open_input (from libavformat)\n"):
            with self.subTest(symbols=symbols), patch.object(prepare, "PACKAGE", package), \
                    patch.object(subprocess, "check_output", return_value=symbols):
                with self.assertRaisesRegex(ValueError, "not bound"):
                    prepare.verify_app(app, "ios-simulator-x86_64", lock)
        metadata = app / "Frameworks/AetherLibavcodec.framework/Info.plist"
        metadata.write_bytes(plistlib.dumps({"MinimumOSVersion": "26.0"}))
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            with self.assertRaisesRegex(ValueError, "raises deployment floor"):
                prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_simulator_app_checks_the_embedded_xctest_resource_copy(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        copied = app / "PlugIns/HostTests.xctest/AetherKernel_AetherEngine.bundle"
        shutil.copytree(app / copied.name, copied)
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            prepare.verify_app(app, "ios-simulator-x86_64", lock)
            (copied / "PrivacyInfo.xcprivacy").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "source resource"):
                prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_device_app_rejects_embedded_xctest_resource_copies(self):
        app, package, lock = self.app_fixture()
        copied = app / "PlugIns/HostTests.xctest/AetherKernel_AetherEngine.bundle"
        shutil.copytree(app / copied.name, copied)
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            with self.assertRaisesRegex(ValueError, "duplicate"):
                prepare.verify_app(app, "ios-arm64", lock)

    def test_simulator_app_requires_one_application_owned_source_bundle(self):
        app, package, lock = self.app_fixture("ios-simulator-x86_64")
        source = app / "AetherKernel_AetherEngine.bundle"
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            for relative in ("Other_AetherEngine.bundle",
                             "Frameworks/MeowAetherKernel.framework/AetherKernel_AetherEngine.bundle",
                             "PlugIns/HostTests.xctest/Other_AetherEngine.bundle"):
                copied = app / relative
                shutil.copytree(source, copied)
                with self.subTest(relative=relative), self.assertRaisesRegex(ValueError, "duplicate"):
                    prepare.verify_app(app, "ios-simulator-x86_64", lock)
                shutil.rmtree(copied)
            shutil.copytree(source, app / "PlugIns/HostTests.xctest" / source.name)
            source.rename(self.root / source.name)
            with self.assertRaisesRegex(ValueError, "missing"):
                prepare.verify_app(app, "ios-simulator-x86_64", lock)

    def test_app_checks_actual_import_library_and_packaged_code_and_notices(self):
        app, package, lock = self.app_fixture()
        symbols = " (undefined) external _avcodec_open2 (from AetherLibavcodec)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            prepare.verify_app(app, "ios-arm64", lock)
            (app / "notice.txt").write_bytes(b"missing notice")
            with self.assertRaisesRegex(ValueError, "license"):
                prepare.verify_app(app, "ios-arm64", lock)
            (app / "notice.txt").write_bytes(b"license")
            (app / "Frameworks/AetherLibavcodec.framework/AetherLibavcodec").write_bytes(image(code=b"changed"))
            with self.assertRaisesRegex(ValueError, "code differs"):
                prepare.verify_app(app, "ios-arm64", lock)
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output",
                return_value=" (undefined) external _avcodec_open2 (from libavcodec)\n"):
            with self.assertRaisesRegex(ValueError, "not bound"):
                prepare.verify_app(app, "ios-arm64", lock)

    def test_app_rejects_missing_upstream_resource_bundle(self):
        app, package, lock = self.app_fixture()
        (app / "AetherKernel_AetherEngine.bundle/PrivacyInfo.xcprivacy").unlink()
        symbols = " (undefined) external _avio_read (from AetherLibavformat)\n"
        with patch.object(prepare, "PACKAGE", package), patch.object(subprocess, "check_output", return_value=symbols):
            with self.assertRaises(OSError):
                prepare.verify_app(app, "ios-arm64", lock)


class AetherPackageBoundaryTests(unittest.TestCase):
    def fixture(self):
        lock = prepare.read_lock()
        return {
            "products": [{"name": "MeowAetherKernel", "type": {"library": ["dynamic"]}, "targets": ["MeowAetherKernel"]}],
            "dependencies": [],
            "targets": [
                {"name": "MeowAetherKernel", "type": "regular"},
                {"name": "AetherEngine", "type": "regular", "path": ".cache/AetherEngine"},
                {"name": "AetherFFmpegBuild", "type": "regular", "path": ".cache/AetherFFmpegBuild"},
                *[{"name": name, "type": "binary", "path": ".cache/binaries/" + name + ".xcframework"}
                  for name in lock["libraries"] + ["Dovi"]]
            ]
        }

    def test_one_dynamic_product_and_locked_local_sources(self):
        with patch.object(scope, "evaluate_package", return_value=self.fixture()):
            self.assertEqual(scope.aether_membership(ROOT), [])

    def test_dynamic_product_can_use_an_additional_local_source_target(self):
        fixture = self.fixture()
        fixture["targets"].append({"name": "AetherSupport", "type": "regular", "path": "Sources/AetherSupport"})
        fixture["targets"][0]["dependencies"] = [{"target": ["AetherSupport", None]}]
        with patch.object(scope, "evaluate_package", return_value=fixture):
            self.assertEqual(scope.aether_membership(ROOT), [])

    def test_static_wrapper_remote_dependencies_and_unlocked_inputs_fail(self):
        for change in ("static", "remote", "optional", "floating-path"):
            fixture = self.fixture()
            if change == "static":
                fixture["products"][0]["type"] = {"library": ["static"]}
            elif change == "remote":
                fixture["dependencies"] = [{"sourceControl": [{"identity": "floating"}]}]
            elif change == "optional":
                # This adds an unlocked binary, not another regular source target.
                fixture["targets"].append({"name": "TrueHD", "type": "binary", "path": ".cache/TrueHD.xcframework"})
            else:
                fixture["targets"][1]["path"] = "../unverified/Sources"
            with self.subTest(change=change), patch.object(scope, "evaluate_package", return_value=fixture):
                self.assertTrue(scope.aether_membership(ROOT))


if __name__ == "__main__":
    unittest.main()

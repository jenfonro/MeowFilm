import hashlib
import importlib.util
import json
import difflib
import io
from pathlib import Path
import plistlib
import re
import shutil
import tarfile
import tempfile
import unittest
from unittest.mock import patch


class PrepareRecipeTests(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[4]
        spec = importlib.util.spec_from_file_location("video_prepare", root / "scripts/prepare-ios-video-kernel.py")
        self.prepare = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.prepare)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.prepare.ROOT = self.root
        self.prepare.PACKAGE = self.root / "package"
        self.prepare.CACHE = self.root / "cache"
        self.prepare.LOCK_PATH = self.root / "lock.json"
        self.prepare.__file__ = str(self.root / "prepare.py")
        Path(self.prepare.__file__).write_text("reviewed recipe\n")
        self.lock = {"targets": {"macos-x86_64": {"sdk": "macosx", "arch": "x86_64"}},
                     "sources": {"ffmpeg": {"inventorySHA256": "source-closure"}},
                     "libraries": ["avcodec"]}
        self.prepare.LOCK_PATH.write_text(json.dumps(self.lock))
        self.target = self.recipe("macos-x86_64")
        artifact = self.prepare.PACKAGE / "Artifacts/libavcodec.xcframework/Info.plist"
        artifact.parent.mkdir(parents=True); artifact.write_text("source-built fixture")
        targets = {"macos-x86_64": self.target}
        self.receipt = {
            "schema": 1, "status": "complete", "targets": targets,
            "sourceLockSHA256": self.prepare.digest(self.prepare.LOCK_PATH),
            "identity": hashlib.sha256(json.dumps(targets, sort_keys=True).encode()).hexdigest(),
            "artifacts": {"libavcodec.xcframework/Info.plist": self.prepare.digest(artifact)}
        }

    def recipe(self, name):
        inputs = {"prepareSHA256": self.prepare.digest(Path(self.prepare.__file__)),
                  "lockSHA256": self.prepare.digest(self.prepare.LOCK_PATH),
                  "target": self.lock["targets"][name], "sources": {"ffmpeg": "source-closure"}}
        return {"inputs": inputs,
                "fingerprint": hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest(),
                "libraries": {}, "headers": {}}

    def testCurrentRecipeAndArtifactsAreAccepted(self):
        self.prepare.verify_packaged(self.receipt, self.lock)

    def testChangedPrepareSourceRejectsPreviouslyHashedOutput(self):
        Path(self.prepare.__file__).write_text("changed recipe\n")
        with self.assertRaisesRegex(ValueError, "prepare source or target recipe"):
            self.prepare.verify_packaged(self.receipt, self.lock)

    def testChangedSourceLockIsRejected(self):
        self.prepare.LOCK_PATH.write_text("changed lock")
        with self.assertRaisesRegex(ValueError, "source lock"):
            self.prepare.verify_packaged(self.receipt, self.lock)

    def testTargetRecipeMismatchAndForgedFingerprintAreRejected(self):
        self.target["inputs"]["target"] = {"sdk": "iphoneos", "arch": "arm64"}
        self.assertFalse(self.prepare.current_recipe("macos-x86_64", self.target, self.lock))
        self.target = self.recipe("macos-x86_64")
        self.target["fingerprint"] = "not-a-recipe"
        self.assertFalse(self.prepare.current_recipe("macos-x86_64", self.target, self.lock))

    def testExtraAndModifiedArtifactCannotPass(self):
        root = self.prepare.PACKAGE / "Artifacts"
        (root / "unreviewed").write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "extra"):
            self.prepare.verify_packaged(self.receipt, self.lock)
        (root / "unreviewed").unlink()
        (root / "libavcodec.xcframework/Info.plist").write_text("changed")
        with self.assertRaisesRegex(ValueError, "modified"):
            self.prepare.verify_packaged(self.receipt, self.lock)

    def testVerifyRequiresRequestedSliceWithoutBuilding(self):
        self.prepare.write_json(self.prepare.PACKAGE / "prepare-receipt.json", self.receipt)
        self.lock["targets"]["ios-arm64"] = {"sdk": "iphoneos", "arch": "arm64"}
        # The locked manifest used for this standalone CLI fixture has the same
        # current mac recipe; only the explicitly requested device is absent.
        with patch("sys.argv", ["prepare", "--verify", "--target", "ios-arm64"]), \
                patch.object(self.prepare, "prepare_sources", side_effect=AssertionError("must not build")), \
                patch.object(self.prepare, "verify_packaged"):
            self.prepare.LOCK_PATH.write_text(json.dumps(self.lock))
            with self.assertRaisesRegex(ValueError, "slice not built"):
                self.prepare.main()

    def testPackagerWillNotReuseOldPrepareSlice(self):
        path = self.prepare.CACHE / "build/macos-x86_64/receipt.json"
        self.prepare.write_json(path, self.target)
        Path(self.prepare.__file__).write_text("new recipe")
        with self.assertRaisesRegex(ValueError, "no source-built target"):
            self.prepare.package_artifacts(self.lock)

    def header_package_fixture(self):
        repository = Path(__file__).resolve().parents[4]
        locked = json.loads((repository / "scripts/ios-video-kernel.json").read_text())
        self.lock["libraries"] = locked["libraries"]
        self.lock["targets"] = locked["targets"]
        self.prepare.LOCK_PATH.write_text(json.dumps(self.lock))
        bridge = (repository / "native/VideoKernel/Sources/CMeowVideoKernel/decoder.c").read_text()
        bridge_headers = set(re.findall(r"#include <(lib[^/]+/[^>]+)>", bridge))
        paths = bridge_headers | {f"lib{library}/version.h" for library in self.lock["libraries"]}
        paths |= {"libavutil/avconfig.h", "libavcodec/nested/definitions.h"}
        expected = {}
        for name, target in self.lock["targets"].items():
            prefix = self.prepare.CACHE / "install" / name
            receipt = self.recipe(name)
            for library in self.lock["libraries"]:
                archive = prefix / "lib" / ("lib" + library + ".a")
                archive.parent.mkdir(parents=True, exist_ok=True)
                archive.write_bytes((name + "/" + library).encode())
                receipt["libraries"][library] = self.prepare.digest(archive)
            # SDK-specific generated headers must not leak into another SDK;
            # the two architectures of one SDK share the public header ABI.
            expected[target["sdk"]] = {path: (target["sdk"] + "/" + path + "\n").encode() for path in paths}
            for relative, data in expected[target["sdk"]].items():
                header = prefix / "include" / relative
                header.parent.mkdir(parents=True, exist_ok=True)
                header.write_bytes(data)
                receipt["headers"][relative] = self.prepare.digest(header)
            self.prepare.write_json(self.prepare.CACHE / "build" / name / "receipt.json", receipt)
        return expected, bridge_headers

    def artifact_tool(self, command, **kwargs):
        if command[:3] == ["xcrun", "lipo", "-create"]:
            end = command.index("-output")
            Path(command[end + 1]).write_bytes(b"".join(Path(path).read_bytes() for path in command[3:end]))
            return
        self.assertEqual(command[:2], ["xcodebuild", "-create-xcframework"])
        output = Path(command[command.index("-output") + 1])
        output.mkdir(parents=True)
        entries = []
        for index in range(2, command.index("-output"), 4):
            self.assertEqual(command[index], "-library")
            self.assertEqual(command[index + 2], "-headers")
            archive, headers = Path(command[index + 1]), Path(command[index + 3])
            sdk = archive.parent.name
            entry = {"LibraryIdentifier": sdk, "LibraryPath": archive.name, "HeadersPath": "Headers",
                     "SupportedPlatform": "macos" if sdk == "macosx" else "ios",
                     "SupportedArchitectures": sorted({target["arch"] for target in self.lock["targets"].values()
                                                       if target["sdk"] == sdk})}
            if sdk == "iphonesimulator":
                entry["SupportedPlatformVariant"] = "simulator"
            destination = output / sdk
            destination.mkdir()
            shutil.copyfile(archive, destination / archive.name)
            shutil.copytree(headers, destination / "Headers")
            entries.append(entry)
        with (output / "Info.plist").open("wb") as stream:
            plistlib.dump({"XCFrameworkFormatVersion": "1.0", "AvailableLibraries": entries}, stream)

    def testFiveLibrariesOwnDisjointCompleteHeadersForEveryLockedSlice(self):
        expected, bridge_headers = self.header_package_fixture()
        source = self.prepare.CACHE / "sources/ffmpeg/source.c"
        source.parent.mkdir(parents=True)
        source.write_bytes(b"preserve the prepared source\n")
        original = {path: path.read_bytes() for path in self.prepare.CACHE.rglob("*") if path.is_file()}
        with patch.object(self.prepare, "run", side_effect=self.artifact_tool):
            receipt = self.prepare.package_artifacts(self.lock)
        self.assertEqual(set(receipt["targets"]), set(self.lock["targets"]))
        output = self.prepare.PACKAGE / "Artifacts"
        self.assertEqual({path.name for path in output.iterdir()},
                         {"lib" + library + ".xcframework" for library in self.lock["libraries"]})
        owners = {sdk: set() for sdk in expected}
        for library in self.lock["libraries"]:
            framework = output / ("lib" + library + ".xcframework")
            with (framework / "Info.plist").open("rb") as stream:
                entries = plistlib.load(stream)["AvailableLibraries"]
            self.assertEqual({entry["LibraryIdentifier"] for entry in entries}, set(expected))
            for entry in entries:
                sdk = entry["LibraryIdentifier"]
                slice_root = framework / sdk
                archive = (slice_root / entry["LibraryPath"]).read_bytes()
                for name, target in self.lock["targets"].items():
                    if target["sdk"] == sdk:
                        self.assertIn((name + "/" + library).encode(), archive)
                headers = slice_root / entry["HeadersPath"]
                actual = {str(path.relative_to(headers)): path.read_bytes()
                          for path in headers.rglob("*") if path.is_file()}
                self.assertEqual(actual, {path: data for path, data in expected[sdk].items()
                                          if path.startswith("lib" + library + "/")})
                self.assertFalse(owners[sdk].intersection(actual), "ProcessXCFramework must have a single owner per header")
                owners[sdk].update(actual)
        for sdk in expected:
            self.assertEqual(owners[sdk], set(expected[sdk]))
            self.assertTrue(bridge_headers.issubset(owners[sdk]))
        for path, data in original.items():
            self.assertEqual(path.read_bytes(), data, "packaging must preserve source, build and install caches")
        self.prepare.verify_packaged(receipt, self.lock)
        with patch.object(self.prepare, "run", side_effect=AssertionError("unchanged package must be verified, not rebuilt")):
            self.assertEqual(self.prepare.package_artifacts(self.lock), receipt)

    def testMergedArchitecturesCannotSilentlyLoseDifferentPublicHeaders(self):
        self.header_package_fixture()
        name = "ios-simulator-arm64"
        header = self.prepare.CACHE / "install" / name / "include/libavutil/avconfig.h"
        header.write_bytes(b"architecture-specific public configuration\n")
        path = self.prepare.CACHE / "build" / name / "receipt.json"
        receipt = json.loads(path.read_text())
        receipt["headers"]["libavutil/avconfig.h"] = self.prepare.digest(header)
        self.prepare.write_json(path, receipt)
        with patch.object(self.prepare, "run", side_effect=AssertionError("incompatible headers must fail before packaging")):
            with self.assertRaisesRegex(ValueError, "incompatible public headers.*iphonesimulator"):
                self.prepare.package_artifacts(self.lock)
        self.assertEqual((self.prepare.PACKAGE / "Artifacts/libavcodec.xcframework/Info.plist").read_text(),
                         "source-built fixture")

    def testPartitionedPackagingStillRejectsModifiedInstalledHeaders(self):
        self.header_package_fixture()
        header = self.prepare.CACHE / "install/ios-arm64/include/libavformat/avformat.h"
        header.write_bytes(b"unreviewed header\n")
        with patch.object(self.prepare, "run", side_effect=AssertionError("modified install must fail before packaging")):
            with self.assertRaisesRegex(ValueError, "modified built header"):
                self.prepare.package_artifacts(self.lock)

    def testDemuxPatchSourceClosureRejectsPartialOrForgedPerFileIdentity(self):
        # A local source archive exercises the same patch/inventory contract
        # without network, compilers or cached binaries.
        originals = {"libavformat/hls.c": b"before hls\n", "libavformat/dashdec.c": b"before dash\n"}
        modified = {name: data.replace(b"before", b"after") for name, data in originals.items()}
        archive = self.root / "source.tar.gz"
        with tarfile.open(archive, "w:gz") as contents:
            for name, data in originals.items():
                member = tarfile.TarInfo("locked-source/" + name)
                member.size = len(data)
                contents.addfile(member, io.BytesIO(data))
        patch_path = self.root / "demux.patch"
        patch_path.write_text("".join("".join(difflib.unified_diff(
            data.decode().splitlines(True), modified[name].decode().splitlines(True),
            fromfile="a/" + name, tofile="b/" + name)) for name, data in originals.items()))
        hashes = {name: {"upstream": hashlib.sha256(data).hexdigest(),
                         "patched": hashlib.sha256(modified[name]).hexdigest()} for name, data in originals.items()}
        inventory = hashlib.sha256(json.dumps(
            {name: value["patched"] for name, value in hashes.items()}, sort_keys=True).encode()).hexdigest()
        lock = {"sources": {"ffmpeg": {"revision": "synthetic-source", "sha256": self.prepare.digest(archive),
                                       "inventorySHA256": inventory, "licenses": {}}},
                "patch": {"path": "demux.patch", "sha256": self.prepare.digest(patch_path), "files": hashes}}
        with patch.object(self.prepare, "archive_for", return_value=archive):
            self.assertEqual(self.prepare.prepare_sources(lock), {"ffmpeg": inventory})
            self.assertEqual(self.prepare.prepare_sources(lock), {"ffmpeg": inventory})
            cached = self.prepare.CACHE / "sources/ffmpeg/libavformat/hls.c"
            cached.write_bytes(originals["libavformat/hls.c"])
            with self.assertRaisesRegex(ValueError, "partial patched source cache"):
                self.prepare.prepare_sources(lock)
            cached.write_bytes(modified["libavformat/hls.c"])
            hashes["libavformat/hls.c"]["patched"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "modified source cache"):
                self.prepare.prepare_sources(lock)


if __name__ == "__main__":
    unittest.main()

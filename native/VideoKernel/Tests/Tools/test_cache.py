"""Behavioral cache regressions using local sources and synthetic build tools.

The public prepare functions own identity, validation, locking and publication.
External tools and I/O faults are synthetic; contention uses real child
processes and real flock calls. Run with the Tools unittest discovery entry.
"""
import copy
from contextlib import ExitStack
import difflib
import fcntl
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch
import urllib.request


REPOSITORY = Path(__file__).resolve().parents[4]
PREPARE_SCRIPT = REPOSITORY / "scripts/prepare-ios-video-kernel.py"
TARGET = "macos-arm64"
PROCESS_TIMEOUT = 15


def checksum(data):
    return hashlib.sha256(data).hexdigest()


def load_prepare(root, shared):
    spec = importlib.util.spec_from_file_location("video_cache_prepare", PREPARE_SCRIPT)
    prepare = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prepare)
    prepare.ROOT = root
    prepare.CACHE = root / ".cache/ios-video-kernel"
    prepare.PACKAGE = root / "native/VideoKernel/.cache"
    prepare.LOCK_PATH = root / "scripts/ios-video-kernel.json"
    prepare.SHARED_CACHE = shared
    prepare.__file__ = str(root / "scripts/prepare-ios-video-kernel.py")
    return prepare


def write_file(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def record_event(path, phase, root):
    # One append per event keeps records intact across the real subprocesses.
    data = (json.dumps({"phase": phase, "root": str(root)}) + "\n").encode()
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    try:
        os.write(descriptor, data)
    finally:
        os.close(descriptor)


def wait_at_gate(gate, phase):
    if not gate or gate["phase"] != phase:
        return
    Path(gate["entered"]).touch()
    deadline = time.monotonic() + PROCESS_TIMEOUT
    while not Path(gate["release"]).exists():
        if time.monotonic() >= deadline:
            raise TimeoutError("test gate was not released: " + phase)
        time.sleep(0.01)


def installed_files(libraries, arch):
    files = {}
    for library in libraries:
        # A well-formed ar container; the synthetic lipo supplies architecture
        # information without requiring an Apple object file or compiler.
        payload = (library + " " + arch + "\n").encode()
        member = f"{'fixture.o/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(payload):<10}`\n"
        files["lib/lib" + library + ".a"] = (
            b"!<arch>\n" + member.encode() + payload + (b"\n" if len(payload) % 2 else b""))
        files["include/lib" + library + "/" + library + ".h"] = (
            "#pragma once\nint fixture_" + library + "(void);\n").encode()
    files["include/libavcodec/nested/detail.h"] = b"#pragma once\n#define FIXTURE_DETAIL 1\n"
    return files


class SyntheticTools:
    def __init__(self, prepare, lock, options, events, gate=None):
        self.prepare = prepare
        self.lock = lock
        self.options = options
        self.events = events
        self.gate = gate

    def __call__(self, command, cwd=None, env=None, log=None):
        command = list(map(str, command))
        if command[:2] == ["xcrun", "--sdk"]:
            sdk = str(Path(self.options["sdk_root"]) / command[2])
            if command[3:] == ["--show-sdk-path"]:
                return sdk
            if command[3:] == ["--show-sdk-version"]:
                return self.options["sdk_version"]
            if command[3:] == ["--find", "clang"]:
                return self.options["compiler"]
        if command == [self.options["compiler"], "--version"]:
            return self.options["compiler_version"]
        if command[:3] == ["xcrun", "lipo", "-archs"]:
            if not Path(command[3]).is_file():
                raise FileNotFoundError(command[3])
            configuration = json.loads((Path(cwd) if cwd else self.last_build).joinpath(
                "fixture-make.json").read_text())
            return self.options.get("reported_arch", configuration["arch"])
        if Path(command[0]).name == "configure":
            phase = "configure"
        elif command[0] == "make":
            phase = "install" if "install" in command[1:] else "make"
        else:
            raise AssertionError("unexpected external command: " + repr(command))

        record_event(self.events, phase, self.prepare.ROOT)
        wait_at_gate(self.gate, phase)
        build = Path(cwd)
        self.last_build = build
        if phase == "configure":
            if not Path(command[0]).is_file():
                raise AssertionError("configure must use this workspace's prepared source")
            configuration = {
                "prefix": next(value.split("=", 1)[1] for value in command if value.startswith("--prefix=")),
                "arch": next(value.split("=", 1)[1] for value in command if value.startswith("--arch="))
            }
            write_file(build / "fixture-make.json", json.dumps(configuration).encode())
            write_file(build / "config.h", b"#define FIXTURE_CONFIGURED 1\n")
            components = [name for name in self.lock["requiredComponents"]
                          if name != self.options.get("missing_component")]
            write_file(build / "config_components.h", "".join(
                "#define CONFIG_" + name + " 1\n" for name in components).encode())
        else:
            configuration = json.loads((build / "fixture-make.json").read_text())
            if phase == "install":
                prefix = Path(configuration["prefix"])
                files = installed_files(self.lock["libraries"], configuration["arch"])
                for relative, data in files.items():
                    if relative == self.options.get("omit_file"):
                        continue
                    if self.options.get("empty_headers") and relative.startswith("include/"):
                        continue
                    write_file(prefix / relative, data)
                    if self.options.get("fail") == "install":
                        break
                # Real FFmpeg also installs private pkg-config metadata. Only
                # the public libraries/headers belong in a shared artifact.
                write_file(prefix / "lib/pkgconfig/fixture.pc",
                           ("prefix=" + str(prefix) + "\n").encode())
        if self.options.get("fail") == phase:
            raise subprocess.CalledProcessError(1, command, stderr="synthetic " + phase + " failure")
        return ""


class DownloadStream(io.BytesIO):
    def __init__(self, data, gate=None, interrupted=False):
        super().__init__(data)
        self.gate = gate
        self.interrupted = interrupted
        self.started = False

    def read(self, size=-1):
        if not self.started:
            self.started = True
            wait_at_gate(self.gate, "download")
            if self.interrupted:
                return super().read(7)
        elif self.interrupted:
            raise OSError("synthetic interrupted download")
        return super().read(size)


def cache_worker(configuration_path):
    configuration = json.loads(Path(configuration_path).read_text())
    prepare = load_prepare(Path(configuration["root"]), Path(configuration["shared"]))
    lock = json.loads(prepare.LOCK_PATH.read_text())
    events = Path(configuration["events"])
    gate = configuration.get("gate")
    real_flock = fcntl.flock

    def observed_flock(descriptor, operation):
        # Observe contention at the OS boundary. The nonblocking probe really
        # fails against the holder, then the original blocking flock waits.
        if operation & fcntl.LOCK_EX and not operation & fcntl.LOCK_NB:
            try:
                return real_flock(descriptor, operation | fcntl.LOCK_NB)
            except BlockingIOError:
                Path(configuration["blocked"]).touch()
        return real_flock(descriptor, operation)

    def open_download(request, timeout):
        if request.full_url != lock["sources"]["ffmpeg"]["url"]:
            raise AssertionError("unexpected download URL")
        record_event(events, "download", prepare.ROOT)
        return DownloadStream(Path(configuration["archive"]).read_bytes(), gate)

    with ExitStack() as stack:
        if configuration.get("blocked"):
            stack.enter_context(patch.object(fcntl, "flock", side_effect=observed_flock))
        if configuration["operation"] == "build":
            tools = SyntheticTools(prepare, lock, configuration["tools"], events, gate)
            stack.enter_context(patch.object(prepare, "run", side_effect=tools))
            result = prepare.build_target(
                TARGET, lock["targets"][TARGET], lock,
                {name: source["inventorySHA256"] for name, source in lock["sources"].items()})
        else:
            stack.enter_context(patch.object(prepare.urllib.request, "urlopen", side_effect=open_download))
            result = str(prepare.archive_for("ffmpeg", lock["sources"]["ffmpeg"]))
    (prepare.ROOT / "worker-result.json").write_text(json.dumps(result))


class SharedCacheTests(unittest.TestCase):
    def setUp(self):
        temporary_root = Path.home() / "Developer/temp"
        temporary_root.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="ffmpeg-cache-test-", dir=temporary_root)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.shared = self.root / "shared"
        self.events_path = self.root / "events.jsonl"
        self.script = PREPARE_SCRIPT.read_bytes()
        self.originals = {
            "configure": b"#!/bin/sh\nexit 0\n",
            "COPYING.TEST": b"Synthetic source fixture; no third-party code.\n",
            "libavcodec/source.c": b"int fixture_source(void) { return 1; }\n"
        }
        self.patched = dict(self.originals)
        self.patched["libavcodec/source.c"] = b"int fixture_source(void) { return 2; }\n"
        self.patch_bytes = "".join(difflib.unified_diff(
            self.originals["libavcodec/source.c"].decode().splitlines(True),
            self.patched["libavcodec/source.c"].decode().splitlines(True),
            fromfile="a/libavcodec/source.c", tofile="b/libavcodec/source.c")).encode()
        self.archive = self.root / "source.tar.gz"
        self.write_archive(self.archive, self.originals)
        inventory = checksum(json.dumps(
            {name: checksum(data) for name, data in self.patched.items()}, sort_keys=True).encode())
        self.lock = {
            "schema": 1,
            "libraries": ["avcodec", "avutil"],
            "sources": {"ffmpeg": {
                "revision": "synthetic-source", "url": self.archive.as_uri(),
                "sha256": checksum(self.archive.read_bytes()), "inventorySHA256": inventory,
                "licenses": {"COPYING.TEST": checksum(self.originals["COPYING.TEST"])}
            }},
            "patch": {
                "path": "patches/fixture.patch", "sha256": checksum(self.patch_bytes),
                "files": {"libavcodec/source.c": {
                    "upstream": checksum(self.originals["libavcodec/source.c"]),
                    "patched": checksum(self.patched["libavcodec/source.c"])
                }}
            },
            "targets": {
                TARGET: {"sdk": "macosx", "arch": "arm64", "triple": "arm64-apple-macos14.0", "minimum": "14.0"},
                "macos-x86_64": {"sdk": "macosx", "arch": "x86_64",
                                 "triple": "x86_64-apple-macos14.0", "minimum": "14.0"},
                "ios-arm64": {"sdk": "iphoneos", "arch": "arm64",
                              "triple": "arm64-apple-ios17.0", "minimum": "17.0"},
                "ios-simulator-arm64": {"sdk": "iphonesimulator", "arch": "arm64",
                                        "triple": "arm64-apple-ios17.0-simulator", "minimum": "17.0"}
            },
            "configure": ["--disable-shared", "--enable-static", "--disable-doc"],
            "requiredComponents": ["HLS_DEMUXER", "AAC_DECODER"]
        }
        self.sources = {"ffmpeg": inventory}
        self.tools = {
            "sdk_root": str(self.root / "SDKs"), "sdk_version": "26.1",
            "compiler": str(self.root / "toolchain/bin/clang"),
            "compiler_version": "Apple clang version 17.0.0 (synthetic)"
        }
        self.create_sdks(Path(self.tools["sdk_root"]))
        write_file(Path(self.tools["compiler"]), b"synthetic clang identity\n")

    def write_archive(self, path, files):
        with tarfile.open(path, "w:gz") as archive:
            for name, data in files.items():
                member = tarfile.TarInfo("locked-source/" + name)
                member.size = len(data)
                member.mode = 0o755 if name == "configure" else 0o644
                archive.addfile(member, io.BytesIO(data))

    def create_sdks(self, root):
        for sdk in {target["sdk"] for target in self.lock["targets"].values()}:
            write_file(root / sdk / "usr/include/libxml2/libxml/xmlversion.h",
                       b'#define LIBXML_DOTTED_VERSION "2.9.13"\n')

    def workspace(self, name, lock=None, prepared=True, shared=None):
        root = self.root / name
        prepare = load_prepare(root, shared if shared is not None else self.shared)
        write_file(Path(prepare.__file__), self.script)
        write_file(prepare.LOCK_PATH, json.dumps(lock if lock is not None else self.lock,
                                               sort_keys=True).encode())
        write_file(root / self.lock["patch"]["path"], self.patch_bytes)
        if prepared:
            for relative, data in self.patched.items():
                write_file(prepare.CACHE / "sources/ffmpeg" / relative, data)
        return prepare

    def events(self):
        if not self.events_path.exists():
            return []
        return [json.loads(line)["phase"] for line in self.events_path.read_text().splitlines()]

    def build(self, prepare, name=TARGET, target=None, lock=None, sources=None, options=None):
        lock = self.lock if lock is None else lock
        target = lock["targets"][name] if target is None else target
        tools = SyntheticTools(prepare, lock, dict(self.tools, **(options or {})), self.events_path)
        with patch.object(prepare, "run", side_effect=tools):
            return prepare.build_target(name, target, lock, self.sources if sources is None else sources)

    def assertInstalled(self, prepare, receipt, name=TARGET, arch="arm64"):
        expected = installed_files(self.lock["libraries"], arch)
        prefix = prepare.CACHE / "install" / name
        for relative, data in expected.items():
            self.assertEqual((prefix / relative).read_bytes(), data, relative)
            self.assertFalse((prefix / relative).is_symlink(), relative)
        self.assertEqual(receipt["libraries"], {
            library: checksum(expected["lib/lib" + library + ".a"]) for library in self.lock["libraries"]})
        self.assertEqual(receipt["headers"], {
            relative.removeprefix("include/"): checksum(data)
            for relative, data in expected.items() if relative.startswith("include/")})
        self.assertEqual(json.loads((prepare.CACHE / "build" / name / "receipt.json").read_text()), receipt)
        shared = prepare.SHARED_CACHE / "artifacts" / receipt["fingerprint"]
        self.assertEqual(json.loads((shared / "receipt.json").read_text()), receipt)
        actual = {path.relative_to(shared / "install").as_posix(): path.read_bytes()
                  for path in (shared / "install").rglob("*") if path.is_file()}
        self.assertEqual(actual, expected)

    def assertNotPublished(self, prepare):
        artifacts = prepare.SHARED_CACHE / "artifacts"
        self.assertEqual(list(artifacts.glob("*/receipt.json")), [])
        self.assertEqual(list(artifacts.glob("*/install")), [])
        self.assertFalse((prepare.CACHE / "build" / TARGET / "receipt.json").exists())

    def assertRebuild(self, prepare, previous, **arguments):
        before = len(self.events())
        receipt = self.build(prepare, **arguments)
        self.assertNotEqual(receipt["fingerprint"], previous["fingerprint"])
        self.assertEqual(self.events()[before:], ["configure", "make", "install"])
        target = arguments.get("target", self.lock["targets"][arguments.get("name", TARGET)])
        self.assertInstalled(prepare, receipt, arguments.get("name", TARGET), target["arch"])
        self.assertEqual(self.build(prepare, **arguments), receipt)
        self.assertEqual(self.events()[before:], ["configure", "make", "install"])
        return receipt

    def testDifferentRootsBuildOnceAndOwnIndependentPrivateFiles(self):
        first = self.workspace("first")
        second = self.workspace("second")
        receipt = self.build(first)
        self.assertEqual(self.events(), ["configure", "make", "install"])
        self.assertEqual(self.build(second), receipt)
        self.assertEqual(self.events(), ["configure", "make", "install"])
        for prepare in (first, second):
            self.assertInstalled(prepare, receipt)
            self.assertNotIn(str(prepare.ROOT), json.dumps(receipt["inputs"]))
        shared = self.shared / "artifacts" / receipt["fingerprint"] / "install"
        expected = installed_files(self.lock["libraries"], "arm64")
        for relative in ("lib/libavcodec.a", "include/libavcodec/avcodec.h"):
            private = first.CACHE / "install" / TARGET / relative
            other = second.CACHE / "install" / TARGET / relative
            self.assertFalse(private.samefile(other))
            self.assertFalse(private.samefile(shared / relative))
            private.write_bytes(b"local edits must remain private\n")
            self.assertEqual(other.read_bytes(), expected[relative])
            self.assertEqual((shared / relative).read_bytes(), expected[relative])
        write_file(first.CACHE / "install" / TARGET / "include/local-only.h", b"private\n")
        self.assertFalse((second.CACHE / "install" / TARGET / "include/local-only.h").exists())
        self.assertFalse((shared / "include/local-only.h").exists())
        self.assertEqual(self.build(first), receipt)
        self.assertInstalled(first, receipt)
        self.assertFalse((first.CACHE / "install" / TARGET / "include/local-only.h").exists())
        self.assertEqual(self.events(), ["configure", "make", "install"])

    def testSourceAndPatchLockChangesDoNotReuseCompletedArtifacts(self):
        original = self.build(self.workspace("original"))
        for section, field in (("source", "revision"), ("source", "sha256"), ("patch", "sha256")):
            with self.subTest(section=section, field=field):
                lock = copy.deepcopy(self.lock)
                entry = lock["sources"]["ffmpeg"] if section == "source" else lock["patch"]
                entry[field] = checksum(("changed " + section + field).encode())
                prepare = self.workspace(section + "-" + field, lock=lock)
                self.assertRebuild(prepare, original, lock=lock)

    def testSourcesTargetsAndConfigureAreIdentityInputsIndependentlyOfLockBytes(self):
        original = self.build(self.workspace("original"))
        # Keep the on-disk lock fixed here: otherwise its hash could conceal
        # omission of an explicit build_target input from the fingerprint.
        changed_configure = copy.deepcopy(self.lock)
        changed_configure["configure"].append("--disable-asm")
        changed_deployment = dict(self.lock["targets"][TARGET],
                                  triple="arm64-apple-macos15.0", minimum="15.0")
        cases = {
            "sources": {"sources": {"ffmpeg": checksum(b"changed prepared closure")}},
            "architecture": {"name": "macos-x86_64"},
            "device": {"name": "ios-arm64"},
            "simulator": {"name": "ios-simulator-arm64"},
            "deployment": {"target": changed_deployment},
            "configure": {"lock": changed_configure}
        }
        for case, arguments in cases.items():
            with self.subTest(input=case):
                self.assertRebuild(self.workspace(case), original, **arguments)

    def testSdkAndCompilerIdentityChangesDoNotReuseCompletedArtifacts(self):
        original = self.build(self.workspace("original"))
        alternate_sdk = self.root / "alternate-SDKs"
        self.create_sdks(alternate_sdk)
        alternate_compiler = self.root / "alternate-toolchain/bin/clang"
        write_file(alternate_compiler, b"synthetic clang identity\n")
        cases = {
            "sdk-version": {"sdk_version": "26.2"},
            "sdk-path": {"sdk_root": str(alternate_sdk)},
            "compiler-version": {"compiler_version": "Apple clang version 18.0.0 (synthetic)"},
            "compiler-path": {"compiler": str(alternate_compiler)}
        }
        for case, options in cases.items():
            with self.subTest(input=case):
                self.assertRebuild(self.workspace(case), original, options=options)

    def testSdkHeaderContentChangesInvalidateUnchangedSdkVersion(self):
        original = self.build(self.workspace("original"))
        header = Path(self.tools["sdk_root"]) / "macosx/usr/include/libxml2/libxml/xmlversion.h"
        header.write_bytes(header.read_bytes() + b"#define FIXTURE_SDK_PATCH 1\n")
        self.assertRebuild(self.workspace("changed-header"), original)

    def testCflagsChangeRebuildsAndRestoringFlagsReusesOriginalArtifact(self):
        prepare = self.workspace("compiler-flags")
        with patch.dict(os.environ, {"CFLAGS": "-O1"}):
            original = self.build(prepare)
            with patch.dict(os.environ, {"CFLAGS": "-O2"}):
                self.assertRebuild(prepare, original)
            before = self.events()
            self.assertEqual(self.build(prepare), original)
            self.assertEqual(self.events(), before)
            self.assertInstalled(prepare, original)

    def testPrepareScriptHashChangesInvalidateCompletedArtifacts(self):
        original = self.build(self.workspace("original"))
        prepare = self.workspace("changed-script")
        Path(prepare.__file__).write_bytes(self.script + b"\n# changed build recipe\n")
        self.assertRebuild(prepare, original)

    def testFailedBuildCanRetryWithoutPublishingPartialOutput(self):
        for phase in ("configure", "make", "install"):
            with self.subTest(phase=phase):
                shared = self.root / ("shared-" + phase)
                prepare = self.workspace("failed-" + phase, shared=shared)
                with self.assertRaises(subprocess.CalledProcessError):
                    self.build(prepare, options={"fail": phase})
                self.assertNotPublished(prepare)
                before = len(self.events())
                receipt = self.build(prepare)
                self.assertEqual(self.events()[before:].count("make"), 1)
                self.assertEqual(self.events()[before:].count("install"), 1)
                self.assertInstalled(prepare, receipt)
                before = len(self.events())
                other = self.workspace("retry-consumer-" + phase, shared=shared)
                self.assertEqual(self.build(other), receipt)
                self.assertInstalled(other, receipt)
                self.assertEqual(len(self.events()), before)

    def testIncompleteOrInvalidBuildCannotBePublished(self):
        cases = {
            "architecture": ({"reported_arch": "x86_64"}, ValueError),
            "missing-library": ({"omit_file": "lib/libavutil.a"}, FileNotFoundError),
            "empty-headers": ({"empty_headers": True}, ValueError),
            "missing-component": ({"missing_component": "HLS_DEMUXER"}, ValueError)
        }
        for case, (options, error) in cases.items():
            with self.subTest(case=case):
                prepare = self.workspace(case, shared=self.root / ("shared-" + case))
                with self.assertRaises(error):
                    self.build(prepare, options=options)
                self.assertNotPublished(prepare)
                self.assertInstalled(prepare, self.build(prepare))

    def testPublicationCopyMustPassHashVerification(self):
        prepare = self.workspace("publisher")
        original_copy = prepare.shutil.copyfile
        corrupted = []

        def corrupt_copy(source, destination, *args, **kwargs):
            result = original_copy(source, destination, *args, **kwargs)
            destination = Path(destination)
            if destination.name == "libavcodec.a" and destination.is_relative_to(self.shared / "artifacts"):
                destination.write_bytes(b"corrupted during publication\n")
                corrupted.append(destination)
            return result

        with patch.object(prepare.shutil, "copyfile", side_effect=corrupt_copy):
            with self.assertRaises(ValueError):
                self.build(prepare)
        self.assertTrue(corrupted, "the publication copy must actually be exercised")
        self.assertNotPublished(prepare)
        self.assertInstalled(prepare, self.build(prepare))

    def testPublishedMissingModifiedAndExtraFilesAreRejected(self):
        publisher = self.workspace("publisher")
        receipt = self.build(publisher)
        artifact = self.shared / "artifacts" / receipt["fingerprint"]
        cases = [
            ("modified-library", "install/lib/libavcodec.a", "modify", ValueError),
            ("missing-library", "install/lib/libavutil.a", "remove", ValueError),
            ("modified-header", "install/include/libavcodec/nested/detail.h", "modify", ValueError),
            ("missing-header", "install/include/libavcodec/avcodec.h", "remove", ValueError),
            ("extra-header", "install/include/libavcodec/unreviewed.h", "extra", ValueError),
            ("extra-library", "install/lib/libunreviewed.a", "extra", ValueError),
            ("extra-file", "install/unreviewed.txt", "extra", ValueError),
            ("missing-receipt", "receipt.json", "remove", FileNotFoundError),
            ("modified-receipt", "receipt.json", "modify", ValueError)
        ]
        before = self.events()
        for case, relative, mutation, error in cases:
            with self.subTest(case=case):
                path = artifact / relative
                original = path.read_bytes() if path.exists() else None
                try:
                    if mutation == "modify":
                        path.write_bytes(bytes([original[0] ^ 1]) + original[1:])
                    elif mutation == "remove":
                        path.unlink()
                    else:
                        write_file(path, b"unreviewed addition\n")
                    consumer = self.workspace(case)
                    with self.assertRaises(error):
                        self.build(consumer)
                    self.assertEqual(self.events(), before, "invalid published data must not trigger a rebuild")
                    self.assertFalse((consumer.CACHE / "build" / TARGET / "receipt.json").exists())
                    self.assertFalse((consumer.CACHE / "install" / TARGET).exists())
                finally:
                    if original is None:
                        path.unlink(missing_ok=True)
                    else:
                        write_file(path, original)
        consumer = self.workspace("restored-consumer")
        self.assertEqual(self.build(consumer), receipt)
        self.assertInstalled(consumer, receipt)
        self.assertEqual(self.events(), before)

    def testPublishedReceiptCannotOmitRequiredLibrariesOrAllHeaders(self):
        receipt = self.build(self.workspace("publisher"))
        path = self.shared / "artifacts" / receipt["fingerprint"] / "receipt.json"
        original = path.read_bytes()
        before = self.events()
        for field in ("libraries", "headers"):
            with self.subTest(field=field):
                changed = copy.deepcopy(receipt)
                changed[field] = {}
                path.write_text(json.dumps(changed))
                try:
                    with self.assertRaises(ValueError):
                        self.build(self.workspace("missing-" + field))
                    self.assertEqual(self.events(), before)
                finally:
                    path.write_bytes(original)

    def testArchiveIdentityUsesContentHashAcrossRootsAndSourceNames(self):
        first = self.workspace("first")
        second = self.workspace("second")
        source = self.lock["sources"]["ffmpeg"]
        with patch.object(urllib.request, "urlopen", wraps=urllib.request.urlopen) as download:
            cached = first.archive_for("ffmpeg", source)
            self.assertEqual(second.archive_for("another-source-name", source), cached)
            self.assertEqual(download.call_count, 1)
        self.assertEqual(cached, self.shared / "downloads" / (source["sha256"] + ".tar.gz"))
        self.assertEqual(cached.read_bytes(), self.archive.read_bytes())
        other_archive = self.root / "other-source.tar.gz"
        self.write_archive(other_archive, dict(self.originals, EXTRA=b"different locked source\n"))
        other_source = dict(source, sha256=checksum(other_archive.read_bytes()), url=other_archive.as_uri())
        other_cached = second.archive_for("ffmpeg", other_source)
        self.assertEqual(other_cached, self.shared / "downloads" / (other_source["sha256"] + ".tar.gz"))
        self.assertNotEqual(cached, other_cached)
        self.assertEqual(cached.read_bytes(), self.archive.read_bytes())
        self.assertEqual(other_cached.read_bytes(), other_archive.read_bytes())

    def testPublishedArchiveIsRehashedBeforeReuse(self):
        first = self.workspace("first")
        source = self.lock["sources"]["ffmpeg"]
        cached = first.archive_for("ffmpeg", source)
        data = cached.read_bytes()
        cached.write_bytes(bytes([data[0] ^ 1]) + data[1:])
        second = self.workspace("second")
        with patch.object(urllib.request, "urlopen", side_effect=AssertionError("must reject damaged cache")):
            with self.assertRaises(ValueError):
                second.archive_for("ffmpeg", source)

    def testFailedOrMismatchedDownloadCanRetryWithoutPublishing(self):
        source = self.lock["sources"]["ffmpeg"]
        for failure in ("open", "interrupted", "checksum"):
            with self.subTest(failure=failure):
                shared = self.root / ("shared-" + failure)
                prepare = self.workspace(failure, shared=shared)
                destination = shared / "downloads" / (source["sha256"] + ".tar.gz")
                if failure == "open":
                    response = patch.object(urllib.request, "urlopen", side_effect=OSError("synthetic offline"))
                    error = OSError
                else:
                    data = self.archive.read_bytes() if failure == "interrupted" else b"wrong archive bytes"
                    response = patch.object(urllib.request, "urlopen", return_value=DownloadStream(
                        data, interrupted=failure == "interrupted"))
                    error = OSError if failure == "interrupted" else ValueError
                with response:
                    with self.assertRaises(error):
                        prepare.archive_for("ffmpeg", source)
                self.assertFalse(destination.exists())
                self.assertEqual(list((shared / "downloads").glob("*.tar.gz")), [])
                with patch.object(urllib.request, "urlopen", wraps=urllib.request.urlopen) as download:
                    self.assertEqual(prepare.archive_for("ffmpeg", source), destination)
                    self.assertEqual(download.call_count, 1)
                self.assertEqual(destination.read_bytes(), self.archive.read_bytes())
                consumer = self.workspace("consumer-" + failure, shared=shared)
                with patch.object(urllib.request, "urlopen", side_effect=AssertionError("retry must be reusable")):
                    self.assertEqual(consumer.archive_for("ffmpeg", source), destination)

    def testPreparedSourcesAndPatchRemainPrivateWhileArchiveIsShared(self):
        first = self.workspace("first", prepared=False)
        second = self.workspace("second", prepared=False)
        with patch.object(urllib.request, "urlopen", wraps=urllib.request.urlopen) as download:
            self.assertEqual(first.prepare_sources(self.lock), self.sources)
            self.assertEqual(second.prepare_sources(self.lock), self.sources)
            self.assertEqual(download.call_count, 1)
        for relative, data in self.patched.items():
            source = first.CACHE / "sources/ffmpeg" / relative
            other = second.CACHE / "sources/ffmpeg" / relative
            self.assertEqual(source.read_bytes(), data)
            self.assertEqual(other.read_bytes(), data)
            self.assertFalse(source.samefile(other))
            source.write_bytes(b"local source edits\n")
            self.assertEqual(other.read_bytes(), data)
        first_patch = first.ROOT / self.lock["patch"]["path"]
        second_patch = second.ROOT / self.lock["patch"]["path"]
        first_patch.write_bytes(b"local patch edits\n")
        self.assertEqual(second_patch.read_bytes(), self.patch_bytes)
        cached = self.shared / "downloads" / (self.lock["sources"]["ffmpeg"]["sha256"] + ".tar.gz")
        self.assertEqual(cached.read_bytes(), self.archive.read_bytes())

    def start_worker(self, prepare, operation, gate=None, blocked=None):
        configuration = {
            "root": str(prepare.ROOT), "shared": str(prepare.SHARED_CACHE),
            "operation": operation, "tools": self.tools, "events": str(self.events_path),
            "archive": str(self.archive), "gate": gate, "blocked": str(blocked) if blocked else None
        }
        path = prepare.ROOT / "worker-input.json"
        path.write_text(json.dumps(configuration))
        process = subprocess.Popen(
            [sys.executable, "-I", "-B", str(Path(__file__).resolve()), "--cache-worker", str(path)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop_worker, process)
        return process

    def stop_worker(self, process):
        if process.poll() is None:
            process.kill()
        process.communicate(timeout=PROCESS_TIMEOUT)

    def wait_for_marker(self, path, process):
        deadline = time.monotonic() + PROCESS_TIMEOUT
        while not path.exists():
            if process.poll() is not None:
                output, error = process.communicate()
                self.fail("worker exited before " + path.name + "\n" + output + error)
            if time.monotonic() >= deadline:
                self.fail("worker did not reach " + path.name)
            time.sleep(0.01)

    def finish_worker(self, process, prepare):
        output, error = process.communicate(timeout=PROCESS_TIMEOUT)
        self.assertEqual(process.returncode, 0, output + error)
        return json.loads((prepare.ROOT / "worker-result.json").read_text())

    def assertConcurrentReuse(self, operation, terminate_holder=False):
        first = self.workspace("holder")
        second = self.workspace("waiter")
        entered, release, blocked = (self.root / name for name in ("entered", "release", "blocked"))
        gate = {"phase": "make" if operation == "build" else "download",
                "entered": str(entered), "release": str(release)}
        holder = self.start_worker(first, operation, gate=gate)
        self.wait_for_marker(entered, holder)
        if operation == "build":
            self.assertNotPublished(first)
        else:
            self.assertFalse((self.shared / "downloads" /
                              (self.lock["sources"]["ffmpeg"]["sha256"] + ".tar.gz")).exists())
        waiter = self.start_worker(second, operation, blocked=blocked)
        self.wait_for_marker(blocked, waiter)
        self.assertIsNone(waiter.poll())
        self.assertFalse((second.ROOT / "worker-result.json").exists())
        self.assertFalse((second.CACHE / "build" / TARGET / "receipt.json").exists())
        if terminate_holder:
            holder.kill()
            holder.communicate(timeout=PROCESS_TIMEOUT)
            self.assertLess(holder.returncode, 0)
            self.assertFalse((first.ROOT / "worker-result.json").exists())
        else:
            release.touch()
        result = self.finish_worker(waiter, second)
        if not terminate_holder:
            self.assertEqual(self.finish_worker(holder, first), result)
        if operation == "build":
            self.assertEqual(self.events().count("configure"), 2 if terminate_holder else 1)
            self.assertEqual(self.events().count("make"), 2 if terminate_holder else 1)
            self.assertEqual(self.events().count("install"), 1)
            self.assertInstalled(second, result)
            if not terminate_holder:
                self.assertInstalled(first, result)
        else:
            self.assertEqual(self.events().count("download"), 2 if terminate_holder else 1)
            expected = self.shared / "downloads" / (self.lock["sources"]["ffmpeg"]["sha256"] + ".tar.gz")
            self.assertEqual(Path(result), expected)
            self.assertEqual(expected.read_bytes(), self.archive.read_bytes())

    def testConcurrentBuildWaitsThenReusesTheSingleCompletedBuild(self):
        self.assertConcurrentReuse("build")

    def testConcurrentDownloadWaitsThenReusesTheSingleVerifiedArchive(self):
        self.assertConcurrentReuse("download")

    def testTerminatedBuilderReleasesLockAndWaiterBuildsWithoutPartialPublication(self):
        self.assertConcurrentReuse("build", terminate_holder=True)

    def testTerminatedDownloaderReleasesLockAndWaiterRetriesPartialDownload(self):
        self.assertConcurrentReuse("download", terminate_holder=True)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--cache-worker":
        cache_worker(sys.argv[2])
    else:
        unittest.main()

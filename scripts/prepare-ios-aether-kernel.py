#!/usr/bin/env python3
"""Prepare the pinned Aether source and binary closure; never resolve floating packages."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
LOCK_PATH = ROOT / "scripts/ios-aether-kernel.json"
PACKAGE = ROOT / "native/AetherKernel"
SHARED = Path.home() / "Developer/cache/aether-engine"
CPU = {0x1000007: "x86_64", 0x100000C: "arm64"}
SLICES = {
    "ios-arm64": (2, {"arm64"}, "16.0"),
    "ios-arm64_x86_64-simulator": (7, {"arm64", "x86_64"}, "16.0"),
    "macos-arm64_x86_64": (1, {"arm64", "x86_64"}, "14.0")
}


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def identity(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


@contextmanager
def guard(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a") as stream:
        fcntl.flock(stream, fcntl.LOCK_EX)
        yield


def read_lock():
    lock = json.loads(LOCK_PATH.read_text())
    if lock["schema"] != 1:
        raise ValueError("unsupported Aether lock schema")
    for entry in lock["patches"]:
        if digest(ROOT / entry["path"]) != entry["sha256"]:
            raise ValueError("Aether patch hash mismatch")
    for filename, checksum in lock["licenses"].items():
        if digest(ROOT / "LICENSES" / filename) != checksum:
            raise ValueError("Aether license hash mismatch: " + filename)
    return lock


def archive_for(source):
    path = SHARED / "downloads" / (source["sha256"] + ".tar.gz")
    with guard(SHARED / "locks" / (source["sha256"] + ".lock")):
        if not path.exists():
            path.parent.mkdir(parents=True, exist_ok=True)
            partial = path.with_suffix(".partial")
            proxy = "http://127.0.0.1:7890" if sys.platform == "win32" else "http://10.0.0.1:7890"
            try:
                for index, proxies in enumerate(({"http": proxy, "https": proxy}, {})):
                    try:
                        request = urllib.request.Request(source["url"], headers={"User-Agent": "Meowfilm-Aether-prepare"})
                        opener = urllib.request.build_opener(urllib.request.ProxyHandler(proxies))
                        with opener.open(request, timeout=10 if index == 0 else 120) as response, partial.open("wb") as stream:
                            for block in iter(lambda: response.read(1024 * 1024), b""):
                                stream.write(block)
                        break
                    except OSError:
                        if index:
                            raise
                if digest(partial) != source["sha256"]:
                    raise ValueError("Aether archive hash mismatch")
                partial.replace(path)
            finally:
                partial.unlink(missing_ok=True)
        if path.is_symlink() or digest(path) != source["sha256"]:
            raise ValueError("Aether archive hash mismatch")
    return path


def safe_path(value):
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts:
        raise ValueError("unsafe Aether archive path")
    return path


def selected_path(name, relative, libraries):
    parts = relative.parts
    if name == "AetherEngine" and parts[:2] == ("Sources", "AetherEngine"):
        return PurePosixPath("AetherEngine", *parts[2:])
    if name == "FFmpegBuild" and parts[:2] == ("Sources", "AetherFFmpegBuild"):
        return PurePosixPath("AetherFFmpegBuild", *parts[2:])
    if name == "FFmpegBuild" and parts[0] == "Sources":
        parts = parts[1:]
    elif name != "LibDovi":
        return None
    if not parts or parts[0] not in {item + ".xcframework" for item in libraries + ["Dovi"]}:
        return None
    if len(parts) > 1 and (parts[1] == "Info.plist" or parts[1] in SLICES):
        # dSYMs are not runtime/package inputs.
        if "dSYMs" not in parts:
            return PurePosixPath("binaries", *parts)
    return None


def extract_closure(archive, name, destination, source, libraries):
    license_hashes = {}
    with tarfile.open(archive, "r:gz") as contents:
        for member in contents:
            original = safe_path(member.name)
            if len(original.parts) < 2:
                continue
            relative = PurePosixPath(*original.parts[1:])
            if relative.as_posix() in source["licenses"]:
                license_hashes[relative.as_posix()] = hashlib.sha256(contents.extractfile(member).read()).hexdigest()
            selected = selected_path(name, relative, libraries)
            if selected is None or member.isdir():
                continue
            target = destination / selected
            if target.exists() or target.is_symlink():
                raise ValueError("duplicate Aether archive input")
            target.parent.mkdir(parents=True, exist_ok=True)
            if member.issym():
                link = PurePosixPath(member.linkname)
                # Versioned macOS frameworks contain only relative links inside their bundle.
                if link.is_absolute() or not (target.parent / str(link)).resolve().is_relative_to(destination.resolve()):
                    raise ValueError("escaping Aether framework link")
                target.symlink_to(member.linkname)
            elif member.isfile():
                target.write_bytes(contents.extractfile(member).read())
                target.chmod(0o755 if member.mode & 0o111 else 0o644)
            else:
                raise ValueError("unsupported Aether archive member")
    if license_hashes != source["licenses"]:
        raise ValueError("Aether upstream license mismatch")


def inventory(directory):
    result = {}
    for path in sorted(directory.rglob("*")):
        relative = path.relative_to(directory).as_posix()
        if path.is_symlink():
            if not path.resolve(strict=True).is_relative_to(directory.resolve()):
                raise ValueError("escaping Aether cache link")
            result[relative] = {"link": os.readlink(path)}
        elif path.is_file():
            result[relative] = {"sha256": digest(path)}
    return result


def verify_inventory(directory, expected):
    if inventory(directory) != expected:
        raise ValueError("modified Aether prepared inputs")


def apply_patches(directory, lock):
    for entry in lock["patches"]:
        for relative, hashes in entry["files"].items():
            path = directory / safe_path(relative)
            actual = digest(path) if path.is_file() else None
            if path.is_symlink() or actual != hashes["upstream"]:
                raise ValueError("Aether patch upstream mismatch")
        subprocess.run(["patch", "-f", "-F", "0", "-p1", "-i", str(ROOT / entry["path"])],
                       cwd=directory, check=True)
        for relative, hashes in entry["files"].items():
            path = directory / relative
            actual = digest(path) if path.is_file() else None
            if path.is_symlink() or actual != hashes["patched"]:
                raise ValueError("Aether patch result mismatch")


def binary_slices(data):
    """Read universal Mach-O/archive containers without executing the downloaded code."""
    if data[:4] in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        wide = data[3] == 0xBF
        count = struct.unpack_from(">I", data, 4)[0]
        size = 32 if wide else 20
        result = []
        for index in range(count):
            cpu, _, offset, length = struct.unpack_from(">IIQQ" if wide else ">IIII", data, 8 + index * size)
            if offset + length > len(data):
                raise ValueError("truncated Aether universal binary")
            result.append((CPU[cpu], data[offset:offset + length]))
        return result
    if data[:4] == b"\xcf\xfa\xed\xfe":
        return [(CPU[struct.unpack_from("<I", data, 4)[0]], data)]
    if data.startswith(b"!<arch>\n"):
        # A device archive is thin; its first Mach-O member determines the CPU.
        offset = 8
        while offset + 60 <= len(data):
            name = data[offset:offset + 16].decode("ascii").strip()
            length = int(data[offset + 48:offset + 58])
            body = offset + 60
            if name.startswith("#1/"):
                body += int(name[3:])
            if data[body:body + 4] == b"\xcf\xfa\xed\xfe":
                return [(CPU[struct.unpack_from("<I", data, body + 4)[0]], data)]
            offset += 60 + length + length % 2
    raise ValueError("not a supported Aether Mach-O/archive")


def version(value):
    return (value >> 16, (value >> 8) & 255, value & 255)


def version_tuple(value):
    return tuple(map(int, value.split("."))) + (0,) * (3 - len(value.split(".")))


def macho(data):
    if data[:4] != b"\xcf\xfa\xed\xfe":
        raise ValueError("not a 64-bit Mach-O dylib")
    _, _, _, kind, count, _, flags, _ = struct.unpack_from("<8I", data)
    if kind != 6 or not flags & 0x80:
        raise ValueError("Aether must use a two-level dynamic namespace")
    result = {"dependencies": [], "id": None, "platform": None, "minimum": None}
    offset = 32
    for _ in range(count):
        command, size = struct.unpack_from("<II", data, offset)
        if size < 8 or offset + size > len(data):
            raise ValueError("invalid Aether Mach-O command")
        if command == 0x32:
            platform, minimum, sdk = struct.unpack_from("<III", data, offset + 8)
            result.update(platform=platform, minimum=version(minimum), sdk=version(sdk))
        if command in (0xD, 0xC, 0x80000018, 0x8000001F):
            name_offset = struct.unpack_from("<I", data, offset + 8)[0]
            name = data[offset + name_offset:offset + size].split(b"\0")[0].decode()
            if command == 0xD:
                result["id"] = name
            else:
                result["dependencies"].append(name)
        offset += size
    if result["platform"] is None:
        raise ValueError("missing Aether build-version command")
    return result


def verify_image(path, platform, arches, minimum, libraries, name):
    images = binary_slices(path.read_bytes())
    if {arch for arch, _ in images} != arches:
        raise ValueError("Aether binary architecture mismatch")
    for _, data in images:
        details = macho(data)
        if details["platform"] != platform or details["minimum"] > version_tuple(minimum):
            raise ValueError("Aether Mach-O deployment mismatch")
        valid_ids = {"@rpath/" + name + ".framework/" + name,
                     "@rpath/" + name + ".framework/Versions/A/" + name}
        if details["id"] not in valid_ids:
            raise ValueError("Aether dylib identity mismatch")
        allowed = {identifier for library in libraries for identifier in (
            "@rpath/" + library + ".framework/" + library,
            "@rpath/" + library + ".framework/Versions/A/" + library)}
        for dependency in details["dependencies"]:
            if (dependency not in allowed and not dependency.startswith(
                    ("/usr/lib/", "/System/Library/Frameworks/"))):
                raise ValueError("unapproved Aether dylib dependency: " + dependency)


def normalize_and_verify(directory, lock, normalize=False):
    for name in lock["libraries"] + ["Dovi"]:
        xcframework = directory / "binaries" / (name + ".xcframework")
        info_path = xcframework / "Info.plist"
        info = plistlib.loads(info_path.read_bytes())
        entries = [entry for entry in info["AvailableLibraries"] if entry["LibraryIdentifier"] in SLICES]
        if {entry["LibraryIdentifier"] for entry in entries} != set(SLICES):
            raise ValueError("missing Aether XCFramework slice")
        for entry in entries:
            slice_name = entry["LibraryIdentifier"]
            platform, arches, minimum = SLICES[slice_name]
            if set(entry["SupportedArchitectures"]) != arches:
                raise ValueError("Aether XCFramework architecture metadata mismatch")
            expected_platform = "macos" if platform == 1 else "ios"
            if (entry["SupportedPlatform"] != expected_platform
                    or entry.get("SupportedPlatformVariant") != ("simulator" if platform == 7 else None)):
                raise ValueError("Aether XCFramework platform metadata mismatch")
            entry.pop("DebugSymbolsPath", None)
            base = xcframework / slice_name
            if name == "Dovi":
                if {arch for arch, data in binary_slices((base / entry["LibraryPath"]).read_bytes())
                    if data.startswith(b"!<arch>\n")} != arches:
                    raise ValueError("Aether Dovi static archive mismatch")
                continue
            framework = base / entry["LibraryPath"]
            binary = base / entry["BinaryPath"]
            verify_image(binary, platform, arches, minimum, lock["libraries"], name)
            metadata_path = framework / ("Versions/A/Resources/Info.plist" if platform == 1 else "Info.plist")
            metadata = plistlib.loads(metadata_path.read_bytes())
            if (metadata["CFBundleExecutable"] != name or metadata["CFBundlePackageType"] != "FMWK"
                    or metadata["MinimumOSVersion"] not in {minimum, "26.0"}):
                raise ValueError("unrecognized Aether framework metadata")
            if metadata["MinimumOSVersion"] != minimum:
                if not normalize:
                    raise ValueError("uncorrected Aether framework deployment metadata")
                metadata["MinimumOSVersion"] = minimum
                metadata_path.write_bytes(plistlib.dumps(metadata, sort_keys=False))
        info["AvailableLibraries"] = entries
        normalized = plistlib.dumps(info, sort_keys=False)
        if info_path.read_bytes() != normalized:
            if not normalize:
                raise ValueError("unpruned Aether XCFramework metadata")
            info_path.write_bytes(normalized)


def section_fingerprint(data):
    """Signing/stripping may change LINKEDIT, but not the shipped code/data sections."""
    _, _, _, _, count, _, _, _ = struct.unpack_from("<8I", data)
    offset = 32
    sections = {}
    for _ in range(count):
        command, size = struct.unpack_from("<II", data, offset)
        if command == 0x19:
            section_count = struct.unpack_from("<I", data, offset + 64)[0]
            for index in range(section_count):
                base = offset + 72 + index * 80
                section, segment, _, length, file_offset = struct.unpack_from("<16s16sQQI", data, base)
                flags = struct.unpack_from("<I", data, base + 64)[0]
                if flags & 0xFF in (1, 0xC, 0x12):
                    continue
                if file_offset + length > len(data):
                    raise ValueError("truncated Aether Mach-O section")
                key = segment.rstrip(b"\0").decode() + "/" + section.rstrip(b"\0").decode()
                sections[key] = hashlib.sha256(data[file_offset:file_offset + length]).hexdigest()
        offset += size
    if "__TEXT/__text" not in sections:
        raise ValueError("missing Aether machine code section")
    return sections


def recipe(lock):
    return {"lockSHA256": digest(LOCK_PATH), "scriptSHA256": digest(Path(__file__)),
            "patches": lock["patches"]}


def verify_receipt_files(files, lock):
    # Reverse only the explicitly reviewed source edits. This keeps the pinned
    # baseline independent of adapter patches while rejecting forged cache receipts.
    baseline = dict(files)
    for patch in reversed(lock["patches"]):
        for relative, hashes in patch["files"].items():
            expected = {"sha256": hashes["patched"]} if hashes["patched"] else None
            if baseline.get(relative) != expected:
                raise ValueError("Aether receipt patch mismatch")
            if hashes["upstream"]:
                baseline[relative] = {"sha256": hashes["upstream"]}
            else:
                baseline.pop(relative)
    if identity(baseline) != lock["preparedInventorySHA256"]:
        raise ValueError("Aether receipt differs from the pinned compile closure")


def prepare(lock, verify=False):
    inputs = recipe(lock)
    key = identity(inputs)
    shared = SHARED / "prepared" / key
    receipt_path = SHARED / "prepared" / (key + ".json")
    with guard(SHARED / "locks" / ("prepare-" + key + ".lock")):
        if not receipt_path.exists():
            if verify:
                raise ValueError("Aether inputs have not been prepared")
            shared.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="prepare-", dir=shared.parent) as temporary:
                staged = Path(temporary) / "inputs"
                staged.mkdir()
                for name, source in lock["sources"].items():
                    extract_closure(archive_for(source), name, staged, source, lock["libraries"])
                apply_patches(staged, lock)
                normalize_and_verify(staged, lock, normalize=True)
                files = inventory(staged)
                verify_receipt_files(files, lock)
                # Publish only complete immutable shared inputs while holding the recipe lock.
                staged.rename(shared)
            receipt_path.write_text(json.dumps({"inputs": inputs, "files": files}, indent=2) + "\n")
        receipt = json.loads(receipt_path.read_text())
        if receipt["inputs"] != inputs:
            raise ValueError("stale Aether shared input receipt")
        verify_receipt_files(receipt["files"], lock)
        verify_inventory(shared, receipt["files"])
        # Validate binary metadata on every use, not just the preparation receipt.
        normalize_and_verify(shared, lock)
        verify_inventory(shared, receipt["files"])
        return shared, receipt


def package_inputs(shared, receipt, verify=False):
    destination = PACKAGE / ".cache"
    marker = PACKAGE / ".cache-inputs.json"
    if destination.exists():
        if not marker.exists():
            raise ValueError("unowned Aether package cache")
        previous = json.loads(marker.read_text())
        verify_inventory(destination, previous["files"])
        if previous == receipt:
            return
        if verify:
            raise ValueError("stale Aether package inputs")
        # Only the previously verified, script-owned inputs are replaced.
        shutil.rmtree(destination)
    elif verify:
        raise ValueError("missing Aether package inputs")
    shutil.copytree(shared, destination, symlinks=True)
    verify_inventory(destination, receipt["files"])
    marker.write_text(json.dumps(receipt, indent=2) + "\n")


def verify_app(app, target, lock):
    app = Path(app).resolve(strict=True)
    config = lock["targets"][target]
    platform, slice_arches, _ = SLICES[config["slice"]]
    locked_arches = {value["arch"] for value in lock["targets"].values()
                     if value["slice"] == config["slice"]} & slice_arches
    frameworks = app / "Frameworks"
    wrapper = frameworks / "MeowAetherKernel.framework"
    wrapper_binary = wrapper / "MeowAetherKernel"
    verify_image(wrapper_binary, platform, {config["arch"]}, config["minimum"],
                 lock["libraries"], "MeowAetherKernel")
    symbols = subprocess.check_output(["xcrun", "nm", "-m", "-u", str(wrapper_binary)], text=True)
    imported = [line for line in symbols.splitlines()
                if re.search(r"\b_(?:avcodec_|avformat_|av_|avfilter_|avio_|sws_|swr_)", line)]
    if not imported or any(not re.search(r"\(from AetherLib\w+\)", line) for line in imported):
        raise ValueError("Aether FFmpeg imports are not bound to their own dylibs")
    for name in lock["libraries"]:
        framework = frameworks / (name + ".framework")
        binary = framework / name
        shipped = binary_slices(binary.read_bytes())
        arches = {arch for arch, _ in shipped}
        if config["arch"] not in arches or not arches <= locked_arches:
            raise ValueError("Aether binary architecture mismatch")
        verify_image(binary, platform, arches, config["minimum"], lock["libraries"], name)
        original = PACKAGE / ".cache/binaries" / (name + ".xcframework") / config["slice"]
        xcinfo = plistlib.loads((original.parent / "Info.plist").read_bytes())
        entry = next(item for item in xcinfo["AvailableLibraries"] if item["LibraryIdentifier"] == config["slice"])
        pinned = dict(binary_slices((original / entry["BinaryPath"]).read_bytes()))
        for arch, data in shipped:
            if section_fingerprint(pinned[arch]) != section_fingerprint(data):
                raise ValueError("packaged Aether code differs from the locked binary")
        original_framework = original / entry["LibraryPath"]
        for manifest in original_framework.rglob("*.xcprivacy"):
            relative = manifest.relative_to(original_framework)
            if digest(framework / relative) != digest(manifest):
                raise ValueError("missing or modified Aether framework privacy manifest")
        metadata = plistlib.loads((framework / "Info.plist").read_bytes())
        if version_tuple(metadata["MinimumOSVersion"]) > version_tuple(config["minimum"]):
            raise ValueError("packaged Aether framework raises deployment floor")
    bundles = list(app.rglob("*_AetherEngine.bundle"))
    if len(bundles) != 1 or bundles[0].parent != app:
        raise ValueError("missing or duplicate Aether source resource bundle")
    for resource in ("silent-atmos.mp4", "PrivacyInfo.xcprivacy"):
        if digest(bundles[0] / resource) != digest(PACKAGE / ".cache/AetherEngine/Resources" / resource):
            raise ValueError("missing or modified Aether source resource")
    for filename, checksum in lock["licenses"].items():
        if digest(app / filename) != checksum:
            raise ValueError("missing or modified packaged Aether license")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", action="append", required=True)
    parser.add_argument("--verify", action="store_true", help="Verify prepared inputs without downloading.")
    parser.add_argument("--verify-app", help="Verify an actual built App, including the dynamic FFmpeg bindings.")
    args = parser.parse_args()
    lock = read_lock()
    targets = list(dict.fromkeys(args.target))
    if not set(targets) <= set(lock["targets"]):
        raise ValueError("unknown locked Aether target")
    if args.verify_app and (len(targets) != 1 or not targets[0].startswith("ios")):
        raise ValueError("App verification requires one iOS target")
    PACKAGE.mkdir(parents=True, exist_ok=True)
    with guard(PACKAGE / ".cache-prepare.lock"):
        shared, receipt = prepare(lock, verify=args.verify or bool(args.verify_app))
        package_inputs(shared, receipt, verify=args.verify or bool(args.verify_app))
        if args.verify_app:
            verify_app(args.verify_app, targets[0], lock)
    print("Aether locked inputs verified: " + ", ".join(targets))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)

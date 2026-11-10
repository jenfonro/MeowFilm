#!/usr/bin/env python3
"""Build the locked FFmpeg source closure, not a downloaded player binary."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".cache/ios-video-kernel"
PACKAGE = ROOT / "native/VideoKernel/.cache"
LOCK_PATH = ROOT / "scripts/ios-video-kernel.json"


def digest(path):
    with path.open("rb") as stream:
        value = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
        return value.hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")
    temporary.replace(path)


def run(command, cwd=None, env=None, log=None):
    if log:
        log.parent.mkdir(parents=True, exist_ok=True)
        with log.open("a") as stream:
            stream.write("\n" + " ".join(map(str, command)) + "\n")
            stream.flush()
            subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=stream, check=True)
    else:
        return subprocess.check_output(command, cwd=cwd, env=env, text=True).strip()


def archive_for(name, source):
    path = CACHE / "downloads" / (name + ".tar.gz")
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        partial = path.with_suffix(".partial")
        try:
            request = urllib.request.Request(source["url"], headers={"User-Agent": "Meowfilm-source-prepare"})
            with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
                # Source archives only. The lock must not be repointed to a binary.
                length = 0
                for block in iter(lambda: response.read(1024 * 1024), b""):
                    length += len(block)
                    if length > 64 * 1024 * 1024:
                        raise ValueError("source archive exceeds the download boundary")
                    output.write(block)
            if digest(partial) != source["sha256"]:
                raise ValueError(name + " archive hash mismatch")
            partial.replace(path)
        finally:
            partial.unlink(missing_ok=True)
    if digest(path) != source["sha256"]:
        raise ValueError(name + " archive hash mismatch")
    return path


def prepare_sources(lock):
    inventories = {}
    patch = ROOT / lock["patch"]["path"]
    if digest(patch) != lock["patch"]["sha256"]:
        raise ValueError("reviewed FFmpeg patch hash mismatch")
    for name, source in lock["sources"].items():
        archive = archive_for(name, source)
        destination = CACHE / "sources" / name
        destination.mkdir(parents=True, exist_ok=True)
        expected = {}
        with tarfile.open(archive, "r:gz") as contents:
            for member in contents:
                parts = PurePosixPath(member.name).parts
                if len(parts) < 2:
                    continue
                relative = PurePosixPath(*parts[1:])
                if member.name.startswith("/") or ".." in parts or member.issym() or member.islnk():
                    raise ValueError("unsafe source archive member")
                if member.isdir():
                    continue
                if not member.isfile():
                    raise ValueError("non-file source archive member")
                data = contents.extractfile(member).read()
                original_hash = hashlib.sha256(data).hexdigest()
                target = destination / str(relative)
                allowed = {original_hash}
                if name == "ffmpeg" and str(relative) in lock["patch"]["files"]:
                    entry = lock["patch"]["files"][str(relative)]
                    if original_hash != entry["upstream"]:
                        raise ValueError("patch no longer applies to locked upstream")
                    allowed.add(entry["patched"])
                if target.exists():
                    if target.is_symlink() or digest(target) not in allowed:
                        raise ValueError("modified source cache: " + str(relative))
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(data)
                    target.chmod(0o755 if member.mode & 0o111 else 0o644)
                expected[str(relative)] = original_hash
        if name == "ffmpeg":
            states = {digest(destination / file) == hashes["patched"]
                      for file, hashes in lock["patch"]["files"].items()}
            if states == {False}:
                run(["patch", "-f", "-F", "0", "-p1", "-i", str(patch)], cwd=destination,
                    log=CACHE / "source-prepare.log")
            elif states != {True}:
                raise ValueError("partial patched source cache")
            for file, hashes in lock["patch"]["files"].items():
                if digest(destination / file) != hashes["patched"]:
                    raise ValueError("patched source mismatch")
                expected[file] = hashes["patched"]
        for file, checksum in source["licenses"].items():
            if digest(destination / file) != checksum:
                raise ValueError("license provenance mismatch")
        # No additional compile-time files may enter the source tree.
        actual = {str(path.relative_to(destination)) for path in destination.rglob("*") if path.is_file()}
        if actual != set(expected):
            raise ValueError("unexpected files in " + name + " source cache")
        inventory_hash = hashlib.sha256(json.dumps(expected, sort_keys=True).encode()).hexdigest()
        if inventory_hash != source["inventorySHA256"]:
            raise ValueError("source inventory differs from the reviewed closure")
        inventories[name] = {"revision": source["revision"], "archiveSHA256": source["sha256"],
                             "inventorySHA256": inventory_hash, "files": expected}
    write_json(CACHE / "source-receipt.json", inventories)
    return {name: value["inventorySHA256"] for name, value in inventories.items()}


def build_target(name, target, lock, sources):
    sdk = run(["xcrun", "--sdk", target["sdk"], "--show-sdk-path"])
    sdk_version = run(["xcrun", "--sdk", target["sdk"], "--show-sdk-version"])
    clang = run(["xcrun", "--sdk", target["sdk"], "--find", "clang"])
    compiler_version = run([clang, "--version"])
    build = CACHE / "build" / name
    prefix = CACHE / "install" / name
    build.mkdir(parents=True, exist_ok=True)
    # DASH requires libxml2; use the SDK's system implementation. This adapter
    # deliberately answers only the one explicit pkg-config dependency.
    pkg_config = build / "sdk-pkg-config"
    xml_header = Path(sdk) / "usr/include/libxml2/libxml/xmlversion.h"
    xml_version = re.search(r'#define LIBXML_DOTTED_VERSION\s+"([^"]+)"', xml_header.read_text()).group(1)
    pkg_config.write_text(
        "#!/bin/sh\ncase \"$*\" in\n"
        "  *--version*) echo 1.0 ;;\n"
        "  *--cflags*) echo '-I" + sdk + "/usr/include/libxml2' ;;\n"
        "  *--libs*) echo '-lxml2' ;;\n"
        "  *--modversion*) echo " + xml_version + " ;;\n"
        "  *libxml-2.0*) exit 0 ;;\n  *) exit 1 ;;\nesac\n")
    pkg_config.chmod(0o755)
    flags = "-target " + target["triple"] + " -isysroot " + sdk
    configure = [str(CACHE / "sources/ffmpeg/configure"), *lock["configure"],
                 "--prefix=" + str(prefix), "--target-os=darwin", "--arch=" + target["arch"],
                 "--cc=" + clang, "--pkg-config=" + str(pkg_config),
                 "--extra-cflags=" + flags, "--extra-ldflags=" + flags]
    if target["sdk"] != "macosx":
        configure += ["--enable-cross-compile"]
    if target["arch"] == "x86_64":
        # No NASM requirement for the macOS test executor / Intel simulator.
        # Device ARM assembly stays enabled.
        configure += ["--disable-x86asm"]
    inputs = {"lockSHA256": digest(LOCK_PATH), "prepareSHA256": digest(Path(__file__)),
              "sources": sources, "target": target, "sdkVersion": sdk_version, "sdkPath": sdk,
              "compiler": compiler_version, "configure": configure}
    fingerprint = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
    receipt_path = build / "receipt.json"
    old = json.loads(receipt_path.read_text()) if receipt_path.exists() else {}
    log = CACHE / "logs" / (name + ".log")
    if old.get("fingerprint") != fingerprint or not (build / "config.h").exists():
        run(configure, cwd=build, log=log)
    configuration = (build / "config_components.h").read_text() + (build / "config.h").read_text()
    for component in lock["requiredComponents"]:
        if "#define CONFIG_" + component + " 1" not in configuration:
            raise ValueError(name + " missing required component " + component)
    run(["make", "-j2"], cwd=build, log=log)
    run(["make", "install"], cwd=build, log=log)
    artifacts = {}
    for library in lock["libraries"]:
        path = prefix / "lib" / ("lib" + library + ".a")
        if target["arch"] not in run(["xcrun", "lipo", "-archs", str(path)]).split():
            raise ValueError("incorrect archive architecture")
        artifacts[library] = digest(path)
    headers = {str(path.relative_to(prefix / "include")): digest(path)
               for path in (prefix / "include").rglob("*") if path.is_file()}
    receipt = {"fingerprint": fingerprint, "inputs": inputs, "libraries": artifacts, "headers": headers}
    write_json(receipt_path, receipt)
    return receipt


def current_recipe(name, receipt, lock):
    inputs = receipt.get("inputs", {})
    return (name in lock["targets"] and inputs.get("lockSHA256") == digest(LOCK_PATH)
            and inputs.get("prepareSHA256") == digest(Path(__file__))
            and inputs.get("target") == lock["targets"][name]
            and inputs.get("sources") == {key: source["inventorySHA256"] for key, source in lock["sources"].items()}
            and receipt.get("fingerprint") == hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest())


def package_artifacts(lock):
    available = {}
    for name, target in lock["targets"].items():
        receipt = CACHE / "build" / name / "receipt.json"
        if receipt.exists():
            value = json.loads(receipt.read_text())
            if not current_recipe(name, value, lock):
                continue
            prefix = CACHE / "install" / name
            for library, checksum in value["libraries"].items():
                if digest(prefix / "lib" / ("lib" + library + ".a")) != checksum:
                    raise ValueError("modified built library")
            for header, checksum in value["headers"].items():
                if digest(prefix / "include" / header) != checksum:
                    raise ValueError("modified built header")
            available[name] = value
    if not available:
        raise ValueError("no source-built target to package")
    sdk_targets = {sdk: [name for name in available if lock["targets"][name]["sdk"] == sdk]
                   for sdk in sorted({lock["targets"][name]["sdk"] for name in available})}
    for sdk, names in sdk_targets.items():
        if any(available[name]["headers"] != available[names[0]]["headers"] for name in names[1:]):
            raise ValueError("incompatible public headers for merged " + sdk + " architectures")
    identity = hashlib.sha256(json.dumps(available, sort_keys=True).encode()).hexdigest()
    output = PACKAGE / "Artifacts"
    packaged = PACKAGE / "prepare-receipt.json"
    previous = json.loads(packaged.read_text()) if packaged.exists() else {}
    if previous.get("identity") == identity:
        verify_packaged(previous, lock)
        return previous
    # Rebuild only this script-owned generated artifact directory. No user
    # code, tracked product, build cache or test results live inside it.
    PACKAGE.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="artifacts-", dir=PACKAGE) as temporary:
        temporary = Path(temporary)
        staged = temporary / "Artifacts"
        staged.mkdir()
        for library in lock["libraries"]:
            command = ["xcodebuild", "-create-xcframework"]
            for sdk, names in sdk_targets.items():
                universal = CACHE / "universal" / sdk / ("lib" + library + ".a")
                universal.parent.mkdir(parents=True, exist_ok=True)
                archives = [CACHE / "install" / name / "lib" / universal.name for name in names]
                if len(archives) == 1:
                    shutil.copyfile(archives[0], universal)
                else:
                    run(["xcrun", "lipo", "-create", *map(str, archives), "-output", str(universal)])
                # Xcode merges these roots into one include directory. Each
                # binary target owns only its lib<name>/ tree, not its peers'.
                headers = temporary / "Headers" / sdk / library
                shutil.copytree(CACHE / "install" / names[0] / "include" / ("lib" + library),
                                headers / ("lib" + library))
                command += ["-library", str(universal), "-headers", str(headers)]
            command += ["-output", str(staged / ("lib" + library + ".xcframework"))]
            run(command, log=CACHE / "logs/xcframework.log")
        if output.exists():
            shutil.rmtree(output)
        staged.rename(output)
    files = {str(path.relative_to(output)): digest(path) for path in output.rglob("*") if path.is_file()}
    receipt = {"schema": 1, "status": "complete", "identity": identity, "sourceLockSHA256": digest(LOCK_PATH),
               "targets": available, "artifacts": files}
    write_json(packaged, receipt)
    return receipt


def verify_packaged(receipt, lock):
    if (receipt.get("schema") != 1 or receipt.get("status") != "complete"
            or receipt.get("sourceLockSHA256") != digest(LOCK_PATH)):
        raise ValueError("source lock/schema changed after build")
    if not receipt.get("targets") or any(not current_recipe(name, target, lock)
                                         for name, target in receipt["targets"].items()):
        raise ValueError("prepare source or target recipe changed after build")
    if receipt.get("identity") != hashlib.sha256(json.dumps(receipt["targets"], sort_keys=True).encode()).hexdigest():
        raise ValueError("packaged target identity mismatch")
    for file, checksum in receipt["artifacts"].items():
        if digest(PACKAGE / "Artifacts" / file) != checksum:
            raise ValueError("modified XCFramework artifact")
    actual = {str(path.relative_to(PACKAGE / "Artifacts"))
              for path in (PACKAGE / "Artifacts").rglob("*") if path.is_file()}
    if actual != set(receipt["artifacts"]):
        raise ValueError("extra XCFramework artifact")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources-only", action="store_true")
    parser.add_argument("--target", action="append", default=[])
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    lock = json.loads(LOCK_PATH.read_text())
    if any(name not in lock["targets"] for name in args.target):
        parser.error("unknown locked target")
    CACHE.mkdir(parents=True, exist_ok=True)
    with (CACHE / "prepare.lock").open("w") as guard:
        fcntl.flock(guard, fcntl.LOCK_EX)
        if args.verify:
            receipt = json.loads((PACKAGE / "prepare-receipt.json").read_text())
            verify_packaged(receipt, lock)
            if any(name not in receipt["targets"] for name in args.target):
                raise ValueError("requested device/simulator slice not built")
            print(json.dumps({"status": "complete", "targets": sorted(receipt["targets"]),
                              "receipt": str(PACKAGE / "prepare-receipt.json")}))
            return
        sources = prepare_sources(lock)
        if args.sources_only:
            print(json.dumps({"status": "sources-ready", "inventories": sources}))
            return
        if platform.system() != "Darwin" or not args.target:
            parser.error("building requires Xcode/macOS and explicit --target")
        for name in dict.fromkeys(args.target):
            print(json.dumps({"phase": "building", "target": name}), flush=True)
            build_target(name, lock["targets"][name], lock, sources)
        receipt = package_artifacts(lock)
        print(json.dumps({"status": "complete", "targets": sorted(receipt["targets"]),
                          "receipt": str(PACKAGE / "prepare-receipt.json")}))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print("video-kernel prepare failed: " + str(error), file=sys.stderr)
        sys.exit(1)

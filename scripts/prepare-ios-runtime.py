"""Prepare target-specific iOS framework and common-native Runtime inputs."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import plistlib
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "scripts/ios-node-mobile.json"
NODE_MOBILE = ROOT / ".cache/nodejs-mobile"
SHARED_DOWNLOADS = (Path("E:/Developer") if os.name == "nt" else Path.home() / "Developer") / "cache/downloads"
FRAMEWORK = "NodeMobile.xcframework"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def target(sdk, arch):
    arch = {"x86_64": "x64", "aarch64": "arm64"}.get(arch, arch)
    if sdk not in ("iphoneos", "iphonesimulator") or arch not in (("arm64",) if sdk == "iphoneos" else ("arm64", "x64")):
        raise ValueError("Unsupported iOS native target")
    return {"sdk": sdk, "arch": arch, "minimumOS": "17.0"}


def runtime_path(selected):
    arch = "x86_64" if selected["arch"] == "x64" else selected["arch"]
    return ROOT / f'.cache/ios-runtime/{selected["sdk"]}-{arch}/Runtime'


def policy():
    value = json.loads(POLICY.read_text(encoding="utf-8"))
    if (value.get("schemaVersion") != 1 or value.get("version") != "18.20.4"
            or value["archive"]["sha256"] != "8c5ca3a0d1e38de7f182a5642593e82593b820efd375a14b3ecafc4bcfee620e"):
        raise ValueError("Unsupported Node Mobile policy")
    if sha256(ROOT / value["license"]["path"]) != value["license"]["sha256"]:
        raise ValueError("Node Mobile license differs from the reviewed text")
    return value


def archive_members(package):
    result = {}
    for member in package.infolist():
        path = PurePosixPath(member.filename)
        if (path.is_absolute() or ".." in path.parts or "\\" in member.filename
                or path.as_posix().rstrip("/") != member.filename.rstrip("/")
                or stat.S_ISLNK(member.external_attr >> 16)):
            raise ValueError("Unsafe Node Mobile archive member")
        if not member.is_dir():
            if member.filename in result:
                raise ValueError("Duplicate Node Mobile archive member")
            result[member.filename] = member
    return result


def verify_extraction(directory, package, members):
    actual = set()
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise ValueError("Linked Node Mobile input")
        if path.is_file():
            actual.add(path.relative_to(directory).as_posix())
        elif not path.is_dir():
            raise ValueError("Non-regular Node Mobile input")
    if actual != set(members):
        raise ValueError("Node Mobile extraction inventory mismatch")
    for name, member in members.items():
        expected = hashlib.sha256()
        with package.open(member) as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                expected.update(block)
        if sha256(directory / name) != expected.hexdigest():
            raise ValueError("Modified Node Mobile input: " + name)


def shared_node_archive(pinned):
    expected = pinned["archive"]["sha256"]
    archive = SHARED_DOWNLOADS / expected
    SHARED_DOWNLOADS.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        with tempfile.TemporaryDirectory(prefix="node-mobile-", dir=SHARED_DOWNLOADS) as temporary:
            partial = Path(temporary) / "archive"
            with urllib.request.urlopen(pinned["archive"]["url"], timeout=120) as source, partial.open("wb") as sink:
                shutil.copyfileobj(source, sink)
            if sha256(partial) != expected:
                raise ValueError("Unexpected Node Mobile archive SHA-256")
            try:
                os.link(partial, archive)
            except FileExistsError:
                # Another workspace may have completed the same download.
                pass
    if archive.is_symlink() or sha256(archive) != expected:
        raise ValueError("Unexpected Node Mobile archive SHA-256")
    return archive


def node_mobile(pinned):
    archive = NODE_MOBILE / PurePosixPath(pinned["archive"]["url"]).name
    directory = NODE_MOBILE / "ios"
    NODE_MOBILE.mkdir(parents=True, exist_ok=True)
    if NODE_MOBILE.is_symlink() or archive.is_symlink() or directory.is_symlink():
        raise ValueError("Linked Node Mobile cache")
    if not archive.exists():
        shared = shared_node_archive(pinned)
        with tempfile.TemporaryDirectory(prefix="download-", dir=NODE_MOBILE) as temporary:
            partial = Path(temporary) / "archive"
            shutil.copyfile(shared, partial)
            try:
                os.link(partial, archive)
            except FileExistsError:
                pass
    if sha256(archive) != pinned["archive"]["sha256"]:
        raise ValueError("Unexpected Node Mobile archive SHA-256: " + archive.name)
    with zipfile.ZipFile(archive) as package:
        members = archive_members(package)
        if f"{FRAMEWORK}/Info.plist" not in members:
            raise ValueError("Node Mobile archive has no " + FRAMEWORK)
        if not directory.exists():
            staging = NODE_MOBILE / "ios.partial"
            if staging.exists():
                raise ValueError("Unfinished Node Mobile extraction")
            staging.mkdir()
            try:
                package.extractall(staging)
                for name, member in members.items():
                    mode = member.external_attr >> 16
                    if mode & 0o777:
                        (staging / name).chmod(mode & 0o777)
                verify_extraction(staging, package, members)
                staging.rename(directory)
            finally:
                if staging.exists():
                    shutil.rmtree(staging)
        verify_extraction(directory, package, members)
    return directory / FRAMEWORK


def node_mobile_inputs(pinned, selected):
    framework = node_mobile(pinned)
    info = plistlib.loads((framework / "Info.plist").read_bytes())
    candidates = [item for item in info["AvailableLibraries"]
                  if item["SupportedPlatform"] == "ios"
                  and item.get("SupportedPlatformVariant") == ("simulator" if selected["sdk"] == "iphonesimulator" else None)
                  and ("x86_64" if selected["arch"] == "x64" else selected["arch"]) in item["SupportedArchitectures"]]
    if len(candidates) != 1:
        raise ValueError("Node Mobile has no exact target slice")
    item = candidates[0]
    slice_directory = framework / item["LibraryIdentifier"]
    header_directory = framework.parent / "include/node"
    if not (header_directory / "node_api.h").is_file():
        raise ValueError("Node Mobile archive has no N-API headers")
    headers = [{"path": path.relative_to(ROOT).as_posix(), "sha256": sha256(path)}
               for path in sorted(header_directory.rglob("*")) if path.is_file()]
    archive = NODE_MOBILE / PurePosixPath(pinned["archive"]["url"]).name
    paths = [archive, ROOT / pinned["license"]["path"], framework / "Info.plist"]
    paths.extend(path for path in sorted(slice_directory.rglob("*")) if path.is_file())
    return {"identity": {"version": pinned["version"], "archiveSha256": pinned["archive"]["sha256"],
                         "slice": item["LibraryIdentifier"]},
            "frameworkDirectory": slice_directory.relative_to(ROOT).as_posix(),
            "headerDirectory": header_directory.relative_to(ROOT).as_posix(), "headers": headers,
            "inputs": [{"path": path.relative_to(ROOT).as_posix(), "sha256": sha256(path)} for path in paths] + headers}


def payload(node, selected):
    version = subprocess.run([node, "--version"], check=True, capture_output=True, text=True).stdout.strip()
    if version != "v24.21.0":
        raise ValueError(f"The build tools require Node 24.21.0, found {version or 'none'}")
    if not (ROOT / "node_modules/@meowfilm/script-host").exists():
        raise ValueError("Install the locked workspace first: npm ci --ignore-scripts")
    runtime = runtime_path(selected)
    runtime.parent.mkdir(parents=True, exist_ok=True)
    staging, previous = runtime.parent / "Runtime.partial", runtime.parent / "Runtime.previous"
    if staging.exists() or previous.exists() or runtime.is_symlink():
        raise ValueError("Unfinished or linked iOS runtime preparation")
    try:
        subprocess.run([node, str(ROOT / "scripts/package-ios-runtime.mjs"), "--product", "aggregate",
                        "--sdk", selected["sdk"], "--arch", selected["arch"], "--output", str(staging)], check=True, cwd=ROOT)
        manifest = json.loads((staging / "runtime-manifest.json").read_text(encoding="utf-8"))
        if (manifest.get("status") != "complete" or manifest.get("platform") != "ios"
                or manifest.get("assemblyProfile") != "common-native" or manifest.get("target") != selected
                or manifest.get("transport") != "runtime.http.v1"
                or manifest.get("modules") != ["music", "cat", "source", "pan", "tmdb", "douban", "film", "netease"]
                or len(manifest.get("trustedNative", [])) != 1):
            raise ValueError("The iOS runtime payload is not complete")
        if runtime.exists():
            runtime.rename(previous)
        try:
            staging.rename(runtime)
        except OSError:
            if previous.exists():
                previous.rename(runtime)
            raise
        if previous.exists():
            shutil.rmtree(previous)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return runtime


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--node", default=os.environ.get("MEOWFILM_BUILD_NODE", "node"))
    parser.add_argument("--sdk", choices=("iphoneos", "iphonesimulator"), default="iphonesimulator")
    parser.add_argument("--arch")
    parser.add_argument("--verify-node-mobile", action="store_true")
    options = parser.parse_args()
    selected = target(options.sdk, options.arch or ("arm64" if options.sdk == "iphoneos" else platform.machine()))
    pinned = policy()
    if options.verify_node_mobile:
        print(json.dumps(node_mobile_inputs(pinned, selected)))
        return
    subprocess.run([options.node, str(ROOT / "scripts/verify-native-javascript.mjs")], check=True, cwd=ROOT)
    subprocess.run([options.node, str(ROOT / "scripts/quickjs/build-ios.mjs"),
                    "--sdk", selected["sdk"], "--arch", selected["arch"]], check=True, cwd=ROOT)
    framework = NODE_MOBILE / "ios" / FRAMEWORK
    runtime = payload(options.node, selected)
    print(json.dumps({"nodeMobile": pinned["version"], "framework": str(framework.relative_to(ROOT)),
                      "runtime": str(runtime.relative_to(ROOT)), "target": selected}))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"iOS runtime preparation failed: {error}", file=sys.stderr)
        sys.exit(1)

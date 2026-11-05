"""Prepare the embedded-runtime inputs of the iOS App build.

Fetches and verifies the pinned Node Mobile release and generates the complete
ios-embedded JS payload with the build machine's Node 24 and root lock. Both are
regenerable build inputs under .cache/, referenced by the generated Xcode project.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "scripts/ios-node-mobile.json"
NODE_MOBILE = ROOT / ".cache/nodejs-mobile"
RUNTIME = ROOT / ".cache/ios-runtime/Runtime"
FRAMEWORK = "NodeMobile.xcframework"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def policy():
    value = json.loads(POLICY.read_text(encoding="utf-8"))
    if value.get("schemaVersion") != 1 or not value.get("version"):
        raise ValueError("Unsupported Node Mobile policy")
    license_path = ROOT / value["license"]["path"]
    if sha256(license_path) != value["license"]["sha256"]:
        raise ValueError("Node Mobile license differs from the reviewed text")
    return value


def node_mobile(pinned):
    """Downloads only the pinned archive; an existing extraction is reused."""
    archive = NODE_MOBILE / PurePosixPath(pinned["archive"]["url"]).name
    target = NODE_MOBILE / "ios"
    NODE_MOBILE.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        partial = archive.with_suffix(".download")
        try:
            with urllib.request.urlopen(pinned["archive"]["url"], timeout=120) as source, partial.open("wb") as sink:
                shutil.copyfileobj(source, sink)
            partial.replace(archive)
        finally:
            partial.unlink(missing_ok=True)
    if sha256(archive) != pinned["archive"]["sha256"]:
        raise ValueError("Unexpected Node Mobile archive SHA-256: " + archive.name)
    if not (target / FRAMEWORK / "Info.plist").is_file():
        staging = NODE_MOBILE / "ios.partial"
        if staging.exists():
            shutil.rmtree(staging)
        with zipfile.ZipFile(archive) as package:
            for member in package.namelist():
                path = PurePosixPath(member)
                if path.is_absolute() or ".." in path.parts:
                    raise ValueError("Unsafe Node Mobile archive member")
            package.extractall(staging)
        if not (staging / FRAMEWORK / "Info.plist").is_file():
            raise ValueError("Node Mobile archive has no " + FRAMEWORK)
        if target.exists():
            shutil.rmtree(target)
        staging.rename(target)
    return target / FRAMEWORK


def payload(node):
    """Always regenerates: packaging is offline and fast, and never reuses stale JS."""
    version = subprocess.run([node, "--version"], check=True, capture_output=True, text=True).stdout.strip()
    if not version.startswith("v24."):
        raise ValueError(f"The build tools require Node 24, found {version or 'none'}")
    if not (ROOT / "node_modules/@meowfilm/script-host").exists():
        raise ValueError("Install the locked workspace first: npm ci --ignore-scripts")
    RUNTIME.parent.mkdir(parents=True, exist_ok=True)
    staging = RUNTIME.parent / f"Runtime.partial-{os.getpid()}"
    if staging.exists():
        shutil.rmtree(staging)
    try:
        subprocess.run([node, str(ROOT / "scripts/package-ios-runtime.mjs"), "--product", "aggregate",
                        "--output", str(staging)], check=True, cwd=ROOT)
        manifest = json.loads((staging / "runtime-manifest.json").read_text(encoding="utf-8"))
        if manifest.get("status") != "complete" or manifest.get("platform") != "ios":
            raise ValueError("The iOS runtime payload is not complete")
        previous = RUNTIME.parent / f"Runtime.previous-{os.getpid()}"
        if RUNTIME.exists():
            RUNTIME.rename(previous)
        staging.rename(RUNTIME)
        if previous.exists():
            shutil.rmtree(previous)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return RUNTIME


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--node", default=os.environ.get("MEOWFILM_BUILD_NODE", "node"),
                        help="Node 24 executable used only as a build tool")
    options = parser.parse_args()
    pinned = policy()
    framework = node_mobile(pinned)
    runtime = payload(options.node)
    print(json.dumps({"nodeMobile": pinned["version"], "framework": str(framework.relative_to(ROOT)),
                      "runtime": str(runtime.relative_to(ROOT))}))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"iOS runtime preparation failed: {error}", file=sys.stderr)
        sys.exit(1)

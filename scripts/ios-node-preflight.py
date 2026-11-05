"""Build and run a diagnostic iOS Node host; NOT the App release packager."""
import argparse
import hashlib
import json
import platform
import plistlib
from pathlib import Path, PurePosixPath
import runpy
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
INPUTS = ROOT / "scripts/ios-node-preflight"
WORK = ROOT / "tmp/ios-node-preflight"
BUNDLE = "org.meowfilm.node-preflight"
PREPARE = runpy.run_path(str(ROOT / "scripts/prepare-ios-runtime.py"))


def validate_runtime(directory):
    """Check the prepared JS inventory, not an upstream signature or an App release."""
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Run scripts/prepare-ios-runtime.py to prepare .cache/ios-runtime/Runtime.")
    actual = set()
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Linked diagnostic payload input: {path}")
        if path.is_file():
            actual.add(path.relative_to(directory).as_posix())
        elif not path.is_dir():
            raise ValueError(f"Non-regular diagnostic payload input: {path}")
    manifest = json.loads((directory / "runtime-manifest.json").read_text())
    if (manifest.get("schemaVersion") != 1 or manifest.get("status") != "complete"
            or manifest.get("platform") != "ios" or manifest.get("transport") != "ios-socketpair-v1"
            or manifest.get("assemblyProfile") != "ios-embedded"):
        raise ValueError("Expected a prepared ios-embedded JS payload, not a desktop or failed assembly.")
    dependencies = {item["name"]: item["version"] for item in manifest.get("dependencies", [])}
    if (dependencies.get("@jitl/quickjs-asmjs-mjs-release-sync") != "0.32.0"
            or "@jitl/quickjs-wasmfile-release-sync" in dependencies):
        raise ValueError("The iOS payload must contain only the reviewed asmjs backend.")
    expected = {"runtime-manifest.json"}
    files = manifest.get("files")
    if not isinstance(files, list) or not files:
        raise ValueError("The JS payload inventory is empty.")
    for item in files:
        name = item.get("path")
        if (not isinstance(name, str) or not name or "\\" in name
                or PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts
                or PurePosixPath(name).as_posix() != name or name in expected):
            raise ValueError("Invalid or duplicate JS payload inventory path.")
        expected.add(name)
        if name not in actual:
            raise ValueError(f"Missing JS payload file: {name}")
        data = (directory / name).read_bytes()
        if len(data) != item.get("bytes") or hashlib.sha256(data).hexdigest() != item.get("sha256"):
            raise ValueError(f"JS payload inventory mismatch: {name}")
    if actual != expected:
        raise ValueError("Unlisted files in the prepared JS payload.")
    return manifest


def run(arguments, **options):
    print("+", " ".join(map(str, arguments)), flush=True)
    return subprocess.run(list(map(str, arguments)), check=True, **options)


def output(arguments):
    return run(arguments, stdout=subprocess.PIPE, text=True).stdout.strip()


def build():
    if sys.platform != "darwin":
        raise RuntimeError("This preflight requires macOS with Xcode and an iOS Simulator.")
    payload = PREPARE["RUNTIME"]
    validate_runtime(payload)
    dependency = PREPARE["node_mobile"](PREPARE["policy"]())
    architectures = [
        ("simulator", "iphonesimulator", platform.machine(), "ios-arm64_x86_64-simulator"),
        ("device", "iphoneos", "arm64", "ios-arm64"),
    ]
    for name, sdk, arch, slice_name in architectures:
        app = WORK / name / "NodePreflight.app"
        app.mkdir(parents=True, exist_ok=True)
        framework = dependency / slice_name / "NodeMobile.framework"
        embedded = app / "Frameworks/NodeMobile.framework"
        shutil.copytree(framework, embedded, dirs_exist_ok=True)
        runtime = app / "Runtime"
        if runtime.is_symlink():
            raise RuntimeError(f"Diagnostic payload must not be a symlink: {runtime}")
        # Replace only this generated payload, so deleted or experimental JS cannot linger.
        if runtime.exists():
            shutil.rmtree(runtime)
        shutil.copytree(payload, runtime)
        validate_runtime(runtime)
        shutil.copy2(INPUTS / "probe.mjs", runtime / "probe.mjs")
        plist = {
            "CFBundleIdentifier": BUNDLE, "CFBundleName": "NodePreflight",
            "CFBundleExecutable": "NodePreflight", "CFBundlePackageType": "APPL",
            "CFBundleVersion": "1", "CFBundleShortVersionString": "1.0",
            "MinimumOSVersion": "17.0", "LSRequiresIPhoneOS": True,
            "UIDeviceFamily": [1, 2], "UILaunchScreen": {},
            "UISupportedInterfaceOrientations": ["UIInterfaceOrientationPortrait"],
            "CFBundleSupportedPlatforms": ["iPhoneSimulator" if name == "simulator" else "iPhoneOS"],
        }
        with (app / "Info.plist").open("wb") as stream:
            plistlib.dump(plist, stream)
        target = f"{arch}-apple-ios17.0" + ("-simulator" if name == "simulator" else "")
        run(["xcrun", "--sdk", sdk, "clang", "-fobjc-arc", "-fmodules",
             "-target", target, "-isysroot", output(["xcrun", "--sdk", sdk, "--show-sdk-path"]),
             "-framework", "UIKit", "-framework", "Foundation", "-framework", "NodeMobile",
             "-F", framework.parent, "-Wl,-rpath,@executable_path/Frameworks",
             INPUTS / "Host.m", "-o", app / "NodePreflight"])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", embedded])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", app])
        run(["codesign", "--verify", "--deep", "--strict", app])
        run(["xcrun", "vtool", "-show-build", app / "NodePreflight"])
        print("BUILD_OK", name, flush=True)


def probe(simulator):
    (WORK / "result.json").unlink(missing_ok=True)
    run(["xcrun", "simctl", "install", simulator, WORK / "simulator/NodePreflight.app"])
    container = Path(output(["xcrun", "simctl", "get_app_container", simulator, BUNDLE, "data"]))
    documents = container / "Documents"
    # Only this experiment's own previous results, never production App data.
    for name in ["node-report.json", "native-peer.json", "node-return.plist"]:
        (documents / name).unlink(missing_ok=True)
    log = WORK / "simulator.log"
    process = subprocess.Popen(
        ["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", simulator, BUNDLE],
        stdout=log.open("w"), stderr=subprocess.STDOUT,
    )
    try:
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            report_path = documents / "node-report.json"
            if report_path.exists() and (documents / "node-return.plist").exists():
                report = json.loads(report_path.read_text())
                if report.get("complete") and (documents / "native-peer.json").exists():
                    report["nativePeer"] = json.loads((documents / "native-peer.json").read_text())
                    with (documents / "node-return.plist").open("rb") as stream:
                        report["nodeReturn"] = plistlib.load(stream)
                    report["passed"] = report["passed"] and all(
                        report["nativePeer"].get(key) is True
                        for key in ["described", "echoed", "closed", "sent"]
                    )
                    (WORK / "result.json").write_text(json.dumps(report, indent=2) + "\n")
                    print(json.dumps(report, indent=2), flush=True)
                    return 0 if report["passed"] and report["nodeReturn"]["exitCode"] == 0 else 1
            if process.poll() is not None:
                raise RuntimeError(f"App exited before completion: {process.returncode}; see {log}")
            time.sleep(0.25)
        raise TimeoutError(f"Preflight did not complete; see {log}")
    finally:
        # Preserve a crash/timeout's original failure when the App has already exited.
        if process.poll() is None:
            subprocess.run(["xcrun", "simctl", "terminate", simulator, BUNDLE], check=False)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--simulator", required=True)
    options = parser.parse_args()
    try:
        build()
        sys.exit(probe(options.simulator))
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.CalledProcessError) as error:
        print(f"iOS Node preflight failed: {error}", file=sys.stderr)
        sys.exit(1)

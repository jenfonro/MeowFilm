"""Build and run a diagnostic iOS Node host; NOT the App release packager."""
import argparse
import hashlib
import json
import platform
import plistlib
import re
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


def validate_runtime(directory, selected=None):
    """Check the prepared JS inventory, not an upstream signature or an App release."""
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Run scripts/prepare-ios-runtime.py for the selected SDK and architecture.")
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
            or manifest.get("platform") != "ios" or manifest.get("transport") != "runtime.http.v1"
            or manifest.get("assemblyProfile") != "common-native"
            or manifest.get("entry") != "app/ios/entries/aggregate.mjs"
            or manifest.get("modules") != ["music", "cat", "source", "pan", "tmdb", "douban", "film", "netease"]):
        raise ValueError("Expected a prepared common-native iOS HTTP payload.")
    native = manifest.get("trustedNative", [])
    if (len(native) != 1 or native[0].get("platform") != "ios" or native[0].get("abi") != 1
            or native[0].get("napi") != 8
            or native[0].get("path") != "Frameworks/MeowScriptHost.framework/MeowScriptHost"
            or not isinstance(native[0].get("bindingIdentity"), str)
            or not re.fullmatch(r"[a-f0-9]{64}", native[0]["bindingIdentity"])):
        raise ValueError("Missing reviewed iOS native framework identity.")
    target = manifest.get("target", {})
    if (PREPARE["target"](target.get("sdk"), target.get("arch")) != target
            or native[0].get("target") != target or native[0].get("arch") != target["arch"]
            or selected is not None and selected != target):
        raise ValueError("Mismatched iOS payload target.")
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


def build(simulator_only=False):
    if sys.platform != "darwin":
        raise RuntimeError("This preflight requires macOS with Xcode and an iOS Simulator.")
    architectures = [
        ("simulator", "iphonesimulator", platform.machine()),
        ("device", "iphoneos", "arm64"),
    ]
    if simulator_only:
        architectures = architectures[:1]
    for _, sdk, arch in architectures:
        run([sys.executable, "-I", "-B", ROOT / "scripts/prepare-ios-runtime.py",
             "--sdk", sdk, "--arch", arch], cwd=ROOT)
    node_mobile_policy = PREPARE["policy"]()
    dependency = PREPARE["node_mobile"](node_mobile_policy)
    for name, sdk, arch in architectures:
        selected = PREPARE["target"](sdk, arch)
        slice_name = PREPARE["node_mobile_inputs"](node_mobile_policy, selected)["identity"]["slice"]
        payload = PREPARE["runtime_path"](selected)
        manifest = validate_runtime(payload, selected)
        node_arch = selected["xcframeworkArchitecture"]
        native = ROOT / f".cache/ios-native/{sdk}-{node_arch}/MeowScriptHost.framework"
        receipt_path = ROOT / f".cache/ios-native/{sdk}-{node_arch}/build/Release/script-host-native.json"
        receipt = json.loads(receipt_path.read_text())
        artifact = receipt["artifact"]
        if (receipt.get("target") != selected or receipt.get("bindingIdentity") != manifest["trustedNative"][0]["bindingIdentity"]
                or artifact["path"] != (native / "MeowScriptHost").relative_to(ROOT).as_posix()
                or artifact["sha256"] != PREPARE["sha256"](native / "MeowScriptHost")):
            raise ValueError("Native framework does not match the prepared target payload")
        app = WORK / name / "NodePreflight.app"
        app.mkdir(parents=True, exist_ok=True)
        framework = dependency / slice_name / "NodeMobile.framework"
        embedded = app / "Frameworks/NodeMobile.framework"
        if app.is_symlink() or native.is_symlink():
            raise ValueError("Linked preflight build input")
        for source, destination in ((framework, embedded), (native, app / "Frameworks/MeowScriptHost.framework")):
            if destination.is_symlink():
                raise ValueError("Linked preflight framework")
            if destination.exists():
                shutil.rmtree(destination)
            shutil.copytree(source, destination)
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
            "MinimumOSVersion": selected["minimumOS"], "LSRequiresIPhoneOS": True,
            "UIDeviceFamily": [1, 2], "UILaunchScreen": {},
            "UISupportedInterfaceOrientations": ["UIInterfaceOrientationPortrait"],
            "CFBundleSupportedPlatforms": [
                "iPhoneSimulator" if selected["variant"] == "simulator" else "iPhoneOS"],
        }
        with (app / "Info.plist").open("wb") as stream:
            plistlib.dump(plist, stream)
        target = f'{selected["xcframeworkArchitecture"]}-apple-ios{selected["minimumOS"]}' + (
            "-simulator" if selected["variant"] == "simulator" else "")
        run(["xcrun", "--sdk", sdk, "clang", "-fobjc-arc", "-fmodules",
             "-target", target, "-isysroot", output(["xcrun", "--sdk", sdk, "--show-sdk-path"]),
             "-framework", "UIKit", "-framework", "Foundation", "-framework", "NodeMobile",
             "-F", framework.parent, "-Wl,-rpath,@executable_path/Frameworks",
             INPUTS / "Host.m", "-o", app / "NodePreflight"])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", embedded])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", app / "Frameworks/MeowScriptHost.framework"])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", app])
        run(["codesign", "--verify", "--deep", "--strict", app])
        run(["xcrun", "vtool", "-show-build", app / "NodePreflight"])
        print("BUILD_OK", name, flush=True)


def probe(simulator, selected_case=None):
    (WORK / "result.json").unlink(missing_ok=True)
    run(["xcrun", "simctl", "install", simulator, WORK / "simulator/NodePreflight.app"])
    container = Path(output(["xcrun", "simctl", "get_app_container", simulator, BUNDLE, "data"]))
    documents = container / "Documents"
    # Only this experiment's own previous results, never production App data.
    for name in ["node-report.json", "node-return.plist"]:
        (documents / name).unlink(missing_ok=True)
    log = WORK / "simulator.log"
    command = ["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", simulator, BUNDLE]
    if selected_case:
        command += ["--case", selected_case]
    process = subprocess.Popen(
        command,
        stdout=log.open("w"), stderr=subprocess.STDOUT,
    )
    try:
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            report_path = documents / "node-report.json"
            if report_path.exists() and (documents / "node-return.plist").exists():
                report = json.loads(report_path.read_text())
                if report.get("complete"):
                    if selected_case and [case.get("name") for case in report.get("cases", [])] != [selected_case]:
                        raise ValueError("Selected diagnostic case did not execute exactly once")
                    with (documents / "node-return.plist").open("rb") as stream:
                        report["nodeReturn"] = plistlib.load(stream)
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
    parser.add_argument("--simulator-only", action="store_true", help="Build only the executing simulator host.")
    parser.add_argument("--case", help="Execute one exact diagnostic case, without unrelated probes.")
    options = parser.parse_args()
    try:
        build(simulator_only=options.simulator_only)
        sys.exit(probe(options.simulator, options.case))
    except (OSError, ValueError, RuntimeError, TimeoutError, subprocess.CalledProcessError) as error:
        print(f"iOS Node preflight failed: {error}", file=sys.stderr)
        sys.exit(1)

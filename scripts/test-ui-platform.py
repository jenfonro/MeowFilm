"""Build and run only the UIKit platform tests with Xcode's default build cache."""
import argparse
import json
from pathlib import Path
import platform
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
CLASS = "MeowUIPlatformTests"


def expected_tests():
    source = (ROOT / "apps/ios/UITests/MeowUIPlatformTests.swift").read_text(encoding="utf-8")
    names = re.findall(r"^\s+func (test\w+)\(", source, re.M)
    if not names or len(set(names)) != len(names):
        raise ValueError("MeowUIPlatformTests must contain unique tests")
    return names


def run(command):
    return subprocess.run(
        command,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--simulator", required=True)
    parser.add_argument("--result", required=True)
    parser.add_argument("--test", action="append", help="Run a named component test; repeat to select more.")
    args = parser.parse_args()
    if sys.platform != "darwin" or not re.fullmatch(r"[0-9A-Fa-f-]{36}", args.simulator):
        raise ValueError("A macOS executor and explicit Simulator UDID are required")

    names = expected_tests()
    if args.test:
        if not set(args.test) <= set(names):
            raise ValueError("Unknown component test")
        names = list(dict.fromkeys(args.test))
    result = Path(args.result).resolve()
    if result.exists():
        raise ValueError("Result bundle already exists: " + str(result))
    log = result.with_suffix(".log")
    log.parent.mkdir(parents=True, exist_ok=True)
    preparation = run([
        sys.executable, "-I", "-B", "scripts/prepare-ios-video-kernel.py",
        "--target", "ios-simulator-" + platform.machine(),
    ])
    if preparation.returncode:
        log.write_text(preparation.stdout, encoding="utf-8")
        print("Video kernel preparation failed; see " + str(log), file=sys.stderr)
        return preparation.returncode
    command = [
        "xcodebuild",
        "-project", "apps/ios/Meowfilm.xcodeproj",
        "-scheme", "MeowfilmAcceptance",
        "-destination", "platform=iOS Simulator,id=" + args.simulator,
        "-parallel-testing-enabled", "NO",
        "-collect-test-diagnostics", "never",
        "-resultBundlePath", str(result),
    ]
    command.extend(
        "-only-testing:MeowfilmAcceptanceUITests/{}/{}".format(CLASS, name)
        for name in names
    )
    # The component runner is native to its macOS host. Building the host's
    # actual simulator architecture avoids linking an unrelated cross-arch
    # package object that cannot satisfy this runner.
    command.extend(["ARCHS=" + platform.machine(), "ONLY_ACTIVE_ARCH=YES"])
    command.append("test")
    process = run(command)
    log.write_text(preparation.stdout + "\n" + process.stdout, encoding="utf-8")
    events = re.findall(
        r"Test Case '-\[[A-Za-z0-9_]+\.{} (test[A-Za-z0-9_]+)\]' "
        r"(started|passed|failed|skipped)".format(CLASS),
        process.stdout,
    )
    started = [name for name, state in events if state == "started"]
    finished = [(name, state) for name, state in events if state != "started"]
    errors = []
    if process.returncode:
        errors.append("xcodebuild exited " + str(process.returncode))
    if len(started) != len(names) or set(started) != set(names):
        errors.append("missing, duplicate or unexpected test starts")
    if len(finished) != len(names) or {name for name, _ in finished} != set(names):
        errors.append("missing, duplicate or unexpected test completions")
    if any(state != "passed" for _, state in finished):
        errors.append("a platform test failed or skipped")
    summary = {
        "tests": names,
        "started": started,
        "finished": finished,
        "exitCode": process.returncode,
        "errors": errors,
    }
    result.with_suffix(".json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())

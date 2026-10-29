"""Package a device-only unsigned IPA; never register an Apple identity."""
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import zipfile

BUNDLE_ID = "dev.meowfilm.aggregate"
IPA_NAME = "meowfilm-beta-ios.ipa"
MACHO = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe",
         b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"}


def build_version(number):
    number = int(number)
    if not 1 <= number < 99_990_000:
        raise ValueError("CI build number is outside the version range")
    return f"{1 + number // 10000}.{number // 100 % 100}.{number % 100}"


def validate_info(info):
    if info.get("CFBundleIdentifier") != BUNDLE_ID:
        raise ValueError("The product bundle identifier must remain stable")
    if info.get("CFBundlePackageType") != "APPL":
        raise ValueError("Not an application bundle")
    if info.get("CFBundleSupportedPlatforms") != ["iPhoneOS"] or info.get("DTPlatformName") != "iphoneos":
        raise ValueError("A device app is required, not a Simulator build")


def output(*command):
    return subprocess.check_output(command, text=True).strip()


def build_number():
    return os.environ.get("MEOW_BUILD_NUMBER") or os.environ.get("GITHUB_RUN_NUMBER") or "1"


def strip_signatures(app):
    if list(app.rglob("embedded.mobileprovision")):
        raise ValueError("CI must not embed provisioning profiles")
    # A toolchain may add ad-hoc Mach-O signatures. Removing a signature does not
    # use a signing identity, contact Apple or allocate a certificate/App ID.
    for path in app.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        with path.open("rb") as stream:
            is_macho = stream.read(4) in MACHO
        if not is_macho:
            continue
        probe = subprocess.run(["/usr/bin/codesign", "-d", str(path)],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if probe.returncode == 0:
            subprocess.run(["/usr/bin/codesign", "--remove-signature", str(path)], check=True)
        check = subprocess.run(["/usr/bin/codesign", "-d", str(path)],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if check.returncode == 0 or "not signed at all" not in check.stderr:
            raise ValueError("Unable to verify an unsigned Mach-O file: " + path.name)
    if list(app.rglob("_CodeSignature")):
        raise ValueError("Unexpected bundle signature in unsigned product")


def main(app_source, directory):
    root = Path(__file__).resolve().parents[1]
    directory = Path(directory).resolve(strict=True)
    if not directory.is_relative_to((root / "tmp").resolve()):
        raise ValueError("Packaging staging must be under tmp/")
    commit = output("git", "-C", str(root), "rev-parse", "HEAD")
    if os.environ.get("GITHUB_SHA", commit) != commit:
        raise ValueError("Build commit does not match the workflow")
    modified = bool(output("git", "-C", str(root), "status", "--porcelain", "--untracked-files=normal"))
    app_source = Path(app_source).resolve(strict=True)
    payload = directory / "Payload"
    payload.mkdir()
    app = payload / "Meowfilm.app"
    shutil.copytree(app_source, app, symlinks=True)
    if list(app.rglob("*.appex")) or list(app.rglob("*.app")):
        raise ValueError("New app identities/extensions need an explicit product decision")
    info_path = app / "Info.plist"
    info = plistlib.loads(info_path.read_bytes())
    validate_info(info)
    executable = app / info["CFBundleExecutable"]
    if output("/usr/bin/lipo", "-archs", str(executable)).split() != ["arm64"]:
        raise ValueError("Expected only the arm64 iPhone architecture")
    platform = output("/usr/bin/xcrun", "vtool", "-show-build", str(executable))
    if not re.search(r"\bplatform\s+IOS\b", platform):
        raise ValueError("Mach-O platform is not iOS device")
    strip_signatures(app)
    number = build_number()
    info["CFBundleVersion"] = build_version(number)
    # Do not rewrite CFBundleIdentifier, marketing version, product name,
    # entitlements, internal data namespaces or platform-account identities.
    info_path.write_bytes(plistlib.dumps(info, fmt=plistlib.FMT_BINARY, sort_keys=False))
    release = directory / "build"
    release.mkdir()
    ipa = release / IPA_NAME
    subprocess.run(["/usr/bin/ditto", "-c", "-k", "--keepParent", str(payload), str(ipa)],
                   env={**os.environ, "COPYFILE_DISABLE": "1"}, check=True)
    with zipfile.ZipFile(ipa) as archive:
        packaged = plistlib.loads(archive.read("Payload/Meowfilm.app/Info.plist"))
        validate_info(packaged)
        if packaged["CFBundleVersion"] != info["CFBundleVersion"]:
            raise ValueError("Packaged version differs from the build")
    digest = hashlib.sha256(ipa.read_bytes()).hexdigest()
    (release / "meowfilm-beta-ios.sha256").write_text(f"{digest}  {IPA_NAME}\n")
    metadata = {
        "commit": commit, "sourceModified": modified, "bundleIdentifier": BUNDLE_ID,
        "version": info["CFBundleShortVersionString"], "buildVersion": info["CFBundleVersion"],
        "architecture": "arm64", "platform": "iphoneos", "signing": "unsigned",
        "minimumOSVersion": info.get("MinimumOSVersion"),
        "xcode": (directory / "toolchain.txt").read_text().strip(),
        "buildNumber": int(number),
        "ipa": IPA_NAME, "ipaSHA256": digest, "ipaBytes": ipa.stat().st_size,
        "testsExecuted": False, "deviceInstallationVerified": False,
    }
    if os.environ.get("GITHUB_RUN_ID"):
        metadata["runAttempt"] = int(os.environ["GITHUB_RUN_ATTEMPT"])
        metadata["runURL"] = (f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}"
                              f"/actions/runs/{os.environ['GITHUB_RUN_ID']}")
    (release / "beta-ios-build.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: package-beta-ios.py <built Meowfilm.app> <tmp staging>")
    main(sys.argv[1], sys.argv[2])

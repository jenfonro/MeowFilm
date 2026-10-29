"""Read-only gate for the local ad-hoc Simulator App, not device/IPA signing."""
import argparse
import json
from pathlib import Path
import plistlib
import struct
import subprocess

BUNDLE = "dev.meowfilm.aggregate"
MAX_EXECUTABLE = 64 * 1024 * 1024


def simulator_entitlements(binary):
    """Read the signed Mach-O's Simulator permissions, not an intermediate file.

    Xcode separates the host code-signing entitlements from simulated iOS
    entitlements in __TEXT,__entitlements. Current builds explicitly select one
    64-bit Simulator architecture; fat/device/other binaries fail closed.
    """
    if not 32 <= len(binary) <= MAX_EXECUTABLE:
        raise ValueError("Invalid Simulator executable size")
    magic, cpu, _, kind, count, size, _, _ = struct.unpack_from("<8I", binary)
    end = 32 + size
    if magic != 0xFEEDFACF or cpu not in (0x01000007, 0x0100000C) or kind != 2:
        raise ValueError("Expected a thin 64-bit Simulator executable")
    if not 0 < count <= 4096 or size > 1024 * 1024 or end > len(binary):
        raise ValueError("Invalid Mach-O load commands")
    offset, payload, platforms = 32, None, []
    for _ in range(count):
        if offset + 8 > end:
            raise ValueError("Truncated Mach-O command")
        command, length = struct.unpack_from("<II", binary, offset)
        if length < 8 or length % 8 or offset + length > end:
            raise ValueError("Invalid Mach-O command size")
        if command == 0x32:  # LC_BUILD_VERSION
            if length < 24:
                raise ValueError("Truncated Mach-O platform")
            platform, _, _, tools = struct.unpack_from("<4I", binary, offset + 8)
            if length != 24 + tools * 8:
                raise ValueError("Invalid Mach-O build-version size")
            platforms.append(platform)
        elif command == 0x19:  # LC_SEGMENT_64
            if length < 72:
                raise ValueError("Truncated Mach-O segment")
            sections = struct.unpack_from("<I", binary, offset + 64)[0]
            if length != 72 + sections * 80:
                raise ValueError("Invalid Mach-O section table")
            for index in range(sections):
                section = offset + 72 + index * 80
                name = binary[section:section + 16].rstrip(b"\0")
                segment = binary[section + 16:section + 32].rstrip(b"\0")
                if (segment, name) != (b"__TEXT", b"__entitlements"):
                    continue
                if payload is not None or binary[offset + 8:offset + 24].rstrip(b"\0") != b"__TEXT":
                    raise ValueError("Ambiguous Simulator entitlement section")
                _, byte_count, start = struct.unpack_from("<QQI", binary, section + 32)
                if not 0 < byte_count <= 65536 or start < end or start + byte_count > len(binary):
                    raise ValueError("Invalid Simulator entitlement range")
                payload = binary[start:start + byte_count].rstrip(b"\0")
        offset += length
    if offset != end or platforms != [7] or payload is None:
        raise ValueError("Missing unambiguous iOS Simulator platform or entitlements")
    try:
        value = plistlib.loads(payload)
    except Exception as error:
        raise ValueError("Invalid Simulator entitlement plist") from error
    if not isinstance(value, dict):
        raise ValueError("Simulator entitlements must be a dictionary")
    return value


def validate(info, entitlements, signature, has_profile=False, host_entitlements=None):
    if info.get("DTPlatformName") != "iphonesimulator" or info.get("CFBundleSupportedPlatforms") != ["iPhoneSimulator"]:
        raise ValueError("Only an iPhone Simulator product is allowed")
    if info.get("CFBundleIdentifier") != BUNDLE:
        raise ValueError("Unexpected product identity")
    if "Signature=adhoc" not in signature.splitlines() or "Authority=" in signature or has_profile:
        raise ValueError("Simulator gate requires local ad-hoc signing without provisioning")
    if entitlements.get("application-identifier") != BUNDLE:
        raise ValueError("Missing or incorrect Simulator Keychain application identity")
    if entitlements.get("get-task-allow") is not True:
        raise ValueError("Simulator debugging entitlement is missing")
    host = host_entitlements if host_entitlements is not None else {}
    if not isinstance(host, dict):
        raise ValueError("Invalid host signing entitlements")
    if "application-identifier" in host and host["application-identifier"] != BUNDLE:
        raise ValueError("Unexpected host signing identity")
    for permissions in (entitlements, host):
        if permissions.get("com.apple.developer.team-identifier"):
            raise ValueError("Simulator validation must not depend on an Apple team")
        if "keychain-access-groups" in permissions and permissions["keychain-access-groups"] != [BUNDLE]:
            raise ValueError("Unexpected cross-application Keychain access")


def command(*args):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
    if result.returncode:
        # Never dump an arbitrary signing diagnostic or product payload.
        raise ValueError(f"{args[0]} {args[1]} failed ({result.returncode})")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    args = parser.parse_args()
    app = args.app.resolve(strict=True)
    info = plistlib.loads((app / "Info.plist").read_bytes())
    command("codesign", "--verify", "--deep", "--strict", str(app))
    display = command("codesign", "-d", "--verbose=4", str(app))
    host = command("codesign", "-d", "--entitlements", ":-", str(app))
    executable = (app / info["CFBundleExecutable"]).resolve(strict=True)
    if executable.parent != app or not executable.is_file() or executable.stat().st_size > MAX_EXECUTABLE:
        raise ValueError("Invalid Simulator executable path or size")
    entitlements = simulator_entitlements(executable.read_bytes())
    validate(info, entitlements, display.stderr.decode("utf-8"),
             has_profile=(app / "embedded.mobileprovision").exists(),
             host_entitlements=plistlib.loads(host.stdout))
    print(json.dumps({"simulatorSigning": "PASS", "bundle": BUNDLE,
                      "signature": "adhoc", "entitlements": "__TEXT,__entitlements",
                      "appleAccountOrProfileRequired": False}))


if __name__ == "__main__":
    main()

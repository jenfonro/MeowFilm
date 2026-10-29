"""Signing policy only: no credentials, Xcode, codesign or App execution."""
import importlib.util
from pathlib import Path
import plistlib
import struct
import unittest

SPEC = importlib.util.spec_from_file_location(
    "simulator_signing", Path(__file__).resolve().parents[1] / "check-simulator-signing.py")
SIGNING = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNING)


class SimulatorSigningTests(unittest.TestCase):
    def setUp(self):
        self.info = {"DTPlatformName": "iphonesimulator", "CFBundleSupportedPlatforms": ["iPhoneSimulator"],
                     "CFBundleIdentifier": SIGNING.BUNDLE}
        self.entitlements = {"application-identifier": SIGNING.BUNDLE, "get-task-allow": True}
        self.signature = "Signature=adhoc\nTeamIdentifier=not set\n"

    def test_ad_hoc_simulator_identity_is_accepted(self):
        SIGNING.validate(self.info, self.entitlements, self.signature)

    def test_device_and_wrong_bundle_cannot_enter_this_gate(self):
        for changes in ({"DTPlatformName": "iphoneos"}, {"CFBundleSupportedPlatforms": ["iPhoneOS"]},
                        {"CFBundleIdentifier": "dev.other"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                SIGNING.validate(dict(self.info, **changes), self.entitlements, self.signature)

    def test_unsigned_or_apple_provisioned_products_fail(self):
        for signature in ("", "Signature=adhoc\nAuthority=unexpected\n"):
            with self.subTest(signature=signature), self.assertRaises(ValueError):
                SIGNING.validate(self.info, self.entitlements, signature)
        with self.assertRaises(ValueError):
            SIGNING.validate(self.info, self.entitlements, self.signature, has_profile=True)

    def test_missing_identity_and_foreign_keychain_scope_fail(self):
        for changes in ({"application-identifier": None}, {"application-identifier": "dev.other"},
                        {"get-task-allow": False}, {"com.apple.developer.team-identifier": "OTHER"},
                        {"keychain-access-groups": ["dev.other"]}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                SIGNING.validate(self.info, dict(self.entitlements, **changes), self.signature)

    def test_empty_host_permissions_do_not_replace_simulated_application_identity(self):
        SIGNING.validate(self.info, self.entitlements, self.signature, host_entitlements={})
        with self.assertRaises(ValueError):
            SIGNING.validate(self.info, {}, self.signature, host_entitlements=self.entitlements)

    def test_host_team_and_foreign_access_groups_are_still_rejected(self):
        for host in ({"application-identifier": "dev.other"},
                     {"com.apple.developer.team-identifier": "OTHER"},
                     {"keychain-access-groups": ["dev.other"]}, []):
            with self.subTest(host=host), self.assertRaises(ValueError):
                SIGNING.validate(self.info, self.entitlements, self.signature, host_entitlements=host)

    def executable(self, payload=None, cpu=0x01000007, platform=7, sections=1, name=b"__entitlements"):
        if payload is None:
            payload = plistlib.dumps(self.entitlements)
        segment_size = 72 + sections * 80
        size = segment_size + 24
        start = 32 + size
        header = struct.pack("<8I", 0xFEEDFACF, cpu, 0, 2, 2, size, 0, 0)
        segment = struct.pack("<II16s4Q4I", 0x19, segment_size, b"__TEXT",
                              0, start + len(payload), 0, start + len(payload), 7, 5, sections, 0)
        section = struct.pack("<16s16sQQ8I", name, b"__TEXT", start, len(payload),
                              start, 0, 0, 0, 0, 0, 0, 0)
        build = struct.pack("<6I", 0x32, 24, platform, 17 << 16, 26 << 16, 0)
        return header + segment + section * sections + build + payload

    def test_reads_simulated_permissions_from_both_current_64_bit_architectures(self):
        for cpu in (0x01000007, 0x0100000C):
            with self.subTest(cpu=cpu):
                self.assertEqual(SIGNING.simulator_entitlements(self.executable(cpu=cpu)), self.entitlements)

    def test_missing_duplicate_and_invalid_entitlement_sections_fail(self):
        for binary in (self.executable(name=b"__other"), self.executable(sections=2),
                       self.executable(payload=b"broken"),
                       self.executable(payload=plistlib.dumps([]))):
            with self.subTest(binary_length=len(binary)), self.assertRaises(ValueError):
                SIGNING.simulator_entitlements(binary)

    def test_device_fat_and_unsupported_architectures_fail_before_permission_validation(self):
        for binary in (self.executable(platform=2), self.executable(platform=1),
                       self.executable(cpu=7), b"\xca\xfe\xba\xbe" + self.executable()[4:]):
            with self.subTest(header=binary[:8]), self.assertRaises(ValueError):
                SIGNING.simulator_entitlements(binary)

    def test_truncated_commands_and_out_of_bounds_entitlements_fail(self):
        original = self.executable()
        malformed = [original[:length] for length in (0, 16, 31, 64, len(original) - 1)]
        for offset, format, value in ((20, "<I", len(original)), (36, "<I", 0),
                                     (96, "<I", 4096), (144, "<Q", 65537),
                                     (152, "<I", len(original)), (152, "<I", 0),
                                     (12, "<I", 6), (16, "<I", 4097)):
            data = bytearray(original)
            struct.pack_into(format, data, offset, value)
            malformed.append(bytes(data))
        for binary in malformed:
            with self.subTest(binary_length=len(binary)), self.assertRaises(ValueError):
                SIGNING.simulator_entitlements(binary)

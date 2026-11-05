import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicCredentialImportParserTests: XCTestCase {
    func testFixedThreePlatformSnapshotsBecomeExistingNativeCredentialV1() async throws {
        let transport = MusicCredentialImportNoHTTP()
        let providers: [MusicAccountPlatform: any MusicAccountProvider] = [
            .qq: QQMusicAccountAdapter(transport: transport, now: { Date() }),
            .netease: NeteaseMusicAccountAdapter(transport: transport, now: { Date() }),
            .kugou: KugouMusicAccountAdapter(transport: transport, now: { Date() })
        ]
        for platform in MusicAccountPlatform.allCases {
            let material = try MusicCredentialImportFixture.material(platform)
            XCTAssertEqual(material.nativeID, "42")
            let id = try await providers[platform]!.nativeID(material.credential)
            XCTAssertEqual(id, "42")
            let raw = try MusicQQWire.decode(material.credential.dataRepresentation())
            XCTAssertEqual(raw["platform"] as? String, platform.rawValue)
            XCTAssertNil(raw["nickname"]); XCTAssertFalse(material.description.contains("synthetic-import-key"))
            if platform == .kugou {
                XCTAssertEqual((raw["device"] as? [String: Any])?["seed"] as? String, MusicCredentialImportFixture.seed)
            }
        }
        let count = await transport.calls
        XCTAssertEqual(count, 0, "Conversion must not contact a platform or old service")
    }
    func testExactMeowmusicEnvelopeDoesNotTreatAdminOrPublicBindingAsCredentialExport() throws {
        let root: [String: Any] = ["format": "meowfilm.music.credentials", "version": 1, "source": "meowmusic",
            "platform": "tx", "accountId": "42", "credentials": ["musicid": "42", "musickey": "synthetic-key"], "device": [:]]
        let bytes = try JSONSerialization.data(withJSONObject: root)
        let result = try MusicCredentialImportParser.parse(bytes, platform: .qq, format: .meowMusicRecord)
        XCTAssertEqual(result.nativeID, "42")
        let invalid: [[String: Any]] = [
            ["username": "synthetic-admin", "password": "synthetic-password"],
            ["token": "synthetic-server-session"], ["platform": "tx", "accountId": "42", "loggedIn": true]
        ]
        for bad in invalid {
            let data = try JSONSerialization.data(withJSONObject: bad)
            XCTAssertThrowsError(try MusicCredentialImportParser.parse(data, platform: .qq, format: .meowMusicRecord))
        }
        var mismatch = root; mismatch["accountId"] = "99"
        XCTAssertThrowsError(try MusicCredentialImportParser.parse(
            JSONSerialization.data(withJSONObject: mismatch), platform: .qq, format: .meowMusicRecord))
        var device = root; device["device"] = ["android_id": "synthetic-device"]
        XCTAssertThrowsError(try MusicCredentialImportParser.parse(
            JSONSerialization.data(withJSONObject: device), platform: .qq, format: .meowMusicRecord)) {
            XCTAssertEqual($0 as? MusicCredentialImportError, .unsupportedDevice)
        }
    }
    func testQQCookieAliasesAreExactAndControlBytesNeverBecomeHeaders() throws {
        let cookie = Data("uin=o00042; qqmusic_key=synthetic-key; qm_keyst=synthetic-key".utf8)
        XCTAssertEqual(try MusicCredentialImportParser.parse(cookie, platform: .qq, format: .qqCookie).nativeID, "42")
        for value in [
            "uin=o00042; musicid=99; musickey=synthetic-key",
            "musicid=42; musickey=first; musickey=second",
            "musicid=42; musickey=first; qqmusic_key=second",
            "musicid=42; musickey=synthetic\r\nInjected=value",
            "musicid=42; musickey=synthetic%0d%0aInjected=value",
            "musicid=42; musickey=synthetic%00key",
            "token=synthetic-admin-session", "{\"cookie\":\"not-a-raw-cookie\"}"
        ] {
            XCTAssertThrowsError(try MusicCredentialImportParser.parse(Data(value.utf8), platform: .qq, format: .qqCookie))
        }
        XCTAssertThrowsError(try MusicCredentialImportParser.parse(cookie, platform: .netease, format: .qqCookie))
    }
    func testWYRequiresScopedUIDAndKGRequiresOriginalSeedWithoutRandomFallback() throws {
        var wy = MusicCredentialImportFixture.snapshot(.netease)
        wy["__private_netease_cloud_scope"] = "different-scope"
        XCTAssertThrowsError(try parse(wy, .netease))
        XCTAssertThrowsError(try parse(["__private_netease_session": "MUSIC_U=synthetic-only"], .netease))
        var kg = MusicCredentialImportFixture.snapshot(.kugou)
        kg.removeValue(forKey: "__private_kugou_module")
        XCTAssertThrowsError(try parse(kg, .kugou))
        kg = MusicCredentialImportFixture.snapshot(.kugou)
        kg["__private_kugou_session"] = ["scope": "synthetic-scope",
            "cookie": ["userid": "42", "token": "synthetic-key", "KUGOU_API_MID": "1"]]
        XCTAssertThrowsError(try parse(kg, .kugou))
        kg = MusicCredentialImportFixture.snapshot(.kugou)
        kg.removeValue(forKey: "__private_kugou_device")
        XCTAssertEqual(try parse(kg, .kugou).deviceState, .requiresVerification)
    }
    func testJSONImportAcceptsRepeatedKeysAndUnusedMetadata() throws {
        for text in [#"{"uid":1,"uid":2}"#, #"{"uid":1,"\u0075id":2}"#] {
            let object = try MusicCredentialImportJSON.object(Data(text.utf8))
            XCTAssertEqual(object["uid"] as? Int, 2)
        }
        let raw = #"{"说明":{"score":1.25,"exponent":1e400,"id":18446744073709551617}}"#
        XCTAssertNotNil(try MusicCredentialImportJSON.object(Data(raw.utf8))["说明"])
    }
    func testFinalImportedIdentityAndCredentialWinButMustStillMatch() throws {
        let raw = #"{"format":"meowfilm.music.credentials","version":1,"source":"meowmusic","platform":"tx","accountId":"99","accountId":42,"credentials":{"musicid":"99","musicid":42,"musickey":null,"musickey":"synthetic-final-key"},"device":{}}"#
        let result = try MusicCredentialImportParser.parse(Data(raw.utf8), platform: .qq, format: .meowMusicRecord)
        XCTAssertEqual(result.nativeID, "42")
        let credential = try MusicQQWire.decode(result.credential.dataRepresentation())
        XCTAssertEqual((credential["auth"] as? [String: Any])?["musickey"] as? String, "synthetic-final-key")
        for invalid in [raw.replacingOccurrences(of: #""accountId":42"#, with: #""accountId":43"#),
                        raw.replacingOccurrences(of: #""musicid":42"#, with: #""musicid":43"#),
                        raw.replacingOccurrences(of: #""musickey":null,"musickey":"synthetic-final-key""#,
                                                 with: #""musickey":"synthetic-final-key","musickey":null"#)] {
            XCTAssertThrowsError(try MusicCredentialImportParser.parse(Data(invalid.utf8),
                platform: .qq, format: .meowMusicRecord))
        }
    }
    func testStringifiedSnapshotUsesFinalMembersAndIgnoresLargeUnusedProfileMetadata() throws {
        var root = MusicCredentialImportFixture.snapshot(.netease)
        root["__private_netease_profile_v1"] =
            #"{"scope":"synthetic-scope","loggedIn":false,"loggedIn":true,"profile":{"userId":99,"\u0075serId":42,"unused":1e400}}"#
        let parsed = try parse(root, .netease)
        XCTAssertEqual(parsed.nativeID, "42")
    }
    func testInvalidJSONAndOversizedImportRemainRejected() throws {
        for text in ["{}{}", "[{}]", "{\"x\":\"\\uD800\"}"] {
            XCTAssertThrowsError(try MusicCredentialImportJSON.object(Data(text.utf8)))
        }
        XCTAssertThrowsError(try MusicCredentialImportJSON.object(Data([0xFF, 0xFE])))
        XCTAssertThrowsError(try MusicCredentialImportInput(data: Data(repeating: 32, count: MusicCredentialImportInput.maximumBytes + 1)))
    }
    func testConsumedAccountIDsRejectFractionalExponentAndUnsafeNumbersWithoutRounding() throws {
        func record(accountID: String, musicID: String) -> Data {
            Data(#"{"format":"meowfilm.music.credentials","version":1,"source":"meowmusic","platform":"tx","accountId":\#(accountID),"credentials":{"musicid":\#(musicID),"musickey":"synthetic-key"},"device":{}}"#.utf8)
        }
        XCTAssertEqual(try MusicCredentialImportParser.parse(record(accountID: "42", musicID: "42"),
                                                             platform: .qq, format: .meowMusicRecord).nativeID, "42")
        for token in ["1.25", "42.0", "4.2e1", "9007199254740991.1", "9007199254740992",
                      "1.00000000000000001", "1e400", "1e-400", "18446744073709551617"] {
            XCTAssertThrowsError(try MusicCredentialImportParser.parse(record(accountID: token, musicID: "42"),
                                                                       platform: .qq, format: .meowMusicRecord), token)
            XCTAssertThrowsError(try MusicCredentialImportParser.parse(record(accountID: "42", musicID: token),
                                                                       platform: .qq, format: .meowMusicRecord), token)
        }
    }
    func testOpaqueInputIsSingleUseRevocableAndPendingRecordRequiresExplicitValidation() throws {
        let input = try MusicCredentialImportFixture.input()
        XCTAssertFalse(String(reflecting: input).contains("synthetic-import-key"))
        XCTAssertFalse(String(describing: input.customMirror.children.map(\.value)).contains("synthetic-import-key"))
        _ = try input.consume(); XCTAssertThrowsError(try input.consume())
        let second = try MusicCredentialImportFixture.input(); second.revoke(); XCTAssertThrowsError(try second.consume())
        let original = try MusicCredentialImportFixture.material().credential
        let pending = try MusicCredentialImportStored.pending(original, platform: .qq)
        XCTAssertThrowsError(try MusicCredentialImportStored.forValidation(pending, platform: .qq, explicit: false)) {
            XCTAssertEqual($0 as? MusicServiceError, .verificationRequired)
        }
        XCTAssertEqual(try MusicCredentialImportStored.forValidation(pending, platform: .qq, explicit: true), original)
        XCTAssertThrowsError(try MusicCredentialImportStored.forValidation(pending, platform: .netease, explicit: true))
    }
    private func parse(_ value: [String: Any], _ platform: MusicAccountPlatform) throws -> MusicCredentialImportMaterial {
        try MusicCredentialImportParser.parse(JSONSerialization.data(withJSONObject: value), platform: platform, format: .iwebPlusSnapshot)
    }
}

private actor MusicCredentialImportNoHTTP: MusicAccountHTTPTransport {
    private(set) var calls = 0
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        calls += 1; throw MusicServiceError.unavailable
    }
}

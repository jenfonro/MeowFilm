import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicKugouProtocolTests: XCTestCase {
    func testRawResponseFinalStatusControlsBusinessResult() throws {
        let good = try MusicQQWire.decode(Data(#"{"status":0,"status":1,"error_code":20018,"error_code":0,"unused":1e400}"#.utf8))
        XCTAssertNoThrow(try MusicKugouWire.business(good, route: .profile))
        let denied = try MusicQQWire.decode(Data(#"{"status":1,"error_code":0,"error_code":20018}"#.utf8))
        XCTAssertThrowsError(try MusicKugouWire.business(denied, route: .profile)) {
            XCTAssertEqual($0 as? MusicServiceError, .authRequired)
        }
    }

    func testSignaturesUseExactUTF8BodyAndWebSalt() throws {
        let query = ["z": "汉字", "a": "1", "mid": "123"]
        let body = Data("{\"value\":\"<&\"}".utf8)
        // cc22a7e/client_test.go goldens, read only, not executed.
        XCTAssertEqual(MusicKugouCrypto.signature(query, body: body), "67425f7d9933791ed6de20c390829080")
        XCTAssertEqual(MusicKugouCrypto.signature(query, body: body, web: true), "bea22ba4a96e6ab277abd322fa948f8c")
        var signed = query; signed["signature"] = "ignored"
        XCTAssertEqual(MusicKugouCrypto.signature(signed, body: body), MusicKugouCrypto.signature(query, body: body))
        XCTAssertNotEqual(MusicKugouCrypto.signature(query, body: Data("{ \"value\":\"<&\"}".utf8)), MusicKugouCrypto.signature(query, body: body))
        let encoded = try MusicKugouWire.json(["z": "\u{2028}\u{2029}", "a": "<&/"])
        XCTAssertEqual(String(decoding: encoded, as: UTF8.self), "{\"a\":\"<&/\",\"z\":\"\\u2028\\u2029\"}")
        let fields = ["token": "synthetic+a b/&=", "z": "汉字"]
        let url = try MusicKugouWire.query("https://gateway.kugou.com/v5/url", fields)
        XCTAssertEqual(try MusicKugouFixture.fields(.init(url: url)), fields)
        XCTAssertTrue(url.absoluteString.contains("synthetic%2Ba+b%2F%26%3D"))
    }
    func testRawRSARightPaddingGoldenAndRange() throws {
        let plain = Data("{\"clienttime\":1800000000,\"token\":\"synthetic-token\"}".utf8)
        let block = try MusicKugouCrypto.rawBlock(plain)
        XCTAssertEqual(block.count, 128); XCTAssertEqual(Data(block.prefix(plain.count)), plain)
        XCTAssertTrue(block.dropFirst(plain.count).allSatisfy { $0 == 0 })
        // Independently calculated with Python int.from_bytes / pow, NOT a reference runner.
        XCTAssertEqual(try MusicKugouCrypto.rsaRaw(plain),
            "1e6dc8a3717d584895186f901a34bcd4b5725006ac5ff047b08d9387d8e8d72087635f8d6b4369ffaf7972f5d362b7834d06a041c949590dca18bf29c8e96f16699f23932901322571870444de9af2d42458338529920b8d53bcd23608c59132c560ac6bf7a19b897deb55a7b6a403b9cdfa49fcc410754c500c4fa44e5ca47a")
        XCTAssertThrowsError(try MusicKugouCrypto.rawBlock(Data(repeating: 0xff, count: 128)))
        XCTAssertThrowsError(try MusicKugouCrypto.rsaRaw(Data(repeating: 1, count: 129)))
    }
    func testCBCGoldenLegacyBase64AndPKCS1NotRaw() throws {
        let plain = Data("{\"hello\":\"合成<&\"}".utf8)
        let cipher = try MusicKugouCrypto.aes(plain, key: "0123456789abcdef", iv: "fedcba9876543210")
        // Independent .NET AES-CBC/PKCS7 synthetic vector.
        XCTAssertEqual(cipher.map { String(format: "%02x", $0) }.joined(), "27fa1f5ac12ce3a61a00fc2d6d65daa6a243c921ebee0de90b3cdd3d78f3ddd7")
        XCTAssertEqual(try MusicKugouCrypto.aes(cipher, key: "0123456789abcdef", iv: "fedcba9876543210", decrypt: true), plain)
        XCTAssertThrowsError(try MusicKugouCrypto.aes(Data([1]), key: "0123456789abcdef", iv: "fedcba9876543210", decrypt: true))
        let envelope = try MusicKugouCrypto.registration(["hello": "合成<&"], seed: "abcdef")
        let key = MusicKugouCrypto.md5("abcdef"), encrypted = try XCTUnwrap(Data(base64Encoded: envelope.body))
        XCTAssertEqual(try MusicKugouCrypto.aes(encrypted, key: String(key.prefix(16)), iv: String(key.suffix(16)), decrypt: true), plain)
        XCTAssertEqual(envelope.p.count, 256); XCTAssertEqual(envelope.p, envelope.p.uppercased())
        let next = try MusicKugouCrypto.registration(["hello": "合成<&"], seed: "abcdef")
        XCTAssertEqual(next.body, envelope.body); XCTAssertNotEqual(next.p, envelope.p) // PKCS#1 randomized padding.
    }
    func testDeviceDecimalIdentityRestoreAndAliasConflicts() throws {
        let device = try MusicKugouDevice(saved: ["seed": MusicKugouFixture.seed])
        XCTAssertEqual(device.mid, "176905054734845483393488264506404844848")
        XCTAssertEqual(device.dev, "0123456789"); XCTAssertEqual(device.mac, "02:00:00:00:00:00")
        XCTAssertEqual(try MusicKugouDevice(saved: device.fields).mid, device.mid)
        XCTAssertThrowsError(try MusicKugouDevice(saved: ["seed": MusicKugouFixture.seed, "mid": "12"]))
        XCTAssertThrowsError(try MusicKugouDevice(saved: ["seed": MusicKugouFixture.seed, "KUGOU_API_GUID": String(repeating: "f", count: 32)]))
        XCTAssertThrowsError(try MusicKugouDevice(saved: ["seed": MusicKugouFixture.seed, "dfid": "bad\r\nx"]))
        let a = try MusicKugouDevice(), b = try MusicKugouDevice()
        XCTAssertNotEqual(a.seed, b.seed)
    }
    func testRouteMethodAndRouterPairsAreClosed() throws {
        for route in MusicKugouRoute.allCases {
            let url = try XCTUnwrap(URL(string: "https://" + route.host + route.path))
            let headers = route.router.map { ["x-router": $0] } ?? [:]
            XCTAssertTrue(MusicKugouRoute.permits(.init(url: url, method: route.method, headers: headers)))
            XCTAssertFalse(MusicKugouRoute.permits(.init(url: url, method: route.method == "GET" ? "POST" : "GET", headers: headers)))
            XCTAssertFalse(MusicKugouRoute.permits(.init(url: url, method: route.method, headers: ["x-router": "attacker.invalid"])))
            if let router = route.router {
                XCTAssertFalse(MusicKugouRoute.permits(.init(url: url, method: route.method, headers: ["x-router": router, "X-Router": router])))
            }
        }
        for url in ["http://gateway.kugou.com/v5/url", "https://gateway.kugou.com.evil.invalid/v5/url",
                    "https://gateway.kugou.com/v7/send_mobile_code", "https://gateway.kugou.com/v5/login_by_token",
                    "https://gateway.kugou.com/v2/delete_list", "https://127.0.0.1/v5/url"] {
            XCTAssertFalse(MusicKugouRoute.permits(.init(url: URL(string: url)!, headers: ["x-router": "trackercdn.kugou.com"])))
        }
    }
    func testErrorCodesAndScalarTypesAreNotTruthySuccess() throws {
        for (code, error) in [(20018, MusicServiceError.authRequired), (20028, .verificationRequired)] {
            XCTAssertThrowsError(try MusicKugouWire.business(["status": 1, "error_code": String(code)], route: .profile)) {
                XCTAssertEqual($0 as? MusicServiceError, error)
            }
        }
        XCTAssertThrowsError(try MusicKugouWire.business(["status": true], route: .profile))
        XCTAssertThrowsError(try MusicKugouWire.business(["status": 1, "ssaCode": "verification"], route: .profile))
        XCTAssertThrowsError(try MusicKugouWire.business(["errcode": 0], route: .profile))
        XCTAssertNoThrow(try MusicKugouWire.business(["errcode": "0"], route: .members))
        XCTAssertThrowsError(try MusicKugouAuth(["userid": 42, "token": "synthetic\r\nsecret"]))
    }
}

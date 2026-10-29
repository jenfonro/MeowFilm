import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

/// Protocol fixtures, not evidence that the official service returned these
/// failures on a device. QR/SMS success semantics do not apply to public audio.
final class MusicNeteaseFailureMatrixTests: XCTestCase {
    func testBusinessCodeRequiresFiniteIntegralJSONNumberNotBooleanOrString() throws {
        for value in [Any](arrayLiteral: true, false, 200.5, "200", NSNull(), 100_000, -100_000) {
            for nested in [false, true] {
                let root: [String: Any] = nested ? ["code": 200, "data": ["code": value]] : ["code": value]
                XCTAssertThrowsError(try MusicNeteaseWire.response(MusicNeteaseFixture.response(root))) {
                    XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
                }
            }
        }
        for value in [Double.nan, .infinity, -.infinity] {
            XCTAssertThrowsError(try MusicNeteaseWire.code(NSNumber(value: value))) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
            }
        }
        XCTAssertEqual(try MusicNeteaseWire.code(NSNumber(value: -1)), -1)
        XCTAssertEqual(try MusicNeteaseWire.code(NSNumber(value: 200.0)), 200)
    }

    func testRootAndNestedFailureMatrixAndHTTPPrecedence() throws {
        let matrix: [(Int, MusicServiceError)] = [
            (301, .authRequired), (401, .authRequired), (403, .requestRejected), (404, .notFound),
            (429, .rateLimited), (509, .rateLimited), (250, .verificationRequired),
            (460, .verificationRequired), (461, .verificationRequired), (-1, .upstreamFailed),
            (500, .upstreamFailed), (999, .upstreamFailed)
        ]
        for (code, expected) in matrix {
            for nested in [false, true] {
                let root: [String: Any] = nested ? ["code": 200, "data": ["code": code]] : ["code": code]
                XCTAssertThrowsError(try MusicNeteaseWire.response(MusicNeteaseFixture.response(root))) {
                    XCTAssertEqual($0 as? MusicServiceError, expected, "code \(code), nested \(nested)")
                }
            }
        }
        for code in [0, 200] {
            XCTAssertNoThrow(try MusicNeteaseWire.response(
                MusicNeteaseFixture.response(["code": 200, "data": ["code": code]])))
        }
        let data = try MusicQQWire.json(["code": 301])
        for (http, expected) in [(403, MusicServiceError.requestRejected), (429, .rateLimited), (503, .upstreamFailed)] {
            XCTAssertThrowsError(try MusicNeteaseWire.response(.init(status: http, data: data))) {
                XCTAssertEqual($0 as? MusicServiceError, expected)
            }
        }
    }

    func testQRAndSMSCodesAreScopedToTheirEndpoints() throws {
        for code in 800...803 {
            let response = try MusicNeteaseFixture.response(["code": code])
            XCTAssertNoThrow(try MusicNeteaseWire.response(response, qr: true))
            XCTAssertThrowsError(try MusicNeteaseWire.response(response)) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamFailed)
            }
        }
        for path in ["/api/sms/captcha/sent", "/api/w/login/cellphone"] {
            for code in [405, 429, 509] {
                XCTAssertThrowsError(try MusicNeteaseWire.response(
                    MusicNeteaseFixture.response(["code": code]), smsPath: path)) {
                    XCTAssertEqual($0 as? MusicServiceError, .rateLimited)
                }
            }
            for code in [400, 502, 503] {
                XCTAssertThrowsError(try MusicNeteaseWire.response(
                    MusicNeteaseFixture.response(["code": code]), smsPath: path)) {
                    XCTAssertEqual(($0 as? MusicSMSFailure)?.detail,
                        path == "/api/sms/captcha/sent" ? .invalidPhone : .invalidCode)
                }
            }
        }
    }

    func testMalformedJSONEncryptedOrCompressedPayloadNeverLooksLikeLogin() {
        for data in [Data(), Data("not-json".utf8), Data([0x1f, 0x8b, 0x08]),
                     Data(repeating: 0, count: 16), Data("[200]".utf8), Data("{\"success\":true}".utf8)] {
            XCTAssertThrowsError(try MusicNeteaseWire.response(.init(status: 200, data: data))) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
            }
        }
    }
}

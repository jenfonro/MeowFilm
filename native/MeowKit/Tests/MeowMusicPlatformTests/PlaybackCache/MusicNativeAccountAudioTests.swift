import Foundation
import XCTest
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

final class MusicNativeAccountAudioTests: XCTestCase {
    private func candidate(expiry: JSONValue? = .null) -> [String: JSONValue] {
        var value: [String: JSONValue] = [
            "url": .string("https://media.example.invalid/synthetic.flac"),
            "qualityVerified": .bool(false)
        ]
        value["expiresAt"] = expiry
        return value
    }

    func testExplicitNullPreservesUnknownURLLifetimeWithoutInventingTTL() throws {
        let audio = try MusicPlatformJSON.nativeAccountAudio(candidate())
        XCTAssertNil(audio.expiresAt)
        XCTAssertTrue(audio.headers.isEmpty)
        XCTAssertEqual(audio.url.absoluteString, "https://media.example.invalid/synthetic.flac")
    }

    func testQQAndNeteaseNumericMillisecondsKeepExistingDecoding() throws {
        for milliseconds in [0.0, 1_700_000_000_250.0, Double(MusicPlatformJSON.maximumInteger)] {
            let audio = try MusicPlatformJSON.nativeAccountAudio(candidate(expiry: .number(milliseconds)))
            XCTAssertEqual(try XCTUnwrap(audio.expiresAt).timeIntervalSince1970, milliseconds / 1000)
        }
    }

    func testMissingAndMalformedExpiryRemainErrorsNotUnknownLifetime() {
        let invalid: [JSONValue?] = [
            nil, .string("1700000000000"), .bool(false), .array([]), .object([:]),
            .number(-1), .number(.nan), .number(.infinity), .number(-.infinity),
            .number(Double(MusicPlatformJSON.maximumInteger) + 1)
        ]
        for value in invalid {
            XCTAssertThrowsError(try MusicPlatformJSON.nativeAccountAudio(candidate(expiry: value))) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidResponse)
            }
        }
    }

    func testNullableExpiryDoesNotAllowMediaHeadersOrVerificationClaims() {
        let headers: [JSONValue] = [.null, .object([:]), .object(["X-Synthetic": .string("safe")])]
        for header in headers {
            var value = candidate()
            value["headers"] = header
            XCTAssertThrowsError(try MusicPlatformJSON.nativeAccountAudio(value)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidMediaCandidate)
            }
        }
        let invalid: [JSONValue?] = [nil, .null, .bool(true), .string("false"), .number(0)]
        for verified in invalid {
            var value = candidate()
            value["qualityVerified"] = verified
            XCTAssertThrowsError(try MusicPlatformJSON.nativeAccountAudio(value)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidMediaCandidate)
            }
        }
    }
}

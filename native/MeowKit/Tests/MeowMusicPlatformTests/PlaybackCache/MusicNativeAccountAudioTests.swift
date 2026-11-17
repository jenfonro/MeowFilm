import Foundation
import MeowRuntime
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicNativeAccountAudioTests: XCTestCase {
    private func candidate(expiry: RuntimeValue? = .null) -> RuntimeValue {
        var value: RuntimeObject = ["url": .string("https://media.example.invalid/synthetic.flac")]
        value["expiresAt"] = expiry
        return .object(value)
    }

    func testAbsentAndNullLifetimeStayUnknownWithoutInventingTTL() throws {
        for expiry: RuntimeValue? in [nil, .null] {
            let audio = try MusicNativeAccountURLCache.audio(candidate(expiry: expiry))
            XCTAssertNil(audio.expiresAt); XCTAssertTrue(audio.headers.isEmpty)
            XCTAssertEqual(audio.url.absoluteString, "https://media.example.invalid/synthetic.flac")
        }
    }

    func testCanonicalMillisecondsRemainExactAtTheConsumer() throws {
        for milliseconds in [1_700_000_000_250.0, 1_800_000_000_125.0] {
            let audio = try MusicNativeAccountURLCache.audio(candidate(expiry: .number(milliseconds)))
            XCTAssertEqual(try XCTUnwrap(audio.expiresAt).timeIntervalSince1970, milliseconds / 1000)
        }
    }

    func testUnconsumedFieldsDoNotBecomeMediaGatesOrTransportHeaders() throws {
        for extra: RuntimeValue in [.null, .bool(true), .object(["Cookie": .string("synthetic-secret")])] {
            guard case .object(var fields) = candidate() else { return XCTFail("Fixture") }
            fields["headers"] = extra; fields["qualityVerified"] = extra; fields["unused"] = extra
            let audio = try MusicNativeAccountURLCache.audio(.object(fields))
            XCTAssertTrue(audio.headers.isEmpty)
            XCTAssertFalse(audio.fileCachePolicy.allowed)
        }
    }

    func testActualQualityAndFullResourceFactsControlDeliveryWithoutMembershipRequests() throws {
        let value: RuntimeValue = .object(["url": .string("https://media.example.invalid/full.flac"),
            "expiresAt": .null, "full": .bool(true), "actualQuality": .string("flac"),
            "bitrate": .number(900_000), "sampleRate": .number(44_100), "duration": .number(180)])
        let audio = try MusicNativeAccountURLCache.audio(value)
        XCTAssertTrue(audio.fileCachePolicy.allowed)
        XCTAssertEqual(audio.delivery?.quality, "flac"); XCTAssertEqual(audio.duration, 180)
    }

    func testMissingURLAndCredentialBearingOrNonMediaLocationsRemainFailures() {
        for value: RuntimeValue in [.object([:]), .object(["url": .number(123)])] {
            XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(value))
        }
        for address in ["file:///private/file.mp3", "https://user:secret@media.invalid/a",
                        "https://media.invalid/a#fragment", "https://media.invalid/a?MUSIC_U=synthetic",
                        "https://media.invalid/a?refresh_token=synthetic"] {
            XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(.object(["url": .string(address)]))) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidMediaCandidate)
            }
        }
    }
}

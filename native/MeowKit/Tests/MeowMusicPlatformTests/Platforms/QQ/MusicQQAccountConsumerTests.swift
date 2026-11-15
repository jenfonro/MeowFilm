import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicQQAccountConsumerTests: XCTestCase {
    func testCanonicalAuthenticationRejectionExpiresOriginalScopeButOtherFailuresKeepCredential() async throws {
        for (code, expected, removed) in [("AUTH_EXPIRED", MusicServiceError.sessionExpired, true),
            ("RATE_LIMITED", .rateLimited, false), ("NETWORK_ERROR", .connectionFailed, false),
            ("INVALID_RESPONSE", .upstreamInvalid, false), ("VERIFICATION_REQUIRED", .verificationRequired, false)] {
            let f = try MusicNativeFixture(); try await f.enable()
            let original = f.store.peek(f.identity)
            await f.platform.fail("account.verify", code: code)
            do { _ = try await f.service.validate(platform: .qq); XCTFail("Failed authentication became signed in") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            if removed { XCTAssertNil(f.store.peek(f.identity)) }
            else { XCTAssertEqual(f.store.peek(f.identity), original) }
            let statuses = try await f.service.statuses()
            XCTAssertFalse(statuses.contains { $0.phase == .signedIn })
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.map(\.operation), ["account.verify"])
            _ = await f.service.close()
        }
    }

    func testMissingOptionalProfileAndRightsDoNotPreemptCurrentAudioOptionsOrActualURLLifetime() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.platform.replies("account.verify", .object(["userId": .string("101"),
            "cookie": try MusicAccountCredential.read(MusicAccountFixture.credential())[music: "cookie"]]))
        await f.platform.replies("audio", .object(["url": .string("https://media.invalid/current.mp3?vkey=capability"),
            "actualQuality": .string("128k"), "full": .bool(true),
            "expiresAt": .number(f.clock.now().addingTimeInterval(120).timeIntervalSince1970 * 1000)]))
        let options = try await f.service.nativeOptions(platform: .qq, nativeID: "canonical-mid", sourceKey: "tx:canonical-mid")
        XCTAssertEqual(options.first?.qualities, ["128k", "320k", "flac"])
        let result = try await f.service.nativeResolve(platform: .qq, nativeID: "canonical-mid",
            selection: try XCTUnwrap(options.first).selection(quality: "flac"))
        XCTAssertEqual(result.expiresAt, f.clock.now().addingTimeInterval(120))
        XCTAssertEqual(result.delivery?.quality, "128k"); XCTAssertTrue(result.fileCachePolicy.allowed)
        XCTAssertNil(result.fileCachePolicy.licenseExpiresAt); XCTAssertTrue(result.headers.isEmpty)
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.map(\.operation), ["account.verify", "audio.options", "audio"])
        XCTAssertFalse(calls.contains { $0.operation == "account.profile" })
        _ = await f.service.close()
    }

    func testCanonicalTrialResourceAndParseFailuresNeverReturnAURLOrRetryAnotherQuality() async throws {
        for (code, expected) in [("TRIAL_ONLY", MusicServiceError.previewOnly), ("MEDIA_UNAVAILABLE", .audioUnavailable),
                                 ("QUALITY_UNAVAILABLE", .qualityDisabled), ("INVALID_RESPONSE", .upstreamInvalid)] {
            let f = try MusicNativeFixture(); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .qq, nativeID: "current", sourceKey: "tx:current")
            await f.platform.fail("audio", code: code)
            do {
                _ = try await f.service.nativeResolve(platform: .qq, nativeID: "current",
                    selection: try XCTUnwrap(options.first).selection(quality: "128k"))
                XCTFail("Failed audio returned a candidate")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.filter { $0.operation == "audio" }.count, 1)
            XCTAssertFalse(calls.contains { $0.operation == "account.profile" })
            XCTAssertNotNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
    }

    func testCanonicalDirectoryFailureIsNotEmptySuccessAndUnusedFlagsDoNotBlockAnEmptyCurrentDirectory() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.platform.replies("personalPlaylists", .object(["items": .null]))
        do { _ = try await f.service.nativeCollections(platform: .qq, cursor: nil); XCTFail("Missing consumed items became empty success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        await f.platform.replies("personalPlaylists", .object(["items": .array([]), "more": .bool(false),
            "unused": .object(["futureFlag": .bool(true)])]))
        let empty = try await f.service.nativeCollections(platform: .qq, cursor: nil)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.cursor)
        XCTAssertNotNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testUnissuedAudioGrantDoesNotProbeAnotherRecordingOrRefreshTheAccount() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .qq, nativeID: "current", sourceKey: "tx:current")
        let selection = try XCTUnwrap(options.first).selection(quality: "128k")
        let before = await f.platform.recorded(), credential = f.store.peek(f.identity)
        do { _ = try await f.service.nativeResolve(platform: .qq, nativeID: "different", selection: selection); XCTFail("A grant for another recording was used") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let after = await f.platform.recorded()
        XCTAssertEqual(after.count, before.count); XCTAssertFalse(after.contains { $0.operation == "audio" })
        XCTAssertEqual(f.store.peek(f.identity), credential)
        _ = await f.service.close()
    }
}

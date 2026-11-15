import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicKugouAudioTests: XCTestCase {
    func testCanonicalOptionsAndAudioKeepRequestedAndActualQualitySeparate() async throws {
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "audio.options": return .object(["qualities": .array([.string("128k"), .string("flac"), .string("flac24bit")])])
            case "audio":
                XCTAssertEqual(request.parameters[music: "id"], .string("canonical-hash"))
                return .object(["url": .string("https://media.invalid/current.flac?vkey=synthetic"),
                    "full": .bool(true), "actualQuality": .string("flac"), "duration": .number(180.25),
                    "bitrate": .number(900_000), "sampleRate": .number(44_100)])
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let options = try await provider.capabilities(mid: "canonical-hash")
        XCTAssertEqual(options, ["128k", "flac", "flac24bit"])
        let raw = try await provider.resolve(mid: "canonical-hash", quality: "flac24bit")
        let result = try MusicNativeAccountURLCache.audio(raw)
        XCTAssertEqual(result.delivery?.quality, "flac"); XCTAssertEqual(result.delivery?.evidence, .officialResponse)
        XCTAssertEqual(result.delivery?.bitrate, 900_000); XCTAssertEqual(result.delivery?.sampleRate, 44_100)
        XCTAssertEqual(result.duration, 180.25); XCTAssertNil(result.expiresAt)
        XCTAssertTrue(result.fileCachePolicy.allowed); XCTAssertNil(result.fileCachePolicy.licenseExpiresAt)
        XCTAssertTrue(result.headers.isEmpty)
        XCTAssertEqual(fixture.requests().map(\.operation), ["audio.options", "audio"])
        XCTAssertEqual(fixture.requests().last?.parameters[music: "quality"], .string("flac24bit"))
        try await client.close(); await fixture.runtime.close()
    }

    func testMissingMembershipAndOptionalDeliveryFactsDoNotPreventCanonicalResolution() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        await f.platform.replies("audio", .object(["url": .string("https://media.invalid/current.flac"), "full": .bool(true)]))
        let options = try await f.service.nativeOptions(platform: .kugou, nativeID: "canonical-hash", sourceKey: "kg:canonical-hash")
        let result = try await f.service.nativeResolve(platform: .kugou, nativeID: "canonical-hash",
            selection: try XCTUnwrap(options.first).selection(quality: "flac"))
        XCTAssertNil(result.delivery); XCTAssertNil(result.expiresAt); XCTAssertNil(result.duration)
        XCTAssertNil(result.fileCachePolicy.licenseExpiresAt); XCTAssertTrue(result.fileCachePolicy.allowed)
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.map(\.operation), ["account.verify", "audio.options", "audio"])
        XCTAssertFalse(calls.contains { $0.operation == "account.profile" })
        _ = await f.service.close()
    }

    func testActualCanonicalFailuresNeverFallBackOrBecomeMembershipGuesses() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("QUALITY_UNAVAILABLE", .qualityDisabled), ("MEDIA_UNAVAILABLE", .audioUnavailable),
            ("TRIAL_ONLY", .previewOnly), ("FORBIDDEN", .permissionDenied),
            ("INVALID_RESPONSE", .upstreamInvalid), ("NETWORK_ERROR", .connectionFailed)
        ]
        for (code, expected) in failures {
            let fixture = try MusicPlatformFixture { _ in throw MusicPlatformFixture.Failure.remote(code) }
            let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
            do { _ = try await provider.resolve(mid: "current", quality: "flac"); XCTFail("Failed audio became a playable candidate") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertEqual(fixture.requests().map(\.operation), ["audio"])
            XCTAssertEqual(fixture.requests().first?.parameters[music: "quality"], .string("flac"))
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testActualMediaUseRejectsCredentialExportAndUnusableURLFormsWithoutRestoringHostAllowlist() async throws {
        for url in ["file:///tmp/audio", "https://user@media.invalid/a", "https://media.invalid/a#fragment",
                    "https://media.invalid/a?MUSIC_U=synthetic", "https://media.invalid/a?access_token=synthetic"] {
            let fixture = try MusicPlatformFixture { _ in .object(["url": .string(url), "full": .bool(true)]) }
            let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
            let value = try await provider.resolve(mid: "current", quality: "flac")
            XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(value)) {
                XCTAssertEqual($0 as? MusicServiceError, .invalidMediaCandidate)
            }
            XCTAssertEqual(fixture.requests().count, 1)
            try await client.close(); await fixture.runtime.close()
        }
        let fixture = try MusicPlatformFixture { _ in .object(["url": .string("http://other-cdn.invalid/current.flac?vkey=capability"),
            "full": .bool(true), "headers": .object(["Cookie": .string("not-forwarded")])]) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let result = try MusicNativeAccountURLCache.audio(await provider.resolve(mid: "current", quality: "flac"))
        XCTAssertEqual(result.url.host, "other-cdn.invalid"); XCTAssertTrue(result.headers.isEmpty)
        try await client.close(); await fixture.runtime.close()
    }

    func testLogoutRevokesIssuedAuthorizationAndRejectsAnOldSelectionWithoutAnotherRequest() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        await f.platform.replies("audio", .object(["url": .string("https://media.invalid/current.flac"), "full": .bool(true)]))
        let options = try await f.service.nativeOptions(platform: .kugou, nativeID: "current", sourceKey: "kg:current")
        let selection = try XCTUnwrap(options.first).selection(quality: "flac")
        let result = try await f.service.nativeResolve(platform: .kugou, nativeID: "current", selection: selection)
        XCTAssertEqual(result.playbackAuthorization?.isActive, true)
        try await f.service.logout(platform: .kugou)
        XCTAssertEqual(result.playbackAuthorization?.isActive, false)
        let before = await f.platform.recorded()
        do { _ = try await f.service.nativeResolve(platform: .kugou, nativeID: "current", selection: selection); XCTFail("Old selection restored an account") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let after = await f.platform.recorded(); XCTAssertEqual(after.count, before.count)
        _ = await f.service.close()
    }
}

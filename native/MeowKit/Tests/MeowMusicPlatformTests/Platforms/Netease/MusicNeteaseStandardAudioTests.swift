import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

/// Canonical runtime replies exercise the actual native account/audio owner.
/// Exact EAPI/XEAPI, trial and bitrate parsing vectors belong to Node.
@MainActor final class MusicNeteaseStandardAudioTests: XCTestCase {
    func testGuestStandardUsesCanonicalAudioWithoutLoginOrMembershipAndReusesURL() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        await f.platform.replies("audio", audio(expiresAt: f.clock.now().addingTimeInterval(1800)))
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "canonical-id", sourceKey: "wy:canonical-id")
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.qualities, ["128k"])
        let before = await f.platform.recorded(); XCTAssertTrue(before.isEmpty)
        let result = try await f.service.nativeResolve(platform: .netease, nativeID: "canonical-id", selection: option.selection(quality: "128k"))
        XCTAssertEqual(result.delivery?.quality, "128k"); XCTAssertEqual(result.delivery?.evidence, .officialResponse)
        XCTAssertEqual(result.expiresAt, f.clock.now().addingTimeInterval(1800))
        XCTAssertTrue(result.fileCachePolicy.allowed); XCTAssertTrue(result.headers.isEmpty)
        XCTAssertEqual(result.playbackAuthorization?.isActive, true)
        _ = try await f.service.nativeResolve(platform: .netease, nativeID: "canonical-id", selection: option.selection(quality: "128k"))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["audio"])
        XCTAssertEqual(requests.first?.parameters[music: "id"], .string("canonical-id"))
        XCTAssertEqual(requests.first?.parameters[music: "quality"], .string("128k"))
        XCTAssertNil(f.store.peek(f.identity)); XCTAssertFalse(f.store.calls().contains("write"))
        _ = await f.service.close()
        XCTAssertEqual(result.playbackAuthorization?.isActive, false)
    }

    func testSignedInSelectionConsumesActualQualityWithoutProfileOrMembershipProbe() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        await f.platform.replies("audio", audio(quality: "192k", expiresAt: f.clock.now().addingTimeInterval(120)))
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let selection = try XCTUnwrap(options.first).selection(quality: "flac")
        let result = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: selection)
        XCTAssertEqual(result.delivery?.quality, "192k", "The request tier is not delivery evidence")
        XCTAssertEqual(result.duration, 240.5)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify", "audio.options", "audio"])
        XCTAssertEqual(requests.last?.parameters[music: "quality"], .string("flac"))
        XCTAssertFalse(requests.contains { $0.operation == "account.profile" })
        _ = await f.service.close()
    }

    func testGuestRecoverySurvivesPrunedCandidateButNotNewAccountBinding() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        await f.platform.replies("audio", audio(expiresAt: f.clock.now().addingTimeInterval(1800)))
        let first = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let selection = try XCTUnwrap(first.first).selection(quality: "128k")
        let original = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: selection)
        f.clock.advance(181)
        _ = try await f.service.nativeOptions(platform: .netease, nativeID: "102", sourceKey: "wy:102")
        let renewed = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
        XCTAssertEqual(renewed.first?.selection(quality: "128k"), selection)
        let newURL = "https://media.invalid/renewed.mp3"
        await f.platform.replies("audio", audio(url: newURL, expiresAt: f.clock.now().addingTimeInterval(120)))
        let result = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selection, rejectedURL: original.url)
        XCTAssertEqual(result.url.absoluteString, newURL)
        try await f.service.logout(platform: .netease)
        XCTAssertEqual(original.playbackAuthorization?.isActive, false)
        let before = await f.platform.recorded()
        do {
            _ = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
            XCTFail("Revoked guest selection restored a new session")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let after = await f.platform.recorded(); XCTAssertEqual(after.count, before.count)
        _ = await f.service.close()
    }

    func testOptionalQualityAndTTLStayUnknownInsteadOfBecomingRequestedFacts() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        await f.platform.replies("audio", .object(["url": .string("https://media.invalid/current.mp3"),
            "full": .bool(true), "duration": .number(240.5), "unused": .object(["future": .array([.null])])]))
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let result = try await f.service.nativeResolve(platform: .netease, nativeID: "101",
            selection: try XCTUnwrap(options.first).selection(quality: "128k"))
        XCTAssertNil(result.delivery); XCTAssertNil(result.expiresAt)
        XCTAssertNil(result.fileCachePolicy.licenseExpiresAt)
        XCTAssertTrue(result.fileCachePolicy.allowed); XCTAssertEqual(result.duration, 240.5)
        _ = await f.service.close()
    }

    func testGuestFailuresKeepTheirActualClassificationWithoutRetryOrLogin() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("AUTH_REQUIRED", .authRequired), ("FORBIDDEN", .permissionDenied), ("NOT_FOUND", .notFound),
            ("RATE_LIMITED", .rateLimited), ("VERIFICATION_REQUIRED", .verificationRequired),
            ("MEDIA_UNAVAILABLE", .audioUnavailable), ("TRIAL_ONLY", .previewOnly),
            ("INVALID_RESPONSE", .upstreamInvalid), ("NETWORK_ERROR", .connectionFailed), ("TIMEOUT", .timeout)
        ]
        for (code, expected) in failures {
            let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            await f.platform.fail("audio", code: code)
            do {
                _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101",
                    selection: try XCTUnwrap(options.first).selection(quality: "128k"))
                XCTFail("Canonical failure became a playable candidate")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.map(\.operation), ["audio"])
            XCTAssertNil(f.store.peek(f.identity)); XCTAssertFalse(f.store.calls().contains("write"))
            _ = await f.service.close()
        }
    }

    func testMissingRequiredCanonicalURLIsParseFailureNotLoginOrUnavailable() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        await f.platform.replies("audio", .object(["full": .bool(true)]))
        do {
            _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101",
                selection: try XCTUnwrap(options.first).selection(quality: "128k"))
            XCTFail("Missing URL was accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testCanonicalSessionRejectionExpiresOnlyTheAuthenticatedSession() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        await f.platform.fail("audio", code: "AUTH_EXPIRED")
        do {
            _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101",
                selection: try XCTUnwrap(options.first).selection(quality: "128k"))
            XCTFail("Rejected session remained authorized")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        XCTAssertNil(f.store.peek(f.identity))
        let statuses = try await f.service.statuses()
        XCTAssertFalse(statuses.contains { $0.platform == .netease && $0.phase == .signedIn })
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "audio" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "qr.create" || $0.operation == "account.profile" })
        _ = await f.service.close()
    }

    private func audio(url: String = "https://media.invalid/current.mp3", quality: String = "128k", expiresAt: Date) -> RuntimeValue {
        .object(["url": .string(url), "full": .bool(true), "actualQuality": .string(quality),
            "duration": .number(240.5), "expiresAt": .number(expiresAt.timeIntervalSince1970 * 1000)])
    }
}

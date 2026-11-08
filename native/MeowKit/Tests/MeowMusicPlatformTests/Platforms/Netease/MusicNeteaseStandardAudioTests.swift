import XCTest
import Foundation
import MeowMusicFeature
@testable import MeowMusicPlatform

/// Synthetic official transport only. These assertions are not real-world
/// listening, login, membership or Simulator evidence.
final class MusicNeteaseStandardAudioTests: XCTestCase {
    private let playerPath = "/xeapi/song/enhance/player/url/v1"

    func testGuestOffersStandardWithoutMembershipAndNeverCreatesLogin() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.qualities, ["128k"])
        let before = await f.http.recorded(); XCTAssertTrue(before.isEmpty)
        let audio = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: option.selection(quality: "128k"))
        XCTAssertEqual(audio.delivery?.quality, "128k")
        XCTAssertEqual(audio.delivery?.evidence, .officialResponse)
        XCTAssertEqual(audio.expiresAt, f.clock.now().addingTimeInterval(1800))
        XCTAssertTrue(audio.headers.isEmpty); XCTAssertEqual(audio.playbackAuthorization?.isActive, true)
        let calls = await f.http.recorded()
        XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
        XCTAssertFalse(calls.contains {
            $0.request.url.path.contains("vip") || $0.request.url.path.contains("nuser") ||
                $0.request.url.path.contains("/register/")
        })
        XCTAssertTrue(calls.allSatisfy { !($0.request.headers["Cookie"] ?? "").contains("MUSIC_U") })
        XCTAssertNil(f.store.peek(f.identity)); XCTAssertFalse(f.store.calls().contains("write"))
        // Reuse the scoped URL, not another bootstrap or media request.
        _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: option.selection(quality: "128k"))
        let again = await f.http.recorded(); XCTAssertEqual(again.count, calls.count)
        _ = await f.service.close()
        XCTAssertEqual(audio.playbackAuthorization?.isActive, false)
    }

    func testNonMemberUsesResourceMetadataAndActualResponseWithoutVIPPreflight() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        XCTAssertTrue(options.first?.qualities.contains("flac") == true)
        let standard = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "128k"))
        XCTAssertEqual(standard.delivery?.quality, "128k")
        await f.http.replies(playerPath, [try MusicNeteaseFixture.response(MusicNeteaseFixture.audio(level: "lossless"))])
        let audio = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "flac"))
        XCTAssertEqual(audio.delivery?.quality, "flac")
        let calls = await f.http.recorded()
        XCTAssertFalse(calls.contains { $0.request.url.path.contains("vip/info") })
        XCTAssertTrue(calls.contains { $0.request.url.path.contains("music/detail/get") })
        XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 2)
        _ = await f.service.close()
    }

    func testGuestLongTrackRecoverySurvivesPrunedCandidateButNotNewBinding() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let first = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let selection = first[0].selection(quality: "128k")
        let original = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: selection)
        f.clock.advance(181)
        _ = try await f.service.nativeOptions(platform: .netease, nativeID: "102", sourceKey: "wy:102")
        let renewed = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
        XCTAssertEqual(renewed.first?.selection(quality: "128k"), selection)
        let newURL = "https://m801.music.126.net/synthetic-renewed.mp3"
        await f.http.replies(playerPath, [try MusicNeteaseFixture.response(MusicNeteaseFixture.audio(level: "standard", url: newURL))])
        let audio = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101", selection: selection, rejectedURL: original.url)
        XCTAssertEqual(audio.url.absoluteString, newURL)
        try await f.service.logout(platform: .netease)
        XCTAssertEqual(original.playbackAuthorization?.isActive, false)
        let calls = await f.http.recorded()
        do {
            _ = try await f.service.nativeRecoveryOptions(platform: .netease, nativeID: "101", selection: selection)
            XCTFail("Revoked guest became a new session")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let after = await f.http.recorded(); XCTAssertEqual(calls.count, after.count)
        _ = await f.service.close()
    }

    func testTrialEligibilityIsNotTrialDeliveryAndActualDowngradeIsLabelledTruthfully() async throws {
        for level in ["standard", "unknown-upstream-level"] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            await f.http.replies(playerPath, [try MusicNeteaseFixture.response(
                MusicNeteaseFixture.audio(level: level, trial: ["resConsumable": true, "userConsumable": true]))])
            let audio = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "flac"))
            XCTAssertEqual(audio.delivery?.quality, level == "standard" ? "128k" : nil)
            let calls = await f.http.recorded()
            XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
            _ = await f.service.close()
        }
    }

    func testGuestDenialNeverRetriesAsMemberAndDoesNotIssueAudioAuthorization() async throws {
        let matrix: [(Int, MusicServiceError)] = [
            (301, .authRequired), (401, .authRequired), (403, .requestRejected), (404, .notFound),
            (429, .rateLimited), (509, .rateLimited), (250, .verificationRequired),
            (460, .verificationRequired), (461, .verificationRequired), (-1, .audioUnavailable)
        ]
        for (code, expected) in matrix {
            let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            await f.http.replies(playerPath, [try MusicNeteaseFixture.response(["code": 200, "data": [
                ["id": 101, "code": code, "url": NSNull()]]])])
            do {
                _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "128k"))
                XCTFail("Official denial was ignored")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let calls = await f.http.recorded()
            XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
            XCTAssertFalse(calls.contains { $0.request.url.path.contains("vip") })
            XCTAssertNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
    }

    func testGuestAudioShapeTrialAndMissingURLAreNotMisreportedAsLogin() async throws {
        let base: [String: Any] = ["id": 101, "code": 200, "url": "https://m801.music.126.net/synthetic.mp3",
                                   "expi": 120, "level": "standard"]
        var scenarios: [([String: Any], MusicServiceError)] = []
        for value in [NSNull(), ""] as [Any] {
            var row = base; row["url"] = value; scenarios.append((row, .audioUnavailable))
        }
        var missing = base; missing.removeValue(forKey: "url"); scenarios.append((missing, .audioUnavailable))
        for value in [true, 123, ["address": "synthetic"]] as [Any] {
            var row = base; row["url"] = value; scenarios.append((row, .upstreamInvalid))
        }
        for (key, value) in [("freeTrialInfo", ["start": 0, "end": 30] as Any),
                             ("preview", true as Any), ("trial", 1 as Any)] {
            var row = base; row[key] = value; scenarios.append((row, .previewOnly))
        }
        var malformed = base; malformed["freeTrialInfo"] = "not-a-trial-object"
        scenarios.append((malformed, .upstreamInvalid))
        var changed = base; changed["id"] = 999; scenarios.append((changed, .targetChanged))
        for value in ["200", true, 200.5] as [Any] {
            var row = base; row["code"] = value; scenarios.append((row, .upstreamInvalid))
        }
        for (row, expected) in scenarios {
            let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
            let option = try XCTUnwrap(options.first)
            await f.http.replies(playerPath, [try MusicNeteaseFixture.response(["code": 200, "data": [row]])])
            do {
                _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: option.selection(quality: "128k"))
                XCTFail("Invalid/full-play restriction ignored")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            let calls = await f.http.recorded()
            XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
            XCTAssertFalse(calls.contains { $0.request.url.path.contains("vip") || $0.request.url.path.contains("nuser") })
            XCTAssertNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
    }

    func testAudioRateLimitIsReportedWithoutLocalRetry() async throws {
        for code in [429, 509] {
            for nested in [false, true] {
                let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
                let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
                let body: [String: Any] = nested
                    ? ["code": 200, "data": [["id": 101, "code": code, "url": NSNull()]]]
                    : ["code": code]
                await f.http.replies(playerPath, [try MusicNeteaseFixture.response(body, headers: ["Retry-After": "17"])])
                do {
                    _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "128k"))
                    XCTFail("Rate limit accepted")
                } catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
                let calls = await f.http.recorded()
                XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
                _ = await f.service.close()
            }
        }
    }

    func testValidatedSessionRejectionExpiresOnlyThatSessionNotAnAnonymousRequest() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        XCTAssertNotNil(f.store.peek(f.identity))
        await f.http.replies(playerPath, [try MusicNeteaseFixture.response(["code": 200, "data": [
            ["id": 101, "code": 401, "url": NSNull()]]])])
        do {
            _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: options[0].selection(quality: "128k"))
            XCTFail("Rejected session remained authorized")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        XCTAssertNil(f.store.peek(f.identity))
        let statuses = try await f.service.statuses()
        XCTAssertFalse(statuses.contains { $0.platform == .netease && $0.phase == .signedIn })
        let calls = await f.http.recorded()
        XCTAssertEqual(calls.filter { $0.request.url.path == playerPath }.count, 1)
        XCTAssertFalse(calls.contains { $0.request.url.path.contains("qrcode") })
        _ = await f.service.close()
    }
}

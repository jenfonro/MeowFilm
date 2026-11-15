import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class NativeMusicKugouIntegrationTests: XCTestCase {
    func testQRThroughSecureOwnerCanonicalBrowseOfficialAudioAndLogout() async throws {
        let f = try MusicNativeFixture(platform: .kugou, restored: false)
        try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .kugou)
        XCTAssertEqual(challenge.platform, .kugou)
        XCTAssertEqual(Array(challenge.image.prefix(8)), [137, 80, 78, 71, 13, 10, 26, 10])
        XCTAssertNil(f.store.peek(f.identity))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed)
        let stored = try XCTUnwrap(f.store.peek(f.identity))
        for kind in ["daily", "fm"] {
            let page = try await f.service.nativeRecommendations(platform: .kugou, kind: kind, cursor: nil)
            XCTAssertEqual(page.items.count, 1); XCTAssertNil(page.cursor)
            let item = try XCTUnwrap(page.items.first)
            XCTAssertEqual(item.platform, "kg")
            let row = try RuntimeJSON.decode(item.data)
            XCTAssertEqual(row[music: "songmid"], .string(MusicAccountFixture.mid))
            XCTAssertEqual(row[music: "token"], .null)
        }
        let collections = try await f.service.nativeCollections(platform: .kugou, cursor: nil)
        XCTAssertEqual(collections.items.map(\.id), ["501"])
        XCTAssertTrue(try XCTUnwrap(collections.items.first).owned)
        let tracks = try await f.service.nativeTracks(platform: .kugou, collectionID: "501", cursor: nil)
        XCTAssertEqual(tracks.items.count, 1)
        let options = try await f.service.nativeOptions(platform: .kugou, nativeID: "canonical-hash", sourceKey: "kg:canonical-hash")
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.siteId, "kg"); XCTAssertEqual(option.qualities, ["128k", "320k", "flac"])
        let audio = try await f.service.nativeResolve(platform: .kugou, nativeID: "canonical-hash", selection: option.selection(quality: "flac"))
        XCTAssertEqual(audio.url.host, "media.invalid"); XCTAssertTrue(audio.headers.isEmpty)
        XCTAssertEqual(f.store.peek(f.identity), stored)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "audio" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "account.profile" })
        try await f.service.logout(platform: .kugou)
        XCTAssertNil(f.store.peek(f.identity))
        let status = try await f.service.statuses()
        XCTAssertEqual(status.first?.phase, .signedOut); XCTAssertEqual(status.first?.nickname, "")
        _ = await f.service.close()
    }

    func testKugouOwnerCreateCooldownUsesFiveSecondBoundary() async throws {
        let f = try MusicNativeFixture(platform: .kugou, restored: false)
        try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .kugou)
        await f.service.cancelLogin(challengeID: challenge.id)
        let requestsAfterCreate = await f.platform.recorded(), callsAfterCreate = f.store.calls()
        f.clock.advance(4)
        do { _ = try await f.service.beginLogin(platform: .kugou); XCTFail("Four-second create admitted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let rejectedRequests = await f.platform.recorded()
        XCTAssertEqual(rejectedRequests.count, requestsAfterCreate.count)
        XCTAssertEqual(f.store.calls(), callsAfterCreate); XCTAssertNil(f.store.peek(f.identity))
        f.clock.advance(1)
        let next = try await f.service.beginLogin(platform: .kugou)
        XCTAssertNotEqual(next.id, challenge.id); XCTAssertEqual(next.platform, .kugou)
        XCTAssertEqual(Array(next.image.prefix(8)), [137, 80, 78, 71, 13, 10, 26, 10])
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.cancel", "qr.create"])
        XCTAssertNil(f.store.peek(f.identity))
        await f.service.cancelLogin(challengeID: next.id)
        _ = await f.service.close()
    }

    func testUsableCanonicalLocatorsAndCurrentRecommendationKindsAreNotRejectedByNativeGuesses() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        for id in ["canonical-alias", "900719925474099312", "lower-case-hash"] {
            let tracks = try await f.service.nativeTracks(platform: .kugou, collectionID: id, cursor: nil)
            XCTAssertEqual(tracks.items.count, 1)
            let options = try await f.service.nativeOptions(platform: .kugou, nativeID: id, sourceKey: "kg:" + id)
            XCTAssertFalse(options.isEmpty)
        }
        let page = try await f.service.nativeRecommendations(platform: .kugou, kind: "current-kind", cursor: nil)
        XCTAssertEqual(page.items.count, 1)
        let requests = await f.platform.recorded()
        XCTAssertTrue(requests.contains { $0.operation == "playlist.complete" && $0.parameters[music: "id"] == .string("canonical-alias") })
        XCTAssertTrue(requests.contains { $0.operation == "recommendations" && $0.parameters[music: "kind"] == .string("current-kind") })
        _ = await f.service.close()
    }

    func testRestoreValidationCommitsOnceAndVerificationChallengeDoesNotRewriteCredentials() async throws {
        let f = try MusicNativeFixture(platform: .kugou), original = try XCTUnwrap(f.store.peek(f.identity))
        try await f.enable()
        XCTAssertEqual(f.store.peek(f.identity), original)
        let status = try await f.service.validate(platform: .kugou)
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(status.nickname, "合成昵称")
        let verified = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertEqual(verified.credential, original.credential)
        XCTAssertNotEqual(verified.revision, original.revision)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2)
        await f.platform.fail("account.verify", code: "VERIFICATION_REQUIRED")
        do { _ = try await f.service.validate(platform: .kugou); XCTFail("Verification challenge ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), verified)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2)
        XCTAssertFalse(f.store.calls().contains("remove"))
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .requiresValidation); XCTAssertEqual(rows.first?.nickname, "")
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify", "account.verify"])
        _ = await f.service.close()
    }

    func testRestoreVerificationChallengeNeverCommitsOrDeletesStoredCredentials() async throws {
        let f = try MusicNativeFixture(platform: .kugou), original = try XCTUnwrap(f.store.peek(f.identity))
        try await f.enable(); await f.platform.fail("account.verify", code: "VERIFICATION_REQUIRED")
        do { _ = try await f.service.validate(platform: .kugou); XCTFail("Verification challenge ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        XCTAssertFalse(f.store.calls().contains("remove"))
        _ = await f.service.close()
    }

    func testLockedLoggedOutAndCancelledResolutionsRejectLateURLs() async throws {
        for action in ["lock", "logout", "cancel"] {
            let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
            let options = try await f.service.nativeOptions(platform: .kugou, nativeID: "current", sourceKey: "kg:current")
            let selection = try XCTUnwrap(options.first).selection(quality: "flac")
            let entered = expectation(description: "canonical audio entered")
            let gate = MusicAccountTestGate { entered.fulfill() }
            await f.platform.hold("audio", gate)
            let pending = MusicAccountQueueOwnerCall("old media response") {
                try await f.service.nativeResolve(platform: .kugou, nativeID: "current", selection: selection)
            }
            await fulfillment(of: [entered], timeout: 3)
            if action == "lock" { _ = try await f.service.setAccess(available: false, generation: 2) }
            else if action == "logout" { try await f.service.logout(platform: .kugou) }
            else { pending.cancel() }
            await gate.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            XCTAssertThrowsError(try pending.value()) {
                let failure = $0 as? MusicServiceError
                XCTAssertTrue([MusicServiceError.cancelled, .staleResult, .accessUnavailable, .authRequired, .staleAccess].contains(failure ?? .unavailable))
            }
            if action == "lock" {
                let rows = try await f.service.statuses()
                XCTAssertTrue(rows.allSatisfy { $0.phase == .unavailable && !$0.canLogin && $0.nickname.isEmpty })
            }
            _ = await f.service.close()
        }
    }

    func testSameIDQQGrantAndLogoutCannotReplaceKugouGrant() async throws {
        let clock = MusicAccountTestClock(), store = MusicAccountTestStore()
        let kg = MusicAccountRuntimePeer(clock: clock), qq = MusicAccountRuntimePeer(clock: clock)
        let kgID = try MusicAccountFixture.identity("kg"), qqID = try MusicAccountFixture.identity()
        try store.write(.init(credential: MusicAccountFixture.credential(platform: .kugou)), for: kgID)
        try store.write(.init(credential: MusicAccountFixture.credential()), for: qqID)
        let fixture = try MusicPlatformFixture { request in
            if request.platform == "kg" { return try await kg.respond(request) }
            return try await qq.respond(request)
        }
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.kugou: kgID, .qq: qqID], platformClient: fixture.makeClient(), clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let options = try await service.nativeOptions(platform: .kugou, nativeID: "same-id", sourceKey: "kg:same-id")
        _ = try await service.nativeOptions(platform: .qq, nativeID: "same-id", sourceKey: "tx:same-id")
        try await service.logout(platform: .qq)
        let audio = try await service.nativeResolve(platform: .kugou, nativeID: "same-id",
            selection: try XCTUnwrap(options.first).selection(quality: "flac"))
        XCTAssertEqual(audio.url.host, "media.invalid")
        XCTAssertNotNil(store.peek(kgID)); XCTAssertNil(store.peek(qqID))
        _ = await service.close()
    }
}

import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicNeteaseBusinessTests: XCTestCase {
    func testCurrentCollectionPreservesCanonicalIDsOrderOccurrencesAndArtworkWithoutDoubleRead() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let ids = ["900719925474099312", "canonical-alias", "900719925474099312"]
        let image = "https://images.invalid/album.jpg?param=800y800&quality=90"
        let tracks = try ids.map { id in
            MusicNativeTrackData(platform: "wy", data: try RuntimeJSON.encode(.object([
                "songmid": .string(id), "name": .string("Current title"), "img": .string(image)])))
        }
        await f.platform.replies("playlist.complete", try MusicPlatformFixture.page(tracks, platform: "wy", total: 99))
        let page = try await f.service.nativeTracks(platform: .netease, collectionID: "requested-alias", cursor: nil)
        XCTAssertEqual(page.total, ids.count); XCTAssertNil(page.cursor)
        let rows = try page.items.map { try RuntimeJSON.decode($0.data) }
        XCTAssertEqual(rows.map { $0[music: "songmid"].musicText }, ids)
        XCTAssertTrue(rows.allSatisfy { $0[music: "img"] == .string(image) })
        XCTAssertTrue(rows.allSatisfy { $0[music: "singer"] == .null }, "Optional display metadata is not invented")
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify", "playlist.complete"])
        XCTAssertEqual(requests.last?.parameters[music: "id"], .string("requested-alias"))
        _ = await f.service.close()
    }

    func testAccountQueueReadsAllCanonicalMembersBeyondFormerCollectionThresholdWithOneRead() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let ids = (101..<2102).map(String.init) + ["101"]
        let tracks = try ids.map { try MusicAccountFixture.track($0, platform: "wy") }
        await f.platform.replies("playlist.complete", try MusicPlatformFixture.page(tracks, platform: "wy"))
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        var cursor: String?, actual: [String] = []
        repeat {
            let page = try await f.service.accountQueuePage(lease, kind: .collection(id: "501"), cursor: cursor)
            XCTAssertEqual(page.total, ids.count)
            actual += try page.items.map { try RuntimeJSON.decode($0.data)[music: "songmid"].musicRequiredText() }
            cursor = page.cursor
        } while cursor != nil
        XCTAssertEqual(actual, ids)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "playlist.complete" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "songs" || $0.operation == "audio" })
        await f.service.endAccountQueue(lease); _ = await f.service.close()
    }

    func testDisplayCursorReusesAcceptedCanonicalRowsWithoutHydratingOrRecheckingTheCollection() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let tracks = try (101..<136).map { try MusicAccountFixture.track(String($0), platform: "wy") }
        await f.platform.replies("playlist.complete", try MusicPlatformFixture.page(tracks, platform: "wy"))
        let first = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        XCTAssertEqual(first.items.count, 30); XCTAssertEqual(first.total, 35)
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor)
        let replay = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor)
        XCTAssertEqual(second.items.map(\.data), replay.items.map(\.data))
        XCTAssertEqual(second.items.count, 5); XCTAssertEqual(second.total, 35); XCTAssertNil(second.cursor)
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.filter { $0.operation == "playlist.complete" }.count, 1)
        XCTAssertFalse(calls.contains { $0.operation == "songs" || $0.operation == "audio" })
        try await f.service.logout(platform: .netease)
        let before = await f.platform.recorded()
        do { _ = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: cursor); XCTFail("Retired cursor was reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let after = await f.platform.recorded(); XCTAssertEqual(after.count, before.count)
        _ = await f.service.close()
    }

    func testLateCanonicalCollectionCannotPublishAfterLogout() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let entered = expectation(description: "canonical collection entered")
        let gate = MusicAccountTestGate { entered.fulfill() }
        await f.platform.hold("playlist.complete", gate)
        let pending = MusicAccountQueueOwnerCall("late collection result") {
            try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil)
        }
        await fulfillment(of: [entered], timeout: 3)
        try await f.service.logout(platform: .netease); await gate.release()
        await fulfillment(of: [pending.finished], timeout: 3)
        XCTAssertThrowsError(try pending.value()) {
            XCTAssertTrue([MusicServiceError.staleResult, .cancelled].contains($0 as? MusicServiceError ?? .unavailable))
        }
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testRecommendationCursorHonorsCanonicalExpiryAndFMDoesNotInventContinuation() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let rows = try (101..<136).map { try MusicPlatformFixture.song(MusicAccountFixture.track(String($0), platform: "wy")) }
        await f.platform.replies("daily", .object(["items": .array(rows), "continuation": .null,
            "expiresAt": .number(f.clock.now().addingTimeInterval(10).timeIntervalSince1970 * 1000)]))
        await f.platform.replies("fm", try MusicPlatformFixture.page([MusicAccountFixture.track(platform: "wy")], platform: "wy"))
        let first = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        let second = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: cursor)
        XCTAssertEqual(first.items.count, 30); XCTAssertEqual(second.items.count, 5); XCTAssertNil(second.cursor)
        let fm = try await f.service.nativeRecommendations(platform: .netease, kind: "fm", cursor: nil)
        XCTAssertEqual(fm.items.count, 1); XCTAssertNil(fm.cursor)
        f.clock.advance(11)
        do { _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: cursor); XCTFail("Expired cursor was reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "daily" }.count, 1)
        XCTAssertEqual(requests.filter { $0.operation == "fm" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "audio" })
        _ = await f.service.close()
    }

    func testDirectoryRenameEmptyDirectoryAndMissingOptionalCountAreCurrentFactsNotContentConflicts() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        for title in ["Earlier title", "Current title"] {
            await f.platform.replies("personalPlaylists", .object(["items": .array([.object([
                "id": .string("canonical-list"), "name": .string(title), "owned": .bool(true)])]), "more": .bool(false)]))
            let page = try await f.service.nativeCollections(platform: .netease, cursor: nil)
            XCTAssertEqual(page.items.first?.title, title); XCTAssertNil(page.items.first?.count)
        }
        await f.platform.replies("personalPlaylists", .object(["items": .array([]), "more": .bool(false)]))
        let empty = try await f.service.nativeCollections(platform: .netease, cursor: nil)
        XCTAssertTrue(empty.items.isEmpty); XCTAssertNil(empty.cursor)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "personalPlaylists" }.count, 3)
        _ = await f.service.close()
    }

    func testOptionalCollectionTitleKeepsAllPlatformsDirectoryAndTrackAccessUsable() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let f = try MusicNativeFixture(platform: platform); try await f.enable()
            let names: [RuntimeValue?] = [nil, .null, .string("")]
            for name in names {
                var row: RuntimeObject = ["id": .string("canonical-list"), "owned": .bool(true)]
                if let name { row["name"] = name }
                await f.platform.replies("personalPlaylists", .object([
                    "items": .array([.object(row)]), "more": .bool(false)]))
                let page = try await f.service.nativeCollections(platform: platform, cursor: nil)
                XCTAssertEqual(page.items.count, 1); XCTAssertNil(page.cursor)
                XCTAssertEqual(page.items.first?.id, "canonical-list")
                XCTAssertEqual(page.items.first?.title, ""); XCTAssertEqual(page.items.first?.owned, true)
                XCTAssertNil(page.items.first?.count)
            }
            await f.platform.replies("playlist.complete",
                try MusicPlatformFixture.page([MusicAccountFixture.track(platform: platform.rawValue)], platform: platform.rawValue))
            let tracks = try await f.service.nativeTracks(platform: platform, collectionID: "canonical-list", cursor: nil)
            XCTAssertEqual(tracks.items.count, 1); XCTAssertNil(tracks.cursor)
            let values = try tracks.items.map { try RuntimeJSON.decode($0.data) }
            XCTAssertEqual(values.map { $0[music: "songmid"].musicText }, [MusicAccountFixture.mid])
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.map(\.operation),
                ["account.verify", "personalPlaylists", "personalPlaylists", "personalPlaylists", "playlist.complete"])
            XCTAssertEqual(requests.last?.parameters[music: "id"], .string("canonical-list"))
            XCTAssertFalse(requests.contains { $0.operation == "songs" || $0.operation == "audio" })
            _ = await f.service.close()
        }
    }

    func testParseAndAuthenticationFailuresRemainDistinctAndOnlyAuthenticationRemovesStoredSession() async throws {
        for (code, expected) in [("INVALID_RESPONSE", MusicServiceError.upstreamInvalid), ("NETWORK_ERROR", .connectionFailed), ("AUTH_EXPIRED", .sessionExpired)] {
            let f = try MusicNativeFixture(platform: .netease); try await f.enable()
            await f.platform.fail("playlist.complete", code: code)
            do { _ = try await f.service.nativeTracks(platform: .netease, collectionID: "501", cursor: nil); XCTFail("Actual failure became empty success") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertEqual(f.store.peek(f.identity) == nil, code == "AUTH_EXPIRED")
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.filter { $0.operation == "playlist.complete" }.count, 1)
            _ = await f.service.close()
        }
    }

    func testSameNativeIDAcrossPlatformsDoesNotReplaceGrantOrLogoutAnotherAccount() async throws {
        let clock = MusicAccountTestClock(), store = MusicAccountTestStore()
        let wy = MusicAccountRuntimePeer(clock: clock), qq = MusicAccountRuntimePeer(clock: clock)
        let wyID = try MusicAccountFixture.identity("wy"), qqID = try MusicAccountFixture.identity()
        try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: wyID)
        try store.write(.init(credential: MusicAccountFixture.credential()), for: qqID)
        let fixture = try MusicPlatformFixture { request in
            if request.platform == "wy" { return try await wy.respond(request) }
            return try await qq.respond(request)
        }
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.netease: wyID, .qq: qqID], platformClient: fixture.makeClient(), clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let options = try await service.nativeOptions(platform: .netease, nativeID: "same-id", sourceKey: "wy:same-id")
        _ = try await service.nativeOptions(platform: .qq, nativeID: "same-id", sourceKey: "tx:same-id")
        try await service.logout(platform: .qq)
        let result = try await service.nativeResolve(platform: .netease, nativeID: "same-id",
            selection: try XCTUnwrap(options.first).selection(quality: "flac"))
        XCTAssertEqual(result.url.host, "media.invalid")
        XCTAssertNotNil(store.peek(wyID)); XCTAssertNil(store.peek(qqID))
        _ = await service.close()
    }
}

import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicAccountSubscriptionProtocolTests: XCTestCase {
    func testQQDirectorySplitsSubscriptionsAndDedicatedReadNeverReadsMembers() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicAccountFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let full = try await provider.syncRead(kinds: [.playlist], credential: credential, guard: authorization)
        XCTAssertEqual(full.collections.map(\.id), ["501"]); XCTAssertTrue(full.collections.allSatisfy(\.writable))
        XCTAssertEqual(full.subscriptions.map(\.remoteID), ["502"])
        let first = await http.recorded()
        let members = try first.filter { try MusicAccountTestHTTP.method($0) == "CgiGetDiss" }
        XCTAssertFalse(members.isEmpty)
        for request in members {
            let payload = try MusicQQWire.object(MusicQQWire.object(MusicAccountTestHTTP.fields(request)["req_0"])["param"])
            XCTAssertEqual(try MusicQQWire.id(payload["disstid"]), "501")
        }
        let directory = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        XCTAssertTrue(directory.collections.isEmpty); XCTAssertEqual(directory.subscriptions, full.subscriptions)
        let all = await http.recorded()
        XCTAssertEqual(try all.filter { try MusicAccountTestHTTP.method($0) == "CgiGetDiss" }.count, members.count)
    }

    func testQQSubscribeAndUnsubscribeUsePublicIDAndEncryptedUinNotDirIDOrDelete() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicAccountFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        await http.replies("FavPlaylist", try MusicAccountFixture.rpc(["result": 0, "v_failedPlaylistId": []]))
        await http.replies("CancelFavPlaylist", try MusicAccountFixture.rpc(["result": 0, "v_failedPlaylistId": []]))
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        let before = try XCTUnwrap(snapshot.subscriptions.first)
        let subscribe = try await provider.syncWrite(.init(action: .subscribe, collectionID: "900", name: "Remote",
            directoryRevision: snapshot.revision), credential: credential, guard: authorization)
        let unsubscribe = try await provider.syncWrite(.init(action: .unsubscribe, collectionID: before.remoteID,
            directoryRevision: snapshot.revision, beforeSubscription: before), credential: credential, guard: authorization)
        if case .acknowledged = subscribe {} else { XCTFail("subscription dispatch") }
        if case .acknowledged = unsubscribe {} else { XCTFail("unsubscription dispatch") }
        let requests = await http.recorded()
        let writes = try requests.filter { try ["FavPlaylist", "CancelFavPlaylist"].contains(MusicAccountTestHTTP.method($0)) }
        XCTAssertEqual(try writes.map(MusicAccountTestHTTP.method), ["FavPlaylist", "CancelFavPlaylist"])
        for (index, request) in writes.enumerated() {
            let rpc = try MusicQQWire.object(MusicAccountTestHTTP.fields(request)["req_0"])
            XCTAssertEqual(rpc["module"] as? String, "music.musicasset.PlaylistFavWrite")
            let params = try MusicQQWire.object(rpc["param"])
            XCTAssertEqual(Set(params.keys), ["uin", "v_playlistId"])
            XCTAssertEqual(params["uin"] as? String, "synthetic-euin-101")
            XCTAssertEqual(params["v_playlistId"] as? [Int], index == 0 ? [900] : [502])
        }
        XCTAssertFalse(try requests.contains { try ["DelPlaylist", "CgiGetDiss", "CgiGetTrackInfo", "AddPlaylist"].contains(MusicAccountTestHTTP.method($0)) })
    }

    func testQQWrongOwnershipRelationOrRevokedAccountCannotDispatchSubscription() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicAccountFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        let requests: [MusicLibrarySyncMutation] = [
            .init(action: .subscribe, collectionID: "501", name: "Owned", directoryRevision: snapshot.revision),
            .init(action: .unsubscribe, collectionID: "502", directoryRevision: snapshot.revision,
                  beforeSubscription: .init(remoteID: "502", relationID: "8", name: "合成订阅"))
        ]
        for mutation in requests {
            let result = try await provider.syncWrite(mutation, credential: credential, guard: authorization)
            if case .notDispatched(.revisionConflict) = result {} else { XCTFail("invalid relationship") }
        }
        let before = await http.recorded().count
        authorization.revoke()
        let revoked = try await provider.syncWrite(.init(action: .subscribe, collectionID: "900", name: "Remote",
            directoryRevision: snapshot.revision), credential: credential, guard: authorization)
        if case .notDispatched = revoked {} else { XCTFail("revoked grant") }
        let all = await http.recorded()
        XCTAssertEqual(all.count, before)
        XCTAssertFalse(try all.contains { try ["FavPlaylist", "CancelFavPlaylist", "DelPlaylist"].contains(MusicAccountTestHTTP.method($0)) })
    }

    func testQQFailedTargetDoesNotBecomeAnAcknowledgedSubscription() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let credential = try MusicAccountFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        await http.replies("FavPlaylist", try MusicAccountFixture.rpc(["result": 0, "v_failedPlaylistId": [900]]))
        do {
            _ = try await provider.syncWrite(.init(action: .subscribe, collectionID: "900", name: "Remote",
                directoryRevision: snapshot.revision), credential: credential, guard: authorization)
            XCTFail("failed target")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let requests = await http.recorded()
        XCTAssertEqual(try requests.filter { try MusicAccountTestHTTP.method($0) == "FavPlaylist" }.count, 1)
    }

    func testNeteaseDirectoryNeverReadsSubscribedMembersOrRequestsToken() async throws {
        let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock), tokens = MusicNeteaseTestTokens()
        let provider = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, tokenProvider: tokens)
        let credential = try MusicNeteaseFixture.credential(), authorization = MusicAccountSyncAuthorization {}
        let owned = MusicNeteaseFixture.collection(501, special: 0), subscribed = MusicNeteaseFixture.collection(502, owner: 99, special: 0)
        let favorites = MusicNeteaseFixture.collection(503, special: 5), special = MusicNeteaseFixture.collection(504, special: 10)
        await http.replies("/weapi/user/playlist", [try MusicNeteaseFixture.response([
            "code": 200, "more": false, "playlist": [owned, favorites, special, subscribed]])])
        var detail = owned, favoriteDetail = favorites
        detail["trackIds"] = [["id": 101], ["id": 102]]; detail["tracks"] = [MusicNeteaseFixture.song(), MusicNeteaseFixture.song(102)]
        favoriteDetail["trackIds"] = [["id": 101], ["id": 102]]
        favoriteDetail["tracks"] = [MusicNeteaseFixture.song(), MusicNeteaseFixture.song(102)]
        let ownedResponse = try MusicNeteaseFixture.response(["code": 200, "playlist": detail])
        let favoriteResponse = try MusicNeteaseFixture.response(["code": 200, "playlist": favoriteDetail])
        await http.replies("/eapi/v6/playlist/detail", [ownedResponse, ownedResponse, favoriteResponse, favoriteResponse])
        let full = try await provider.syncRead(kinds: [.favorites, .playlist], credential: credential, guard: authorization)
        XCTAssertEqual(full.collections.map(\.id), ["501", "503"])
        XCTAssertEqual(full.collections.map(\.kind), [.playlist, .favorites])
        XCTAssertTrue(full.collections.allSatisfy(\.writable))
        XCTAssertEqual(full.subscriptions, [.init(remoteID: "502", relationID: "502", name: "合成歌单502", count: 2)])
        let initial = await http.recorded()
        let memberReads = initial.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }
        let memberIDs = try memberReads.map { record in
            let body = try MusicQQWire.decode(XCTUnwrap(record.payload))
            return try MusicQQWire.id(body["id"])
        }
        XCTAssertEqual(Set(memberIDs), ["501", "503"])
        let directory = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
        XCTAssertTrue(directory.collections.isEmpty); XCTAssertEqual(directory.subscriptions, full.subscriptions)
        XCTAssertEqual(directory.revision, full.revision)
        let before = await http.recorded()
        XCTAssertEqual(before.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, memberReads.count)
        XCTAssertTrue(before.dropFirst(initial.count).allSatisfy { $0.request.url.path == "/weapi/user/playlist" })
        let tokenCalls = await tokens.calls()
        XCTAssertEqual(tokenCalls, 0)
    }

    func testNeteaseIgnoredOwnedSpecialListsRemainInBothDirectoryFingerprintsAndStabilityChecks() async throws {
        for subscriptionsOnly in [false, true] {
            let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock)
            let provider = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() })
            let credential = try MusicNeteaseFixture.credential(), authorization = MusicAccountSyncAuthorization {}
            let favorites = MusicNeteaseFixture.collection(), subscribed = MusicNeteaseFixture.collection(502, owner: 99, special: 0)
            let special = MusicNeteaseFixture.collection(503, special: 10)
            var changed = special
            changed["name"] = "更新后的特殊列表"
            let originalResponse = try MusicNeteaseFixture.response([
                "code": 200, "more": false, "playlist": [favorites, special, subscribed]])
            let changedResponse = try MusicNeteaseFixture.response([
                "code": 200, "more": false, "playlist": [favorites, changed, subscribed]])
            func read() async throws -> MusicLibrarySyncSnapshot {
                if subscriptionsOnly {
                    return try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
                }
                return try await provider.syncRead(kinds: [.playlist], credential: credential, guard: authorization)
            }
            await http.replies("/weapi/user/playlist", [originalResponse])
            let original = try await read()
            XCTAssertTrue(original.collections.isEmpty)
            XCTAssertEqual(original.subscriptions.map(\.remoteID), ["502"])
            await http.replies("/weapi/user/playlist", [changedResponse])
            let updated = try await read()
            XCTAssertEqual(updated.collections, original.collections); XCTAssertEqual(updated.subscriptions, original.subscriptions)
            XCTAssertNotEqual(updated.revision, original.revision)
            await http.replies("/weapi/user/playlist", [originalResponse, changedResponse])
            do { _ = try await read(); XCTFail("ignored entry changed during directory read") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            let requests = await http.recorded()
            XCTAssertTrue(requests.allSatisfy { $0.request.url.path == "/weapi/user/playlist" })
        }
    }
}

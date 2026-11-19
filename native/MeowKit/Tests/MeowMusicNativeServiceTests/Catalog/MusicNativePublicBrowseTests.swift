import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

enum MusicNativePublicBrowseFixture {
    static let id = "9007199254740993"
    static let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", title: "Synthetic playlist")
    static func track(_ id: String = id, title: String = "Synthetic song", platform: String = "wy", artwork: String = "") -> MusicNativeTrackData {
        var row = try! MJ.parse(MusicNativeCandidateDiscoveryFixture.track(platform, id: id, title: title, artist: "Artist").data)
        row["albumName"] = .string("Album"); row["img"] = .string(artwork)
        return .init(platform: platform, data: row.bytes)
    }
    static func song(_ id: String = id) -> MJ {
        try! MJ.parse(RuntimeJSON.encode(MusicPlatformFixture.song(track(id))))
    }
    static func page(_ ids: [String], platform: String = "wy", collectionID: String = "41", title: String = "Synthetic playlist",
                     total: Int? = nil, positions: [Int?]? = nil, consumed: Int? = nil, continuation: RuntimeValue = .null) throws -> RuntimeValue {
        let rows = ids.map { track($0, title: "Song " + $0, platform: platform, artwork: "https://artwork.invalid/song-" + $0 + ".jpg") }
        guard case .object(var value) = try MusicPlatformFixture.page(rows, platform: platform, total: total, continuation: continuation) else { throw MusicServiceError.invalidResponse }
        value["id"] = .string(collectionID); value["name"] = .string(title); value["creator"] = .string("Creator")
        value["cover"] = .string("https://artwork.invalid/collection.jpg")
        value["sourcePositions"] = .array((positions ?? ids.indices.map { Optional($0) }).map { $0.map { .number(Double($0)) } ?? .null })
        value["consumed"] = .number(Double(consumed ?? ids.count))
        return .object(value)
    }
}

final class MusicNativePublicBrowseTests: XCTestCase {
    func testWYPlaylistSearchUsesCanonicalCollectionsWithoutSongsScriptsOrAccounts() async throws {
        let peer = NativeMusicResultFixture([.object(["items": .array([.object(["id": .string(MusicNativePublicBrowseFixture.id),
            "name": .string("Synthetic"), "count": .number(1)])]), "total": .number(1)])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let page = try await service.publicPlaylists(platform: .netease, query: "test", cursor: nil)
        XCTAssertEqual(page.items.first?.nativeID, MusicNativePublicBrowseFixture.id); XCTAssertNil(page.cursor)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["search"])
        XCTAssertEqual(calls[0].parameters[music: "kind"].musicText, "playlists")
        await service.close()
    }
    func testWYDetailPreservesCanonicalLongIDsAndRepositoryReferencesInOrder() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id], peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(ids)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertEqual(page.tracks.map { $0.sources[0].nativeId }, ids); XCTAssertEqual(page.members.map(\.sourcePosition), [0, 1])
        XCTAssertTrue(page.tracks.allSatisfy { UUID(uuidString: $0.songId) != nil && $0.ref.kind == .library })
        let songs = try await service.repositoryTracks(songIDs: Set(page.tracks.map(\.songId)))
        XCTAssertEqual(Set(songs.keys), Set(page.tracks.map(\.songId)))
        await service.close()
    }
    func testWYVisiblePagesForwardTheWholeContinuationWithoutRecheckingHead() async throws {
        let token: RuntimeValue = .object(["page": .number(2), "offset": .number(30), "opaque": .string("cursor")])
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page((1...30).map(String.init), total: 35, continuation: token),
            try MusicNativePublicBrowseFixture.page((31...35).map(String.init), total: 35, positions: (30..<35).map { Optional($0) })])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        let second = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(first.tracks.count, 30); XCTAssertEqual(second.tracks.count, 5); XCTAssertNil(second.cursor)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist"])
        XCTAssertEqual(calls[1].parameters[music: "continuation"], token)
        await service.close()
    }
    func testDetailMembersKeepActualPlatformLocatorsWhenTheRepositoryHasAnotherPrimarySource() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17", "17"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let primary = MusicNativePublicBrowseFixture.track("QQ-primary", title: "Song 17", platform: "tx")
        let adopted = try await service.adoptSongs([MusicModel.normalize(primary)], authoritative: true)
        let id = try XCTUnwrap(adopted.first)["songId"].string
        let detail = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertEqual(detail.members.map(\.nativeID), ["17", "17"])
        XCTAssertEqual(detail.members.map(\.sourcePosition), [0, 1])
        XCTAssertEqual(detail.tracks.map(\.songId), [id, id])
        XCTAssertEqual(detail.tracks.map { $0.sources.first?.nativeId }, ["QQ-primary", "QQ-primary"])
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 0); XCTAssertTrue(queue.entries.isEmpty)
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.operation), ["playlist"])
        await service.close()
    }
    func testWYUnavailableMembersRemainAbsentAndCurrentCompleteResultCanPlay() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page([], total: 1, consumed: 1),
            try MusicNativePublicBrowseFixture.page(["17"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let displayed = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertTrue(displayed.tracks.isEmpty); XCTAssertEqual(displayed.total, 1); XCTAssertEqual(displayed.consumed, 1)
        let source = try await service.openPublicQueue(.init(collection: displayed.collection))
        let result = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        XCTAssertEqual(result.total, 1)
        await source.close(); await service.close()
    }
    func testWYSummaryChangesDoNotInvalidateOrdinaryPlayback() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id]
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(ids), try MusicNativePublicBrowseFixture.page(ids, title: "Refreshed radar")])
        let service = try await NativeMusicFixture.service(platform: peer)
        let displayed = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        let source = try await service.openPublicQueue(.init(collection: displayed.collection))
        XCTAssertEqual(source.context.title, "Refreshed radar")
        _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries.map { $0.track.sources[0].nativeId }, ids)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist.complete"])
        await source.close(); await service.close()
    }
    func testWYCurrentMemberOrderAndCountChangesAreConsumedWithoutSnapshotProof() async throws {
        for changed in [["18", "17"], ["17", "19"], ["17"], ["17", "18", "19"]] {
            let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17", "18"]), try MusicNativePublicBrowseFixture.page(changed)])
            let service = try await NativeMusicFixture.service(platform: peer)
            let displayed = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: displayed.collection))
            _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
            let queue = try await service.queue(); XCTAssertEqual(queue.entries.map { $0.track.sources[0].nativeId }, changed)
            await source.close(); await service.close()
        }
    }
    func testWYEmptyVisiblePageAdvancesWithoutInventedSongsAndSavedCollectionKeepsReference() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page([], total: 31, consumed: 30, continuation: .object(["page": .number(2)])),
            try MusicNativePublicBrowseFixture.page(["31"], total: 31, positions: [30]),
            try MusicNativePublicBrowseFixture.page(["31"], total: 31, positions: [30])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let first = try await service.publicDetail(MusicNativePublicBrowseFixture.collection, cursor: nil)
        XCTAssertTrue(first.tracks.isEmpty); XCTAssertEqual(first.consumed, 30)
        let second = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.members.map(\.sourcePosition), [30]); XCTAssertNil(second.cursor)
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        XCTAssertEqual(source.context.unavailableCount, 30)
        let before = try await service.read(), writes = store.snapshot().2
        let saved = try await service.saveRemoteCollection(source: source, name: "Incomplete", intent: .init())
        XCTAssertEqual(saved.networkReference?.remoteID, "41"); XCTAssertEqual(saved.count, 31)
        let after = try await service.read(); XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback)
        XCTAssertEqual((try after.fixtureSongs()), (try before.fixtureSongs())); XCTAssertEqual(store.snapshot().2, writes + 1)
        await source.close(); await service.close()
    }
    func testWYAllUnavailableQueueIsContentUnavailableNotAuthenticationFailure() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page([], total: 1)])
        let service = try await NativeMusicFixture.service(platform: peer)
        await assertMusicFailure(.contentUnavailable) { _ = try await service.openPublicQueue(.init(collection: MusicNativePublicBrowseFixture.collection)) }
        await service.close()
    }
    func testCursorBindsQueryAndAccessLifetimeNotContentOrClock() throws {
        let epoch = UUID(), position = try MusicNativePublicBrowseCursor.read(nil, key: "a", epoch: epoch)
        let token = try position.encode(key: "a", epoch: epoch, offset: 30)
        XCTAssertEqual(try MusicNativePublicBrowseCursor.read(token, key: "a", epoch: epoch).offset, 30)
        XCTAssertThrowsError(try MusicNativePublicBrowseCursor.read(token, key: "b", epoch: epoch))
        XCTAssertThrowsError(try MusicNativePublicBrowseCursor.read(token, key: "a", epoch: UUID()))
    }
    func testRefreshRevokesOpenedSourceAndProtectionClearsRegistrations() async throws {
        let response = try MusicNativePublicBrowseFixture.page(["17"]), peer = NativeMusicResultFixture([response, response])
        let service = try await NativeMusicFixture.service(platform: peer)
        let source = try await service.openPublicQueue(.init(collection: MusicNativePublicBrowseFixture.collection))
        await service.refreshPublicCollection(MusicNativePublicBrowseFixture.collection)
        XCTAssertFalse(source.authorization.isActive)
        let retired = await service.publicQueueSources.count; XCTAssertEqual(retired, 0)
        let reopened = try await service.openPublicQueue(.init(collection: MusicNativePublicBrowseFixture.collection))
        try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(reopened.authorization.isActive)
        let cleared = await service.publicQueueSources.count; XCTAssertEqual(cleared, 0)
        await source.close(); await reopened.close(); await service.close()
    }
    func testKGCanonicalGlobalIDAndMissingLocatorBothRetainCollectionDisplay() async throws {
        let peer = NativeMusicResultFixture([.object(["items": .array([.object(["id": .string("synthetic-global-id"), "name": .string("Synthetic")])]), "total": .number(1)]),
            .object(["items": .array([.object(["name": .string("No locator")])]), "total": .number(1)])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicPlaylists(platform: .kugou, query: "test", cursor: nil)
        XCTAssertEqual(page.items[0].nativeID, "synthetic-global-id")
        let partial = try await service.publicPlaylists(platform: .kugou, query: "test", cursor: nil)
        XCTAssertEqual(partial.items.map(\.title), ["No locator"])
        XCTAssertEqual(partial.items[0].nativeID, "")
        await service.close()
    }
    func testAlbumConsumesActualCanonicalIDAndFullMembers() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page([MusicNativePublicBrowseFixture.id], collectionID: "canonical-album")])
        let service = try await NativeMusicFixture.service(platform: peer)
        let detail = try await service.publicDetail(.init(platform: .netease, nativeID: "alias", kind: .album, title: "Album"), cursor: nil)
        XCTAssertEqual(detail.collection.nativeID, "canonical-album"); XCTAssertEqual(detail.collection.kind, .album)
        XCTAssertEqual(detail.tracks[0].sources[0].nativeId, MusicNativePublicBrowseFixture.id)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["album"])
        await service.close()
    }
}

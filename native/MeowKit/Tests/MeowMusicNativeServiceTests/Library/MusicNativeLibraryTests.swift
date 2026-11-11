import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeLibraryTests: XCTestCase {
    func testUnboundMemberCancellationKeepsOnlyRetainedFavoritesAcrossColdOpen() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let store = try NativeSyncFixture.store([a, b]), service = try await NativeMusicFixture.service(store)
        let refs = try NativeSyncFixture.refs([a, b])
        let initial = try await service.collections()
        let favorites = try XCTUnwrap(initial.first { $0.kind == "favorites" })
        let added = try await service.editPlaylist(favorites, name: nil, refs: refs, order: nil)
        let removed = try await service.editPlaylist(added, name: nil, refs: [refs[1]], order: nil)
        let intents = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["intents"]
        XCTAssertFalse(intents.contains(refs[0].id))
        XCTAssertTrue(intents[refs[1].id]["present"].bool)
        XCTAssertTrue(intents[refs[1].id]["pending"].bool)
        let checkpoint = store.snapshot().0
        do {
            _ = try await service.editPlaylist(added, name: nil, refs: [], order: nil)
            XCTFail("A stale collection must not cancel retained favorites")
        } catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        XCTAssertEqual(store.snapshot().0, checkpoint)
        await service.close()

        let cold = try await NativeMusicFixture.service(store)
        let reopened = try await cold.collections(), restored = try XCTUnwrap(reopened.first { $0.id == favorites.id })
        XCTAssertEqual(restored.revision, removed.revision)
        let tracks = try await cold.tracks(in: restored)
        XCTAssertEqual(tracks.map(\.songId), [refs[1].id])
        let readded = try await cold.editPlaylist(restored, name: nil, refs: refs, order: nil)
        XCTAssertEqual(readded.count, 2)
        let retained = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["intents"]
        XCTAssertTrue(retained[refs[0].id]["present"].bool)
        XCTAssertTrue(retained[refs[0].id]["pending"].bool)
        await cold.close()
    }

    func testLocalPlaylistCreateEditAndFavoritesPersistMoreThanTwoThousandSongs() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let songs = try (0..<2002).map { try NativeMusicFixture.song("BULK\($0)") }
        let ids = songs.map { $0["songId"].string }
        let refs = ids.map { MusicReference(kind: .library, id: $0) }
        try await service.transaction(["library"]) { d in
            var library = d.library["library"]
            library["songs"] = .obj(songs.map { ($0["songId"].string, $0) })
            d.library["library"] = library
        }

        let created = try await service.createPlaylist(name: "Bulk", refs: Array(refs.prefix(2001)))
        XCTAssertEqual(created.count, 2001)
        XCTAssertEqual(created.revision, 1)
        let createdTracks = try await service.tracks(in: created)
        XCTAssertEqual(createdTracks.map(\.songId), Array(ids.prefix(2001)))

        let order = Array(ids.reversed())
        let edited = try await service.editPlaylist(created, name: "Edited bulk", refs: refs, order: order)
        XCTAssertEqual(edited.title, "Edited bulk")
        XCTAssertEqual(edited.count, 2002)
        XCTAssertEqual(edited.revision, created.revision + 1)
        let editedTracks = try await service.tracks(in: edited)
        XCTAssertEqual(editedTracks.map(\.songId), order)

        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let savedFavorites = try await service.editPlaylist(favorites, name: nil, refs: refs, order: order)
        XCTAssertEqual(savedFavorites.count, 2002)
        XCTAssertEqual(savedFavorites.revision, favorites.revision + 1)
        let favoriteTracks = try await service.tracks(in: savedFavorites)
        XCTAssertEqual(favoriteTracks.map(\.songId), order)
        await service.close()

        let cold = try await NativeMusicFixture.service(store)
        let restoredCollections = try await cold.collections()
        let restoredPlaylist = try XCTUnwrap(restoredCollections.first { $0.id == edited.id })
        XCTAssertEqual(restoredPlaylist.title, edited.title)
        XCTAssertEqual(restoredPlaylist.count, 2002)
        XCTAssertEqual(restoredPlaylist.revision, edited.revision)
        let restoredPlaylistTracks = try await cold.tracks(in: restoredPlaylist)
        XCTAssertEqual(restoredPlaylistTracks.map(\.songId), order)
        let restoredFavorites = try XCTUnwrap(restoredCollections.first { $0.id == savedFavorites.id })
        XCTAssertEqual(restoredFavorites.count, 2002)
        XCTAssertEqual(restoredFavorites.revision, savedFavorites.revision)
        let restoredFavoriteTracks = try await cold.tracks(in: restoredFavorites)
        XCTAssertEqual(restoredFavoriteTracks.map(\.songId), order)
        await cold.close()
    }
}

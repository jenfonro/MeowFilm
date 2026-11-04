import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeLibraryTests: XCTestCase {
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

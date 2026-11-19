import XCTest
import MeowMusicFeature
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeLibraryTests: XCTestCase {
    func testLongPlaylistNamesRoundTripThroughTheLibrary() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let name = String(repeating: "曲", count: 3000)
        let created = try await service.createPlaylist(name: name, refs: [])
        XCTAssertEqual(created.title, name)
        let renamed = try await service.editPlaylist(created, name: name + "改", refs: nil, order: nil)
        XCTAssertEqual(renamed.title, name + "改")
        await service.close()
        let reopened = try await NativeMusicFixture.service(store)
        let collections = try await reopened.collections()
        XCTAssertEqual(collections.first { $0.id == created.id }?.title, name + "改")
        await reopened.close()
    }

    func testUnboundMemberCancellationKeepsOnlyRetainedFavoritesAcrossColdOpen() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let store = try NativeSyncFixture.store([a, b]), service = try await NativeMusicFixture.service(store)
        let refs = try await NativeSyncFixture.refs([a, b], service: service)
        let initial = try await service.collections()
        let favorites = try XCTUnwrap(initial.first { $0.kind == "favorites" })
        let added = try await service.changeCollectionMembership(favorites, adding: refs, removing: [])
        let removed = try await service.changeCollectionMembership(added, adding: [], removing: [refs[0]])
        let intents = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["intents"]
        XCTAssertFalse(intents.contains(refs[0].id))
        XCTAssertTrue(intents[refs[1].id]["present"].bool)
        XCTAssertTrue(intents[refs[1].id]["pending"].bool)
        let checkpoint = store.snapshot().0
        do {
            _ = try await service.changeCollectionMembership(added, adding: [], removing: refs)
            XCTFail("A stale collection must not cancel retained favorites")
        } catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        XCTAssertEqual(store.snapshot().0, checkpoint)
        await service.close()

        let cold = try await NativeMusicFixture.service(store)
        let reopened = try await cold.collections(), restored = try XCTUnwrap(reopened.first { $0.id == favorites.id })
        XCTAssertEqual(restored.revision, removed.revision)
        let tracks = try await cold.tracks(in: restored)
        XCTAssertEqual(tracks.map(\.songId), [refs[1].id])
        let readded = try await cold.changeCollectionMembership(restored, adding: [refs[0]], removing: [])
        XCTAssertEqual(readded.count, 2)
        let retained = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["intents"]
        XCTAssertTrue(retained[refs[0].id]["present"].bool)
        XCTAssertTrue(retained[refs[0].id]["pending"].bool)
        await cold.close()
    }

    func testTenThousandFavoritesDeltaAndFullReferenceEditsNeverReadOldMetadata() async throws {
        let store = MembershipReadBudgetStore()
        let service = NativeMusicService(database: store, platformClient: try NativeMusicPlatformFixture().makeClient())
        try await service.setAccess(available: true, generation: 1)
        let songs = try (0..<10_001).map { try NativeMusicFixture.song("MEMBER\($0)") }
        try await service.seedRepositorySongs(songs)
        let refs = songs.map { MusicReference(kind: .library, id: $0["songId"].string) }
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorites, name: nil, refs: Array(refs.prefix(10_000)), order: nil)
        store.budget.reset()
        let updated = try await service.changeCollectionMembership(saved, adding: [refs[10_000]], removing: [])
        XCTAssertEqual(updated.count, 10_001); XCTAssertEqual(updated.revision, saved.revision + 1)
        let members = try await service.collectionReferences(in: updated)
        XCTAssertEqual(members, refs)
        XCTAssertEqual(store.budget.snapshot(), .init())

        store.budget.reset()
        let reversed = Array(refs.reversed())
        let edited = try await service.editPlaylist(updated, name: nil, refs: reversed, order: reversed.map(\.id))
        let created = try await service.createPlaylist(name: "Reference only", refs: refs)
        XCTAssertEqual(edited.revision, updated.revision + 1); XCTAssertEqual(created.count, refs.count)
        XCTAssertEqual(store.budget.snapshot(), .init())
        let editedMembers = try await service.collectionReferences(in: edited)
        XCTAssertEqual(editedMembers, reversed)

        store.budget.reset()
        let unchanged = try await service.changeCollectionMembership(edited, adding: [refs[0]], removing: [])
        XCTAssertEqual(unchanged.revision, edited.revision)
        XCTAssertEqual(store.budget.snapshot(), .init())
        do {
            _ = try await service.changeCollectionMembership(updated, adding: [], removing: [refs[0]])
            XCTFail("Delta must use the requested revision")
        } catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        let stillSaved = try await service.collectionReferences(in: edited)
        XCTAssertEqual(stillSaved, reversed)
        await service.close()
    }

    func testMissingMemberDeltaIsAtomicAndDoesNotCreateSongs() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let song = try NativeMusicFixture.song("EXISTING")
        try await service.seedRepositorySongs([song])
        let refs = [MusicReference(kind: .library, id: song["songId"].string)]
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let before = store.snapshot().0
        do {
            _ = try await service.changeCollectionMembership(favorites,
                adding: refs + [.init(kind: .library, id: UUID().uuidString.lowercased())], removing: [])
            XCTFail("Unknown references must not create songs")
        } catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before)
        await service.close()
    }

    func testLocalPlaylistCreateEditAndFavoritesPersistMoreThanTwoThousandSongs() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let songs = try (0..<2002).map { try NativeMusicFixture.song("BULK\($0)") }
        let ids = songs.map { $0["songId"].string }
        let refs = ids.map { MusicReference(kind: .library, id: $0) }
        try await service.seedRepositorySongs(songs)

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

private final class MembershipReadBudget: @unchecked Sendable {
    struct Counts: Equatable { var metadataReads = 0, songWrites = 0, enumerations = 0, candidateQueries = 0 }
    private let lock = NSLock()
    private var counts = Counts()
    func reset() { lock.lock(); defer { lock.unlock() }; counts = Counts() }
    func snapshot() -> Counts { lock.lock(); defer { lock.unlock() }; return counts }
    func metadata() { lock.lock(); defer { lock.unlock() }; counts.metadataReads += 1 }
    func write() { lock.lock(); defer { lock.unlock() }; counts.songWrites += 1 }
    func enumerate() { lock.lock(); defer { lock.unlock() }; counts.enumerations += 1 }
    func candidates() { lock.lock(); defer { lock.unlock() }; counts.candidateQueries += 1 }
}

private final class MembershipReadBudgetStore: MusicNativeLibraryStoring, @unchecked Sendable {
    let budget = MembershipReadBudget()
    private let base = NativeMusicTestLibrary()
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try base.readSnapshot() }
    func songData(id: String) throws -> Data? { budget.metadata(); return try base.songData(id: id) }
    func containsSong(id: String) throws -> Bool { try base.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { try base.sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { budget.candidates(); return try base.candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { budget.enumerate(); return try base.allSongIDs() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try base.withTransaction(commit: { action in
            try withoutActuallyEscaping(action) { try commit($0) }
        }) { try body(MembershipReadBudgetWriter(base: $0, budget: budget)) }
    }
    func close() throws { try base.close() }
}

private struct MembershipReadBudgetWriter: MusicNativeLibraryWriting {
    let base: any MusicNativeLibraryWriting
    let budget: MembershipReadBudget
    var changedSongIDs: Set<String> { base.changedSongIDs }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try base.readSnapshot() }
    func songData(id: String) throws -> Data? { budget.metadata(); return try base.songData(id: id) }
    func containsSong(id: String) throws -> Bool { try base.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { try base.sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { budget.candidates(); return try base.candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { budget.enumerate(); return try base.allSongIDs() }
    func saveSong(_ data: Data) throws { budget.write(); try base.saveSong(data) }
    func saveArea(_ area: String, value: RuntimeValue) throws { try base.saveArea(area, value: value) }
}

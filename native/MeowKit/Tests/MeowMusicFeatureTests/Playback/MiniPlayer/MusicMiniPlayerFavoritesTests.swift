#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicMiniPlayerFavoritesTests: XCTestCase {
    @MainActor private func setup() async -> (MusicCollectionService, MusicViewModel, MusicMiniPlayerFavorites) {
        let service = MusicCollectionService(rows: [.init(id: "favorites", kind: "favorites", songs: ["a", "b"])])
        let model = MusicViewModel(service: service, player: MusicCollectionPlayer())
        await model.load()
        let favorites = model.favorites
        await favorites.flush()
        return (service, model, favorites)
    }
    private func track(_ id: String, title: String? = nil, sources: [MusicNativeSource] = []) -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: id), songId: id, title: title ?? id, sources: sources)
    }

    @MainActor func testFavoritesUseExistingCollectionAndPrependWithoutChangingPlaybackQueue() async throws {
        let (service, model, favorites) = await setup()
        XCTAssertTrue(favorites.loaded)
        XCTAssertFalse(favorites.isFavorite(track("c")))
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        XCTAssertTrue(favorites.isFavorite(track("c"))) // Pending visual state.
        XCTAssertTrue(favorites.busy)
        XCTAssertNil(favorites.toggle(track("c")))
        await task.value
        XCTAssertEqual(favorites.tracks.map(\.songId), ["c", "a", "b"])
        XCTAssertTrue(favorites.isFavorite(track("c")))
        XCTAssertFalse(favorites.busy)
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(counts.edited.first?.kind, "favorites")
        await model.close()
    }

    @MainActor func testUnfavoriteRemovesOnlyCapturedIdentity() async throws {
        let (service, model, favorites) = await setup()
        let task = try XCTUnwrap(favorites.toggle(track("a")))
        XCTAssertFalse(favorites.isFavorite(track("a")))
        XCTAssertTrue(favorites.isFavorite(track("b")))
        await task.value
        XCTAssertEqual(favorites.tracks.map(\.songId), ["b"])
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.queueWrites, 0)
        await model.close()
    }

    @MainActor func testCancellationBeforeWriteAdmissionDoesNotWriteOrRetargetAnotherSong() async throws {
        let (service, model, favorites) = await setup()
        let entered = expectation(description: "favorite write reached admission")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockEdit(before: gate)
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        await fulfillment(of: [entered], timeout: 2)
        model.setAvailable(false)
        XCTAssertFalse(favorites.loaded)
        XCTAssertTrue(favorites.tracks.isEmpty)
        XCTAssertNil(favorites.toggle(track("other")))
        await gate.release()
        await task.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        await model.close()
    }

    @MainActor func testLateOldReadCannotOverwriteReopenedProtectedFavorites() async throws {
        let (service, model, favorites) = await setup()
        let entered = expectation(description: "old favorites read captured")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockTracks(gate)
        let old = try XCTUnwrap(favorites.reload())
        await fulfillment(of: [entered], timeout: 2)
        model.setAvailable(false)
        await service.replace([.init(id: "favorites", revision: 2, kind: "favorites", songs: ["new"])])
        model.setAvailable(true); await model.load()
        let restored = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { favorites.tracks.map(\.songId) == ["new"] }
        }, evaluatedWith: favorites)
        await fulfillment(of: [restored], timeout: 2)
        XCTAssertEqual(favorites.tracks.map(\.songId), ["new"])
        await gate.release(); await old.value
        XCTAssertEqual(favorites.tracks.map(\.songId), ["new"])
        XCTAssertNil(favorites.error)
        await model.close()
    }

    @MainActor func testSameTitleDoesNotConflateVersionsButExplicitSourceAliasesMatch() {
        let album = track("album", title: "歌曲")
        let live = track("live", title: "歌曲")
        XCTAssertFalse(MusicMiniPlayerFavorites.sameSong(album, live))
        let source = MusicNativeSource(sourceKey: "wy:known", platform: "wy", nativeId: "known")
        XCTAssertTrue(MusicMiniPlayerFavorites.sameSong(
            track("catalog-id", sources: [source]), track("remote-id", sources: [source])))
    }

    @MainActor func testAmbiguousCommitIsReadBackWithoutReplayingTheWrite() async throws {
        let (service, model, favorites) = await setup()
        await service.failCommitAcknowledgement()
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        await task.value
        // Join a fresh read even if automatic reconciliation is still pending.
        await favorites.reload()?.value
        XCTAssertTrue(favorites.isFavorite(track("c")))
        XCTAssertEqual(favorites.error, .commitUnconfirmed)
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.editCalls, 1)
        favorites.clearError()
        XCTAssertNil(favorites.error)
        await model.close()
    }

    @MainActor func testOneRootOwnsFavoritesAndUnrelatedCatalogueOrPlaybackChangesDoNotReadThem() async {
        let (service, model, favorites) = await setup()
        XCTAssertTrue(model.favorites === favorites)
        let before = await service.statistics()
        await model.createPlaylist("普通歌单")
        model.playbackState.state = .init(revision: 1, currentId: "other", total: 1)
        await favorites.flush()
        let after = await service.statistics()
        XCTAssertEqual(after.tracks, before.tracks)
        XCTAssertEqual(favorites.tracks.map(\.songId), ["a", "b"])
        XCTAssertTrue(favorites.loaded)
        await model.close()
    }

    @MainActor func testExternallyRetainedFavoritesDoNotKeepTheirRootAlive() async {
        let service = MusicCollectionService(rows: [.init(id: "favorites", kind: "favorites")])
        var model: MusicViewModel? = MusicViewModel(service: service, player: MusicCollectionPlayer())
        await model?.load()
        let favorites = model!.favorites
        await favorites.flush()
        weak var root = model
        model = nil
        XCTAssertNil(root)
        XCTAssertFalse(favorites.loaded)
        XCTAssertTrue(favorites.tracks.isEmpty)
    }
}
#endif

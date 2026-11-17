#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicMiniPlayerFavoritesTests: XCTestCase {
    @MainActor private func setup(songs: [String] = ["a", "b"]) async ->
        (MusicCollectionService, MusicViewModel, MusicMiniPlayerFavorites) {
        let service = MusicCollectionService(rows: [.init(id: "favorites", kind: "favorites", songs: songs)])
        let model = MusicViewModel(service: service, player: MusicCollectionPlayer())
        await model.load()
        let favorites = model.favorites
        await favorites.flush()
        return (service, model, favorites)
    }
    private func track(_ id: String, title: String? = nil, sources: [MusicNativeSource] = []) -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: id), songId: id, title: title ?? id, sources: sources)
    }

    @MainActor func testFavoritesUseExistingMembershipDeltaWithoutChangingPlaybackQueue() async throws {
        let (service, model, favorites) = await setup()
        XCTAssertTrue(favorites.loaded)
        XCTAssertFalse(favorites.isFavorite(track("c")))
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        XCTAssertTrue(favorites.isFavorite(track("c"))) // Pending visual state.
        XCTAssertTrue(favorites.busy)
        XCTAssertNil(favorites.toggle(track("c")))
        await task.value
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["c", "a", "b"]))
        XCTAssertTrue(favorites.isFavorite(track("c")))
        XCTAssertFalse(favorites.busy)
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.tracks, 0)
        XCTAssertEqual(counts.membershipAdds, [[track("c").ref]])
        XCTAssertEqual(counts.membershipRemovals, [[]])
        XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(counts.edited.first?.kind, "favorites")
        let rows = await service.snapshotRows()
        XCTAssertEqual(rows.first?.tracks.map(\.songId), ["a", "b", "c"])
        await model.close()
    }

    @MainActor func testUnfavoriteRemovesOnlyCapturedIdentity() async throws {
        let (service, model, favorites) = await setup()
        let task = try XCTUnwrap(favorites.toggle(track("a")))
        XCTAssertFalse(favorites.isFavorite(track("a")))
        XCTAssertTrue(favorites.isFavorite(track("b")))
        await task.value
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["b"]))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.membershipAdds, [[]])
        XCTAssertEqual(counts.membershipRemovals, [[track("a").ref]])
        XCTAssertEqual(counts.tracks, 0); XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(counts.queueWrites, 0)
        await model.close()
    }

    @MainActor func testTenThousandFavoritesAddAndRemoveOnlyOneReferenceWithoutTrackReads() async throws {
        let ids = (0..<10_000).map { "saved-\($0)" }
        let (service, model, favorites) = await setup(songs: ids)
        let target = try XCTUnwrap(model.collections.first { $0.kind == "favorites" })
        XCTAssertEqual(favorites.references.count, 10_000)
        await favorites.toggle(track("new"))?.value
        XCTAssertTrue(favorites.isFavorite(track("new")))
        XCTAssertEqual(favorites.references.count, 10_001)
        await favorites.toggle(track("new"))?.value
        await favorites.flush()
        XCTAssertEqual(favorites.references, Set(ids.map { MusicReference(kind: .library, id: $0) }))
        let counts = await service.statistics()
        XCTAssertEqual(counts.tracks, 0)
        XCTAssertEqual(counts.membershipAdds, [[track("new").ref], []])
        XCTAssertEqual(counts.membershipRemovals, [[], [track("new").ref]])
        XCTAssertEqual(counts.edited.map(\.revision), [target.revision, target.revision + 1])
        XCTAssertEqual(counts.writes, 2)
        XCTAssertTrue(counts.requestedRefs.isEmpty); XCTAssertEqual(counts.queueWrites, 0)
        await model.close()
    }

    @MainActor func testQueueOccurrenceUsesPermanentMembershipAcrossChangedDisplayFacts() async throws {
        let (service, model, favorites) = await setup()
        let source = MusicNativeSource(sourceKey: "wy:a", platform: "wy", nativeId: "a")
        let queued = MusicRemoteTrack(ref: .init(kind: .queue, id: "entry", occurrence: "scope"),
            songId: "a", title: "Corrected", sources: [source])
        XCTAssertTrue(favorites.isFavorite(queued))
        XCTAssertTrue(favorites.isFavorite(track("a", title: "Older facts")))
        XCTAssertFalse(favorites.isFavorite(track("different", title: queued.title)))
        let task = try XCTUnwrap(favorites.toggle(queued))
        XCTAssertFalse(favorites.isFavorite(track("a", title: "Older facts")))
        XCTAssertTrue(favorites.isFavorite(track("b")))
        await task.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.membershipRemovals, [[MusicReference(kind: .library, id: "a")]])
        XCTAssertEqual(counts.membershipAdds, [[]])
        XCTAssertEqual(favorites.references, Set([track("b").ref]))
        XCTAssertEqual(counts.tracks, 0); XCTAssertTrue(counts.requestedRefs.isEmpty)
        await model.close()
    }

    @MainActor func testDelayedReferenceReadCannotUpgradeCapturedFavoriteRevision() async throws {
        let (service, model, favorites) = await setup()
        let entered = expectation(description: "captured mini-player favorite refs")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        await fulfillment(of: [entered], timeout: 2)
        let external = MusicCollectionService.Row(id: "favorites", revision: 2, kind: "favorites",
            songs: ["a", "b", "external"])
        await service.replace([external])
        model.publishCollections([external.collection])
        await gate.release(); await task.value
        XCTAssertEqual(favorites.error, .staleRevision)
        await favorites.reload()?.value
        let counts = await service.statistics(), rows = await service.snapshotRows()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.tracks, 0); XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(rows.first?.tracks.map(\.songId), ["a", "b", "external"])
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["a", "b", "external"]))
        XCTAssertFalse(favorites.isFavorite(track("c")))
        await model.close()
    }

    @MainActor func testReactivationWaitsForCancelledReferenceReadBeforeNewWrites() async throws {
        let (service, model, favorites) = await setup()
        let entered = expectation(description: "cancelled favorite refs retained")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let task = try XCTUnwrap(favorites.toggle(track("c")))
        await fulfillment(of: [entered], timeout: 2)
        favorites.suspend()
        await favorites.setActive(true)?.value
        XCTAssertTrue(favorites.busy)
        XCTAssertNil(favorites.toggle(track("other")))
        await gate.release(); await task.value; await favorites.flush()
        XCTAssertFalse(favorites.busy); XCTAssertTrue(favorites.loaded)
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["a", "b"]))
        XCTAssertNil(favorites.error)
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 0); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.tracks, 0); XCTAssertEqual(counts.queueWrites, 0)
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
        XCTAssertTrue(favorites.references.isEmpty)
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
        await service.blockReferences(gate)
        let old = try XCTUnwrap(favorites.reload())
        await fulfillment(of: [entered], timeout: 2)
        model.setAvailable(false)
        await service.replace([.init(id: "favorites", revision: 2, kind: "favorites", songs: ["new"])])
        model.setAvailable(true); await model.load()
        let restored = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { Set(favorites.references.map(\.id)) == Set(["new"]) }
        }, evaluatedWith: favorites)
        await fulfillment(of: [restored], timeout: 2)
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["new"]))
        await gate.release(); await old.value
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["new"]))
        XCTAssertNil(favorites.error)
        await model.close()
    }

    @MainActor func testFreshFavoritesReadDoesNotWaitForRetiredReads() async throws {
        let (service, model, favorites) = await setup()
        var gates: [MusicCollectionGate] = [], tasks: [Task<Void, Never>] = []
        for index in 0..<4 {
            let entered = expectation(description: "retired favorites read \(index)")
            let gate = MusicCollectionGate { entered.fulfill() }
            gates.append(gate)
            await service.blockReferences(gate)
            tasks.append(try XCTUnwrap(favorites.reload()))
            await fulfillment(of: [entered], timeout: 2)
        }
        let fresh = try XCTUnwrap(favorites.reload())
        await fresh.value
        XCTAssertTrue(favorites.loaded); XCTAssertFalse(favorites.loading)
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["a", "b"])); XCTAssertNil(favorites.error)
        for gate in gates { await gate.release() }
        for task in tasks { await task.value }
        XCTAssertTrue(favorites.loaded); XCTAssertNil(favorites.error)
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
        XCTAssertEqual(counts.tracks, 0)
        XCTAssertEqual(counts.membershipAdds, [[track("c").ref]])
        XCTAssertTrue(counts.requestedRefs.isEmpty)
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
        XCTAssertEqual(after.referenceReads, before.referenceReads)
        XCTAssertEqual(Set(favorites.references.map(\.id)), Set(["a", "b"]))
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
        XCTAssertTrue(favorites.references.isEmpty)
    }
}
#endif

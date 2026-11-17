import XCTest
import Combine
@testable import MeowMusicFeature

@MainActor final class MusicSearchActionsTests: XCTestCase {
    private func setup(songs: [String] = []) async ->
        (MusicCollectionService, MusicViewModel, MusicSearchActions, MusicCollectionPlayer) {
        let service = MusicCollectionService(rows: [.init(id: "favorites", kind: "favorites", songs: songs)])
        let player = MusicCollectionPlayer()
        // The view model and tests must inspect the very same player.
        let owner = MusicViewModel(service: service, player: player)
        await owner.load()
        let actions = MusicSearchActions(model: owner)
        actions.setActive(true); await actions.finishPending()
        return (service, owner, actions, player)
    }
    private func song(_ id: String) throws -> MusicSearchSong {
        let track = MusicCollectionService.track(id)
        return try XCTUnwrap(MusicSearchFusion.songs([.init(platform: .netease, items: [track])]).first)
    }
    func testReactivationWaitsForCancelledPhysicalActionBeforeNewWrites() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        let entered = expectation(description: "old physical read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let old = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        actions.suspend()
        actions.setActive(true)
        XCTAssertTrue(actions.busy)
        XCTAssertNil(actions.toggleFavorite(try song("other")))
        await gate.release()
        await old.value
        await actions.finishPending()
        XCTAssertFalse(actions.busy)
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept"]))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }

    func testAccountChangeCancelsSearchActionBeforeAnyDurableWrite() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        let entered = expectation(description: "old account favorite read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let old = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        model.accountSessionsChanged(.netease)
        XCTAssertTrue(actions.busy)
        XCTAssertNil(actions.toggleFavorite(try song("other")))
        await gate.release(); await old.value; await actions.finishPending()
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept"]))
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }

    func testNewFavoritesBindingPublishesBeforeRetiredReadsReturn() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        var gates: [MusicCollectionGate] = []
        for index in 0..<4 {
            let entered = expectation(description: "retired search favorites read \(index)")
            let gate = MusicCollectionGate { entered.fulfill() }
            gates.append(gate)
            await service.blockReferences(gate)
            if index == 0 { actions.refreshFavorites() } else { actions.setActive(true) }
            await fulfillment(of: [entered], timeout: 2)
            actions.suspend()
        }
        await service.replace([.init(id: "favorites", kind: "favorites", songs: ["fresh"])])
        let loaded = expectation(description: "fresh search favorites published")
        let observation = actions.$favoritesLoaded.dropFirst().filter { $0 }.prefix(1).sink { _ in loaded.fulfill() }
        actions.setActive(true)
        await fulfillment(of: [loaded], timeout: 2)
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["fresh"]))
        XCTAssertFalse(actions.favoritesLoading); XCTAssertNil(actions.error)
        for gate in gates { await gate.release() }
        await actions.finishPending()
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["fresh"]))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0); XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty)
        observation.cancel(); actions.suspend(); await model.close()
    }

    func testDisplayedQueueFenceRejectsFreshServiceRevisionWithoutRetargetingCommand() async throws {
        let (service, model, actions, _) = await setup()
        let intent = MusicReferenceQueueIntent(access: model.accountAccessGeneration,
            revision: model.state.revision, ids: model.queue.map(\.id))
        let entered = expectation(description: "queue state read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockFreshState(gate)
        let command = Task { await model.changeDisplayedQueue(.clear, intent: intent) }
        await fulfillment(of: [entered], timeout: 2)
        await service.bumpQueueRevision()
        await gate.release()
        await command.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.queueRequests, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(model.state.revision, 1)
        XCTAssertTrue(model.error?.contains("内容已更改") == true)
        actions.suspend(); await model.close()
    }

    func testFavoriteToggleUsesExistingLibraryAndNeverTouchesQueueOrPlayback() async throws {
        let (service, model, actions, player) = await setup()
        let track = try song("new")
        XCTAssertTrue(actions.favoritesLoaded)
        XCTAssertFalse(actions.isFavorite(track))
        await actions.toggleFavorite(track)?.value
        XCTAssertTrue(actions.isFavorite(track))
        XCTAssertNil(actions.error)
        await actions.toggleFavorite(track)?.value
        XCTAssertFalse(actions.isFavorite(track))
        XCTAssertTrue(actions.favorites.isEmpty)
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 2)
        XCTAssertEqual(counts.tracks, 0)
        XCTAssertEqual(counts.membershipAdds, [[track.track.ref], []])
        XCTAssertEqual(counts.membershipRemovals, [[], [track.track.ref]])
        XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }
    func testTenThousandFavoritesAddAndRemoveOnlyOneReferenceWithoutTrackReads() async throws {
        let ids = (0..<10_000).map { "saved-\($0)" }
        let (service, model, actions, player) = await setup(songs: ids)
        let row = try song("new")
        let target = try XCTUnwrap(model.collections.first { $0.kind == "favorites" })
        XCTAssertEqual(actions.favorites.count, 10_000)
        await actions.toggleFavorite(row)?.value
        XCTAssertEqual(actions.favorites.count, 10_001)
        XCTAssertTrue(actions.isFavorite(row))
        await actions.toggleFavorite(row)?.value
        XCTAssertEqual(actions.favorites, Set(ids.map { MusicReference(kind: .library, id: $0) }))
        let counts = await service.statistics()
        XCTAssertEqual(counts.tracks, 0)
        XCTAssertEqual(counts.referenceReads, 5) // Initial plus before/after each delta.
        XCTAssertEqual(counts.membershipAdds, [[row.track.ref], []])
        XCTAssertEqual(counts.membershipRemovals, [[], [row.track.ref]])
        XCTAssertEqual(counts.edited.map(\.revision), [target.revision, target.revision + 1])
        XCTAssertEqual(counts.writes, 2)
        XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(counts.queueWrites, 0); XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }

    func testFavoriteVariantsUsePermanentReferencesAndRemoveOnlyMatchingMembers() async throws {
        let (service, model, actions, _) = await setup(songs: ["saved", "other"])
        let queued = MusicRemoteTrack(ref: .init(kind: .queue, id: "occurrence", occurrence: "scope"),
            songId: "saved", title: "Changed title")
        let primary = MusicCollectionService.track("saved")
        let row = MusicSearchSong(id: primary.songId, track: primary, variants: [primary, queued],
            matchedPlatforms: [], score: 0)
        XCTAssertTrue(actions.isFavorite(row))
        let live = MusicRemoteTrack(ref: .init(kind: .library, id: "live"),
            songId: "live", title: queued.title)
        let different = try XCTUnwrap(MusicSearchFusion.songs([.init(platform: .netease, items: [live])]).first)
        XCTAssertFalse(actions.isFavorite(different))
        var refreshes = 0
        await actions.toggleFavorite(row, refresh: { refreshes += 1; return primary })?.value
        XCTAssertFalse(actions.isFavorite(row))
        XCTAssertEqual(actions.favorites, Set([MusicReference(kind: .library, id: "other")]))
        let counts = await service.statistics()
        XCTAssertEqual(counts.membershipAdds, [[]])
        XCTAssertEqual(counts.membershipRemovals, [[MusicReference(kind: .library, id: "saved")]])
        XCTAssertEqual(counts.tracks, 0); XCTAssertEqual(refreshes, 0)
        XCTAssertTrue(counts.requestedRefs.isEmpty)
        actions.suspend(); await model.close()
    }

    func testDelayedReferenceReadCannotUpgradeCapturedFavoriteRevision() async throws {
        let (service, model, actions, _) = await setup(songs: ["kept"])
        let entered = expectation(description: "captured favorite refs")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let task = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        let external = MusicCollectionService.Row(id: "favorites", revision: 2, kind: "favorites",
            songs: ["kept", "external"])
        await service.replace([external])
        model.publishCollections([external.collection])
        await gate.release(); await task.value
        XCTAssertEqual(actions.error, .staleRevision)
        let counts = await service.statistics(), rows = await service.snapshotRows()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.tracks, 0); XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(rows.first?.tracks.map(\.songId), ["kept", "external"])
        actions.refreshFavorites(); await actions.finishPending()
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept", "external"]))
        actions.suspend(); await model.close()
    }

    func testUnconfirmedFavoriteDeltaIsReadBackWithoutReplayingWrite() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        let row = try song("new")
        await service.failCommitAcknowledgement()
        await actions.toggleFavorite(row)?.value
        XCTAssertEqual(actions.error, .commitUnconfirmed)
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept"]))
        actions.refreshFavorites(); await actions.finishPending()
        XCTAssertTrue(actions.isFavorite(row))
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 1)
        XCTAssertEqual(counts.membershipAdds, [[row.track.ref]])
        XCTAssertEqual(counts.tracks, 0); XCTAssertTrue(counts.requestedRefs.isEmpty)
        XCTAssertEqual(counts.queueWrites, 0); XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }
    func testASecondTapWhileFavoriteWriteIsPendingCannotReverseTheIntendedState() async throws {
        let (service, model, actions, _) = await setup()
        let track = try song("new")
        let entered = expectation(description: "favorite write entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockEdit(before: gate)
        let task = try XCTUnwrap(actions.toggleFavorite(track))
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertTrue(actions.busy)
        XCTAssertNil(actions.toggleFavorite(track))
        await gate.release(); await task.value
        XCTAssertTrue(actions.isFavorite(track))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 1)
        actions.suspend(); await model.close()
    }
    func testLeavingSearchBeforeFavoriteReadReturnsPreventsAnyWrite() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        let entered = expectation(description: "favorite refresh entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockReferences(gate)
        let task = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        actions.suspend()
        await gate.release(); await task.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(actions.favorites.isEmpty)
        XCTAssertFalse(actions.favoritesLoaded)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }
    func testConcurrentCollectionRevisionChangeIsReportedWithoutOverwritingOtherChanges() async throws {
        let (service, model, actions, _) = await setup(songs: ["kept"])
        let entered = expectation(description: "favorite CAS entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockEdit(before: gate)
        let task = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        await service.replace([.init(id: "favorites", revision: 2, kind: "favorites", songs: ["kept", "external"])])
        await gate.release(); await task.value
        XCTAssertEqual(actions.error, .staleRevision)
        let rows = await service.snapshotRows(), counts = await service.statistics()
        XCTAssertEqual(rows.first?.tracks.map(\.songId), ["kept", "external"])
        XCTAssertEqual(counts.writes, 0)
        actions.refreshFavorites(); await actions.finishPending()
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept", "external"]))
        actions.suspend(); await model.close()
    }
    func testSingleRowActionPassesCurrentPermanentReferenceToExistingQueueOwner() async throws {
        let (service, model, actions, player) = await setup()
        var reads = 0
        let session = MusicSearchResultsModel(readSongs: { platform, query, page, _ in
            reads += 1
            let track = MusicRemoteTrack(ref: .init(kind: .library, id: "recording"),
                songId: "recording", title: "Song", artist: "Artist", duration: 180,
                sources: [.init(sourceKey: "wy:a", platform: "wy", nativeId: "a")])
            return .init(platform: platform.rawValue, query: query, page: page, limit: 30, total: 1, nextPage: nil, items: [track])
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { request in
            reads += 1
            let track = request.targets[0].track
            return .init(ref: .init(kind: .library, id: track.songId), songId: track.songId,
                title: track.title, artist: track.artist, duration: track.duration, sources: track.sources)
        })
        session.setAccess(available: true, generation: model.accountAccessGeneration); session.appear()
        session.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await session.finishPendingReads()
        let row = try XCTUnwrap(session.songs.first)
        await actions.perform(song: row, session: session) { await model.append($0) }?.value
        let queue = await service.queue(), counts = await service.statistics()
        XCTAssertEqual(queue.entries.first?.track.songId, "recording")
        XCTAssertEqual(counts.queueWrites, 1)
        XCTAssertEqual(reads, 2)
        XCTAssertNil(actions.actionError)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); session.disappear(); await model.close()
    }
    func testFavoriteRecordingReadFailureDoesNotWriteOrChangeDisplayedMembership() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        await actions.toggleFavorite(try song("new"), refresh: { throw MusicServiceError.staleResult })?.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(Set(actions.favorites.map(\.id)), Set(["kept"]))
        XCTAssertEqual(actions.error, .staleResult)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }
    func testFavoriteRecordingRefreshCannotWriteAfterLeavingSearch() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        let row = try song("new")
        let entered = expectation(description: "favorite recording refresh entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        let task = try XCTUnwrap(actions.toggleFavorite(row, refresh: {
            await gate.wait()
            return row.track
        }))
        await fulfillment(of: [entered], timeout: 2)
        actions.suspend()
        await gate.release(); await task.value; await actions.finishPending()
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 0); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.tracks, 0); XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(actions.favorites.isEmpty); XCTAssertFalse(actions.favoritesLoaded)
        XCTAssertNil(actions.error); XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }
    func testSearchActionCannotWriteAfterLeavingDuringPermanentRecordingRead() async throws {
        let (service, model, actions, player) = await setup()
        let entered = expectation(description: "permanent recording read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        var reads = 0
        let session = MusicSearchResultsModel(readSongs: { platform, query, page, _ in
            reads += 1
            let track = MusicRemoteTrack(ref: .init(kind: .library, id: "recording"),
                songId: "recording", title: "Song", artist: "Artist", duration: 180,
                sources: [.init(sourceKey: "wy:a", platform: "wy", nativeId: "a")])
            return .init(platform: platform.rawValue, query: query, page: page, limit: 30, total: 1, nextPage: nil, items: [track])
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { request in
            await gate.wait()
            return request.targets[0].track
        })
        session.setAccess(available: true, generation: model.accountAccessGeneration); session.appear()
        session.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await session.finishPendingReads()
        let row = try XCTUnwrap(session.songs.first)
        let task = try XCTUnwrap(actions.perform(song: row, session: session) { await model.append($0) })
        await fulfillment(of: [entered], timeout: 2)
        actions.suspend(); session.disappear()
        await gate.release(); await task.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertFalse(actions.busy)
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }
    func testPlayAllOverTwoThousandBuildsEveryDisplayedRequestWithinByteBudget() async throws {
        let (service, model, actions, _) = await setup()
        let session = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
            let tracks = (0..<limit).map { index -> MusicRemoteTrack in
                let id = "\(platform.rawValue)-\((page - 1) * limit + index)"
                return .init(ref: .init(kind: .library, id: id), songId: id, title: id,
                    artist: "Fixture", duration: 180,
                    sources: [.init(sourceKey: id, platform: platform.rawValue, nativeId: id)])
            }
            return .init(platform: platform.rawValue, query: query, page: page, limit: limit, total: 1020,
                nextPage: page < 34 ? page + 1 : nil, items: tracks)
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { _ in throw MusicServiceError.unsupported })
        session.setAccess(available: true, generation: model.searchAccessGeneration); session.appear()
        session.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await session.finishPendingReads()
        for _ in 1..<34 { session.more(.songs); await session.finishPendingReads() }
        XCTAssertEqual(session.songs.count, 2040)
        let request = try session.makeBatchRequest()
        XCTAssertEqual(request.targets.count, 2040)
        XCTAssertEqual(request.targets.map(\.track.songId), session.songs.map(\.id))
        let playback = actions.playAll(session: session)
        XCTAssertNotNil(playback)
        await playback?.value
        XCTAssertNil(actions.actionError)
        XCTAssertEqual(session.songs.count, 2040)
        let counts = await service.statistics()
        XCTAssertEqual(counts.queueWrites, 0)
        let batchTargets = await service.batchTargetCounts()
        XCTAssertEqual(batchTargets, [2040])
        actions.suspend(); session.disappear(); await model.close()
    }
}

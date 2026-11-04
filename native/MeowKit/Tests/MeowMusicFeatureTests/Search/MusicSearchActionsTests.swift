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
        await service.blockTracks(gate)
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
        XCTAssertEqual(actions.favorites.map(\.songId), ["kept"])
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
        await service.blockTracks(gate)
        let old = try XCTUnwrap(actions.toggleFavorite(try song("new")))
        await fulfillment(of: [entered], timeout: 2)
        model.accountSessionsChanged(.netease)
        XCTAssertTrue(actions.busy)
        XCTAssertNil(actions.toggleFavorite(try song("other")))
        await gate.release(); await old.value; await actions.finishPending()
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(actions.favorites.map(\.songId), ["kept"])
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
            await service.blockTracks(gate)
            if index == 0 { actions.refreshFavorites() } else { actions.setActive(true) }
            await fulfillment(of: [entered], timeout: 2)
            actions.suspend()
        }
        await service.replace([.init(id: "favorites", kind: "favorites", songs: ["fresh"])])
        let loaded = expectation(description: "fresh search favorites published")
        let observation = actions.$favoritesLoaded.dropFirst().filter { $0 }.prefix(1).sink { _ in loaded.fulfill() }
        actions.setActive(true)
        await fulfillment(of: [loaded], timeout: 2)
        XCTAssertEqual(actions.favorites.map(\.songId), ["fresh"])
        XCTAssertFalse(actions.favoritesLoading); XCTAssertNil(actions.error)
        for gate in gates { await gate.release() }
        await actions.finishPending()
        XCTAssertEqual(actions.favorites.map(\.songId), ["fresh"])
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
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty)
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
        await service.blockTracks(gate)
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
        XCTAssertEqual(actions.favorites.map(\.songId), ["kept", "external"])
        actions.suspend(); await model.close()
    }
    func testSingleRowActionPassesRenewedHandleToExistingQueueOwner() async throws {
        let (service, model, actions, player) = await setup()
        var reads = 0
        let session = MusicSearchResultsModel(readSongs: { platform, query, page, _ in
            reads += 1
            let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "expired"),
                songId: "recording", title: "Song", artist: "Artist", duration: 180,
                sources: [.init(sourceKey: "wy:a", platform: "wy", nativeId: "a")])
            return .init(platform: platform.rawValue, query: query, page: page, limit: 30, total: 1, nextPage: nil, items: [track])
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { request in
            reads += 1
            let track = request.targets[0].track
            return .init(ref: .init(kind: .handle, id: "renewed"), songId: track.songId,
                title: track.title, artist: track.artist, duration: track.duration, sources: track.sources)
        })
        session.setAccess(available: true, generation: model.accountAccessGeneration); session.appear()
        session.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await session.finishPendingReads()
        let row = try XCTUnwrap(session.songs.first)
        await actions.perform(song: row, session: session) { await model.append($0) }?.value
        let queue = await service.queue(), counts = await service.statistics()
        XCTAssertEqual(queue.entries.first?.track.ref, .init(kind: .handle, id: "renewed"))
        XCTAssertEqual(counts.queueWrites, 1)
        XCTAssertEqual(reads, 2)
        XCTAssertNil(actions.actionError)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); session.disappear(); await model.close()
    }
    func testFavoriteRenewalFailureDoesNotWriteOrChangeDisplayedMembership() async throws {
        let (service, model, actions, player) = await setup(songs: ["kept"])
        await actions.toggleFavorite(try song("new"), refresh: { throw MusicServiceError.staleResult })?.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertEqual(actions.favorites.map(\.songId), ["kept"])
        XCTAssertEqual(actions.error, .staleResult)
        XCTAssertTrue(player.resources.isEmpty)
        actions.suspend(); await model.close()
    }
    func testSearchActionCannotWriteAfterLeavingDuringHandleRenewal() async throws {
        let (service, model, actions, player) = await setup()
        let entered = expectation(description: "exact-page renewal entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        var reads = 0
        let session = MusicSearchResultsModel(readSongs: { platform, query, page, _ in
            reads += 1
            let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "handle-\(reads)"),
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
                return .init(ref: .init(kind: .handle, id: id), songId: id, title: id,
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
        XCTAssertNoThrow(try request.validate())
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

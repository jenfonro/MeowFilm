import XCTest
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
        let track = MusicRemoteTrack(ref: .init(kind: .library, id: id), songId: id, title: id)
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
        let session = MusicSearchResultsModel(readSongs: { platform, query, page in
            reads += 1
            let track = MusicRemoteTrack(ref: .init(kind: .handle, id: reads == 1 ? "expired" : "renewed"),
                songId: "recording", title: "Song", artist: "Artist", duration: 180,
                sources: [.init(sourceKey: "wy:a", platform: "wy", nativeId: "a")])
            return .init(platform: platform.rawValue, query: query, page: page, total: 1, nextPage: nil, items: [track])
        }, readPlaylists: { _, _, _ in .init(items: [], total: 0) })
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
        let session = MusicSearchResultsModel(readSongs: { platform, query, page in
            reads += 1
            if reads > 1 { await gate.wait() }
            let track = MusicRemoteTrack(ref: .init(kind: .handle, id: "handle-\(reads)"),
                songId: "recording", title: "Song", artist: "Artist", duration: 180,
                sources: [.init(sourceKey: "wy:a", platform: "wy", nativeId: "a")])
            return .init(platform: platform.rawValue, query: query, page: page, total: 1, nextPage: nil, items: [track])
        }, readPlaylists: { _, _, _ in .init(items: [], total: 0) })
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
    func testPlayAllOverTwoThousandReportsExplicitLimitAndKeepsEveryDisplayedResult() async throws {
        let (service, model, actions, _) = await setup()
        let session = MusicSearchResultsModel(readSongs: { platform, query, page in
            let tracks = (0..<30).map { index -> MusicRemoteTrack in
                let id = "\(platform.rawValue)-\((page - 1) * 30 + index)"
                return .init(ref: .init(kind: .handle, id: id), songId: id, title: id,
                    artist: "Fixture", duration: 180,
                    sources: [.init(sourceKey: id, platform: platform.rawValue, nativeId: id)])
            }
            return .init(platform: platform.rawValue, query: query, page: page, total: 1020,
                nextPage: page < 34 ? page + 1 : nil, items: tracks)
        }, readPlaylists: { _, _, _ in .init(items: [], total: 0) })
        session.setAccess(available: true, generation: model.accountAccessGeneration); session.appear()
        session.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await session.finishPendingReads()
        for _ in 1..<34 { session.more(.songs); await session.finishPendingReads() }
        XCTAssertEqual(session.songs.count, 2040)
        XCTAssertNil(actions.playAll(session: session))
        XCTAssertEqual(actions.actionError, "最多播放2000首")
        XCTAssertEqual(session.songs.count, 2040)
        let counts = await service.statistics()
        XCTAssertEqual(counts.queueWrites, 0)
        actions.suspend(); session.disappear(); await model.close()
    }
}

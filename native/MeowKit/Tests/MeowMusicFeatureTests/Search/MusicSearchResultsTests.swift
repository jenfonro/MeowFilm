import XCTest
import Combine
@testable import MeowMusicFeature

private actor MusicSearchTestPages {
    struct SongStep: Sendable {
        let tracks: [MusicRemoteTrack]
        var next: Int? = nil
        var failure: MusicServiceError? = nil
    }
    var songs: [String: SongStep] = [:]
    var playlists: [String: MusicPublicBrowseCollectionPage] = [:]
    var playlistErrors: [String: MusicServiceError] = [:]
    var gates: [String: MusicCollectionGate] = [:]
    var calls: [String] = []
    func song(_ key: String, _ value: SongStep) { songs[key] = value }
    func playlist(_ key: String, _ value: MusicPublicBrowseCollectionPage) { playlists[key] = value }
    func failPlaylist(_ key: String, _ value: MusicServiceError?) { playlistErrors[key] = value }
    func gate(_ key: String, _ value: MusicCollectionGate) { gates[key] = value }
    func log() -> [String] { calls }
    func readSongs(_ platform: MusicPublicBrowsePlatform, query: String, page: Int) async throws -> MusicSearchPage {
        let key = "\(query)|\(platform.rawValue)|\(page)"
        calls.append(key)
        let value = songs[key] ?? SongStep(tracks: [])
        if let gate = gates.removeValue(forKey: key) { await gate.wait() }
        if let failure = value.failure { throw failure }
        return .init(platform: platform.rawValue, query: query, page: page, total: value.tracks.count,
                     nextPage: value.next, items: value.tracks)
    }
    func readPlaylists(_ platform: MusicPublicBrowsePlatform, query: String, cursor: String?) async throws -> MusicPublicBrowseCollectionPage {
        let key = "lists|\(query)|\(platform.rawValue)|\(cursor ?? "first")"
        calls.append(key)
        let value = playlists[key] ?? .init(items: [], total: 0), error = playlistErrors[key]
        if let gate = gates.removeValue(forKey: key) { await gate.wait() }
        if let error { throw error }
        return value
    }
}

@MainActor final class MusicSearchResultsTests: XCTestCase {
    private func track(_ id: String, platform: String = "wy", songID: String? = nil, handle: String? = nil,
                       title: String? = nil, duration: Double = 180) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: handle ?? id), songId: songID ?? id,
              title: title ?? songID ?? id, artist: "Artist", duration: duration,
              sources: [.init(sourceKey: platform + ":" + id, platform: platform, nativeId: id)])
    }
    private func session(_ pages: MusicSearchTestPages) -> MusicSearchResultsModel {
        let value = MusicSearchResultsModel(readSongs: { platform, query, page in
            try await pages.readSongs(platform, query: query, page: page)
        }, readPlaylists: { platform, query, cursor in
            try await pages.readPlaylists(platform, query: query, cursor: cursor)
        })
        value.setAccess(available: true, generation: UUID()); value.appear()
        return value
    }
    func testDefaultThreePlatformRequestsStartConcurrentlyAndFusionWaitsForNoPreferredPlatform() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "all native-shaped requests started")
        entered.expectedFulfillmentCount = 3
        let gate = MusicCollectionGate { entered.fulfill() }
        for platform in MusicSearchPreferences.platforms {
            let key = "q|\(platform.rawValue)|1"
            await pages.song(key, .init(tracks: [track(platform.rawValue, platform: platform.rawValue, songID: "same")]))
            await pages.gate(key, gate)
        }
        model.submit("q", preferences: .init())
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertTrue(model.songsLoading)
        await gate.release()
        await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 1)
        XCTAssertEqual(model.songs.first?.matchedPlatforms, ["kg", "tx", "wy"])
        let calls = await pages.log()
        XCTAssertEqual(Set(calls.filter { !$0.hasPrefix("lists|") }), Set(["q|wy|1", "q|tx|1", "q|kg|1"]))
        XCTAssertEqual(calls.filter { $0.hasPrefix("lists|") }, ["lists|q|wy|first"])
        XCTAssertFalse(model.songsLoading)
    }
    func testPaginationAccumulatesDeduplicatesBySourceAndKeepsOriginalRanks() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let first = track("a"), second = track("b")
        await pages.song("q|wy|1", .init(tracks: [first], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("a", handle: "new-handle"), second]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        model.more(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["a", "b"])
        XCTAssertEqual(model.songs.first?.track.ref, first.ref)
        XCTAssertEqual(model.songs.last?.score ?? 0, 1.0 / 37, accuracy: 0.000001)
        XCTAssertFalse(model.moreSongs)
        XCTAssertTrue(model.songFailures.isEmpty)
    }
    func testPartialFailureRetainsSuccessfulRowsAndRetryTargetsOnlyFailedPlatform() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("wy-first")]))
        await pages.song("q|tx|1", .init(tracks: [], failure: .rateLimited))
        await pages.song("q|kg|1", .init(tracks: [track("kg-first", platform: "kg")]))
        model.submit("q", preferences: .init(searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 2)
        XCTAssertEqual(model.songFailures.map(\.platform), [.qq])
        await pages.song("q|tx|1", .init(tracks: [track("qq-retry", platform: "tx")]))
        model.retry(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 3)
        XCTAssertTrue(model.songFailures.isEmpty)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "q|wy|1" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|kg|1" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 2)
    }
    func testOldUncooperativeQueryCannotPublishOverNewQuery() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "old request entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.song("old|wy|1", .init(tracks: [track("old")]))
        await pages.gate("old|wy|1", gate)
        await pages.song("new|wy|1", .init(tracks: [track("new")]))
        let preferences = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        model.submit("old", preferences: preferences)
        await fulfillment(of: [entered], timeout: 2)
        model.submit("new", preferences: preferences)
        await model.finishPendingReads()
        await gate.release()
        XCTAssertEqual(model.query, "new")
        XCTAssertEqual(model.songs.map(\.id), ["new"])
        XCTAssertTrue(model.songFailures.isEmpty)
    }
    func testUserCancellationKeepsLoadedRowsAndRetriesCancelledNextPage() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("b")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let entered = expectation(description: "next page entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.gate("q|wy|2", gate)
        model.more(.songs)
        await fulfillment(of: [entered], timeout: 2)
        model.cancel(.songs)
        XCTAssertEqual(model.songs.map(\.id), ["a"])
        XCTAssertTrue(model.songsCancelled); XCTAssertFalse(model.songsLoading)
        await gate.release()
        model.retry(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["a", "b"])
        XCTAssertFalse(model.songsCancelled)
    }
    func testDetailReturnPreservesResultsAndDoesNotAutoFetchWithoutNewScrollIntent() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("b")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let request = model.requestID
        model.disappear(); model.appear()
        model.more(.songs, automatic: true); await model.finishPendingReads()
        XCTAssertEqual(model.requestID, request)
        XCTAssertEqual(model.songs.map(\.id), ["a"])
        let before = await pages.log()
        XCTAssertEqual(before, ["q|wy|1"])
        model.resumePagination(); model.more(.songs, automatic: true); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["a", "b"])
    }
    func testProtectionGenerationClearsRowsAndRejectsLateCallbacks() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "protected read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.song("q|wy|1", .init(tracks: [track("old-user")]))
        await pages.gate("q|wy|1", gate)
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await fulfillment(of: [entered], timeout: 2)
        model.setAccess(available: false, generation: UUID())
        await gate.release()
        model.setAccess(available: true, generation: UUID()); model.appear()
        await model.finishPendingReads()
        XCTAssertTrue(model.query.isEmpty); XCTAssertTrue(model.songs.isEmpty)
        XCTAssertFalse(model.songsLoaded); XCTAssertFalse(model.songsLoading)
    }
    func testPlaylistAggregationAccumulatesPagesWithoutMergingSameNativeIDAcrossPlatforms() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let wy = MusicPublicBrowseCollection(platform: .netease, nativeID: "1", title: "List")
        let tx = MusicPublicBrowseCollection(platform: .qq, nativeID: "1", title: "List")
        let next = MusicPublicBrowseCollection(platform: .netease, nativeID: "2", title: "Next")
        await pages.playlist("lists|q|wy|first", .init(items: [wy], total: 2, cursor: "next"))
        await pages.playlist("lists|q|tx|first", .init(items: [tx], total: 1))
        await pages.playlist("lists|q|wy|next", .init(items: [wy, next], total: 2))
        model.submit("q", preferences: .init(songPlatforms: [], playlistMode: "aggregate", playlistPlatforms: [.netease, .qq]))
        await model.finishPendingReads()
        XCTAssertEqual(Set(model.playlists.map(\.id)), Set([wy.id, tx.id]))
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(Set(model.playlists.map(\.id)), Set([wy.id, tx.id, next.id]))
        XCTAssertFalse(model.morePlaylists)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "lists|q|tx|first" }.count, 1)
    }
    func testNoEnabledPlatformDoesNotFallBackAndRepeatedPagesStopAtReferenceStallLimit() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        model.submit("q", preferences: .init(songPlatforms: [], searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertTrue(model.songPlatformsEmpty)
        let emptyCalls = await pages.log()
        XCTAssertTrue(emptyCalls.isEmpty)
        for page in 1...5 { await pages.song("q|wy|\(page)", .init(tracks: [track("a")], next: page + 1)) }
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        for _ in 0..<4 { model.more(.songs); await model.finishPendingReads() }
        XCTAssertEqual(model.songs.count, 1)
        XCTAssertFalse(model.moreSongs)
        XCTAssertEqual(model.songFailures.first?.error, .limit)
    }
    func testMismatchedResponseIsNotAdoptedUnderRequestedPlatform() async {
        let model = MusicSearchResultsModel(readSongs: { _, query, page in
            .init(platform: "tx", query: query, page: page, total: 0, nextPage: nil, items: [])
        }, readPlaylists: { _, _, _ in .init(items: [], total: 0) })
        model.setAccess(available: true, generation: UUID()); model.appear()
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertEqual(model.songFailures.first?.error, .invalidResponse)
    }
    func testHandleRenewalUsesExactOriginPageAndKeepsAllAccumulatedRowsAndRanks() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let old = track("second", handle: "expired"), fresh = track("second", handle: "renewed")
        await pages.song("q|wy|1", .init(tracks: [track("first")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [old]))
        await pages.song("q|tx|1", .init(tracks: [track("other-platform", platform: "tx")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        model.more(.songs); await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first { $0.id == "second" })
        let identities = model.songs.map(\.id), scores = model.songs.map(\.score)
        await pages.song("q|wy|2", .init(tracks: [fresh]))
        let renewed = try await model.refreshTrack(row)
        XCTAssertEqual(renewed, fresh)
        XCTAssertEqual(model.songs.map(\.id), identities)
        XCTAssertEqual(model.songs.map(\.score), scores)
        XCTAssertEqual(model.songs.first { $0.id == "second" }?.track.ref, fresh.ref)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "q|wy|1" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|wy|2" }.count, 2)
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 1)
        XCTAssertTrue(model.automaticPaginationHeld)
    }
    func testHandleRenewalRejectsMissingOrChangedRecordingWithoutSubstitutingOrRemovingRow() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let original = track("a", songID: "recording", handle: "expired")
        await pages.song("q|wy|1", .init(tracks: [original]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first)
        for replacement in [
            track("different-native-id", songID: "recording"),
            track("a", songID: "recording", handle: "live", title: "recording (Live)"),
            track("a", songID: "recording", handle: "long", duration: 240),
            track("a", songID: "different-recording", handle: "changed-id")
        ] {
            await pages.song("q|wy|1", .init(tracks: [replacement]))
            do {
                _ = try await model.refreshTrack(row)
                XCTFail("A displayed recording must never silently become a replacement")
            } catch {
                XCTAssertEqual(error as? MusicServiceError, .staleResult)
            }
            XCTAssertEqual(model.songs.first?.track, original)
            XCTAssertEqual(model.songs.count, 1)
        }
        let calls = await pages.log()
        XCTAssertEqual(Set(calls), ["q|wy|1"])
    }
    func testHandleRenewalResumesOtherPlatformsInterruptedFirstPage() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "QQ first page pending")
        let loaded = expectation(description: "NetEase first page displayed")
        let observation = model.$songs.filter { $0.contains(where: { $0.id == "a" }) }.prefix(1)
            .sink { _ in loaded.fulfill() }
        defer { observation.cancel() }
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        await pages.song("q|tx|1", .init(tracks: [track("b", platform: "tx")]))
        await pages.gate("q|tx|1", gate)
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await fulfillment(of: [entered, loaded], timeout: 2)
        let row = try XCTUnwrap(model.songs.first { $0.id == "a" })
        _ = try await model.refreshTrack(row)
        await gate.release()
        await model.finishPendingReads()
        XCTAssertEqual(Set(model.songs.map(\.id)), Set(["a", "b"]))
        XCTAssertFalse(model.songsCancelled)
        XCTAssertFalse(model.songsLoading)
        XCTAssertTrue(model.songFailures.isEmpty)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 2)
    }
    func testHandleRenewalCannotPublishAfterAccessRevocation() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first)
        let entered = expectation(description: "renewal pending")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.gate("q|wy|1", gate)
        let renewal = Task { try await model.refreshTrack(row) }
        await fulfillment(of: [entered], timeout: 2)
        model.setAccess(available: false, generation: UUID())
        await gate.release()
        do { _ = try await renewal.value; XCTFail("Revoked renewal was accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertTrue(model.query.isEmpty)
    }
    func testBatchSnapshotRetainsExactOriginsAndSelectedOrderWithoutReadingAgain() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("first")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("second")]))
        await pages.song("q|tx|1", .init(tracks: [track("qq", platform: "tx")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        model.more(.songs); await model.finishPendingReads()
        let before = await pages.log()
        let request = try model.makeBatchRequest(startIndex: 1)
        XCTAssertEqual(request.query, "q")
        XCTAssertEqual(request.startIndex, 1)
        XCTAssertEqual(request.targets.map(\.track.ref), model.songs.map(\.track.ref))
        XCTAssertEqual(request.targets.first { $0.track.songId == "second" }?.page, 2)
        XCTAssertEqual(request.targets.first { $0.track.songId == "qq" }?.platform, .qq)
        let after = await pages.log()
        XCTAssertEqual(after, before)
    }
    func testExpiredPlaylistCursorRebuildsEveryAcceptedPageThenAppendsOnlyTheRequestedPage() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let a = MusicPublicBrowseCollection(platform: .netease, nativeID: "a", title: "A")
        let b = MusicPublicBrowseCollection(platform: .netease, nativeID: "b", title: "B")
        let c = MusicPublicBrowseCollection(platform: .netease, nativeID: "c", title: "C")
        await pages.playlist("lists|q|wy|first", .init(items: [a], total: 3, cursor: "old-1"))
        await pages.playlist("lists|q|wy|old-1", .init(items: [b], total: 3, cursor: "old-2"))
        model.submit("q", preferences: .init(songPlatforms: []))
        await model.finishPendingReads()
        model.more(.playlists); await model.finishPendingReads()
        let before = model.playlists.map(\.id)
        await pages.failPlaylist("lists|q|wy|old-2", .handleExpired)
        await pages.playlist("lists|q|wy|first", .init(items: [a], total: 3, cursor: "new-1"))
        await pages.playlist("lists|q|wy|new-1", .init(items: [b], total: 3, cursor: "new-2"))
        await pages.playlist("lists|q|wy|new-2", .init(items: [c], total: 3))
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(Array(model.playlists.prefix(2)).map(\.id), before)
        XCTAssertEqual(Set(model.playlists.map(\.id)), Set([a.id, b.id, c.id]))
        XCTAssertTrue(model.playlistFailures.isEmpty)
        XCTAssertFalse(model.morePlaylists)
        let calls = await pages.log()
        XCTAssertEqual(calls, ["lists|q|wy|first", "lists|q|wy|old-1", "lists|q|wy|old-2",
                              "lists|q|wy|first", "lists|q|wy|new-1", "lists|q|wy|new-2"])
    }
    func testPlaylistRebuildFailureRetainsRowsUntilExplicitRestart() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let original = MusicPublicBrowseCollection(platform: .netease, nativeID: "original", title: "Original")
        let changed = MusicPublicBrowseCollection(platform: .netease, nativeID: "changed", title: "Changed")
        await pages.playlist("lists|q|wy|first", .init(items: [original], total: 2, cursor: "expired"))
        model.submit("q", preferences: .init(songPlatforms: []))
        await model.finishPendingReads()
        await pages.failPlaylist("lists|q|wy|expired", .handleExpired)
        await pages.playlist("lists|q|wy|first", .init(items: [changed], total: 2, cursor: "changed-next"))
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.playlists.map(\.id), [original.id])
        XCTAssertEqual(model.playlistFailures.first?.error, .staleResult)
        let failedCalls = await pages.log()
        XCTAssertFalse(failedCalls.contains("lists|q|wy|changed-next"))
        model.restartPlaylists(); await model.finishPendingReads()
        XCTAssertEqual(model.playlists.map(\.id), [changed.id])
        XCTAssertTrue(model.playlistFailures.isEmpty)
    }
    func testCancelledPlaylistRebuildCannotPublishOverNewQuery() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let old = MusicPublicBrowseCollection(platform: .netease, nativeID: "old", title: "Old")
        let new = MusicPublicBrowseCollection(platform: .netease, nativeID: "new", title: "New")
        await pages.playlist("lists|old|wy|first", .init(items: [old], total: 2, cursor: "expired"))
        model.submit("old", preferences: .init(songPlatforms: []))
        await model.finishPendingReads()
        await pages.failPlaylist("lists|old|wy|expired", .handleExpired)
        let entered = expectation(description: "cursor reconstruction first page entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.gate("lists|old|wy|first", gate)
        model.more(.playlists)
        await fulfillment(of: [entered], timeout: 2)
        await pages.playlist("lists|new|wy|first", .init(items: [new], total: 1))
        model.submit("new", preferences: .init(songPlatforms: []))
        await model.finishPendingReads()
        await gate.release()
        XCTAssertEqual(model.query, "new")
        XCTAssertEqual(model.playlists.map(\.id), [new.id])
        XCTAssertTrue(model.playlistFailures.isEmpty)
    }
}

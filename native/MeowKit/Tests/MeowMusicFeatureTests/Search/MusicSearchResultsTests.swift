import XCTest
import Combine
@testable import MeowMusicFeature

private final class MusicSearchCacheClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = Date(timeIntervalSince1970: 1_800_000_000)
    func read() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: Double) { lock.lock(); date.addTimeInterval(seconds); lock.unlock() }
}

private actor MusicSearchTestPages {
    struct SongStep: Sendable {
        let tracks: [MusicRemoteTrack]
        var next: Int? = nil
        var failure: MusicServiceError? = nil
        var total: Int? = nil
    }
    struct PlaylistStep: Sendable {
        let items: [MusicPublicBrowseCollection]
        let total: Int
        var next: Int? = nil
    }
    struct Call: Sendable, Equatable { let key: String; let limit: Int }
    var songs: [String: SongStep] = [:]
    var playlists: [String: [PlaylistStep]] = [:]
    var playlistErrors: [String: MusicServiceError] = [:]
    var gates: [String: MusicCollectionGate] = [:]
    var calls: [Call] = []
    var recordings: [String: MusicRemoteTrack] = [:]
    var recordingGate: MusicCollectionGate?
    var recordingReads: [MusicReference] = []
    func song(_ key: String, _ value: SongStep) {
        songs[key] = value
        for track in value.tracks {
            let sources = (recordings[track.songId]?.sources ?? []) + track.sources
            var seen = Set<String>()
            recordings[track.songId] = .init(ref: .init(kind: .library, id: track.songId),
                songId: track.songId, title: track.title, artist: track.artist, album: track.album,
                duration: track.duration, sources: sources.filter { seen.insert($0.sourceKey).inserted },
                artwork: track.artwork, artistNames: track.artistNames)
        }
    }
    func blockRecording(_ gate: MusicCollectionGate) { recordingGate = gate }
    func recordingLog() -> [MusicReference] { recordingReads }
    func playlist(_ key: String, _ value: PlaylistStep) {
        playlists[key, default: []].append(value)
    }
    func failPlaylist(_ key: String, _ value: MusicServiceError?) { playlistErrors[key] = value }
    func gate(_ key: String, _ value: MusicCollectionGate) { gates[key] = value }
    func log() -> [String] { calls.map(\.key) }
    func ranges() -> [Call] { calls }
    func readSongs(_ platform: MusicPublicBrowsePlatform, query: String, page: Int,
                   limit: Int) async throws -> MusicSearchPage {
        let key = "\(query)|\(platform.rawValue)|\(page)"
        calls.append(.init(key: key, limit: limit))
        let value = songs[key] ?? SongStep(tracks: [])
        if let gate = gates.removeValue(forKey: key) { await gate.wait() }
        if let failure = value.failure { throw failure }
        return .init(platform: platform.rawValue, query: query, page: page, limit: limit,
                     total: value.total ?? value.tracks.count, nextPage: value.next, items: value.tracks)
    }
    func readPlaylists(_ platform: MusicPublicBrowsePlatform, query: String, page: Int,
                       limit: Int) async throws -> MusicPlaylistSearchPage {
        let key = "lists|\(query)|\(platform.rawValue)|\(page)"
        calls.append(.init(key: key, limit: limit))
        var values = playlists[key] ?? []
        let value = values.count > 1 ? values.removeFirst() : values.first
        playlists[key] = values
        let result = value ?? .init(items: [], total: 0), error = playlistErrors[key]
        if let gate = gates.removeValue(forKey: key) { await gate.wait() }
        if let error { throw error }
        return .init(platform: platform, query: query, page: page, limit: limit,
                     total: result.total, nextPage: result.next, items: result.items)
    }
    func readRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        guard request.targets.count == 1 else { throw MusicServiceError.invalidRequest }
        let selected = request.targets[0].track
        recordingReads.append(selected.ref)
        let gate = recordingGate; recordingGate = nil
        if let gate { await gate.wait() }
        guard let current = recordings[selected.songId] else { throw MusicServiceError.notFound }
        return current
    }
}

@MainActor final class MusicSearchResultsTests: XCTestCase {
    private func track(_ id: String, platform: String = "wy", songID: String? = nil,
                       title: String? = nil, artist: String = "Artist", duration: Double = 180) -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: songID ?? id, occurrence: platform + ":" + id), songId: songID ?? id,
              title: title ?? songID ?? id, artist: artist, duration: duration,
              sources: [.init(sourceKey: platform + ":" + id, platform: platform, nativeId: id)])
    }
    private func session(_ pages: MusicSearchTestPages, clock: @escaping @Sendable () -> Date = { Date() },
                         sharedCache: MusicSearchResultsCache.Storage? = nil) -> MusicSearchResultsModel {
        let value = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
            try await pages.readSongs(platform, query: query, page: page, limit: limit)
        }, readPlaylists: { platform, query, page, limit in
            try await pages.readPlaylists(platform, query: query, page: page, limit: limit)
        }, readRecording: { request in
            try await pages.readRecording(request)
        }, cacheProvider: { sharedCache ?? MusicSearchResultsCache.make(clock: clock) })
        value.setAccess(available: true, generation: UUID()); value.appear()
        return value
    }
    func testNewSearchSessionReusesSongAndPlaylistPagesButNotQueryOrPlatform() async {
        let pages = MusicSearchTestPages(), cache = MusicSearchResultsCache.make()
        let playlist = MusicPublicBrowseCollection(platform: .netease, nativeID: "one", title: "Playlist")
        await pages.song("q|wy|1", .init(tracks: [track("a")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("b")]))
        await pages.song("q|tx|1", .init(tracks: [track("c", platform: "tx")]))
        await pages.playlist("lists|q|wy|1", .init(items: [playlist], total: 1))
        let settings = MusicSearchPreferences(songPlatforms: [.netease], playlistPlatforms: [.netease])
        let first = session(pages, sharedCache: cache)
        first.submit("q", preferences: settings); await first.finishPendingReads()
        first.more(.songs); await first.finishPendingReads()
        first.disappear()
        let second = session(pages, sharedCache: cache)
        second.submit("q", preferences: settings); await second.finishPendingReads()
        XCTAssertEqual(second.songs.map(\.id), ["a"])
        XCTAssertEqual(second.playlists, [playlist])
        XCTAssertTrue(second.moreSongs)
        XCTAssertFalse(first.requestID == second.requestID)
        second.more(.songs); await second.finishPendingReads()
        XCTAssertEqual(second.songs.map(\.id), ["a", "b"])
        second.submit("other", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await second.finishPendingReads()
        second.submit("q", preferences: .init(songPlatforms: [.qq], searchPlaylists: false))
        await second.finishPendingReads()
        XCTAssertEqual(second.songs.map(\.id), ["c"])
        let reads = await pages.log()
        XCTAssertEqual(reads.count, 5)
        XCTAssertEqual(Set(reads), Set(["q|wy|1", "q|wy|2", "lists|q|wy|1", "other|wy|1", "q|tx|1"]))
        second.disappear()
    }

    func testSharedCacheKeepsExistingExpiryAcrossNewPagesAndDurationChanges() async {
        let pages = MusicSearchTestPages(), clock = MusicSearchCacheClock()
        let cache = MusicSearchResultsCache.make(clock: { clock.read() })
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        var settings = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        let first = session(pages, sharedCache: cache)
        first.submit("q", preferences: settings); await first.finishPendingReads(); first.disappear()
        clock.advance(3599)
        settings.songs.cacheMinutes = 1
        let second = session(pages, sharedCache: cache)
        second.submit("q", preferences: settings); await second.finishPendingReads()
        var reads = await pages.log(); XCTAssertEqual(reads.count, 1)
        clock.advance(2)
        let third = session(pages, sharedCache: cache)
        third.submit("q", preferences: settings); await third.finishPendingReads()
        reads = await pages.log(); XCTAssertEqual(reads.count, 2)
        clock.advance(61)
        second.submit("q", preferences: settings); await second.finishPendingReads()
        reads = await pages.log(); XCTAssertEqual(reads.count, 3)
        second.disappear(); third.disappear()
    }

    func testCancellingOnePageDoesNotCancelAnotherPagesCoalescedRead() async {
        let pages = MusicSearchTestPages(), cache = MusicSearchResultsCache.make()
        let entered = expectation(description: "shared physical search entered")
        let joined = expectation(description: "second page joined the physical read")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        await pages.gate("q|wy|1", gate)
        let first = session(pages, sharedCache: cache), second = session(pages, sharedCache: cache)
        let settings = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        first.submit("q", preferences: settings)
        await fulfillment(of: [entered], timeout: 2)
        MusicRequestTrace.$observer.withValue({ event in
            if event.operation == .search, event.phase == .coalesced { joined.fulfill() }
        }) { second.submit("q", preferences: settings) }
        await fulfillment(of: [joined], timeout: 2)
        first.disappear()
        await gate.release()
        await second.finishPendingReads()
        XCTAssertEqual(second.songs.map(\.id), ["a"])
        XCTAssertTrue(second.songFailures.isEmpty)
        XCTAssertTrue(first.songs.isEmpty)
        let reads = await pages.log(); XCTAssertEqual(reads, ["q|wy|1"])
        second.disappear()
    }
    func testDefaultFivePlatformRequestsStartConcurrentlyAndFusionWaitsForNoPreferredPlatform() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "all native-shaped requests started")
        entered.expectedFulfillmentCount = 5
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
        XCTAssertEqual(model.songs.first?.matchedPlatforms, ["kg", "kw", "mg", "tx", "wy"])
        let calls = await pages.log()
        XCTAssertEqual(Set(calls.filter { !$0.hasPrefix("lists|") }), Set(["q|wy|1", "q|tx|1", "q|kg|1", "q|kw|1", "q|mg|1"]))
        XCTAssertEqual(Set(calls.filter { $0.hasPrefix("lists|") }),
                       Set(["lists|q|wy|1", "lists|q|tx|1", "lists|q|kg|1", "lists|q|kw|1", "lists|q|mg|1"]))
        XCTAssertFalse(model.songsLoading)
    }
    func testSongPagesRespectPerPlatformConcurrencyAndRemainOrdered() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "first batch of pages started")
        entered.expectedFulfillmentCount = 2
        let gate = MusicCollectionGate { entered.fulfill() }
        for page in 1...4 {
            await pages.song("q|tx|\(page)", .init(tracks: [track("s\(page)", platform: "tx")],
                                                   next: page < 4 ? page + 1 : nil))
        }
        await pages.gate("q|tx|1", gate); await pages.gate("q|tx|2", gate)
        model.submit("q", preferences: .init(songPlatforms: [.qq], searchPlaylists: false,
                                             pages: 4, concurrency: 2))
        await fulfillment(of: [entered], timeout: 2)
        let firstBatch = await pages.log()
        XCTAssertEqual(Set(firstBatch), Set(["q|tx|1", "q|tx|2"]))
        await gate.release()
        await model.finishPendingReads()
        let calls = await pages.log()
        XCTAssertEqual(Set(calls), Set(["q|tx|1", "q|tx|2", "q|tx|3", "q|tx|4"]))
        XCTAssertEqual(model.songs.map(\.id), ["s1", "s2", "s3", "s4"])
    }
    func testPagesAndConcurrencyAboveTenAreAcceptedAndRequested() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        for page in 1...12 {
            await pages.song("q|tx|\(page)", .init(tracks: [track("s\(page)", platform: "tx")],
                next: page < 12 ? page + 1 : nil))
        }
        model.submit("q", preferences: .init(songPlatforms: [.qq], searchPlaylists: false,
                                             pages: 12, concurrency: 12))
        await model.finishPendingReads()
        let calls = await pages.log()
        XCTAssertEqual(Set(calls), Set((1...12).map { "q|tx|\($0)" }))
        XCTAssertEqual(model.songs.map(\.id), (1...12).map { "s\($0)" })
        XCTAssertTrue(model.songFailures.isEmpty)
    }

    func testRepeatedNativeSourceRetainsPermanentIDThroughEnrichmentAndCorrection() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let incomplete = track("one", artist: "", duration: 0)
        let complete = track("one", artist: "Artist")
        let conflicting = track("one", artist: "Other artist")
        await pages.song("q|wy|1", .init(tracks: [incomplete], next: 2))
        await pages.song("q|wy|2", .init(tracks: [complete], next: 3))
        await pages.song("q|wy|3", .init(tracks: [conflicting]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let original = try XCTUnwrap(model.songs.first)
        model.more(.songs); await model.finishPendingReads()
        let enriched = try XCTUnwrap(model.songs.first)
        XCTAssertEqual(enriched.id, original.id); XCTAssertEqual(enriched.score, original.score)
        XCTAssertEqual(enriched.variants.count, 2)
        XCTAssertTrue(enriched.variants.contains(incomplete)); XCTAssertTrue(enriched.variants.contains(complete))
        XCTAssertEqual(model.songs.count, 1)
        let request = try model.makeBatchRequest()
        XCTAssertEqual(request.targets[0].track.songId, incomplete.songId)
        model.more(.songs); await model.finishPendingReads()
        XCTAssertTrue(model.songFailures.isEmpty)
        XCTAssertFalse(model.moreSongs)
        XCTAssertEqual(model.songs.first?.id, original.id)
        XCTAssertEqual(model.songs.first?.variants.count, 3)
        XCTAssertTrue(model.songs.first?.variants.contains(conflicting) == true)
        model.disappear()
    }
    func testSongAndPlaylistRoundsAdvanceIndependently() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("s1")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("s2")]))
        let one = MusicPublicBrowseCollection(platform: .netease, nativeID: "1", title: "One")
        let two = MusicPublicBrowseCollection(platform: .netease, nativeID: "2", title: "Two")
        await pages.playlist("lists|q|wy|1", .init(items: [one], total: 2, next: 2))
        await pages.playlist("lists|q|wy|2", .init(items: [two], total: 2))
        model.submit("q", preferences: .init(songPlatforms: [.netease], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["s1"])
        XCTAssertEqual(model.playlists.map(\.id), [one.id, two.id])
        model.more(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["s1", "s2"])
        XCTAssertEqual(model.playlists.map(\.id), [one.id, two.id])
    }
    func testSongAndPlaylistConcurrencyAreIndependentInsideTheSamePlatform() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let entered = expectation(description: "two song requests and one playlist request started")
        entered.expectedFulfillmentCount = 3
        let gate = MusicCollectionGate { entered.fulfill() }
        for page in 1...4 {
            await pages.song("q|tx|\(page)", .init(tracks: [track("s\(page)", platform: "tx")],
                next: page < 4 ? page + 1 : nil))
        }
        for page in 1...3 {
            let item = MusicPublicBrowseCollection(platform: .qq, nativeID: String(page), title: "Playlist \(page)")
            await pages.playlist("lists|q|tx|\(page)", .init(items: [item], total: 3,
                next: page < 3 ? page + 1 : nil))
        }
        for key in ["q|tx|1", "q|tx|2", "lists|q|tx|1"] { await pages.gate(key, gate) }
        model.submit("q", preferences: .init(songPlatforms: [.qq], playlistPlatforms: [.qq],
            songPages: 4, songConcurrency: 2, playlistPages: 3, playlistConcurrency: 1))
        await fulfillment(of: [entered], timeout: 2)
        let initial = await pages.log()
        XCTAssertEqual(Set(initial), Set(["q|tx|1", "q|tx|2", "lists|q|tx|1"]))
        await gate.release(); await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 4)
        XCTAssertEqual(model.playlists.count, 3)
        XCTAssertTrue(model.songFailures.isEmpty); XCTAssertTrue(model.playlistFailures.isEmpty)
    }
    func testLaterSongRoundKeepsDisplayedOrderWhenLateSourceWouldScoreHigher() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")], next: 2))
        await pages.song("q|tx|1", .init(tracks: [track("a", platform: "tx", songID: "a")], next: 2))
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        await pages.song("q|wy|2", .init(tracks: [track("b", songID: "b")]))
        await pages.song("q|tx|2", .init(tracks: [track("high", platform: "tx", songID: "high")]))
        let before = model.songs.map(\.id)
        model.more(.songs); await model.finishPendingReads()
        XCTAssertEqual(Array(model.songs.prefix(before.count)).map(\.id), before)
        XCTAssertTrue(model.songs.contains { $0.id == "high" })
        XCTAssertEqual(model.songs.first { $0.id == "a" }?.matchedPlatforms, ["tx", "wy"])
    }
    func testAccessGenerationClearsSearchCache() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        let preferences = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        model.submit("q", preferences: preferences)
        await model.finishPendingReads()
        let firstCalls = await pages.log()
        model.setAccess(available: true, generation: UUID())
        model.submit("q", preferences: preferences)
        await model.finishPendingReads()
        let calls = await pages.log()
        XCTAssertEqual(calls, firstCalls + ["q|wy|1"])
    }
    func testUnchangedAccessDoesNotClearOrReplay() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        let generation = UUID()
        model.setAccess(available: true, generation: generation)
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let request = model.requestID, calls = await pages.log()
        XCTAssertFalse(model.setAccess(available: true, generation: generation))
        await model.finishPendingReads()
        let unchangedCalls = await pages.log()
        XCTAssertEqual(unchangedCalls, calls)
        XCTAssertEqual(model.requestID, request)
        XCTAssertEqual(model.songs.map(\.id), ["a"])
        XCTAssertEqual(model.query, "q")
    }
    func testChangedAccessClearsRowsAndCacheForPageOwnerToResubmit() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertTrue(model.setAccess(available: true, generation: UUID()))
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertTrue(model.query.isEmpty)
        XCTAssertFalse(model.songsLoaded)
    }
    func testUnavailableAccessClearsRowsAndDoesNotStartFreshReads() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let calls = await pages.log()
        XCTAssertTrue(model.setAccess(available: false, generation: UUID()))
        model.more(.songs); model.retry(.songs)
        await model.finishPendingReads()
        let unavailableCalls = await pages.log()
        XCTAssertEqual(unavailableCalls, calls)
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertTrue(model.query.isEmpty)
        XCTAssertFalse(model.songsLoaded)
    }
    func testRepeatedSubmitReusesCacheWithinAccountGeneration() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        let preferences = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        model.submit("q", preferences: preferences)
        await model.finishPendingReads()
        model.submit("q", preferences: preferences)
        await model.finishPendingReads()
        let calls = await pages.log()
        XCTAssertEqual(calls, ["q|wy|1"])
        XCTAssertEqual(model.songs.map(\.id), ["a"])
    }
    func testPaginationAccumulatesDeduplicatesBySourceAndKeepsOriginalRanks() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let first = track("a"), second = track("b")
        await pages.song("q|wy|1", .init(tracks: [first], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("a"), second]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        model.more(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["a", "b"])
        XCTAssertEqual(model.songs.first?.track.ref, first.ref)
        XCTAssertEqual(model.songs.last?.score ?? 0, 1.0 / 37, accuracy: 0.000001)
        XCTAssertFalse(model.moreSongs)
        XCTAssertTrue(model.songFailures.isEmpty)
    }
    func testLargeProviderContinuationConsumesSongsAndPlaylistsWithoutRankOverflow() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let first = MusicPublicBrowseCollection(platform: .netease, nativeID: "first", title: "First")
        let last = MusicPublicBrowseCollection(platform: .netease, nativeID: "last", title: "Last")
        await pages.song("q|wy|1", .init(tracks: [track("first")], next: Int.max))
        await pages.song("q|wy|\(Int.max)", .init(tracks: [track("last")]))
        await pages.playlist("lists|q|wy|1", .init(items: [first], total: 2, next: Int.max))
        await pages.playlist("lists|q|wy|\(Int.max)", .init(items: [last], total: 2))
        model.submit("q", preferences: .init(songPlatforms: [.netease], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        model.more(.songs); model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["first", "last"])
        XCTAssertEqual(model.playlists.map(\.id), [first.id, last.id])
        XCTAssertTrue(model.songFailures.isEmpty); XCTAssertTrue(model.playlistFailures.isEmpty)
        XCTAssertFalse(model.moreSongs); XCTAssertFalse(model.morePlaylists)
        XCTAssertEqual(model.songs.first?.score ?? 0, 1.0 / 6, accuracy: 0.000001)
        XCTAssertTrue(model.songs.last?.score.isFinite == true)
        XCTAssertGreaterThan(model.songs.last?.score ?? 0, 0)
        XCTAssertLessThan(model.songs.last?.score ?? 1, 1e-18)
        let reads = await pages.log()
        XCTAssertEqual(Set(reads), Set(["q|wy|1", "q|wy|\(Int.max)",
                                       "lists|q|wy|1", "lists|q|wy|\(Int.max)"]))
        XCTAssertEqual(reads.count, 4)
        model.disappear()
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
        await pages.playlist("lists|q|wy|1", .init(items: [wy], total: 2, next: 2))
        await pages.playlist("lists|q|tx|1", .init(items: [tx], total: 1))
        await pages.playlist("lists|q|wy|2", .init(items: [wy, next], total: 2))
        model.submit("q", preferences: .init(songPlatforms: [], playlistPlatforms: [.netease, .qq]))
        await model.finishPendingReads()
        XCTAssertEqual(Set(model.playlists.map(\.id)), Set([wy.id, tx.id]))
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(Set(model.playlists.map(\.id)), Set([wy.id, tx.id, next.id]))
        XCTAssertFalse(model.morePlaylists)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "lists|q|tx|1" }.count, 1)
    }
    func testNoEnabledPlatformDoesNotFallBackAndRepeatedPageKeepsAdvancingWithoutHiddenCap() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        model.submit("q", preferences: .init(songPlatforms: [], searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertTrue(model.songPlatformsEmpty)
        let emptyCalls = await pages.log()
        XCTAssertTrue(emptyCalls.isEmpty)
        for page in 1...6 { await pages.song("q|wy|\(page)", .init(tracks: [track("a")], next: page + 1)) }
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        for _ in 0..<5 { model.more(.songs); await model.finishPendingReads() }
        XCTAssertEqual(model.songs.count, 1)
        XCTAssertTrue(model.moreSongs)
        XCTAssertTrue(model.songFailures.isEmpty)
        let calls = await pages.log()
        XCTAssertTrue(calls.contains("q|wy|6"))
    }
    func testResponseEchoDoesNotCreateASecondBusinessAdmissionCheck() async {
        let model = MusicSearchResultsModel(readSongs: { _, query, page, _ in
            .init(platform: "tx", query: query, page: page, limit: 30, total: 0, nextPage: nil, items: [])
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { _ in throw MusicServiceError.unsupported })
        model.setAccess(available: true, generation: UUID()); model.appear()
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertTrue(model.songFailures.isEmpty)
    }
    func testPermanentRecordingReadKeepsAccumulatedRowsAndRanksWithoutRepeatingSearchPages() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let old = track("second"), fresh = track("second", title: "Current title")
        await pages.song("q|wy|1", .init(tracks: [track("first")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [old]))
        await pages.song("q|tx|1", .init(tracks: [track("other-platform", platform: "tx")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        model.more(.songs); await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first { $0.id == "second" })
        let identities = model.songs.map(\.id), scores = model.songs.map(\.score)
        await pages.song("q|wy|2", .init(tracks: [fresh]))
        let current = try await model.refreshTrack(row)
        XCTAssertEqual(current.songId, fresh.songId)
        XCTAssertEqual(current.title, fresh.title)
        XCTAssertEqual(model.songs.map(\.id), identities)
        XCTAssertEqual(model.songs.map(\.score), scores)
        XCTAssertEqual(model.songs.first { $0.id == "second" }?.track.ref, old.ref)
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "q|wy|1" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|wy|2" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 1)
        XCTAssertTrue(model.automaticPaginationHeld)
    }
    func testChangedSearchResultsDoNotInvalidateTheSelectedPermanentRecording() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let original = track("a", songID: "recording")
        await pages.song("q|wy|1", .init(tracks: [original]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first)
        for replacement in [
            track("different-native-id", songID: "different-recording"),
            track("a", songID: "recording", title: "Corrected full title"),
            track("a", songID: "recording", duration: 240)
        ] {
            await pages.song("q|wy|1", .init(tracks: [replacement]))
            let current = try await model.refreshTrack(row)
            XCTAssertEqual(current.songId, original.songId)
            XCTAssertEqual(model.songs.first?.track, original)
            XCTAssertEqual(model.songs.count, 1)
        }
        let calls = await pages.log()
        XCTAssertEqual(Set(calls), ["q|wy|1"])
    }
    func testRecordingReadDoesNotCancelOrRestartAnotherPlatformsPendingPage() async throws {
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
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 1)
    }
    func testPermanentRecordingReadCannotPublishAfterAccessRevocation() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first)
        let entered = expectation(description: "permanent recording read pending")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.blockRecording(gate)
        let read = Task { try await model.refreshTrack(row) }
        await fulfillment(of: [entered], timeout: 2)
        model.setAccess(available: false, generation: UUID())
        await gate.release()
        do { _ = try await read.value; XCTFail("Revoked recording read was accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(model.songs.isEmpty)
        XCTAssertTrue(model.query.isEmpty)
    }
    func testBatchRetainsPermanentReferencesAndSelectedOrderWithoutReadingAgain() async throws {
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
        XCTAssertEqual(request.targets.map(\.track.songId), model.songs.map(\.id))
        let after = await pages.log()
        XCTAssertEqual(after, before)
    }
    func testPlaylistPaginationReadsNextPageAndAppendsWithoutRepeatingPriorRows() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let a = MusicPublicBrowseCollection(platform: .netease, nativeID: "a", title: "A")
        let b = MusicPublicBrowseCollection(platform: .netease, nativeID: "b", title: "B")
        let c = MusicPublicBrowseCollection(platform: .netease, nativeID: "c", title: "C")
        await pages.playlist("lists|q|wy|1", .init(items: [a], total: 3, next: 2))
        await pages.playlist("lists|q|wy|2", .init(items: [b], total: 3, next: 3))
        await pages.playlist("lists|q|wy|3", .init(items: [c], total: 3))
        model.submit("q", preferences: .init(songPlatforms: [], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        model.more(.playlists); await model.finishPendingReads()
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.playlists.map(\.id), [a.id, b.id, c.id])
        XCTAssertTrue(model.playlistFailures.isEmpty)
        XCTAssertFalse(model.morePlaylists)
        let calls = await pages.log()
        XCTAssertEqual(calls, ["lists|q|wy|1", "lists|q|wy|2", "lists|q|wy|3"])
    }
    func testPlaylistFailureRetainsRowsAndRetryUsesFailedPage() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let original = MusicPublicBrowseCollection(platform: .netease, nativeID: "original", title: "Original")
        let changed = MusicPublicBrowseCollection(platform: .netease, nativeID: "changed", title: "Changed")
        await pages.playlist("lists|q|wy|1", .init(items: [original], total: 2, next: 2))
        await pages.failPlaylist("lists|q|wy|2", .rateLimited)
        model.submit("q", preferences: .init(songPlatforms: [], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        model.more(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.playlists.map(\.id), [original.id])
        XCTAssertEqual(model.playlistFailures.first?.error, .rateLimited)
        await pages.failPlaylist("lists|q|wy|2", nil)
        await pages.playlist("lists|q|wy|2", .init(items: [changed], total: 2))
        model.retry(.playlists); await model.finishPendingReads()
        XCTAssertEqual(model.playlists.map(\.id), [original.id, changed.id])
        XCTAssertTrue(model.playlistFailures.isEmpty)
        let calls = await pages.log()
        XCTAssertEqual(calls, ["lists|q|wy|1", "lists|q|wy|2", "lists|q|wy|2"])
    }
    func testCancelledPlaylistReadCannotPublishOverNewQuery() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let old = MusicPublicBrowseCollection(platform: .netease, nativeID: "old", title: "Old")
        let new = MusicPublicBrowseCollection(platform: .netease, nativeID: "new", title: "New")
        await pages.playlist("lists|old|wy|1", .init(items: [old], total: 2, next: 2))
        model.submit("old", preferences: .init(songPlatforms: [], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        let entered = expectation(description: "next page entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await pages.gate("lists|old|wy|2", gate)
        model.more(.playlists)
        await fulfillment(of: [entered], timeout: 2)
        await pages.playlist("lists|new|wy|1", .init(items: [new], total: 1))
        model.submit("new", preferences: .init(songPlatforms: [], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        await gate.release()
        XCTAssertEqual(model.query, "new")
        XCTAssertEqual(model.playlists.map(\.id), [new.id])
        XCTAssertTrue(model.playlistFailures.isEmpty)
    }
    func testLaterRoundRemainsStableUntilEveryPlatformsRequestSettles() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("first")], next: 2))
        await pages.song("q|tx|1", .init(tracks: [track("first-qq", platform: "tx", songID: "first", title: "first")], next: 2))
        model.submit("q", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        let before = model.songs.map(\.id), score = model.songs.map(\.score)
        let wyEntered = expectation(description: "WY later page read"), qqEntered = expectation(description: "QQ later page held")
        let wyGate = MusicCollectionGate { wyEntered.fulfill() }, qqGate = MusicCollectionGate { qqEntered.fulfill() }
        await wyGate.release()
        await pages.song("q|wy|2", .init(tracks: [track("new")]))
        await pages.song("q|tx|2", .init(tracks: [track("new-qq", platform: "tx", songID: "new", title: "new")]))
        await pages.gate("q|wy|2", wyGate); await pages.gate("q|tx|2", qqGate)
        model.more(.songs)
        await fulfillment(of: [wyEntered, qqEntered], timeout: 2)
        XCTAssertEqual(model.songs.map(\.id), before)
        XCTAssertEqual(model.songs.map(\.score), score)
        await qqGate.release(); await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 2)
        XCTAssertEqual(Array(model.songs.prefix(before.count)).map(\.id), before)
        XCTAssertEqual(model.songs.last?.matchedPlatforms, ["tx", "wy"])
    }
    func testFailedRangeRetriesButSuccessfulSpeculativePagesAreReusedFromCache() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        for page in 1...4 {
            await pages.song("q|tx|\(page)", .init(tracks: [track("s\(page)", platform: "tx")],
                next: page < 4 ? page + 1 : nil, failure: page == 2 ? .rateLimited : nil))
        }
        model.submit("q", preferences: .init(songPlatforms: [.qq], searchPlaylists: false, pages: 4, concurrency: 4))
        await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["s1"])
        XCTAssertEqual(model.songFailures.first?.error, .rateLimited)
        await pages.song("q|tx|2", .init(tracks: [track("s2", platform: "tx")], next: 3))
        model.retry(.songs); await model.finishPendingReads()
        XCTAssertEqual(model.songs.map(\.id), ["s1", "s2", "s3", "s4"])
        let calls = await pages.log()
        XCTAssertEqual(calls.filter { $0 == "q|tx|1" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|tx|2" }.count, 2)
        XCTAssertEqual(calls.filter { $0 == "q|tx|3" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "q|tx|4" }.count, 1)
        XCTAssertFalse(model.moreSongs)
    }
    func testCacheDurationChangeStillHitsExistingEntryAndAppliesToLaterMisses() async {
        let pages = MusicSearchTestPages(), clock = MusicSearchCacheClock()
        let model = session(pages, clock: { clock.read() })
        await pages.song("q|wy|1", .init(tracks: [track("a")]))
        var settings = MusicSearchPreferences(songPlatforms: [.netease], searchPlaylists: false)
        model.submit("q", preferences: settings); await model.finishPendingReads()
        clock.advance(3599)
        settings.songs.cacheMinutes = 1
        model.submit("q", preferences: settings); await model.finishPendingReads()
        let warm = await pages.log(); XCTAssertEqual(warm.count, 1)
        clock.advance(2)
        model.submit("q", preferences: settings); await model.finishPendingReads()
        let expired = await pages.log(); XCTAssertEqual(expired.count, 2)
        clock.advance(61)
        model.submit("q", preferences: settings); await model.finishPendingReads()
        let expiredShort = await pages.log(); XCTAssertEqual(expiredShort.count, 3)
    }
    func testNeteaseBatchContainsReferencesFromEveryConsumedPage() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        await pages.song("q|wy|1", .init(tracks: [track("a")], next: 2))
        await pages.song("q|wy|2", .init(tracks: [track("b")]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false, pages: 4, concurrency: 1))
        await model.finishPendingReads()
        let calls = await pages.ranges()
        XCTAssertEqual(calls.map(\.limit), [30, 30])
        let request = try model.makeBatchRequest()
        XCTAssertEqual(request.targets.map(\.track.songId), ["a", "b"])
        XCTAssertEqual(model.songs.map(\.id), ["a", "b"])
    }
    func testTerminalSongAndPlaylistPagesNeverLoadAnotherPageForAnyPlatform() async throws {
        for platform in MusicPublicBrowsePlatform.allCases {
            for (count, total) in [(0, 100), (2, 100), (30, 30), (30, 29)] {
                let tracks = (0..<count).map { track("s\($0)", platform: platform.rawValue) }
                let lists = (0..<count).map {
                    MusicPublicBrowseCollection(platform: platform, nativeID: "p\($0)", title: "Playlist \($0)")
                }
                var songCalls: [Int] = [], playlistCalls: [Int] = []
                let model = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                    songCalls.append(page)
                    return .init(platform: platform.rawValue, query: query, page: page, limit: limit,
                        total: total, nextPage: nil, items: tracks)
                }, readPlaylists: { platform, query, page, limit in
                    playlistCalls.append(page)
                    return .init(platform: platform, query: query, page: page, limit: limit,
                        total: total, nextPage: nil, items: lists)
                }, readRecording: { _ in throw MusicServiceError.unsupported })
                model.setAccess(available: true, generation: UUID()); model.appear()
                model.submit("q", preferences: .init(songPlatforms: [platform], playlistPlatforms: [platform],
                    pages: 10, concurrency: 1))
                await model.finishPendingReads()
                model.more(.songs); model.more(.playlists)
                model.retry(.songs); model.retry(.playlists)
                await model.finishPendingReads()
                XCTAssertEqual(songCalls, [1]); XCTAssertEqual(playlistCalls, [1])
                XCTAssertEqual(model.songs.count, count); XCTAssertEqual(model.playlists.count, count)
                XCTAssertTrue(model.songsLoaded); XCTAssertTrue(model.playlistsLoaded)
                XCTAssertTrue(model.songFailures.isEmpty); XCTAssertTrue(model.playlistFailures.isEmpty)
                XCTAssertFalse(model.moreSongs); XCTAssertFalse(model.morePlaylists)
                model.disappear()
            }
        }
    }
    func testMoreRowsThanRequestedAreConsumedAndTheirRealCursorRemainsAvailable() async {
        let tracks = (0..<31).map { track("s\($0)") }
        let lists = (0..<31).map {
            MusicPublicBrowseCollection(platform: .netease, nativeID: "p\($0)", title: "Playlist \($0)")
        }
        let model = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
            .init(platform: platform.rawValue, query: query, page: page, limit: limit,
                  total: 100, nextPage: page + 1, items: tracks)
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit,
                  total: 100, nextPage: page + 1, items: lists)
        }, readRecording: { _ in throw MusicServiceError.unsupported })
        model.setAccess(available: true, generation: UUID()); model.appear()
        model.submit("q", preferences: .init(songPlatforms: [.netease], playlistPlatforms: [.netease]))
        await model.finishPendingReads()
        XCTAssertEqual(model.songs.count, 31); XCTAssertEqual(model.playlists.count, 31)
        XCTAssertTrue(model.songFailures.isEmpty)
        XCTAssertTrue(model.playlistFailures.isEmpty)
        XCTAssertTrue(model.moreSongs); XCTAssertTrue(model.morePlaylists)
        model.disappear()
    }
    func testMiguCursorSurvivesExpandedOrDeduplicatedEditionsAndBatchKeepsEveryRecording() async throws {
        for count in [1, 60] {
            let pages = MusicSearchTestPages(), model = session(pages)
            let first = (0..<count).map { track("mg-\($0)", platform: "mg") }
            let tail = track("tail", platform: "mg")
            await pages.song("q|mg|1", .init(tracks: first, next: 2, total: 31))
            await pages.song("q|mg|2", .init(tracks: [tail], total: 31))
            model.submit("q", preferences: .init(songPlatforms: [.migu], searchPlaylists: false,
                                                 pages: 10, concurrency: 1))
            await model.finishPendingReads()
            let calls = await pages.ranges(), request = try model.makeBatchRequest()
            XCTAssertEqual(calls.map(\.key), ["q|mg|1", "q|mg|2"])
            XCTAssertEqual(calls.map(\.limit), [30, 30])
            XCTAssertEqual(model.songs.count, count + 1)
            XCTAssertTrue(model.songFailures.isEmpty); XCTAssertFalse(model.moreSongs)
            XCTAssertEqual(request.targets.map(\.track.songId), first.map(\.songId) + ["tail"])
            XCTAssertEqual(request.targets.map(\.track.ref), first.map(\.ref) + [tail.ref])
            XCTAssertEqual(Set(request.targets.map(\.track.songId)).count, count + 1)
            model.disappear()
        }
    }
    func testExpandedPlaylistPageKeepsContinuationAfterEveryPreviouslyReturnedItem() async {
        let pages = MusicSearchTestPages(), model = session(pages)
        let first = (0..<60).map {
            MusicPublicBrowseCollection(platform: .migu, nativeID: "p-\($0)", title: "Playlist \($0)")
        }
        let tail = MusicPublicBrowseCollection(platform: .migu, nativeID: "tail", title: "Tail")
        await pages.playlist("lists|q|mg|1", .init(items: first, total: 31, next: 2))
        await pages.playlist("lists|q|mg|2", .init(items: [tail], total: 31))
        model.submit("q", preferences: .init(songPlatforms: [], playlistPlatforms: [.migu], pages: 10, concurrency: 1))
        await model.finishPendingReads()
        let calls = await pages.ranges()
        XCTAssertEqual(calls.map(\.key), ["lists|q|mg|1", "lists|q|mg|2"])
        XCTAssertEqual(calls.map(\.limit), [30, 30])
        XCTAssertEqual(model.playlists.map(\.id), first.map(\.id) + [tail.id])
        XCTAssertTrue(model.playlistFailures.isEmpty); XCTAssertFalse(model.morePlaylists)
        model.disappear()
    }
    func testBatchUsesPreferredPermanentReferenceAndRecordingReadKeepsAllKnownSources() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        for platform in MusicPublicBrowsePlatform.allCases {
            await pages.song("q|\(platform.rawValue)|1",
                .init(tracks: [track(platform.rawValue, platform: platform.rawValue, songID: "recording")]))
        }
        let order: [MusicPublicBrowsePlatform] = [.migu, .qq, .netease, .kuwo, .kugou]
        model.submit("q", preferences: .init(songPlatforms: order, searchPlaylists: false))
        await model.finishPendingReads()
        let request = try model.makeBatchRequest()
        XCTAssertEqual(request.targets.count, 1)
        XCTAssertEqual(request.targets[0].track.sources.first?.platform, "mg")
        XCTAssertEqual(request.targets[0].track.songId, "recording")
        let restored = try await model.refreshTrack(XCTUnwrap(model.songs.first))
        XCTAssertEqual(Set(restored.sources.map(\.platform)), Set(order.map(\.rawValue)))
    }
    func testBatchRetainsDifferentNativeSourcesWithinTheSamePlatform() async throws {
        let pages = MusicSearchTestPages(), model = session(pages)
        let first = track("first", songID: "recording"), second = track("second", songID: "recording")
        await pages.song("q|wy|1", .init(tracks: [first, second]))
        model.submit("q", preferences: .init(songPlatforms: [.netease], searchPlaylists: false))
        await model.finishPendingReads()
        let row = try XCTUnwrap(model.songs.first)
        XCTAssertEqual(model.songs.count, 1)
        let request = try model.makeBatchRequest(for: row)
        XCTAssertEqual(Set(row.variants.map(\.ref)), Set([first.ref, second.ref]))
        XCTAssertEqual(request.targets[0].track.songId, first.songId)
        let restored = try await model.refreshTrack(row)
        XCTAssertEqual(restored.sources, first.sources + second.sources)
        model.disappear()
    }
}

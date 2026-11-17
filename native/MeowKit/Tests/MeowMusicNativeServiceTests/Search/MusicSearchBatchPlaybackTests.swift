import XCTest
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private final class MusicSearchBatchClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = NativeMusicFixture.date
    func read() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: Double) { lock.lock(); date.addTimeInterval(seconds); lock.unlock() }
}

private actor MusicSearchBatchQQ: MusicPlatformFixtureResponding {
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        switch request.operation {
        case "search":
            return try MusicPlatformFixture.page(await search(query: request.parameters[music: "keyword"].musicText,
                page: Int(request.parameters[music: "page"].musicNumber ?? 1),
                limit: Int(request.parameters[music: "limit"].musicNumber ?? 30)), platform: request.platform)
        case "lyrics": return .object(["text": .string(try await lyrics(mid: request.parameters[music: "musicInfo"][music: "songmid"].musicText).text)])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }

    struct Call: Equatable, Sendable { let query: String; let page: Int; let limit: Int }
    let count: Int
    private var calls: [Call] = []
    private var replacement: Int?
    private var failedPage: Int?
    private var gate: NativeMusicGate?
    init(count: Int) { self.count = count }
    func replace(_ index: Int?) { replacement = index }
    func fail(_ page: Int?) { failedPage = page }
    func suspend(_ gate: NativeMusicGate) { self.gate = gate }
    func log() -> [Call] { calls }
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage {
        calls.append(.init(query: query, page: page, limit: limit))
        if let gate { self.gate = nil; await gate.wait() }
        if page == failedPage { throw MusicServiceError.upstreamFailed }
        let lower = (page - 1) * limit, upper = min(page * limit, count)
        guard lower < upper else { return .init(items: [], total: count) }
        let items = (lower..<upper).map { index in
            // Same title/artist/duration is not authority to choose a new ID.
            index == replacement ? NativeMusicFixture.track("Substitute") : NativeMusicFixture.track("Track\(index)")
        }
        return .init(items: items, total: count, nextPage: upper < count ? page + 1 : nil)
    }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
}

@MainActor final class MusicSearchBatchPlaybackTests: XCTestCase {
    private func setup(_ count: Int) async throws -> (NativeMusicService, NativeMusicTestLibrary, MusicSearchBatchQQ, MusicSearchBatchClock) {
        let document = NativeMusicTestLibrary(), peer = MusicSearchBatchQQ(count: count), clock = MusicSearchBatchClock()
        let service = NativeMusicService(database: document, platformClient: try peer.makeClient(), clock: { clock.read() })
        try await service.setAccess(available: true, generation: 1); return (service, document, peer, clock)
    }
    private func capture(_ service: NativeMusicService, count: Int) async throws -> [MusicSearchBatchTarget] {
        var targets: [MusicSearchBatchTarget] = []
        for page in 1...((count + 29) / 30) {
            let result = try await service.search(platform: "tx", query: "original query", page: page, limit: 30)
            targets += result.items.map { .init(track: $0) }
        }; return targets
    }
    func testDirectSearchClickUsesOnlyClickedSongAndItsFixedRecommendationSeed() async throws {
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([MusicNativePublicBrowseFixture.track("101", title: "弑仙"), MusicNativePublicBrowseFixture.track("102", title: "重生")], platform: "wy")])
        let account = SimilarModeAccountFixture([["301"]]), document = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(document, accounts: account, platform: peer)
        let results = try await service.search(platform: "wy", query: "弑仙", page: 1, limit: 30), clicked = try XCTUnwrap(results.items.last)
        let state = try await service.replaceSearchBatch(.init(query: "弑仙", targets: [.init(track: clicked)]), expectedRevision: 0, intent: .init()), queue = try await service.queue()
        XCTAssertEqual(queue.entries.map(\.track.title), ["重生"]); XCTAssertEqual(queue.entries[0].track.sources[0].nativeId, "102")
        let saved = try MusicDocument(document.read(), runID: "fixture"); XCTAssertEqual(saved.queue["source"]["seedEntryId"].string, state.currentId)
        let before = await account.count(); XCTAssertEqual(before, 0)
        let availability = try await service.sourceAvailability(for: queue.entries[0].track.ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: XCTUnwrap(state.currentId), selection: XCTUnwrap(availability.options.first).selection(quality: "128k"))
        _ = try await service.recordSimilarPlaybackFact(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId, resourceId: candidate.resourceId, kind: .playing, position: 1))
        let preview = try await service.previewQueueRecommendations(); XCTAssertEqual(preview?.candidates.map(\.song.title), ["Song 301"])
        let requests = await account.requests; XCTAssertEqual(requests, [.songRecommendations(seedID: "102", offset: 0)])
        let unchanged = try await service.queue(); XCTAssertEqual(unchanged.entries.map(\.id), queue.entries.map(\.id)); await service.close()
    }
    func testDirectNeteaseSearchWithoutRecommendationCapabilityHasNoCandidateSession() async throws {
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([MusicNativePublicBrowseFixture.track("101", title: "Song")], platform: "wy")]), service = try await NativeMusicFixture.service(platform: peer)
        let results = try await service.search(platform: "wy", query: "Song", page: 1, limit: 30)
        _ = try await service.replaceSearchBatch(.init(query: "Song", targets: [.init(track: XCTUnwrap(results.items.first))]), expectedRevision: 0, intent: .init())
        let snapshot = try await service.queueRecommendationSnapshot(), queue = try await service.queue(); XCTAssertNil(snapshot); XCTAssertEqual(queue.entries.map(\.track.title), ["Song"]); await service.close()
    }
    func testSixHundredFiftyPermanentReferencesCommitInOneQueueCASWithoutRenewal() async throws {
        let (service, document, peer, clock) = try await setup(650), targets = try await capture(service, count: 650)
        clock.advance(601); let writes = document.snapshot().2, calls = await peer.log()
        let state = try await service.replaceSearchBatch(.init(query: "original query", targets: targets, startIndex: 649), expectedRevision: 0, intent: .init()), queue = try await service.queue()
        XCTAssertEqual(state.total, 650); XCTAssertEqual(state.revision, 1); XCTAssertEqual(document.snapshot().2, writes + 1)
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId)); XCTAssertEqual(queue.entries.last?.id, state.currentId)
        let after = await peer.log(); XCTAssertEqual(after, calls); XCTAssertEqual(calls.count, 22)
        let saved = try MusicDocument(document.snapshot().0, runID: "fixture")
        XCTAssertTrue(saved.queue["entries"].array.allSatisfy { $0["song"] == .null && $0["context"]["searchKeyword"].string == "original query" })
        await service.close()
    }
    func testUnknownRepositoryReferenceAndLaterReadFailureLeaveQueueAndHistoryUnchanged() async throws {
        let (service, document, peer, _) = try await setup(31), targets = try await capture(service, count: 31)
        let id = UUID().uuidString.lowercased(), foreign = MusicRemoteTrack(ref: .init(kind: .library, id: id), songId: id, title: "Missing", sources: [])
        let baseline = try await service.read(), before = document.snapshot()
        await assertMusicFailure(.notFound) { _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets + [.init(track: foreign)]), expectedRevision: 0, intent: .init()) }
        XCTAssertEqual(document.snapshot().0, before.0)
        await peer.fail(2); await assertMusicFailure(.upstreamFailed) { _ = try await service.search(platform: "tx", query: "q", page: 2, limit: 30) }
        let state = try await service.read()
        XCTAssertEqual(state.queue, baseline.queue); XCTAssertEqual(state.history, baseline.history)
        XCTAssertEqual(state.playback, baseline.playback); XCTAssertEqual(state.library, baseline.library)
        XCTAssertEqual(document.snapshot().0, before.0); XCTAssertEqual(document.snapshot().2, before.2)
        let calls = await peer.log(); XCTAssertEqual(calls.map(\.page), [1, 2, 2]); await service.close()
    }
    func testOverTwoThousandSelectionKeepsEveryOccurrenceWithoutTruncation() async throws {
        let (service, document, peer, _) = try await setup(1), targets = try await capture(service, count: 1), writes = document.snapshot().2
        let result = try await service.replaceSearchBatch(.init(query: "q", targets: Array(repeating: targets[0], count: 2001), startIndex: 2000), expectedRevision: 0, intent: .init()), queue = try await service.queue()
        XCTAssertEqual(result.total, 2001); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 2001); XCTAssertEqual(queue.currentId, queue.entries[2000].id)
        XCTAssertEqual(document.snapshot().2, writes + 1); let calls = await peer.log(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testOversizedDisplayTextCannotReplaceRepositoryFactsOrCreateAShadowPayloadBudget() async throws {
        let (service, _, _, _) = try await setup(1), targets = try await capture(service, count: 1), track = targets[0].track
        let display = MusicRemoteTrack(ref: track.ref, songId: track.songId, title: String(repeating: "x", count: 65 * 1024), sources: track.sources)
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: [.init(track: display)]), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries[0].track.title, track.title); await service.close()
    }
    func testActualStorageByteLimitIsNotAFalseSuccessOrPartialQueue() async throws {
        let (service, document, _, _) = try await setup(1), targets = try await capture(service, count: 1), before = document.snapshot().0
        document.fail(.limit)
        await assertMusicFailure(.limit) { _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init()) }
        XCTAssertEqual(document.snapshot().0, before); await service.close()
    }
    func testDifferentSearchPageSizesNeedNoPageRenewalForPermanentRecords() async throws {
        let (service, document, peer, clock) = try await setup(8)
        let first = try await service.search(platform: "tx", query: "q", page: 1, limit: 4), second = try await service.search(platform: "tx", query: "q", page: 2, limit: 4), targets = (first.items + second.items).map { MusicSearchBatchTarget(track: $0) }
        clock.advance(601); let writes = document.snapshot().2
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init())
        let calls = await peer.log(); XCTAssertEqual(calls.map(\.page), [1, 2]); XCTAssertEqual(calls.map(\.limit), [4, 4]); XCTAssertEqual(document.snapshot().2, writes + 1); await service.close()
    }
    func testMiguAllCanonicalEditionsRemainAvailableAfterFormerExpiry() async throws {
        let rows = (0..<40).map { MusicNativeCandidateDiscoveryFixture.track("mg", id: String(600000 + $0), title: "Synthetic \($0)") }
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page(rows, platform: "mg", total: 100)]), clock = MusicSearchBatchClock(), service = try await NativeMusicFixture.service(platform: peer, clock: { clock.read() })
        let first = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30); XCTAssertEqual(first.items.count, 40)
        clock.advance(601); let selected = try XCTUnwrap(first.items.last), current = try await service.searchRecording(.init(query: "Synthetic", targets: [.init(track: selected)]))
        XCTAssertEqual(current.songId, selected.songId); XCTAssertEqual(current.sources, selected.sources)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testOldDisplayEvidenceCannotRebindStableSourceOrOverrideCurrentRepository() async throws {
        let (service, _, _, _) = try await setup(1), targets = try await capture(service, count: 1), original = targets[0].track
        let stale = MusicRemoteTrack(ref: original.ref, songId: original.songId, title: "Old display", duration: 270, sources: original.sources)
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: [.init(track: stale)]), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries[0].track.duration, original.duration); XCTAssertEqual(queue.entries[0].track.title, original.title); await service.close()
    }
    func testThreePlatformBatchKeepsSelectedOrderAndRealSharedOwnership() async throws {
        let peer = MusicNativeDiscoveryPlatforms(), service = try await NativeMusicFixture.service(platform: peer)
        var targets: [MusicSearchBatchTarget] = []
        for platform in ["kg", "tx", "wy"] {
            let page = try await service.search(platform: platform, query: "q", page: 1, limit: 30); targets.append(.init(track: try XCTUnwrap(page.items.first)))
        }
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        let current = try await service.repositoryTracks(songIDs: Set(targets.map(\.track.songId)))
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.sources == current[$0.track.songId]?.sources }); await service.close()
    }
    func testFusedRecordingPersistsAllFiveSourceLocatorsWithReferenceOnlyQueueAndFavorites() async throws {
        let service = try await NativeMusicFixture.service(), platforms = ["mg", "tx", "wy", "kw", "kg"]
        var tracks: [MusicRemoteTrack] = []
        for platform in platforms {
            let rows = try await service.adoptSongs([MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(platform, id: platform + "-locator"))], authoritative: true)
            tracks.append(try await service.repositoryTrack(rows[0]))
        }
        let recording = try await service.searchRecording(.init(query: "q", targets: [.init(track: tracks[0])]))
        XCTAssertEqual(Set(recording.sources.map(\.platform)), Set(platforms)); XCTAssertEqual(Set(tracks.map(\.songId)).count, 1)
        let favorites = try await service.collections(), favorite = try XCTUnwrap(favorites.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorite, name: nil, refs: [recording.ref], order: nil)
        let favoriteTracks = try await service.tracks(in: saved); XCTAssertEqual(favoriteTracks[0].sources, recording.sources)
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: [.init(track: recording)]), expectedRevision: 0, intent: .init())
        let document = try await service.read(), entry = document.queue["entries"].array[0], stored = (try document.fixtureSong(recording.songId))
        XCTAssertEqual(entry["song"], .null); XCTAssertEqual(entry["songId"].string, recording.songId); XCTAssertEqual(stored["sources"].array.count, 5)
        let feedback = try MusicNativeFeedbackMapping.targets(song: stored, context: .obj()); XCTAssertEqual(Set(feedback.map(\.platform)), Set(MusicAccountPlatform.allCases)); await service.close()
    }
    func testSamePlatformSourcesStayDistinctInFavoritesAndBatchQueue() async throws {
        let service = try await NativeMusicFixture.service(); var tracks: [MusicRemoteTrack] = []
        for id in ["101", "102"] {
            let rows = try await service.adoptSongs([MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: id))], authoritative: true)
            tracks.append(try await service.repositoryTrack(rows[0]))
        }
        let current = try await service.searchRecording(.init(query: "q", targets: [.init(track: tracks[0])]))
        XCTAssertEqual(Set(current.sources.map(\.nativeId)), ["101", "102"])
        let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorites, name: nil, refs: [current.ref], order: nil), rows = try await service.tracks(in: saved)
        XCTAssertEqual(rows[0].sources, current.sources)
        _ = try await service.replaceSearchBatch(.init(query: "q", targets: [.init(track: current)]), expectedRevision: 0, intent: .init()); await service.close()
    }
    func testRevokedIntentPreventsTheActualQueueCASWithoutASyntheticRefresh() async throws {
        let (service, document, _, _) = try await setup(1), targets = try await capture(service, count: 1), before = document.snapshot().0
        let gate = NativeMusicGate(), intent = MusicQueueAuthorization(), task = Task { await gate.wait(); return try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: intent) }
        await gate.started(); intent.revoke(); await gate.release(); await assertMusicFailure(.staleAccess) { _ = try await task.value }
        XCTAssertEqual(document.snapshot().0, before); await service.close()
    }
    func testQueueRevisionChangeCannotBeOverwrittenByDelayedSelection() async throws {
        let (service, document, _, _) = try await setup(1), targets = try await capture(service, count: 1), gate = NativeMusicGate()
        let task = Task { await gate.wait(); return try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init()) }
        await gate.started(); _ = try await service.changeQueue(.clear, expectedRevision: 0); let changed = document.snapshot().0
        await gate.release(); await assertMusicFailure(.staleRevision) { _ = try await task.value }; XCTAssertEqual(document.snapshot().0, changed); await service.close()
    }
    func testNativeAccessRevocationPreventsDelayedSelectionCommit() async throws {
        let (service, document, _, _) = try await setup(1), targets = try await capture(service, count: 1), before = document.snapshot().0, gate = NativeMusicGate()
        let task = Task { await gate.wait(); return try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init()) }
        await gate.started(); try await service.setAccess(available: false, generation: 2); await gate.release()
        await assertMusicFailure(.accessUnavailable) { _ = try await task.value }; XCTAssertEqual(document.snapshot().0, before); await service.close()
    }
    func testFreshDisplayedRecordingDoesNotRequireAnotherSearchToPlay() async throws {
        let (service, document, peer, _) = try await setup(1), targets = try await capture(service, count: 1), writes = document.snapshot().2
        await peer.fail(1); _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init())
        let calls = await peer.log(), queue = try await service.queue(); XCTAssertEqual(calls.count, 1); XCTAssertEqual(queue.entries[0].track.songId, targets[0].track.songId)
        XCTAssertEqual(document.snapshot().2, writes + 1); await service.close()
    }
    func testIncompleteKnownSourceRetainsPermanentIDDuringAuthoritativeCorrection() async throws {
        let service = try await NativeMusicFixture.service()
        let first = try await service.adoptSongs([MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "known-id", artist: "未知歌手", duration: 0))], authoritative: true)
        let old = try await service.repositoryTrack(first[0]), collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorites, name: nil, refs: [old.ref], order: nil)
        _ = try await service.adoptSongs([MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "known-id"))], authoritative: true)
        _ = try await service.adoptSongs([MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "new-id"))], authoritative: true)
        let current = try await service.tracks(in: saved); XCTAssertEqual(current.count, 1); XCTAssertEqual(current[0].songId, old.songId); XCTAssertEqual(current[0].artist, "Fixture")
        XCTAssertEqual(Set(current[0].sources.map(\.platform)), ["tx", "wy"]); await service.close()
    }
    func testMixedOlderAndNewerSearchResultsNeedNoRenewalAndReadCurrentRepository() async throws {
        let (service, _, peer, clock) = try await setup(31), first = try await service.search(platform: "tx", query: "q", page: 1, limit: 30)
        clock.advance(601); let second = try await service.search(platform: "tx", query: "q", page: 2, limit: 30), targets = (first.items + second.items).map { MusicSearchBatchTarget(track: $0) }
        await peer.fail(2); _ = try await service.replaceSearchBatch(.init(query: "q", targets: targets), expectedRevision: 0, intent: .init())
        let calls = await peer.log(), queue = try await service.queue(); XCTAssertEqual(calls.map(\.page), [1, 2]); XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId)); await service.close()
    }
}

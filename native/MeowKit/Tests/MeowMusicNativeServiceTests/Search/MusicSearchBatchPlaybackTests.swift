import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class MusicSearchBatchClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = NativeMusicFixture.date
    func read() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: Double) { lock.lock(); date.addTimeInterval(seconds); lock.unlock() }
}

private actor MusicSearchBatchQQ: MusicPublicQQProviding {
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
    private func setup(_ count: Int) async throws ->
        (NativeMusicService, NativeMusicMemoryDocument, MusicSearchBatchQQ, MusicSearchBatchClock) {
        let document = NativeMusicMemoryDocument(), qq = MusicSearchBatchQQ(count: count), clock = MusicSearchBatchClock()
        let service = NativeMusicService(document: document, qq: qq, enabledPlatforms: [.netease, .qq, .kugou],
            transport: NativeMusicNoNetwork(), clock: { clock.read() })
        try await service.setAccess(available: true, generation: 1)
        return (service, document, qq, clock)
    }
    private func capture(_ service: NativeMusicService, count: Int) async throws -> [MusicSearchBatchTarget] {
        var targets: [MusicSearchBatchTarget] = []
        for page in 1...((count + 29) / 30) {
            let result = try await service.search(platform: "tx", query: "original query", page: page, limit: 30)
            targets += result.items.map { .init(platform: .qq, page: page, track: $0) }
        }
        return targets
    }
    func testRealOwnerCommitsMoreThanHandleCapacityWithAllOldHandlesExpiredInOneCAS() async throws {
        let (service, document, qq, clock) = try await setup(650)
        let targets = try await capture(service, count: 650)
        clock.advance(601)
        do {
            _ = try await service.changeQueue(.replace([targets[0].track.ref], index: 0), expectedRevision: 0)
            XCTFail("The fixture must actually exercise expired production handles")
        } catch { XCTAssertEqual(error as? MusicServiceError, .handleExpired) }
        XCTAssertEqual(document.snapshot().2, 0)
        let request = MusicSearchBatchRequest(query: "original query", targets: targets, startIndex: 649)
        let state = try await service.replaceSearchBatch(request, expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(state.total, 650)
        XCTAssertEqual(state.revision, 1)
        XCTAssertEqual(document.snapshot().2, 1)
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        XCTAssertEqual(queue.entries.last?.id, state.currentId)
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.ref.kind == .queue })
        let saved = try MusicDocument(document.snapshot().0, runID: "fixture")
        XCTAssertTrue(saved.queue["entries"].array.allSatisfy {
            $0["context"]["sourceType"].string == "search" &&
            $0["context"]["searchKeyword"].string == request.query
        })
        let calls = await qq.log()
        XCTAssertEqual(calls.count, 44)
        XCTAssertEqual(Array(calls.suffix(22)), Array(calls.prefix(22)))
        // Batch playback never renews or depends on the original handle cache.
        do {
            _ = try await service.changeQueue(.replace([targets[0].track.ref], index: 0), expectedRevision: 1)
            XCTFail("Batch writer must not register display handles")
        } catch { XCTAssertEqual(error as? MusicServiceError, .handleExpired) }
        XCTAssertEqual(document.snapshot().2, 1)
        await service.close()
    }
    func testMissingExactTargetAndLaterPageFailureLeaveNoPartialQueueOrHistory() async throws {
        let (service, document, qq, clock) = try await setup(31)
        let targets = try await capture(service, count: 31)
        clock.advance(601)
        let request = MusicSearchBatchRequest(query: "original query", targets: targets)
        let before = document.snapshot()
        await qq.replace(30)
        do {
            _ = try await service.replaceSearchBatch(request, expectedRevision: 0, intent: .init())
            XCTFail("Must not substitute the missing recording")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(document.snapshot().0, before.0)
        XCTAssertEqual(document.snapshot().2, before.2)
        await qq.replace(nil); await qq.fail(2)
        do {
            _ = try await service.replaceSearchBatch(request, expectedRevision: 0, intent: .init())
            XCTFail("No prefix queue may be committed on a later-page failure")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        XCTAssertEqual(document.snapshot().0, before.0)
        XCTAssertEqual(document.snapshot().2, before.2)
        await service.close()
    }
    func testOverTwoThousandSelectionRemainsValidWithoutTruncation() async throws {
        let (service, document, qq, _) = try await setup(1)
        let song = try MusicModel.normalize(NativeMusicFixture.track("Track0"))
        let track = try MusicModel.track(song, .init(kind: .handle, id: "not-a-registered-handle"))
        let target = MusicSearchBatchTarget(platform: .qq, page: 1, track: track)
        let request = MusicSearchBatchRequest(query: "original query",
            targets: Array(repeating: target, count: 2001), startIndex: 2000)
        XCTAssertNoThrow(try request.validate())
        XCTAssertEqual(request.targets.count, 2001)
        XCTAssertEqual(request.startIndex, 2000)
        let calls = await qq.log()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(document.snapshot().2, 0)
        await service.close()
    }
    func testOversizedSingleRecordingStillFailsTheExistingByteBudget() async throws {
        let song = try MusicModel.normalize(NativeMusicFixture.track("Track0"))
        let track = try MusicModel.track(song, .init(kind: .handle, id: "not-a-registered-handle"))
        let oversized = MusicRemoteTrack(ref: track.ref, songId: track.songId,
            title: String(repeating: "x", count: 65 * 1024), artist: track.artist,
            album: track.album, duration: track.duration, sources: track.sources)
        let target = MusicSearchBatchTarget(platform: .qq, page: 1, track: oversized)
        let request = MusicSearchBatchRequest(query: "original query", targets: [target])
        XCTAssertThrowsError(try request.validate()) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }
    func testAggregateByteBudgetStillRejectsOtherwiseValidRecordings() throws {
        let song = try MusicModel.normalize(NativeMusicFixture.track("Track0"))
        let original = try MusicModel.track(song, .init(kind: .handle, id: "synthetic-handle"))
        let track = MusicRemoteTrack(ref: original.ref, songId: original.songId,
            title: String(repeating: "x", count: 60 * 1024), artist: original.artist,
            album: original.album, duration: original.duration, sources: original.sources)
        let target = MusicSearchBatchTarget(platform: .qq, page: 1, track: track)
        let size = try JSONEncoder().encode(track).count
        XCTAssertLessThanOrEqual(size, 64 * 1024)
        XCTAssertGreaterThan(size * 70, 4 * 1024 * 1024)
        XCTAssertNoThrow(try MusicSearchBatchRequest(query: "q", targets: [target]).validate())
        XCTAssertThrowsError(try MusicSearchBatchRequest(query: "q", targets: Array(repeating: target, count: 70)).validate()) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }
    func testRenewalPreservesEachOriginsOnePageLimitAndOffset() async throws {
        let (service, document, qq, clock) = try await setup(8)
        let first = try await service.search(platform: "tx", query: "original query", page: 1, limit: 4)
        let second = try await service.search(platform: "tx", query: "original query", page: 2, limit: 4)
        let targets = first.items.map { MusicSearchBatchTarget(platform: .qq, page: 1, limit: 4, track: $0) } +
            second.items.map { MusicSearchBatchTarget(platform: .qq, page: 2, limit: 4, track: $0) }
        clock.advance(601)
        _ = try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
            expectedRevision: 0, intent: .init())
        let calls = await qq.log(), queue = try await service.queue()
        XCTAssertEqual(calls.map(\.page), [1, 2, 1, 2])
        XCTAssertEqual(calls.map(\.limit), [4, 4, 4, 4])
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        XCTAssertEqual(document.snapshot().2, 1)
        await service.close()
    }
    func testMiguExpiredOriginRenewsOneRawGroupPageWithEveryEditionAvailable() async throws {
        let rows = (0..<40).map {
            MusicNativeMiguSearchFixture.song(id: String(1000 + $0), copyrightID: String(600000 + $0),
                title: "Synthetic \($0)")
        }
        let page = MusicNativeMiguSearchFixture.page([Array(rows.prefix(20)), Array(rows.suffix(20))], total: 100)
        let http = NativeMusicHTTPFixture([page, page]), clock = MusicSearchBatchClock()
        let document = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(document, http: http, clock: { clock.read() })
        let first = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(first.items.count, 40); XCTAssertNil(first.nextPage)
        let target = MusicSearchBatchTarget(platform: .migu, page: 1, track: try XCTUnwrap(first.items.last))
        clock.advance(601)
        let renewed = try await service.searchRecording(.init(query: "Synthetic", targets: [target]))
        XCTAssertEqual(renewed.sources.map(\.nativeId), target.track.sources.map(\.nativeId))
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 2)
        for call in calls {
            let query = URLComponents(url: call.url, resolvingAgainstBaseURL: false)?.queryItems
            XCTAssertEqual(query?.first { $0.name == "pageNo" }?.value, "1")
            XCTAssertEqual(query?.first { $0.name == "pageSize" }?.value, "30")
        }
        XCTAssertEqual(document.snapshot().2, 0)
        await service.close()
    }
    func testSameNativeIDWithConflictingRecordingEvidenceIsRejected() async throws {
        let (service, document, _, _) = try await setup(1)
        let captured = try await capture(service, count: 1)
        let original = try XCTUnwrap(captured.first?.track)
        let stale = MusicRemoteTrack(ref: original.ref, songId: original.songId, title: original.title,
            artist: original.artist, album: original.album, duration: 270, sources: original.sources)
        do {
            _ = try await service.replaceSearchBatch(.init(query: "original query",
                targets: [.init(platform: .qq, page: 1, track: stale)]), expectedRevision: 0, intent: .init())
            XCTFail("Reusing a native ID cannot authorize a different recording")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(document.snapshot().2, 0)
        await service.close()
    }
    func testThreePlatformBatchPreservesSelectedOrderWithoutFakingMergedSources() async throws {
        let document = NativeMusicMemoryDocument(), qq = MusicSearchBatchQQ(count: 1)
        let http = MusicNativeCandidateDiscoveryHTTP()
        let service = NativeMusicService(document: document, qq: qq, enabledPlatforms: [.netease, .qq, .kugou],
            transport: http, clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        var targets: [MusicSearchBatchTarget] = []
        for platform in [MusicPublicBrowsePlatform.kugou, .qq, .netease] {
            let page = try await service.search(platform: platform.rawValue, query: "original query", page: 1, limit: 30)
            targets.append(.init(platform: platform, page: 1, track: try XCTUnwrap(page.items.first)))
        }
        _ = try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
            expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        XCTAssertEqual(queue.entries.map(\.track.sources), targets.map(\.track.sources))
        XCTAssertEqual(document.snapshot().2, 1)
        let saved = try MusicDocument(document.snapshot().0, runID: "fixture")
        XCTAssertTrue(saved.queue["entries"].array.allSatisfy {
            $0["context"]["searchKeyword"].string == "original query"
        })
        let single = try XCTUnwrap(targets.first { $0.platform == .qq })
        _ = try await service.replaceSearchBatch(.init(query: "original query", targets: [single]),
            expectedRevision: queue.revision, intent: .init())
        let singleSaved = try MusicDocument(document.snapshot().0, runID: "fixture")
        XCTAssertEqual(singleSaved.queue["entries"].array.count, 1)
        XCTAssertEqual(singleSaved.queue["entries"].array.first?["context"]["sourceType"].string, "search-song")
        XCTAssertEqual(singleSaved.queue["entries"].array.first?["context"]["searchKeyword"].string, "original query")
        await service.close()
    }
    func testFusedRecordingPersistsAllFiveRealPlatformIDsForQueueAndFavorites() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let order: [MusicPublicBrowsePlatform] = [.migu, .qq, .netease, .kuwo, .kugou]
        let expectedPlatforms = ["mg", "kg", "kw", "tx", "wy"]
        let expectedNativeIDs = ["600123", String(repeating: "A", count: 32), "789012", "QQMID01", "123456"]
        var origins: [MusicSearchBatchOrigin] = []
        for platform in order {
            let id: String
            switch platform {
            case .migu: id = "600123"
            case .qq: id = "QQMID01"
            case .netease: id = "123456"
            case .kuwo: id = "789012"
            case .kugou: id = String(repeating: "A", count: 32)
            }
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(platform.rawValue, id: id))
            let handle = try await service.remember(song)
            origins.append(.init(platform: platform, page: 1, track: handle))
        }
        let request = MusicSearchBatchRequest(query: "original query", targets: [try .init(origins: origins)])
        let recording = try await service.searchRecording(request)
        XCTAssertEqual(recording.sources.map(\.platform), expectedPlatforms)
        XCTAssertEqual(recording.sources.map(\.nativeId), expectedNativeIDs)
        let favorites = try await service.collections().first { $0.kind == "favorites" }
        let favorite = try XCTUnwrap(favorites)
        _ = try await service.editPlaylist(favorite, name: nil, refs: [recording.ref], order: nil)
        let savedFavorite = try await service.collections().first { $0.kind == "favorites" }
        let favoritesTracks = try await service.tracks(in: XCTUnwrap(savedFavorite))
        XCTAssertEqual(favoritesTracks.first?.sources, recording.sources)
        _ = try await service.replaceSearchBatch(request, expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.count, 1)
        XCTAssertEqual(queue.entries.first?.track.sources, recording.sources)
        let stored = try MusicDocument(document.snapshot().0, runID: "fixture").queue["entries"].array[0]["song"]
        XCTAssertEqual(stored.fields.map(\.0).sorted(), ["album", "artist", "cover_url", "duration", "songId", "sources", "title"])
        for source in stored["sources"].array {
            XCTAssertEqual(Set(source.fields.map(\.0)), ["source", "sourceKey", "musicInfo"])
            XCTAssertFalse(source["musicInfo"]["songmid"].id.isEmpty)
            XCTAssertEqual(source["musicInfo"]["name"].string, "Synthetic recording")
        }
        XCTAssertEqual(stored["sources"].array.map { $0["source"].string }, expectedPlatforms)
        XCTAssertEqual(stored["sources"].array.map { $0["musicInfo"]["songmid"].string }, expectedNativeIDs)
        let feedback = try MusicNativeFeedbackMapping.targets(song: stored, context: .obj())
        XCTAssertEqual(Set(feedback.map(\.platform)), Set(MusicAccountPlatform.allCases))
        XCTAssertEqual(try XCTUnwrap(feedback.first { $0.platform == .netease }).nativeID, "123456")
        XCTAssertEqual(try XCTUnwrap(feedback.first { $0.platform == .qq }).nativeID, "QQMID01")
        let kugou = try XCTUnwrap(feedback.first { $0.platform == .kugou })
        XCTAssertEqual(kugou.nativeID, String(repeating: "A", count: 32))
        XCTAssertNil(kugou.mixsongID)
        await service.close()
    }
    func testSamePlatformSourcesRemainDistinctInFavoritesAndBatchQueue() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        var origins: [MusicSearchBatchOrigin] = []
        for id in ["101", "102"] {
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: id))
            let track = try await service.remember(song)
            origins.append(.init(platform: .netease, page: 1, track: track))
        }
        let request = MusicSearchBatchRequest(query: "original query", targets: [try .init(origins: origins)])
        let recording = try await service.searchRecording(request)
        XCTAssertEqual(recording.sources.map(\.nativeId), ["101", "102"])
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorites, name: nil, refs: [recording.ref], order: nil)
        let tracks = try await service.tracks(in: saved)
        XCTAssertEqual(tracks.first?.sources, recording.sources)
        _ = try await service.replaceSearchBatch(request, expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.count, 1)
        XCTAssertEqual(queue.entries.first?.track.sources, recording.sources)
        await service.close()
    }
    func testRevokedIntentDuringRefreshPreventsTheCAS() async throws {
        let (service, document, qq, clock) = try await setup(1)
        let targets = try await capture(service, count: 1)
        clock.advance(601)
        let gate = NativeMusicGate(), intent = MusicQueueAuthorization()
        await qq.suspend(gate)
        let task = Task {
            try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
                expectedRevision: 0, intent: intent)
        }
        await gate.started(); intent.revoke(); await gate.release()
        do { _ = try await task.value; XCTFail("Revoked batch was committed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(document.snapshot().2, 0)
        await service.close()
    }
    func testQueueRevisionChangeDuringRefreshCannotBeOverwritten() async throws {
        let (service, document, qq, clock) = try await setup(1)
        let targets = try await capture(service, count: 1)
        clock.advance(601)
        let gate = NativeMusicGate()
        await qq.suspend(gate)
        let task = Task {
            try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
                expectedRevision: 0, intent: .init())
        }
        await gate.started()
        _ = try await service.changeQueue(.clear, expectedRevision: 0)
        let changed = document.snapshot()
        await gate.release()
        do { _ = try await task.value; XCTFail("Concurrent queue edit was overwritten") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        XCTAssertEqual(document.snapshot().0, changed.0)
        XCTAssertEqual(document.snapshot().2, 1)
        await service.close()
    }
    func testNativeAccessRevocationDuringRefreshCannotCommit() async throws {
        let (service, document, qq, clock) = try await setup(1)
        let targets = try await capture(service, count: 1)
        clock.advance(601)
        let gate = NativeMusicGate()
        await qq.suspend(gate)
        let task = Task {
            try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
                expectedRevision: 0, intent: .init())
        }
        await gate.started()
        try await service.setAccess(available: false, generation: 2)
        await gate.release()
        do { _ = try await task.value; XCTFail("Revoked native access committed the queue") }
        catch {
            let failure = error as? MusicServiceError
            XCTAssertTrue(failure == .cancelled || failure == .staleAccess || failure == .accessUnavailable)
        }
        XCTAssertEqual(document.snapshot().2, 0)
        await service.close()
    }

    func testFreshDisplayedRecordingDoesNotRequireAnotherSearchToPlay() async throws {
        let (service, document, qq, _) = try await setup(1)
        let targets = try await capture(service, count: 1)
        await qq.fail(1)
        _ = try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
            expectedRevision: 0, intent: .init())
        let queue = try await service.queue(), calls = await qq.log()
        XCTAssertEqual(calls.count, 1, "Tap must not re-search a fresh owner-held recording")
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        XCTAssertEqual(document.snapshot().2, 1)
        await service.close()
    }

    func testNonPreferredKnownSourceKeepsExistingLibraryIdentityDuringMetadataEnrichment() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let incomplete = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(
            "tx", id: "known-id", artist: "未知歌手", duration: 0))
        let old = try await service.remember(incomplete)
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await service.editPlaylist(favorites, name: nil, refs: [old.ref], order: nil)
        let known = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "known-id"))
        let preferred = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "new-id"))
        let combined = try MusicModel.mergeSources(preferred, adding: known["sources"].array)
        let incoming = try await service.remember(combined)
        let updated = try await service.editPlaylist(saved, name: nil, refs: [incoming.ref], order: nil)
        let tracks = try await service.tracks(in: updated)
        XCTAssertEqual(tracks.count, 1)
        XCTAssertEqual(tracks.first?.songId, old.songId)
        XCTAssertEqual(tracks.first?.artist, "Fixture")
        XCTAssertEqual(tracks.first?.sources.map(\.platform), ["wy", "tx"])
        let library = try MusicDocument(document.snapshot().0, runID: "fixture").library["library"]
        XCTAssertEqual(library["songs"].fields.count, 1)
        await service.close()
    }

    func testMixedLiveAndExpiredResultsRenewOnlyTheMissingExactPage() async throws {
        let (service, document, qq, clock) = try await setup(31)
        let first = try await service.search(platform: "tx", query: "original query", page: 1, limit: 30)
        clock.advance(601)
        let second = try await service.search(platform: "tx", query: "original query", page: 2, limit: 30)
        let targets = first.items.map { MusicSearchBatchTarget(platform: .qq, page: 1, track: $0) } +
            second.items.map { MusicSearchBatchTarget(platform: .qq, page: 2, track: $0) }
        await qq.fail(2)
        _ = try await service.replaceSearchBatch(.init(query: "original query", targets: targets),
            expectedRevision: 0, intent: .init())
        let calls = await qq.log(), queue = try await service.queue()
        XCTAssertEqual(calls.map(\.page), [1, 2, 1])
        XCTAssertEqual(queue.entries.map(\.track.songId), targets.map(\.track.songId))
        XCTAssertEqual(document.snapshot().2, 1)
        await service.close()
    }
}

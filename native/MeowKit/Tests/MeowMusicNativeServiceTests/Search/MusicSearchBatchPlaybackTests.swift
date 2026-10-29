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
    struct Call: Equatable, Sendable { let query: String; let page: Int }
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
    func search(query: String, page: Int) async throws -> MusicNativeTrackPage {
        calls.append(.init(query: query, page: page))
        if let gate { self.gate = nil; await gate.wait() }
        if page == failedPage { throw MusicServiceError.upstreamFailed }
        let lower = (page - 1) * 30, upper = min(page * 30, count)
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
            let result = try await service.search(platform: "tx", query: "original query", page: page)
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
    func testOverTwoThousandFailsBeforeReadingOrTruncating() async throws {
        let (service, document, qq, _) = try await setup(1)
        let song = try MusicModel.normalize(NativeMusicFixture.track("Track0"))
        let track = try MusicModel.track(song, .init(kind: .handle, id: "not-a-registered-handle"))
        let target = MusicSearchBatchTarget(platform: .qq, page: 1, track: track)
        do {
            _ = try await service.replaceSearchBatch(.init(query: "original query",
                targets: Array(repeating: target, count: 2001)), expectedRevision: 0, intent: .init())
            XCTFail("The complete selection cannot silently become a prefix")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let calls = await qq.log()
        XCTAssertTrue(calls.isEmpty)
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
            let page = try await service.search(platform: platform.rawValue, query: "original query", page: 1)
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

    func testMixedLiveAndExpiredResultsRenewOnlyTheMissingExactPage() async throws {
        let (service, document, qq, clock) = try await setup(31)
        let first = try await service.search(platform: "tx", query: "original query", page: 1)
        clock.advance(601)
        let second = try await service.search(platform: "tx", query: "original query", page: 2)
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

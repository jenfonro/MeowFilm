import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private actor QQBrowsePeer: MusicPlatformFixtureResponding {
    let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "42", title: "Synthetic remote list")
    let count: Int, pageSize: Int
    private var changed = false
    private var failure: MusicServiceError?
    private var sent: [MusicPlatformFixture.Request] = []
    init(count: Int, pageSize: Int = 30) { self.count = count; self.pageSize = pageSize }
    func changeFirst() { changed = true }
    func failPage(_ value: MusicServiceError?) { failure = value }
    func calls() -> [MusicPlatformFixture.Request] { sent }
    func respond(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        sent.append(request)
        if let failure { throw failure }
        let complete = request.operation == "playlist.complete"
        let offset = complete ? 0 : Int(request.parameters[music: "continuation"][music: "offset"].musicNumber ?? 0)
        let end = complete ? count : min(offset + pageSize, count)
        let ids = (offset..<end).map { changed && $0 == 0 ? "ChangedMID" : "MID\($0 % 2)" }
        return try MusicNativePublicBrowseFixture.page(ids, platform: "tx", collectionID: "42", total: count,
            positions: (offset..<end).map { Optional($0) }, continuation: end < count ? .object([
                "page": .number(Double(offset / pageSize + 2)), "offset": .number(Double(end))]) : .null)
    }
}
final class MusicNativePublicBrowseQueueTests: XCTestCase {
    func testRepeatedReaderCloseWaitsForTheSameNonCooperativeLoad() async throws {
        let gate = NativeMusicGate()
        let reader = MusicNativePublicBrowseQueueRead { _ in
            await gate.wait()
            return [.init(songID: "song-1", occurrenceID: "occurrence-1", platform: "wy",
                          sourceKey: "wy/1", byteCount: NativeMusicFixture.track().data.count)]
        }
        let reading = Task { try await reader.page(nil, intent: .init()) }
        await gate.started()
        let firstReturned = expectation(description: "first close drains")
        let secondReturned = expectation(description: "second close drains")
        firstReturned.isInverted = true; secondReturned.isInverted = true
        let first = Task { await reader.close(); firstReturned.fulfill() }
        let second = Task { await reader.close(); secondReturned.fulfill() }
        await fulfillment(of: [firstReturned, secondReturned], timeout: 0.05)
        await gate.release()
        await first.value; await second.value
        do { _ = try await reading.value; XCTFail("closed reader published late rows") }
        catch { XCTAssertTrue(error is CancellationError || error as? MusicServiceError == .closed) }
    }
    @MainActor func testSavedReferenceOpensCurrentMembersAndReadFailureNeverChangesItsIdentity() async throws {
        let peer = QQBrowsePeer(count: 1), store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let saved = try await service.saveNetworkPlaylist(peer.collection, intent: .init())
        let before = await peer.calls(); XCTAssertTrue(before.isEmpty)
        let remote = try XCTUnwrap(saved.remoteCollection)
        let page = MusicPublicBrowseViewModel(service: service, request: .detail(remote))
        page.setAccess(available: true, generation: UUID()); page.appear(); await page.loadIfNeeded()
        XCTAssertEqual(page.tracks.first?.sources.first?.nativeId, "MID0")
        page.disappear(); await peer.changeFirst(); page.appear(); await page.loadIfNeeded()
        XCTAssertEqual(page.tracks.first?.sources.first?.nativeId, "ChangedMID")
        page.disappear(); await peer.failPage(.upstreamFailed)
        let checkpoint = store.snapshot()
        page.appear(); await page.loadIfNeeded()
        XCTAssertEqual(page.error, .upstreamFailed); XCTAssertEqual(page.request, .detail(remote))
        let collections = try await service.collections(); XCTAssertEqual(collections.first { $0.id == saved.id }, saved)
        XCTAssertEqual(store.snapshot().0, checkpoint.0); XCTAssertEqual(store.snapshot().2, checkpoint.2)
        let calls = await peer.calls(); XCTAssertFalse(calls.contains { $0.operation == "playlist.complete" })
        page.disappear(); await service.close()
    }
    @MainActor func testQQSelectionUsesVisibleOccurrenceWithoutRenewingContentProof() async throws {
        let peer = QQBrowsePeer(count: 35), service = try await NativeMusicFixture.service(platform: peer)
        let page = MusicPublicBrowseViewModel(service: service, request: .detail(peer.collection),
            queuePage: { try await service.remoteQueuePage($0, cursor: $1) })
        page.setAccess(available: true, generation: UUID()); page.appear(); await page.refresh()
        let selected = try XCTUnwrap(page.tracks.first)
        await peer.changeFirst()
        let (source, index) = try await page.playbackSource(for: selected)
        XCTAssertEqual(index, 0)
        _ = try await service.replaceRemoteQueue(source, start: .init(index: index), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries.first?.track.sources.first?.nativeId, "ChangedMID")
        page.disappear(); await source.close(); await service.close()
    }
    func testQQDisplayReadsOnlyCurrentPagesAndExplicitActionLoadsOneCompleteResult() async throws {
        let peer = QQBrowsePeer(count: 35, pageSize: 27), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(peer.collection, cursor: nil)
        XCTAssertEqual(first.tracks.count, 27); XCTAssertEqual(first.consumed, 27)
        let second = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.tracks.count, 8); XCTAssertNil(second.cursor)
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        let rows = try await source.collectionTracks(intent: .init()); XCTAssertEqual(rows.count, 35)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist", "playlist.complete"])
        XCTAssertEqual(calls[1].parameters[music: "continuation"][music: "offset"], .number(27))
        await source.close(); await service.close()
    }
    func testQQWholeActionAcceptsCurrentChangedMemberAndDoesNotReplaceQueueUntilCommit() async throws {
        let peer = QQBrowsePeer(count: 35), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(peer.collection, cursor: nil)
        await peer.changeFirst()
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        let rows = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(try MusicModel.sources(MusicModel.normalize(rows[0])).first?.nativeId, "ChangedMID")
        let queue = try await service.queue(); XCTAssertTrue(queue.entries.isEmpty); XCTAssertEqual(queue.revision, 0)
        await source.close(); await service.close()
    }
    func testExplicitQueueActionKeeps650RepeatedOccurrencesWithOneAtomicQueueCommit() async throws {
        let peer = QQBrowsePeer(count: 650), store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let source = try await service.openPublicQueue(.init(collection: peer.collection))
        let before = store.snapshot().2, preview = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(preview.items.count, 30)
        let state = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(state.total, 650); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 650)
        XCTAssertEqual(queue.entries[0].track.songId, queue.entries[2].track.songId); XCTAssertEqual(store.snapshot().2, before + 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist.complete"])
        await source.close(); await service.close()
    }
    func testProtectionRevokesPublicSourceSynchronouslyWithoutQueueCommit() async throws {
        let peer = QQBrowsePeer(count: 3), store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let source = try await service.openPublicQueue(.init(collection: peer.collection))
        let before = store.snapshot().0; service.revokeAccess(generation: 2)
        XCTAssertFalse(source.authorization.isActive)
        await assertMusicFailure(.staleAccess) { _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init()) }
        XCTAssertEqual(store.snapshot().0, before)
        await source.close(); await service.close()
    }
    func testCompleteResultFailurePublishesNoSourceAndDoesNotWriteQueue() async throws {
        let peer = NativeMusicResultFixture(failure: .upstreamFailed), store = NativeMusicTestLibrary()
        let service = try await NativeMusicFixture.service(store, platform: peer)
        await assertMusicFailure(.upstreamFailed) { _ = try await service.openPublicQueue(.init(collection: MusicNativePublicBrowseFixture.collection)) }
        XCTAssertEqual(store.snapshot().2, 0)
        let retained = await service.publicQueueSources.count; XCTAssertEqual(retained, 0)
        await service.close()
    }
    func testPublicSourcesReleaseIndependentlyAndRefreshRevokesOnlyCollectionLifetime() async throws {
        let peer = QQBrowsePeer(count: 1), clock = NativeMusicFixtureClock(), service = try await NativeMusicFixture.service(platform: peer, clock: { clock.now() })
        var sources: [MusicRemoteQueueSource] = []
        for _ in 0..<33 { sources.append(try await service.openPublicQueue(.init(collection: peer.collection))) }
        await sources[0].close()
        XCTAssertFalse(sources[0].authorization.isActive); XCTAssertTrue(sources.dropFirst().allSatisfy { $0.authorization.isActive })
        clock.advance(601)
        let fresh = try await service.openPublicQueue(.init(collection: peer.collection))
        XCTAssertTrue(sources.dropFirst().allSatisfy { $0.authorization.isActive }, "ordinary time passage is not a content gate")
        await service.refreshPublicCollection(peer.collection)
        XCTAssertFalse(fresh.authorization.isActive); XCTAssertTrue(sources.allSatisfy { !$0.authorization.isActive })
        let count = await service.publicQueueSources.count; XCTAssertEqual(count, 0)
        for source in sources { await source.close() }; await fresh.close(); await service.close()
    }
    func testCancelledNonCooperativeSearchCannotPublishAndPhysicalPeerEndsBeforeTeardown() async throws {
        let gate = NativeMusicGate(), peer = NativeMusicResultFixture([.object(["items": .array([]), "total": .number(0)])], gate: gate)
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let task = Task { try await service.publicPlaylists(platform: .netease, query: "synthetic", cursor: nil) }
        await gate.started(); task.cancel()
        do { _ = try await task.value; XCTFail("late search") } catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await gate.release(); await peer.waitUntilIdle(); await service.close()
        XCTAssertEqual(store.snapshot().2, 0)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
    }
}

import XCTest
import MeowMusicFeature

private actor HomeLifecycleGate {
    private var entered = false, released = false
    private var startWaiters: [CheckedContinuation<Void, Never>] = []
    private var releaseWaiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; startWaiters.forEach { $0.resume() }; startWaiters.removeAll()
        if !released { await withCheckedContinuation { releaseWaiters.append($0) } }
    }
    func didStart() async {
        if !entered { await withCheckedContinuation { startWaiters.append($0) } }
    }
    func release() {
        released = true; releaseWaiters.forEach { $0.resume() }; releaseWaiters.removeAll()
    }
}

private actor HomeLifecycleFixture: MusicHomeBrowsing {
    var failure: MusicServiceError?
    var restored: MusicPublicBrowsePage?
    var calls: [(String?, Bool)] = []
    var subscription: AsyncStream<Void>.Continuation?
    var subscribed = false
    var waiters: [CheckedContinuation<Void, Never>] = []
    var source: MusicPublicBrowsePlatform = .netease
    var emptyFirstPage = false
    var gate: HomeLifecycleGate?
    var snapshotReads = 0
    func hold(_ gate: HomeLifecycleGate) { self.gate = gate }
    func homeSourceSelection() -> MusicHomeSourceSelection { .init(selected: source, available: [.netease, .qq]) }
    func selectHomeSource(_ value: MusicPublicBrowsePlatform) throws {
        guard homeSourceSelection().available.contains(value) else { throw MusicServiceError.unsupported }
        guard source != value else { return }
        source = value; subscription?.yield(())
    }
    func startWithFilteredPage() { emptyFirstPage = true }
    static func page(_ ids: [String], cursor: String? = nil, source: MusicPublicBrowsePlatform = .netease) -> MusicPublicBrowsePage {
        .init(sections: [.init(id: "home:stable", title: "音乐", items: ids.map {
            .init(id: $0, title: $0, content: .collection(.init(platform: source, nativeID: $0, title: $0)))
        }, layout: .playlistRail)], cursor: cursor)
    }
    func fail(_ value: MusicServiceError?) { failure = value }
    func setSnapshot(_ value: MusicPublicBrowsePage) { restored = value }
    func restoredHome() -> MusicPublicBrowsePage? { snapshotReads += 1; return restored }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        calls.append((cursor, refresh))
        if let gate { await gate.wait() }
        if let failure { throw failure }
        if cursor == nil, emptyFirstPage { return .init(sections: [], cursor: "next", consumed: 1) }
        return cursor == nil ? Self.page(["1"], cursor: "next", source: source) : Self.page(["1", "2"], source: source)
    }
    func homeChanges() -> AsyncStream<Void> {
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        subscription = pair.continuation; subscribed = true
        waiters.forEach { $0.resume() }; waiters.removeAll()
        return pair.stream
    }
    func waitForSubscription() async {
        if !subscribed { await withCheckedContinuation { waiters.append($0) } }
    }
    func change() { subscription?.yield(()) }
    func end() { subscription?.finish() }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        try await homePage(cursor: cursor, refresh: false)
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) throws -> MusicPublicBrowseDetail {
        throw MusicServiceError.unsupported
    }
}

@MainActor final class MusicHomeLifecycleTests: XCTestCase {
    private func model(_ service: HomeLifecycleFixture) -> MusicPublicBrowseViewModel {
        let value = MusicPublicBrowseViewModel(service: service, request: .discovery(.home))
        value.setAccess(available: true, generation: UUID()); value.appear()
        return value
    }
    func testRefreshFailurePreservesLastSuccessfulHomeButAuthRejectionClearsIt() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        await service.fail(.unavailable); await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertTrue(value.loaded); XCTAssertEqual(value.error, .unavailable)
        await service.fail(.authRequired); await value.retry()
        XCTAssertTrue(value.sections.isEmpty); XCTAssertFalse(value.loaded)
        XCTAssertNil(value.nextCursor); XCTAssertEqual(value.error, .authRequired)
    }
    func testHomeContinuationMergesStableSectionsAndRefreshResetsPagination() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh(); await value.appendNext()
        XCTAssertEqual(value.sections.count, 1)
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertEqual(value.pageNumber, 2)
        await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertEqual(value.pageNumber, 1); XCTAssertFalse(value.canPrevious)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next", nil])
        XCTAssertEqual(calls.map { $0.1 }, [true, false, true])
    }
    func testSnapshotIsShownWithoutBeingTreatedAsFreshContinuation() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await service.setSnapshot(HomeLifecycleFixture.page(["7"]))
        await value.restoreHomeIfNeeded()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["7"])
        XCTAssertNil(value.nextCursor)
        let calls = await service.calls; XCTAssertTrue(calls.isEmpty)
        await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
    }
    func testAccountChangeDoesNotReloadRemovedHome() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        XCTAssertFalse(value.sections.isEmpty)
        value.disappear()
        XCTAssertTrue(value.sections.isEmpty)
        await service.change(); await service.end(); await observation.value
        XCTAssertTrue(value.sections.isEmpty); XCTAssertFalse(value.loaded)
        let calls = await service.calls; XCTAssertEqual(calls.count, 1)
    }
    func testSourceChangeInvalidatesHomeAndCurrentSourceSelectionDoesNotReload() async throws {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        try await service.selectHomeSource(.netease)
        try await service.selectHomeSource(.qq)
        await service.end(); await observation.value
        guard let item = value.sections.first?.items.first, case .collection(let collection) = item.content else {
            return XCTFail("Selected source must replace the home page")
        }
        XCTAssertEqual(collection.platform, .qq)
        XCTAssertTrue(value.contentIsCurrent)
        XCTAssertEqual(value.pageNumber, 1)
        let calls = await service.calls
        XCTAssertEqual(calls.count, 2)
        XCTAssertTrue(calls.allSatisfy { $0.0 == nil })
    }
    func testFilteredHomePageKeepsContinuationWithoutInventingVisibleRows() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await service.startWithFilteredPage()
        await value.refresh()
        XCTAssertTrue(value.sections.isEmpty); XCTAssertTrue(value.loaded)
        XCTAssertNil(value.error); XCTAssertEqual(value.nextCursor, "next")
        XCTAssertFalse(value.requiresExplicitNext)
        await value.appendNext()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertNil(value.error); XCTAssertNil(value.nextCursor)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
    }
    func testHiddenHomeAndCatalogueRevokeChangedScopeWithoutStartingReplacementReads() async throws {
        for destination in [MusicPublicBrowseDestination.home, .channels] {
            let service = HomeLifecycleFixture()
            let value = MusicPublicBrowseViewModel(service: service, request: .discovery(destination))
            value.setAccess(available: true, generation: UUID()); value.appear()
            await value.loadIfNeeded()
            let observation = Task { await value.observeHomeChanges() }
            await service.waitForSubscription()
            value.setDisplayVisible(false)
            try await service.selectHomeSource(.qq)
            await service.end(); await observation.value
            XCTAssertTrue(value.sections.isEmpty, "Hidden content from the revoked scope must clear immediately")
            XCTAssertFalse(value.loaded); XCTAssertFalse(value.contentIsCurrent)
            let hiddenCalls = await service.calls
            XCTAssertEqual(hiddenCalls.count, 1, "A retained owner is not new display demand")
            await value.restoreHomeIfNeeded()
            let snapshotReads = await service.snapshotReads
            XCTAssertEqual(snapshotReads, 0)
            value.setDisplayVisible(true)
            await value.loadIfNeeded(); await value.loadIfNeeded()
            let returnedCalls = await service.calls
            XCTAssertEqual(returnedCalls.count, 2)
            guard case .collection(let collection) = value.sections.first?.items.first?.content else {
                return XCTFail("Returning must display the current scope")
            }
            XCTAssertEqual(collection.platform, .qq)
            value.disappear()
        }
    }
    func testOrdinaryHidingDoesNotCancelOrDiscardAnAlreadyAdmittedHomeRead() async {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await service.hold(gate)
        let read = Task { await value.loadIfNeeded() }
        await gate.didStart()
        value.setDisplayVisible(false)
        await gate.release(); await read.value
        XCTAssertTrue(value.loaded); XCTAssertTrue(value.contentIsCurrent)
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        value.setDisplayVisible(true)
        await value.loadIfNeeded()
        let calls = await service.calls
        XCTAssertEqual(calls.count, 1)
    }
}

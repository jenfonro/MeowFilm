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
    var sourceRevision = 0
    var emptyFirstPage = false
    var gate: HomeLifecycleGate?
    var gateCursor: String?
    var failureCursor: String?
    var pages: [String: MusicPublicBrowsePage]?
    var snapshotReads = 0
    func hold(_ gate: HomeLifecycleGate, cursor: String? = nil) { self.gate = gate; gateCursor = cursor }
    func setPages(_ pages: [String: MusicPublicBrowsePage]) { self.pages = pages }
    func homeSourceSelection() -> MusicHomeSourceSelection {
        .init(selected: source, revision: sourceRevision, available: [.netease, .qq])
    }
    func selectHomeSource(_ value: MusicPublicBrowsePlatform, ifRevision: Int) throws -> MusicHomeSourceSelection {
        guard homeSourceSelection().available.contains(value) else { throw MusicServiceError.unsupported }
        guard ifRevision == sourceRevision else { throw MusicServiceError.staleRevision }
        guard source != value else { return homeSourceSelection() }
        source = value; sourceRevision += 1; subscription?.yield(())
        return homeSourceSelection()
    }
    func startWithFilteredPage() { emptyFirstPage = true }
    static func page(_ ids: [String], cursor: String? = nil, source: MusicPublicBrowsePlatform = .netease,
                     consumed: Int? = nil) -> MusicPublicBrowsePage {
        .init(sections: [.init(id: "home:stable", title: "音乐", items: ids.map {
            .init(id: $0, title: $0, content: .collection(.init(platform: source, nativeID: $0, title: $0)))
        }, layout: .playlistRail)], cursor: cursor, consumed: consumed)
    }
    func fail(_ value: MusicServiceError?, cursor: String? = nil) { failure = value; failureCursor = cursor }
    func setSnapshot(_ value: MusicPublicBrowsePage) { restored = value }
    func restoredHome() -> MusicPublicBrowsePage? { snapshotReads += 1; return restored }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        calls.append((cursor, refresh))
        if let gate, cursor == gateCursor { self.gate = nil; await gate.wait() }
        if let failure, failureCursor == nil || cursor == failureCursor { throw failure }
        if let pages {
            guard let page = pages[cursor ?? ""] else { throw MusicServiceError.invalidRequest }
            return page
        }
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
    func testRefreshFailurePreservesLastSuccessfulHomeIncludingAuthRejection() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        await service.fail(.unavailable); await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertTrue(value.loaded); XCTAssertEqual(value.error, .unavailable)
        await service.fail(.authRequired); await value.retry()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertTrue(value.loaded); XCTAssertTrue(value.contentIsCurrent)
        XCTAssertNil(value.nextCursor); XCTAssertEqual(value.error, .authRequired)
        let retried = await service.calls
        XCTAssertTrue(retried.last?.1 == true, "Explicit first-page retry must not reuse the old response cache")
    }
    func testHomeContinuationMergesStableSectionsAndRefreshResetsPagination() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        XCTAssertEqual(value.sections.count, 1)
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertEqual(value.pageNumber, 2)
        await service.setPages(["": HomeLifecycleFixture.page(["3"])])
        await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["3"])
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
        await value.loadIfNeeded()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        let refreshCalls = await service.calls
        XCTAssertEqual(refreshCalls.count, 2); XCTAssertTrue(refreshCalls[0].1)
        XCTAssertEqual(refreshCalls.map { $0.0 }, [nil, "next"])
        await value.loadIfNeeded()
        let repeatedCalls = await service.calls
        XCTAssertEqual(repeatedCalls.count, 2)
    }
    func testSlowStartupRefreshKeepsSnapshotActionableAndReplacesOnlyOnSuccess() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await service.setSnapshot(HomeLifecycleFixture.page(["7"]))
        await value.restoreHomeIfNeeded()
        await service.hold(gate)
        let refresh = Task { await value.loadIfNeeded() }
        await gate.didStart()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["7"])
        XCTAssertTrue(value.isLoading)
        let intent = try XCTUnwrap(value.makeActionIntent())
        var opened: MusicPublicBrowseCollection?
        guard case .collection(let collection) = value.sections[0].items[0].content else {
            return XCTFail("Cached public collection must keep its own identity")
        }
        await value.perform(intent: intent) { opened = collection }
        XCTAssertEqual(opened?.platform, .netease); XCTAssertEqual(opened?.nativeID, "7")
        await gate.release(); await refresh.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertNil(value.actionError)
    }
    func testRefreshAndAppendDoNotRetireCapturedHomeActions() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        let continuation = HomeLifecycleGate()
        await service.hold(continuation, cursor: "next")
        let read = Task { await value.loadIfNeeded() }
        await continuation.didStart()
        let captured = try XCTUnwrap(value.makeActionIntent())
        await continuation.release(); await read.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        var completed = false
        let action = Task {
            await value.perform(intent: captured) {
                await gate.wait()
                try Task.checkCancellation()
                completed = true
            }
        }
        await gate.didStart()
        await value.refresh()
        XCTAssertTrue(value.isActing)
        await gate.release(); await action.value
        XCTAssertTrue(completed); XCTAssertNil(value.actionError)
        await value.perform(intent: captured) { XCTFail("Action intents remain single-use") }
    }
    func testAccountChangeKeepsDisplayAndCapturedActionWhileGuestHomeIsPending() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await value.loadIfNeeded()
        let captured = try XCTUnwrap(value.makeActionIntent())
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        await service.hold(gate); await service.change()
        await gate.didStart()
        XCTAssertTrue(value.loaded); XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        var acted = false
        await value.perform(intent: captured) { acted = true }
        XCTAssertTrue(acted)
        await gate.release(); await service.end(); await observation.value
        XCTAssertTrue(value.loaded); XCTAssertNil(value.error)
        let calls = await service.calls
        XCTAssertEqual(calls.count, 4)
    }
    func testPrivateActionFailureDoesNotClearHomeOrDiscardAuthenticationError() async throws {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.loadIfNeeded()
        let intent = try XCTUnwrap(value.makeActionIntent())
        await value.perform(intent: intent) { throw MusicServiceError.authRequired }
        XCTAssertEqual(value.actionError, .authRequired)
        XCTAssertTrue(value.loaded); XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
    }
    func testLateHomeTabDisplayCannotOverwriteNewFirstPage() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        let old = HomeLifecycleFixture.page(["7"]).sections[0].items
        let late = HomeLifecycleFixture.page(["8"]).sections[0].items
        await service.setSnapshot(.init(sections: [.init(id: "home:stable", title: "音乐", items: old,
            layout: .playlistRail, tabs: [
                .init(id: "first", title: "原栏目", items: old),
                .init(id: "second", title: "其他栏目", items: [], loadItems: { await gate.wait(); return late }),
            ], selectedTab: "first")]))
        await value.restoreHomeIfNeeded()
        let intent = try XCTUnwrap(value.makeActionIntent())
        let tab = Task { await value.selectSectionTab("second", sectionID: "home:stable", intent: intent) }
        await gate.didStart()
        await value.refresh()
        await gate.release(); await tab.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertTrue(value.sections.first?.tabs.isEmpty == true); XCTAssertNil(value.error)
    }
    func testSourceChangeRetiresCapturedActionWhileReplacementIsPending() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await value.loadIfNeeded()
        let intent = try XCTUnwrap(value.makeActionIntent())
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        await service.hold(gate)
        _ = try await service.selectHomeSource(.qq, ifRevision: 0)
        await gate.didStart()
        XCTAssertTrue(value.sections.isEmpty); XCTAssertFalse(value.loaded)
        await value.perform(intent: intent) { XCTFail("A source change must retire the old action") }
        await gate.release(); await service.end(); await observation.value
        guard case .collection(let collection) = value.sections[0].items[0].content else {
            return XCTFail("The new source must publish its own content")
        }
        XCTAssertEqual(collection.platform, .qq)
    }
    func testLateCancelledHomeReadCannotOverwriteReplacementOrItsError() async {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await value.loadIfNeeded()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        await service.hold(gate)
        let old = Task { await value.refresh() }
        await gate.didStart()
        await service.fail(.unavailable)
        await service.change(); await service.end(); await observation.value
        XCTAssertEqual(value.error, .unavailable); XCTAssertTrue(value.loaded)
        await service.fail(nil)
        await gate.release(); await old.value
        XCTAssertEqual(value.error, .unavailable, "A cancelled old success cannot clear the replacement error")
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertFalse(value.isLoading)
    }
    func testHiddenAccountChangeRetainsDisplayAndRefreshesWhenVisibleAgain() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.loadIfNeeded()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        value.setDisplayVisible(false)
        await service.change(); await service.end(); await observation.value
        XCTAssertTrue(value.loaded); XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        let hiddenCalls = await service.calls; XCTAssertEqual(hiddenCalls.count, 2)
        value.setDisplayVisible(true)
        await value.loadIfNeeded()
        let calls = await service.calls; XCTAssertEqual(calls.count, 4)
    }
    func testSourceChangeBeforeSubscriptionIsReconciledOnReturnWithoutReusingOldActions() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await value.loadIfNeeded()
        let intent = try XCTUnwrap(value.makeActionIntent())
        value.setDisplayVisible(false)
        _ = try await service.selectHomeSource(.qq, ifRevision: 0)
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        await service.hold(gate)
        value.setDisplayVisible(true)
        let returned = Task { await value.loadIfNeeded() }
        await gate.didStart()
        XCTAssertTrue(value.sections.isEmpty); XCTAssertFalse(value.loaded)
        await value.perform(intent: intent) { XCTFail("The missed source notification cannot preserve old actions") }
        await gate.release(); await returned.value
        guard case .collection(let collection) = value.sections.first?.items.first?.content else {
            return XCTFail("Returning must load the durable source")
        }
        XCTAssertEqual(collection.platform, .qq)
        await value.loadIfNeeded()
        let calls = await service.calls
        XCTAssertEqual(calls.count, 4, "An unchanged source must not start another complete read")
        await service.end(); await observation.value
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
        let calls = await service.calls; XCTAssertEqual(calls.count, 2)
    }
    func testSourceChangeInvalidatesHomeAndCurrentSourceSelectionDoesNotReload() async throws {
        let service = HomeLifecycleFixture(), value = model(service)
        await value.refresh()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        _ = try await service.selectHomeSource(.netease, ifRevision: 0)
        _ = try await service.selectHomeSource(.qq, ifRevision: 0)
        await service.end(); await observation.value
        guard let item = value.sections.first?.items.first, case .collection(let collection) = item.content else {
            return XCTFail("Selected source must replace the home page")
        }
        XCTAssertEqual(collection.platform, .qq)
        XCTAssertTrue(value.contentIsCurrent)
        XCTAssertEqual(value.pageNumber, 2)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next", nil, "next"])
    }
    func testFilteredFirstPageContinuesToRealEndWithoutInventingVisibleRows() async {
        let service = HomeLifecycleFixture(), value = model(service)
        let gate = HomeLifecycleGate()
        await service.startWithFilteredPage()
        await service.hold(gate, cursor: "next")
        let read = Task { await value.refresh() }
        await gate.didStart()
        XCTAssertTrue(value.sections.isEmpty); XCTAssertTrue(value.loaded)
        XCTAssertNil(value.error); XCTAssertEqual(value.nextCursor, "next")
        XCTAssertFalse(value.requiresExplicitNext)
        await gate.release(); await read.value
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
            _ = try await service.selectHomeSource(.qq, ifRevision: 0)
            await service.end(); await observation.value
            XCTAssertTrue(value.sections.isEmpty, "Hidden content from the revoked scope must clear immediately")
            XCTAssertFalse(value.loaded); XCTAssertFalse(value.contentIsCurrent)
            let hiddenCalls = await service.calls
            XCTAssertEqual(hiddenCalls.count, destination == .home ? 2 : 1, "A retained owner is not a new read")
            await value.restoreHomeIfNeeded()
            let snapshotReads = await service.snapshotReads
            XCTAssertEqual(snapshotReads, 0)
            value.setDisplayVisible(true)
            await value.loadIfNeeded(); await value.loadIfNeeded()
            let returnedCalls = await service.calls
            XCTAssertEqual(returnedCalls.count, destination == .home ? 4 : 2)
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
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        value.setDisplayVisible(true)
        await value.loadIfNeeded()
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
    }

    func testCompleteHomeReadKeepsSectionOrderAndAdvancesFilteredAndDuplicateBatches() async {
        let service = HomeLifecycleFixture(), value = model(service)
        let first = HomeLifecycleFixture.page(["1"]).sections[0]
        let tail = MusicPublicBrowseSection(id: "original-tail", title: "原栏目", items:
            HomeLifecycleFixture.page(["3"]).sections[0].items, layout: .playlistRail)
        await service.setPages([
            "": .init(sections: [first], cursor: "filtered", consumed: 1),
            "filtered": .init(sections: [], cursor: "duplicate", consumed: 2),
            "duplicate": .init(sections: [first], cursor: "tail", consumed: 1),
            "tail": .init(sections: [tail], consumed: 1),
        ])
        await value.loadIfNeeded()
        XCTAssertEqual(value.sections.map(\.id), ["home:stable", "original-tail"])
        XCTAssertEqual(value.sections.flatMap(\.items).map(\.id), ["1", "3"])
        XCTAssertEqual(value.sections.map(\.layout), [.playlistRail, .playlistRail])
        XCTAssertNil(value.nextCursor); XCTAssertNil(value.error); XCTAssertFalse(value.isLoading)
        XCTAssertFalse(value.canPrevious)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "filtered", "duplicate", "tail"])
        XCTAssertEqual(calls.map { $0.1 }, [true, false, false, false])
        await value.previous(); await value.next(); await value.appendNext()
        XCTAssertEqual(value.sections.map(\.id), ["home:stable", "original-tail"])
        XCTAssertEqual(value.sections.flatMap(\.items).map(\.id), ["1", "3"])
        await value.loadIfNeeded()
        let afterReturn = await service.calls
        XCTAssertEqual(afterReturn.count, calls.count, "Completed home does not read beyond the real terminal response")
    }

    func testContinuationFailureRetainsSuccessAndRetryContinuesThroughRemainingBatches() async throws {
        let service = HomeLifecycleFixture(), value = model(service)
        await service.setPages([
            "": HomeLifecycleFixture.page(["1"], cursor: "next"),
            "next": HomeLifecycleFixture.page(["1", "2"], cursor: "tail", consumed: 1),
            "tail": HomeLifecycleFixture.page(["3"]),
        ])
        await service.fail(.unavailable, cursor: "next")
        await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertEqual(value.error, .unavailable); XCTAssertEqual(value.nextCursor, "next")
        XCTAssertTrue(value.contentIsCurrent)
        XCTAssertFalse(value.canPrevious)
        let captured = try XCTUnwrap(value.makeActionIntent())
        await value.previous(); await value.next(); await value.appendNext()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertEqual(value.error, .unavailable); XCTAssertEqual(value.nextCursor, "next")
        await value.loadIfNeeded()
        let stopped = await service.calls
        XCTAssertEqual(stopped.map { $0.0 }, [nil, "next"], "An ordinary error does not silently retry")
        await service.fail(nil); await value.retry()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2", "3"])
        XCTAssertNil(value.error); XCTAssertNil(value.nextCursor)
        var acted = false
        await value.perform(intent: captured) { acted = true }
        XCTAssertTrue(acted, "A continuation retry keeps captured public actions valid")
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next", "next", "tail"])
    }

    func testRepeatedRefreshJoinsTheCompleteInFlightHomeRead() async {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await service.hold(gate, cursor: "next")
        let first = Task { await value.refresh() }
        await gate.didStart()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        let joined = expectation(description: "refresh reentry starts before continuation is released")
        let reentry = Task { joined.fulfill(); await value.refresh() }
        await fulfillment(of: [joined], timeout: 1)
        await value.loadIfNeeded()
        await gate.release(); await first.value; await reentry.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        XCTAssertNil(value.nextCursor); XCTAssertFalse(value.isLoading)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
    }

    func testFailedContinuationSectionRetainsSuccessAndRetriesTheUncommittedBatch() async {
        let service = HomeLifecycleFixture(), value = model(service)
        let first = HomeLifecycleFixture.page(["1"], cursor: "next")
        await service.setPages([
            "": first,
            "next": .init(sections: [.init(id: "failed", title: "未成功栏目", items: [],
                layout: .playlistRail, error: .unavailable)]),
        ])
        await value.refresh()
        XCTAssertEqual(value.sections.map(\.id), ["home:stable"])
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertEqual(value.error, .unavailable); XCTAssertEqual(value.nextCursor, "next")
        XCTAssertTrue(value.contentIsCurrent); XCTAssertFalse(value.isLoading)
        await service.setPages([
            "": first,
            "next": HomeLifecycleFixture.page(["2"], cursor: "tail"),
            "tail": HomeLifecycleFixture.page(["3"]),
        ])
        await value.retry()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2", "3"])
        XCTAssertNil(value.error); XCTAssertNil(value.nextCursor)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next", "next", "tail"])
    }

    func testCallerCancellationStopsHomeDrainAndRejectsLateContinuationSuccess() async {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        await service.setPages([
            "": HomeLifecycleFixture.page(["1"], cursor: "next"),
            "next": HomeLifecycleFixture.page(["2"], cursor: "tail"),
            "tail": HomeLifecycleFixture.page(["3"]),
        ])
        await service.hold(gate, cursor: "next")
        let read = Task { await value.refresh() }
        await gate.didStart()
        read.cancel()
        await gate.release(); await read.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertNil(value.error); XCTAssertFalse(value.isLoading)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
    }

    func testProviderCancellationStopsWithoutLoopingOrInventingATerminalPage() async {
        let service = HomeLifecycleFixture(), value = model(service)
        await service.fail(.cancelled, cursor: "next")
        await value.refresh()
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1"])
        XCTAssertEqual(value.nextCursor, "next"); XCTAssertNil(value.error)
        XCTAssertFalse(value.isLoading)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
    }

    func testExitOrAccessRevocationStopsHomeDrainAndRejectsLateResults() async {
        for losesAccess in [false, true] {
            let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
            await service.hold(gate, cursor: "next")
            let read = Task { await value.refresh() }
            await gate.didStart()
            if losesAccess { value.setAccess(available: false, generation: UUID()) }
            else { value.disappear() }
            await gate.release(); await read.value
            XCTAssertTrue(value.sections.isEmpty); XCTAssertFalse(value.loaded)
            XCTAssertNil(value.nextCursor); XCTAssertNil(value.makeActionIntent())
            XCTAssertFalse(value.isLoading)
            let calls = await service.calls
            XCTAssertEqual(calls.map { $0.0 }, [nil, "next"])
        }
    }

    func testSourceChangeDuringContinuationRetiresOldReadAndLoadsTheReplacementFully() async throws {
        let service = HomeLifecycleFixture(), value = model(service), gate = HomeLifecycleGate()
        let observation = Task { await value.observeHomeChanges() }
        await service.waitForSubscription()
        await service.hold(gate, cursor: "next")
        let old = Task { await value.refresh() }
        await gate.didStart()
        let captured = try XCTUnwrap(value.makeActionIntent())
        _ = try await service.selectHomeSource(.qq, ifRevision: 0)
        await service.end(); await observation.value
        await gate.release(); await old.value
        XCTAssertEqual(value.sections.first?.items.map(\.id), ["1", "2"])
        for item in value.sections.flatMap(\.items) {
            guard case .collection(let collection) = item.content else { return XCTFail("Expected source-bound collections") }
            XCTAssertEqual(collection.platform, .qq)
        }
        await value.perform(intent: captured) { XCTFail("The retired source cannot authorize an old action") }
        XCTAssertNil(value.error); XCTAssertNil(value.nextCursor); XCTAssertFalse(value.isLoading)
        let calls = await service.calls
        XCTAssertEqual(calls.map { $0.0 }, [nil, "next", nil, "next"])
    }

    func testCyclicOrZeroConsumptionHomeContinuationStopsAtProtocolFailure() async {
        let cyclic: [String: MusicPublicBrowsePage] = [
            "": HomeLifecycleFixture.page(["1"], cursor: "a"),
            "a": HomeLifecycleFixture.page(["2"], cursor: "b"),
            "b": HomeLifecycleFixture.page(["3"], cursor: "a"),
        ]
        let zeroConsumption = ["": HomeLifecycleFixture.page(["1"], cursor: "a", consumed: 0)]
        let cases: [([String: MusicPublicBrowsePage], [String?])] = [
            (cyclic, [nil, "a", "b"]), (zeroConsumption, [nil]),
        ]
        for (pages, expected) in cases {
            let service = HomeLifecycleFixture(), value = model(service)
            await service.setPages(pages)
            await value.refresh()
            XCTAssertEqual(value.error, .invalidResponse); XCTAssertFalse(value.isLoading)
            let calls = await service.calls
            XCTAssertEqual(calls.map { $0.0 }, expected)
        }
    }
}

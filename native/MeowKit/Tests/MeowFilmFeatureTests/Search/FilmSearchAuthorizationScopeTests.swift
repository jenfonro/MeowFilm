#if os(iOS) || os(macOS)
import XCTest
import Combine
import MeowJavaScript
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

@MainActor
final class FilmSearchAuthorizationScopeTests: XCTestCase {
    func testGenerationChangeRejectsPendingPageAndSeedsWhileNewSessionKeepsRawIdentity() async throws {
        let entered = expectation(description: "generation one page two is physically pending")
        let reads = try AuthorizationScopeReads(secondPageEntered: entered)
        let rig = try await makeRig(reads)
        let old = try rig.service.session(for: "未关联样本")
        await old.drain()
        XCTAssertEqual(old.snapshot.rawCards.count, 2)
        XCTAssertTrue(old.snapshot.hasMore)
        let oldItems = old.snapshot.siteItems
        let oldContext = try rawContext(old, detailID: .number(0))
        let oldStringContext = try rawContext(old, detailID: .string("0"))
        let oldID = try oldContext.playbackTarget().id
        let oldStringID = try oldStringContext.playbackTarget().id
        XCTAssertNotEqual(oldID, oldStringID)

        old.loadMore()
        await fulfillment(of: [entered], timeout: 3)
        await rig.runtime.changeGeneration()
        try await rig.sources.refresh()
        await waitFor(old) { $0.siteState == .cancelled && !$0.loading }
        XCTAssertTrue(old.invalidated)
        XCTAssertFalse(old.snapshot.hasMore)
        let callsBefore = await reads.calls
        old.loadMore()
        old.startIfNeeded()
        let callsAfter = await reads.calls
        XCTAssertEqual(callsAfter, callsBefore, "Revoked sessions cannot admit more source work")

        let current = try rig.service.session(for: "未关联样本", reusing: old)
        guard current !== old else {
            XCTFail("A stable source key cannot authorize reuse across generations")
            return
        }
        await current.drain()
        XCTAssertNotEqual(old.snapshot.sessionID, current.snapshot.sessionID)
        XCTAssertEqual(current.snapshot.rawCards.count, 2)
        let currentContext = try rawContext(current, detailID: .number(0))
        let currentStringContext = try rawContext(current, detailID: .string("0"))
        XCTAssertEqual(try currentContext.playbackTarget().id, oldID)
        XCTAssertEqual(try currentStringContext.playbackTarget().id, oldStringID)
        XCTAssertEqual(currentContext.sources.first?.catField("generation"), .number(2))
        let callsBeforeRelease = await reads.calls
        XCTAssertEqual(callsBeforeRelease, [
            .init(generation: 1, query: "未关联样本", page: 1),
            .init(generation: 1, query: "未关联样本", page: 2),
            .init(generation: 2, query: "未关联样本", page: 1),
        ], "The new generation must settle without waiting for the revoked physical read")

        await reads.release()
        await old.drain()
        XCTAssertEqual(old.snapshot.siteItems, oldItems)
        XCTAssertEqual(old.snapshot.rawCards.count, 2)
        XCTAssertEqual(old.pages.count, 1, "A late page and its cache receipt must not be admitted")
        XCTAssertEqual(old.pages.first?.read.cacheEntry?.catField("page"), .number(1))
        XCTAssertEqual(old.pages.first?.read.cacheEntry?.catField("source")?.catField("generation"), .number(1))
        XCTAssertFalse(old.snapshot.loading)
        XCTAssertEqual(old.snapshot.siteState, .cancelled)

        let sites = FilmSearchService.searchableSites(await rig.sources.snapshot)
        let currentSite = try XCTUnwrap(sites.first)
        let base = FilmPlaybackRequest(target: try currentContext.playbackTarget(),
                                       sources: sites.map(\.playbackSource))
        let request = try currentContext.playbackRequest(base: base, owner: reads.producer)
        XCTAssertEqual(request.sources.first?.value["generation"], .number(2))
        XCTAssertEqual(request.results, [.object(["sourceKey": .string(currentSite.key),
            "items": .array([AuthorizationScopeReads.row(.number(0))])])])
        XCTAssertEqual(request.search, [.object(["sourceKey": .string(currentSite.key),
            "page": .number(1), "completed": .bool(false)])])
        XCTAssertEqual(request.cacheEntries, [AuthorizationScopeReads.seed(currentSite, query: "未关联样本", page: 1)])
        XCTAssertTrue(request.resultHints.isEmpty)
        let hints = try oldContext.playbackRequest(base: base, owner: reads.producer)
        XCTAssertEqual(hints.sources.first?.value["generation"], .number(2))
        XCTAssertTrue(hints.results.isEmpty)
        XCTAssertTrue(hints.search.isEmpty)
        XCTAssertTrue(hints.cacheEntries.isEmpty)
        XCTAssertEqual(hints.resultHints, [.object(["sourceKey": .string(currentSite.key),
            "items": .array([AuthorizationScopeReads.row(.number(0))])])])
    }

    func testSettledNavigationSessionCannotBeReusedUnderANewGeneration() async throws {
        let reads = try AuthorizationScopeReads()
        let rig = try await makeRig(reads)
        let retained = try rig.service.session(for: "原词")
        await retained.drain()
        let retainedContext = try rawContext(retained, detailID: .number(0))
        let originalID = try retainedContext.playbackTarget().id
        let other = try rig.service.session(for: "其他词")
        await other.drain()
        XCTAssertFalse(retained.invalidated)

        await rig.runtime.changeGeneration()
        try await rig.sources.refresh()
        await waitFor(other) { $0.siteState == .cancelled && !$0.loading }
        XCTAssertTrue(other.invalidated)
        XCTAssertFalse(retained.invalidated, "Settled navigation is not retained as service.current")
        let restored = try rig.service.session(for: "原词", reusing: retained)
        guard restored !== retained else {
            XCTFail("Reuse must compare the retained scope, not just current-session invalidation")
            return
        }
        await restored.drain()
        let restoredContext = try rawContext(restored, detailID: .number(0))
        XCTAssertNotEqual(restored.snapshot.sessionID, retained.snapshot.sessionID)
        XCTAssertEqual(try restoredContext.playbackTarget().id, originalID)
        XCTAssertEqual(restoredContext.sources.first?.catField("generation"), .number(2))
        let calls = await reads.calls
        XCTAssertEqual(calls, [
            .init(generation: 1, query: "原词", page: 1),
            .init(generation: 1, query: "其他词", page: 1),
            .init(generation: 2, query: "原词", page: 1),
        ])
    }

    func testUnknownSessionScopeIsNotAnEmptyOrRevokedSourceScope() async throws {
        let entered = expectation(description: "real rules initialization is pending")
        let engine = try AuthorizationScopeRuleEngine(entered: entered)
        let reads = try AuthorizationScopeReads()
        let rig = try await makeRig(reads, engine: engine)
        let pending = try rig.service.session(for: "未关联样本")
        await fulfillment(of: [entered], timeout: 3)
        let sites = FilmSearchService.searchableSites(await rig.sources.snapshot)
        XCTAssertEqual(sites.count, 1)
        XCTAssertTrue(pending.admittedSources.isEmpty)
        XCTAssertFalse(pending.hasDifferentSources(FilmSearchService.authorizationScope(sites)),
                       "A session still acquiring its scope has not admitted an empty scope")
        XCTAssertFalse(pending.invalidated)
        XCTAssertTrue(pending.snapshot.loading)
        let same = try rig.service.session(for: "未关联样本", reusing: pending)
        XCTAssertTrue(same === pending)
        let callsWhileUnknown = await reads.calls
        XCTAssertTrue(callsWhileUnknown.isEmpty)

        await engine.release()
        await same.drain()
        XCTAssertFalse(same.invalidated)
        XCTAssertEqual(same.snapshot.siteState, .completed)
        XCTAssertEqual(same.snapshot.rawCards.count, 2)
        XCTAssertEqual(same.admittedSources.first?.value["generation"], .number(1))
        XCTAssertTrue(same.hasDifferentSources([:]), "An acquired nonempty scope differs from a known empty scope")
        let calls = await reads.calls
        XCTAssertEqual(calls, [.init(generation: 1, query: "未关联样本", page: 1)])
    }

    private func makeRig(_ reads: AuthorizationScopeReads,
                         engine: AuthorizationScopeRuleEngine? = nil) async throws -> AuthorizationScopeRig {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let runtime = SourcesRuntimeFixture()
        await runtime.setCat(SourcesRuntimeFixture.cat("b", state: "disabled", enabled: false))
        let sourceStore = try DocumentStore(baseURL: root, namespace: FilmSourceService.documentNamespace)
        let ruleStore = try DocumentStore(baseURL: root, namespace: FilmRuleService.documentNamespace)
        let sources = FilmSourceService(transport: runtime.transport, storeFactory: { sourceStore })
        let rules: FilmRuleService
        if let engine { rules = FilmRuleService(engine: engine, store: ruleStore) }
        else { rules = try FilmRuleService(store: ruleStore) }
        let service = try FilmSearchService(sources: sources, rules: rules,
            data: .init(read: { site, query, page in await reads.search(site, query: query, page: page) }))
        addTeardownBlock {
            await reads.release()
            await engine?.release()
            await service.close()
            await rules.close()
            await sources.close()
            await reads.producer.close()
        }
        try await sources.refresh()
        return .init(runtime: runtime, sources: sources, service: service)
    }

    private func rawContext(_ session: FilmSearchSession, detailID: RuntimeValue) throws -> FilmSearchContext {
        let card = try XCTUnwrap(session.snapshot.rawCards.first { $0.value.catField("detailId") == detailID })
        return session.context(for: card)
    }

    private func waitFor(_ session: FilmSearchSession,
                         _ predicate: @escaping (FilmSearchSnapshot) -> Bool) async {
        let ready = expectation(description: "source scope invalidated the current session")
        var matched = false
        let observation = session.$snapshot.sink { value in
            if !matched, predicate(value) { matched = true; ready.fulfill() }
        }
        await fulfillment(of: [ready], timeout: 3)
        observation.cancel()
    }
}

private struct AuthorizationScopeRig {
    let runtime: SourcesRuntimeFixture
    let sources: FilmSourceService
    let service: FilmSearchService
}

private actor AuthorizationScopeReads {
    struct Call: Equatable {
        let generation: Int64
        let query: String
        let page: Int
    }
    nonisolated let producer: RuntimeHTTPClient
    private(set) var calls: [Call] = []
    private let secondPageEntered: XCTestExpectation?
    private let gate = AuthorizationScopeGate()

    init(secondPageEntered: XCTestExpectation? = nil) throws {
        self.secondPageEntered = secondPageEntered
        producer = try .init(baseURL: URL(string: "http://127.0.0.1:43211")!)
    }

    func search(_ site: FilmSearchSite, query: String, page: Int) async -> FilmSearchRead {
        calls.append(.init(generation: site.cat.generation, query: query, page: page))
        if site.cat.generation == 1, page == 2, let secondPageEntered {
            secondPageEntered.fulfill()
            // Deliberately ignore waiter cancellation so the late receipt
            // crosses the real session's revocation fence.
            await gate.wait()
        }
        return .init(value: Self.payload(page), producer: producer, cacheEntry: Self.seed(site, query: query, page: page))
    }

    func release() async { await gate.release() }

    nonisolated static func row(_ id: RuntimeValue) -> RuntimeValue {
        .object(["vod_id": id, "vod_name": .string("未关联样本"), "vod_remarks": .string("原备注")])
    }
    private nonisolated static func payload(_ page: Int) -> RuntimeValue {
        .object(["list": .array(page == 1 ? [row(.number(0)), row(.string("0"))] : [row(.string("late"))]),
                 "pagecount": .number(2)])
    }
    nonisolated static func seed(_ site: FilmSearchSite, query: String, page: Int) -> RuntimeValue {
        .object(["kind": .string("search"), "source": .object(site.playbackSource.value),
                 "query": .string(query), "page": .number(Double(page)), "value": payload(page),
                 "completeness": .string("complete"), "fetchedAt": .number(1000 + Double(page)),
                 "expiresAt": .number(9000 + Double(page))])
    }
}

private actor AuthorizationScopeGate {
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if released { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() {
        released = true
        let waiting = waiters
        waiters.removeAll()
        for waiter in waiting { waiter.resume() }
    }
}

private actor AuthorizationScopeRuleEngine: JavaScriptJSONEngine {
    private let engine: NativeJavaScriptJSONEngine
    private let entered: XCTestExpectation
    private let gate = AuthorizationScopeGate()
    private var first = true
    init(entered: XCTestExpectation) throws {
        self.entered = entered
        engine = NativeJavaScriptJSONEngine(source: try FilmRuleNativeProgram.load())
    }
    func invoke(_ request: Data) async throws -> Data {
        if first {
            first = false
            entered.fulfill()
            await gate.wait()
        }
        return try await engine.invoke(request)
    }
    func release() async { await gate.release() }
    func close() async { await gate.release(); await engine.close() }
}
#endif

import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowNovelFeature

@MainActor
final class NovelDiscoveryTests: XCTestCase {
    private var fixtures: [(NovelDiscoverySession, NovelDiscoveryRuntime, URL)] = []
    override func tearDown() async throws {
        for (session, runtime, root) in fixtures {
            await runtime.release()
            await session.close(); await session.feature.close()
            try? FileManager.default.removeItem(at: root)
        }
        fixtures = []
        try await super.tearDown()
    }

    private func fixture(sources: [NovelSource]? = nil) throws -> (NovelDiscoverySession, NovelDiscoveryRuntime) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: root, namespace: NovelLibrary.namespace)
        let runtime = NovelDiscoveryRuntime(sources: sources ?? [Self.source("a"), Self.source("b", order: 1)])
        let feature = NovelFeature(library: NovelLibrary(store: store), rules: NovelRuleProcessor(runtime: runtime), runtime: runtime)
        let session = NovelDiscoverySession(feature: feature)
        fixtures.append((session, runtime, root))
        return (session, runtime)
    }

    private static func source(_ id: String, revision: Int = 1, order: Double = 0) -> NovelSource {
        .init(installationId: id, revision: revision, name: id, sourceUrl: "", group: "", order: order,
              enabled: true, enabledExplore: true, protocol: "legado", actions: ["exploreKinds", "explore"])
    }

    private func wait(_ condition: @escaping @MainActor () async -> Bool,
                      file: StaticString = #filePath, line: UInt = #line) async {
        let deadline = ContinuousClock.now.advanced(by: .seconds(4))
        while !(await condition()), ContinuousClock.now < deadline { try? await Task.sleep(nanoseconds: 10_000_000) }
        let completed = await condition()
        XCTAssertTrue(completed, file: file, line: line)
    }

    private func activate(_ session: NovelDiscoverySession, _ runtime: NovelDiscoveryRuntime) async {
        session.activate()
        await wait {
            let lists = await runtime.listCount
            return !session.loading && lists >= 2
        }
    }

    func testGroupingKeepsOrderRepeatedHeadingsStandaloneAndURLOnlyItems() {
        let channels = NovelDiscoveryChannel.make([
            .init(title: "推荐", url: "first"),
            .init(title: "", url: "second, {\"method\":\"POST\"}"),
            .init(title: "榜单", url: ""),
            .init(title: "同名", url: "rank-a"),
            .init(title: "同名", url: "rank-b"),
            .init(title: "空组", url: ""),
            .init(title: "榜单", url: ""),
            .init(title: "", url: "rank-c"),
            .init(title: "末尾空组", url: "")
        ])
        XCTAssertEqual(channels.map(\.title), ["推荐", "未命名", "榜单", "榜单"])
        XCTAssertEqual(channels.map { $0.items.map(\.url) }, [["first"], ["second, {\"method\":\"POST\"}"], ["rank-a", "rank-b"], ["rank-c"]])
        XCTAssertEqual(channels.last?.items.first?.title, "未命名")
        XCTAssertEqual(Set(channels.map(\.id)).count, channels.count)
        let items = channels.flatMap(\.items)
        XCTAssertEqual(Set(items.map(\.id)).count, items.count)
        XCTAssertTrue(NovelDiscoveryChannel.make([.init(title: "空组", url: "")]).isEmpty)
    }

    func testKindsProjectionPreservesHeadingsURLOnlyAndRejectsUnusableResponses() async throws {
        let runtime = NovelDiscoveryRuntime(sources: [])
        let client = NovelSourceClient(runtime: runtime)
        await runtime.setKinds([.object(["title": .string("分组")]), .null,
                               .object(["url": .string("{{page}}, {\"method\":\"POST\"}")])])
        let kinds = try await client.kinds(Self.source("a"))
        XCTAssertEqual(kinds.map(\.title), ["分组", ""])
        XCTAssertEqual(kinds.last?.url, "{{page}}, {\"method\":\"POST\"}")
        await runtime.setKinds([.null, .object(["title": .string(" "), "url": .number(7)])])
        do { _ = try await client.kinds(Self.source("a")); XCTFail("Malformed kinds must not look like an empty discovery") }
        catch { XCTAssertEqual(error as? NovelError, .invalidResponse) }
        await runtime.setKinds([])
        let empty = try await client.kinds(Self.source("a"))
        XCTAssertTrue(empty.isEmpty)
    }

    func testFirstSelectionFiltersAvailabilityAndReturnPreservesLoadedSelection() async throws {
        var disabled = Self.source("disabled", order: -4); disabled.enabled = false
        var hidden = Self.source("hidden", order: -3); hidden.enabledExplore = false
        var search = Self.source("search", order: -2); search.actions = ["search"]
        let (session, runtime) = try fixture(sources: [Self.source("b", order: 2), hidden, Self.source("a", order: 1), disabled, search])
        await activate(session, runtime)
        XCTAssertEqual(session.availableSources.map(\.id), ["a", "b"])
        XCTAssertEqual(session.selectedSource?.id, "a")
        XCTAssertEqual(session.selectedChannel?.title, "推荐")
        XCTAssertEqual(session.selectedKind?.url, "first")
        let group = try XCTUnwrap(session.channels.last)
        session.selectChannel(id: group.id); await wait { !session.loading }
        session.selectKind(id: group.items[1].id); await wait { !session.loading }
        let books = session.books, channel = session.selectedChannelID, kind = session.selectedKindID
        let before = await runtime.calls.count
        session.deactivate(); session.activate()
        XCTAssertFalse(session.loading)
        XCTAssertEqual(session.books, books)
        XCTAssertEqual(session.selectedChannelID, channel); XCTAssertEqual(session.selectedKindID, kind)
        let after = await runtime.calls.count
        XCTAssertEqual(after, before, "Returning to a loaded page does not fetch it again")
    }

    func testImmediateCancellationDoesNotAdmitDiscoveryAndActivationStillLoads() async throws {
        let (session, runtime) = try fixture()
        session.activate(); session.cancel()
        await wait { (await runtime.listCount) >= 1 }
        let cancelledCalls = await runtime.calls
        XCTAssertTrue(cancelledCalls.isEmpty); XCTAssertFalse(session.loading)
        session.activate(); await wait { !session.loading && !session.books.isEmpty }
        XCTAssertEqual(session.selectedSource?.id, "a"); XCTAssertEqual(session.selectedKind?.url, "first")
        XCTAssertNil(session.error)
    }

    func testInitiallyEmptySourcesCanSelectFirstNewSource() async throws {
        let (session, runtime) = try fixture(sources: [])
        await activate(session, runtime)
        XCTAssertNil(session.selectedSource); XCTAssertFalse(session.canLoadMore)
        await runtime.setSources([Self.source("new")]); try await session.feature.refreshSources()
        await wait { !session.loading && !session.books.isEmpty }
        XCTAssertEqual(session.selectedSource?.id, "new")
        XCTAssertEqual(session.books.first?.installationId, "new")
    }

    func testSwitchingTargetsClearsBooksAndPaginationDeduplicatesWithinAndAcrossPages() async throws {
        let (session, runtime) = try fixture()
        await runtime.setBooks(["one", "one", "two"])
        await activate(session, runtime)
        XCTAssertEqual(session.books.map(\.bookUrl), ["one", "two"])
        await runtime.setBooks(["two", "three", "three"])
        session.loadMore(); await wait { !session.loading }
        XCTAssertEqual(session.books.map(\.bookUrl), ["one", "two", "three"])
        var calls = await runtime.calls
        XCTAssertEqual(calls.last?["payload"]["page"], .number(2))
        session.loadMore(); await wait { !session.loading }
        XCTAssertFalse(session.canLoadMore)
        let before = await runtime.calls.count
        session.loadMore()
        let after = await runtime.calls.count
        XCTAssertEqual(after, before)
        await runtime.blockNext("explore")
        session.selectSource(id: "b")
        XCTAssertTrue(session.books.isEmpty); XCTAssertFalse(session.canLoadMore)
        await wait { await runtime.blocked }
        await runtime.release(); await wait { !session.loading }
        XCTAssertEqual(session.books.map(\.installationId), ["b", "b"])
        calls = await runtime.calls
        XCTAssertEqual(calls.last?["payload"]["page"], .number(1))
        let group = try XCTUnwrap(session.channels.last)
        await runtime.blockNext("explore")
        session.selectChannel(id: group.id)
        XCTAssertTrue(session.books.isEmpty); XCTAssertEqual(session.selectedKind?.url, "rank-a")
        await wait { await runtime.blocked }
        await runtime.release(); await wait { !session.loading }
        session.selectKind(id: group.items[1].id); await wait { !session.loading }
        calls = await runtime.calls
        XCTAssertEqual(calls.last?["payload"]["url"], .string("rank-b"))
        XCTAssertEqual(calls.last?["payload"]["page"], .number(1))
    }

    func testEmptySuccessfulPageExhaustsAndFailedPageCanRetrySameNumber() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        let books = session.books
        await runtime.failNext("explore")
        session.loadMore(); await wait { !session.loading }
        XCTAssertEqual(session.books, books); XCTAssertTrue(session.canLoadMore); XCTAssertNotNil(session.error)
        await runtime.setBooks([])
        session.retry(); await wait { !session.loading }
        XCTAssertEqual(session.books, books); XCTAssertFalse(session.canLoadMore); XCTAssertNil(session.error)
        let pages = await runtime.calls.filter { $0["action"] == .string("explore") }.map { $0["payload"]["page"] }
        XCTAssertEqual(pages, [.number(1), .number(2), .number(2)])
    }

    func testRefreshPreservesURLAndCommitsKindsAndFirstPageOnlyAfterSuccess() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        let group = try XCTUnwrap(session.channels.last)
        session.selectChannel(id: group.id); await wait { !session.loading }
        session.selectKind(id: group.items[1].id); await wait { !session.loading }
        session.loadMore(); await wait { !session.loading }
        let oldChannels = session.channels, oldBooks = session.books
        let oldChannel = session.selectedChannelID, oldKind = session.selectedKindID, oldMore = session.canLoadMore
        await runtime.setKinds([.object(["title": .string("新入口"), "url": .string("new")]),
                                .object(["title": .string("新组")]),
                                .object(["title": .string("改名"), "url": .string("rank-b")])])
        await runtime.setBooks(["refreshed"])
        await runtime.blockNext("explore")
        session.refresh(); await wait { await runtime.blocked }
        XCTAssertEqual(session.channels, oldChannels); XCTAssertEqual(session.books, oldBooks)
        await runtime.release(failing: true); await wait { !session.loading }
        XCTAssertEqual(session.channels, oldChannels); XCTAssertEqual(session.books, oldBooks)
        XCTAssertEqual(session.selectedChannelID, oldChannel); XCTAssertEqual(session.selectedKindID, oldKind)
        XCTAssertEqual(session.canLoadMore, oldMore); XCTAssertNotNil(session.error)
        session.refresh(); await wait { !session.loading }
        XCTAssertEqual(session.selectedKind?.url, "rank-b"); XCTAssertEqual(session.selectedChannel?.title, "新组")
        XCTAssertEqual(session.books.map(\.bookUrl), ["refreshed"]); XCTAssertNil(session.error)
        let call = await runtime.calls.last
        XCTAssertEqual(call?["payload"]["page"], .number(1))
    }

    func testFailedRefreshLeavesNextPageNumberAndDoesNotRefreshOnReturn() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        session.loadMore(); await wait { !session.loading }
        let books = session.books
        await runtime.failNext("exploreKinds")
        session.refresh(); await wait { !session.loading }
        XCTAssertEqual(session.books, books); XCTAssertNotNil(session.error)
        let before = await runtime.calls.count
        session.deactivate(); session.activate()
        XCTAssertFalse(session.loading); XCTAssertEqual(session.books, books)
        let after = await runtime.calls.count
        XCTAssertEqual(after, before)
        session.loadMore(); await wait { !session.loading }
        let call = await runtime.calls.last
        XCTAssertEqual(call?["payload"]["page"], .number(3))
        XCTAssertEqual(session.books.count, 3); XCTAssertNil(session.error)
    }

    func testCancelledRefreshRetainsPageAndResumesRefreshWhenActivated() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        let books = session.books, channels = session.channels
        await runtime.setBooks(["cancelled"]); await runtime.blockNext("explore")
        session.refresh(); await wait { await runtime.blocked }
        session.cancel(); await wait { await runtime.cancelled }
        XCTAssertEqual(session.books, books); XCTAssertEqual(session.channels, channels)
        await runtime.setBooks(["refreshed"])
        session.activate(); await wait { !session.loading }
        XCTAssertEqual(session.books.map(\.bookUrl), ["refreshed"])
        await runtime.release(); await session.close()
        XCTAssertEqual(session.books.map(\.bookUrl), ["refreshed"])
        let call = await runtime.calls.last
        XCTAssertEqual(call?["payload"]["page"], .number(1))
    }

    func testDeactivateCancelsPhysicalCallAndResumesUnfinishedPageWithoutLatePublication() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        let initial = session.books
        await runtime.setBooks(["late"]); await runtime.blockNext("explore")
        session.loadMore(); await wait { await runtime.blocked }
        session.deactivate(); await wait { await runtime.cancelled }
        XCTAssertFalse(session.loading); XCTAssertEqual(session.books, initial)
        await runtime.setBooks(["resumed"])
        session.activate(); await wait { !session.loading }
        XCTAssertEqual(session.books.map(\.bookUrl), initial.map(\.bookUrl) + ["resumed"])
        await runtime.release(); await session.close()
        XCTAssertEqual(session.books.map(\.bookUrl), initial.map(\.bookUrl) + ["resumed"])
        let pages = await runtime.calls.filter { $0["action"] == .string("explore") }.map { $0["payload"]["page"] }
        XCTAssertEqual(pages, [.number(1), .number(2), .number(2)])
    }

    func testCancelledFirstKindsCannotStartExploreAndReactivationResumesInitialLoad() async throws {
        let (session, runtime) = try fixture()
        await runtime.blockNext("exploreKinds")
        session.activate(); await wait { await runtime.blocked }
        session.cancel(); await wait { await runtime.cancelled }
        await runtime.release()
        await session.close()
        let actions = await runtime.calls.map { $0["action"] }
        XCTAssertEqual(actions, [.string("exploreKinds")])
        XCTAssertTrue(session.books.isEmpty); XCTAssertTrue(session.channels.isEmpty)

        let (resumed, resumedRuntime) = try fixture()
        await resumedRuntime.blockNext("exploreKinds")
        resumed.activate(); await wait { await resumedRuntime.blocked }
        resumed.deactivate(); await wait { await resumedRuntime.cancelled }
        await resumedRuntime.release()
        resumed.activate(); await wait { !resumed.loading && !resumed.books.isEmpty }
        XCTAssertEqual(resumed.selectedKind?.url, "first")
    }

    func testSourceRevisionChangeCancelsOldGenerationAndReloadsOnlyCurrentRevision() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        await runtime.setBooks(["old"]); await runtime.blockNext("explore")
        session.loadMore(); await wait { await runtime.blocked }
        await runtime.setSources([Self.source("a", revision: 2), Self.source("b", order: 1)])
        await runtime.setBooks(["current"])
        try await session.feature.refreshSources()
        await wait { !session.loading && session.selectedSource?.revision == 2 && session.books.first?.bookUrl == "current" }
        await wait { await runtime.cancelled }
        await runtime.release(); await session.close()
        XCTAssertEqual(session.books.map(\.bookUrl), ["current"])
        let latest = await runtime.calls.last
        XCTAssertEqual(latest?["revision"], .number(2)); XCTAssertEqual(latest?["payload"]["page"], .number(1))
    }

    func testRemovedDisabledAndNonDiscoverableSourceNeverFallBackToAnotherSource() async throws {
        for change in 0..<4 {
            let (session, runtime) = try fixture()
            await activate(session, runtime)
            await runtime.blockNext("explore")
            session.loadMore(); await wait { await runtime.blocked }
            var changed = Self.source("a")
            if change == 1 { changed.enabled = false }
            if change == 2 { changed.enabledExplore = false }
            if change == 3 { changed.actions = ["search"] }
            await runtime.setSources((change == 0 ? [] : [changed]) + [Self.source("b", order: 1)])
            try await session.feature.refreshSources()
            await wait { await runtime.cancelled }
            XCTAssertEqual(session.selectedSource?.id, "a")
            XCTAssertTrue(session.books.isEmpty); XCTAssertTrue(session.channels.isEmpty); XCTAssertFalse(session.canLoadMore)
            session.deactivate(); session.activate(); await wait { !session.loading }
            XCTAssertEqual(session.selectedSource?.id, "a")
            XCTAssertEqual(session.availableSources.map(\.id), ["b"])
            session.selectSource(id: "b"); await wait { !session.loading && !session.books.isEmpty }
            await runtime.release(); await session.close()
            XCTAssertEqual(session.selectedSource?.id, "b")
            XCTAssertTrue(session.books.allSatisfy { $0.installationId == "b" })
        }
    }

    func testInactiveRevisionChangeWaitsForActivation() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        session.deactivate()
        let before = await runtime.calls.count
        await runtime.setSources([Self.source("a", revision: 2)])
        try await session.feature.refreshSources()
        XCTAssertTrue(session.books.isEmpty); XCTAssertFalse(session.loading)
        let after = await runtime.calls.count
        XCTAssertEqual(after, before)
        session.activate(); await wait { !session.loading && !session.books.isEmpty }
        let call = await runtime.calls.last
        XCTAssertEqual(call?["revision"], .number(2))
    }

    func testLateSourceListCannotRollBackAcceptedRevisionOrRestartDiscovery() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        await runtime.blockNext("library.list")
        let older = Task { try await session.feature.refreshSources() }
        await wait { await runtime.blocked }
        await runtime.setSources([Self.source("a", revision: 2)])
        try await session.feature.refreshSources()
        await wait { !session.loading && session.selectedSource?.revision == 2 }
        let books = session.books, calls = await runtime.calls
        await runtime.release(); try await older.value
        XCTAssertEqual(session.feature.sources.first?.revision, 2)
        XCTAssertEqual(session.selectedSource?.revision, 2)
        XCTAssertEqual(session.books, books); XCTAssertFalse(session.loading)
        let finalCalls = await runtime.calls
        XCTAssertEqual(finalCalls, calls)
    }

    func testCloseWaitsForAllAcceptedWorkAndDoesNotCloseSharedFeature() async throws {
        let (session, runtime) = try fixture()
        await activate(session, runtime)
        let books = session.books
        await runtime.blockNext("explore")
        session.loadMore(); await wait { await runtime.blocked }
        var finished = false
        let close = Task { await session.close(); finished = true }
        await wait { await runtime.cancelled }
        XCTAssertFalse(finished)
        let before = await runtime.calls.count
        session.activate(); session.refresh(); session.selectSource(id: "b"); session.loadMore()
        await runtime.release(); await close.value
        let after = await runtime.calls.count, closeCount = await runtime.closeCount
        XCTAssertEqual(after, before); XCTAssertEqual(closeCount, 0)
        XCTAssertFalse(session.feature.closed); XCTAssertEqual(session.books, books)
        try await session.feature.refreshSources()
    }
}

/// Deliberately holds one physical reply after cancellation. Subsequent calls
/// can finish so generation checks and close draining are tested independently.
private actor NovelDiscoveryRuntime: NovelRuntimeServing {
    private var sources: [NovelSource]
    private var kinds: [RuntimeValue] = [
        .object(["title": .string("推荐"), "url": .string("first")]),
        .object(["title": .string("榜单")]),
        .object(["title": .string("月榜"), "url": .string("rank-a")]),
        .object(["title": .string("周榜"), "url": .string("rank-b")])
    ]
    private var bookURLs: [String]?
    private var blockingAction: String?
    private var failingAction: String?
    private var response: CheckedContinuation<RuntimeValue, Error>?
    private var heldValue: RuntimeValue = .null
    private(set) var cancelled = false
    private(set) var calls: [RuntimeValue] = []
    private(set) var listCount = 0
    private(set) var closeCount = 0
    var blocked: Bool { response != nil }

    init(sources: [NovelSource]) { self.sources = sources }
    func setSources(_ sources: [NovelSource]) { self.sources = sources }
    func setKinds(_ kinds: [RuntimeValue]) { self.kinds = kinds }
    func setBooks(_ urls: [String]) { bookURLs = urls }
    func blockNext(_ action: String) { blockingAction = action; cancelled = false }
    func failNext(_ action: String) { failingAction = action }
    private func markCancelled() { cancelled = true }
    func release(failing: Bool = false) {
        if failing { response?.resume(throwing: NovelError.invalidResponse) }
        else { response?.resume(returning: heldValue) }
        response = nil
    }

    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "library.list" {
            listCount += 1
            let value = RuntimeValue.object(["sources": try .encoding(sources)])
            if blockingAction == operation {
                blockingAction = nil; heldValue = value
                return try await withCheckedThrowingContinuation { response = $0 }
            }
            return value
        }
        guard operation == "library.invoke", let action = parameters["action"].string else { throw NovelError.invalidResponse }
        calls.append(parameters)
        if failingAction == action { failingAction = nil; throw NovelError.invalidResponse }
        let value: RuntimeValue
        switch action {
        case "exploreKinds": value = .object(["kinds": .array(kinds)])
        case "explore":
            let payload = parameters["payload"], page = Int(payload["page"].number ?? 0)
            let urls = bookURLs ?? ["\(payload["url"].string ?? "")-\(page)"]
            value = .object(["books": .array(urls.map { .object(["bookUrl": .string($0), "name": .string($0)]) })])
        default: throw NovelError.invalidResponse
        }
        if blockingAction == action {
            blockingAction = nil; heldValue = value
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { response = $0 }
            } onCancel: { Task { await self.markCancelled() } }
        }
        return value
    }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func close() { closeCount += 1 }
}

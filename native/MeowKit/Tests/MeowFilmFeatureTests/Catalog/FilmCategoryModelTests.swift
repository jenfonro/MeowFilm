import Foundation
import MeowRuntime
import XCTest
@testable import MeowFilmFeature

@MainActor
final class FilmCategoryModelTests: XCTestCase {
    func testCombinedFiltersResetPaginationAndIgnoreLateResults() async throws {
        for lateFailure in [false, true] {
            let source = ControlledCategorySource()
            let model = FilmCategoryModel(category: .movie, source: source)
            let first = try XCTUnwrap(model.loadIfNeeded())
            await source.wait(1)
            let second = try XCTUnwrap(model.selectFilter("area", value: "日本"))
            await source.wait(2)
            let third = try XCTUnwrap(model.selectFilter("category", value: "豆瓣高分"))
            await source.wait(3)
            XCTAssertTrue(first.isCancelled)
            XCTAssertTrue(second.isCancelled)
            XCTAssertTrue(model.items.isEmpty)
            let requests = await source.requests
            XCTAssertEqual(requests[2].filters, ["category": "豆瓣高分", "area": "日本"])
            XCTAssertEqual(requests.map(\.offset), [0, 0, 0])
            XCTAssertEqual(requests.map(\.refresh), [false, false, false])
            await source.complete(3, .success(page(["new"], next: nil)))
            await third.value
            await source.complete(1, lateFailure ? .failure(FilmHomeError.network) : .success(page(["old"], next: 24)))
            await source.complete(2, .success(page(["older"], next: 24)))
            await first.value
            await second.value
            XCTAssertEqual(model.items.map(\.id), ["new"])
            XCTAssertNil(model.error)
            XCTAssertFalse(model.hasMore)
            XCTAssertFalse(model.isLoading)
            model.close()
        }
    }

    func testPaginationDeduplicatesRetriesSameOffsetAndStopsAtEnd() async throws {
        let source = ControlledCategorySource()
        let model = FilmCategoryModel(category: .movie, source: source)
        let first = try XCTUnwrap(model.loadIfNeeded())
        _ = model.loadMore()
        await source.wait(1)
        await source.complete(1, .success(page(["a", "b", "a"], next: 24)))
        await first.value
        XCTAssertEqual(model.items.map(\.id), ["a", "b"])
        let failed = try XCTUnwrap(model.loadMore())
        await source.wait(2)
        await source.complete(2, .failure(FilmHomeError.timeout))
        await failed.value
        XCTAssertEqual(model.error, .timeout)
        XCTAssertEqual(model.items.map(\.id), ["a", "b"])
        let retry = try XCTUnwrap(model.loadMore())
        await source.wait(3)
        await source.complete(3, .success(page(["b", "c"], next: nil)))
        await retry.value
        let requests = await source.requests
        XCTAssertEqual(requests.map(\.offset), [0, 24, 24])
        XCTAssertEqual(model.items.map(\.id), ["a", "b", "c"])
        XCTAssertNil(model.error)
        XCTAssertFalse(model.hasMore)
        XCTAssertNil(model.loadMore())
        model.close()
    }

    func testEmptySuccessCloseAndReleaseCancelRequests() async throws {
        let source = ControlledCategorySource()
        var model: FilmCategoryModel? = FilmCategoryModel(category: .anime, source: source)
        let first = try XCTUnwrap(model?.loadIfNeeded())
        await source.wait(1)
        await source.complete(1, .success(page([], next: nil)))
        await first.value
        XCTAssertEqual(model?.hasMore, false)
        let reentry = try XCTUnwrap(model?.loadIfNeeded())
        await source.wait(2)
        await source.complete(2, .success(page([], next: nil)))
        await reentry.value
        let pending = try XCTUnwrap(model?.refresh())
        await source.wait(3)
        model?.close()
        XCTAssertTrue(pending.isCancelled)
        await source.complete(3, .success(page(["late"], next: 24)))
        await pending.value
        XCTAssertEqual(model?.items, [])
        XCTAssertNil(model?.refresh())
        model = FilmCategoryModel(category: .movie, source: source)
        weak var released = model
        let abandoned = try XCTUnwrap(model?.loadIfNeeded())
        await source.wait(4)
        model = nil
        XCTAssertNil(released)
        XCTAssertTrue(abandoned.isCancelled)
        await source.complete(4, .success(page([], next: nil)))
        await abandoned.value
    }

    func testReentryAndRefreshConsultOwnerAndKeepOldItemsOnFailureOrStale() async throws {
        let source = ControlledCategorySource()
        let model = FilmCategoryModel(category: .movie, source: source)
        let initial = try XCTUnwrap(model.loadIfNeeded())
        await source.wait(1)
        await source.complete(1, .success(page(["old"], next: 24)))
        await initial.value
        let failed = try XCTUnwrap(model.refresh())
        XCTAssertEqual(model.items.map(\.id), ["old"])
        await source.wait(2)
        await source.complete(2, .failure(RuntimeHTTPError.remote("UPSTREAM_HTTP")))
        await failed.value
        XCTAssertEqual(model.items.map(\.id), ["old"])
        XCTAssertEqual(model.error, .runtime(code: "UPSTREAM_HTTP"))
        let cache = try FilmMetadataCache(value: .object([
            "status": .string("stale"), "error": .object(["code": .string("NETWORK_ERROR")]),
            "components": .object([:])
        ]))
        let check = try XCTUnwrap(model.loadIfNeeded())
        XCTAssertEqual(model.items.map(\.id), ["old"])
        await source.wait(3)
        await source.complete(3, .success(.init(items: page(["old"], next: 24).items, nextOffset: 24, cache: cache)))
        await check.value
        XCTAssertEqual(model.items.map(\.id), ["old"])
        XCTAssertEqual(model.error, .runtime(code: "NETWORK_ERROR"))
        let recovered = try XCTUnwrap(model.refresh())
        await source.wait(4)
        await source.complete(4, .success(page(["fresh"], next: nil)))
        await recovered.value
        XCTAssertEqual(model.items.map(\.id), ["fresh"])
        XCTAssertNil(model.error)
        let requests = await source.requests
        XCTAssertEqual(requests.map(\.offset), [0, 0, 0, 0])
        XCTAssertEqual(requests.map(\.refresh), [false, true, false, true])
        model.close()
    }

    func testReentryChecksTheExpandedRangeAndKeepsItsNextOffsetForFreshAndStalePages() async throws {
        for stale in [false, true] {
            let source = ControlledCategorySource()
            let model = FilmCategoryModel(category: .movie, source: source)
            let firstIDs = (0..<24).map { String($0) }, secondIDs = (24..<48).map { String($0) }
            let first = try XCTUnwrap(model.loadIfNeeded())
            await source.wait(1)
            await source.complete(1, .success(page(firstIDs, next: 24)))
            await first.value
            let more = try XCTUnwrap(model.loadMore())
            await source.wait(2)
            await source.complete(2, .success(page(secondIDs, next: 48)))
            await more.value
            let original = model.items
            let cache = try FilmMetadataCache(value: .object([
                "status": .string(stale ? "stale" : "fresh"), "components": .object([:]),
                "error": stale ? .object(["code": .string("NETWORK_ERROR")]) : .null
            ]))
            let reentry = try XCTUnwrap(model.loadIfNeeded())
            await source.wait(3)
            await source.complete(3, .success(.init(items: page(firstIDs, next: 24).items, nextOffset: 24, cache: cache)))
            await source.wait(4)
            XCTAssertEqual(model.items, original, "Do not publish only the first page while checking the expanded range.")
            XCTAssertTrue(model.isLoading)
            await source.complete(4, .success(.init(items: page(secondIDs, next: 48).items, nextOffset: 48, cache: cache)))
            await reentry.value
            XCTAssertEqual(model.items, original)
            XCTAssertEqual(model.items.count, 48)
            XCTAssertEqual(model.error, stale ? .runtime(code: "NETWORK_ERROR") : nil)
            let next = try XCTUnwrap(model.loadMore())
            await source.wait(5)
            await source.complete(5, .success(page(["48"], next: nil)))
            await next.value
            let requests = await source.requests
            XCTAssertEqual(requests.map(\.offset), [0, 24, 0, 24, 48])
            XCTAssertEqual(requests.map(\.refresh), [false, false, false, false, false])
            XCTAssertEqual(model.items.count, 49)
            model.close()
        }
    }

    func testSecondPageFailureDuringReentryOrRefreshKeepsTheWholeDisplayAndContinuation() async throws {
        for refresh in [false, true] {
            let source = ControlledCategorySource()
            let model = FilmCategoryModel(category: .movie, source: source)
            let first = try XCTUnwrap(model.loadIfNeeded())
            await source.wait(1)
            await source.complete(1, .success(page((0..<24).map { String($0) }, next: 24)))
            await first.value
            let more = try XCTUnwrap(model.loadMore())
            await source.wait(2)
            await source.complete(2, .success(page((24..<48).map { String($0) }, next: 48)))
            await more.value
            let original = model.items
            let check = try XCTUnwrap(refresh ? model.refresh() : model.loadIfNeeded())
            await source.wait(3)
            await source.complete(3, .success(page(["new-first"], next: 24)))
            await source.wait(4)
            XCTAssertEqual(model.items, original)
            await source.complete(4, .failure(RuntimeHTTPError.remote("UPSTREAM_HTTP")))
            await check.value
            XCTAssertEqual(model.items, original)
            XCTAssertEqual(model.error, .runtime(code: "UPSTREAM_HTTP"))
            XCTAssertTrue(model.hasMore)
            let next = try XCTUnwrap(model.loadMore())
            await source.wait(5)
            await source.complete(5, .success(page(["48"], next: nil)))
            await next.value
            let requests = await source.requests
            XCTAssertEqual(requests.map(\.offset), [0, 24, 0, 24, 48])
            XCTAssertEqual(requests.map(\.refresh), [false, false, refresh, refresh, false])
            XCTAssertEqual(model.items, original + page(["48"], next: nil).items)
            model.close()
        }
    }

    func testSuccessfulEmptyFirstPageClearsPreviouslyExpandedPages() async throws {
        let source = ControlledCategorySource()
        let model = FilmCategoryModel(category: .movie, source: source)
        let first = try XCTUnwrap(model.loadIfNeeded())
        await source.wait(1)
        await source.complete(1, .success(page(["first"], next: 24)))
        await first.value
        let more = try XCTUnwrap(model.loadMore())
        await source.wait(2)
        await source.complete(2, .success(page(["second"], next: 48)))
        await more.value
        let refresh = try XCTUnwrap(model.refresh())
        await source.wait(3)
        await source.complete(3, .success(page([], next: nil)))
        await refresh.value
        XCTAssertTrue(model.items.isEmpty)
        XCTAssertFalse(model.hasMore)
        XCTAssertNil(model.error)
        XCTAssertNil(model.loadMore())
        let requests = await source.requests
        XCTAssertEqual(requests.map(\.offset), [0, 24, 0])
        model.close()
    }

    func testUnsupportedAndUnchangedFiltersDoNotRequestAndTVHidesInapplicableRegion() async throws {
        let source = ControlledCategorySource()
        let model = FilmCategoryModel(category: .tv, source: source)
        XCTAssertEqual(model.filterRows.map(\.id), ["category", "area"])
        XCTAssertNil(model.selectFilter("area", value: "unknown"))
        XCTAssertNil(model.selectFilter("category", value: "tv"))
        let change = try XCTUnwrap(model.selectFilter("category", value: "tv_documentary"))
        await source.wait(1)
        XCTAssertEqual(model.filterRows.map(\.id), ["category"])
        XCTAssertNil(model.selectFilter("area", value: "tv_domestic"))
        await source.complete(1, .success(page([], next: nil)))
        await change.value
        model.close()
    }

    private func page(_ ids: [String], next: Int?) -> FilmCategoryPage {
        .init(items: ids.map { FilmItem(sourceID: "fixture", id: $0, title: $0) }, nextOffset: next)
    }
}

private actor ControlledCategorySource: FilmCategorySource {
    nonisolated let descriptor = FilmSourceDescriptor(id: "fixture", title: "测试")
    nonisolated let categories = FilmCategory.allCases
    struct Request: Sendable { let filters: [String: String]; let offset: Int; let refresh: Bool }
    private(set) var requests: [Request] = []
    private var pending: [Int: CheckedContinuation<FilmCategoryPage, Error>] = [:]
    private var arrivals: [(Int, CheckedContinuation<Void, Never>)] = []
    func loadHome(refresh: Bool) async throws -> [FilmHomeSection] { [] }
    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int, refresh: Bool) async throws -> FilmCategoryPage {
        requests.append(.init(filters: filters, offset: offset, refresh: refresh))
        let call = requests.count
        return try await withCheckedThrowingContinuation { continuation in
            pending[call] = continuation
            let ready = arrivals.filter { $0.0 <= call }
            arrivals.removeAll { $0.0 <= call }
            for (_, continuation) in ready { continuation.resume() }
        }
    }
    func wait(_ count: Int) async {
        if requests.count < count { await withCheckedContinuation { arrivals.append((count, $0)) } }
    }
    func complete(_ count: Int, _ result: Result<FilmCategoryPage, Error>) {
        pending.removeValue(forKey: count)?.resume(with: result)
    }
}

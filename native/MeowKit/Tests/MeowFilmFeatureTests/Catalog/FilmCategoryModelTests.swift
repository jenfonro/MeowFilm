import Foundation
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
        XCTAssertNil(model.loadIfNeeded(), "Returning to the page must not retry a failure automatically.")
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
        XCTAssertNil(model?.loadIfNeeded())
        let pending = try XCTUnwrap(model?.refresh())
        await source.wait(2)
        model?.close()
        XCTAssertTrue(pending.isCancelled)
        await source.complete(2, .success(page(["late"], next: 24)))
        await pending.value
        XCTAssertEqual(model?.items, [])
        XCTAssertNil(model?.refresh())
        model = FilmCategoryModel(category: .movie, source: source)
        weak var released = model
        let abandoned = try XCTUnwrap(model?.loadIfNeeded())
        await source.wait(3)
        model = nil
        XCTAssertNil(released)
        XCTAssertTrue(abandoned.isCancelled)
        await source.complete(3, .success(page([], next: nil)))
        await abandoned.value
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
    struct Request: Sendable { let filters: [String: String]; let offset: Int }
    private(set) var requests: [Request] = []
    private var pending: [Int: CheckedContinuation<FilmCategoryPage, Error>] = [:]
    private var arrivals: [(Int, CheckedContinuation<Void, Never>)] = []
    func loadHome() async throws -> [FilmHomeSection] { [] }
    func loadCategory(_ category: FilmCategory, filters: [String: String], offset: Int) async throws -> FilmCategoryPage {
        requests.append(.init(filters: filters, offset: offset))
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

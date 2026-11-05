import Foundation
import XCTest
@testable import MeowFilmFeature

@MainActor
final class FilmHomeModelTests: XCTestCase {
    func testConstructionDoesNotLoadAndRepeatedEntrySharesOneRequest() async throws {
        let source = ControlledFilmSource(id: "first")
        let model = FilmHomeModel(sources: [source])
        let initialCount = await source.numberOfCalls
        XCTAssertEqual(initialCount, 0)
        XCTAssertEqual(model.selectedSourceID, "first")
        XCTAssertFalse(model.isLoading)

        let first = try XCTUnwrap(model.loadIfNeeded())
        let repeated = try XCTUnwrap(model.loadIfNeeded())
        XCTAssertTrue(model.isLoading)
        await source.waitForCalls(1)
        let pendingCount = await source.numberOfCalls
        XCTAssertEqual(pendingCount, 1)
        let sections = home(sourceID: "first", title: "已加载")
        await source.complete(1, with: .success(sections))
        await first.value
        await repeated.value

        XCTAssertEqual(model.sections, sections)
        XCTAssertNil(model.error)
        XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.loadIfNeeded())
        let finalCount = await source.numberOfCalls
        XCTAssertEqual(finalCount, 1)
    }

    func testFailureRequiresExplicitRetryAndEmptySuccessEndsLoading() async throws {
        let source = ControlledFilmSource(id: "first")
        let model = FilmHomeModel(sources: [source])
        let failed = try XCTUnwrap(model.loadIfNeeded())
        await source.waitForCalls(1)
        await source.complete(1, with: .failure(FilmHomeError.upstream(statusCode: 503)))
        await failed.value
        XCTAssertEqual(model.error, .upstream(statusCode: 503))
        XCTAssertTrue(model.sections.isEmpty)
        XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.loadIfNeeded(), "Re-entering a failed page must not retry implicitly.")

        let retry = try XCTUnwrap(model.refresh())
        XCTAssertTrue(model.isLoading)
        XCTAssertNil(model.error)
        await source.waitForCalls(2)
        let empty = [FilmHomeSection(id: "movie", title: "热门电影", items: [])]
        await source.complete(2, with: .success(empty))
        await retry.value
        XCTAssertEqual(model.sections, empty)
        XCTAssertNil(model.error)
        XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.loadIfNeeded(), "An empty success is still a completed snapshot.")
        let count = await source.numberOfCalls
        XCTAssertEqual(count, 2)
    }

    func testSourceSwitchRejectsLateSuccessAndFailureAndRestoresCompletedSnapshots() async throws {
        for failsLate in [false, true] {
            let firstSource = ControlledFilmSource(id: "first")
            let secondSource = ControlledFilmSource(id: "second")
            let model = FilmHomeModel(sources: [firstSource, secondSource])
            let old = try XCTUnwrap(model.loadIfNeeded())
            await firstSource.waitForCalls(1)
            let current = try XCTUnwrap(model.selectSource(id: "second"))
            XCTAssertTrue(old.isCancelled)
            XCTAssertEqual(model.selectedSourceID, "second")
            XCTAssertTrue(model.sections.isEmpty)
            await secondSource.waitForCalls(1)
            let secondHome = home(sourceID: "second", title: "当前源")
            await secondSource.complete(1, with: .success(secondHome))
            await current.value

            let late: Result<[FilmHomeSection], Error> = failsLate
                ? .failure(FilmHomeError.network)
                : .success(home(sourceID: "first", title: "迟到的旧源"))
            await firstSource.complete(1, with: late)
            await old.value
            XCTAssertEqual(model.selectedSourceID, "second")
            XCTAssertEqual(model.sections, secondHome)
            XCTAssertNil(model.error)
            XCTAssertFalse(model.isLoading)
            let observedCancellation = await firstSource.cancelledSettlements
            XCTAssertEqual(observedCancellation, [1])

            let reload = try XCTUnwrap(model.selectSource(id: "first"))
            XCTAssertTrue(model.sections.isEmpty, "A retired response must not seed the old source's cache.")
            await firstSource.waitForCalls(2)
            await firstSource.complete(2, with: .success(home(sourceID: "first", title: "重新加载")))
            await reload.value
            XCTAssertNil(model.selectSource(id: "second"))
            XCTAssertEqual(model.sections, secondHome)
            let secondCount = await secondSource.numberOfCalls
            XCTAssertEqual(secondCount, 1, "Returning to a completed source reuses its snapshot.")
        }
    }

    func testRefreshRevokesOlderRequestOnTheSameSourceAndPreservesVisibleContent() async throws {
        let source = ControlledFilmSource(id: "first")
        let model = FilmHomeModel(sources: [source])
        let seedLoad = try XCTUnwrap(model.loadIfNeeded())
        await source.waitForCalls(1)
        let seed = home(sourceID: "first", title: "原内容")
        await source.complete(1, with: .success(seed))
        await seedLoad.value

        let oldRefresh = try XCTUnwrap(model.refresh())
        await source.waitForCalls(2)
        let newRefresh = try XCTUnwrap(model.refresh())
        XCTAssertTrue(oldRefresh.isCancelled)
        XCTAssertEqual(model.sections, seed)
        await source.waitForCalls(3)
        let fresh = home(sourceID: "first", title: "刷新后的内容")
        await source.complete(3, with: .success(fresh))
        await newRefresh.value
        await source.complete(2, with: .success(home(sourceID: "first", title: "迟到的刷新")))
        await oldRefresh.value
        XCTAssertEqual(model.sections, fresh)
        XCTAssertNil(model.error)
        XCTAssertFalse(model.isLoading)
    }

    func testCancelAllowsAnotherEntryAndClosePermanentlyRevokesPublication() async throws {
        let source = ControlledFilmSource(id: "first")
        let model = FilmHomeModel(sources: [source])
        let cancelled = try XCTUnwrap(model.loadIfNeeded())
        await source.waitForCalls(1)
        model.cancel()
        XCTAssertTrue(cancelled.isCancelled)
        XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.error)

        let replacement = try XCTUnwrap(model.loadIfNeeded())
        await source.waitForCalls(2)
        await source.complete(1, with: .failure(FilmHomeError.network))
        await cancelled.value
        XCTAssertTrue(model.isLoading, "A cancelled request must not finish its replacement's loading state.")
        XCTAssertNil(model.error)
        model.close()
        XCTAssertTrue(replacement.isCancelled)
        await source.complete(2, with: .success(home(sourceID: "first", title: "关闭后的响应")))
        await replacement.value
        XCTAssertTrue(model.sections.isEmpty)
        XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.error)
        XCTAssertNil(model.loadIfNeeded())
        XCTAssertNil(model.refresh())
        XCTAssertNil(model.selectSource(id: "first"))
    }

    func testCancelDuringRefreshRetainsLastCompletedSnapshot() async throws {
        let source = ControlledFilmSource(id: "first")
        let model = FilmHomeModel(sources: [source])
        let seedLoad = try XCTUnwrap(model.loadIfNeeded())
        await source.waitForCalls(1)
        let seed = home(sourceID: "first", title: "已完成快照")
        await source.complete(1, with: .success(seed))
        await seedLoad.value
        let refresh = try XCTUnwrap(model.refresh())
        await source.waitForCalls(2)
        model.cancel()
        await source.complete(2, with: .failure(FilmHomeError.timeout))
        await refresh.value
        XCTAssertEqual(model.sections, seed)
        XCTAssertNil(model.error)
        XCTAssertNil(model.loadIfNeeded())
    }

    func testReleasingPageModelCancelsWithoutWaitingForUncooperativeSource() async throws {
        let source = ControlledFilmSource(id: "first")
        var model: FilmHomeModel? = FilmHomeModel(sources: [source])
        weak var releasedModel = model
        let load = try XCTUnwrap(model?.loadIfNeeded())
        await source.waitForCalls(1)
        model = nil
        XCTAssertNil(releasedModel, "The loading task must not retain the page model across its await.")
        XCTAssertTrue(load.isCancelled)
        await source.complete(1, with: .success(home(sourceID: "first", title: "释放后的响应")))
        await load.value
    }

    private func home(sourceID: String, title: String) -> [FilmHomeSection] {
        [FilmHomeSection(id: "movie", title: "热门电影",
                         items: [FilmItem(sourceID: sourceID, id: "same-item", title: title)])]
    }
}

/// Returns its result even after cancellation, so late publication is observable.
private actor ControlledFilmSource: FilmSource {
    nonisolated let descriptor: FilmSourceDescriptor
    private(set) var numberOfCalls = 0
    private(set) var cancelledSettlements: [Int] = []
    private var pending: [Int: CheckedContinuation<[FilmHomeSection], Error>] = [:]
    private var arrivals: [(count: Int, continuation: CheckedContinuation<Void, Never>)] = []

    init(id: String) {
        descriptor = FilmSourceDescriptor(id: id, title: id)
    }

    func loadHome() async throws -> [FilmHomeSection] {
        numberOfCalls += 1
        let call = numberOfCalls
        defer {
            if Task.isCancelled { cancelledSettlements.append(call) }
        }
        return try await withCheckedThrowingContinuation { continuation in
            pending[call] = continuation
            let ready = arrivals.filter { $0.count <= numberOfCalls }
            arrivals.removeAll { $0.count <= numberOfCalls }
            for arrival in ready { arrival.continuation.resume() }
        }
    }

    func waitForCalls(_ count: Int) async {
        if numberOfCalls < count {
            await withCheckedContinuation { arrivals.append((count, $0)) }
        }
    }

    func complete(_ call: Int, with result: Result<[FilmHomeSection], Error>) {
        pending.removeValue(forKey: call)?.resume(with: result)
    }
}

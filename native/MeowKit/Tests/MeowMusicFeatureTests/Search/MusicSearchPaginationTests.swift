import XCTest
@testable import MeowMusicFeature

@MainActor final class MusicSearchPaginationTests: XCTestCase {
    func testEachPlatformRequestIsOnePageAcrossPagesAndConcurrencySettings() async throws {
        for platform in MusicPublicBrowsePlatform.allCases {
            for pages in [1, 2, 4, 10] {
                for concurrency in [1, 2, 10] {
                    var loaded: [MusicSearchPagination.Request] = [], accepted: [MusicSearchPagination.Request] = []
                    try await MusicSearchRoundReader.read(platform: platform, start: 1,
                        pages: pages, concurrency: concurrency, load: { request in
                            loaded.append(request)
                            return request.nextPage
                        }, accept: { request, next in
                            accepted.append(request)
                            return next
                        })
                    XCTAssertEqual(Set(loaded), Set(accepted))
                    XCTAssertEqual(accepted.count, pages)
                    XCTAssertEqual(accepted.first?.page, 1)
                    XCTAssertEqual(try XCTUnwrap(accepted.last).nextPage, Optional(pages + 1))
                    XCTAssertTrue(accepted.allSatisfy {
                        $0.limit == MusicSearchPagination.pageSize(platform)
                    })
                    XCTAssertEqual(Set(loaded.map(\.page)).count, loaded.count)
                    XCTAssertEqual(Set(accepted.map(\.page)).count, accepted.count)
                    for pair in zip(accepted, accepted.dropFirst()) {
                        XCTAssertEqual(pair.0.nextPage, Optional(pair.1.page))
                    }
                }
            }
        }
    }
    func testOffsetIsAlwaysPageMinusOneTimesLimitIncludingNetease() throws {
        for platform in MusicPublicBrowsePlatform.allCases {
            XCTAssertEqual(try MusicSearchPagination.offset(platform: platform, page: 1, limit: 30), 0)
            XCTAssertEqual(try MusicSearchPagination.offset(platform: platform, page: 2, limit: 30), 30)
            XCTAssertEqual(try MusicSearchPagination.offset(platform: platform, page: 4, limit: 30), 90)
            XCTAssertThrowsError(try MusicSearchPagination.offset(platform: platform, page: .max, limit: 30))
        }
        XCTAssertEqual(try (1...10).map {
            try MusicSearchPagination.offset(platform: .netease, page: $0, limit: 30)
        }, Array(stride(from: 0, to: 300, by: 30)))
    }
    func testNextCursorOnlyAdvancesByOneAndRequiresItems() {
        XCTAssertTrue(MusicSearchPagination.validNextPage(page: 1, limit: 30, next: 2, hasItems: true))
        XCTAssertFalse(MusicSearchPagination.validNextPage(page: 1, limit: 30, next: 3, hasItems: true))
        XCTAssertFalse(MusicSearchPagination.validNextPage(page: 1, limit: 30, next: 2, hasItems: false))
        XCTAssertFalse(MusicSearchPagination.validNextPage(page: .max, limit: 30, next: .max, hasItems: true))
        XCTAssertTrue(MusicSearchPagination.validNextPage(page: 1, limit: 30, next: nil, hasItems: false))
        XCTAssertFalse(MusicSearchPagination.validNextPage(page: 0, limit: 30, next: nil, hasItems: false))
        XCTAssertFalse(MusicSearchPagination.validNextPage(page: 1, limit: 101, next: nil, hasItems: false))
    }
    func testShortEmptyAndFullTotalPagesTerminateWhileFullPageContinues() throws {
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: 29, total: 100))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: 0, total: 100))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 2, limit: 30, count: 0, total: 0))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: 30, total: 30))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 4, limit: 30, count: 30, total: 120))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: 30, total: 29))
        XCTAssertNil(try MusicSearchPagination.nextPage(page: 2, limit: 30, count: 30, total: 59))
        XCTAssertEqual(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: 30, total: 61), 2)
        for (count, total) in [(31, 61), (-1, 61), (0, -1)] {
            XCTAssertThrowsError(try MusicSearchPagination.nextPage(page: 1, limit: 30, count: count, total: total)) {
                XCTAssertEqual($0 as? MusicServiceError, .upstreamInvalid)
            }
        }
    }
    func testTerminalRawPagesStopEveryPlatformBeforeTheNextSerialRequest() async throws {
        for platform in MusicPublicBrowsePlatform.allCases {
            for (count, total) in [(0, 100), (2, 100), (30, 30), (30, 29)] {
                var loaded: [Int] = [], accepted: [Int] = []
                try await MusicSearchRoundReader.read(platform: platform, start: 1, pages: 10, concurrency: 1,
                    load: { request in
                        loaded.append(request.page)
                        return try MusicSearchPagination.nextPage(page: request.page,
                            limit: request.limit, count: count, total: total)
                    }, accept: { request, next in
                        accepted.append(request.page)
                        return next
                    })
                XCTAssertEqual(loaded, [1])
                XCTAssertEqual(accepted, [1])
            }
        }
    }
    func testOutOfOrderConcurrentCompletionKeepsContinuousPageNumbersForEveryPlatform() async throws {
        for platform in MusicPublicBrowsePlatform.allCases {
            let firstEntered = expectation(description: "\(platform.rawValue) first page held")
            let fourthCompleted = expectation(description: "\(platform.rawValue) fourth page completed")
            let tenthEntered = expectation(description: "\(platform.rawValue) speculative pages refilled")
            let gate = MusicCollectionGate { firstEntered.fulfill() }
            var loaded: [Int] = [], completed: [Int] = [], accepted: [Int] = []
            var active = 0, peak = 0
            let read = Task {
                try await MusicSearchRoundReader.read(platform: platform, start: 1, pages: 10, concurrency: 4,
                    load: { request in
                        loaded.append(request.page)
                        active += 1; peak = max(peak, active)
                        defer { active -= 1 }
                        if request.page == 1 { await gate.wait() }
                        completed.append(request.page)
                        if request.page == 4 { fourthCompleted.fulfill() }
                        if request.page == 10 { tenthEntered.fulfill() }
                        return request.nextPage
                    }, accept: { request, next in
                        accepted.append(request.page)
                        return next
                    })
            }
            await fulfillment(of: [firstEntered, fourthCompleted, tenthEntered], timeout: 2)
            XCTAssertEqual(Set(loaded), Set(1...10))
            XCTAssertTrue(accepted.isEmpty)
            XCTAssertLessThanOrEqual(peak, 4)
            await gate.release()
            try await read.value
            XCTAssertEqual(loaded.count, 10)
            XCTAssertEqual(Set(loaded), Set(1...10))
            XCTAssertEqual(accepted, Array(1...10))
            XCTAssertNotEqual(completed.first, 1)
            XCTAssertLessThanOrEqual(peak, 4)
        }
    }
    func testPagesAndConcurrencyAboveTenAreExecuted() async throws {
        var loaded: [Int] = [], accepted: [Int] = []
        var active = 0, peak = 0
        try await MusicSearchRoundReader.read(platform: .qq, start: 1, pages: 12, concurrency: 12,
            load: { request in
                loaded.append(request.page)
                active += 1; peak = max(peak, active)
                defer { active -= 1 }
                return request.nextPage
            }, accept: { request, next in
                accepted.append(request.page)
                return next
            })
        XCTAssertEqual(Set(loaded), Set(1...12))
        XCTAssertEqual(accepted, Array(1...12))
        XCTAssertLessThanOrEqual(peak, 12)
    }
    func testSlidingWindowRefillsOnEachCompletionWithoutExceedingConcurrency() async throws {
        let firstEntered = expectation(description: "first page held")
        let secondEntered = expectation(description: "second page held")
        let thirdEntered = expectation(description: "third page refilled")
        let fourthEntered = expectation(description: "fourth page refilled")
        let first = MusicCollectionGate { firstEntered.fulfill() }
        let second = MusicCollectionGate { secondEntered.fulfill() }
        let third = MusicCollectionGate { thirdEntered.fulfill() }
        let fourth = MusicCollectionGate { fourthEntered.fulfill() }
        let gates = [first, second, third, fourth]
        var loaded: [Int] = [], accepted: [Int] = []
        var active = 0, peak = 0
        let read = Task {
            try await MusicSearchRoundReader.read(platform: .qq, start: 1, pages: 6, concurrency: 2,
                load: { request in
                    loaded.append(request.page)
                    active += 1; peak = max(peak, active)
                    defer { active -= 1 }
                    if request.page <= gates.count { await gates[request.page - 1].wait() }
                    return request.nextPage
                }, accept: { request, next in
                    accepted.append(request.page)
                    return next
                })
        }
        await fulfillment(of: [firstEntered, secondEntered], timeout: 2)
        XCTAssertEqual(Set(loaded), Set([1, 2]))
        XCTAssertEqual(loaded.count, 2)
        XCTAssertEqual(active, 2)
        await first.release()
        await fulfillment(of: [thirdEntered], timeout: 2)
        XCTAssertEqual(Set(loaded), Set([1, 2, 3]))
        XCTAssertEqual(loaded.count, 3)
        XCTAssertEqual(accepted, [1])
        XCTAssertEqual(active, 2)
        await second.release()
        await fulfillment(of: [fourthEntered], timeout: 2)
        XCTAssertEqual(Set(loaded), Set([1, 2, 3, 4]))
        XCTAssertEqual(loaded.count, 4)
        XCTAssertEqual(accepted, [1, 2])
        XCTAssertEqual(active, 2)
        for gate in [third, fourth] { await gate.release() }
        try await read.value
        XCTAssertEqual(accepted, Array(1...6))
        XCTAssertLessThanOrEqual(peak, 2)
    }
    func testCompletionRefillsNeverExceedSingleRoundStartBudget() async throws {
        let firstEntered = expectation(description: "first page held")
        let secondEntered = expectation(description: "second page held")
        let thirdEntered = expectation(description: "third page started")
        let fourthEntered = expectation(description: "fourth page started")
        let thirdAccepted = expectation(description: "third page accepted")
        let first = MusicCollectionGate { firstEntered.fulfill() }
        let second = MusicCollectionGate { secondEntered.fulfill() }
        let third = MusicCollectionGate { thirdEntered.fulfill() }
        let fourth = MusicCollectionGate { fourthEntered.fulfill() }
        var loaded: [Int] = [], accepted: [Int] = []
        let read = Task {
            try await MusicSearchRoundReader.read(platform: .qq, start: 1, pages: 4, concurrency: 2,
                load: { request in
                    loaded.append(request.page)
                    switch request.page {
                    case 1: await first.wait()
                    case 2: await second.wait()
                    case 3: await third.wait()
                    case 4: await fourth.wait()
                    default: throw MusicServiceError.rateLimited
                    }
                    return request.nextPage
                }, accept: { request, next in
                    accepted.append(request.page)
                    if request.page == 3 { thirdAccepted.fulfill() }
                    return next
                })
        }
        await fulfillment(of: [firstEntered, secondEntered], timeout: 2)
        await first.release()
        await fulfillment(of: [thirdEntered], timeout: 2)
        await second.release()
        await fulfillment(of: [fourthEntered], timeout: 2)
        await third.release()
        await fulfillment(of: [thirdAccepted], timeout: 2)
        await fourth.release()
        try await read.value
        XCTAssertEqual(Set(loaded), Set(1...4))
        XCTAssertEqual(loaded.count, 4)
        XCTAssertEqual(accepted, [1, 2, 3, 4])
    }
    func testIntegerOverflowStopsPageGenerationWithoutTrapping() async throws {
        var loaded: [Int] = [], accepted: [Int] = []
        try await MusicSearchRoundReader.read(platform: .qq, start: Int.max - 1, pages: 10, concurrency: 2,
            load: { request in
                loaded.append(request.page)
                return request.nextPage
            }, accept: { request, next in
                accepted.append(request.page)
                return next
            })
        XCTAssertEqual(Set(loaded), Set([Int.max - 1, Int.max]))
        XCTAssertEqual(loaded.count, 2)
        XCTAssertEqual(accepted, [Int.max - 1, Int.max])
    }
    func testLaterBatchFailureRetainsEveryAcceptedPrefixAndNeverSkipsFailedRange() async {
        let sixthEntered = expectation(description: "sixth page held")
        let seventhEntered = expectation(description: "seventh speculative page held")
        let seventhCancelled = expectation(description: "seventh page observed cancellation")
        let sixth = MusicCollectionGate { sixthEntered.fulfill() }
        let seventh = MusicCollectionGate { seventhEntered.fulfill() }
        var loaded: [Int] = [], accepted: [Int] = []
        var active = 0, peak = 0
        let read = Task {
            try await MusicSearchRoundReader.read(platform: .qq, start: 1, pages: 10, concurrency: 2,
                load: { request in
                    loaded.append(request.page)
                    active += 1; peak = max(peak, active)
                    defer { active -= 1 }
                    switch request.page {
                    case 6:
                        await sixth.wait()
                        throw MusicServiceError.rateLimited
                    case 7:
                        await withTaskCancellationHandler(operation: { await seventh.wait() },
                            onCancel: { seventhCancelled.fulfill() })
                        try Task.checkCancellation()
                    default:
                        break
                    }
                    return request.nextPage
                }, accept: { request, next in
                    accepted.append(request.page)
                    return next
                })
        }
        await fulfillment(of: [sixthEntered, seventhEntered], timeout: 2)
        XCTAssertEqual(Set(loaded), Set(1...7))
        XCTAssertEqual(loaded.count, 7)
        XCTAssertLessThanOrEqual(peak, 2)
        await sixth.release()
        await fulfillment(of: [seventhCancelled], timeout: 2)
        await seventh.release()
        do { try await read.value; XCTFail("Failed interval was skipped") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        XCTAssertEqual(accepted, [1, 2, 3, 4, 5])
        XCTAssertEqual(Set(loaded), Set(1...7))
        XCTAssertEqual(loaded.count, 7)
        XCTAssertTrue(loaded.contains(6))
        XCTAssertFalse(accepted.contains(6))
        XCTAssertLessThanOrEqual(peak, 2)
    }
    func testRealEndStopsFurtherSerialRequestsAndSpeculationIsNeverAccepted() async throws {
        for concurrency in [1, 4] {
            var loaded: [Int] = [], accepted: [Int] = []
            let acceptedFirst = expectation(description: "first page ended the round")
            let gate = MusicCollectionGate { }
            let read = Task {
                try await MusicSearchRoundReader.read(platform: .qq, start: 1,
                    pages: 10, concurrency: concurrency,
                    load: { request in
                        loaded.append(request.page)
                        if request.page > 1 { await gate.wait() }
                        return request.page
                    }, accept: { request, _ in
                        accepted.append(request.page)
                        if request.page == 1 { acceptedFirst.fulfill() }
                        return nil
                    })
            }
            await fulfillment(of: [acceptedFirst], timeout: 2)
            XCTAssertEqual(accepted, [1])
            await gate.release()
            try await read.value
            XCTAssertEqual(accepted, [1])
            XCTAssertEqual(Set(loaded), Set(1...concurrency))
        }
    }
}

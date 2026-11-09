#if os(iOS) || os(macOS)
import XCTest
import Combine
import MeowJavaScript
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

@MainActor
final class FilmSearchSessionTests: XCTestCase {
    func testCompletePagesBeyondTwelveAndOneHundredUseIndependentCursors() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: (0..<125).map { row("a\($0)", "影片\($0)") }, pageCount: 2)
        await reads.set("a", page: 2, list: [row("next", "下一页")], pageCount: 2)
        await reads.set("b", page: 1, list: [row("b", "独立来源")], pageCount: 1)
        let service = try makeService(reads, sites: [site("a", order: 0), site("b", order: 1)])
        let session = try service.session(for: "影片")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 126)
        XCTAssertEqual(session.snapshot.rawCards.count, 126)
        XCTAssertEqual(session.snapshot.metadataState, .unavailable)
        XCTAssertTrue(session.snapshot.hasMore)
        session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 127)
        XCTAssertFalse(session.snapshot.hasMore)
        let calls = await reads.calls
        XCTAssertEqual(calls.filter { $0.site == "a" }.map(\.page), [1, 2])
        XCTAssertEqual(calls.filter { $0.site == "b" }.map(\.page), [1])
        await service.close()
    }

    func testMissingMetadataStopsOnEmptyOrEntirelyRepeatedIdentityPage() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: [row("1", "斗破")])
        await reads.set("a", page: 2, list: [row("1", "斗破 重复")])
        await reads.set("b", page: 1, list: [row("2", "别名")])
        await reads.set("b", page: 2, list: [])
        let service = try makeService(reads, sites: [site("a"), site("b")])
        let session = try service.session(for: "斗破")
        await session.drain(); session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 2)
        XCTAssertFalse(session.snapshot.hasMore)
        session.loadMore(); await session.drain()
        let calls = await reads.calls
        XCTAssertEqual(calls.count, 4, "No hidden all-pages loop or repeated-page retry")
        await service.close()
    }

    func testSingleSourceFailureSettlesSilentlyAndOnlyExplicitMoreRetriesSamePage() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: [row("a", "成功")], pageCount: 1)
        await reads.set("b", page: 1, list: [row("b", "后来成功")], pageCount: 1)
        await reads.failNext("b", page: 1)
        let service = try makeService(reads, sites: [site("a"), site("b")])
        let session = try service.session(for: "成功")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .completed)
        XCTAssertFalse(session.snapshot.failed)
        XCTAssertEqual(session.snapshot.siteItems.count, 1)
        let before = await reads.calls
        XCTAssertEqual(before.count, 2)
        _ = try service.session(for: "成功")
        await session.drain()
        let reused = await reads.calls
        XCTAssertEqual(reused.count, 2, "Same query reuses the settled snapshot")
        session.loadMore(); await session.drain()
        let calls = await reads.calls
        XCTAssertEqual(calls.filter { $0.site == "b" }.map(\.page), [1, 1])
        XCTAssertEqual(session.snapshot.siteItems.count, 2)
        await service.close()
    }

    func testBothModesFreezeOldPositionsAndUpgradeOrdinaryCardWithAllLoadedMembers() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: [row("1", "远方"), row("2", "斗破苍穹")], pageCount: 2)
        await reads.set("a", page: 2, list: [row("3", "斗破"), row("4", "远方高清", remark: "更新至12集")], pageCount: 2)
        let service = try makeService(reads, sites: [site("a")], rules: ["高清"])
        let session = try service.session(for: "斗破")
        await session.drain()
        let grouped = session.snapshot.cards.map(\.displayKey)
        let raw = session.snapshot.rawCards.map(\.displayKey)
        let original = try XCTUnwrap(session.snapshot.cards.first { $0.title == "远方" })
        let context = session.context(for: original)
        session.loadMore(); await session.drain()
        XCTAssertEqual(Array(session.snapshot.cards.prefix(grouped.count)).map(\.displayKey), grouped)
        XCTAssertEqual(Array(session.snapshot.rawCards.prefix(raw.count)).map(\.displayKey), raw)
        let upgraded = try XCTUnwrap(session.snapshot.cards.first { $0.displayKey == original.displayKey })
        XCTAssertEqual(upgraded.label, "聚合")
        XCTAssertEqual(upgraded.members.count, 2, "Two members from one site still form an aggregate")
        XCTAssertEqual(context.sources.count, 2)
        XCTAssertEqual(context.snapshot.query, "斗破", "Original query is not reconstructed from the card title")
        XCTAssertEqual(context.snapshot.round, 2)
        let again = try service.session(for: "斗破")
        XCTAssertTrue(again === session)
        XCTAssertEqual(again.snapshot.cards, session.snapshot.cards)
        await service.close()
    }

    func testAllSourceFailuresDifferFromSuccessfulEmptyAndKeepExplicitRetryWithoutDiscardingEarlierData() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: [row("1", "真实结果")], pageCount: 2)
        await reads.set("a", page: 2, list: [], pageCount: 2)
        await reads.failNext("a", page: 1)
        let service = try makeService(reads, sites: [site("a")])
        let session = try service.session(for: "原词")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .failed)
        XCTAssertTrue(session.snapshot.failed, "A failed source channel is not a successful no-results response")
        XCTAssertTrue(session.snapshot.hasMore, "The failed page remains available for an explicit retry")
        _ = try service.session(for: "原词")
        await session.drain()
        let beforeRetry = await reads.calls
        XCTAssertEqual(beforeRetry.count, 1, "Returning must not automatically retry a failed page")
        session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .completed)
        XCTAssertFalse(session.snapshot.failed)
        XCTAssertEqual(session.snapshot.siteItems.count, 1)
        await reads.failNext("a", page: 2)
        session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .failed)
        XCTAssertFalse(session.snapshot.failed, "A later failed round does not hide already acquired results")
        XCTAssertEqual(session.snapshot.cards.count, 1)
        XCTAssertTrue(session.snapshot.hasMore)
        session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteState, .completed, "A successful empty page settles normally")
        XCTAssertFalse(session.snapshot.hasMore)
        let calls = await reads.calls
        XCTAssertEqual(calls.map(\.page), [1, 1, 2, 2])
        await service.close()

        let emptyReads = SearchCatReads()
        let emptyOwner = try makeService(emptyReads, sites: [site("a")])
        let empty = try emptyOwner.session(for: "真实空页")
        await empty.drain()
        XCTAssertEqual(empty.snapshot.siteState, .completed)
        XCTAssertFalse(empty.snapshot.failed)
        XCTAssertTrue(empty.snapshot.cards.isEmpty)
        XCTAssertFalse(empty.snapshot.hasMore)
        await emptyOwner.close()
    }

    func testCompleteIdentitySeparatesInstallationsAndPreservesRawFields() async throws {
        let reads = SearchCatReads()
        await reads.set("same", page: 1, list: [row("same-id", "斗破 第2季", remark: "更新至8集")], pageCount: 1)
        let first = site("same", installation: "first", generation: 3)
        let second = site("same", installation: "second", generation: 9)
        let service = try makeService(reads, sites: [first, second])
        let session = try service.session(for: "斗破")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 2)
        let card = try XCTUnwrap(session.snapshot.cards.first)
        let context = session.context(for: card)
        XCTAssertEqual(context.sources.count, 2)
        XCTAssertEqual(Set(context.sources.compactMap { $0.catField("installationId")?.catText }), ["first", "second"])
        XCTAssertEqual(Set(context.sources.compactMap { $0.catField("generation")?.catInteger }), [3, 9])
        XCTAssertTrue(context.sources.allSatisfy { $0.catField("siteId") == .string("same") })
        XCTAssertTrue(context.sources.allSatisfy { $0.catField("siteDetail") == .string("same-id") })
        XCTAssertTrue(context.sources.allSatisfy { $0.catField("rawTitle") == .string("斗破 第2季") })
        XCTAssertTrue(context.sources.allSatisfy { $0.catField("textBadge") == .string("更新至8集") })
        XCTAssertTrue(context.sources.allSatisfy { $0.catField("seasonHint") == .number(2) })
        await service.close()
    }

    func testTMDBFallbackDetailsAndMoreReuseOriginalPayloads() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        await reads.set("a", page: 1, list: [row("1", "斗破")], pageCount: 2)
        await reads.set("a", page: 2, list: [row("2", "斗破高清")], pageCount: 2)
        await metadata.setSearch("斗破2", results: [])
        await metadata.setSearch("斗破", results: [tmdbRow(7, "斗破")])
        let payload: RuntimeValue = .object(["id": .number(7), "name": .string("斗破"),
            "overview": .string("真实接口样例简介"), "number_of_seasons": .number(2), "number_of_episodes": .number(24),
            "poster_path": .string("/runtime/images/a.jpg"), "seasons": .array([])])
        await metadata.setDetail("tv:7", payload: payload)
        let service = try makeService(reads, sites: [site("a")], rules: ["高清"], metadata: metadata)
        let session = try service.session(for: "斗破2")
        await session.drain()
        let card = try XCTUnwrap(session.snapshot.cards.first { $0.sourceKind == "tmdb" })
        let context = session.context(for: card)
        XCTAssertEqual(context.metadataDetail, payload)
        XCTAssertEqual(context.detail.summary, "真实接口样例简介")
        XCTAssertEqual(context.detail.episodeCount, 24)
        XCTAssertNil(context.detail.playbackURL)
        session.loadMore(); await session.drain()
        let queries = await metadata.queries, details = await metadata.detailCalls
        XCTAssertEqual(queries, ["斗破2", "斗破"])
        XCTAssertEqual(details, ["tv:7"])
        XCTAssertEqual(context.card.tmdbID, 7)
        XCTAssertEqual(context.card.tmdbType, "tv")
        XCTAssertEqual(context.snapshot.metadataDetails["tv:7"], payload)
        XCTAssertEqual(context.snapshot.query, "斗破2")
        await service.close()
    }

    func testSearchFailureDoesNotFallbackAndDetailFailureKeepsSearchItem() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        await reads.set("a", page: 1, list: [row("1", "斗破")], pageCount: 1)
        let service = try makeService(reads, sites: [site("a")], metadata: metadata)
        let failed = try service.session(for: "失败2")
        await failed.drain()
        XCTAssertEqual(failed.snapshot.metadataState, .failed)
        XCTAssertEqual(failed.snapshot.siteItems.count, 1)
        let failedQueries = await metadata.queries
        XCTAssertEqual(failedQueries, ["失败2"])
        await metadata.setSearch("斗破", results: [tmdbRow(8, "斗破")])
        let next = try service.session(for: "斗破")
        await next.drain()
        XCTAssertEqual(next.snapshot.metadataState, .completed)
        XCTAssertEqual(next.snapshot.cards.filter { $0.sourceKind == "tmdb" }.count, 1)
        XCTAssertTrue(next.snapshot.metadataDetails.isEmpty)
        await metadata.setDetail("tv:8", payload: .object(["id": .number(8), "name": .string("斗破")]))
        await metadata.setSearch("另一个词", results: [tmdbRow(8, "斗破")])
        let retry = try service.session(for: "另一个词")
        await retry.drain()
        XCTAssertNotNil(retry.snapshot.metadataDetails["tv:8"], "A failed detail is removed from the shared cache")
        await metadata.setSearch("第三个词", results: [tmdbRow(8, "斗破")])
        let cached = try service.session(for: "第三个词")
        await cached.drain()
        let calls = await metadata.detailCalls
        XCTAssertEqual(calls.filter { $0 == "tv:8" }.count, 2, "Successful detail is reused across queries")
        await service.close()
    }

    func testLateOldQueryCannotPublishAndCloseDrainsAcceptedTransport() async throws {
        let reads = SearchCatReads()
        let entered = expectation(description: "old query entered"), release = SearchReadGate()
        await reads.block(query: "旧词", entered: entered, gate: release)
        await reads.set("a", page: 1, list: [row("1", "影片")], pageCount: 1)
        let service = try makeService(reads, sites: [site("a")])
        let old = try service.session(for: "旧词")
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertTrue(old.snapshot.loading)
        old.loadMore()
        let next = try service.session(for: "新词")
        await next.drain()
        XCTAssertEqual(next.snapshot.siteItems.count, 1)
        let closing = Task { await service.close() }
        await release.release()
        await closing.value
        XCTAssertTrue(old.snapshot.siteItems.isEmpty)
        XCTAssertEqual(old.snapshot.siteState, .cancelled)
        XCTAssertEqual(next.snapshot.query, "新词")
        let calls = await reads.calls
        XCTAssertEqual(calls.filter { $0.query == "旧词" }.map(\.page), [1], "More is refused before round settlement")
        XCTAssertThrowsError(try service.session(for: "closed"))
    }

    func testFailedDetailReusesSearchOverviewByExactTypeAndIDWithoutChangingRawSelection() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        await reads.set("a", page: 1, list: [row("1", "斗破")], pageCount: 1)
        let movie: RuntimeValue = .object(["id": .number(7), "media_type": .string("movie"),
            "title": .string("另一电影"), "overview": .string("不能串电影")])
        let other: RuntimeValue = .object(["id": .number(8), "media_type": .string("tv"),
            "name": .string("另一剧集"), "overview": .string("不能串另一ID")])
        let show: RuntimeValue = .object(["id": .number(7), "media_type": .string("tv"),
            "name": .string("斗破"), "overview": .string("搜索已经取得的真实简介"),
            "first_air_date": .string("2026-01-01")])
        await metadata.setSearch("原查询", results: [movie, other, show])
        let service = try makeService(reads, sites: [site("a")], metadata: metadata)
        let session = try service.session(for: "原查询")
        await session.drain()
        let card = try XCTUnwrap(session.snapshot.cards.first { $0.tmdbType == "tv" && $0.tmdbID == 7 })
        let context = session.context(for: card)
        XCTAssertTrue(session.snapshot.metadataDetails.isEmpty, "All detail reads deliberately fail in this fixture")
        XCTAssertEqual(context.metadataDetail, show)
        XCTAssertEqual(context.detail.summary, "搜索已经取得的真实简介")
        XCTAssertEqual(context.detail.releaseDate, "2026-01-01")
        XCTAssertNil(context.detail.episodeCount, "Search metadata must not invent absent detail fields")
        XCTAssertNil(context.detail.playbackURL)
        let raw = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first { $0.sourceKind == "site" }))
        XCTAssertEqual(raw.card.sourceKind, "site")
        XCTAssertEqual(raw.sources.first?.catField("siteDetail"), .string("1"))
        XCTAssertNil(raw.metadataDetail, "A raw source selection is not reidentified as TMDB")
        XCTAssertTrue(raw.detail.summary.isEmpty)
        XCTAssertEqual(raw.snapshot.metadataSearch?.catField("results"), .array([movie, other, show]))
        let queries = await metadata.queries, details = await metadata.detailCalls
        XCTAssertEqual(queries, ["原查询"])
        XCTAssertEqual(details.sorted(), ["movie:7", "tv:7", "tv:8"])
        await service.close()
    }

    func testCancelledPreparationAndNotYetStartedRoundCanResumeSameWord() async throws {
        let reads = SearchCatReads(), gate = SearchReadGate()
        await reads.set("a", page: 1, list: [row("1", "原词")], pageCount: 1)
        let entered = expectation(description: "preparation entered")
        let preparation = SearchPreparationReads(sites: [site("a")], entered: entered, gate: gate)
        let service = FilmSearchService(processor: try FilmSearchProcessor(),
            transport: .init(request: { _, params in try await reads.read(params) },
                             upload: { _, _, _ in throw FilmSearchError.unavailable }),
            sites: { await preparation.read() }, rules: { [] })
        let session = try service.session(for: "原词")
        await fulfillment(of: [entered], timeout: 3)
        session.cancel()
        await gate.release()
        await session.drain()
        XCTAssertTrue(session.snapshot.siteItems.isEmpty)
        let resumed = try service.session(for: "原词")
        XCTAssertTrue(resumed === session)
        await resumed.drain()
        XCTAssertEqual(resumed.snapshot.siteItems.count, 1)
        XCTAssertEqual(resumed.snapshot.round, 1)
        let count = await preparation.count
        XCTAssertEqual(count, 2)
        let calls = await reads.calls
        XCTAssertEqual(calls.count, 1, "Cancelled preparation must not invoke a source")
        await service.close()

        let immediate = try makeService(reads, sites: [site("a")])
        let notStarted = try immediate.session(for: "立即取消")
        notStarted.cancel()
        _ = try immediate.session(for: "立即取消")
        await notStarted.drain()
        XCTAssertEqual(notStarted.snapshot.siteItems.count, 1)
        XCTAssertEqual(notStarted.snapshot.round, 1)
        await immediate.close()

        await reads.set("a", page: 1, list: [row("1", "首轮")], pageCount: 2)
        await reads.set("a", page: 2, list: [row("2", "更多")], pageCount: 2)
        let moreOwner = try makeService(reads, sites: [site("a")])
        let more = try moreOwner.session(for: "更多立即取消")
        await more.drain()
        more.loadMore(); more.cancel()
        _ = try moreOwner.session(for: "更多立即取消")
        await more.drain()
        XCTAssertEqual(more.snapshot.round, 2)
        XCTAssertEqual(more.snapshot.siteItems.count, 2, "A cancelled-before-scheduling more round still owns its pending pages")
        await moreOwner.close()
    }

    func testCancelledInitialAndMoreRoundsResumeOnlyUnsettledReadsWithoutRefetchingMetadata() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        for key in ["a", "b"] {
            await reads.set(key, page: 1, list: [row("\(key)1", "首轮\(key)")], pageCount: 2)
            await reads.set(key, page: 2, list: [row("\(key)2", "次轮\(key)")], pageCount: 2)
        }
        await metadata.setSearch("原词", results: [tmdbRow(7, "元数据")])
        await metadata.setDetail("tv:7", payload: .object(["id": .number(7), "name": .string("元数据")]))
        let gate = SearchReadGate(), entered = expectation(description: "b initial entered")
        await reads.block(site: "b", page: 1, entered: entered, gate: gate)
        let service = try makeService(reads, sites: [site("a"), site("b")], metadata: metadata)
        let session = try service.session(for: "原词")
        await fulfillment(of: [entered], timeout: 3)
        await waitFor(session) { $0.siteItems.count == 1 && $0.metadataState == .completed }
        session.cancel()
        await gate.release(); await session.drain()
        _ = try service.session(for: "原词")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 2)
        XCTAssertEqual(session.snapshot.round, 1)
        let order = session.snapshot.cards.map(\.displayKey), rawOrder = session.snapshot.rawCards.map(\.displayKey)
        let moreGate = SearchReadGate(), moreEntered = expectation(description: "b more entered")
        await reads.block(site: "b", page: 2, entered: moreEntered, gate: moreGate)
        session.loadMore()
        await fulfillment(of: [moreEntered], timeout: 3)
        await waitFor(session) { $0.siteItems.count == 3 && $0.round == 2 }
        session.cancel()
        await moreGate.release(); await session.drain()
        _ = try service.session(for: "原词")
        await session.drain()
        XCTAssertEqual(session.snapshot.round, 2, "Resuming is not a new logical batch")
        XCTAssertEqual(session.snapshot.siteItems.count, 4)
        XCTAssertEqual(Array(session.snapshot.cards.prefix(order.count)).map(\.displayKey), order)
        XCTAssertEqual(Array(session.snapshot.rawCards.prefix(rawOrder.count)).map(\.displayKey), rawOrder)
        let calls = await reads.calls, queries = await metadata.queries, details = await metadata.detailCalls
        XCTAssertEqual(calls.filter { $0.site == "a" }.map(\.page), [1, 2], "Accepted pages must not be fetched again")
        XCTAssertEqual(calls.filter { $0.site == "b" }.map(\.page), [1, 1, 2, 2])
        XCTAssertEqual(queries, ["原词"])
        XCTAssertEqual(details, ["tv:7"])
        XCTAssertFalse(session.snapshot.hasMore)
        await service.close()
    }

    func testCloseCancelsHangingMetadataDetailBeforeWaitingForSessionDrain() async throws {
        let entered = expectation(description: "detail is suspended until cancellation")
        let cancelled = expectation(description: "detail received cancellation")
        let gate = SearchCancellationGate(entered: entered, cancelled: cancelled)
        let metadata = SearchCancellationMetadata(gate: gate)
        let service = try makeService(SearchCatReads(), sites: [], metadata: metadata)
        let session = try service.session(for: "斗破")
        await fulfillment(of: [entered], timeout: 3)
        let ended = expectation(description: "close completed")
        let closing = Task { await service.close(); ended.fulfill() }
        await fulfillment(of: [cancelled, ended], timeout: 3)
        // Cleanup prevents a failed close regression from hanging the test run;
        // it does not fulfill either of the cancellation/close expectations.
        await gate.releaseForCleanup()
        await closing.value
        let callsBefore = await metadata.searchCalls
        session.loadMore(); session.startIfNeeded()
        await session.drain()
        let callsAfter = await metadata.searchCalls
        XCTAssertEqual(callsAfter, callsBefore)
        XCTAssertEqual(session.snapshot.metadataState, .cancelled)
        XCTAssertThrowsError(try service.session(for: "斗破"))
    }

    func testCancelledSameIDDetailStartsFreshCachesCurrentAndCloseDrainsLateOld() async throws {
        let entered = expectation(description: "old tv:7 detail entered")
        let cancelled = expectation(description: "old detail observed cancellation without returning")
        let oldReturned = expectation(description: "old physical detail returned late-old")
        let prematureEngineClose = XCTestExpectation(description: "engine closed before old physical detail returned")
        prematureEngineClose.isInverted = true
        let currentPayload: RuntimeValue = .object(["id": .number(7), "name": .string("元数据"), "overview": .string("current")])
        let latePayload: RuntimeValue = .object(["id": .number(7), "name": .string("元数据"), "overview": .string("late-old")])
        let metadata = SearchRetiredDetailMetadata(item: tmdbRow(7, "元数据"), current: currentPayload, late: latePayload,
            entered: entered, cancelled: cancelled, returned: oldReturned)
        let engine = SearchDetailCloseObservedEngine(processor: try FilmSearchProcessor(), metadata: metadata,
            prematureClose: prematureEngineClose)
        let service = FilmSearchService(processor: FilmSearchProcessor(engine: engine),
            transport: .init(request: { _, _ in throw FilmSearchError.unavailable },
                             upload: { _, _, _ in throw FilmSearchError.unavailable }),
            sites: { [] }, rules: { [] }, metadata: metadata)
        addTeardownBlock {
            await metadata.releaseOld()
            await service.close()
        }

        let old = try service.session(for: "旧查询")
        await fulfillment(of: [entered], timeout: 3)
        service.release(old)
        await fulfillment(of: [cancelled], timeout: 3)
        let didCancel = await metadata.didCancel
        XCTAssertTrue(didCancel)
        guard didCancel else { return }
        let returnedBeforeNewQuery = await metadata.didReturnOld
        XCTAssertFalse(returnedBeforeNewQuery, "Cancellation must not physically settle the old reader")

        let current = try service.session(for: "新查询")
        await waitFor(current) { !$0.loading && $0.metadataState == .completed }
        XCTAssertEqual(current.snapshot.metadataDetails["tv:7"], currentPayload)
        guard current.snapshot.metadataDetails["tv:7"] == currentPayload else { return }
        let context = current.context(for: try XCTUnwrap(current.snapshot.cards.first { $0.tmdbType == "tv" && $0.tmdbID == 7 }))
        XCTAssertEqual(context.detail.summary, "current")

        let cached = try service.session(for: "缓存查询")
        await waitFor(cached) { !$0.loading && $0.metadataState == .completed }
        XCTAssertEqual(cached.snapshot.metadataDetails["tv:7"], currentPayload)
        guard cached.snapshot.metadataDetails["tv:7"] == currentPayload else { return }
        let calls = await metadata.detailCalls
        XCTAssertEqual(calls, ["tv:7", "tv:7"], "The cancelled task is not shared; the third query reuses the current success")
        let stillPending = await metadata.didReturnOld
        XCTAssertFalse(stillPending)

        let closeStarted = expectation(description: "service close entered")
        let closeEnded = expectation(description: "service close physically completed")
        let prematureClose = expectation(description: "service close completed before old detail returned")
        prematureClose.isInverted = true
        var closeCompleted = false, closeCompletedBeforeOldReturn = false
        let closing = Task {
            closeStarted.fulfill()
            await service.close()
            closeCompletedBeforeOldReturn = !(await metadata.didReturnOld)
            if closeCompletedBeforeOldReturn { prematureClose.fulfill() }
            closeCompleted = true
            closeEnded.fulfill()
        }
        await fulfillment(of: [closeStarted], timeout: 3)
        // This bounded inverted wait asserts pending work, not guessed completion.
        // The callbacks also retain ordering violations beyond this observation window.
        await fulfillment(of: [prematureClose, prematureEngineClose], timeout: 0.1)
        XCTAssertThrowsError(try service.session(for: "关闭后查询"))
        XCTAssertFalse(closeCompleted)
        let closesBeforeRelease = await engine.closes
        XCTAssertEqual(closesBeforeRelease, 0)
        let returnedBeforeRelease = await metadata.didReturnOld
        XCTAssertFalse(returnedBeforeRelease)

        await metadata.releaseOld()
        await fulfillment(of: [oldReturned, closeEnded], timeout: 3)
        guard closeCompleted else { return }
        await closing.value
        let closes = await engine.closes, closedBeforeOldReturn = await engine.closedBeforeOldReturn
        XCTAssertEqual(closes, 1)
        XCTAssertFalse(closedBeforeOldReturn)
        XCTAssertFalse(closeCompletedBeforeOldReturn)
        XCTAssertNil(old.snapshot.metadataDetails["tv:7"], "The old cancelled session cannot publish late-old")
        XCTAssertEqual(context.snapshot.query, "新查询")
        XCTAssertEqual(context.metadataDetail, currentPayload)
        XCTAssertEqual(context.detail.summary, "current")
        XCTAssertEqual(cached.snapshot.metadataDetails["tv:7"], currentPayload)
    }

    func testMetadataSettingsChangeRevokesSameWordAndCacheWithoutDestroyingOldDetailOrAutoSearching() async throws {
        let entered = expectation(description: "old detail entered"), cancelled = expectation(description: "configuration cancels old detail")
        let returned = expectation(description: "old detail physically returned")
        let current: RuntimeValue = .object(["id": .number(7), "name": .string("元数据"), "overview": .string("current")])
        let late: RuntimeValue = .object(["id": .number(7), "name": .string("元数据"), "overview": .string("late-old")])
        let metadata = SearchRetiredDetailMetadata(item: tmdbRow(7, "元数据"), current: current, late: late,
            entered: entered, cancelled: cancelled, returned: returned)
        let service = try makeService(SearchCatReads(), sites: [], metadata: metadata)
        addTeardownBlock { await metadata.releaseOld(); await service.close() }
        let old = try service.session(for: "同词")
        await fulfillment(of: [entered], timeout: 3)
        await waitFor(old) { !$0.cards.isEmpty }
        let oldContext = old.context(for: try XCTUnwrap(old.snapshot.cards.first))
        service.metadataSettingsChanged()
        await fulfillment(of: [cancelled], timeout: 3)
        let callsBeforeExplicitSearch = await metadata.detailCalls
        XCTAssertEqual(callsBeforeExplicitSearch, ["tv:7"])
        let new = try service.session(for: "同词", reusing: oldContext.session)
        XCTAssertFalse(new === old)
        await new.drain()
        let context = new.context(for: try XCTUnwrap(new.snapshot.cards.first))
        XCTAssertEqual(context.metadataDetail, current)
        service.metadataSettingsChanged()
        XCTAssertEqual(context.metadataDetail, current, "An already-open detail keeps its acquired snapshot")
        let third = try service.session(for: "同词", reusing: new)
        XCTAssertFalse(third === new)
        await third.drain()
        let calls = await metadata.detailCalls
        XCTAssertEqual(calls, ["tv:7", "tv:7", "tv:7"], "A successful configuration change discards the previous detail cache")
        await metadata.releaseOld()
        await fulfillment(of: [returned], timeout: 3)
        await old.drain()
        XCTAssertNil(old.snapshot.metadataDetails["tv:7"])
        XCTAssertEqual(context.metadataDetail, current)
        XCTAssertEqual(third.snapshot.metadataDetails["tv:7"], current)
        await service.close()
    }

    func testNewWordReleasesSettledSnapshotWhileActualNavigationCanRetainAndResumeItsOwnSession() async throws {
        let reads = SearchCatReads()
        await reads.set("a", page: 1, list: [row("1", "影片")], pageCount: 1)
        let service = try makeService(reads, sites: [site("a")])
        var original: FilmSearchSession? = try service.session(for: "原查询")
        weak var released = original
        await original?.drain()
        let selected = try XCTUnwrap(original?.snapshot.cards.first)
        var context: FilmSearchContext? = original?.context(for: selected)
        original = nil
        let next = try service.session(for: "新查询")
        await next.drain()
        XCTAssertNotNil(released, "The actual navigation context still owns its acquired snapshot")
        XCTAssertEqual(context?.snapshot.query, "原查询")
        XCTAssertEqual(context?.sources.count, 1)
        let resumed = try service.session(for: "原查询", reusing: context?.session)
        XCTAssertTrue(resumed === context?.session)
        await resumed.drain()
        let readsAfterResume = await reads.calls
        XCTAssertEqual(readsAfterResume.map(\.query), ["原查询", "新查询"], "Returning retained navigation does not recreate an already settled query")
        context = nil
        // End the local resumed reference before testing physical ownership.
        await service.close()

        let singleOwner = try makeService(reads, sites: [site("a")])
        var first: FilmSearchSession? = try singleOwner.session(for: "first")
        weak var old = first
        await first?.drain()
        first = nil
        let second = try singleOwner.session(for: "second")
        await second.drain()
        let freed = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { old == nil } }, evaluatedWith: nil)
        await fulfillment(of: [freed], timeout: 3)
        XCTAssertNil(old, "No historical keyword dictionary may retain settled cumulative pages")
        await singleOwner.close()
    }

    func testRetiredInFlightWorkReleasesAfterPhysicalSettlementAndClosedSearchKeepsNoSnapshot() async throws {
        let reads = SearchCatReads(), gate = SearchReadGate()
        let entered = expectation(description: "retired physical read entered")
        await reads.set("a", page: 1, list: [row("1", "影片")], pageCount: 1)
        await reads.block(query: "旧词", entered: entered, gate: gate)
        let service = try makeService(reads, sites: [site("a")])
        var old: FilmSearchSession? = try service.session(for: "旧词")
        weak var retired = old
        await fulfillment(of: [entered], timeout: 3)
        old = nil
        let current = try service.session(for: "新词")
        await current.drain()
        XCTAssertNotNil(retired, "The owner must keep physically pending work drainable")
        await gate.release()
        let settled = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { retired == nil } }, evaluatedWith: nil)
        await fulfillment(of: [settled], timeout: 3)
        XCTAssertNil(retired)
        await service.close()

        let pageOwner = try makeService(reads, sites: [site("a")])
        var pageSession: FilmSearchSession? = try pageOwner.session(for: "关闭页面")
        weak var pageReleased = pageSession
        await pageSession?.drain()
        pageOwner.release(try XCTUnwrap(pageSession))
        pageSession = nil
        let gone = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { pageReleased == nil } }, evaluatedWith: nil)
        await fulfillment(of: [gone], timeout: 3)
        XCTAssertNil(pageReleased, "Leaving the search content releases its current snapshot after draining")
        await pageOwner.close()
    }

    func testRealSourceOwnerGenerationAndDisableInvalidateInFlightSessionBeforeLateResponse() async throws {
        for change in ["generation", "catDisabled", "searchDisabled"] {
            let runtime = SourcesRuntimeFixture()
            await runtime.setCat(SourcesRuntimeFixture.cat("b", state: "disabled", enabled: false))
            await runtime.setReply("search", .object(["list": .array([row("old", "斗破")]), "pagecount": .number(1)]))
            let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: base) }
            let sourceStore = try DocumentStore(baseURL: base, namespace: FilmSourceService.documentNamespace)
            let ruleStore = try DocumentStore(baseURL: base, namespace: FilmRuleService.documentNamespace)
            let owner = FilmSourceService(transport: runtime.transport, storeFactory: { sourceStore })
            let rules = try FilmRuleService(store: ruleStore)
            let service = try FilmSearchService(sources: owner, rules: rules)
            await runtime.pause("invoke")
            let old = try service.session(for: "斗破")
            await runtime.waitUntilPaused()
            switch change {
            case "generation": await runtime.changeGeneration(); try await owner.refresh()
            case "catDisabled": _ = try await owner.enableCat("a", enabled: false)
            default: try await owner.saveVideoOptions(.init(installationId: "a", siteKey: "same"),
                options: .init(searchEnabled: false))
            }
            await waitFor(old) { $0.siteState == .cancelled && !$0.loading }
            await runtime.release()
            await old.drain()
            XCTAssertTrue(old.snapshot.cards.isEmpty, change)
            XCTAssertTrue(old.snapshot.siteItems.isEmpty, change)
            let next = try service.session(for: "斗破")
            XCTAssertFalse(next === old, "Invalid source identity must retire the cached session: \(change)")
            await next.drain()
            if change == "generation" {
                XCTAssertEqual(next.snapshot.siteItems.first?.catField("generation"), .number(2))
            } else {
                XCTAssertTrue(next.snapshot.noSources)
                XCTAssertTrue(next.snapshot.siteItems.isEmpty)
            }
            await service.close(); await rules.close(); await owner.close()
        }
    }

    func testNonemptyRawInvalidResultsNeverFallbackAndEmptyFallbackKeepsOriginalPayload() async throws {
        let metadata = SearchMetadataReads()
        let person: RuntimeValue = .object(["id": .number(1), "media_type": .string("person"), "name": .string("人物")])
        await metadata.setSearch("人物2", results: [person])
        await metadata.setSearch("无效2", results: [.object(["id": .number(0), "media_type": .string("tv")])])
        let original: RuntimeValue = .object(["results": .array([]), "marker": .string("original")])
        await metadata.setSearchPayload("空2", payload: original)
        await metadata.setSearchPayload("空", payload: .object(["results": .array([]), "marker": .string("fallback")]))
        let service = try makeService(SearchCatReads(), sites: [], metadata: metadata)
        for query in ["人物2", "无效2"] {
            let session = try service.session(for: query)
            await session.drain()
            XCTAssertEqual(session.snapshot.metadataState, .completed)
            XCTAssertTrue(session.snapshot.tmdbItems.isEmpty)
            XCTAssertEqual(session.snapshot.metadataQuery, query)
        }
        let empty = try service.session(for: "空2")
        await empty.drain()
        XCTAssertEqual(empty.snapshot.metadataSearch, original)
        XCTAssertEqual(empty.snapshot.metadataQuery, "空2")
        let queries = await metadata.queries
        XCTAssertEqual(queries, ["人物2", "无效2", "空2", "空"])
        await service.close()
    }

    func testFirstRoundLateHighScoreReordersAndMoreWaitsForMetadataSettlement() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        await reads.set("a", page: 1, list: [row("a", "远方")], pageCount: 2)
        await reads.set("b", page: 1, list: [row("b", "斗破")], pageCount: 2)
        await metadata.setSearch("斗破", results: [])
        let siteGate = SearchReadGate(), metadataGate = SearchReadGate()
        let siteEntered = expectation(description: "higher-score source pending")
        let metadataEntered = expectation(description: "metadata pending")
        await reads.block(site: "b", page: 1, entered: siteEntered, gate: siteGate)
        await metadata.blockSearch(entered: metadataEntered, gate: metadataGate)
        let service = try makeService(reads, sites: [site("a"), site("b")], metadata: metadata)
        let session = try service.session(for: "斗破")
        await fulfillment(of: [siteEntered, metadataEntered], timeout: 3)
        await waitFor(session) { $0.cards.count == 1 }
        XCTAssertEqual(session.snapshot.cards.first?.title, "远方")
        await siteGate.release()
        await waitFor(session) { $0.cards.count == 2 }
        XCTAssertEqual(session.snapshot.cards.first?.title, "斗破", "First-round callbacks do not freeze partial order")
        session.loadMore()
        let before = await reads.calls
        XCTAssertEqual(before.count, 2, "Even settled sites cannot page while TMDB is pending")
        XCTAssertTrue(session.snapshot.loading)
        await metadataGate.release(); await session.drain()
        XCTAssertEqual(session.snapshot.round, 1)
        XCTAssertTrue(session.snapshot.hasMore)
        await service.close()
    }

    func testInFlightGroupContextFollowsActualTMDBCarrierAndRawSelectionKeepsIdentity() async throws {
        let reads = SearchCatReads(), metadata = SearchMetadataReads()
        await reads.set("a", page: 1, list: [row("a", "斗破苍穹")], pageCount: 1)
        await reads.set("b", page: 1, list: [row("b", "斗破苍穹高清")], pageCount: 1)
        let movie: RuntimeValue = .object(["id": .number(9), "media_type": .string("movie"), "title": .string("斗破苍穹")])
        await metadata.setSearch("真实原查询", results: [movie, tmdbRow(7, "斗破苍穹")])
        let tvDetail: RuntimeValue = .object(["id": .number(7), "name": .string("斗破苍穹"), "overview": .string("真实剧集简介")])
        await metadata.setDetail("movie:9", payload: .object(["id": .number(9), "title": .string("斗破苍穹"), "overview": .string("另一电影简介")]))
        await metadata.setDetail("tv:7", payload: tvDetail)
        let siteGate = SearchReadGate(), metadataGate = SearchReadGate()
        let siteEntered = expectation(description: "second source pending"), metadataEntered = expectation(description: "TMDB pending")
        await reads.block(site: "b", page: 1, entered: siteEntered, gate: siteGate)
        await metadata.blockSearch(entered: metadataEntered, gate: metadataGate)
        let service = try makeService(reads, sites: [site("a"), site("b")], rules: ["高清"], metadata: metadata)
        let session = try service.session(for: "真实原查询")
        await fulfillment(of: [siteEntered, metadataEntered], timeout: 3)
        await waitFor(session) { $0.cards.count == 1 && $0.rawCards.count == 1 }
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let raw = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first))
        XCTAssertTrue(context.selectedCard.displayKey.hasPrefix("group:"))
        await siteGate.release(); await metadataGate.release(); await session.drain()
        XCTAssertEqual(context.selectedCard.sourceKind, "site", "The original selection is retained separately from its carrier")
        XCTAssertEqual(context.card.displayKey, "tmdb:tv:7", "Follow the actual shared-program membership, not the same-title movie")
        XCTAssertEqual(context.card.tmdbType, "tv")
        XCTAssertEqual(context.sources.count, 2)
        XCTAssertEqual(context.metadataDetail, tvDetail)
        XCTAssertEqual(context.snapshot.query, "真实原查询")
        XCTAssertTrue(context.session === session)
        XCTAssertEqual(raw.card.id, raw.selectedCard.id)
        XCTAssertEqual(raw.card.displayKey, raw.selectedCard.displayKey)
        XCTAssertEqual(raw.card.sourceKind, "site")
        XCTAssertEqual(raw.sources.count, 1)
        XCTAssertNil(raw.metadataDetail)
        _ = try service.session(for: "真实原查询")
        let calls = await reads.calls, queries = await metadata.queries
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(queries, ["真实原查询"])
        await service.close()
    }

    private func waitFor(_ session: FilmSearchSession, _ predicate: @escaping (FilmSearchSnapshot) -> Bool) async {
        let ready = expectation(description: "search snapshot reached expected state")
        var matched = false
        let observation = session.$snapshot.sink { snapshot in
            if !matched, predicate(snapshot) { matched = true; ready.fulfill() }
        }
        await fulfillment(of: [ready], timeout: 3)
        observation.cancel()
    }

    private func makeService(_ reads: SearchCatReads, sites: [FilmSearchSite], rules: [String] = [],
                             metadata: (any FilmSearchMetadataReading)? = nil) throws -> FilmSearchService {
        FilmSearchService(processor: try FilmSearchProcessor(), transport: CatScriptTransport(request: { operation, params in
            guard operation == "invoke" else { throw FilmSearchError.invalidResponse }
            return try await reads.read(params)
        }, upload: { _, _, _ in throw FilmSearchError.unavailable }), sites: { sites }, rules: { rules }, metadata: metadata)
    }
    private func site(_ key: String, installation: String = "cat", generation: Int64 = 1, order: Int = 0) -> FilmSearchSite {
        FilmSearchSite(cat: .init(installationId: installation, name: installation, sourceType: "local", generation: generation, state: "ready"),
            source: .init(key: .init(installationId: installation, siteKey: key), name: key, api: "fixture", actions: ["search"], methods: [:], order: order))
    }
    private func row(_ id: String, _ title: String, remark: String = "") -> RuntimeValue {
        .object(["vod_id": .string(id), "vod_name": .string(title), "vod_remarks": .string(remark)])
    }
    private func tmdbRow(_ id: Int, _ title: String) -> RuntimeValue {
        .object(["id": .number(Double(id)), "media_type": .string("tv"), "name": .string(title), "poster_path": .string("/p.jpg")])
    }
}

private actor SearchCatReads {
    struct Call { let site: String; let query: String; let page: Int }
    var calls: [Call] = []
    var pages: [String: RuntimeValue] = [:]
    var failures: Set<String> = []
    var blocked: (String, XCTestExpectation, SearchReadGate)?
    var blockedSite: (String, Int, XCTestExpectation, SearchReadGate)?
    func set(_ site: String, page: Int, list: [RuntimeValue], pageCount: Int? = nil) {
        var fields: RuntimeObject = ["list": .array(list)]
        if let pageCount { fields["pagecount"] = .number(Double(pageCount)) }
        pages["\(site):\(page)"] = .object(fields)
    }
    func failNext(_ site: String, page: Int) { failures.insert("\(site):\(page)") }
    func block(query: String, entered: XCTestExpectation, gate: SearchReadGate) { blocked = (query, entered, gate) }
    func block(site: String, page: Int, entered: XCTestExpectation, gate: SearchReadGate) { blockedSite = (site, page, entered, gate) }
    func read(_ parameters: RuntimeValue) async throws -> RuntimeValue {
        let site = parameters.catField("siteId")?.catText ?? ""
        let payload = parameters.catField("payload") ?? .null
        let query = payload.catField("wd")?.catText ?? "", page = payload.catField("page")?.catInteger ?? 0
        calls.append(Call(site: site, query: query, page: page))
        if let blocked, query == blocked.0 { blocked.1.fulfill(); await blocked.2.wait() }
        if let blockedSite, site == blockedSite.0, page == blockedSite.1 {
            self.blockedSite = nil
            blockedSite.2.fulfill(); await blockedSite.3.wait()
        }
        let key = "\(site):\(page)"
        if failures.remove(key) != nil { throw FilmSourceError(code: "TIMEOUT") }
        return pages[key] ?? .object(["list": .array([])])
    }
}

private actor SearchMetadataReads: FilmSearchMetadataReading {
    var queries: [String] = [], detailCalls: [String] = []
    private var results: [String: RuntimeValue] = [:], details: [String: RuntimeValue] = [:]
    private var searchGate: (XCTestExpectation, SearchReadGate)?
    func setSearch(_ query: String, results: [RuntimeValue]) { self.results[query] = .object(["results": .array(results)]) }
    func setSearchPayload(_ query: String, payload: RuntimeValue) { results[query] = payload }
    func blockSearch(entered: XCTestExpectation, gate: SearchReadGate) { searchGate = (entered, gate) }
    func setDetail(_ key: String, payload: RuntimeValue) { details[key] = payload }
    func search(query: String) async throws -> RuntimeValue {
        queries.append(query)
        if let searchGate { self.searchGate = nil; searchGate.0.fulfill(); await searchGate.1.wait() }
        guard let result = results[query] else { throw FilmSearchError.unavailable }
        return result
    }
    func detail(type: String, id: Int) throws -> RuntimeValue {
        let key = "\(type):\(id)"; detailCalls.append(key)
        guard let result = details[key] else { throw FilmSearchError.unavailable }
        return result
    }
    nonisolated func imageURL(path: String) -> URL? { URL(string: path, relativeTo: URL(string: "https://runtime.example.test")!)?.absoluteURL }
}

private actor SearchReadGate {
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    func wait() async {
        if released { return }
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

private actor SearchPreparationReads {
    let sites: [FilmSearchSite], entered: XCTestExpectation, gate: SearchReadGate
    var count = 0
    init(sites: [FilmSearchSite], entered: XCTestExpectation, gate: SearchReadGate) {
        self.sites = sites; self.entered = entered; self.gate = gate
    }
    func read() async -> [FilmSearchSite] {
        count += 1
        if count == 1 { entered.fulfill(); await gate.wait() }
        return sites
    }
}

private actor SearchCancellationGate {
    let entered: XCTestExpectation, cancelled: XCTestExpectation
    private var continuation: CheckedContinuation<RuntimeValue, Error>?
    private var didCancel = false
    init(entered: XCTestExpectation, cancelled: XCTestExpectation) { self.entered = entered; self.cancelled = cancelled }
    func wait() async throws -> RuntimeValue {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                if didCancel { continuation.resume(throwing: CancellationError()); return }
                self.continuation = continuation
                entered.fulfill()
            }
        } onCancel: { Task { await self.receiveCancellation() } }
    }
    private func receiveCancellation() {
        didCancel = true; cancelled.fulfill()
        continuation?.resume(throwing: CancellationError()); continuation = nil
    }
    func releaseForCleanup() { continuation?.resume(throwing: FilmSearchError.unavailable); continuation = nil }
}

private actor SearchCancellationMetadata: FilmSearchMetadataReading {
    let gate: SearchCancellationGate
    var searchCalls = 0
    init(gate: SearchCancellationGate) { self.gate = gate }
    func search(query: String) -> RuntimeValue {
        searchCalls += 1
        return .object(["results": .array([.object(["id": .number(7), "media_type": .string("tv"), "name": .string("斗破")])])])
    }
    func detail(type: String, id: Int) async throws -> RuntimeValue { try await gate.wait() }
    nonisolated func imageURL(path: String) -> URL? { nil }
}

private actor SearchRetiredDetailMetadata: FilmSearchMetadataReading {
    let item: RuntimeValue, current: RuntimeValue, late: RuntimeValue
    let entered: XCTestExpectation, cancelled: XCTestExpectation, returned: XCTestExpectation
    private var continuation: CheckedContinuation<RuntimeValue, Never>?
    private var released = false
    private(set) var didCancel = false, didReturnOld = false
    private(set) var detailCalls: [String] = []
    init(item: RuntimeValue, current: RuntimeValue, late: RuntimeValue,
         entered: XCTestExpectation, cancelled: XCTestExpectation, returned: XCTestExpectation) {
        self.item = item; self.current = current; self.late = late
        self.entered = entered; self.cancelled = cancelled; self.returned = returned
    }
    func search(query: String) -> RuntimeValue { .object(["results": .array([item])]) }
    func detail(type: String, id: Int) async -> RuntimeValue {
        detailCalls.append("\(type):\(id)")
        guard detailCalls.count == 1 else { return current }
        let value = await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                if released { continuation.resume(returning: late) }
                else { self.continuation = continuation }
                entered.fulfill()
            }
        } onCancel: { Task { await self.receiveCancellation() } }
        didReturnOld = true; returned.fulfill()
        return value
    }
    private func receiveCancellation() {
        guard !didCancel else { return }
        didCancel = true; cancelled.fulfill()
        // Intentionally keep the physical read pending after cancellation.
    }
    func releaseOld() {
        released = true
        continuation?.resume(returning: late); continuation = nil
    }
    nonisolated func imageURL(path: String) -> URL? { nil }
}

/// Forward to the real QuickJS search processor; observe only engine ownership.
private actor SearchDetailCloseObservedEngine: JavaScriptJSONEngine {
    let processor: FilmSearchProcessor, metadata: SearchRetiredDetailMetadata
    let prematureClose: XCTestExpectation
    private(set) var closes = 0
    private(set) var closedBeforeOldReturn = false
    init(processor: FilmSearchProcessor, metadata: SearchRetiredDetailMetadata, prematureClose: XCTestExpectation) {
        self.processor = processor; self.metadata = metadata; self.prematureClose = prematureClose
    }
    func invoke(_ request: Data) async throws -> Data {
        guard case .object(let fields) = try RuntimeJSON.decode(request) else { throw FilmSearchError.invalidResponse }
        let value = try await processor.invoke(Dictionary(uniqueKeysWithValues: fields.map { ($0.key, $0.value) }))
        return try RuntimeJSON.encode(value)
    }
    func close() async {
        closes += 1
        if !(await metadata.didReturnOld) {
            closedBeforeOldReturn = true
            prematureClose.fulfill()
        }
        await processor.close()
    }
}
#endif

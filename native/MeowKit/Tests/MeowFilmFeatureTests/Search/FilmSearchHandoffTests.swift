#if os(iOS) || os(macOS)
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchHandoffTests: XCTestCase {
    private typealias F = FilmSearchWireFixture
    func testNodeHandoffPreservesTypedIDsAllMembersConfirmedPagesAndActualCacheReceipt() async throws {
        let rows = [F.row(), F.row(.string("0"), key: "site:string-zero")]
        let target = FilmPlaybackTarget(id: "group:opaque", kind: .group, title: "Node准确标题", members: rows.map {
            .object(["sourceKey": .string(F.site.key), "detailId": $0.catField("detailId")!])
        })
        let seed: RuntimeValue = .object(["kind": .string("search"), "fetchedAt": .number(123),
            "expiresAt": .number(456), "revision": .string("opaque"), "completeness": .string("complete")])
        let results: [RuntimeValue] = [.object(["sourceKey": .string(F.site.key), "items": .array(rows.map { $0.catField("raw")! })])]
        let cursors: [RuntimeValue] = [.object(["sourceKey": .string(F.site.key), "page": .number(2), "completed": .bool(false)])]
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group(rows)], raw: rows),
            handoff: F.handoff(target: target, results: results, search: cursors, entries: [seed]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let request = try await context.playbackRequest(sources: [F.site.playbackSource], owner: nil)
        XCTAssertEqual(request.target, target)
        XCTAssertEqual(request.results, results); XCTAssertEqual(request.search, cursors); XCTAssertEqual(request.cacheEntries, [seed])
        XCTAssertEqual(request.query, "原查询"); XCTAssertNotEqual(request.query, context.selectedCard.title)
        XCTAssertEqual(request.target.value["members"]?.filmArray?.map { $0.filmObject?["detailId"] }, [.number(0), .string("0")])
        XCTAssertNil(request.value.filmObject?["rules"])
        XCTAssertEqual(request.searchReference, .object(["sessionId": .string(F.id.uuidString.lowercased()), "selectionId": .string(context.selectionID)]))
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.handoff"])
        XCTAssertEqual(calls[1].1.filmObject?["sources"], .array([.object(F.site.playbackSource.value)]))
        XCTAssertNil(calls[1].1.filmObject?["results"]); XCTAssertNil(calls[1].1.filmObject?["cacheEntries"])
        await service.close()
    }

    func testLateCarrierIsResolvedByNodeAndRawSelectionNeverGetsExpandedByTheClient() async throws {
        let raw = F.row(), group = F.group([F.row()])
        let rpc = FilmSearchRPCFixture(F.snapshot(loading: true, cards: [group], raw: [raw]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let initial = try await context.playbackRequest(sources: [F.site.playbackSource], owner: nil)
        XCTAssertEqual(initial.target.kind, .group)
        let movie = FilmPlaybackTarget(id: context.selectionID, kind: .movie, title: "Node可靠关联", metadataProvider: .tmdb, metadataID: "7")
        await rpc.setHandoff(F.handoff(target: movie, revision: 9, metadata: .object(["overview": .string("完整资料")])))
        try await rpc.emit(F.snapshot(9, cards: [], raw: [raw]))
        let update = try await context.playbackUpdate(sources: [F.site.playbackSource], owner: nil)
        XCTAssertEqual(update.revision, 9)
        XCTAssertEqual(update.fields["searchReference"], initial.searchReference)
        XCTAssertNil(update.fields["metadataTarget"]); XCTAssertNil(update.fields["cacheEntries"])
        XCTAssertEqual(context.metadataDetail?.catField("overview"), .string("完整资料"))
        let rawContext = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first))
        let single = FilmPlaybackTarget(id: rawContext.selectionID, kind: .source, title: "原始单源",
            source: ["key": .string(F.site.key), "detailId": .number(0)])
        await rpc.setHandoff(F.handoff(target: single, selection: rawContext.selectionID, revision: 9))
        let request = try await rawContext.playbackRequest(sources: [F.site.playbackSource], owner: nil)
        XCTAssertEqual(request.target, single); XCTAssertNil(request.metadata)
        await service.close()
    }

    func testReentryRetainsSelectedEpisodeZeroAndExactSourceCandidate() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])], raw: [F.row()]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let selected = try FilmPlaybackTarget(.object(["id": .string(context.selectionID), "kind": .string("tv"),
            "title": .string("用户当前选择"), "metadataProvider": .string("douban"), "metadataId": .string("8"),
            "season": .number(0), "episode": .number(0), "source": .object(["key": .string(F.site.key),
            "detailId": .number(0), "lineId": .number(0), "candidateId": .string("opaque-candidate")])]))
        let request = try await context.playbackRequest(sources: [F.site.playbackSource], owner: nil, target: selected)
        XCTAssertEqual(request.target, selected); XCTAssertNil(request.metadata)
        XCTAssertEqual(request.value.filmObject?["searchReference"], request.searchReference)
        XCTAssertEqual(request.value.filmObject?["resumeTarget"], .object(selected.value))
        let nonSearch = FilmPlaybackRequest(target: selected, sources: []).retargeted(selected)
        XCTAssertNil(nonSearch.value.filmObject?["resumeTarget"])
        await service.close()
    }

    func testMissingAndMalformedReferenceFailWithoutSeedOrEngineFallback() async throws {
        for missing in ["searchReference", "target", "results", "cacheEntries"] {
            var fields = try F.handoff().filmRequiredObject(); fields[missing] = nil
            let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])]), handoff: .object(fields))
            let service = filmSearchService(rpc), session = try service.session(for: "原查询")
            await session.drain()
            let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
            do { _ = try await context.playbackRequest(sources: [F.site.playbackSource], owner: nil); XCTFail(missing) }
            catch {}
            let calls = await rpc.calls
            XCTAssertEqual(calls.map(\.0), ["search.open", "search.handoff"])
            await service.close()
        }
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])]), handoff: F.handoff(id: UUID()))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        do { _ = try await context.playbackRequest(sources: [F.site.playbackSource], owner: nil); XCTFail("Wrong Node session") }
        catch { XCTAssertTrue(error is FilmSearchError) }
        await service.close()
    }

    func testReentryMetadataSeedRequiresTheSameProviderKindAndID() throws {
        let tv = FilmPlaybackTarget(id: "chosen", kind: .tv, title: "剧集", metadataProvider: .tmdb, metadataID: "7")
        let metadata: RuntimeValue = .object(["id": .number(7), "name": .string("剧集资料")])
        let reference: RuntimeValue = .object(["sessionId": .string(F.id.uuidString.lowercased()), "selectionId": .string("chosen")])
        let request = FilmPlaybackRequest(target: tv, sources: [], metadata: metadata, searchReference: reference)
        XCTAssertEqual(request.retargeted(tv).metadata, metadata)
        let movie = FilmPlaybackTarget(id: "chosen", kind: .movie, title: "同号电影", metadataProvider: .tmdb, metadataID: "7")
        let retargeted = request.retargeted(movie)
        XCTAssertNil(retargeted.metadata, "A TV ID cannot seed the same numeric movie ID")
        XCTAssertEqual(retargeted.value.filmObject?["resumeTarget"], .object(movie.value))
        XCTAssertEqual(retargeted.searchReference, reference)
    }
}
#endif

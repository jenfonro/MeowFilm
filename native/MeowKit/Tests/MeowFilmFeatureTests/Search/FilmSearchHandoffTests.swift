import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchHandoffTests: XCTestCase {
    func testTypedZeroOriginalRowsAndLastConfirmedPageReachOnlyTheActualOwner() async throws {
        let owner = try client(), replacement = try client()
        addTeardownBlock { await owner.close(); await replacement.close() }
        let site = site()
        let zero = row(.number(0)), stringZero = row(.string("0"))
        let seed: RuntimeValue = .object(["kind": .string("search"), "source": .object(site.playbackSource.value),
            "query": .string("原查询"), "page": .number(1), "value": .object(["list": .array([zero, stringZero])]),
            "completeness": .string("complete"), "fetchedAt": .number(123_456), "expiresAt": .number(234_567)])
        let reads = HandoffPages()
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, page in
            await reads.record(page)
            return .init(value: .object(["list": .array(page == 1 ? [zero, stringZero] : [zero]),
                                         "pagecount": .number(3)]),
                         producer: owner, cacheEntry: page == 1 ? seed : nil)
        }), sites: { [site] }, rules: { [] })
        addTeardownBlock { await service.close() }
        let session = try service.session(for: "原查询")
        await session.drain()
        XCTAssertEqual(session.snapshot.rawCards.count, 2)
        XCTAssertEqual(Set(session.snapshot.rawCards.map { Data($0.id.utf8) }).count, 2)
        XCTAssertEqual(session.snapshot.siteItems.map { $0.catField("raw") }, [zero, stringZero])
        session.loadMore(); await session.drain()
        XCTAssertTrue(session.snapshot.hasMore, "Explicit pagecount wins over an entirely repeated page")
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let base = FilmPlaybackRequest(target: try context.playbackTarget(), sources: [site.playbackSource])
        let request = try context.playbackRequest(base: base, owner: owner)
        XCTAssertEqual(request.target.kind, .group, "An unclassified group is not a guessed TV target")
        XCTAssertEqual(request.target.value["members"]?.catArray?.count, 2)
        XCTAssertEqual(request.query, "原查询")
        XCTAssertEqual(request.results.first?.catField("items"), .array([zero, stringZero]))
        XCTAssertEqual(request.cacheEntries, [seed])
        XCTAssertEqual(request.search, [.object(["sourceKey": .string(site.key), "page": .number(2),
                                                "completed": .bool(false)])])
        for other in [replacement, nil] as [RuntimeHTTPClient?] {
            let hints = try context.playbackRequest(base: base, owner: other)
            XCTAssertTrue(hints.results.isEmpty); XCTAssertTrue(hints.search.isEmpty)
            XCTAssertTrue(hints.cacheEntries.isEmpty)
            XCTAssertEqual(hints.resultHints.first?.catField("items"), .array([zero, stringZero]))
        }
        let raw = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first { $0.detailID == .number(0) }))
        let rawRequest = try raw.playbackRequest(base: base, owner: owner)
        XCTAssertEqual(rawRequest.target.kind, .source)
        XCTAssertEqual(rawRequest.target.value["source"]?.catField("detailId"), .number(0))
        XCTAssertEqual(rawRequest.results.first?.catField("items"), .array([zero]))
        XCTAssertNil(rawRequest.target.metadataProvider)
        let calls = await reads.pages
        XCTAssertEqual(calls, [1, 2], "Building the handoff must not read another page or any detail")
        await owner.close()
        let revoked = try context.playbackRequest(base: base, owner: owner)
        XCTAssertTrue(revoked.results.isEmpty); XCTAssertTrue(revoked.search.isEmpty)
        XCTAssertTrue(revoked.cacheEntries.isEmpty)
        XCTAssertEqual(revoked.resultHints.first?.catField("items"), .array([zero, stringZero]))
    }

    func testNoProducerEvidenceIsNotStampedAtFirstConsumptionAndScopeCannotExpand() async throws {
        let consumer = try client()
        addTeardownBlock { await consumer.close() }
        let site = site(), extra = self.site("other")
        let original: RuntimeValue = .object(["vod_id": .null, "id": .number(0),
            "vod_name": .string("真实标题"), "vod_remarks": .string("原备注"),
            "unknown": .object(["full": .bool(true)])])
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, _ in
            .init(value: .object(["list": .array([original]), "pagecount": .number(1)]))
        }), sites: { [site] }, rules: { [] })
        addTeardownBlock { await service.close() }
        let session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        XCTAssertEqual(context.sources.first?.catField("detailId"), .number(0))
        let base = FilmPlaybackRequest(target: try context.playbackTarget(), sources: [extra.playbackSource, site.playbackSource])
        for _ in 0..<2 {
            let request = try context.playbackRequest(base: base, owner: consumer)
            XCTAssertEqual(request.sources.map(\.key), [site.key])
            XCTAssertTrue(request.results.isEmpty); XCTAssertTrue(request.search.isEmpty)
            XCTAssertTrue(request.cacheEntries.isEmpty)
            XCTAssertEqual(request.resultHints.first?.catField("items"), .array([original]))
        }
        let raw = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first))
        XCTAssertThrowsError(try raw.playbackRequest(base: .init(target: base.target, sources: [extra.playbackSource]),
                                                    owner: consumer))
    }

    func testNewQueryReadsMetadataAgainWithoutNativeSuccessOrFlightCache() async throws {
        let metadata = HandoffMetadata()
        let service = FilmSearchService(processor: try FilmSearchProcessor(),
            data: .init(read: { _, _, _ in throw FilmSearchError.unavailable }),
            sites: { [] }, rules: { [] }, metadata: metadata)
        addTeardownBlock { await service.close() }
        let first = try service.session(for: "第一词")
        await first.drain()
        let context = first.context(for: try XCTUnwrap(first.snapshot.cards.first))
        let target = try context.playbackTarget()
        XCTAssertEqual(target.kind, .movie)
        XCTAssertEqual(context.detail.item.id, "7")
        let request = try context.playbackRequest(base: .init(target: target, sources: []), owner: nil)
        let payload = await metadata.payload
        XCTAssertEqual(request.metadata, payload)
        XCTAssertTrue(request.cacheEntries.isEmpty, "Presentation is not a successful cache-owner export")
        let second = try service.session(for: "第二词")
        await second.drain()
        let calls = await metadata.details
        XCTAssertEqual(calls, 2)
        XCTAssertEqual(context.metadataDetail?.catField("unknown"), .object(["full": .bool(true)]))
    }

    func testLoadedRawDetailsKeepTheSharedTypedIdentityInTheRealLibrary() async throws {
        let name = "film-search-identity-" + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let source = site()
        let rows = [row(.number(0)), row(.string("0"))]
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { _, _, _ in
            .init(value: .object(["list": .array(rows), "pagecount": .number(1)]))
        }), sites: { [source] }, rules: { [] })
        addTeardownBlock { await service.close() }
        let session = try service.session(for: "真实标题")
        await session.drain()
        let details = try session.snapshot.rawCards.map { card -> FilmDetail in
            let context = session.context(for: card)
            let target = try context.playbackTarget()
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["vod_name": .string("完整标题")]), target: .object(target.value)))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: context.detail)
            XCTAssertEqual(detail.item.id, card.id)
            XCTAssertEqual(detail.item.sourceID, source.key)
            return detail
        }
        XCTAssertEqual(details.count, 2)
        XCTAssertNotEqual(details[0].item.identity, details[1].item.identity)
        let library = FilmLibrary(defaults: defaults)
        for detail in details { XCTAssertTrue(try library.toggle(detail.item)) }
        XCTAssertEqual(library.favorites.count, 2)
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertTrue(restored.contains(details[0].item)); XCTAssertTrue(restored.contains(details[1].item))
        XCTAssertFalse(try library.toggle(details[0].item))
        XCTAssertTrue(library.contains(details[1].item))
    }

    func testCurrentStableSourceScopeRebindsGenerationWithoutRewritingTheOriginalProducer() async throws {
        let owner = try client(), replacement = try client()
        addTeardownBlock { await owner.close(); await replacement.close() }
        let a = site("A", generation: 1), b = site("B", generation: 1), unrelated = site("C", generation: 2)
        let original = row(.number(0))
        let seed: RuntimeValue = .object(["kind": .string("search"), "source": .object(a.playbackSource.value),
            "query": .string("原查询"), "page": .number(1), "value": .object(["list": .array([original])]),
            "completeness": .string("complete"), "fetchedAt": .number(123_456)])
        let service = FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { source, _, _ in
            .init(value: .object(["list": .array([original]), "pagecount": .number(1)]),
                  producer: owner, cacheEntry: source.source.key.siteKey == "A" ? seed : nil)
        }), sites: { [a, b] }, rules: { [] })
        addTeardownBlock { await service.close() }
        let session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let target = try context.playbackTarget()
        let currentA = FilmPlaybackSource(key: "current-A", engine: "cat", installationID: "fixture", siteID: "A",
                                          generation: 2, name: "改名后的A")
        let current = FilmPlaybackRequest(target: target, sources: [unrelated.playbackSource, currentA, b.playbackSource])
        for consumer in [owner, replacement] {
            let request = try context.playbackRequest(base: current, owner: consumer)
            XCTAssertEqual(request.sources.map(\.key), [a.key, b.key], "New C is not part of the old query admission")
            XCTAssertEqual(request.sources[0].value["generation"], .number(2))
            XCTAssertEqual(request.sources[0].name, "改名后的A")
            XCTAssertEqual(request.target.kind, .group)
            XCTAssertEqual(request.target.value["members"]?.filmArray?.count, 2)
            XCTAssertEqual(request.resultHints.first?.filmObject?["sourceKey"], .string(a.key))
            XCTAssertEqual(request.resultHints.first?.filmObject?["items"], .array([original]))
            XCTAssertTrue(request.cacheEntries.isEmpty, "A new generation must not import an old source seed")
            XCTAssertFalse(request.results.contains { $0.filmObject?["sourceKey"] == .string(a.key) })
            XCTAssertFalse(request.search.contains { $0.filmObject?["sourceKey"] == .string(a.key) })
            XCTAssertEqual(session.pages.first { $0.site.key == a.key }?.read.cacheEntry, seed)
            XCTAssertTrue(session.pages.allSatisfy { $0.read.producer === owner })
        }
        let raw = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first { $0.text("siteKey") == a.key }))
        let reboundRaw = try raw.playbackRequest(base: current, owner: owner)
        XCTAssertEqual(reboundRaw.sources.map(\.key), [a.key])
        XCTAssertEqual(reboundRaw.target.kind, .source)
        XCTAssertEqual(reboundRaw.target.value["source"]?.filmObject?["detailId"], .number(0))
        XCTAssertTrue(reboundRaw.results.isEmpty); XCTAssertTrue(reboundRaw.search.isEmpty)
        XCTAssertEqual(reboundRaw.sources.first?.value["generation"], .number(2))

        let renamedA = FilmPlaybackSource(key: "different-key-is-not-proof", engine: "cat",
            installationID: "fixture", siteID: "A", generation: 1, name: "只改名的A")
        let renamed = try raw.playbackRequest(base: .init(target: target, sources: [renamedA]), owner: owner)
        XCTAssertEqual(renamed.sources.first?.key, a.key)
        XCTAssertEqual(renamed.sources.first?.name, "只改名的A")
        XCTAssertEqual(renamed.results.first?.filmObject?["items"], .array([original]))
        XCTAssertEqual(renamed.cacheEntries, [seed])
        XCTAssertEqual(renamed.search.first?.filmObject?["page"], .number(1))

        let remaining = try context.playbackRequest(base: .init(target: target, sources: [b.playbackSource]), owner: owner)
        XCTAssertEqual(remaining.target.kind, .group, "Losing A does not turn the remaining group into raw")
        XCTAssertEqual(remaining.target.value["members"], .array([
            .object(["sourceKey": .string(b.key), "detailId": .number(0)]),
        ]))
        XCTAssertEqual(remaining.sources.map(\.key), [b.key])
        var selectedGroup = target.value
        selectedGroup["source"] = .object(["key": .string(a.key), "detailId": .number(0),
            "candidateId": .string("old-A-candidate")])
        let revokedSelection = try context.playbackRequest(base: .init(target: target, sources: [b.playbackSource]),
            owner: owner, target: FilmPlaybackTarget(.object(selectedGroup)))
        XCTAssertEqual(revokedSelection.target.kind, .group)
        XCTAssertNil(revokedSelection.target.value["source"])
        XCTAssertEqual(revokedSelection.target.value["members"], remaining.target.value["members"])
        XCTAssertThrowsError(try raw.playbackRequest(base: .init(target: target, sources: [b.playbackSource]), owner: owner)) {
            XCTAssertEqual(($0 as? FilmPlaybackError)?.code, "SOURCE_NOT_FOUND")
        }
        XCTAssertThrowsError(try context.playbackRequest(base: .init(target: target, sources: []), owner: owner)) {
            XCTAssertEqual(($0 as? FilmPlaybackError)?.code, "SOURCE_NOT_FOUND")
        }
        let disabled = FilmPlaybackSource(engine: "cat", installationID: "fixture", siteID: "A",
                                          generation: 2, name: "A", enabled: false)
        XCTAssertThrowsError(try raw.playbackRequest(base: .init(target: target, sources: [disabled]), owner: owner))
        let notSearchable = FilmPlaybackSource(engine: "cat", installationID: "fixture", siteID: "A",
                                               generation: 2, name: "A", searchable: false)
        XCTAssertThrowsError(try raw.playbackRequest(base: .init(target: target, sources: [notSearchable]), owner: owner))
    }

    private func site(_ id: String = "site", generation: Int64 = 4) -> FilmSearchSite {
        .init(cat: .init(installationId: "fixture", name: "fixture", sourceType: "local", generation: generation),
              source: .init(key: .init(installationId: "fixture", siteKey: id), name: id,
                            api: "fixture", actions: ["search"], methods: [:]))
    }
    private func row(_ id: RuntimeValue) -> RuntimeValue {
        .object(["vod_id": id, "vod_name": .string("真实标题"), "vod_remarks": .string("原备注"),
                 "original": .object(["unknown": .array([.number(0), .null, .string("保留")])])])
    }
    private func client() throws -> RuntimeHTTPClient {
        try .init(baseURL: URL(string: "http://127.0.0.1:43211")!)
    }
}

private actor HandoffPages {
    var pages: [Int] = []
    func record(_ page: Int) { pages.append(page) }
}

private actor HandoffMetadata: FilmSearchMetadataReading {
    var details = 0
    let payload: RuntimeValue = .object(["id": .number(7), "title": .string("真实电影"),
        "overview": .string("完整简介"), "unknown": .object(["full": .bool(true)]),
        "credits": .object(["cast": .array([])]), "similar": .object(["results": .array([])]),
        "images": .object(["logos": .array([])])])
    func search(query: String) -> RuntimeValue {
        .object(["results": .array([.object(["id": .number(7), "media_type": .string("movie"),
                                            "title": .string("真实电影")])])])
    }
    func detail(type: String, id: Int) -> RuntimeValue { details += 1; return payload }
    nonisolated func imageURL(path: String) -> URL? { nil }
}

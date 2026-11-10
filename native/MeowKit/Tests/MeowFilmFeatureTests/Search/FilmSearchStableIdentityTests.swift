#if os(iOS) || os(macOS)
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchStableIdentityTests: XCTestCase {
    func testRawTargetsStayStableAcrossGenerationsAndSessionsWithoutMergingSourcesOrTypedIDs() async throws {
        let identities = [("install", "site"), ("other-install", "site"), ("install", "other-site"),
                          ("a:b", "c"), ("a", "b:c")]
        let oldSites = identities.map { site(installation: $0.0, id: $0.1, generation: 7) }
        let newSites = identities.map { site(installation: $0.0, id: $0.1, generation: 19) }
        let oldOwner = try service(sites: oldSites), newOwner = try service(sites: newSites)
        addTeardownBlock { await oldOwner.close(); await newOwner.close() }
        let old = try oldOwner.session(for: "未关联样本"), current = try newOwner.session(for: "未关联样本")
        await old.drain(); await current.drain()
        XCTAssertNotEqual(old.snapshot.sessionID, current.snapshot.sessionID)
        XCTAssertEqual(old.snapshot.rawCards.count, identities.count * 2)
        XCTAssertEqual(current.snapshot.rawCards.count, identities.count * 2)

        var oldIDs: [String] = [], currentIDs: [String] = []
        for (before, after) in zip(oldSites, newSites) {
            XCTAssertEqual(before.key, after.key)
            for detailID in [RuntimeValue.number(0), .string("0")] {
                let oldContext = try rawContext(old, site: before, detailID: detailID)
                let currentContext = try rawContext(current, site: after, detailID: detailID)
                let oldTarget = try oldContext.playbackTarget(), currentTarget = try currentContext.playbackTarget()
                XCTAssertEqual(oldTarget.kind, .source)
                XCTAssertEqual(currentTarget.kind, .source)
                XCTAssertEqual(oldTarget.id, currentTarget.id)
                XCTAssertEqual(oldTarget.value["source"]?.catField("detailId"), detailID)
                XCTAssertEqual(currentTarget.value["source"]?.catField("detailId"), detailID)
                XCTAssertEqual(currentTarget.value["source"]?.catField("key"), .string(after.key))
                XCTAssertEqual(oldContext.sources.first?.catField("generation"), .number(7))
                XCTAssertEqual(currentContext.sources.first?.catField("generation"), .number(19))
                oldIDs.append(oldTarget.id); currentIDs.append(currentTarget.id)
            }
        }
        XCTAssertEqual(Set(oldIDs).count, identities.count * 2)
        XCTAssertEqual(Set(currentIDs).count, identities.count * 2)
        XCTAssertEqual(oldIDs, currentIDs)
    }

    func testStableKeyDoesNotTransferOldGenerationFactsOrCacheScopeToTheCurrentHandoff() async throws {
        let oldClient = try client(), currentClient = try client()
        let before = site(installation: "install", id: "site", generation: 7)
        let after = site(installation: "install", id: "site", generation: 19)
        let oldOwner = try service(sites: [before], producer: oldClient)
        let currentOwner = try service(sites: [after], producer: currentClient)
        addTeardownBlock {
            await oldOwner.close(); await currentOwner.close()
            await oldClient.close(); await currentClient.close()
        }
        let old = try oldOwner.session(for: "未关联样本"), current = try currentOwner.session(for: "未关联样本")
        await old.drain(); await current.drain()
        XCTAssertNotEqual(old.snapshot.sessionID, current.snapshot.sessionID)
        let oldContext = try rawContext(old, site: before, detailID: .number(0))
        let currentContext = try rawContext(current, site: after, detailID: .number(0))
        let target = try currentContext.playbackTarget()
        XCTAssertEqual(try oldContext.playbackTarget().id, target.id)
        let base = FilmPlaybackRequest(target: target, sources: [after.playbackSource])
        let request = try currentContext.playbackRequest(base: base, owner: currentClient)
        XCTAssertEqual(request.sources.first?.value["generation"], .number(19))
        XCTAssertEqual(request.sources.first?.key, after.key)
        XCTAssertEqual(request.results, [.object(["sourceKey": .string(after.key),
                                                  "items": .array([Self.row(.number(0))])])])
        XCTAssertEqual(request.search, [.object(["sourceKey": .string(after.key), "page": .number(1),
                                                 "completed": .bool(true)])])
        XCTAssertTrue(request.resultHints.isEmpty)
        XCTAssertEqual(request.cacheEntries, [Self.seed(after, query: "未关联样本", page: 1)])
        XCTAssertEqual(request.cacheEntries.first?.catField("source")?.catField("generation"), .number(19))
        XCTAssertEqual(request.cacheEntries.first?.catField("source")?.catField("key"), .string(after.key))
        XCTAssertEqual(request.cacheEntries.first?.catField("fetchedAt"), .number(123_456))
        XCTAssertEqual(request.cacheEntries.first?.catField("expiresAt"), .number(234_567))

        for consumer in [oldClient, currentClient] {
            let hints = try oldContext.playbackRequest(base: base, owner: consumer)
            XCTAssertEqual(hints.sources.first?.key, after.key)
            XCTAssertEqual(hints.sources.first?.value["generation"], .number(19))
            XCTAssertEqual(hints.target.id, target.id)
            XCTAssertTrue(hints.results.isEmpty)
            XCTAssertTrue(hints.search.isEmpty)
            XCTAssertTrue(hints.cacheEntries.isEmpty, "The stable key does not authorize a prior generation's seed")
            XCTAssertEqual(hints.resultHints, [.object(["sourceKey": .string(after.key),
                                                        "items": .array([Self.row(.number(0))])])])
        }
        XCTAssertEqual(old.pages.first?.read.cacheEntry?.catField("source")?.catField("generation"), .number(7))
        XCTAssertEqual(old.pages.first?.read.cacheEntry?.catField("fetchedAt"), .number(123_456))
        XCTAssertEqual(oldContext.sources.first?.catField("generation"), .number(7))
    }

    func testGroupAndMetadataTargetsKeepTheirSharedIdentitiesAcrossGenerations() async throws {
        let before = site(installation: "install", id: "site", generation: 7)
        let after = site(installation: "install", id: "site", generation: 19)
        let oldOwner = try service(sites: [before], metadata: StableIdentityMetadata())
        let currentOwner = try service(sites: [after], metadata: StableIdentityMetadata())
        addTeardownBlock { await oldOwner.close(); await currentOwner.close() }
        let old = try oldOwner.session(for: "未关联样本"), current = try currentOwner.session(for: "未关联样本")
        await old.drain(); await current.drain()
        XCTAssertNotEqual(old.snapshot.sessionID, current.snapshot.sessionID)
        let oldGroup = try XCTUnwrap(old.snapshot.cards.first { $0.displayKey.hasPrefix("group:") })
        let currentGroup = try XCTUnwrap(current.snapshot.cards.first { $0.displayKey.hasPrefix("group:") })
        let groupBefore = try old.context(for: oldGroup).playbackTarget()
        let groupAfter = try current.context(for: currentGroup).playbackTarget()
        XCTAssertEqual(oldGroup.groupKey, currentGroup.groupKey)
        XCTAssertEqual(groupBefore.kind, .group)
        XCTAssertEqual(groupAfter.kind, .group)
        XCTAssertEqual(groupBefore.id, groupAfter.id)
        XCTAssertEqual(groupBefore.id, oldGroup.displayKey)
        XCTAssertEqual(groupBefore.value["members"]?.catArray?.count, 2)
        XCTAssertEqual(groupBefore.value["members"], groupAfter.value["members"])
        XCTAssertNil(groupAfter.metadataProvider)

        var metadataIDs: [String] = []
        for kind in [FilmPlaybackKind.movie, .tv] {
            let oldCard = try XCTUnwrap(old.snapshot.cards.first {
                $0.sourceKind == "tmdb" && $0.tmdbID == 7 && $0.tmdbType == kind.rawValue
            })
            let currentCard = try XCTUnwrap(current.snapshot.cards.first {
                $0.sourceKind == "tmdb" && $0.tmdbID == 7 && $0.tmdbType == kind.rawValue
            })
            let first = try old.context(for: oldCard).playbackTarget()
            let second = try current.context(for: currentCard).playbackTarget()
            XCTAssertEqual(first.id, second.id)
            XCTAssertEqual(second.id, "tmdb:\(kind.rawValue):7")
            XCTAssertEqual(first.kind, kind)
            XCTAssertEqual(second.kind, kind)
            XCTAssertEqual(first.metadataProvider, .tmdb)
            XCTAssertEqual(second.metadataProvider, .tmdb)
            XCTAssertEqual(first.value["metadataId"], .string("7"))
            XCTAssertEqual(second.value["metadataId"], .string("7"))
            metadataIDs.append(second.id)
        }
        XCTAssertEqual(Set(metadataIDs).count, 2)
        XCTAssertFalse(metadataIDs.contains(groupAfter.id))
    }

    private func site(installation: String, id: String, generation: Int64) -> FilmSearchSite {
        .init(cat: .init(installationId: installation, name: "猫源 \(generation)", sourceType: "local",
                         generation: generation, state: "ready"),
              source: .init(key: .init(installationId: installation, siteKey: id), name: "站点 \(generation)",
                            api: "fixture", actions: ["search"], methods: [:]))
    }

    private func service(sites: [FilmSearchSite], producer: RuntimeHTTPClient? = nil,
                         metadata: (any FilmSearchMetadataReading)? = nil) throws -> FilmSearchService {
        FilmSearchService(processor: try FilmSearchProcessor(), data: .init(read: { site, query, page in
            .init(value: Self.page, producer: producer,
                  cacheEntry: producer == nil ? nil : Self.seed(site, query: query, page: page))
        }), sites: { sites }, rules: { [] }, metadata: metadata)
    }

    private func rawContext(_ session: FilmSearchSession, site: FilmSearchSite,
                            detailID: RuntimeValue) throws -> FilmSearchContext {
        let card = try XCTUnwrap(session.snapshot.rawCards.first {
            $0.text("siteKey") == site.key && $0.value.catField("detailId") == detailID
        })
        return session.context(for: card)
    }

    private nonisolated static func row(_ id: RuntimeValue) -> RuntimeValue {
        .object(["vod_id": id, "vod_name": .string("未关联样本"), "vod_remarks": .string("原备注")])
    }

    private nonisolated static var page: RuntimeValue {
        .object(["list": .array([row(.number(0)), row(.string("0"))]), "pagecount": .number(1)])
    }

    private nonisolated static func seed(_ site: FilmSearchSite, query: String, page: Int) -> RuntimeValue {
        .object(["kind": .string("search"), "source": .object(site.playbackSource.value),
                 "query": .string(query), "page": .number(Double(page)), "value": Self.page,
                 "completeness": .string("complete"), "fetchedAt": .number(123_456), "expiresAt": .number(234_567)])
    }

    private func client() throws -> RuntimeHTTPClient {
        try .init(baseURL: URL(string: "http://127.0.0.1:43211")!)
    }
}

private actor StableIdentityMetadata: FilmSearchMetadataReading {
    func search(query: String) -> RuntimeValue {
        .object(["results": .array([
            .object(["id": .number(7), "media_type": .string("movie"), "title": .string("资料电影")]),
            .object(["id": .number(7), "media_type": .string("tv"), "name": .string("资料剧集")]),
        ])])
    }
    func detail(type: String, id: Int) -> RuntimeValue {
        var fields: RuntimeObject = ["id": .number(Double(id)), "overview": .string("资料简介")]
        fields[type == "movie" ? "title" : "name"] = .string(type == "movie" ? "资料电影" : "资料剧集")
        return .object(fields)
    }
    nonisolated func imageURL(path: String) -> URL? { nil }
}
#endif

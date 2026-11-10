import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmPlaybackWireTests: XCTestCase {
    func testExternalPlayerTemplatesPreserveSignedQueriesAndSpecialCharacters() throws {
        let suffix = "://media.example.invalid:8443/a%20b/%E4%B8%AD.m3u8?sig=a%2Fb%2Bc%3D&part=one+two&part=%26%3D%25&empty=&punct=!~*'()"
        for scheme in ["http", "https"] {
            let source = try XCTUnwrap(URL(string: scheme + suffix))
            let infuse = try FilmExternalPlayer.infuse.url(for: source)
            let components = try XCTUnwrap(URLComponents(url: infuse, resolvingAgainstBaseURL: false))
            XCTAssertEqual(components.scheme, "infuse")
            XCTAssertEqual(components.host, "x-callback-url"); XCTAssertEqual(components.path, "/play")
            XCTAssertEqual(components.queryItems?.count, 1)
            XCTAssertEqual(components.queryItems?.first?.name, "url")
            XCTAssertEqual(components.queryItems?.first?.value, source.absoluteString,
                           "Exactly one decode must restore the complete URL, including repeated signed parameters")
            XCTAssertTrue(infuse.absoluteString.contains("sig%3Da%252Fb%252Bc%253D"))
            XCTAssertTrue(infuse.absoluteString.contains("one%2Btwo"))
            XCTAssertTrue(infuse.absoluteString.hasSuffix("punct%3D!~*'()"))
            let nPlayer = try FilmExternalPlayer.nPlayer.url(for: source)
            XCTAssertEqual(nPlayer.absoluteString, "nplayer-" + source.absoluteString,
                           "nPlayer changes only the scheme, not a query's encoding, order or signature")
        }
    }

    func testManualShareSourceDoesNotRequireOrInventScriptInstallation() throws {
        let pan: RuntimeValue = .object(["key": .string("manual:fixture"), "engine": .string("pan"),
                                        "name": .string("私人分享"), "detailId": .number(0)])
        var candidate = try FilmPlaybackFixture.candidate(source: pan, lineID: .number(0)).filmRequiredObject()
        candidate["provider"] = .string("quark")
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(candidates: [.object(candidate)]))
        let value = try XCTUnwrap(snapshot.candidates.first)
        XCTAssertEqual(value.source.key, "manual:fixture")
        XCTAssertNil(value.source.value["installationId"]); XCTAssertNil(value.source.value["siteId"])
        XCTAssertEqual(value.source.detailID, .number(0)); XCTAssertEqual(value.lineID, .number(0))
        XCTAssertEqual(value.provider, "quark")
    }
    func testProviderZeroIdentifiersAndSpecialEpisodeAreNotChangedToStringsOrGlobals() throws {
        let browsing: RuntimeValue = .object([
            "sourceId": .string("result-a"), "lineId": .number(0), "loading": .bool(false), "error": .null,
            "lines": .array([.object(["id": .number(0), "label": .string("原标签")])]),
            "episodes": .array([.object(["id": .string("opaque-file"), "candidateId": .string("opaque-candidate"),
                                        "name": .string("特别篇"), "season": .number(0), "episode": .number(0)])]),
        ])
        let episode: RuntimeValue = .object(["season": .number(0), "episode": .number(0)])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: browsing, episodes: [episode]))
        XCTAssertEqual(snapshot.browsing.lineID, .number(0))
        XCTAssertEqual(snapshot.browsing.lines.first?.id, .number(0))
        XCTAssertEqual(snapshot.browsing.files.first?.candidateID, "opaque-candidate")
        XCTAssertNil(snapshot.episodes.first?.position.globalEpisode)
        XCTAssertEqual(snapshot.episodes.first?.position.value, ["season": .number(0), "episode": .number(0)])
    }
    func testPresentationMetadataDoesNotManufactureCacheEntries() {
        let metadata: RuntimeValue = .object(["title": .string("partial")])
        let request = FilmPlaybackRequest(target: FilmPlaybackFixture.request().target, sources: [],
                                          metadata: metadata)
        XCTAssertEqual(request.value.filmObject?["metadata"], metadata)
        XCTAssertEqual(request.value.filmObject?["cacheEntries"], .array([]))
    }
    func testCacheOwnerIdentityTimesCompletenessAndRevisionAreTransferredUnchanged() {
        let entries: [RuntimeValue] = [
            .object(["kind": .string("detail"), "source": FilmPlaybackFixture.source,
                     "detailId": .number(0), "value": .object(["title": .string("partial")]),
                     "completeness": .string("partial"), "fetchedAt": .number(1_730_100_123_456),
                     "expiresAt": .number(1_730_100_723_456)]),
            .object(["kind": .string("metadata"), "provider": .string("tmdb"),
                     "path": .string("/tv/321"), "query": .object(["language": .string("zh-CN")]),
                     "imageSize": .string("original"),
                     "settings": .object(["revision": .string("opaque-owner-revision")]),
                     "value": .object(["id": .number(321), "name": .string("完整元数据")]),
                     "completeness": .string("complete"), "fetchedAt": .number(1_730_100_654_321)]),
        ]
        let request = FilmPlaybackRequest(target: FilmPlaybackFixture.request().target, sources: [],
                                          cacheEntries: entries)
        XCTAssertEqual(request.value.filmObject?["cacheEntries"], .array(entries))
    }
    func testFailedCurrentBrowsingCannotDisplayOldPlayableRows() throws {
        let browsing: RuntimeValue = .object([
            "sourceId": .string("result-b"), "lineId": .null, "loading": .bool(false), "error": .string("UPSTREAM_HTTP"),
            "lines": .array([]), "episodes": .array([.object(["id": .string("old"), "name": .string("old"),
                                                             "candidateId": .string("old-candidate")])]),
        ])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: browsing))
        XCTAssertTrue(snapshot.browsing.files.isEmpty)
        XCTAssertEqual(snapshot.browsing.sourceID, "result-b")
    }
    func testDirectoryRetainsAbsoluteRootUnicodeAndAccurateCandidateIdentity() throws {
        let file = try FilmPlaybackFile(.object(["id": .string("opaque"), "candidateId": .string("candidate"),
                                                "name": .string("片.mp4"), "path": .string("/目录/片.mp4")]))
        let directory = FilmPlaybackDirectory(files: [file])
        XCTAssertEqual(directory.children(at: []), [.folder("")])
        XCTAssertEqual(directory.children(at: [""]), [.folder("目录")])
        XCTAssertEqual(directory.children(at: ["", "目录"]), [.file(file)])
        XCTAssertTrue(directory.children(at: ["目录"]).isEmpty)
    }
    func testSeasonFilteredRowsAndCompleteOriginalDirectoryAreSeparate() throws {
        let first: RuntimeValue = .object(["id": .string("file-one"), "candidateId": .string("candidate-one"),
                                          "name": .string("第一季"), "path": .string("/剧集/第一季/1.mp4")])
        let second: RuntimeValue = .object(["id": .string("file-two"), "candidateId": .string("candidate-two"),
                                           "name": .string("第二季"), "path": .string("/剧集/第二季/1.mp4")])
        let browsing: RuntimeValue = .object([
            "sourceId": .string("result-a"), "lineId": .number(0), "season": .number(2),
            "loading": .bool(false), "error": .null, "lines": .array([]),
            "episodes": .array([second]), "originalEpisodes": .array([first, second]),
        ])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: browsing))
        XCTAssertEqual(snapshot.browsing.files.map(\.candidateID), ["candidate-two"])
        XCTAssertEqual(snapshot.browsing.originalFiles.map(\.candidateID), ["candidate-one", "candidate-two"])
        let directory = FilmPlaybackDirectory(files: snapshot.browsing.originalFiles)
        XCTAssertEqual(directory.children(at: ["", "剧集"]), [.folder("第一季"), .folder("第二季")])
        let missing = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot())
        XCTAssertTrue(missing.browsing.originalFiles.isEmpty)
    }

    func testMetadataMenuContainsOnlyConfirmedIdentitiesAndPreservesSpecialSeasonZero() throws {
        let sources: [RuntimeValue] = [
            .object(["id": .string("tmdb:tv:19"), "provider": .string("tmdb"), "metadataId": .number(19),
                     "kind": .string("tv"), "title": .string("星河"),
                     "seasons": .array([.object(["season_number": .number(0)])])]),
            .object(["id": .string("douban:tv:0"), "provider": .string("douban"), "metadataId": .number(0),
                     "kind": .string("tv"), "title": .string("星河特别篇")]),
        ]
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadataSources: sources, metadataSourceID: "tmdb:tv:19", metadataError: "UPSTREAM_HTTP"))
        XCTAssertEqual(snapshot.metadataSources.map(\.provider), [.tmdb, .douban])
        XCTAssertEqual(snapshot.metadataSources[1].metadataID, .number(0))
        XCTAssertEqual(snapshot.metadataSources[0].seasons, [.object(["season_number": .number(0)])])
        XCTAssertEqual(snapshot.metadataSourceID, "tmdb:tv:19")
        XCTAssertEqual(snapshot.metadataError, "UPSTREAM_HTTP")
        let missing = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot())
        XCTAssertTrue(missing.metadataSources.isEmpty, "A module or title does not prove a provider association")
    }

    func testMediaReceiptAcceptsHTTPURLsWithoutHostPortOrResourcePathAdmission() throws {
        var fields = try FilmPlaybackFixture.mediaInput("attempt-a").filmRequiredObject()
        for url in [
            "http://127.0.0.1:43210/media/resource-attempt-a/opaque-child",
            "http://127.0.0.1:43211/media/resource-attempt-a/opaque-child",
            "http://127.0.0.1:43210/media/other-resource/opaque-child",
            "http://127.0.0.1:43210/media/resource-attempt-a",
            "http://127.0.0.1:43210/media/resource-attempt-a/",
            "http://127.0.0.1:43210/media/resource-attempt-a/%2e%2e/child",
            "http://127.0.0.1:43210/media/resource-attempt-a/../child",
            "http://127.0.0.1:43210/media/resource-attempt-a/child%2fother",
            "http://127.0.0.1:43210/media/resource-attempt-a/opaque-child?auth=fixture",
            "https://media.example.invalid/media/resource-attempt-a/opaque-child",
            "http://localhost:43210/direct.mp4", "http://[::1]:43210/direct.mp4",
            "http://192.168.1.2:43210/direct.mp4",
            "https://user:synthetic@media.example.invalid:8443/direct.mp4?token=fixture#chapter",
        ] {
            fields["url"] = .string(url)
            let input = try FilmPlaybackMediaInput(value: .object(fields),
                sessionID: FilmPlaybackFixture.sessionID, attemptID: "attempt-a")
            XCTAssertEqual(input.url.absoluteString, url)
            XCTAssertEqual(input.resourceID, "resource-attempt-a")
            XCTAssertEqual(input.attemptID, "attempt-a")
            XCTAssertEqual(input.sessionID, FilmPlaybackFixture.sessionID)
            XCTAssertFalse(String(reflecting: input).contains("opaque-child"))
            XCTAssertFalse(String(describing: input).contains("43210"))
        }
    }

    func testMediaReceiptRetainsExactAttemptIdentityAndReadableHTTPFields() throws {
        var fields = try FilmPlaybackFixture.mediaInput("attempt-a").filmRequiredObject()
        fields["attemptId"] = .string("other-attempt")
        XCTAssertThrowsError(try FilmPlaybackMediaInput(value: .object(fields),
            sessionID: FilmPlaybackFixture.sessionID, attemptID: "attempt-a"))
        fields["attemptId"] = .string("attempt-a"); fields["id"] = .string("")
        XCTAssertThrowsError(try FilmPlaybackMediaInput(value: .object(fields),
            sessionID: FilmPlaybackFixture.sessionID, attemptID: "attempt-a"))
        fields["id"] = .string("resource-attempt-a")
        for value in [RuntimeValue.number(43210), .string("/relative.mp4"), .string("ftp://media.invalid/file.mp4")] {
            fields["url"] = value
            XCTAssertThrowsError(try FilmPlaybackMediaInput(value: .object(fields),
                sessionID: FilmPlaybackFixture.sessionID, attemptID: "attempt-a"))
        }
    }

    func testArtworkProjectionAcceptsLocalEndpointsAndPreservesURLComponents() throws {
        for address in ["http://localhost:19191/image.jpg", "http://[::1]:19191/image.jpg",
                        "https://user:synthetic@artwork.invalid:8443/image.jpg#detail",
                        "/v1/tmdb/images/fixture", "/v1/douban/images/fixture"] {
            XCTAssertEqual(FilmPlaybackPresentation.artwork(.string(address))?.absoluteString, address)
        }
    }

    func testMetadataIdentityChangeDropsOldArtworkAndContentWithoutInventingMissingData() throws {
        let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "原始影片",
            posterURL: URL(string: "https://artwork.example.invalid/old-poster.jpg"), score: "9.0"),
            backdropURL: URL(string: "https://artwork.example.invalid/old-backdrop.jpg"),
            logoURL: URL(string: "https://artwork.example.invalid/old-logo.png"), summary: "原简介",
            releaseDate: "2025-01-01", seasonCount: 2, episodeCount: 8,
            seasons: [.init(id: "original-season", title: "原始季", number: 1)],
            cast: [.init(id: "original-cast", name: "原始演员")],
            similarItems: [.init(sourceID: "tmdb", id: "20", title: "原始关联")])
        let same = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["name": .string("补充标题")])))
        let updated = FilmPlaybackPresentation.detail(same, original: original)
        XCTAssertEqual(updated.title, "补充标题")
        XCTAssertEqual(updated.item.kind, .tv)
        XCTAssertEqual(updated.seasons, original.seasons)
        XCTAssertEqual(updated.cast, original.cast)
        XCTAssertEqual(updated.similarItems, original.similarItems)
        XCTAssertEqual(updated.item.posterURL, original.item.posterURL)
        var target = try FilmPlaybackFixture.target.filmRequiredObject()
        target["metadataProvider"] = .string("douban"); target["metadataId"] = .number(0)
        let linked: RuntimeValue = .object(["id": .string("douban:tv:0"), "provider": .string("douban"),
            "metadataId": .number(0), "kind": .string("tv"), "title": .string("可靠关联"),
            "seasons": .array([.object(["id": .number(0), "season_number": .number(0), "name": .string("特别季")])])])
        let changed = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(target: .object(target),
            metadataSources: [linked], metadataSourceID: "douban:tv:0"))
        let result = FilmPlaybackPresentation.detail(changed, original: original)
        XCTAssertEqual(result.item.sourceID, "douban"); XCTAssertEqual(result.item.id, "0")
        XCTAssertNil(result.item.posterURL); XCTAssertNil(result.backdropURL); XCTAssertNil(result.logoURL)
        XCTAssertTrue(result.summary.isEmpty); XCTAssertTrue(result.cast.isEmpty)
        XCTAssertTrue(result.similarItems.isEmpty); XCTAssertTrue(result.item.score.isEmpty)
        XCTAssertNil(result.releaseDate); XCTAssertNil(result.episodeCount)
        XCTAssertEqual(result.seasons.map(\.number), [0], "Only the confirmed association supplies this season")
    }

    func testTMDBDetailAndSimilarProjectionCarryTheExactEndpointKind() throws {
        for kind in [FilmItemKind.movie, .tv] {
            let target = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19",
                kind: kind == .movie ? .movie : .tv, title: "影片",
                metadataProvider: .tmdb, metadataID: "19")
            let metadata: RuntimeValue = .object([
                "title": .string("真实标题"),
                "similar": .object(["results": .array([
                    .object(["id": .number(20), "title": .string("相似条目")]),
                ])]),
            ])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: metadata, target: .object(target.value)))
            let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "目录标题", kind: kind))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.item.kind, kind)
            XCTAssertEqual(detail.item.identity, FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: kind))
            XCTAssertEqual(detail.similarItems.count, 1)
            XCTAssertEqual(detail.similarItems.first?.kind, kind)
            XCTAssertEqual(detail.similarItems.first?.identity,
                           FilmItemIdentity(sourceID: "tmdb", itemID: "20", kind: kind))
        }
    }

    func testChangingOnlyTMDBKindDropsOtherWorkContentWhileSameKindPartialKeepsIt() throws {
        for kind in [FilmItemKind.movie, .tv] {
            let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "原作", kind: kind,
                posterURL: URL(string: "https://artwork.example.invalid/poster.jpg"),
                largePosterURL: URL(string: "https://artwork.example.invalid/large.jpg"),
                score: "9.0", subtitle: "2025"),
                backdropURL: URL(string: "https://artwork.example.invalid/backdrop.jpg"),
                logoURL: URL(string: "https://artwork.example.invalid/logo.png"),
                summary: "原作简介", releaseDate: "2025-01-01",
                seasonCount: kind == .tv ? 2 : nil, episodeCount: kind == .tv ? 8 : nil,
                seasons: kind == .tv ? [.init(id: "old-season", title: "原作季度", number: 1)] : [],
                cast: [.init(id: "old-cast", name: "原作演员")],
                similarItems: [.init(sourceID: "tmdb", id: "20", title: "原作关联", kind: kind)])
            let target = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19",
                kind: kind == .movie ? .movie : .tv, title: "当前作品",
                metadataProvider: .tmdb, metadataID: "19")
            let same = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["name": .string("补充标题")]), target: .object(target.value)))
            let partial = FilmPlaybackPresentation.detail(same, original: original)
            XCTAssertEqual(partial.item.identity, original.item.identity)
            XCTAssertEqual(partial.title, "补充标题")
            XCTAssertEqual(partial.item.posterURL, original.item.posterURL)
            XCTAssertEqual(partial.item.largePosterURL, original.item.largePosterURL)
            XCTAssertEqual(partial.backdropURL, original.backdropURL)
            XCTAssertEqual(partial.logoURL, original.logoURL)
            XCTAssertEqual(partial.summary, original.summary)
            XCTAssertEqual(partial.releaseDate, original.releaseDate)
            XCTAssertEqual(partial.seasonCount, original.seasonCount)
            XCTAssertEqual(partial.episodeCount, original.episodeCount)
            XCTAssertEqual(partial.seasons, original.seasons)
            XCTAssertEqual(partial.cast, original.cast)
            XCTAssertEqual(partial.similarItems, original.similarItems)
            let otherKind: FilmItemKind = kind == .movie ? .tv : .movie
            let changedTarget = FilmPlaybackTarget(id: "tmdb:\(otherKind.rawValue):19",
                kind: otherKind == .movie ? .movie : .tv, title: "另一作品",
                metadataProvider: .tmdb, metadataID: "19")
            let changed = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                target: .object(changedTarget.value)))
            let detail = FilmPlaybackPresentation.detail(changed, original: original)
            XCTAssertEqual(detail.item.kind, otherKind)
            XCTAssertNotEqual(detail.item.identity, original.item.identity)
            XCTAssertEqual(detail.title, "另一作品")
            XCTAssertNil(detail.item.posterURL)
            XCTAssertNil(detail.item.largePosterURL)
            XCTAssertNil(detail.backdropURL)
            XCTAssertNil(detail.logoURL)
            XCTAssertTrue(detail.item.score.isEmpty)
            XCTAssertTrue(detail.item.subtitle.isEmpty)
            XCTAssertTrue(detail.summary.isEmpty)
            XCTAssertNil(detail.releaseDate)
            XCTAssertNil(detail.seasonCount)
            XCTAssertNil(detail.episodeCount)
            XCTAssertTrue(detail.seasons.isEmpty)
            XCTAssertTrue(detail.cast.isEmpty)
            XCTAssertTrue(detail.similarItems.isEmpty)
        }
    }

    func testDoubanProjectionCarriesKnownKindWithoutRekeyingLegacyGlobalIdentity() throws {
        let target = FilmPlaybackTarget(id: "douban:tv:19", kind: .tv, title: "豆瓣条目",
            metadataProvider: .douban, metadataID: "19")
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(target: .object(target.value)))
        let original = FilmDetail(item: .init(sourceID: "douban", id: "19", title: "旧豆瓣"))
        let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
        XCTAssertEqual(detail.item.kind, .tv)
        XCTAssertEqual(detail.item.identity, original.item.identity)
        XCTAssertNil(original.item.kind)
    }

    func testOriginalSourceProjectionDoesNotInventAMetadataKindOrChangeIdentity() throws {
        let target = FilmPlaybackTarget(id: "original-source-item", kind: .source, title: "原始单源",
            source: ["key": .string("source-key"), "detailId": .number(0)])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(target: .object(target.value)))
        let original = FilmDetail(item: .init(sourceID: "source-key", id: "0", title: "原始单源"))
        let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
        XCTAssertNil(detail.item.kind)
        XCTAssertEqual(detail.item.identity, original.item.identity)
        XCTAssertEqual(detail.item.sourceID, "source-key")
        XCTAssertEqual(detail.item.id, "0")
    }
}

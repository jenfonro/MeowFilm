import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmPlaybackWireTests: XCTestCase {
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
    func testMetadataEntryRetargetKeepsProvenanceAndExactSourceWithoutSearchReference() throws {
        let source = try FilmPlaybackSource(FilmPlaybackFixture.source)
        let sourceKey = try XCTUnwrap(source.key)
        for provider in [FilmMetadataProvider.tmdb, .douban] {
            for kind in [FilmPlaybackKind.movie, .tv] {
                let entry = FilmPlaybackTarget(id: "\(provider.rawValue):\(kind.rawValue):19",
                    kind: kind, title: "元数据入口", metadataProvider: provider, metadataID: "19")
                let metadata: RuntimeValue = .object(["id": .number(19), "title": .string("入口资料")])
                let exact = FilmPlaybackTarget(id: entry.id, kind: .source, title: entry.title,
                    position: kind == .tv ? .init(season: 0, episode: 0) : .init(),
                    source: ["key": .string(sourceKey), "detailId": .number(0),
                             "lineId": .string("0"), "candidateId": .string("opaque-selected")])
                let original = FilmPlaybackRequest(target: entry, sources: [source],
                    metadata: metadata, entrySurface: "home")
                XCTAssertNil(original.resumeTarget)
                let request = original.retargeted(exact)
                XCTAssertEqual(request.target, entry)
                XCTAssertEqual(request.metadata, metadata)
                XCTAssertEqual(request.sources, [source])
                XCTAssertNil(request.searchReference)
                XCTAssertEqual(request.resumeTarget, exact)
                XCTAssertEqual(request.value.filmObject?["target"], .object(entry.value))
                XCTAssertEqual(request.value.filmObject?["metadata"], metadata)
                XCTAssertEqual(request.value.filmObject?["resumeTarget"], .object(exact.value))
                XCTAssertEqual(request.value.filmObject?["entrySurface"], .string("home"))
                XCTAssertEqual(request.value.filmObject?["platform"], .string("ios"))
                XCTAssertNil(request.resumeTarget?.metadataProvider, "Provenance does not turn the exact execution into metadata playback")

                let reference: RuntimeValue = .object(["sessionId": .string(UUID().uuidString.lowercased()),
                                                       "selectionId": .string(entry.id)])
                let fromSearch = FilmPlaybackRequest(target: entry, sources: [source], metadata: metadata,
                    searchReference: reference, entrySurface: "search").retargeted(exact)
                XCTAssertEqual(fromSearch.target, exact)
                XCTAssertEqual(fromSearch.resumeTarget, exact)
                XCTAssertEqual(fromSearch.searchReference, reference)
                XCTAssertNil(fromSearch.metadata, "Search reentry reloads the entry through its owner reference")
            }
        }
    }
    func testSourceAndGroupRetargetDoNotInventMetadataProvenance() {
        let source: RuntimeObject = ["key": .string("source-key"), "detailId": .number(0),
            "lineId": .string("0"), "candidateId": .string("opaque-selected")]
        for kind in [FilmPlaybackKind.source, .group] {
            let members: [RuntimeValue]? = kind == .group
                ? [.object(["sourceKey": .string("source-key"), "detailId": .number(0)])] : nil
            let entry = FilmPlaybackTarget(id: "site-card", kind: kind, title: "站点条目",
                source: kind == .source ? ["key": .string("source-key"), "detailId": .number(0)] : nil,
                members: members)
            let selected = FilmPlaybackTarget(id: entry.id, kind: kind, title: entry.title,
                source: source, members: members)
            let request = FilmPlaybackRequest(target: entry, sources: []).retargeted(selected)
            XCTAssertEqual(request.target, selected)
            XCTAssertNil(request.target.metadataProvider)
            XCTAssertNil(request.metadata); XCTAssertNil(request.searchReference)
            XCTAssertNil(request.resumeTarget)
            XCTAssertNil(request.value.filmObject?["metadata"])
            XCTAssertNil(request.value.filmObject?["resumeTarget"])
            XCTAssertEqual(request.target.value["members"], members.map(RuntimeValue.array))
        }
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
    func testRefreshingBrowseKeepsSuccessfulFilesAndDefaultsToFalseWhenAbsent() throws {
        let file: RuntimeValue = .object(["id": .string("old"), "name": .string("旧成功文件"),
                                          "candidateId": .string("old-candidate")])
        var browsing: RuntimeObject = [
            "sourceId": .string("result-a"), "lineId": .number(0), "loading": .bool(true),
            "error": .null, "lines": .array([]), "episodes": .array([file]), "originalEpisodes": .array([file])
        ]
        for refreshing in [RuntimeValue?.none, .bool(false), .bool(true)] {
            browsing["refreshing"] = refreshing
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(browsing)))
            XCTAssertTrue(snapshot.browsing.loading)
            XCTAssertEqual(snapshot.browsing.refreshing, refreshing == .bool(true))
            XCTAssertEqual(snapshot.browsing.files.map(\.candidateID), ["old-candidate"])
            XCTAssertEqual(snapshot.browsing.originalFiles.map(\.candidateID), ["old-candidate"])
        }
        browsing["refreshing"] = .string("true")
        XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(browsing))))
    }

    func testRefreshSessionFailureKeepsSuccessfulDirectoryButBrowsingFailureHidesIt() throws {
        let file: RuntimeValue = .object(["id": .string("old"), "name": .string("旧成功文件"),
                                          "candidateId": .string("old-candidate")])
        var browsing: RuntimeObject = [
            "sourceId": .string("result-a"), "lineId": .number(0), "loading": .bool(false),
            "refreshing": .bool(false), "error": .null, "lines": .array([]),
            "episodes": .array([file]), "originalEpisodes": .array([file])
        ]
        var fields = try FilmPlaybackFixture.snapshot(browsing: .object(browsing)).filmRequiredObject()
        fields["error"] = .string("UPSTREAM_HTTP")
        let failedRefresh = try FilmPlaybackSnapshot(value: .object(fields))
        XCTAssertEqual(failedRefresh.error, "UPSTREAM_HTTP")
        XCTAssertNil(failedRefresh.browsing.error)
        XCTAssertFalse(failedRefresh.browsing.refreshing)
        XCTAssertEqual(failedRefresh.browsing.files.map(\.candidateID), ["old-candidate"])
        XCTAssertEqual(failedRefresh.browsing.originalFiles.map(\.candidateID), ["old-candidate"])
        browsing["error"] = .string("STALE_SOURCE")
        fields["browsing"] = .object(browsing)
        let failedBrowse = try FilmPlaybackSnapshot(value: .object(fields))
        XCTAssertEqual(failedBrowse.browsing.error, "STALE_SOURCE")
        XCTAssertTrue(failedBrowse.browsing.files.isEmpty)
        XCTAssertTrue(failedBrowse.browsing.originalFiles.isEmpty)
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
                        "/v1/tmdb/images/original/fixture.jpg", "/v1/douban/images/fixture"] {
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

    func testExactSourceTargetRetainsConfirmedMetadataKindSeasonAndIdentity() throws {
        for kind in [FilmPlaybackKind.movie, .tv] {
            let target = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19", kind: .source, title: "准确来源",
                position: kind == .tv ? .init(season: 0, episode: 0) : .init(),
                source: ["key": .string("source-key"), "detailId": .number(0),
                         "lineId": .string("0"), "candidateId": .string("opaque-selected")])
            let confirmed: RuntimeValue = .object([
                "id": .string("confirmed-tmdb"), "provider": .string("tmdb"), "metadataId": .number(19),
                "kind": .string(kind.rawValue), "title": .string("可靠作品"),
                "seasons": .array(kind == .tv ? [.object(["season_number": .number(0), "name": .string("特别季")])] : []),
            ])
            let episode: RuntimeValue = .object(["season": .number(0), "episode": .number(0), "name": .string("真实特别集")])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["name": .string("可靠作品")]), episodes: kind == .tv ? [episode] : [],
                target: .object(target.value), metadataSources: [confirmed], metadataSourceID: "confirmed-tmdb"))
            let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "原作品",
                kind: kind == .movie ? .movie : .tv), summary: "已有简介")
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(snapshot.target, target)
            XCTAssertEqual(snapshot.target.kind, .source)
            XCTAssertEqual(snapshot.target.value["source"]?.filmObject?["lineId"], .string("0"))
            XCTAssertEqual(detail.item.identity, original.item.identity)
            XCTAssertEqual(detail.item.kind, kind == .movie ? .movie : .tv)
            XCTAssertEqual(detail.title, "可靠作品"); XCTAssertEqual(detail.summary, "已有简介")
            XCTAssertEqual(detail.seasons.map(\.number), kind == .tv ? [0] : [])
            XCTAssertEqual(detail.seasons.first?.episodes.map(\.title) ?? [], kind == .tv ? ["真实特别集"] : [])
        }
    }

    func testMetadataBrowseProjectionUsesConfirmedProviderWithoutChangingPlaybackTarget() throws {
        let target = FilmPlaybackTarget(id: "tmdb:movie:19", kind: .source, title: "原作品",
            source: ["key": .string("source-key"), "detailId": .number(0), "candidateId": .string("opaque-current")])
        let confirmed: RuntimeValue = .object(["id": .string("douban:movie:0"),
            "provider": .string("douban"), "metadataId": .number(0), "kind": .string("movie"), "title": .string("当前资料")])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["title": .string("当前资料")]), target: .object(target.value),
            metadataSources: [confirmed], metadataSourceID: "douban:movie:0"))
        let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "旧资料", kind: .movie,
            posterURL: URL(string: "https://artwork.example.invalid/old.jpg")), summary: "旧简介")
        let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
        XCTAssertEqual(detail.item.sourceID, "douban"); XCTAssertEqual(detail.item.id, "0")
        XCTAssertEqual(detail.item.kind, .movie); XCTAssertEqual(detail.title, "当前资料")
        XCTAssertTrue(detail.summary.isEmpty); XCTAssertNil(detail.item.posterURL)
        XCTAssertEqual(snapshot.target, target)
        XCTAssertEqual(snapshot.target.value["source"]?.filmObject?["candidateId"], .string("opaque-current"))
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

import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmPlaybackWireTests: XCTestCase {
    func testUnconsumedPreferencesDoNotRejectAPreparationAttempt() throws {
        let value = FilmPlaybackFixture.snapshot(stage: "prepared",
            attempt: FilmPlaybackFixture.attempt("prepared", prepareOnly: true))
        let expected = try FilmPlaybackSnapshot(value: value)
        for preferences in [RuntimeValue?.none, .null, .bool(false), .string("unused"),
                            .object(["iosAutoPrepare": .array([])])] {
            var fields = try value.filmRequiredObject()
            fields["preferences"] = preferences
            XCTAssertEqual(try FilmPlaybackSnapshot(value: .object(fields)), expected)
        }
        XCTAssertEqual(expected.attempt?.id, "prepared")
        XCTAssertEqual(expected.attempt?.prepareOnly, true)
        XCTAssertNil(expected.current)
    }

    func testUnusableSupplementDoesNotRejectPrimaryMetadataOrAnActiveAttempt() throws {
        let target = FilmPlaybackTarget(id: "douban:tv:100", kind: .tv, title: "豆瓣",
            metadataProvider: .douban, metadataID: "100")
        let metadata: RuntimeValue = .object(["title": .string("主资料")])
        for supplement in [
            RuntimeValue.bool(false),
            .object(["provider": .string("future-provider"), "metadata": .object([:])]),
            .object(["provider": .string("tmdb"), "metadataId": .object([:]),
                     "kind": .string("tv"), "metadata": .object([:])]),
        ] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                stage: "pending", attempt: FilmPlaybackFixture.attempt("pending"), metadata: metadata,
                target: .object(target.value), metadataSupplement: supplement))
            XCTAssertEqual(snapshot.target.id, target.id)
            XCTAssertEqual(snapshot.metadata, metadata)
            XCTAssertEqual(snapshot.attempt?.id, "pending")
            XCTAssertNil(snapshot.metadataSupplement)
        }
    }

    func testAuxiliaryToleranceDoesNotAdmitInvalidSessionSequenceOrExecutionMaterials() throws {
        for (key, value) in [
            ("sessionId", RuntimeValue.string("not-a-uuid")),
            ("sequence", .string("1")),
            ("attempt", .object(["id": .string("pending")])),
            ("current", .object(["attemptId": .string("current")])),
        ] {
            var fields = try FilmPlaybackFixture.snapshot().filmRequiredObject()
            fields[key] = value
            fields["metadataCache"] = .object(["status": .string("future")])
            fields["metadataSupplement"] = .bool(false)
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: .object(fields))) {
                XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
            }
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
        XCTAssertEqual(snapshot.episodes.first?.businessID, "fixture-season-0-episode-0")
        XCTAssertEqual(snapshot.episodes.first?.playbackTarget.position, .init(season: 0, episode: 0))
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
        for unused in [RuntimeValue?.none, .null, .bool(false), .object(["unused": .number(1)]),
                       .array([.null, .object(["id": .string("missing-name")])]),
                       .array([.object(["id": .string("old"), "name": .string("old"),
                                       "candidateId": .string("old-candidate")])])] {
            var browsing: RuntimeObject = [
                "sourceId": .string("result-b"), "lineId": .number(0), "loading": .bool(false),
                "refreshing": .bool(true), "error": .string("UPSTREAM_HTTP"),
                "lines": .array([.object(["id": .number(0), "label": .string("真实线路")])]),
            ]
            browsing["episodes"] = unused; browsing["originalEpisodes"] = unused
            let value = FilmPlaybackFixture.snapshot(
                current: FilmPlaybackFixture.current("playing"), browsing: .object(browsing))
            XCTAssertEqual(value.filmObject?["browsing"]?.filmObject?["episodes"], unused)
            XCTAssertEqual(value.filmObject?["browsing"]?.filmObject?["originalEpisodes"], unused)
            let snapshot = try FilmPlaybackSnapshot(value: value)
            XCTAssertEqual(snapshot.browsing.error, "UPSTREAM_HTTP")
            XCTAssertTrue(snapshot.browsing.files.isEmpty); XCTAssertTrue(snapshot.browsing.originalFiles.isEmpty)
            XCTAssertEqual(snapshot.browsing.sourceID, "result-b")
            XCTAssertEqual(snapshot.browsing.lineID, .number(0))
            XCTAssertEqual(snapshot.browsing.lines.map(\.label), ["真实线路"])
            XCTAssertTrue(snapshot.browsing.refreshing)
            XCTAssertEqual(snapshot.current?.attemptID, "playing")
            XCTAssertEqual(snapshot.current?.candidate.lineID, .string("opaque-line"))
        }
    }

    func testBrowsingFailureStillConsumesLinesRefreshingAndIndependentCurrent() throws {
        let browsing: RuntimeObject = [
            "sourceId": .null, "lineId": .null,
            "loading": .bool(false), "error": .string("UPSTREAM_HTTP"), "lines": .array([]),
        ]
        let healthy = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(browsing)))
        XCTAssertEqual(healthy.browsing.error, "UPSTREAM_HTTP")
        XCTAssertTrue(healthy.browsing.lines.isEmpty)
        for (key, value) in [("lines", RuntimeValue.bool(false)),
                             ("lines", .array([.object(["businessId": .string("fixture-line-0"),
                                 "playbackTarget": FilmPlaybackFixture.sourceTarget(lineID: .number(0)),
                                 "label": .string("缺少身份")])])),
                             ("refreshing", .string("true"))] {
            var invalid = browsing; invalid[key] = value
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(invalid)))) {
                XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
            }
        }
        XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            current: .object(["attemptId": .string("missing-candidate")]), browsing: .object(browsing)))) {
            XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
        }
    }

    func testSuccessfulBrowsingRequiresUsableFileListsAndIdentities() throws {
        let browsing: RuntimeObject = [
            "sourceId": .null, "lineId": .null, "loading": .bool(false), "error": .null,
            "lines": .array([]), "episodes": .array([]),
        ]
        let healthy = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(browsing)))
        XCTAssertTrue(healthy.browsing.files.isEmpty); XCTAssertTrue(healthy.browsing.originalFiles.isEmpty)
        for key in ["episodes", "originalEpisodes"] {
            for invalid in [RuntimeValue.object([:]), .array([.null]),
                            .array([.object(["businessId": .string("file"), "playbackTarget": FilmPlaybackFixture.sourceTarget(),
                                             "id": .string("missing-name")])]),
                            .array([.object(["businessId": .string("file"), "playbackTarget": FilmPlaybackFixture.sourceTarget(),
                                             "id": .number(0), "name": .string("错误身份")])])] {
                var invalidBrowsing = browsing; invalidBrowsing[key] = invalid
                XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: .object(invalidBrowsing)))) {
                    XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
                }
            }
        }
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
        let file = try FilmPlaybackFile(.object(["businessId": .string("opaque"),
                                                "playbackTarget": FilmPlaybackFixture.sourceTarget(candidateID: "candidate"),
                                                "id": .string("opaque"), "candidateId": .string("candidate"),
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

    func testMetadataMenuKeepsHealthySiblingsAndPrimarySeasonsDespiteUnusableAuxiliaryRows() throws {
        let selectedTarget = FilmPlaybackTarget(id: "tmdb:tv:19", kind: .tv, title: "主资料",
            metadataProvider: .tmdb, metadataID: "19")
        let selected: RuntimeObject = [
            "id": .string("tmdb:tv:19"), "provider": .string("tmdb"), "metadataId": .number(19),
            "kind": .string("tv"), "title": .string("主资料"),
            "businessId": .string("tmdb:tv:19"), "playbackTarget": .object(selectedTarget.value),
        ]
        let other: RuntimeObject = [
            "id": .string("douban:tv:0"), "provider": .string("douban"), "metadataId": .number(0),
            "kind": .string("tv"), "title": .string("健康后项"),
        ]
        let specialTarget = FilmPlaybackTarget(id: "tmdb:tv:19", kind: .tv, title: "主资料",
            metadataProvider: .tmdb, metadataID: "19", position: .init(season: 0))
        let metadata: RuntimeValue = .object([
            "name": .string("主资料"), "seasons": .array([
                .object(["id": .string("special"), "season_number": .number(0), "name": .string("主特别篇"),
                         "businessId": .string("tmdb:tv:19:season:0"), "playbackTarget": .object(specialTarget.value)]),
            ]),
        ])
        let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "原资料", kind: .tv))
        for seasons in [RuntimeValue?.none, .null, .bool(false), .string("unused"),
                        .object(["unused": .array([])]), .array([.null])] {
            var row = selected; row["seasons"] = seasons
            var last = other; last["seasons"] = seasons
            var badProvider = selected; badProvider["id"] = .string("bad-provider")
            badProvider["provider"] = .string("future")
            var badKind = selected; badKind["id"] = .string("bad-kind"); badKind["kind"] = .string("future")
            var badID = selected; badID["id"] = .string("bad-id"); badID["metadataId"] = .object([:])
            var badBusinessID = selected; badBusinessID["id"] = .string("bad-business-id"); badBusinessID["businessId"] = .null
            var badTarget = selected; badTarget["id"] = .string("bad-target"); badTarget["playbackTarget"] = .object([:])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("pending"), metadata: metadata,
                episodes: [.object(["season": .number(0), "episode": .number(0), "name": .string("特别集")])],
                metadataSources: [.object(row), .null, .object(badProvider), .object(badKind), .object(badID),
                                  .object(badBusinessID), .object(badTarget), .object(last)],
                metadataSourceID: "tmdb:tv:19"))
            XCTAssertEqual(snapshot.metadataSources.map(\.id), ["tmdb:tv:19", "douban:tv:0"])
            XCTAssertEqual(snapshot.metadataSources.map(\.provider), [.tmdb, .douban])
            XCTAssertEqual(snapshot.metadataSources.map(\.metadataID), [.number(19), .number(0)])
            XCTAssertEqual(snapshot.metadataSourceID, "tmdb:tv:19")
            XCTAssertEqual(snapshot.attempt?.id, "pending")
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.item.identity, original.item.identity)
            let seasons = FilmPlaybackPresentation.seasons(snapshot)
            XCTAssertEqual(seasons.map(\.number), [0])
            XCTAssertEqual(seasons.map(\.title), ["主特别篇"])
            XCTAssertEqual(seasons.first?.episodes.map(\.number), [0])
            XCTAssertEqual(seasons.first?.businessID, "tmdb:tv:19:season:0")
            XCTAssertEqual(seasons.first?.playbackTarget, specialTarget)
            XCTAssertEqual(seasons.first?.episodes.first?.businessID, "fixture-season-0-episode-0")
            XCTAssertEqual(seasons.first?.episodes.first?.playbackTarget?.position, .init(season: 0, episode: 0))
        }
    }

    func testSelectedMetadataReferenceRequiresAnExactStringOrNoSelection() throws {
        let selected: RuntimeValue = .object([
            "id": .string("0"), "provider": .string("tmdb"), "metadataId": .number(0),
            "kind": .string("tv"), "title": .string("特别篇"),
        ])
        let value = FilmPlaybackFixture.snapshot(metadataSources: [selected], metadataSourceID: "0")
        let snapshot = try FilmPlaybackSnapshot(value: value)
        XCTAssertEqual(snapshot.metadataSourceID, "0")
        XCTAssertEqual(snapshot.metadataSources.first?.metadataID, .number(0))
        for invalid in [RuntimeValue.number(0), .object(["id": .string("0")]),
                        .bool(false), .array([.string("0")]), .string("")] {
            var fields = try value.filmRequiredObject(); fields["metadataSourceId"] = invalid
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: .object(fields))) {
                XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
            }
        }
        for absent in [RuntimeValue?.none, .null] {
            var fields = try value.filmRequiredObject(); fields["metadataSourceId"] = absent
            let unselected = try FilmPlaybackSnapshot(value: .object(fields))
            XCTAssertNil(unselected.metadataSourceID)
            XCTAssertEqual(unselected.metadataSources.map(\.id), ["0"])
        }
    }

    func testSelectedMetadataStillRequiresConfirmedProviderKindAndTypedIdentity() throws {
        let selectedTarget = FilmPlaybackTarget(id: "0", kind: .tv, title: "特别篇",
            metadataProvider: .tmdb, metadataID: "0")
        let selected: RuntimeObject = [
            "id": .string("0"), "provider": .string("tmdb"), "metadataId": .number(0),
            "kind": .string("tv"), "title": .string("特别篇"),
            "businessId": .string("0"), "playbackTarget": .object(selectedTarget.value),
        ]
        var other = selected; other["id"] = .string("healthy"); other["metadataId"] = .string("0")
        other["businessId"] = .string("healthy")
        other["playbackTarget"] = .object(FilmPlaybackTarget(id: "healthy", kind: .tv, title: "特别篇",
            metadataProvider: .tmdb, metadataID: "0").value)
        for (key, invalid) in [
            ("id", RuntimeValue.object([:])), ("id", .number(0)), ("provider", .string("future")),
            ("kind", .string("future")), ("metadataId", .object([:])), ("metadataId", .bool(false)),
            ("businessId", .null), ("businessId", .number(0)),
            ("playbackTarget", .null), ("playbackTarget", .object([:])),
        ] {
            var row = selected; row[key] = invalid
            let value = FilmPlaybackFixture.snapshot(metadataSources: [.object(row), .object(other)], metadataSourceID: "0")
            XCTAssertEqual(value.filmObject?["metadataSources"]?.filmArray?.first?.filmObject?[key], invalid)
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: value)) {
                XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
            }
        }
        XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadataSources: [.object(selected), .object(other)], metadataSourceID: "missing"))) {
            XCTAssertEqual($0 as? FilmPlaybackError, .invalidResponse)
        }
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

    func testWorkInfoRequiresItsCompleteMetadataProjection() throws {
        var work = FilmPlaybackFixture.workInfo().filmObject!
        for invalid in [RuntimeValue.null, .array([]), .string("metadata")] {
            work["metadata"] = invalid
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(workInfo: .object(work))))
        }
        work["metadata"] = nil
        XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(workInfo: .object(work))))
    }

    func testTMDBDetailAndSimilarProjectionCarryTheExactEndpointKind() throws {
        for kind in [FilmItemKind.movie, .tv] {
            let target = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19",
                kind: kind == .movie ? .movie : .tv, title: "影片",
                metadataProvider: .tmdb, metadataID: "19")
            let similarTarget = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):20",
                kind: kind == .movie ? .movie : .tv, title: "相似条目",
                metadataProvider: .tmdb, metadataID: "20")
            let metadata: RuntimeValue = .object([
                "title": .string("真实标题"),
                "similar": .object(["results": .array([
                    .object(["id": .number(20), "title": .string("相似条目"),
                             "businessId": .string(similarTarget.id), "playbackTarget": .object(similarTarget.value)]),
                ])]),
            ])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["title": .string("浏览标题")]), target: .object(target.value),
                workInfo: FilmPlaybackFixture.workInfo(kind: kind == .movie ? .movie : .tv,
                    title: "真实标题", metadata: metadata.filmObject!)))
            let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "目录标题", kind: kind))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.item.kind, kind)
            XCTAssertEqual(detail.item.identity, FilmItemIdentity(sourceID: "tmdb", itemID: "19", kind: kind))
            XCTAssertEqual(detail.similarItems.count, 1)
            XCTAssertEqual(detail.similarItems.first?.kind, kind)
            XCTAssertEqual(detail.similarItems.first?.id, "20")
            XCTAssertEqual(detail.similarItems.first?.playbackTarget, similarTarget)
            XCTAssertEqual(detail.similarItems.first?.identity,
                           FilmItemIdentity(sourceID: "tmdb", itemID: "20", kind: kind,
                                            businessID: similarTarget.id, playbackTarget: similarTarget))
        }
    }

    func testMissingWorkInfoNeverUsesBrowsingMetadataOrRekeysTheEntry() throws {
        for provider in [FilmMetadataProvider.tmdb, .douban] {
            let target = FilmPlaybackTarget(id: "entry", kind: .tv, title: "入口",
                metadataProvider: provider, metadataID: "19")
            let original = FilmDetail(item: .init(sourceID: provider.rawValue, id: "19", title: "入口", kind: .tv,
                businessID: target.id, playbackTarget: target), summary: "入口简介")
            let browse = FilmPlaybackFixture.snapshot(metadata: .object(["title": .string("另一资料")]),
                target: FilmPlaybackFixture.sourceTarget())
            XCTAssertEqual(FilmPlaybackPresentation.detail(try .init(value: browse), original: original), original)
        }
    }

    func testExactSourceTargetRetainsConfirmedMetadataKindSeasonAndIdentity() throws {
        for kind in [FilmPlaybackKind.movie, .tv] {
            let target = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19", kind: .source, title: "准确来源",
                position: kind == .tv ? .init(season: 0, episode: 0) : .init(),
                source: ["key": .string("source-key"), "detailId": .number(0),
                         "lineId": .string("0"), "candidateId": .string("opaque-selected")])
            let specialTarget = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19", kind: kind, title: "可靠作品",
                metadataProvider: .tmdb, metadataID: "19", position: .init(season: 0))
            let confirmed: RuntimeValue = .object([
                "id": .string("confirmed-tmdb"), "provider": .string("tmdb"), "metadataId": .number(19),
                "kind": .string(kind.rawValue), "title": .string("可靠作品"),
                "seasons": .array(kind == .tv ? [.object(["season_number": .number(0), "name": .string("特别季"),
                    "businessId": .string("tmdb:tv:19:season:0"), "playbackTarget": .object(specialTarget.value)])] : []),
            ])
            let episode: RuntimeValue = .object(["season": .number(0), "episode": .number(0), "name": .string("真实特别集")])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["name": .string("可靠作品")]), episodes: kind == .tv ? [episode] : [],
                target: .object(target.value), metadataSources: [confirmed], metadataSourceID: "confirmed-tmdb",
                workInfo: FilmPlaybackFixture.workInfo(kind: kind, title: "可靠作品", summary: "已有简介",
                    metadata: ["seasons": confirmed.filmObject!["seasons"]!])))
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
            XCTAssertTrue(detail.seasons.allSatisfy { $0.episodes.isEmpty })
            XCTAssertEqual(FilmPlaybackPresentation.seasons(snapshot).first?.episodes.map(\.title) ?? [],
                           kind == .tv ? ["真实特别集"] : [])
        }
    }

    func testCompleteWorkProjectionPreservesTheEntryAndIgnoresAllBrowsingFields() throws {
        let target = FilmPlaybackTarget(id: "entry-source", kind: .source, title: "入口",
            source: ["key": .string("source-key"), "detailId": .number(0)])
        let original = FilmDetail(item: .init(sourceID: "source-key", id: "0", title: "入口",
            businessID: target.id, playbackTarget: target))
        let metadata: RuntimeObject = ["poster_path": .string("https://artwork.invalid/work.jpg"),
            "backdrop_path": .string("https://artwork.invalid/backdrop.jpg"), "vote_average": .number(8.5),
            "release_date": .string("2026-01-01"),
            "images": .object(["logos": .array([.object(["file_path": .string("https://artwork.invalid/logo.png")])])]),
            "credits": .object(["cast": .array([.object(["id": .number(1), "name": .string("作品演员")])])])]
        let work = FilmPlaybackFixture.workInfo(title: "作品", summary: "稳定简介", metadata: metadata,
                                               seasonCount: 2, episodeCount: 16)
        var baseline: FilmDetail?
        for provider in [FilmMetadataProvider.tmdb, .douban] {
            let source: RuntimeValue = .object(["id": .string(provider.rawValue), "provider": .string(provider.rawValue),
                "metadataId": .number(99), "kind": .string("movie"), "title": .string("浏览作品")])
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["title": .string("浏览作品"), "poster_path": .string("https://artwork.invalid/browse.jpg")]),
                target: .object(target.value), metadataSources: [source], metadataSourceID: provider.rawValue, workInfo: work))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.item.identity, original.item.identity)
            XCTAssertEqual(detail.item.playbackTarget, target)
            XCTAssertNil(detail.item.kind)
            XCTAssertEqual(detail.title, "作品"); XCTAssertEqual(detail.summary, "稳定简介")
            XCTAssertEqual(detail.item.posterURL?.absoluteString, "https://artwork.invalid/work.jpg")
            XCTAssertEqual(detail.backdropURL?.absoluteString, "https://artwork.invalid/backdrop.jpg")
            XCTAssertEqual(detail.logoURL?.absoluteString, "https://artwork.invalid/logo.png")
            XCTAssertEqual(detail.item.score, "8.5"); XCTAssertEqual(detail.releaseDate, "2026-01-01")
            XCTAssertEqual(detail.cast.map(\.name), ["作品演员"])
            XCTAssertEqual(detail.seasonCount, 2); XCTAssertEqual(detail.episodeCount, 16)
            if let baseline { XCTAssertEqual(detail, baseline) } else { baseline = detail }
        }
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

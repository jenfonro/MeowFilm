import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackBrowsingTests: XCTestCase {
    func testInitialLoadingFollowsTheFirstResolvedDirectoryWithoutAnEarlyEmptyState() async throws {
        var initial = FilmPlaybackFixture.snapshot(stage: "loading").filmObject!
        initial["sources"] = .array([])
        let server = FilmPlaybackWireServer(.object(initial)), model = makeModel(server)
        await model.open()?.value; await server.awaitObservation()
        XCTAssertFalse(model.directoryHasNoVideoSource)
        XCTAssertFalse(model.canSelectFromPresentation)
        try await server.publish(value(10, source: "result-a", line: .number(0)))
        XCTAssertEqual(model.presentation?.browsing.sourceID, "result-a")
        XCTAssertTrue(model.canSelectFromPresentation)
        XCTAssertFalse(model.directoryHasNoVideoSource)
        await model.close()
    }

    func testCompletedUnavailableDisablesPlaybackWithoutUsingMetadataEpisodeCounts() async throws {
        var fields = value(1, source: nil, line: nil).filmObject!
        fields["sources"] = .array([]); fields["searchComplete"] = .bool(true)
        fields["stage"] = .string("unavailable")
        let server = FilmPlaybackWireServer(.object(fields)), model = makeModel(server)
        await model.open()?.value
        XCTAssertFalse(try XCTUnwrap(model.presentation).episodes.isEmpty)
        XCTAssertTrue(model.directoryHasNoVideoSource)
        XCTAssertEqual(model.playbackStatus, .unavailable)
        XCTAssertFalse(model.canStartPlayback)
        model.pauseOrResume()
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("play") })
        await model.close()
    }

    func testSourceAndTypedLineRoundTripsUseOnlyLoadedDisplaySlices() async throws {
        let server = FilmPlaybackWireServer(value(1, source: "result-a", line: .number(0)))
        let model = makeModel(server)
        await model.open()?.value
        let original = try XCTUnwrap(model.presentation)
        await server.reply(to: "browse", with: value(10, source: "result-a", line: .string("0")))
        model.browse(sourceID: "result-a", lineID: .string("0"))
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.loading)
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.files.isEmpty)
        await model.waitForPendingWork()
        let line = try XCTUnwrap(model.presentation)
        model.browse(sourceID: "result-a", lineID: .number(0))
        XCTAssertEqual(model.presentation?.browsing, original.browsing)
        model.browse(sourceID: "result-a", lineID: .string("0"))
        XCTAssertEqual(model.presentation?.browsing, line.browsing)
        await server.reply(to: "browse", with: value(20, source: "result-b", line: .number(0)))
        model.browse(sourceID: "result-b")
        await model.waitForPendingWork()
        model.browse(sourceID: "result-a")
        XCTAssertEqual(model.presentation?.browsing, line.browsing)
        XCTAssertEqual(model.snapshot?.browsing.sourceID, "result-b", "The executor is not a display selector")
        await model.waitForPendingWork()
        let calls = await server.calls().filter { $0.1.filmObject?["action"] == .string("browse") }
        XCTAssertEqual(calls.count, 2, "Loaded A/B returns must not send a browsing RPC")
        let file = try XCTUnwrap(model.presentation?.browsing.files.first)
        model.selectTarget(file.playbackTarget, metadataSourceID: model.presentation?.metadataSourceID)
        await model.waitForPendingWork()
        let selected = await server.calls().last { $0.1.filmObject?["action"] == .string("select.target") }
        XCTAssertEqual(selected?.1.filmObject?["target"], .object(file.playbackTarget.value))
        XCTAssertEqual(selected?.1.filmObject?["metadataSourceId"], .string("confirmed-tmdb"))
        XCTAssertNil(selected?.1.filmObject?["candidateId"])
        XCTAssertNil(selected?.1.filmObject?["episode"])
        await model.close()
    }

    func testMetadataAndSeasonRoundTripsReuseTheirExactSlices() async throws {
        let server = FilmPlaybackWireServer(value(1, source: nil, line: nil))
        let model = makeModel(server)
        await model.open()?.value
        let first = try XCTUnwrap(model.presentation)
        await server.reply(to: "browse.season", with: value(10, source: nil, line: nil, season: 2))
        model.browseSeason(2)
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.loading)
        await model.waitForPendingWork()
        model.browseSeason(1)
        XCTAssertEqual(model.presentation?.episodes, first.episodes)
        await server.reply(to: "browse.metadata", with: value(20, source: nil, line: nil, metadata: .douban))
        model.browseMetadata("confirmed-douban")
        await model.waitForPendingWork()
        model.browseMetadata("confirmed-tmdb")
        XCTAssertEqual(model.presentation?.metadataSourceID, "confirmed-tmdb")
        XCTAssertEqual(model.presentation?.episodes, first.episodes)
        let calls = await server.calls().filter { $0.0 == "session.command" }
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls[0].1.filmObject?["metadataSourceId"], .string("confirmed-tmdb"))
        XCTAssertEqual(calls[0].1.filmObject?["sourceId"], .null)
        XCTAssertEqual(calls[0].1.filmObject?["season"], .number(2))
        await model.close()
    }

    func testLateSuccessUpdatesOnlyItsOwnSliceAndCannotReplaceLocalA() async throws {
        let server = FilmPlaybackWireServer(value(1, source: "result-a", line: .number(0)))
        let model = makeModel(server), entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        await model.open()?.value
        let original = try XCTUnwrap(model.presentation)
        await server.reply(to: "browse", with: value(10, source: "result-b", line: .number(0)))
        await server.beforeAction { action in
            if action == "browse" { await entered.release(); await finish.wait() }
        }
        model.browse(sourceID: "result-b")
        XCTAssertEqual(model.presentation?.browsing.sourceID, "result-b")
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.loading)
        await entered.wait()
        model.browse(sourceID: "result-a")
        XCTAssertEqual(model.presentation?.browsing, original.browsing)
        await finish.release()
        await model.waitForPendingWork()
        XCTAssertEqual(model.presentation?.browsing, original.browsing)
        model.browse(sourceID: "result-b")
        XCTAssertFalse(try XCTUnwrap(model.presentation).browsing.loading)
        let calls = await server.calls().filter { $0.1.filmObject?["action"] == .string("browse") }
        XCTAssertEqual(calls.count, 1)
        await model.close()
    }

    func testLateFailureCannotHideLocalAOrPublishItsActionError() async throws {
        let server = FilmPlaybackWireServer(value(1, source: "result-a", line: .number(0)))
        let model = makeModel(server), entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        await model.open()?.value
        let original = try XCTUnwrap(model.presentation)
        await server.fail("browse", with: .remote("UPSTREAM_ERROR"))
        await server.beforeAction { action in
            if action == "browse" { await entered.release(); await finish.wait() }
        }
        model.browse(sourceID: "result-b")
        await entered.wait()
        model.browse(sourceID: "result-a")
        await finish.release()
        await model.waitForPendingWork()
        XCTAssertEqual(model.presentation?.browsing, original.browsing)
        XCTAssertNil(model.actionError)
        await model.close()
    }

    func testCurrentFailureHasNoFilesAndSuccessfulEmptyIsNotLoading() async throws {
        let server = FilmPlaybackWireServer(value(1, source: "result-a", line: .number(0)))
        let model = makeModel(server)
        await model.open()?.value
        await server.fail("browse", with: .remote("UPSTREAM_ERROR"))
        model.browse(sourceID: "result-b")
        await model.waitForPendingWork()
        XCTAssertEqual(model.presentation?.browsing.error, "UPSTREAM_ERROR")
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.files.isEmpty)
        XCTAssertFalse(try XCTUnwrap(model.presentation).browsing.loading)
        await server.reply(to: "browse.refresh", with: value(10, source: "result-b", line: .number(0), empty: true))
        await model.refreshBrowsing()?.value
        XCTAssertTrue(model.directoryHasNoVideoSource)
        XCTAssertFalse(try XCTUnwrap(model.presentation).browsing.loading)
        XCTAssertNil(model.presentation?.browsing.error)
        await model.close()
    }

    func testRefreshFromLocallyRestoredSliceCarriesAllSelectorsAndHidesOldRows() async throws {
        let server = FilmPlaybackWireServer(value(1, source: "result-a", line: .number(0)))
        let model = makeModel(server), entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        await model.open()?.value
        await server.reply(to: "browse", with: value(10, source: "result-b", line: .string("0")))
        model.browse(sourceID: "result-b")
        await model.waitForPendingWork()
        model.browse(sourceID: "result-a")
        await server.beforeAction { action in
            if action == "browse.refresh" { await entered.release(); await finish.wait() }
        }
        await server.reply(to: "browse.refresh", with: value(20, source: "result-a", line: .number(0)))
        let refresh = model.refreshBrowsing()
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.loading)
        XCTAssertTrue(try XCTUnwrap(model.presentation).browsing.files.isEmpty)
        await entered.wait()
        let call = await server.calls().last!
        XCTAssertEqual(call.1.filmObject?["sourceId"], .string("result-a"))
        XCTAssertEqual(call.1.filmObject?["lineId"], .number(0))
        XCTAssertEqual(call.1.filmObject?["metadataSourceId"], .string("confirmed-tmdb"))
        XCTAssertEqual(call.1.filmObject?["season"], .number(1))
        await finish.release(); await refresh?.value
        XCTAssertFalse(try XCTUnwrap(model.presentation).browsing.loading)
        await model.close()
    }

    func testMetadataBrowsingDoesNotCancelAnAdmittedNativePreparation() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("native")))
        let media = FilmPlaybackTestMedia(), entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        media.preparationEntered = entered; media.preparationGate = finish
        let model = makeModel(server, media: media)
        await model.open()?.value
        await entered.wait()
        let cancellations = media.pendingCancellations
        model.browseMetadata("confirmed-douban")
        await server.awaitCalls(3)
        await finish.release()
        await model.waitForPendingWork()
        XCTAssertEqual(media.pendingCancellations, cancellations)
        XCTAssertTrue(media.cancelled.isEmpty)
        XCTAssertEqual(media.committed, ["native"])
        XCTAssertEqual(model.mediaState.attemptID, "native")
        XCTAssertTrue(media.stopped.isEmpty)
        await model.close()
        XCTAssertEqual(media.nativeReleases, ["native"])
        XCTAssertTrue(model.isReleased)
    }

    func testPlaybackStatusUsesTheCommittedOwnerDespiteBrowsingAndNextPreparation() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("native")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media: media)
        await model.open()?.value; await model.waitForPendingWork(); await server.awaitObservation()
        XCTAssertEqual(model.playbackStatus, .playing)
        model.pauseOrResume()
        XCTAssertEqual(model.playbackStatus, .paused)
        var state = model.mediaState
        state.buffering = true
        media.emit(.state(state), attemptID: "native")
        XCTAssertEqual(model.playbackStatus, .buffering)
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "resolving",
            current: FilmPlaybackFixture.current("native")))
        XCTAssertEqual(model.playbackStatus, .buffering)
        XCTAssertTrue(model.canControlPlayback)
        media.emit(.ended(attemptID: "native"), attemptID: "native")
        XCTAssertEqual(model.playbackStatus, .ended)
        XCTAssertFalse(model.canControlPlayback)
        await model.close()
    }

    func testWorkInfoIsIndependentFromSelectedMetadataAndDoesNotInventTotals() throws {
        let original = FilmDetail(item: .init(sourceID: "tmdb", id: "19", title: "入口", kind: .tv))
        for provider in [FilmMetadataProvider.tmdb, .douban] {
            let snapshot = try FilmPlaybackSnapshot(value: value(1, source: nil, line: nil, metadata: provider))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.title, "主作品")
            XCTAssertEqual(detail.summary, "可靠主简介")
            XCTAssertEqual(detail.seasonCount, 3)
            XCTAssertEqual(detail.episodeCount, 24)
            var fields = try value(1, source: nil, line: nil, metadata: provider).filmRequiredObject()
            var info = try fields.filmValue("workInfo").filmRequiredObject()
            info["seasonCount"] = .null; info["episodeCount"] = .null
            fields["workInfo"] = .object(info)
            let unknown = FilmPlaybackPresentation.detail(try .init(value: .object(fields)), original: original)
            XCTAssertNil(unknown.seasonCount); XCTAssertNil(unknown.episodeCount)
            info["episodeCount"] = .string("24"); fields["workInfo"] = .object(info)
            XCTAssertThrowsError(try FilmPlaybackSnapshot(value: .object(fields)))
        }
    }

    private func makeModel(_ server: FilmPlaybackWireServer,
                           media: FilmPlaybackTestMedia? = nil) -> FilmPlaybackModel {
        let model = FilmPlaybackModel(transport: server.transport, media: media ?? FilmPlaybackTestMedia()) {
            FilmPlaybackFixture.request()
        }
        addTeardownBlock { await model.close() }
        return model
    }

    private func value(_ sequence: Int, source: String?, line: FilmPlaybackIdentifier?,
                       season: Int = 1, metadata: FilmMetadataProvider = .tmdb, empty: Bool = false) -> RuntimeValue {
        var secondSource = FilmPlaybackFixture.source.filmObject!
        secondSource["detailId"] = .string("second-detail")
        let sourceValue = source == "result-b" ? RuntimeValue.object(secondSource) : FilmPlaybackFixture.source
        let file: RuntimeValue = .object(["id": .string("file-\(source ?? "metadata")-\(line?.display ?? "")"),
            "businessId": .string("file-\(source ?? "metadata")-\(String(describing: line))"),
            "playbackTarget": FilmPlaybackFixture.sourceTarget(source: sourceValue,
                lineID: line?.value, candidateID: "exact-file"),
            "candidateId": .string("exact-file"), "name": .string("已解析视频"),
            "season": .number(Double(season)), "episode": .number(1)])
        let browsing: RuntimeValue = .object(["sourceId": source.map(RuntimeValue.string) ?? .null,
            "lineId": line?.value ?? .null, "season": .number(Double(season)),
            "loading": .bool(false), "error": .null, "lines": .array([]),
            "episodes": .array(source == nil || empty ? [] : [file]),
            "originalEpisodes": .array(source == nil || empty ? [] : [file])])
        var fields = FilmPlaybackFixture.snapshot(sequence, browsing: browsing,
            metadata: .object(["title": .string("浏览资料"), "overview": .string("不是主简介")]),
            episodes: [.object(["season": .number(Double(season)), "episode": .number(1)])],
            metadataSources: [FilmMetadataProvider.tmdb, .douban].map { provider in .object([
                "id": .string("confirmed-\(provider.rawValue)"), "provider": .string(provider.rawValue),
                "metadataId": .number(19), "kind": .string("tv"), "title": .string("浏览资料")]) },
            metadataSourceID: "confirmed-\(metadata.rawValue)",
            workInfo: .object(["provider": .string("tmdb"), "metadataId": .number(19), "kind": .string("tv"),
                "title": .string("主作品"), "summary": .string("可靠主简介"),
                "seasonCount": .number(3), "episodeCount": .number(24)])).filmObject!
        fields["sources"] = .array(fields["sources"]!.filmArray! + [.object([
            "businessId": .string("source-second"), "id": .string("result-b"), "title": .string("第二来源"),
            "source": .object(secondSource), "state": .string("ready"), "error": .null,
            "playbackTarget": FilmPlaybackFixture.sourceTarget(source: .object(secondSource))])])
        return .object(fields)
    }
}

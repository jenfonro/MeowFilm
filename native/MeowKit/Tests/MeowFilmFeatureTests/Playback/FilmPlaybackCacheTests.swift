import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackCacheTests: XCTestCase {
    func testCachedAndLoadingDirectoriesCannotReplaceTheCurrentEntryResolution() throws {
        let previous = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot())
        let directory = FilmPlaybackDirectorySlice(previous)
        for state in ["loading", "error", "unmatched"] {
            var fields = try FilmPlaybackFixture.snapshot().filmRequiredObject()
            fields["entryResolution"] = .string(state)
            let current = try FilmPlaybackSnapshot(value: .object(fields))
            XCTAssertEqual(current.retainingLoadedPresentation(previous).entryResolution, state)
            XCTAssertEqual(current.displaying(directory).entryResolution, state)
            XCTAssertEqual(current.loadingDirectory(directory.key).entryResolution, state)
            XCTAssertEqual(current.loadingDirectory(directory.key, error: "UPSTREAM_HTTP").entryResolution, state)
        }
    }
    func testSnapshotConsumesCacheStatusAndComponentsWithoutClearingStaleMetadata() throws {
        let metadata: RuntimeValue = .object(["overview": .string("旧简介")])
        let cache: RuntimeValue = .object([
            "status": .string("stale"), "error": .object(["code": .string("UPSTREAM_HTTP")]),
            "components": .object(["detail": .object(["status": .string("failed"), "fetchedAt": .number(10)]),
                                   "credits": .object(["status": .string("empty")])])
        ])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: metadata, metadataError: "UPSTREAM_HTTP", metadataCache: cache))
        XCTAssertEqual(snapshot.metadata, metadata)
        XCTAssertEqual(snapshot.metadataCache?.status, .stale)
        XCTAssertEqual(snapshot.metadataCache?.errorCode, "UPSTREAM_HTTP")
        XCTAssertEqual(snapshot.metadataCache?.components["credits"], .object(["status": .string("empty")]))
        XCTAssertEqual(snapshot.metadataError, "UPSTREAM_HTTP")
        XCTAssertNil(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot()).metadataCache)
        let unknown = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: metadata, metadataCache: .object(["status": .string("unknown"), "components": .object([:])])))
        XCTAssertEqual(unknown.metadata, metadata)
        XCTAssertEqual(unknown.metadataCache?.status, .stale)
    }

    func testUnreadableAuxiliaryCacheEvidenceIsConservativelyStaleWithoutRejectingPlayback() throws {
        let metadata: RuntimeValue = .object(["overview": .string("主资料")])
        for cache in [
            RuntimeValue.string("unrecognized cache"),
            .object(["status": .number(1), "components": .object([:])]),
            .object(["status": .string("fresh"), "components": .array([])]),
            .object(["components": .object([:]), "error": .object(["code": .array([])])]),
        ] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                current: FilmPlaybackFixture.current("current"), metadata: metadata, metadataCache: cache))
            XCTAssertEqual(snapshot.sessionID, FilmPlaybackFixture.sessionID)
            XCTAssertEqual(snapshot.current?.attemptID, "current")
            XCTAssertEqual(snapshot.metadata, metadata)
            XCTAssertEqual(snapshot.metadataCache?.status, .stale)
            XCTAssertNil(snapshot.metadataCache?.errorCode)
        }
        let fresh = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadataCache: .object(["status": .string("fresh"), "components": .object([:]),
                                    "error": .object(["code": .array([])])])))
        XCTAssertEqual(fresh.metadataCache?.status, .fresh)
        XCTAssertNil(fresh.metadataCache?.errorCode)
    }

    func testUnknownCacheStatusRetainsIndependentComponentCompletionEvidence() throws {
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["name": .string("浏览资料")]),
            metadataCache: .object(["status": .string("refreshing"),
                                    "components": .object(["detail": .object(["status": .string("ready")])])]),
            workInfo: FilmPlaybackFixture.workInfo(title: "新资料")))
        let detail = FilmPlaybackPresentation.detail(snapshot, original: originalDetail())
        XCTAssertEqual(snapshot.metadataCache?.status, .stale)
        XCTAssertEqual(detail.title, "新资料")
        XCTAssertTrue(detail.summary.isEmpty, "A completed component must not revive omitted old content.")
        XCTAssertNil(detail.item.posterURL)
    }

    func testOrdinaryReentryAndExplicitRefreshLeaveCurrentMediaAndPositionUntouched() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let current = try XCTUnwrap(model.snapshot?.current)
        let target = try XCTUnwrap(model.snapshot?.target)
        let stopped = media.stopped
        let cancellations = media.pendingCancellations
        await server.reply(to: "browse.refresh", with: FilmPlaybackFixture.snapshot(100, stage: "prepared",
            current: FilmPlaybackFixture.current("current"), metadata: .object(["overview": .string("新简介")]),
            metadataCache: .object(["status": .string("fresh"), "components": .object([:])])))
        await model.open()?.value
        await model.refreshBrowsing()?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.current, current)
        XCTAssertEqual(model.snapshot?.target, target)
        XCTAssertEqual(media.prepared.map(\.attemptID), ["current"])
        XCTAssertEqual(media.committed, ["current"])
        XCTAssertEqual(media.stopped, stopped)
        XCTAssertEqual(media.pendingCancellations, cancellations)
        let calls = await server.calls()
        let checks = calls.filter { $0.1.filmObject?["action"] == .string("browse.refresh") }
        XCTAssertEqual(checks.count, 1)
        XCTAssertNil(checks[0].1.filmObject?["refresh"])
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("play") })
        await model.close()
    }

    func testBrowseRefreshFailurePreservesCurrentAndReportsExistingActionError() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let before = model.snapshot
        let stopped = media.stopped
        await server.fail("browse.refresh", with: .remote("NETWORK_ERROR"))
        await model.refreshBrowsing()?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot, before)
        XCTAssertEqual(model.actionError?.code, "NETWORK_ERROR")
        XCTAssertEqual(media.stopped, stopped)
        XCTAssertEqual(media.prepared.map(\.attemptID), ["current"])
        await model.close()
    }

    func testAddressRetryCarriesPendingCurrentOrLastFailedIdentitySuppliedByOwner() async throws {
        let values = [
            FilmPlaybackFixture.snapshot(stage: "prepared", attempt: FilmPlaybackFixture.attempt("pending", prepareOnly: true)),
            FilmPlaybackFixture.snapshot(stage: "playing", current: FilmPlaybackFixture.current("current")),
            FilmPlaybackFixture.snapshot(stage: "unavailable", retryMediaAttemptID: "failed")
        ]
        for (value, expected) in zip(values, [RuntimeValue.string("pending"), .string("current"), .string("failed")]) {
            let server = FilmPlaybackWireServer(value)
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            XCTAssertTrue(model.canRetryMedia)
            await model.retryMedia()?.value
            await model.retryMedia(attemptID: "captured")?.value
            let retries = await server.calls().filter { $0.1.filmObject?["action"] == .string("media.retry") }
            XCTAssertEqual(retries.count, 2)
            XCTAssertEqual(retries[0].1.filmObject?["attemptId"], expected)
            XCTAssertEqual(retries[1].1.filmObject?["attemptId"], .string("captured"))
            XCTAssertFalse(retries.contains { $0.1.filmObject?["candidateId"] != nil })
            await model.close()
        }
    }

    func testInitializationFailureHasNoMediaRetryAndRefreshUsesTheLoadingOwner() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "error",
            initializationError: "NOT_CONFIGURED"))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        XCTAssertEqual(model.snapshot?.initializationError, "NOT_CONFIGURED")
        XCTAssertFalse(model.canStartPlayback)
        XCTAssertFalse(model.canRetryMedia)
        XCTAssertNil(model.retryMedia())
        await model.refreshBrowsing()?.value
        let actions = await server.calls().filter { $0.0 == "session.command" }
        XCTAssertTrue(actions.contains { $0.1.filmObject?["action"] == .string("browse.refresh") })
        XCTAssertFalse(actions.contains { $0.1.filmObject?["action"] == .string("media.retry") })
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testRejectedStaleRetryKeepsTheAdmittedPendingAndCurrentUntilTheOriginalPendingCommits() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        let preparation = FilmPlaybackGate(), preparationEntered = FilmPlaybackGate()
        media.preparationGate = preparation; media.preparationEntered = preparationEntered
        try await server.publish(FilmPlaybackFixture.snapshot(Int(try XCTUnwrap(model.snapshot).sequence) + 1,
            stage: "pending", attempt: FilmPlaybackFixture.attempt("pending"),
            current: FilmPlaybackFixture.current("current")))
        await preparationEntered.wait()
        let cancellations = media.pendingCancellations
        let retryEntered = FilmPlaybackGate(), retryReply = FilmPlaybackGate()
        await server.beforeAction { action in
            if action == "media.retry" { await retryEntered.release(); await retryReply.wait() }
        }
        await server.fail("media.retry", with: .remote("STALE_SESSION"))
        let retry = model.retryMedia(attemptID: "stale")
        await retryEntered.wait()
        XCTAssertEqual(model.snapshot?.attempt?.id, "pending")
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        XCTAssertEqual(model.mediaState.attemptID, "current")
        XCTAssertEqual(media.pendingCancellations, cancellations)
        XCTAssertTrue(media.cancelled.isEmpty)
        XCTAssertTrue(media.stopped.isEmpty)
        await retryReply.release()
        await retry?.value
        XCTAssertEqual(model.actionError?.code, "STALE_SESSION")
        XCTAssertEqual(model.snapshot?.attempt?.id, "pending")
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        XCTAssertEqual(model.mediaState.attemptID, "current")
        XCTAssertEqual(media.pendingCancellations, cancellations)
        XCTAssertTrue(media.nativeReleases.isEmpty)
        await preparation.release()
        await model.waitForPendingWork()
        XCTAssertEqual(media.prepared.map(\.attemptID), ["current", "pending"])
        XCTAssertEqual(media.committed, ["current", "pending"])
        XCTAssertTrue(media.cancelled.isEmpty)
        XCTAssertEqual(model.snapshot?.current?.attemptID, "pending")
        let retries = await server.calls().filter { $0.1.filmObject?["action"] == .string("media.retry") }
        XCTAssertEqual(retries.count, 1)
        XCTAssertEqual(retries[0].1.filmObject?["attemptId"], .string("stale"))
        await model.close()
    }

    func testCurrentAddressRetryChangesOutputOnlyAfterAcceptanceAndPreservesPosition() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        var state = model.mediaState
        state.position = 123; state.paused = true
        media.emit(.state(state), attemptID: "current")
        await model.waitForPendingWork()
        var current = try FilmPlaybackFixture.current("current").filmRequiredObject()
        current["position"] = .number(123); current["paused"] = .bool(true)
        var attempt = try FilmPlaybackFixture.attempt("new-address").filmRequiredObject()
        attempt["position"] = .number(123); attempt["paused"] = .bool(true)
        await server.replyToReceipt { id in
            var input = try FilmPlaybackFixture.mediaInput(id).filmRequiredObject()
            input["url"] = .string("https://media.example.invalid/refreshed.mp4")
            input["position"] = .number(123); input["paused"] = .bool(true)
            return .object(input)
        }
        await server.reply(to: "media.retry", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: .object(attempt), current: .object(current)))
        let retryEntered = FilmPlaybackGate(), retryReply = FilmPlaybackGate()
        await server.beforeAction { action in
            if action == "media.retry" { await retryEntered.release(); await retryReply.wait() }
        }
        let preparation = FilmPlaybackGate(), preparationEntered = FilmPlaybackGate()
        media.preparationGate = preparation; media.preparationEntered = preparationEntered
        let cancellations = media.pendingCancellations
        let retry = model.retryMedia()
        await retryEntered.wait()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        XCTAssertEqual(model.mediaState.position, 123)
        XCTAssertEqual(media.committed, ["current"])
        XCTAssertEqual(media.pendingCancellations, cancellations)
        XCTAssertTrue(media.stopped.isEmpty)
        await retryReply.release()
        await retry?.value
        await preparationEntered.wait()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        XCTAssertEqual(model.snapshot?.attempt?.id, "new-address")
        XCTAssertEqual(model.mediaState.attemptID, "current")
        XCTAssertEqual(model.mediaState.position, 123)
        XCTAssertEqual(media.committed, ["current"])
        await preparation.release()
        await model.waitForPendingWork()
        XCTAssertEqual(media.prepared.map(\.attemptID), ["current", "new-address"])
        XCTAssertEqual(media.prepared.map { $0.url.absoluteString }, [
            "https://media.example.invalid/video.mp4", "https://media.example.invalid/refreshed.mp4"
        ])
        XCTAssertEqual(media.committed, ["current", "new-address"])
        XCTAssertEqual(media.preservedPosition, [false, true])
        XCTAssertEqual(model.snapshot?.current?.attemptID, "new-address")
        XCTAssertEqual(model.mediaState.attemptID, "new-address")
        XCTAssertEqual(model.mediaState.position, 123)
        XCTAssertTrue(model.mediaState.paused)
        XCTAssertTrue(media.stopped.isEmpty)
        XCTAssertNil(model.actionError)
        let retries = await server.calls().filter { $0.1.filmObject?["action"] == .string("media.retry") }
        XCTAssertEqual(retries.count, 1)
        XCTAssertEqual(retries[0].1.filmObject?["attemptId"], .string("current"))
        await model.close()
    }

    func testCompleteEmptyWorkProjectionClearsOldFieldsAndFailureRetainsThatResult() throws {
        let original = originalDetail()
        for metadata in [RuntimeObject(), ["poster_path": .null, "credits": .object(["cast": .array([])]),
            "similar": .object(["results": .array([])]), "images": .object(["logos": .array([])])]] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["poster_path": .string("https://artwork.invalid/browse.jpg")]),
                workInfo: FilmPlaybackFixture.workInfo(title: "稳定作品", metadata: metadata)))
            let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(detail.item.identity, original.item.identity)
            XCTAssertTrue(detail.summary.isEmpty); XCTAssertTrue(detail.item.score.isEmpty)
            XCTAssertNil(detail.item.posterURL); XCTAssertNil(detail.item.largePosterURL)
            XCTAssertNil(detail.backdropURL); XCTAssertNil(detail.logoURL); XCTAssertNil(detail.releaseDate)
            XCTAssertNil(detail.seasonCount); XCTAssertNil(detail.episodeCount)
            XCTAssertTrue(detail.cast.isEmpty); XCTAssertTrue(detail.similarItems.isEmpty); XCTAssertTrue(detail.seasons.isEmpty)
            let failed = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadataError: "UPSTREAM_HTTP"))
                .retainingLoadedPresentation(snapshot)
            XCTAssertEqual(failed.workInfo, snapshot.workInfo)
            XCTAssertEqual(FilmPlaybackPresentation.detail(failed, original: original), detail)
        }
    }

    func testNoWorkProjectionRetainsEntryWithoutUsingBrowseSupplement() throws {
        let original = originalDetail(provider: "douban", id: "100")
        let supplemental: RuntimeValue = .object(["provider": .string("tmdb"), "metadataId": .number(999),
            "kind": .string("tv"), "metadata": .object(["title": .string("其他标题"),
                "poster_path": .string("https://artwork.invalid/browse.jpg"),
                "credits": .object(["cast": .array([.object(["name": .string("浏览演员")])])])])])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["title": .string("浏览标题")]), metadataSupplement: supplemental))
        XCTAssertEqual(FilmPlaybackPresentation.detail(snapshot, original: original), original)
    }

    func testDoubanDirectorySeasonsUseTheirOwnRowsNotWorkOrSupplement() throws {
        let target = FilmPlaybackTarget(id: "douban:tv:100", kind: .tv, title: "豆瓣",
            metadataProvider: .douban, metadataID: "100", position: .init(season: 2))
        let row: RuntimeValue = .object(["season_number": .number(2), "name": .string("目录季度"),
            "businessId": .string("douban:tv:100:season:2"), "playbackTarget": .object(target.value)])
        for rows in [[row], []] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["doubanSeasons": .array(rows), "seasons": .array([.object(["title": .string("其他条目")])])]),
                episodes: [.object(["season": .number(2), "episode": .number(7), "name": .string("目录集")])],
                target: .object(target.value), workInfo: FilmPlaybackFixture.workInfo(title: "主作品")))
            let seasons = FilmPlaybackPresentation.seasons(snapshot)
            XCTAssertEqual(seasons.map(\.number), rows.isEmpty ? [] : [2])
            XCTAssertEqual(seasons.first?.episodes.map(\.number) ?? [], rows.isEmpty ? [] : [7])
            XCTAssertTrue(FilmPlaybackPresentation.detail(snapshot, original: originalDetail()).seasons.isEmpty)
        }
    }

    private func makeModel(_ server: FilmPlaybackWireServer, _ media: FilmPlaybackTestMedia) -> FilmPlaybackModel {
        .init(transport: server.transport, media: media,
              request: { FilmPlaybackFixture.request() })
    }

    private func originalDetail(provider: String = "tmdb", id: String = "19") -> FilmDetail {
        .init(item: .init(sourceID: provider, id: id, title: "旧标题", kind: .tv,
            posterURL: URL(string: "https://artwork.invalid/old.jpg"),
            largePosterURL: URL(string: "https://artwork.invalid/old-large.jpg"), score: "8.0"),
            backdropURL: URL(string: "https://artwork.invalid/old-backdrop.jpg"),
            logoURL: URL(string: "https://artwork.invalid/old-logo.png"), summary: "旧简介",
            releaseDate: "2025-01-01", seasonCount: 3, episodeCount: 8,
            seasons: [.init(id: "old-season", title: "旧季度", number: 1)],
            cast: [.init(id: "old-cast", name: "旧演员")],
            similarItems: [.init(sourceID: provider, id: "20", title: "旧关联", kind: .tv)])
    }
}

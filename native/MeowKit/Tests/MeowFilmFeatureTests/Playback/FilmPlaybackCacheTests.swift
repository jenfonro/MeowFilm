import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackCacheTests: XCTestCase {
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
        XCTAssertThrowsError(try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadataCache: .object(["status": .string("unknown"), "components": .object([:])]))))
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
        XCTAssertEqual(checks.count, 2)
        XCTAssertEqual(checks[0].1.filmObject?["refresh"], .bool(false))
        XCTAssertNil(checks[1].1.filmObject?["refresh"])
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

    func testAddressRetryCarriesPendingCurrentOrExplicitIdentityAndLeavesLastFailedSelectionToOwner() async throws {
        let values = [
            FilmPlaybackFixture.snapshot(stage: "prepared", attempt: FilmPlaybackFixture.attempt("pending", prepareOnly: true)),
            FilmPlaybackFixture.snapshot(stage: "playing", current: FilmPlaybackFixture.current("current")),
            FilmPlaybackFixture.snapshot(stage: "unavailable")
        ]
        for (value, expected) in zip(values, [RuntimeValue.string("pending"), .string("current"), .null]) {
            let server = FilmPlaybackWireServer(value)
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            await model.retryMedia()?.value
            await model.retryMedia(attemptID: "captured")?.value
            let retries = await server.calls().filter { $0.1.filmObject?["action"] == .string("media.retry") }
            XCTAssertEqual(retries.count, 2)
            XCTAssertEqual(retries[0].1.filmObject?["attemptId"], expected == .null ? nil : expected)
            XCTAssertEqual(retries[1].1.filmObject?["attemptId"], .string("captured"))
            XCTAssertFalse(retries.contains { $0.1.filmObject?["candidateId"] != nil })
            await model.close()
        }
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

    func testSuccessfulEmptyFieldsAndCreditsRemoveOldPresentationWhileMissingFieldsKeepIt() throws {
        let original = originalDetail()
        let empty: RuntimeValue = .object([
            "overview": .string(""), "poster_path": .null, "backdrop_path": .null,
            "images": .object(["logos": .array([])]),
            "credits": .object(["crew": .array([]), "cast": .array([])]),
            "similar": .object(["results": .array([])]),
            "seasons": .array([]), "number_of_seasons": .null, "number_of_episodes": .null,
            "release_date": .null, "vote_average": .null
        ])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: empty, metadataCache: .object(["status": .string("fresh"), "components": .object([
                "detail": .object(["status": .string("ready")]), "credits": .object(["status": .string("empty")])
            ])])))
        let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
        XCTAssertEqual(detail.item.identity, original.item.identity)
        XCTAssertTrue(detail.summary.isEmpty)
        XCTAssertNil(detail.item.posterURL)
        XCTAssertNil(detail.item.largePosterURL)
        XCTAssertNil(detail.backdropURL)
        XCTAssertNil(detail.logoURL)
        XCTAssertTrue(detail.cast.isEmpty)
        XCTAssertTrue(detail.similarItems.isEmpty)
        XCTAssertTrue(detail.seasons.isEmpty)
        XCTAssertNil(detail.seasonCount)
        XCTAssertNil(detail.episodeCount)
        XCTAssertNil(detail.releaseDate)
        XCTAssertTrue(detail.item.score.isEmpty)
        let missing = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadataError: "UPSTREAM_HTTP",
            metadataCache: .object(["status": .string("stale"), "error": .object(["code": .string("UPSTREAM_HTTP")]),
                                   "components": .object(["detail": .object(["status": .string("failed")])])])))
        let kept = FilmPlaybackPresentation.detail(missing, original: original)
        XCTAssertEqual(kept.summary, original.summary)
        XCTAssertEqual(kept.item.posterURL, original.item.posterURL)
        XCTAssertEqual(kept.cast, original.cast)
        XCTAssertEqual(kept.seasons, original.seasons)
    }

    func testSuccessfulDetailOmissionsClearOldFieldsAndLaterFailureCannotReviveThem() throws {
        let original = originalDetail()
        for detail in [
            RuntimeValue.object(["status": .string("ready")]),
            .object(["status": .string("empty")]),
            .object(["status": .string("failed"), "fetchedAt": .number(10)])
        ] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["name": .string("新资料")]),
                metadataCache: .object(["status": .string("stale"),
                                       "components": .object(["detail": detail])])))
            let projected = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertTrue(projected.summary.isEmpty)
            XCTAssertNil(projected.item.posterURL)
            XCTAssertNil(projected.item.largePosterURL)
            XCTAssertNil(projected.backdropURL)
            XCTAssertTrue(projected.item.score.isEmpty)
            XCTAssertNil(projected.releaseDate)
            XCTAssertNil(projected.seasonCount)
            XCTAssertNil(projected.episodeCount)
            XCTAssertEqual(projected.logoURL, original.logoURL, "Unrequested append data owns separate success evidence.")
            XCTAssertEqual(projected.cast, original.cast)
            XCTAssertEqual(projected.similarItems, original.similarItems)
        }
        let failed = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["name": .string("摘要")]), metadataError: "UPSTREAM_HTTP",
            metadataCache: .object(["status": .string("stale"), "components": .object([
                "detail": .object(["status": .string("failed")])
            ])])))
        let retained = FilmPlaybackPresentation.detail(failed, original: original)
        XCTAssertEqual(retained.summary, original.summary)
        XCTAssertEqual(retained.item.posterURL, original.item.posterURL)
        XCTAssertEqual(retained.backdropURL, original.backdropURL)
        XCTAssertEqual(retained.releaseDate, original.releaseDate)
        XCTAssertEqual(retained.seasonCount, original.seasonCount)
        XCTAssertEqual(retained.episodeCount, original.episodeCount)
    }

    func testAppendSuccessAndFailureUseTheirOwnEvidenceWhenFieldsAreOmitted() throws {
        let original = originalDetail()
        for component in [
            RuntimeValue.object(["status": .string("ready")]),
            .object(["status": .string("empty")]),
            .object(["status": .string("failed"), "fetchedAt": .number(10)])
        ] {
            let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
                metadata: .object(["name": .string("摘要")]),
                metadataCache: .object(["status": .string("stale"), "components": .object([
                    "detail": .object(["status": .string("failed")]), "credits": component,
                    "images": component, "similar": component, "seasons": component
                ])])))
            let projected = FilmPlaybackPresentation.detail(snapshot, original: original)
            XCTAssertEqual(projected.summary, original.summary)
            XCTAssertEqual(projected.item.posterURL, original.item.posterURL)
            XCTAssertNil(projected.logoURL)
            XCTAssertTrue(projected.cast.isEmpty)
            XCTAssertTrue(projected.similarItems.isEmpty)
            XCTAssertTrue(projected.seasons.isEmpty)
        }
        let missing = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            metadata: .object(["name": .string("摘要")]),
            metadataCache: .object(["status": .string("stale"), "components": .object([
                "credits": .object(["status": .string("failed")]),
                "images": .object(["status": .string("missing")]),
                "similar": .object(["status": .string("failed")])
            ])])))
        let retained = FilmPlaybackPresentation.detail(missing, original: original)
        XCTAssertEqual(retained.logoURL, original.logoURL)
        XCTAssertEqual(retained.cast, original.cast)
        XCTAssertEqual(retained.similarItems, original.similarItems)
    }

    func testReliableTMDBSupplementFillsDoubanDisplayWithoutChangingIdentityCountsOrEpisodes() throws {
        let target = FilmPlaybackTarget(id: "douban:tv:100", kind: .tv, title: "豆瓣",
            metadataProvider: .douban, metadataID: "100", position: .init(season: 2, episode: 7))
        let main: RuntimeValue = .object([
            "title": .string("豆瓣主资料"), "intro": .string(""), "pic": .null, "backdrop_path": .null,
            "images": .object(["logos": .array([])]), "directors": .array([]), "actors": .array([]),
            "similar": .object(["results": .array([])]), "episodes_count": .number(9),
            "seasons": .array([.object(["season_number": .number(2), "name": .string("主季度")])])
        ])
        let supplement: RuntimeValue = .object([
            "provider": .string("tmdb"), "metadataId": .number(999), "kind": .string("tv"),
            "metadata": .object([
                "overview": .string("补充简介"), "poster_path": .string("/v1/tmdb/images/original/extra.jpg"),
                "backdrop_path": .string("/v1/tmdb/images/original/backdrop.jpg"),
                "images": .object(["logos": .array([.object(["file_path": .string("/v1/tmdb/images/original/logo.png")])])]),
                "credits": .object(["cast": .array([.object(["id": .number(1), "name": .string("补充演员")])])]),
                "similar": .object(["results": .array([.object(["id": .number(1000), "name": .string("补充关联")])])]),
                "seasons": .array([.object(["season_number": .number(99)])]),
                "number_of_seasons": .number(100), "number_of_episodes": .number(999)
            ])
        ])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadata: main,
            episodes: [.object(["season": .number(2), "episode": .number(7), "name": .string("主集")])],
            target: .object(target.value), metadataSupplement: supplement))
        let original = originalDetail(provider: "douban", id: "100")
        let detail = FilmPlaybackPresentation.detail(snapshot, original: original)
        XCTAssertEqual(detail.item.identity, original.item.identity)
        XCTAssertEqual(detail.title, "豆瓣主资料")
        XCTAssertEqual(detail.summary, "补充简介")
        XCTAssertEqual(detail.item.posterURL?.absoluteString, "/v1/tmdb/images/original/extra.jpg")
        XCTAssertEqual(detail.backdropURL?.absoluteString, "/v1/tmdb/images/original/backdrop.jpg")
        XCTAssertEqual(detail.logoURL?.absoluteString, "/v1/tmdb/images/original/logo.png")
        XCTAssertEqual(detail.cast.map(\.name), ["补充演员"])
        XCTAssertEqual(detail.similarItems.first?.identity, .init(sourceID: "tmdb", itemID: "1000", kind: .tv))
        XCTAssertEqual(detail.episodeCount, 9)
        XCTAssertEqual(detail.seasonCount, original.seasonCount)
        XCTAssertEqual(detail.seasons.map(\.number), [2])
        XCTAssertEqual(detail.seasons.first?.episodes.map(\.number), [7])
        XCTAssertEqual(snapshot.target.position, target.position)

        var associated = try main.filmRequiredObject()
        associated["doubanSeasons"] = associated["seasons"]
        associated["seasons"] = .array([.object([
            "id": .string("101"), "title": .string("关联豆瓣条目")
        ])])
        let associatedSnapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadata: .object(associated),
            episodes: [.object(["season": .number(2), "episode": .number(7), "name": .string("主集")])],
            target: .object(target.value), metadataSupplement: supplement))
        let associatedDetail = FilmPlaybackPresentation.detail(associatedSnapshot, original: original)
        XCTAssertEqual(associatedDetail.seasons.map(\.number), [2])
        XCTAssertEqual(associatedDetail.seasons.map(\.title), ["主季度"])
        XCTAssertEqual(associatedDetail.seasons.first?.episodes.map(\.number), [7])
        XCTAssertEqual(associatedDetail.seasons.first?.episodes.map(\.title), ["主集"])

        var emptySeasons = try main.filmRequiredObject()
        emptySeasons["doubanSeasons"] = .array([])
        let emptySnapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadata: .object(emptySeasons),
            target: .object(target.value), metadataSupplement: supplement))
        XCTAssertTrue(FilmPlaybackPresentation.detail(emptySnapshot, original: original).seasons.isEmpty)

        let cleared = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadata: main,
            target: .object(target.value)))
        let withoutSupplement = FilmPlaybackPresentation.detail(cleared, original: original)
        XCTAssertTrue(withoutSupplement.summary.isEmpty)
        XCTAssertNil(withoutSupplement.item.posterURL)
        XCTAssertNil(withoutSupplement.item.largePosterURL)
        XCTAssertTrue(withoutSupplement.cast.isEmpty)
        XCTAssertTrue(withoutSupplement.similarItems.isEmpty)

        var populated = try main.filmRequiredObject()
        populated["intro"] = .string("主简介")
        populated["pic"] = .object(["normal": .string("https://artwork.invalid/main.jpg"),
                                   "large": .string("https://artwork.invalid/main-large.jpg")])
        populated["backdrop_path"] = .string("https://artwork.invalid/main-backdrop.jpg")
        populated["images"] = .object(["logos": .array([.object(["file_path": .string("https://artwork.invalid/main-logo.png")])])])
        populated["actors"] = .array([.string("主演")])
        populated["similar"] = .object(["results": .array([.object(["id": .string("main-similar"), "title": .string("主关联")])])])
        let nonempty = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(metadata: .object(populated),
            target: .object(target.value), metadataSupplement: supplement))
        let primary = FilmPlaybackPresentation.detail(nonempty, original: original)
        XCTAssertEqual(primary.summary, "主简介")
        XCTAssertEqual(primary.item.posterURL?.absoluteString, "https://artwork.invalid/main.jpg")
        XCTAssertEqual(primary.item.largePosterURL?.absoluteString, "https://artwork.invalid/main-large.jpg")
        XCTAssertEqual(primary.backdropURL?.absoluteString, "https://artwork.invalid/main-backdrop.jpg")
        XCTAssertEqual(primary.logoURL?.absoluteString, "https://artwork.invalid/main-logo.png")
        XCTAssertEqual(primary.cast.map(\.name), ["主演"])
        XCTAssertEqual(primary.similarItems.map(\.id), ["main-similar"])
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

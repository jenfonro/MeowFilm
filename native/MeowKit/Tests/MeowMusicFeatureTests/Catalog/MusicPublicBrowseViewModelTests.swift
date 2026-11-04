import XCTest
import Foundation
import MeowMusicFeature

private actor MusicPublicBrowseGate {
    private var started = false, released = false
    private var startWaiters: [CheckedContinuation<Void, Never>] = []
    private var endWaiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        started = true; startWaiters.forEach { $0.resume() }; startWaiters.removeAll()
        if !released { await withCheckedContinuation { endWaiters.append($0) } }
    }
    func didStart() async { if !started { await withCheckedContinuation { startWaiters.append($0) } } }
    func release() { released = true; endWaiters.forEach { $0.resume() }; endWaiters.removeAll() }
}
private actor MusicPublicBrowseFeatureService: MusicPublicBrowsing, MusicPublicBrowseQueueLoading {
    static let collection = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", title: "Synthetic playlist")
    static func track(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .handle, id: id), songId: "song:" + id, title: id,
              sources: [.init(sourceKey: "wy:" + id, platform: "wy", nativeId: id)])
    }
    var gate: MusicPublicBrowseGate?
    var error: MusicServiceError?
    var duplicateCursor = false
    var longCycle = false
    var longPages = false
    var emptyDetailFirst = false
    var detailCount = 1, detailPageSize = 30
    var openedQueues = 0
    var queries: [String] = []
    var cursors: [String?] = []
    var platforms: [MusicPublicBrowsePlatform] = []
    func suspend(_ gate: MusicPublicBrowseGate) { self.gate = gate }
    func fail(_ error: MusicServiceError?) { self.error = error }
    func loopCursor() { duplicateCursor = true }
    func loopMultiplePages() { longCycle = true }
    func keepPaging() { longPages = true }
    func omitFirstDetailPage() { emptyDetailFirst = true }
    func keepDetailPaging(count: Int, pageSize: Int = 30) { detailCount = count; detailPageSize = pageSize }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) async throws -> MusicPublicBrowseCollectionPage {
        queries.append(query); cursors.append(cursor); platforms.append(platform)
        if let gate { await gate.wait() }
        if let error { throw error }
        let row = MusicPublicBrowseCollection(platform: platform, nativeID: cursor == nil ? "1" : "2", title: query)
        if longCycle { return .init(items: [row], total: 90, cursor: cursor == nil ? "a" : cursor == "a" ? "b" : "a") }
        if longPages {
            let page = cursor.flatMap(Int.init) ?? 1
            return .init(items: [row], total: 201, cursor: page < 201 ? String(page + 1) : nil)
        }
        return .init(items: [row], total: 2, cursor: cursor == nil || duplicateCursor ? "next" : nil)
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail {
        cursors.append(cursor)
        if emptyDetailFirst {
            let members: [MusicPublicBrowseMember] = cursor == nil ? [] :
                [.init(nativeID: "31", sourcePosition: 30, track: Self.track("31"))]
            return .init(collection: collection, members: members, total: 31,
                         cursor: cursor == nil ? "raw30" : nil, snapshot: String(repeating: "a", count: 64),
                         notice: cursor == nil ? "部分歌曲当前不可见或不可用" : nil, consumed: cursor == nil ? 30 : 1)
        }
        if detailCount > 1 {
            let start = cursor.flatMap(Int.init) ?? 0, end = min(start + detailPageSize, detailCount)
            return .init(collection: collection, members: (start..<end).map {
                .init(nativeID: String($0 + 1), sourcePosition: $0, track: Self.track(String($0 + 1)))
            }, total: detailCount, cursor: end < detailCount ? String(end) : nil,
               snapshot: String(repeating: "a", count: 64))
        }
        return .init(collection: collection, members: [.init(nativeID: "1", sourcePosition: 0, track: Self.track("1"))],
                     total: 1, cursor: nil, snapshot: String(repeating: "a", count: 64))
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        if let gate { await gate.wait() }
        if let error { throw error }
        return .init(sections: [.init(id: "home", title: "Synthetic", items: [
            .init(id: "1", title: "Song", content: .track(Self.track("1")))
        ])])
    }
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) -> [MusicNativeTrackData] { [] }
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) -> MusicRemoteQueueSource {
        openedQueues += 1
        return .init(context: .init(kind: .playlist, id: "wy:playlist:41", title: "Synthetic", platform: "wy"),
              loadPage: { _ in .init(items: []) })
    }
}

@MainActor final class MusicPublicBrowseViewModelTests: XCTestCase {
    private func model(_ service: MusicPublicBrowseFeatureService,
                       request: MusicPublicBrowseRequest = .playlists(.netease, query: "Synthetic")) -> MusicPublicBrowseViewModel {
        let model = MusicPublicBrowseViewModel(service: service, request: request)
        model.setAccess(available: true, generation: UUID()); model.appear()
        return model
    }
    func testBoundedPaginationReusesLoadedPreviousAndNextPagesUntilExplicitRefresh() async throws {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        await model.refresh()
        XCTAssertEqual(model.pageNumber, 1); XCTAssertEqual(model.sections[0].items[0].id, "wy:playlist:1")
        await model.next()
        XCTAssertEqual(model.pageNumber, 2); XCTAssertEqual(model.sections.count, 1)
        XCTAssertEqual(model.sections[0].items[0].id, "wy:playlist:2"); XCTAssertNil(model.nextCursor)
        await model.previous()
        XCTAssertEqual(model.pageNumber, 1); XCTAssertEqual(model.sections[0].items[0].id, "wy:playlist:1")
        await model.next()
        var cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "next"])
        await model.refresh()
        await model.next()
        cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "next", nil, "next"])
    }
    func testNonCooperativePageCannotPublishAfterDepartureOrProtectionChange() async throws {
        let service = MusicPublicBrowseFeatureService(), gate = MusicPublicBrowseGate()
        await service.suspend(gate)
        let model = model(service)
        let task = Task { await model.refresh() }
        await gate.didStart()
        model.disappear(); model.setAccess(available: false, generation: UUID())
        await gate.release(); await task.value
        XCTAssertTrue(model.sections.isEmpty); XCTAssertFalse(model.loaded); XCTAssertFalse(model.isLoading)
        XCTAssertNil(model.error)
    }
    func testSameCursorIsExplicitErrorAndDoesNotAdvancePage() async {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        await service.loopCursor(); await model.refresh(); await model.next()
        XCTAssertEqual(model.error, .invalidResponse); XCTAssertEqual(model.pageNumber, 1)
    }
    func testAuthErrorClearsOldContentAndNeverChangesPlatform() async {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        await model.refresh(); await service.fail(.authRequired); await model.next()
        XCTAssertTrue(model.sections.isEmpty); XCTAssertNil(model.nextCursor); XCTAssertEqual(model.error, .authRequired)
        XCTAssertEqual(model.request, .playlists(.netease, query: "Synthetic"))
        await service.fail(nil); await model.refresh(); await model.next()
        let cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "next", nil, "next"])
    }
    func testExplicitActionIsSingleUseAndBlockedAfterRefreshOrPageDeparture() async throws {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        var calls = 0
        await model.refresh(); XCTAssertEqual(calls, 0)
        let intent = try XCTUnwrap(model.makeActionIntent())
        await model.perform(intent: intent) { calls += 1 }; XCTAssertEqual(calls, 1)
        await model.perform(intent: intent) { calls += 1 }; XCTAssertEqual(calls, 1)
        let oldPageIntent = try XCTUnwrap(model.makeActionIntent())
        await model.refresh()
        await model.perform(intent: oldPageIntent) { calls += 1 }; XCTAssertEqual(calls, 1)
        let last = try XCTUnwrap(model.makeActionIntent())
        model.disappear()
        await model.perform(intent: last) { calls += 1 }; XCTAssertEqual(calls, 1)
    }
    func testDetailDefersQueueUntilExplicitActionAndReleasesExactSourceOnDeparture() async throws {
        let service = MusicPublicBrowseFeatureService()
        var loadedSource: UUID?, releasedSource: UUID?
        let model = MusicPublicBrowseViewModel(service: service, request: .detail(MusicPublicBrowseFeatureService.collection),
            queuePage: { source, cursor in
                XCTAssertNil(cursor); loadedSource = source.id
                return .init(items: [MusicPublicBrowseFeatureService.track("1")], cursor: nil)
            }, releaseSource: { source in releasedSource = source.id; source.revoke() })
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        XCTAssertNil(model.remoteSource); XCTAssertNil(loadedSource); XCTAssertEqual(model.tracks.count, 1)
        let initialOpens = await service.openedQueues; XCTAssertEqual(initialOpens, 0)
        let (source, index) = try await model.playbackSource(for: model.tracks[0])
        XCTAssertEqual(index, 0); XCTAssertEqual(model.remoteSource?.id, loadedSource)
        model.disappear()
        XCTAssertEqual(releasedSource, loadedSource); XCTAssertFalse(source.authorization.isActive)
        XCTAssertNil(model.remoteSource); XCTAssertTrue(model.sections.isEmpty)
        await source.close()
    }
    func testEmptyVisiblePageRequiresExplicitContinuationAndKeepsExactRenewalCursor() async throws {
        let service = MusicPublicBrowseFeatureService()
        await service.omitFirstDetailPage()
        let model = model(service, request: .detail(MusicPublicBrowseFeatureService.collection))
        await model.refresh()
        XCTAssertNil(model.error); XCTAssertTrue(model.loaded); XCTAssertTrue(model.requiresExplicitNext)
        XCTAssertTrue(model.tracks.isEmpty); XCTAssertEqual(model.nextCursor, "raw30")
        var cursors = await service.cursors; XCTAssertEqual(cursors.count, 1)
        await model.appendNext()
        XCTAssertNil(model.error); XCTAssertEqual(model.tracks.map(\.title), ["31"]); XCTAssertFalse(model.requiresExplicitNext)
        XCTAssertEqual(model.sections[0].items.map(\.id), ["30:song:31"])
        XCTAssertEqual(model.queueIndex(of: model.tracks[0]), 30)
        _ = try await model.refreshedActionTrack(model.tracks[0])
        cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "raw30", "raw30"])
        let opens = await service.openedQueues; XCTAssertEqual(opens, 0)
    }
    func testConcreteActionFailureCarriesSafeDiagnosticAndCancellationDoesNotAlert() async throws {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        await model.refresh()
        let intent = try XCTUnwrap(model.makeActionIntent())
        await model.perform(intent: intent, operation: "播放歌曲") { throw MusicServiceError.requestRejected }
        XCTAssertEqual(model.actionError, .requestRejected); XCTAssertEqual(model.actionOperation, "播放歌曲")
        let id = try XCTUnwrap(model.actionDiagnosticID)
        XCTAssertNotNil(id.range(of: "^[A-Fa-f0-9]{8}$", options: .regularExpression))
        let cancelled = try XCTUnwrap(model.makeActionIntent())
        await model.perform(intent: cancelled) { throw CancellationError() }
        XCTAssertNil(model.actionError); XCTAssertNil(model.actionDiagnosticID)
    }
    func testCatalogPlatformsDoNotInventAccountsAndExistingCollectionIDsStayStable() async throws {
        XCTAssertEqual(MusicPublicBrowsePlatform.allCases.map(\.rawValue), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertEqual(MusicAccountPlatform.allCases.map(\.rawValue), ["wy", "tx", "kg"])
        for account in MusicAccountPlatform.allCases {
            let platform = MusicPublicBrowsePlatform(accountPlatform: account)
            XCTAssertEqual(platform.rawValue, account.rawValue)
            let collection = MusicPublicBrowseCollection(platform: platform, nativeID: "41", title: "Synthetic")
            XCTAssertEqual(collection.id, account.rawValue + ":playlist:41")
            let album = MusicPublicBrowseCollection(platform: platform, nativeID: "41", kind: .album, title: "Synthetic")
            XCTAssertEqual(album.id, account.rawValue + ":album:41")
            let service = MusicPublicBrowseFeatureService()
            _ = try await service.publicPlaylists(accountPlatform: account, query: "Synthetic", cursor: nil)
            let platforms = await service.platforms; XCTAssertEqual(platforms, [platform])
        }
    }
    func testNewPlatformsRetryFailedContinuationWithoutRefreshingOrChangingTarget() async {
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let service = MusicPublicBrowseFeatureService(), model = model(service, request: .playlists(platform, query: "Synthetic"))
            await model.refresh(); await service.fail(.rateLimited); await model.next()
            XCTAssertEqual(model.pageNumber, 1); XCTAssertEqual(model.error, .rateLimited)
            XCTAssertEqual(model.sections[0].items[0].id, platform.rawValue + ":playlist:1")
            await service.fail(nil); await model.retry()
            XCTAssertNil(model.error); XCTAssertEqual(model.pageNumber, 2)
            let cursors = await service.cursors, platforms = await service.platforms
            XCTAssertEqual(cursors, [nil, "next", "next"]); XCTAssertEqual(platforms, [platform, platform, platform])
            model.disappear()
        }
    }
    func testNewPlatformsRejectLongCursorCycleWithoutDiscardingLastGoodPage() async {
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let service = MusicPublicBrowseFeatureService(), model = model(service, request: .playlists(platform, query: "Synthetic"))
            await service.loopMultiplePages(); await model.refresh(); await model.next()
            XCTAssertEqual(model.pageNumber, 2)
            await model.next()
            XCTAssertEqual(model.error, .invalidResponse); XCTAssertEqual(model.pageNumber, 2)
            XCTAssertEqual(model.request, .playlists(platform, query: "Synthetic"))
            model.disappear()
        }
    }
    func testPlaylistPaginationKeepsMoreThanFormerTwoHundredPages() async {
        let service = MusicPublicBrowseFeatureService(), model = model(service)
        await service.keepPaging(); await model.refresh()
        for page in 1...200 {
            await model.next()
            XCTAssertNil(model.error, "page \(page + 1)")
        }
        XCTAssertEqual(model.pageNumber, 201)
        XCTAssertEqual(model.total, 201)
        XCTAssertEqual(model.sections.reduce(0, { $0 + $1.items.count }), 1)
        model.disappear()
    }

    func testDetailAppendsBeyondTwoThousandSongsAndFindsFullAndFilteredQueueIndex() async throws {
        let service = MusicPublicBrowseFeatureService(), count = 2001
        await service.keepDetailPaging(count: count)
        let model = MusicPublicBrowseViewModel(service: service, request: .detail(MusicPublicBrowseFeatureService.collection),
            queuePage: { _, cursor in
                let start = cursor.flatMap(Int.init) ?? 0, end = min(start + 30, count)
                return .init(items: (start..<end).map { MusicPublicBrowseFeatureService.track(String($0 + 1)) },
                             cursor: end < count ? String(end) : nil)
            })
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        while model.nextCursor != nil, model.error == nil { await model.appendNext() }
        XCTAssertNil(model.error); XCTAssertFalse(model.usesPageNavigation)
        XCTAssertEqual(model.tracks.count, count); XCTAssertEqual(model.total, count)
        let track = try XCTUnwrap(model.tracks.last)
        XCTAssertEqual(model.queueIndex(of: track), 2000)
        let (source, index) = try await model.playbackSource(for: track)
        XCTAssertEqual(index, 2000)
        let filtered = try await model.filteredQueueIndex(source: source, index: index, track: track, query: "2001")
        XCTAssertEqual(filtered, 0)
        model.disappear(); await source.close()
    }

    func testDetailPaginationKeepsMoreThanFormerSixHundredSixtySevenPages() async {
        let service = MusicPublicBrowseFeatureService()
        await service.keepDetailPaging(count: 670, pageSize: 1)
        let model = model(service, request: .detail(MusicPublicBrowseFeatureService.collection))
        await model.refresh()
        for page in 1..<670 {
            await model.next()
            XCTAssertNil(model.error, "page \(page + 1)")
        }
        XCTAssertEqual(model.pageNumber, 670); XCTAssertEqual(model.total, 670)
        XCTAssertEqual(model.tracks.last?.title, "670"); XCTAssertNil(model.nextCursor)
        model.disappear()
    }

    func testQueueIndexTraversalStillRejectsMultiPageCursorCycles() async throws {
        let service = MusicPublicBrowseFeatureService()
        let model = MusicPublicBrowseViewModel(service: service, request: .detail(MusicPublicBrowseFeatureService.collection),
            queuePage: { _, cursor in
                .init(items: [MusicPublicBrowseFeatureService.track("other")],
                      cursor: cursor == nil ? "a" : cursor == "a" ? "b" : "a")
            })
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        do {
            _ = try await model.playbackSource(for: XCTUnwrap(model.tracks.first))
            XCTFail("cursor cycle")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let source = try XCTUnwrap(model.remoteSource)
        do {
            _ = try await model.filteredQueueIndex(source: source, index: 2001,
                track: MusicPublicBrowseFeatureService.track("missing"), query: "")
            XCTFail("filtered cursor cycle")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        model.disappear(); await source.close()
    }

    func testNewPlatformSearchLateCallbackCannotSurviveProtectionGenerationChange() async {
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let service = MusicPublicBrowseFeatureService(), gate = MusicPublicBrowseGate()
            await service.suspend(gate)
            let model = model(service, request: .playlists(platform, query: "Synthetic"))
            let task = Task { await model.refresh() }
            await gate.didStart(); model.setAccess(available: true, generation: UUID())
            await gate.release(); await task.value
            XCTAssertFalse(model.loaded); XCTAssertTrue(model.sections.isEmpty); XCTAssertNil(model.error)
            model.disappear()
        }
    }
}

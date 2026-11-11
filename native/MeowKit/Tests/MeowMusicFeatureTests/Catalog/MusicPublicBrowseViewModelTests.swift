import XCTest
import Foundation
@testable import MeowMusicFeature

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
private actor MusicPublicBrowseFeatureService: MusicPublicBrowsing, MusicPublicBrowseQueueLoading, MusicPublicBrowseRefreshing, MusicHomeActionQueueServing {
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
    var queueRequests: [MusicPublicBrowseQueueRequest] = []
    var detailTrackID = "1"
    var refreshes: [MusicPublicBrowseCollection] = []
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
    func replaceDetailTrack(_ id: String) { detailTrackID = id }
    func refreshPublicCollection(_ collection: MusicPublicBrowseCollection) { refreshes.append(collection) }
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
        if let error { throw error }
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
        return .init(collection: collection, members: [.init(nativeID: detailTrackID, sourcePosition: 0, track: Self.track(detailTrackID))],
                     total: 1, cursor: nil, snapshot: String(repeating: "a", count: 64))
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        if let gate { await gate.wait() }
        if let error { throw error }
        return .init(sections: [.init(id: "home", title: "Synthetic", items: [
            .init(id: "1", title: "Song", content: .track(Self.track("1")))
        ])])
    }
    var dailyActions: [MusicHomeAction] = []
    private var defaultDailyGate: MusicPublicBrowseGate?
    func suspendDefaultDaily(_ gate: MusicPublicBrowseGate) { defaultDailyGate = gate }
    var styleWrites: [MusicDailyStyleSelection] = []
    var styleFailure: MusicServiceError?
    var changedStyle = false
    static let styleTags = [MusicDailyStyleTag(id: "7", name: "标签甲"), .init(id: "8", name: "标签乙")] +
        (9...14).map { MusicDailyStyleTag(id: String($0), name: "服务标签\($0)") }
    static let styleCategories = [MusicDailyStyleCategory(id: "1000", name: "服务分类甲", tags: styleTags),
                                  .init(id: "2000", name: "服务分类乙", tags: styleTags)]
    static let initialStyle = MusicDailyStyleSelection(category: "1000", tags: [styleTags[0]])
    func setStyleFailure(_ failure: MusicServiceError?) { styleFailure = failure }
    func changeStyleReadback() { changedStyle = true }
    private static func dailyPage(_ selection: MusicDailyStyleSelection?) throws -> MusicNativeTrackPage {
        .init(items: [.init(platform: "wy", data: try JSONEncoder().encode(track(selection == nil ? "default" : "style")))],
              total: 1, dailyStyle: selection)
    }
    func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        guard case .daily(let branch) = action else { throw MusicServiceError.unsupported }
        dailyActions.append(action)
        // Deliberately ignores cancellation until the old open returns.
        if branch == .default, let defaultDailyGate { await defaultDailyGate.wait() }
        return MusicRemoteQueueSource(
            context: .init(kind: .recommendation, id: branch.rawValue, title: title, platform: "wy"),
            loadPage: { _ in try Self.dailyPage(branch == .style ? Self.initialStyle : nil) },
            dailyStyle: branch == .style ? .init(categories: { Self.styleCategories }, save: { selection in
                try await self.saveStyle(selection)
            }) : nil,
            homeAction: action)
    }
    private func saveStyle(_ selection: MusicDailyStyleSelection) throws -> MusicDailyStyleSave {
        styleWrites.append(selection)
        if let styleFailure { throw styleFailure }
        let actual = changedStyle ? Self.initialStyle : selection
        return .init(page: try Self.dailyPage(actual), saveConfirmed: true, selectionChanged: changedStyle)
    }
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) -> [MusicNativeTrackData] { [] }
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) -> MusicRemoteQueueSource {
        openedQueues += 1
        queueRequests.append(request)
        return .init(context: .init(kind: request.collection.kind == .album ? .album : .playlist,
              id: request.collection.id, title: "Synthetic", platform: request.collection.platform.rawValue,
              collection: request.collection),
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
    private func dailyModel(_ service: MusicPublicBrowseFeatureService) -> MusicPublicBrowseViewModel {
        let page = MusicPublicBrowseViewModel(service: service,
            request: .homeAction(.daily(branch: .default), title: "每日推荐"),
            queuePage: { source, cursor in
                let result = try await source.page(cursor: cursor)
                return .init(items: try result.items.map { try JSONDecoder().decode(MusicRemoteTrack.self, from: $0.data) },
                             cursor: result.cursor, dailyStyle: result.dailyStyle)
            })
        page.setAccess(available: true, generation: UUID())
        page.appear()
        return page
    }

    func testDailyEntryIdentityAndBranchSwitchRetireOldSourceWithoutQueueCommit() async throws {
        XCTAssertEqual(MusicPublicBrowseRequest.discovery(.daily(.netease)).dailyBranch, .default)
        XCTAssertEqual(MusicPublicBrowseRequest.accountRecommendation(.netease, kind: "daily", title: "日推").dailyPlatform, .netease)
        XCTAssertNil(MusicPublicBrowseRequest.discovery(.home).dailyBranch)
        let service = MusicPublicBrowseFeatureService(), page = dailyModel(service)
        await page.loadIfNeeded()
        let old = try XCTUnwrap(page.remoteSource)
        XCTAssertEqual(page.tracks.map(\.songId), ["song:default"])
        await page.selectDailyBranch(.style)
        XCTAssertEqual(page.request.dailyBranch, .style)
        XCTAssertEqual(page.tracks.map(\.songId), ["song:style"])
        XCTAssertNotEqual(page.remoteSource?.id, old.id)
        XCTAssertFalse(old.authorization.isActive)
        XCTAssertEqual(page.dailyStyle, MusicPublicBrowseFeatureService.initialStyle)
        XCTAssertFalse(page.canSaveCollection)
        let actions = await service.dailyActions, writes = await service.styleWrites
        XCTAssertEqual(actions, [.daily(branch: .default), .daily(branch: .style)])
        XCTAssertTrue(writes.isEmpty)
        page.disappear()
    }

    func testDailyBranchSwitchRejectsDelayedOldDefaultResponse() async throws {
        let service = MusicPublicBrowseFeatureService(), gate = MusicPublicBrowseGate()
        await service.suspendDefaultDaily(gate)
        let page = dailyModel(service)
        let oldRead = Task { await page.loadIfNeeded() }
        await gate.didStart()
        XCTAssertTrue(page.isLoading)
        await page.selectDailyBranch(.style)
        let styleSource = page.remoteSource?.id
        XCTAssertNotNil(styleSource)
        XCTAssertEqual(page.request.dailyBranch, .style)
        XCTAssertEqual(page.tracks.map(\.songId), ["song:style"])
        await gate.release()
        await oldRead.value
        XCTAssertEqual(page.request.dailyBranch, .style)
        XCTAssertEqual(page.remoteSource?.id, styleSource)
        XCTAssertEqual(page.tracks.map(\.songId), ["song:style"])
        XCTAssertEqual(page.dailyStyle, MusicPublicBrowseFeatureService.initialStyle)
        XCTAssertTrue(page.loaded)
        XCTAssertFalse(page.isLoading)
        XCTAssertNil(page.error)
        let actions = await service.dailyActions, writes = await service.styleWrites
        XCTAssertEqual(actions, [.daily(branch: .default), .daily(branch: .style)])
        XCTAssertTrue(writes.isEmpty)
        page.disappear()
    }

    #if os(iOS) || os(macOS)
    private func waitForStyle(_ condition: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 2)
    }

    func testDailyStyleDraftCancelAndSaveReadbackAreSeparateFromBrowsing() async throws {
        let service = MusicPublicBrowseFeatureService(), page = dailyModel(service)
        await page.loadIfNeeded(); await page.selectDailyBranch(.style)
        let source = try XCTUnwrap(page.remoteSource)
        let cancelled = MusicDailyStyleViewModel(page: page, source: source)
        cancelled.loadIfNeeded()
        await waitForStyle { !cancelled.categories.isEmpty && !cancelled.working }
        XCTAssertEqual(cancelled.categories.map(\.name), ["服务分类甲", "服务分类乙"])
        cancelled.selectCategory("2000")
        for tag in MusicPublicBrowseFeatureService.styleTags { cancelled.selectTag(tag.id) }
        XCTAssertEqual(cancelled.selected.count, 8)
        XCTAssertNil(cancelled.error)
        for tag in MusicPublicBrowseFeatureService.styleTags where tag.id != "8" { cancelled.selectTag(tag.id) }
        cancelled.selectCategory("1000"); cancelled.selectCategory("2000")
        XCTAssertEqual(cancelled.selected, ["8"])
        cancelled.close()
        let cancelledWrites = await service.styleWrites
        XCTAssertTrue(cancelledWrites.isEmpty)
        XCTAssertEqual(page.dailyStyle, MusicPublicBrowseFeatureService.initialStyle)

        let editor = MusicDailyStyleViewModel(page: page, source: source)
        editor.loadIfNeeded()
        await waitForStyle { !editor.categories.isEmpty && !editor.working }
        editor.selectCategory("2000"); editor.selectTag("8")
        editor.save(); editor.save()
        await waitForStyle { editor.saved && !editor.working }
        XCTAssertEqual(page.dailyStyle, .init(category: "2000", tags: [MusicPublicBrowseFeatureService.styleTags[1]]))
        XCTAssertEqual(editor.category, "2000"); XCTAssertEqual(editor.selected, ["8"])
        let writes = await service.styleWrites
        XCTAssertEqual(writes.count, 1)
        XCTAssertEqual(page.remoteSource?.id, source.id)
        editor.close(); page.disappear()
    }

    func testDailyStyleFailedSaveCanRetryAndAccountChangeClearsDraft() async throws {
        let service = MusicPublicBrowseFeatureService(), page = dailyModel(service)
        await page.loadIfNeeded(); await page.selectDailyBranch(.style)
        let editor = MusicDailyStyleViewModel(page: page, source: try XCTUnwrap(page.remoteSource))
        editor.loadIfNeeded()
        await waitForStyle { !editor.categories.isEmpty && !editor.working }
        editor.selectCategory("2000"); editor.selectTag("8")
        await service.setStyleFailure(.upstreamInvalid)
        editor.save()
        await waitForStyle { editor.error != nil && !editor.working }
        XCTAssertFalse(editor.saved); XCTAssertNil(page.dailyStyle); XCTAssertTrue(page.tracks.isEmpty)
        await service.setStyleFailure(nil)
        editor.save()
        await waitForStyle { editor.saved && !editor.working }
        XCTAssertEqual(page.dailyStyle?.category, "2000")
        page.setAccess(available: false, generation: UUID())
        await waitForStyle { !editor.available && editor.categories.isEmpty }
        XCTAssertTrue(editor.choices.isEmpty); XCTAssertFalse(editor.canSave)
        editor.close(); page.disappear()
    }

    func testDailyStyleDifferentReadbackRemainsVisibleWithoutPretendingSaveSucceeded() async throws {
        let service = MusicPublicBrowseFeatureService(), page = dailyModel(service)
        await page.loadIfNeeded(); await page.selectDailyBranch(.style)
        let editor = MusicDailyStyleViewModel(page: page, source: try XCTUnwrap(page.remoteSource))
        editor.loadIfNeeded()
        await waitForStyle { !editor.categories.isEmpty && !editor.working }
        editor.selectCategory("2000"); editor.selectTag("8")
        await service.changeStyleReadback()
        editor.save()
        await waitForStyle { editor.error != nil && !editor.working }
        XCTAssertFalse(editor.saved)
        XCTAssertEqual(page.dailyStyle, MusicPublicBrowseFeatureService.initialStyle)
        XCTAssertEqual(editor.category, "1000"); XCTAssertEqual(editor.selected, ["7"])
        let writes = await service.styleWrites
        XCTAssertEqual(writes.count, 1)
        editor.close(); page.disappear()
    }
    #endif

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
    func testPlaylistReopeningRefreshesRemoteMembersButNotOnEveryVisibleLoad() async throws {
        let service = MusicPublicBrowseFeatureService(), page = model(service, request: .detail(MusicPublicBrowseFeatureService.collection))
        await page.loadIfNeeded(); await page.loadIfNeeded()
        XCTAssertEqual(page.tracks.map(\.title), ["1"])
        let initial = await service.refreshes
        XCTAssertEqual(initial.count, 1)
        page.disappear(); await service.replaceDetailTrack("2")
        page.appear(); await page.loadIfNeeded()
        XCTAssertEqual(page.tracks.map(\.title), ["2"])
        let refreshed = await service.refreshes, opens = await service.openedQueues
        XCTAssertEqual(refreshed, [MusicPublicBrowseFeatureService.collection, MusicPublicBrowseFeatureService.collection])
        XCTAssertEqual(opens, 0)
        page.disappear()
    }
    func testNetworkSaveUsesDisplayedIdentityWithoutPreparingAPlaybackQueue() async throws {
        let browser = MusicPublicBrowseFeatureService(), page = model(browser, request: .detail(MusicPublicBrowseFeatureService.collection))
        let service = MusicCollectionService(rows: []), library = MusicViewModel(service: service, player: MusicCollectionPlayer())
        await page.loadIfNeeded()
        let collection = try XCTUnwrap(page.networkSaveCollection), intent = try XCTUnwrap(page.makeActionIntent())
        await page.perform(intent: intent, operation: "收藏歌单", kind: .library) { try await library.saveNetworkPlaylist(collection) }
        let saves = await service.statistics(), queues = await browser.openedQueues
        XCTAssertEqual(saves.networkSaves, 1); XCTAssertEqual(saves.createCalls, 0)
        XCTAssertEqual(saves.tracks, 0); XCTAssertEqual(saves.queueWrites, 0); XCTAssertEqual(queues, 0)
        XCTAssertEqual(library.collections.first?.networkReference?.remoteID, collection.nativeID)
        XCTAssertNil(page.remoteSource)
        page.disappear(); await library.close()
    }
    func testFailedReopeningKeepsRemoteIdentityAndAlbumsCannotBecomeNetworkPlaylists() async throws {
        let service = MusicPublicBrowseFeatureService(), requested = MusicPublicBrowseFeatureService.collection
        let page = model(service, request: .detail(requested))
        await page.loadIfNeeded(); page.disappear(); await service.fail(.upstreamFailed)
        page.appear(); await page.loadIfNeeded()
        XCTAssertEqual(page.error, .upstreamFailed); XCTAssertEqual(page.request, .detail(requested))
        XCTAssertEqual(page.networkSaveCollection, requested)
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", kind: .album, title: "Album")
        let albumPage = model(service, request: .detail(album))
        XCTAssertFalse(albumPage.canSaveCollection); XCTAssertNil(albumPage.networkSaveCollection)
        let listing = model(service)
        XCTAssertFalse(listing.canSaveCollection)
        page.disappear(); albumPage.disappear(); listing.disappear()
    }
    func testAlbumCannotBeSavedButKeepsFullPlaybackSourceAndSelectedOccurrence() async throws {
        let service = MusicPublicBrowseFeatureService()
        let album = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", kind: .album, title: "Album")
        await service.keepDetailPaging(count: 3, pageSize: 1)
        var queueCursors: [String?] = []
        let page = MusicPublicBrowseViewModel(service: service, request: .detail(album),
            queuePage: { _, cursor in
                queueCursors.append(cursor)
                let index = cursor.flatMap(Int.init) ?? 0
                return .init(items: [MusicPublicBrowseFeatureService.track(String(index + 1))],
                             cursor: index < 2 ? String(index + 1) : nil)
            })
        page.setAccess(available: true, generation: UUID()); page.appear()
        XCTAssertFalse(page.canPlayRemoteQueue)
        await page.loadIfNeeded(); await page.next()
        XCTAssertNil(page.error); XCTAssertFalse(page.canSaveCollection); XCTAssertNil(page.networkSaveCollection)
        XCTAssertTrue(page.canPlayRemoteQueue); XCTAssertNil(page.remoteSource)
        XCTAssertEqual(page.tracks.map(\.title), ["2"])
        let initialOpens = await service.openedQueues
        XCTAssertEqual(initialOpens, 0)
        let (source, index) = try await page.playbackSource(for: XCTUnwrap(page.tracks.first))
        XCTAssertEqual(index, 1, "第二页选曲必须保留在完整专辑中的位置")
        XCTAssertEqual(source.context.kind, .album); XCTAssertEqual(source.context.collection, album)
        XCTAssertEqual(queueCursors, [nil, "1"])
        let requests = await service.queueRequests
        XCTAssertEqual(requests.count, 1); XCTAssertEqual(requests.first?.collection, album)
        XCTAssertEqual(requests.first?.snapshot, String(repeating: "a", count: 64))
        XCTAssertTrue(page.canPlayRemoteQueue); XCTAssertFalse(page.canSaveCollection)
        page.disappear()
        XCTAssertFalse(page.canPlayRemoteQueue); XCTAssertFalse(source.authorization.isActive)
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

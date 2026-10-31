#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicQueueUIKitTests: XCTestCase {
    func testSharedHeaderHasStableSizesAndOneIndicatorFollowingPartialPaging() async throws {
        let service = QueueUIKitService(), model = await makeModel(service)
        let (queue, root, window) = await mount(model)
        defer { release(queue, root: root, window: window) }
        await wait("local history loaded") { model.playedPlaylists.count == 2 }
        let pager = try XCTUnwrap(queue.children.first as? MusicQueuePagerViewController)
        let group = try XCTUnwrap(queue.view.subviews.first { $0.accessibilityIdentifier == "music.queue.tabs" })
        let tabs = try (0..<3).map { index in
            try XCTUnwrap(group.subviews.first { $0.accessibilityIdentifier == "music.queue.tab.\(index)" } as? UIButton)
        }
        let indicator = try XCTUnwrap(group.subviews.first { $0.accessibilityIdentifier == "music.queue.tab.indicator" })
        let clear = try XCTUnwrap(queue.view.subviews.first { $0.accessibilityIdentifier == "music.queue.clear" } as? UIButton)
        let labels = try tabs.enumerated().map { index, tab in
            try XCTUnwrap(tab.subviews.first { $0.accessibilityIdentifier == "music.queue.tab.title.\(index)" } as? UILabel)
        }
        let titles = ["当前播放", "已播歌曲", "已播歌单"]
        XCTAssertEqual(labels.map(\.text), titles)
        XCTAssertEqual(tabs.map(\.accessibilityLabel), ["当前播放 4", "已播歌曲", "已播歌单"])
        let titleFont = labels[0].font!
        let badge = try XCTUnwrap(tabs[0].subviews.first { $0.accessibilityIdentifier == "music.queue.tab.badge.0" } as? UILabel)
        tabs[0].layoutIfNeeded()
        XCTAssertEqual(badge.text, "4")
        XCTAssertEqual(badge.font.pointSize, 8)
        XCTAssertLessThan(badge.frame.minY, labels[0].frame.minY + 2, "The queue count stays as a raised badge")
        XCTAssertEqual(badge.frame.minX, labels[0].frame.maxX, accuracy: 0.5)
        XCTAssertLessThanOrEqual(badge.frame.maxX, tabs[0].bounds.maxX)
        XCTAssertEqual(labels[0].center.x, tabs[0].bounds.midX, accuracy: 0.5)
        XCTAssertGreaterThanOrEqual(labels[0].frame.minX, 18)
        for (tab, label) in zip(tabs, labels) {
            tab.layoutIfNeeded()
            XCTAssertEqual(label.numberOfLines, 1)
            XCTAssertEqual(label.lineBreakMode, .byClipping)
            XCTAssertLessThanOrEqual(label.bounds.height, titleFont.lineHeight + 5, "Category titles stay on one line")
        }
        XCTAssertEqual(labels[0].font.pointSize * labels[0].transform.a,
                       labels[1].font.pointSize * labels[1].transform.a + 2, accuracy: 0.01)
        let theme = queue.theme
        XCTAssertEqual(labels[0].textColor, UIColor(theme.ink))
        XCTAssertEqual(labels[1].textColor, UIColor(theme.muted))
        XCTAssertEqual(labels[2].textColor, UIColor(theme.muted))
        let selectedScale = labels[0].transform.a, normalScale = labels[1].transform.a
        let frames = tabs.map(\.frame), groupFrame = group.frame, panelFrame = queue.view.frame
        let indicatorSize = indicator.bounds.size
        XCTAssertLessThan(group.frame.maxX, clear.frame.minX - 8)
        XCTAssertFalse(clear.isHidden)

        let pageWidth = pager.scroll.bounds.width
        for position in [CGFloat(0.25), 0.5, 0.75, 1.5] {
            pager.scroll.setContentOffset(CGPoint(x: pageWidth * position, y: 0), animated: false)
            let actualPosition = pager.categoryPosition
            XCTAssertEqual(actualPosition, position, accuracy: 1 / pageWidth)
            let lower = Int(actualPosition), fraction = actualPosition - CGFloat(lower)
            let expected = frames[lower].minX + (frames[lower + 1].minX - frames[lower].minX) * fraction
            XCTAssertEqual(indicator.frame.minX, expected, accuracy: 0.5)
            XCTAssertEqual(indicator.bounds.size, indicatorSize)
            XCTAssertEqual(tabs.map(\.frame), frames)
            XCTAssertEqual(pager.selectedID, "current", "The indicator follows the scroll before selection settles")
            for (index, label) in labels.enumerated() {
                let strength = max(0, 1 - abs(actualPosition - CGFloat(index)))
                XCTAssertEqual(label.transform.a, normalScale + (selectedScale - normalScale) * strength, accuracy: 0.001)
                XCTAssertEqual(label.textColor, UIColor(theme.muted.mixed(with: theme.ink, ratio: Double(strength))))
            }
        }
        for id in ["songs", "history:archive-a", "history:archive-b", "current"] {
            pager.select(id, animated: false)
            queue.view.layoutIfNeeded()
            let category = MusicReferenceQueueStrip.category(id).rawValue
            XCTAssertEqual(indicator.frame.minX, frames[category].minX, accuracy: 0.5)
            XCTAssertEqual(indicator.bounds.size, indicatorSize)
            XCTAssertEqual(tabs.map(\.frame), frames)
            XCTAssertEqual(group.frame, groupFrame)
            XCTAssertEqual(queue.view.frame, panelFrame)
            XCTAssertEqual(labels.map(\.text), titles)
            XCTAssertEqual(badge.textColor, labels[0].textColor)
            for (index, label) in labels.enumerated() {
                XCTAssertEqual(label.transform.a, index == category ? selectedScale : normalScale, accuracy: 0.001)
                XCTAssertEqual(label.textColor, UIColor(index == category ? theme.ink : theme.muted))
            }
            XCTAssertFalse(clear.isHidden)
        }
        queue.overrideUserInterfaceStyle = theme.dark ? .light : .dark
        await updates()
        queue.applyTheme()
        queue.view.layoutIfNeeded()
        XCTAssertEqual(queue.theme.dark, !theme.dark)
        XCTAssertEqual(labels[0].textColor, UIColor(queue.theme.ink))
        XCTAssertEqual(labels[1].textColor, UIColor(queue.theme.muted))
        XCTAssertEqual(labels[2].textColor, UIColor(queue.theme.muted))
        XCTAssertEqual(tabs.map(\.frame), frames)
        await model.close()
    }

    func testHistoryClearUpdatesOnlyItsCategoryAndRejectsOlderReads() async throws {
        let service = QueueUIKitService(), model = await makeModel(service)
        let (queue, root, window) = await mount(model)
        defer { release(queue, root: root, window: window) }
        await wait("local history loaded") { model.playedPlaylists.count == 2 && !model.playedSongs.isEmpty }
        let pager = try XCTUnwrap(queue.children.first as? MusicQueuePagerViewController)
        let clear = try XCTUnwrap(queue.view.subviews.first { $0.accessibilityIdentifier == "music.queue.clear" } as? UIButton)
        let entries = model.queue, state = model.state
        for (id, title) in [("songs", "清空已播歌曲？"), ("history:archive-a", "清空已播歌单？")] {
            pager.select(id, animated: false)
            await wait("queue ready for confirmation") { queue.canPresentPageAlert }
            clear.sendActions(for: .touchUpInside)
            let alert = try XCTUnwrap(queue.presentedViewController as? UIAlertController)
            await wait("history confirmation ready") { !alert.isBeingPresented }
            XCTAssertEqual(alert.title, title)
            XCTAssertEqual(alert.actions.map(\.style), [.destructive, .cancel])
            let dismissed = expectation(description: "history confirmation dismissed")
            alert.dismiss(animated: false) { dismissed.fulfill() }
            await fulfillment(of: [dismissed], timeout: 3)
            await wait("queue returned from confirmation") { queue.canPresentPageAlert }
        }
        let before = await service.statistics()
        XCTAssertTrue(before.clears.isEmpty, "Opening or cancelling a confirmation cannot clear history")

        let entered = expectation(description: "older history read captured")
        let gate = QueueUIKitGate { entered.fulfill() }
        await service.holdNextHistoryRead(gate)
        let older = Task { await model.loadHistory() }
        await fulfillment(of: [entered], timeout: 3)
        await model.clearHistory(.songs)
        await gate.release()
        await older.value
        XCTAssertTrue(model.playedSongs.isEmpty, "An old read cannot resurrect cleared listening history")
        XCTAssertEqual(model.playedPlaylists.count, 2)
        XCTAssertEqual(model.queue, entries)
        XCTAssertEqual(model.state, state)

        pager.select("history:archive-a", animated: false)
        weak var removedCard = pager.children.compactMap { $0 as? MusicHistoryCardViewController }.first
        XCTAssertNotNil(removedCard)
        await model.clearHistory(.playlists)
        await wait("empty history page reconciled") { pager.selectedID == "playlists" }
        await wait("cleared history card released") { removedCard == nil }
        XCTAssertTrue(model.playedPlaylists.isEmpty)
        XCTAssertTrue(model.playedSongs.isEmpty)
        XCTAssertEqual(model.queue, entries)
        XCTAssertEqual(model.state, state)
        XCTAssertEqual(clear.accessibilityLabel, "清空已播歌单")
        XCTAssertFalse(clear.isHidden)
        let saved = await service.statistics()
        XCTAssertEqual(saved.clears, [.songs, .playlists])
        XCTAssertEqual(saved.previewReads, 0)
        XCTAssertNil(model.error)
        await model.close()
    }

    func testAnonymousQueueReadsLocalHistoryAndRetainsPagedCards() async throws {
        let service = QueueUIKitService(), model = await makeModel(service)
        let (queue, root, window) = await mount(model)
        defer { release(queue, root: root, window: window) }
        let pager = try XCTUnwrap(queue.children.first as? MusicQueuePagerViewController)
        await wait("local history loaded") { model.playedPlaylists.count == 2 }
        let frame = queue.view.frame
        XCTAssertTrue(model.isAvailable)
        XCTAssertNil(queue.presentedViewController)
        XCTAssertEqual(model.queue.map(\.id), ["entry-0", "entry-1", "entry-2", "entry-3"])
        pager.select("history:archive-b", animated: false)
        queue.view.layoutIfNeeded(); pager.view.layoutIfNeeded()
        let card = try XCTUnwrap(pager.children.compactMap { $0 as? MusicHistoryCardViewController }.last)
        let table = try XCTUnwrap(card.view.subviews.compactMap { $0 as? UITableView }.first)
        await wait("visible local details loaded") { table.numberOfSections > 0 && table.numberOfRows(inSection: 0) == 12 }
        table.setContentOffset(CGPoint(x: 0, y: 12), animated: false)
        let position = table.contentOffset
        pager.select("songs", animated: false)
        pager.select("history:archive-b", animated: false)
        queue.view.layoutIfNeeded()
        XCTAssertTrue(pager.children.contains { $0 === card })
        XCTAssertEqual(table.contentOffset, position)
        XCTAssertEqual(queue.view.frame, frame, "Changing categories cannot enlarge the sheet")
        XCTAssertLessThan(card.view.bounds.width, pager.view.bounds.width)
        XCTAssertNil(model.error)
        let before = await service.statistics()
        XCTAssertEqual(before.historyReads, 1)
        XCTAssertEqual(before.previewReads, 0)
        XCTAssertEqual(Set(before.detailReads), ["archive-a", "archive-b"])
        XCTAssertTrue(before.reorders.isEmpty)

        model.libraryState.playedPlaylists.removeAll { $0.id == "archive-b" }
        await wait("removed selected card reconciled") { pager.selectedID == "history:archive-a" }
        XCTAssertFalse(card.isPageActive)
        let after = await service.statistics()
        XCTAssertEqual(after.detailReads, before.detailReads)
        await model.close()
    }

    func testNativeInteractiveMovementCommitsOccurrencesOnceAndPreservesSelection() async throws {
        let service = QueueUIKitService(), model = await makeModel(service)
        let (queue, root, window) = await mount(model)
        defer { release(queue, root: root, window: window) }
        let (collection, reorder) = try movement(in: queue)
        let from = try center(0, in: collection), to = try center(2, in: collection)
        let firstCell = try XCTUnwrap(collection.cellForItem(at: IndexPath(item: 0, section: 0)) as? MusicQueueCell)
        let resting = firstCell.row.backgroundColor
        reorder.beginPress(at: from)
        XCTAssertEqual(firstCell.row.backgroundColor, UIColor(queue.theme.chip), "Touch down highlights immediately")
        reorder.endPress()
        XCTAssertEqual(firstCell.row.backgroundColor, resting)
        // Grab below the centre: native movement must retain this offset.
        XCTAssertTrue(reorder.begin(at: CGPoint(x: from.x, y: from.y + 14)))
        XCTAssertEqual(firstCell.row.backgroundColor, UIColor(queue.theme.chip))
        reorder.move(to: CGPoint(x: to.x, y: to.y + 14))
        await updates()
        reorder.end()
        XCTAssertEqual(firstCell.row.backgroundColor, resting)
        await wait("native drop saved") { model.queue.map(\.id) == ["entry-1", "entry-2", "entry-0", "entry-3"] }
        let saved = await service.statistics()
        XCTAssertEqual(saved.reorders, [["entry-1", "entry-2", "entry-0", "entry-3"]])
        XCTAssertEqual(model.state.currentId, "entry-1")
        XCTAssertEqual(model.queue.filter { $0.track.songId == "same-song" }.count, 2)
        XCTAssertEqual(model.state.revision, 8)

        await updates()
        XCTAssertTrue(reorder.begin(at: try center(0, in: collection)))
        reorder.end() // An unchanged drop releases its intent without saving.
        XCTAssertTrue(reorder.begin(at: try center(0, in: collection)))
        reorder.move(to: try center(2, in: collection))
        reorder.cancel()
        await updates()
        let final = await service.statistics()
        XCTAssertEqual(final.reorders, saved.reorders)
        XCTAssertFalse(reorder.isActive)
        let dataSource = try XCTUnwrap(collection.dataSource as? UICollectionViewDiffableDataSource<Int, String>)
        XCTAssertEqual(dataSource.snapshot().itemIdentifiers, model.queue.map(\.id))

        let entered = expectation(description: "accepted drop entered service")
        let gate = QueueUIKitGate { entered.fulfill() }
        await service.holdNextReorder(gate)
        XCTAssertTrue(reorder.begin(at: try center(0, in: collection)))
        reorder.move(to: try center(1, in: collection))
        await updates()
        reorder.end()
        await fulfillment(of: [entered], timeout: 3)
        root.dismiss(animated: false)
        await gate.release()
        await wait("accepted drop survives sheet dismissal") {
            model.queue.map(\.id) == ["entry-2", "entry-1", "entry-0", "entry-3"]
        }
        let closed = await service.statistics()
        XCTAssertEqual(closed.reorders.count, 2)
        XCTAssertEqual(model.state.currentId, "entry-1")
        await model.close()
    }

    func testConcurrentQueueChangeCancelsOldNativePreview() async throws {
        let service = QueueUIKitService(), model = await makeModel(service)
        let (queue, root, window) = await mount(model)
        defer { release(queue, root: root, window: window) }
        let (collection, reorder) = try movement(in: queue)
        XCTAssertTrue(reorder.begin(at: try center(0, in: collection)))
        reorder.move(to: try center(2, in: collection))
        model.playbackState.queue.removeLast()
        model.playbackState.state = .init(revision: 8, currentId: "entry-1", total: 3)
        await wait("old drag cancelled") { !reorder.isActive }
        reorder.end()
        await updates()
        let dataSource = try XCTUnwrap(collection.dataSource as? UICollectionViewDiffableDataSource<Int, String>)
        XCTAssertEqual(dataSource.snapshot().itemIdentifiers, ["entry-0", "entry-1", "entry-2"])
        let saved = await service.statistics()
        XCTAssertTrue(saved.reorders.isEmpty)
        await model.close()
    }

    private func makeModel(_ service: QueueUIKitService) async -> MusicViewModel {
        let model = MusicViewModel(service: service, player: QueueUIKitPlayer())
        model.playbackState.state = await service.state()
        model.playbackState.queue = await service.queue().entries
        return model
    }

    private func mount(_ model: MusicViewModel) async -> (MusicQueueViewController, UIViewController, UIWindow) {
        let root = UIViewController()
        root.view.backgroundColor = .white
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        let queue = MusicQueueViewController(model: model, appearance: MusicAppearancePreferencesStore())
        root.present(queue, animated: false)
        NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
        await wait("queue appeared") { queue.isPageActive && queue.isPageVisible && !queue.isBeingPresented }
        queue.view.layoutIfNeeded()
        await updates()
        return (queue, root, window)
    }

    private func release(_ queue: UIViewController, root: UIViewController, window: UIWindow) {
        PageViewController.invalidatePages(in: queue)
        root.dismiss(animated: false)
        window.isHidden = true; window.rootViewController = nil
    }

    private func movement(in queue: MusicQueueViewController) throws -> (UICollectionView, MusicQueueReorderController) {
        let pager = try XCTUnwrap(queue.children.first as? MusicQueuePagerViewController)
        pager.select("current", animated: false)
        let page = try XCTUnwrap(pager.children.compactMap { $0 as? MusicQueueCurrentPage }.first)
        page.view.layoutIfNeeded(); page.collection.layoutIfNeeded()
        XCTAssertEqual(page.collection.contentSize.width, page.collection.bounds.width, accuracy: 0.5)
        let reorder = try XCTUnwrap(page.collection.gestureRecognizers?.compactMap {
            $0.delegate as? MusicQueueReorderController
        }.first)
        return (page.collection, reorder)
    }

    private func center(_ item: Int, in collection: UICollectionView) throws -> CGPoint {
        collection.layoutIfNeeded()
        return try XCTUnwrap(collection.layoutAttributesForItem(at: IndexPath(item: item, section: 0))).center
    }

    private func wait(_ description: String, until predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } },
                                evaluatedWith: self)
        ready.expectationDescription = description
        await fulfillment(of: [ready], timeout: 3)
    }

    private func updates() async {
        await withCheckedContinuation { continuation in RunLoop.main.perform { continuation.resume() } }
    }
}

private actor QueueUIKitService: MusicServing, MusicSimilarModeServing, MusicHistoryManaging {
    private var entries: [MusicQueueEntry] = (0..<4).map { index in
        let id = "entry-\(index)", song = index < 2 ? "same-song" : "song-\(index)"
        return .init(id: id, track: .init(ref: .init(kind: .queue, id: id), songId: song, title: song))
    }
    private var revision = 7
    private var historyReads = 0, previewReads = 0
    private var detailReads: [String] = [], reorders: [[String]] = []
    private var clears: [MusicHistoryCategory] = []
    private var reorderGate: QueueUIKitGate?, historyReadGate: QueueUIKitGate?
    func holdNextReorder(_ gate: QueueUIKitGate) { reorderGate = gate }
    func holdNextHistoryRead(_ gate: QueueUIKitGate) { historyReadGate = gate }
    func statistics() -> (historyReads: Int, previewReads: Int, detailReads: [String],
                          reorders: [[String]], clears: [MusicHistoryCategory]) {
        (historyReads, previewReads, detailReads, reorders, clears)
    }
    func state() -> MusicPlaybackState { .init(revision: revision, currentId: "entry-1", total: entries.count) }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: "entry-1", entries: entries) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        let gate = reorderGate; reorderGate = nil
        if let gate { await gate.wait() }
        guard expectedRevision == revision else { throw MusicServiceError.revisionConflict }
        guard case .reorder(let order) = change, Set(order) == Set(entries.map(\.id)),
              order.count == entries.count else { throw MusicServiceError.invalidRequest }
        let rows = Dictionary(uniqueKeysWithValues: entries.map { ($0.id, $0) })
        entries = order.compactMap { rows[$0] }; revision += 1; reorders.append(order)
        return state()
    }
    func playedSongs() async -> [MusicPlayedSong] {
        historyReads += 1
        let songs: [MusicPlayedSong] = clears.contains(.songs) ? [] :
            [.init(date: "2026-01-01", at: 1, track: entries[0].track)]
        let gate = historyReadGate; historyReadGate = nil
        if let gate { await gate.wait() }
        return songs
    }
    func playedPlaylists() -> [MusicPlayedPlaylist] {
        clears.contains(.playlists) ? [] :
            ["archive-a", "archive-b"].map { .init(id: $0, title: $0, count: 12, at: 1) }
    }
    func historyDetail(id: String) -> MusicHistoryDetail {
        detailReads.append(id)
        return .init(summary: .init(id: id, title: id, count: 12, at: 1),
            entries: (0..<12).map { .init(id: "\(id)-\($0)", index: $0,
                title: "歌曲 \($0)", artist: "", album: "", artwork: nil, duration: 0) },
            selectedIndex: 1, sourceLabel: "歌单", actionToken: .init(rawValue: "synthetic-" + id))
    }
    func removeHistory(detail: MusicHistoryDetail) throws { throw MusicServiceError.unsupported }
    func clearHistory(_ category: MusicHistoryCategory) { clears.append(category) }
    func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func similarModeSnapshot() -> MusicSimilarModeSnapshot? { nil }
    func similarModeChanges() -> AsyncStream<Void> { .init { $0.finish() } }
    func canEnterSimilarMode() -> Bool { false }
    func previewSimilarMode() throws -> MusicSimilarModeSnapshot? {
        previewReads += 1; throw MusicServiceError.authRequired
    }
    func enterSimilarMode() throws -> MusicSimilarModeResult { throw MusicServiceError.authRequired }
    func startSimilarMode(_ start: MusicSimilarModeStart) throws -> MusicSimilarModeResult { throw MusicServiceError.authRequired }
    func exitSimilarMode() throws -> MusicSimilarModeResult { throw MusicServiceError.unsupported }
    func playSimilarCandidate(snapshotID: UUID, candidateID: String) throws -> MusicSimilarModeResult { throw MusicServiceError.authRequired }
    func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) throws { throw MusicServiceError.unsupported }
    func retrySimilarMode(snapshotID: UUID) throws -> MusicSimilarModeResult { throw MusicServiceError.unsupported }
    func cancelSimilarMode() {}
    func recordSimilarPlaybackFact(_ fact: MusicPlaybackFact) -> Bool { false }
    func pauseSimilarNavigation() {}
    func platforms() -> [MusicPlatform] { [.init(id: "wy", title: "网易云音乐", accountState: "unauthorized")] }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func search(platform: String, query: String, page: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func playbackOptions(for ref: MusicReference) -> [MusicPlaybackOption] { XCTFail("Viewing/reordering cannot resolve media"); return [] }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }
}

private actor QueueUIKitGate {
    private let entered: @Sendable () -> Void
    private var waiter: CheckedContinuation<Void, Never>?
    private var released = false
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

@MainActor
private final class QueueUIKitPlayer: MusicPlaybackDriving {
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt { XCTFail("Unexpected playback"); return .init(id: UUID(), resourceID: resource.id) }
    func pause(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected pause") }
    func resume(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected resume") }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { XCTFail("Unexpected seek"); return false }
    func stop(_ attempt: MusicPlayerAttempt) { XCTFail("Unexpected stop") }
}
#endif

import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicReferenceQueueTests: XCTestCase {
    func testQueueStripPreservesCategoryLabelsButMakesHistoricalCardsRealNeighbors() {
        let history = ["a", "b", "c"]
        XCTAssertEqual(MusicReferenceQueueStrip.ids(history: history),
                       ["current", "songs", "history:a", "history:b", "history:c"])
        XCTAssertEqual(MusicReferenceQueueStrip.tab(.playlists, selected: "current", history: history), "history:a")
        XCTAssertEqual(MusicReferenceQueueStrip.tab(.playlists, selected: "history:c", history: history), "history:c")
        XCTAssertEqual(MusicReferenceQueueStrip.reconciled("history:b", history: ["a", "c"]), "history:a")
        XCTAssertEqual(MusicReferenceQueueStrip.reconciled("history:b", history: []), "playlists")
        XCTAssertEqual(MusicReferenceQueueStrip.ids(history: []), ["current", "songs", "playlists"])
        XCTAssertEqual(MusicReferenceQueueStrip.category("history:c"), .playlists)
    }

    func testHistoryHighlightUsesSongAndSourceRatherThanRestoreSelectionOrTitle() {
        func identity(song: String = "recording-a", occurrence: String = "entry-1",
                      type: String = "playlist", source: String = "playlist-a",
                      context: Bool = true) -> MusicQueueDisplayIdentity {
            .init(songID: song, occurrenceID: occurrence, sourceType: type, sourceID: source, hasContext: context)
        }
        let archived = identity()
        XCTAssertTrue(archived.matches(identity(occurrence: "entry-2")))
        XCTAssertFalse(archived.matches(identity(song: "recording-b")))
        XCTAssertFalse(archived.matches(identity(source: "playlist-b")))
        XCTAssertFalse(archived.matches(identity(type: "collection")))
        XCTAssertFalse(archived.matches(nil))
        let empty = identity(type: "", source: "", context: false)
        XCTAssertTrue(empty.matches(empty))
        XCTAssertFalse(empty.matches(identity(occurrence: "entry-2", type: "", source: "", context: false)))
        XCTAssertFalse(empty.matches(identity(type: "", source: "", context: true)))
        XCTAssertFalse(identity(type: "", source: "", context: true).matches(identity(type: "", source: "", context: true)))
    }

    func testRepeatedPlayedSongRowsKeepDistinctStableDisplayIDs() {
        let track = MusicRemoteTrack(ref: .init(kind: .queue, id: "entry-1"),
            songId: "recording", title: "歌曲")
        let first = MusicPlayedSong(date: "2026-10-26", at: 1, track: track)
        let second = MusicPlayedSong(date: "2026-10-26", at: 2, track: track)
        XCTAssertNotEqual(first.id, second.id)
        XCTAssertEqual(first.id, MusicPlayedSong(date: first.date, at: first.at, track: track).id)
    }

    private let access = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
    private let request = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
    private let replacement = UUID(uuidString: "33333333-3333-4333-8333-333333333333")!
    private let ids = ["song-a-first", "song-b", "song-a-repeat", "song-c", "song-d"]
    private func intent(ids: [String]? = nil) -> MusicReferenceQueueIntent {
        .init(access: access, revision: 7, ids: ids ?? self.ids)
    }

    func testCurrentQueueIntentRejectsAccessRevisionOrderAndLifecycleChanges() {
        let intent = intent()
        XCTAssertTrue(intent.admits(access: access, revision: 7, ids: ids, permitted: true))
        XCTAssertFalse(intent.admits(access: access, revision: 7, ids: ids, permitted: false))
        XCTAssertFalse(intent.admits(access: replacement, revision: 7, ids: ids, permitted: true))
        XCTAssertFalse(intent.admits(access: access, revision: 8, ids: ids, permitted: true))
        XCTAssertFalse(intent.admits(access: access, revision: 7, ids: Array(ids.reversed()), permitted: true))
        XCTAssertFalse(intent.admits(access: access, revision: 7, ids: Array(ids.dropLast()), permitted: true))
    }

    func testReorderPreservesRepeatedSongsAsDistinctOccurrencesInBothDirections() {
        XCTAssertEqual(intent().moving(IndexSet(integer: 0), to: 5),
                       ["song-b", "song-a-repeat", "song-c", "song-d", "song-a-first"])
        XCTAssertEqual(intent().moving(IndexSet(integer: 4), to: 0),
                       ["song-d", "song-a-first", "song-b", "song-a-repeat", "song-c"])
        XCTAssertEqual(intent().moving(IndexSet([1, 3]), to: 5),
                       ["song-a-first", "song-a-repeat", "song-d", "song-b", "song-c"])
        XCTAssertEqual(intent().moving(IndexSet([1, 3]), to: 0),
                       ["song-b", "song-c", "song-a-first", "song-a-repeat", "song-d"])
    }

    func testNativeDestinationSemanticsKeepNoOpMovesStable() {
        XCTAssertEqual(intent().moving(IndexSet([1, 2]), to: 3), ids)
        XCTAssertEqual(intent().moving(IndexSet(integer: 2), to: 2), ids)
        XCTAssertEqual(intent().moving(IndexSet(integer: 2), to: 3), ids)
        XCTAssertEqual(intent().moving(IndexSet(integersIn: 0..<5), to: 5), ids)
    }

    func testReorderRejectsInvalidOffsetsDestinationAndDuplicateOccurrenceIDs() {
        XCTAssertNil(intent().moving(IndexSet(), to: 0))
        XCTAssertNil(intent().moving(IndexSet(integer: 5), to: 0))
        XCTAssertNil(intent().moving(IndexSet([0, 6]), to: 1))
        XCTAssertNil(intent().moving(IndexSet(integer: 0), to: -1))
        XCTAssertNil(intent().moving(IndexSet(integer: 0), to: 6))
        XCTAssertNil(intent(ids: []).moving(IndexSet(integer: 0), to: 0))
        XCTAssertNil(intent(ids: ["same-occurrence", "same-occurrence"]).moving(IndexSet(integer: 0), to: 2))
    }

    func testHistoryConfirmationsBindOpaqueTokenReadGenerationAccessAndVisibility() {
        let token = MusicHistoryActionToken(rawValue: "synthetic-history-token")
        let intent = MusicReferenceHistoryIntent(access: access, request: request, token: token)
        XCTAssertTrue(intent.admits(access: access, request: request, token: token, permitted: true))
        XCTAssertFalse(intent.admits(access: access, request: request, token: token, permitted: false))
        XCTAssertFalse(intent.admits(access: replacement, request: request, token: token, permitted: true))
        XCTAssertFalse(intent.admits(access: access, request: replacement, token: token, permitted: true))
        XCTAssertFalse(intent.admits(access: access, request: request, token: nil, permitted: true))
        XCTAssertFalse(intent.admits(access: access, request: request,
                                    token: .init(rawValue: "new-detail-token"), permitted: true))
    }

    private func similar(active: Bool, status: MusicSimilarModeSnapshot.Status,
                         canRetry: Bool = false) -> MusicSimilarModeSnapshot {
        .init(id: request, active: active, status: status, seed: .init(title: "种子歌曲"),
              seedEntryID: ids[0], continueEnabled: true,
              candidates: [.init(id: "candidate", song: .init(title: "候选歌曲"))],
              batches: [], sourceIDs: ["101"], fromHome: false, provider: .songRoaming,
              error: nil, canRetry: canRetry, legacy: false)
    }

    func testReadyRecommendationPreviewShowsCandidatesButActiveSessionDoesNot() {
        let preview = MusicReferenceSimilarPresentation(snapshot: similar(active: false, status: .ready))
        XCTAssertTrue(preview.showsSection)
        XCTAssertEqual(preview.title, "根据 种子歌曲 推荐")
        XCTAssertEqual(preview.candidates.map(\.id), ["candidate"])
        XCTAssertNil(preview.status)
        let active = MusicReferenceSimilarPresentation(snapshot: similar(active: true, status: .ready))
        XCTAssertFalse(active.showsSection)
        XCTAssertEqual(active.title, "推荐续播")
        XCTAssertTrue(active.candidates.isEmpty)
        XCTAssertFalse(active.showsRetry)
    }

    func testLoadingNeverOffersRetryAndUsesTheRealPreviewOrContinuationState() {
        let preview = MusicReferenceSimilarPresentation(snapshot: similar(active: false, status: .loading, canRetry: true))
        let active = MusicReferenceSimilarPresentation(snapshot: similar(active: true, status: .loading, canRetry: true))
        XCTAssertTrue(preview.showsSection)
        XCTAssertTrue(active.showsSection)
        XCTAssertEqual(preview.status, "正在获取相似歌曲…")
        XCTAssertEqual(active.status, "正在获取下一批相似歌曲…")
        XCTAssertFalse(preview.showsRetry)
        XCTAssertFalse(preview.canRetry)
        XCTAssertFalse(active.canRetry)
    }

    func testRetryRequiresErrorOrEmptyAndOwnerPermissionRatherThanAnUnconditionalButton() {
        let statuses: [MusicSimilarModeSnapshot.Status] = [.error, .empty]
        for status in statuses {
            let blocked = MusicReferenceSimilarPresentation(snapshot: similar(active: true, status: status))
            let allowed = MusicReferenceSimilarPresentation(snapshot: similar(active: true, status: status, canRetry: true))
            XCTAssertTrue(blocked.showsSection)
            XCTAssertTrue(blocked.showsRetry)
            XCTAssertFalse(blocked.canRetry)
            XCTAssertTrue(allowed.canRetry)
            XCTAssertNotNil(blocked.status)
        }
        XCTAssertFalse(MusicReferenceSimilarPresentation(snapshot: similar(active: false, status: .ready, canRetry: true)).canRetry)
    }

    func testQueueMetricsUseFullViewportWithReferenceHeightCapAndSheetInsets() {
        let normal = MusicReferenceQueueMetrics(viewportWidth: 375, viewportHeight: 812)
        XCTAssertEqual(normal.width, 375)
        XCTAssertEqual(normal.height, 633.36, accuracy: 0.001)
        XCTAssertEqual(normal.horizontalPadding, 12)
        XCTAssertEqual(normal.handleHeight, 24)
        XCTAssertEqual(normal.headingBottomPadding, 8)
        XCTAssertEqual(normal.tabFontSize, 14)
        XCTAssertEqual(normal.selectionGap, 8)
        XCTAssertEqual(normal.rowControlWidth, 44)
        XCTAssertEqual(MusicReferenceQueueMetrics(viewportWidth: 440, viewportHeight: 956).height, 640)
        XCTAssertEqual(MusicReferenceQueueMetrics.bottomPadding(safeArea: 34), 34)
        XCTAssertEqual(MusicReferenceQueueMetrics.bottomPadding(safeArea: 0), 12)
    }

    func testQueueMediaQueriesIncludeTheirActual350And500Boundaries() {
        let narrow = MusicReferenceQueueMetrics(viewportWidth: 350, viewportHeight: 700)
        XCTAssertEqual(narrow.horizontalPadding, 6)
        XCTAssertEqual(narrow.tabFontSize, 12)
        XCTAssertEqual(narrow.selectionGap, 5)
        XCTAssertEqual(narrow.rowControlWidth, 40)
        XCTAssertEqual(MusicReferenceQueueMetrics(viewportWidth: 351, viewportHeight: 700).horizontalPadding, 12)
        let short = MusicReferenceQueueMetrics(viewportWidth: 844, viewportHeight: 500)
        XCTAssertEqual(short.width, 480)
        XCTAssertEqual(short.height, 460)
        XCTAssertEqual(short.handleHeight, 16)
        XCTAssertEqual(short.headingBottomPadding, 0)
        XCTAssertEqual(MusicReferenceQueueMetrics(viewportWidth: 844, viewportHeight: 501).handleHeight, 24)
    }

    func testQueueGeometryRejectsNonFiniteAndNeverEscapesATinyViewport() {
        for invalid in [Double.nan, .infinity, -.infinity, -1] {
            let value = MusicReferenceQueueMetrics(viewportWidth: invalid, viewportHeight: invalid)
            XCTAssertEqual(value.width, 0)
            XCTAssertEqual(value.height, 0)
            XCTAssertEqual(MusicReferenceQueueMetrics.historyCardWidth(pageWidth: invalid), 0)
            XCTAssertEqual(MusicReferenceQueueMetrics.bottomPadding(safeArea: invalid), 12)
        }
        XCTAssertEqual(MusicReferenceQueueMetrics(viewportWidth: 200, viewportHeight: 100).height, 100)
    }

    func testHistoryCardsUsePageWidthMinus32WithoutAnInventedMinimumWidth() {
        let sheet = MusicReferenceQueueMetrics(viewportWidth: 375, viewportHeight: 812)
        let page = sheet.width - sheet.horizontalPadding * 2
        XCTAssertEqual(MusicReferenceQueueMetrics.historyCardWidth(pageWidth: page), 319)
        XCTAssertEqual(MusicReferenceQueueMetrics.historyCardWidth(pageWidth: 100), 68)
        XCTAssertEqual(MusicReferenceQueueMetrics.historyCardWidth(pageWidth: 20), 0)
    }

    func testRecommendationPreviewDoesNotReserveFortyPercentForOneShortRow() {
        XCTAssertEqual(MusicReferenceQueueMetrics.previewQueueHeight(count: 1, availableHeight: 500), 64)
        XCTAssertEqual(MusicReferenceQueueMetrics.previewQueueHeight(count: 2, availableHeight: 500), 128)
        XCTAssertEqual(MusicReferenceQueueMetrics.previewQueueHeight(count: 20, availableHeight: 500), 200)
        XCTAssertEqual(MusicReferenceQueueMetrics.previewQueueHeight(count: 20, availableHeight: 40), 40)
        XCTAssertEqual(MusicReferenceQueueMetrics.previewQueueHeight(count: 1, availableHeight: .nan), 0)
    }

    func testHistoryDisplayIdentityTracksSummaryAndAccountButNotCardSelection() {
        func identity(title: String = "歌单", count: Int = 2, at: Double = 100, access: UUID) -> MusicReferenceHistoryDisplayIdentity {
            .init(.init(id: "snapshot", title: title, count: count, at: at), access: access)
        }
        let original = identity(access: access)
        XCTAssertEqual(original, identity(access: access))
        XCTAssertNotEqual(original, identity(title: "改名", access: access))
        XCTAssertNotEqual(original, identity(count: 3, access: access))
        XCTAssertNotEqual(original, identity(at: 101, access: access))
        XCTAssertNotEqual(original, identity(access: replacement))
        // Revocation of an action epoch doesn't erase this read-only identity.
        let token = MusicHistoryActionToken(rawValue: "synthetic-history-token")
        let command = MusicReferenceHistoryIntent(access: access, request: request, token: token)
        XCTAssertFalse(command.admits(access: access, request: replacement, token: token, permitted: true))
        XCTAssertEqual(original, identity(access: access))
    }
}

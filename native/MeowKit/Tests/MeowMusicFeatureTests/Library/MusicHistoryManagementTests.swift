import XCTest
import MeowMusicFeature

final class MusicHistoryManagementTests: XCTestCase {
    func testDisplayContractPreservesDuplicateOccurrencesWithoutPlaybackReferences() async throws {
        let provider: any MusicHistoryManaging = MusicHistoryContractFixture()
        let detail = try await provider.historyDetail(id: "archive")
        XCTAssertEqual(detail.id, detail.summary.id)
        XCTAssertEqual(detail.summary.count, detail.entries.count)
        XCTAssertEqual(detail.entries.map(\.id), ["entry-10", "entry-11"])
        XCTAssertEqual(detail.entries.map(\.index), [0, 1])
        XCTAssertEqual(detail.entries[0].title, detail.entries[1].title)
        XCTAssertEqual(detail.selectedIndex, 1)
        XCTAssertEqual(detail.sourceLabel, "搜索")
        XCTAssertNil(detail.entries[0].artwork)
    }

    func testHistoryActionsCarryExactDetailAndSeparateClearCategories() async throws {
        let fixture = MusicHistoryContractFixture()
        let provider: any MusicHistoryManaging = fixture
        let detail = try await provider.historyDetail(id: "archive")
        let resumed = try await provider.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 3)
        XCTAssertEqual(resumed.currentId, "entry-11")
        let explicit = try await provider.restoreHistory(detail: detail, startIndex: 0, expectedRevision: 4)
        XCTAssertEqual(explicit.currentId, "entry-10")
        try await provider.removeHistory(detail: detail)
        try await provider.clearHistory(.songs)
        try await provider.clearHistory(.playlists)
        let actions = await fixture.actions
        XCTAssertEqual(actions, ["restore:nil:3:opaque-fixture", "restore:0:4:opaque-fixture",
                                 "remove:opaque-fixture", "clear:songs", "clear:playlists"])
    }
}

private actor MusicHistoryContractFixture: MusicHistoryManaging {
    var actions: [String] = []
    func historyDetail(id: String) throws -> MusicHistoryDetail {
        .init(summary: .init(id: id, title: "Snapshot", count: 2, at: 1000),
              entries: (0..<2).map {
                  .init(id: "entry-\($0 + 10)", index: $0, title: "Same song", artist: "Artist",
                        album: "Album", artwork: nil, duration: 180)
              }, selectedIndex: 1, sourceLabel: "搜索", actionToken: .init(rawValue: "opaque-fixture"))
    }
    func removeHistory(detail: MusicHistoryDetail) { actions.append("remove:\(detail.actionToken.rawValue)") }
    func clearHistory(_ category: MusicHistoryCategory) {
        actions.append(category == .songs ? "clear:songs" : "clear:playlists")
    }
    func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?, expectedRevision: Int) -> MusicPlaybackState {
        actions.append("restore:\(startIndex.map(String.init) ?? "nil"):\(expectedRevision):\(detail.actionToken.rawValue)")
        return .init(revision: expectedRevision + 1, settingsRevision: 0, mode: .loop,
            qualityEnabled: ["standard"], qualityOrder: ["standard"], spatialEnabled: [], spatialOrder: [],
            lyricsEnabled: true, showTranslation: true, currentId: detail.entries[startIndex ?? 1].id,
            total: 2, position: 0, canNext: true, canPrevious: true)
    }
}

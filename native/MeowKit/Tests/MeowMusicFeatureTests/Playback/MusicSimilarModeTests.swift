import XCTest
import MeowMusicFeature

final class MusicSimilarModeTests: XCTestCase {
    func testHeadingsFollowContiguousQueueOccurrencesIncludingManualEdits() {
        let snapshot = MusicSimilarModeSnapshot(id: UUID(), active: true, status: .ready,
            seed: .init(title: "Seed"), seedEntryID: "seed", continueEnabled: true, candidates: [],
            batches: [.init(id: "batch", basedOn: .init(title: "Seed"), provider: .heartbeat, entryIDs: ["a", "b"]),
                      .init(id: "more", basedOn: .init(title: "Tail"), provider: .queueRecommendations, entryIDs: ["c"])],
            sourceIDs: ["1"], fromHome: false, provider: .heartbeat, error: nil, canRetry: false)
        XCTAssertEqual(snapshot.headings(for: ["seed", "a", "manual", "b", "c"]),
                       ["seed": "推荐种子", "a": "根据 Seed 推荐", "manual": "手动加入", "b": "根据 Seed 推荐", "c": "更多推荐"])
        XCTAssertNil(snapshot.headings(for: ["a", "b"])["b"])
    }
}

import XCTest
@testable import MeowMusicFeature

final class MusicPlaybackModePresentationTests: XCTestCase {
    func testSpecialChoiceRemainsAvailableAfterSelectingEachOrdinaryMode() {
        for (provider, special) in [
            (MusicSimilarModeProvider.heartbeat, MusicPlaybackModePresentation.Special.heartbeat),
            (.queueRecommendations, .recommendations), (.songRoaming, .roaming), (.artistRoaming, .roaming)
        ] {
            for mode in MusicPlaybackMode.allCases {
                for continuing in [false, true] {
                    let snapshot = MusicSimilarModeSnapshot(id: UUID(), active: true, status: .ready,
                        seed: .init(title: "Seed"), seedEntryID: "seed", continueEnabled: continuing,
                        candidates: [], batches: [], sourceIDs: [], fromHome: false,
                        provider: provider, error: nil, canRetry: false)
                    let value = MusicPlaybackModePresentation(mode: mode, similar: snapshot, recommendations: nil, fm: nil)
                    XCTAssertEqual(value.special, special)
                    XCTAssertEqual(value.continuing, continuing)
                    XCTAssertEqual(value.title, continuing ? special.title : mode.title)
                }
            }
        }
    }

    func testPendingSearchCandidatesOfferRecommendationWithoutInventingCheckpoint() {
        let snapshot = MusicQueueRecommendationSnapshot(id: UUID(), status: .ready, currentEntryID: "seed",
            continueEnabled: false, candidates: [.init(id: "candidate", song: .init(title: "Candidate"))],
            exhausted: false, error: nil, canRetry: false, active: false)
        let value = MusicPlaybackModePresentation(mode: .shuffle, similar: nil, recommendations: snapshot, fm: nil)
        XCTAssertEqual(value.special, .recommendations)
        XCTAssertFalse(value.continuing)
        XCTAssertEqual(value.title, "随机播放")
    }

    func testFMAndOrdinaryQueueExposeTheirCommittedChoice() {
        let fm = MusicFMPlaybackSnapshot(id: UUID(), action: .fm(mode: .default, submode: "", entry: nil),
            continueEnabled: true, loading: false, error: nil)
        let value = MusicPlaybackModePresentation(mode: .loop, similar: nil, recommendations: nil, fm: fm)
        XCTAssertEqual(value.special, .roaming)
        XCTAssertTrue(value.continuing)
        XCTAssertEqual(value.title, "漫游模式")
        for mode in MusicPlaybackMode.allCases {
            let ordinary = MusicPlaybackModePresentation(mode: mode, similar: nil, recommendations: nil, fm: nil)
            XCTAssertNil(ordinary.special)
            XCTAssertFalse(ordinary.continuing)
            XCTAssertEqual(ordinary.title, mode.title)
        }
    }
}

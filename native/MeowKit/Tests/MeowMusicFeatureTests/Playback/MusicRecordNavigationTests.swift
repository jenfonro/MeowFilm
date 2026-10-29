#if os(iOS) || os(macOS)
import XCTest
@testable import MeowMusicFeature

final class MusicRecordNavigationTests: XCTestCase {
    @MainActor func testRealSelectionIsDeliveredBeforeResolutionAndOnlyOneCommandIsAdmitted() async throws {
        let service = MusicCandidateViewModelService(), player = MusicCandidateViewModelPlayer()
        let resolverStarted = expectation(description: "resolver started")
        let gate = MusicCandidateViewModelGate { resolverStarted.fulfill() }
        await service.configure(resolve: gate)
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        let completed = expectation(description: "navigation completed")
        var selections: [String] = []
        XCTAssertTrue(model.requestNavigation(.next, onSelection: { entry in
            selections.append(entry.id)
            XCTAssertTrue(player.resources.isEmpty)
            XCTAssertEqual(model.current?.id, entry.id)
        }, onCompletion: { completed.fulfill() }))
        XCTAssertFalse(model.requestPrevious())
        await fulfillment(of: [resolverStarted], timeout: 2)
        XCTAssertEqual(selections, ["entry-2"])
        XCTAssertTrue(player.resources.isEmpty)
        await gate.release()
        await fulfillment(of: [completed], timeout: 2)
        XCTAssertEqual(selections, ["entry-2"])
        XCTAssertEqual(player.resources.count, 1)
        await model.close()
    }
    @MainActor func testProtectionBeforeDispatchCannotEmitASelectionOrStartAudio() async {
        let service = MusicCandidateViewModelService(), player = MusicCandidateViewModelPlayer()
        let model = MusicViewModel(service: service, player: player)
        await model.load()
        var selected = false
        XCTAssertTrue(model.requestNavigation(.next, onSelection: { _ in selected = true }))
        model.setAvailable(false)
        await model.close()
        XCTAssertFalse(selected)
        XCTAssertTrue(player.resources.isEmpty)
    }
}
#endif

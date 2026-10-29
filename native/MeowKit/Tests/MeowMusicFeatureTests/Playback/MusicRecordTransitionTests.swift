import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicRecordTransitionTests: XCTestCase {
    private let access = UUID(), attempt = UUID()
    private func fence(_ occurrence: String = "b", revision: Int = 1, settings: Int = 0,
                       access: UUID? = nil) -> MusicReferencePlayerFence {
        .init(occurrence: occurrence, attempt: attempt, access: access ?? self.access,
              queueRevision: revision, settingsRevision: settings)
    }
    private func entry(_ id: String) -> MusicQueueEntry {
        // Same recording, different occurrences must remain distinguishable.
        .init(id: id, track: .init(ref: .init(kind: .queue, id: id), songId: "same", title: "Synthetic"))
    }
    private func prepared() -> MusicRecordTransition {
        var value = MusicRecordTransition()
        value.synchronize(current: entry("b"), previous: entry("a"), next: entry("c"))
        return value
    }

    func testNativeDragOnlyCapturesItsOriginAndDoesNotIssueACommand() {
        var value = prepared()
        let original = value.slots
        XCTAssertTrue(value.begin(fence()))
        XCTAssertFalse(value.begin(fence()))
        XCTAssertEqual(value.phase, .dragging)
        XCTAssertNil(value.token)
        XCTAssertNil(value.direction)
        XCTAssertFalse(value.hasCommand)
        XCTAssertEqual(value.slots, original)
        XCTAssertFalse(value.accepts(fence("new", revision: 2)))
        value.cancel()
        XCTAssertEqual(value.center, 1)
        XCTAssertEqual(value.slots, original)
    }
    func testUnknownNeighborStaysUndecoratedUntilActualSelection() {
        var value = MusicRecordTransition()
        value.synchronize(current: entry("b"), previous: nil, next: nil)
        XCTAssertTrue(value.begin(fence()))
        XCTAssertNil(value.slots[value.neighbor(.previous)].entry)
        XCTAssertNil(value.slots[value.neighbor(.next)].entry)
    }
    func testSuccessfulSlidesRebaseTheIncomingSlotWithoutReplacingItsIdentity() throws {
        var value = prepared()
        let originalIDs = value.slots.map(\.id)
        for (step, direction) in [MusicDirection.next, .previous, .next].enumerated() {
            let from = step == 1 ? "c" : "b", to = direction == .next ? "c" : "b"
            let incoming = value.neighbor(direction)
            let token = try XCTUnwrap(value.request(direction, fence: fence(from, revision: step + 1)))
            XCTAssertNil(value.request(direction, fence: fence(from)), "no stacked command during one slide")
            XCTAssertTrue(value.selected(entry(to), token: token, current: fence(to, revision: step + 2)))
            XCTAssertTrue(value.finish(token, currentID: to))
            XCTAssertEqual(value.center, incoming)
            XCTAssertEqual(value.slots[incoming].entry?.id, to)
            XCTAssertEqual(value.slots.map(\.id), originalIDs)
            XCTAssertFalse(value.finish(token, currentID: to))
            value.synchronize(current: entry(to), previous: entry("b"), next: entry("c"))
        }
    }
    func testShortDragAndRejectedNavigationReturnWithoutChangingCurrentSlot() throws {
        var value = prepared()
        let center = value.center
        XCTAssertTrue(value.begin(fence()))
        let returned = try XCTUnwrap(value.returnToOrigin())
        XCTAssertTrue(value.finish(returned, currentID: "b"))
        XCTAssertEqual(value.center, center)
        let command = try XCTUnwrap(value.request(.next, fence: fence()))
        let failureReturn = try XCTUnwrap(value.returnToOrigin())
        XCTAssertFalse(value.selected(entry("c"), token: command, current: fence("c", revision: 2)))
        XCTAssertTrue(value.finish(failureReturn, currentID: "b"))
        XCTAssertEqual(value.center, center)
    }
    func testCancelledAndOldCompletionsCannotPromotePreviewsOrOverwriteNewGesture() throws {
        var value = prepared()
        let old = try XCTUnwrap(value.request(.next, fence: fence()))
        value.cancel()
        XCTAssertTrue(value.begin(fence()))
        XCTAssertFalse(value.selected(entry("c"), token: old, current: fence("c", revision: 2)))
        XCTAssertFalse(value.finish(old, currentID: "c"))
        XCTAssertEqual(value.phase, .dragging)
        XCTAssertFalse(value.accepts(fence("c", revision: 2)))
        value.cancel()
        let fresh = try XCTUnwrap(value.request(.next, fence: fence()))
        XCTAssertFalse(value.selected(entry("c"), token: fresh, current: fence("c", revision: 2, access: UUID())))
        XCTAssertFalse(value.selected(entry("c"), token: fresh, current: fence("c", revision: 2, settings: 1)))
        XCTAssertFalse(value.selected(entry("wrong"), token: fresh, current: fence("c", revision: 2)))
    }
    func testUnknownPreviewWaitsForTheRealSelectedOccurrence() throws {
        var value = prepared()
        value.synchronize(current: entry("b"), previous: entry("a"), next: nil)
        let token = try XCTUnwrap(value.request(.next, fence: fence()))
        XCTAssertEqual(value.phase, .awaiting)
        XCTAssertFalse(value.finish(token, currentID: "b"))
        XCTAssertTrue(value.selected(entry("actual-random"), token: token, current: fence("actual-random", revision: 2)))
        XCTAssertEqual(value.slots[value.neighbor(.next)].entry?.id, "actual-random")
        XCTAssertTrue(value.finish(token, currentID: "actual-random"))
    }
    func testRepeatedNativeSettlementCannotPromoteAnotherSlot() throws {
        var value = prepared()
        let token = try XCTUnwrap(value.request(.next, fence: fence()))
        XCTAssertTrue(value.selected(entry("c"), token: token, current: fence("c", revision: 2)))
        XCTAssertTrue(value.finish(token, currentID: "c"))
        let center = value.center
        for _ in 0..<10 { XCTAssertFalse(value.finish(token, currentID: "c")) }
        XCTAssertEqual(value.center, center)
    }
}

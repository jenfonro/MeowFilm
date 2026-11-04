import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class RecordPreviewRandom: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func draw() -> Double { lock.lock(); defer { lock.unlock() }; value += 1; return 0.75 }
    var calls: Int { lock.lock(); defer { lock.unlock() }; return value }
}

final class MusicNativeRecordPreviewTests: XCTestCase {
    func testPreviewUsesOccurrenceOrderAndDoesNotMutateStorageOrDrawShuffle() async throws {
        let random = RecordPreviewRandom(), store = NativeMusicMemoryDocument()
        let service = NativeMusicService(document: store, qq: NativeMusicQQFixture(),
            transport: NativeMusicNoNetwork(), random: { random.draw() })
        try await service.setAccess(available: true, generation: 1)
        var refs: [MusicReference] = []
        for query in ["First", "Second", "Third"] {
            refs.append(try await service.search(platform: "tx", query: query, page: 1, limit: 30).items[0].ref)
        }
        let initial = try await service.changeQueue(.replace(refs, index: 1), expectedRevision: 0)
        let entries = try await service.queue().entries
        _ = try await service.updateSettings(.init(ifRevision: 0, mode: .order))
        let before = store.snapshot()
        let ordered = try await service.state()
        XCTAssertEqual(ordered.previousPreviewID, entries[0].id)
        XCTAssertEqual(ordered.nextPreviewID, entries[2].id)
        XCTAssertEqual(ordered.fmPreviousPreviewID, entries[0].id)
        XCTAssertEqual(ordered.fmNextPreviewID, entries[2].id)
        XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
        XCTAssertEqual(random.calls, 0)
        let shuffle = try await service.updateSettings(.init(ifRevision: ordered.settingsRevision, mode: .shuffle))
        XCTAssertNil(shuffle.nextPreviewID)
        XCTAssertTrue(shuffle.canNext)
        XCTAssertEqual(random.calls, 0)
        XCTAssertEqual(shuffle.fmNextPreviewID, entries[2].id, "FM keeps its sequential semantics under shuffle")
        let advanced = try await service.advance(.next, reason: .manual, expectedRevision: initial.revision)
        XCTAssertEqual(random.calls, 1)
        XCTAssertEqual(advanced.state.previousPreviewID, entries[1].id, "shuffle previous previews real history")
        await service.close()
    }
    func testPriorityAndManualSingleLoopPreviewAreExactButFMTailDoesNotWrap() async throws {
        let service = try await NativeMusicFixture.service()
        let first = try await service.search(platform: "tx", query: "First", page: 1, limit: 30).items[0]
        let next = try await service.search(platform: "tx", query: "Next", page: 1, limit: 30).items[0]
        let initial = try await service.changeQueue(.replace([first.ref, next.ref], index: 1), expectedRevision: 0)
        let entries = try await service.queue().entries
        let single = try await service.updateSettings(.init(ifRevision: 0, mode: .single))
        XCTAssertEqual(single.nextPreviewID, entries[0].id, "manual next is not ended/single-repeat")
        XCTAssertNil(single.fmNextPreviewID, "preview must not fetch or invent the next FM batch")
        let shuffled = try await service.updateSettings(.init(ifRevision: single.settingsRevision, mode: .shuffle))
        XCTAssertNil(shuffled.nextPreviewID)
        let priority = try await service.changeQueue(.append([first.ref], next: true), expectedRevision: initial.revision)
        let appended = try await service.queue().entries
        let selected = try XCTUnwrap(priority.nextPreviewID)
        XCTAssertNotEqual(selected, entries[0].id, "duplicate recording is a new exact occurrence")
        XCTAssertEqual(selected, appended.last?.id)
        let advanced = try await service.advance(.next, reason: .manual, expectedRevision: priority.revision)
        XCTAssertEqual(advanced.selectedId, selected)
        await service.close()
    }
}

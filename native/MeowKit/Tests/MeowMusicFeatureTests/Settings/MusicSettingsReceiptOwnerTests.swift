#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

@MainActor
final class MusicSettingsReceiptOwnerTests: XCTestCase {
    func testUnknownBelongsToSettingAndStoreAndOnlyLaterConfirmedWriteClearsIt() {
        let owner = MusicSettingsReceiptOwner(), otherStore = MusicSettingsReceiptOwner()
        let first = owner.begin(settings: ["playback/officialSources"], operation: "保存音源")
        owner.settle(first, failure: .commitUnconfirmed)
        let notice = owner.unknown.first
        XCTAssertEqual(notice?.setting, "playback/officialSources")
        XCTAssertEqual(notice?.message, MusicServiceError.commitUnconfirmed.message(operation: "保存音源"))
        otherStore.settle(otherStore.begin(settings: ["playback/officialSources"], operation: "保存音源"), failure: nil)
        owner.settle(owner.begin(settings: ["playback/requestQuality"], operation: "保存音质"), failure: nil)
        XCTAssertEqual(owner.unknown.first, notice)
        for failure in [MusicServiceError.storageFailed, .storageConflict, .staleRevision, .cancelled] {
            owner.settle(owner.begin(settings: ["playback/officialSources"], operation: "保存音源"), failure: failure)
            XCTAssertEqual(owner.unknown.first, notice)
        }
        owner.settle(owner.begin(settings: ["playback/officialSources"], operation: "保存音源"), failure: nil)
        XCTAssertTrue(owner.unknown.isEmpty)
        XCTAssertTrue(otherStore.unknown.isEmpty)
    }

    func testSubmissionOrderNotCompletionOrderGovernsLateUnknownAndLateSuccess() {
        let owner = MusicSettingsReceiptOwner()
        let oldUnknown = owner.begin(settings: ["home/selectedSource"], operation: "保存数据源")
        let newerSuccess = owner.begin(settings: ["home/selectedSource"], operation: "保存数据源")
        owner.settle(newerSuccess, failure: nil)
        owner.settle(oldUnknown, failure: .commitUnconfirmed)
        XCTAssertTrue(owner.unknown.isEmpty, "A late old unknown cannot revive an acknowledged setting")

        let oldSuccess = owner.begin(settings: ["home/selectedSource"], operation: "保存数据源")
        let newerUnknown = owner.begin(settings: ["home/selectedSource"], operation: "保存数据源")
        owner.settle(newerUnknown, failure: .commitUnconfirmed)
        let notice = owner.unknown.first
        owner.settle(oldSuccess, failure: nil)
        XCTAssertEqual(owner.unknown.first, notice, "A late old success cannot clear a later uncertain write")
        owner.settle(oldUnknown, failure: .commitUnconfirmed)
        XCTAssertEqual(owner.unknown.first, notice)
    }

    func testPatchSettlesOnlyItsOwnSettingsAndLeavesIndependentUnknownNotices() {
        let owner = MusicSettingsReceiptOwner()
        owner.settle(owner.begin(settings: ["metadata/platforms", "metadata/enabledPlatforms"],
                                 operation: "保存歌词与封面"), failure: .commitUnconfirmed)
        owner.settle(owner.begin(settings: ["fm/kg/selection"], operation: "保存漫游"), failure: .commitUnconfirmed)
        owner.settle(owner.begin(settings: ["metadata/platforms"], operation: "保存顺序"), failure: nil)
        XCTAssertEqual(Set(owner.unknown.map(\.setting)), ["metadata/enabledPlatforms", "fm/kg/selection"])
        owner.settle(owner.begin(settings: ["metadata/enabledPlatforms"], operation: "保存开关"), failure: nil)
        XCTAssertEqual(owner.unknown.map(\.setting), ["fm/kg/selection"])
    }

    func testSubmittedWorkOutlivesCancelledPageAndFlushWaitsForPhysicalSettlementWithoutRetry() async {
        let owner = MusicSettingsReceiptOwner(), gate = ReceiptSettlementGate()
        var writes = 0, physicallyReturned = false, flushed = false
        let submitted: Task<Int, Error> = owner.submit(settings: ["scripts/source/enabled"], operation: "保存音源") {
            writes += 1
            await gate.wait()
            XCTAssertFalse(Task.isCancelled)
            physicallyReturned = true
            throw MusicServiceError.commitUnconfirmed
        }
        let page = Task { _ = await submitted.result }
        await gate.started()
        page.cancel()
        let drain = Task { await owner.flush(); flushed = true }
        XCTAssertFalse(physicallyReturned); XCTAssertFalse(flushed)
        await gate.release()
        await page.value; await drain.value
        XCTAssertTrue(physicallyReturned); XCTAssertTrue(flushed)
        XCTAssertEqual(writes, 1)
        XCTAssertEqual(owner.unknown.map(\.setting), ["scripts/source/enabled"])
        await owner.flush()
        XCTAssertEqual(writes, 1, "An unknown receipt must not enqueue a replay")
    }
}

private actor ReceiptSettlementGate {
    private var entered = false, released = false
    private var arrivals: [CheckedContinuation<Void, Never>] = []
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true; arrivals.forEach { $0.resume() }; arrivals.removeAll()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func started() async { if !entered { await withCheckedContinuation { arrivals.append($0) } } }
    func release() { released = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}
#endif

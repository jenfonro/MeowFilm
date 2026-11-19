import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
import MeowStorageTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

final class MusicNeteaseFeedbackOutboxTests: XCTestCase {
    private let setting = MusicPlaybackFeedbackSetting(enabled: true, generation: 1)
    private let now = 1_800_000_010.0
    private func store() throws -> SQLiteDatabase { try storageTestDatabase(self) }
    private func ready(_ store: SQLiteDatabase, scope: String = "original-account") throws -> MusicNeteaseFeedbackOutbox {
        let queue = MusicNeteaseFeedbackOutbox(database: store)
        try queue.configure(setting, at: now); try queue.activate(scope: scope, at: now)
        return queue
    }
    private func enqueue(_ queue: MusicNeteaseFeedbackOutbox, _ event: MusicPlaybackFeedbackReport.Event,
                         instance: UUID, scope: String = "original-account") throws -> MusicPlaybackFeedbackReceipt {
        try queue.enqueue(MusicAccountFeedbackFixture.report(event, platform: .netease, instance: instance),
            scope: scope, setting: setting, at: now)
    }
    private func rows(_ store: SQLiteDatabase) throws -> [[String: Any]] {
        guard let data = try musicFeedbackData(store) else { return [] }
        let document = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        return try XCTUnwrap(document["entries"] as? [[String: Any]])
    }
    func testDurableAdmissionPairsStartBeforeEndAndACKRemovesOnlyAcknowledgedEvent() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store), instance = UUID()
        XCTAssertEqual(try enqueue(queue, .start, instance: instance), .registered)
        XCTAssertEqual(try enqueue(queue, .end, instance: instance), .registered)
        let ids = try queue.candidates(at: now)
        XCTAssertEqual(ids.count, 2)
        XCTAssertNil(try queue.reserve(ids[1]), "Unconfirmed start cannot be bypassed by its end")
        let (start, gate) = try XCTUnwrap(queue.reserve(ids[0]))
        XCTAssertTrue(gate.isActive)
        XCTAssertEqual(try rows(store).map { $0["state"] as? String }, ["inflight", "pending"])
        try queue.complete(start, confirmed: true, notSent: false)
        XCTAssertEqual(try rows(store).count, 1)
        XCTAssertEqual(try rows(store).first?["startConfirmed"] as? Bool, true)
        let (end, _) = try XCTUnwrap(queue.reserve(ids[1]))
        XCTAssertEqual(end.instance, start.instance)
        try queue.complete(end, confirmed: true, notSent: false)
        XCTAssertNil(try musicFeedbackData(store))
        XCTAssertTrue(try queue.candidates(at: now).isEmpty)
        }
    }
    func testClearlyNotSentRecoversAcrossNormalCloseAndColdStartWithOriginalTimeAndNoCredentials() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store), instance = UUID()
        _ = try enqueue(queue, .start, instance: instance)
        _ = try enqueue(queue, .end, instance: instance)
        let ids = try queue.candidates(at: now)
        let (start, _) = try XCTUnwrap(queue.reserve(ids[0]))
        try queue.complete(start, confirmed: false, notSent: true)
        queue.suspend(preserving: true, closing: true)
        let data = try XCTUnwrap(musicFeedbackData(store))
        let text = String(decoding: data, as: UTF8.self)
        for forbidden in ["MUSIC_U", "Cookie", "token", "credential", "synthetic-user-"] {
            XCTAssertFalse(text.contains(forbidden), forbidden)
        }
        let restored = try ready(store)
        let coldIDs = try restored.candidates(at: now)
        XCTAssertEqual(coldIDs, ids)
        let (cold, _) = try XCTUnwrap(restored.reserve(coldIDs[0]))
        XCTAssertEqual(cold.occurredAt, 1_800_000_000)
        XCTAssertEqual(try RuntimeJSON.decode(cold.eventBody()), try RuntimeJSON.decode(start.eventBody()))
        try restored.complete(cold, confirmed: true, notSent: false)
        let (end, _) = try XCTUnwrap(restored.reserve(coldIDs[1]))
        XCTAssertEqual(end.occurredAt, 1_800_000_001.25)
        }
    }
    func testUnknownStartOrInterruptedInflightNeverReplaysAndNeverLeavesOrphanEnd() throws {
        try storageTestIO { [self] in
        for interrupted in [false, true] {
            let store = try store(), queue = try ready(store), instance = UUID()
            _ = try enqueue(queue, .start, instance: instance)
            _ = try enqueue(queue, .end, instance: instance)
            let first = try XCTUnwrap(queue.candidates(at: now).first)
            let (start, _) = try XCTUnwrap(queue.reserve(first))
            if !interrupted { try queue.complete(start, confirmed: false, notSent: false) }
            let cold = try ready(store)
            XCTAssertTrue(try cold.candidates(at: now).isEmpty)
            XCTAssertNil(try musicFeedbackData(store))
        }
        }
    }
    func testUnknownEndNeverReplaysButAnAcknowledgedStartCanPairWithSafeColdEnd() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store), instance = UUID()
        _ = try enqueue(queue, .start, instance: instance)
        let first = try XCTUnwrap(queue.candidates(at: now).first)
        let (start, _) = try XCTUnwrap(queue.reserve(first))
        try queue.complete(start, confirmed: true, notSent: false)
        XCTAssertEqual(try enqueue(queue, .end, instance: instance), .registered)
        queue.suspend(preserving: true, closing: true)
        let cold = try ready(store), ids = try cold.candidates(at: now)
        XCTAssertEqual(ids.count, 1)
        let (end, _) = try XCTUnwrap(cold.reserve(ids[0]))
        XCTAssertEqual(end.phase, .end)
        // Simulated crash after physical end dispatch, before saving its ACK.
        let next = try ready(store)
        XCTAssertTrue(try next.candidates(at: now).isEmpty)
        }
    }
    func testFrozenHeartbeatSceneAndActualSourceSurviveSafeColdRecovery() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store), instance = UUID()
        let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "actual-heart-source", sourceID: "actual-source-id", algorithm: "actual-alg",
            origin: .init(kind: .heartbeat, platform: .netease, sourceID: "501"))
        for event in [MusicPlaybackFeedbackReport.Event.start, .end] {
            let value = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: event,
                duration: 0, listened: event == .start ? 0 : 4, mediaListened: event == .start ? 0 : 8,
                position: event == .start ? 0 : 8, rate: 2, reason: event == .start ? nil : .ui,
                startedAt: now - 5, occurredAt: event == .start ? now - 5 : now - 1)
            XCTAssertEqual(try queue.enqueue(value, scope: "original-account", setting: setting, at: now), .registered)
        }
        queue.suspend(preserving: true, closing: true)
        let cold = try ready(store), ids = try cold.candidates(at: now)
        XCTAssertEqual(ids.count, 2)
        for (index, id) in ids.enumerated() {
            let (entry, _) = try XCTUnwrap(cold.reserve(id))
            let body = try RuntimeJSON.decode(entry.eventBody())
            XCTAssertEqual(body[music: "event"], .string(index == 0 ? "start" : "end"))
            XCTAssertEqual(body[music: "listened"], .number(index == 0 ? 0 : 4))
            XCTAssertEqual(body[music: "mediaListened"], .number(index == 0 ? 0 : 8))
            XCTAssertEqual(body[music: "target"][music: "origin"][music: "kind"], .string("heartbeat"))
            XCTAssertEqual(body[music: "target"][music: "source"], .string("actual-heart-source"))
            XCTAssertEqual(body[music: "target"][music: "sourceId"], .string("actual-source-id"),
                "The local favorite list is not an upstream source ID")
            try cold.complete(entry, confirmed: true, notSent: false)
        }
        }
    }
    func testNormalCloseKeepsSafeEndAfterAcknowledgedStartAndRejectsFurtherAdmissions() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store), instance = UUID()
        _ = try enqueue(queue, .start, instance: instance)
        let id = try XCTUnwrap(queue.candidates(at: now).first)
        let (start, _) = try XCTUnwrap(queue.reserve(id))
        try queue.complete(start, confirmed: true, notSent: false)
        queue.stopDispatching()
        XCTAssertEqual(try enqueue(queue, .end, instance: instance), .registered)
        queue.suspend(preserving: true, closing: true)
        XCTAssertThrowsError(try enqueue(queue, .start, instance: UUID()))
        let cold = try ready(store), ids = try cold.candidates(at: now)
        XCTAssertEqual(ids.count, 1)
        let (end, _) = try XCTUnwrap(cold.reserve(ids[0]))
        XCTAssertEqual(end.phase, .end)
        XCTAssertTrue(end.startConfirmed)
        XCTAssertEqual(end.occurredAt, 1_800_000_001.25)
        }
    }
    func testAccountSettingAndConsentRevocationFenceQueuedAndPhysicalWork() throws {
        try storageTestIO { [self] in
        for change in ["account", "disabled", "generation", "revoke"] {
            let store = try store(), queue = try ready(store), instance = UUID()
            _ = try enqueue(queue, .start, instance: instance)
            let id = try XCTUnwrap(queue.candidates(at: now).first)
            let (_, authorization) = try XCTUnwrap(queue.reserve(id))
            switch change {
            case "account": try queue.activate(scope: "replacement-account", at: now)
            case "disabled": try queue.configure(.init(enabled: false, generation: 2), at: now)
            case "generation": try queue.configure(.init(enabled: true, generation: 2), at: now)
            default: queue.suspend(preserving: false, revokingConsent: true)
            }
            XCTAssertFalse(authorization.isActive, change)
            XCTAssertTrue(try rows(store).isEmpty, change)
            XCTAssertNil(try queue.reserve(id), change)
            XCTAssertThrowsError(try enqueue(queue, .end, instance: instance), change)
        }
        }
    }
    func testExpiryAndCapacityAreResourceLimitsAndEvictWholeUnconfirmedPairs() throws {
        try storageTestIO { [self] in
        let store = try store(), queue = try ready(store)
        var first: UUID?
        for _ in 0..<130 {
            let instance = UUID(); if first == nil { first = instance }
            _ = try enqueue(queue, .start, instance: instance)
            _ = try enqueue(queue, .end, instance: instance)
        }
        let rows = try rows(store)
        XCTAssertEqual(rows.count, 256)
        XCTAssertFalse(rows.contains { $0["instance"] as? String == first?.uuidString })
        let grouped = Dictionary(grouping: rows) { $0["instance"] as? String }
        XCTAssertTrue(grouped.values.allSatisfy { $0.count == 2 })
        XCTAssertTrue(try queue.candidates(at: now + 7 * 86_400).isEmpty)
        XCTAssertNil(try musicFeedbackData(store))
        }
    }
    func testStorageFailureCannotAdmitOrDispatchAndMalformedRowsAreNotSilentlyOverwritten() throws {
        try storageTestIO { [self] in
        let limited = try store(), queue = try ready(limited)
        try storageTestReadOnly(limited, true)
        XCTAssertThrowsError(try enqueue(queue, .start, instance: UUID()))
        XCTAssertTrue(try queue.candidates(at: now).isEmpty)
        try storageTestReadOnly(limited, false)
        let store = try store(), healthy = try ready(store)
        _ = try enqueue(healthy, .start, instance: UUID())
        try store.transactionSync { try $0.execute("UPDATE ios_music_feedback_outbox SET id='invalid-id'") }
        let original = try musicFeedbackData(store)
        let corrupted = MusicNeteaseFeedbackOutbox(database: store)
        XCTAssertThrowsError(try corrupted.configure(setting, at: now))
        XCTAssertEqual(try musicFeedbackData(store), original)
        corrupted.stopDispatching(); corrupted.suspend(preserving: true, closing: true)
        XCTAssertEqual(try musicFeedbackData(store), original, "Normal close preserves unreadable durable rows")
        corrupted.suspend(preserving: false, revokingConsent: true)
        XCTAssertNil(try musicFeedbackData(store), "Explicit revocation clears only its own queue")
        }
    }
    func testInflightWriteFailurePreventsSendAndACKWriteFailureCannotMakeInflightReplayable() throws {
        try storageTestIO { [self] in
        for ack in [false, true] {
            let store = try store()
            let queue = try ready(store), instance = UUID()
            _ = try enqueue(queue, .start, instance: instance)
            let id = try XCTUnwrap(queue.candidates(at: now).first)
            var dispatched: MusicNeteaseFeedbackOutbox.Entry?
            if ack { dispatched = try XCTUnwrap(queue.reserve(id)).0 }
            try storageTestReadOnly(store, true)
            if let dispatched { XCTAssertThrowsError(try queue.complete(dispatched, confirmed: true, notSent: false)) }
            else { XCTAssertThrowsError(try queue.reserve(id), "A sender must never receive an entry before inflight is durable") }
            try storageTestReadOnly(store, false)
            let cold = try ready(store), candidates = try cold.candidates(at: now)
            XCTAssertEqual(candidates.count, ack ? 0 : 1,
                "Failed ACK persistence retains only a spent inflight record; failure before send remains safe")
        }
        }
    }
}

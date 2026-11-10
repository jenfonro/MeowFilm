import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

final class MusicNeteaseFeedbackOutboxTests: XCTestCase {
    private let setting = MusicPlaybackFeedbackSetting(enabled: true, generation: 1)
    private let now = 1_800_000_010.0
    private func store(maximum: Int = 8 * 1024 * 1024) throws -> DocumentStore {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return try .init(baseURL: root, namespace: "synthetic-pending", maximumDocumentBytes: maximum)
    }
    private func ready(_ store: DocumentStore, scope: String = "original-account") throws -> MusicNeteaseFeedbackOutbox {
        let queue = MusicNeteaseFeedbackOutbox(store: store)
        try queue.configure(setting, at: now); try queue.activate(scope: scope, at: now)
        return queue
    }
    private func enqueue(_ queue: MusicNeteaseFeedbackOutbox, _ event: MusicPlaybackFeedbackReport.Event,
                         instance: UUID, scope: String = "original-account") throws -> MusicPlaybackFeedbackReceipt {
        try queue.enqueue(MusicAccountFeedbackFixture.report(event, platform: .netease, instance: instance),
            scope: scope, setting: setting, at: now)
    }
    private func rows(_ store: DocumentStore) throws -> [[String: Any]] {
        guard let data = try store.read(key: MusicNeteaseFeedbackOutbox.key) else { return [] }
        let document = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        return try XCTUnwrap(document["entries"] as? [[String: Any]])
    }
    func testDurableAdmissionPairsStartBeforeEndAndACKRemovesOnlyAcknowledgedEvent() throws {
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
        XCTAssertNil(try store.read(key: MusicNeteaseFeedbackOutbox.key))
        XCTAssertTrue(try queue.candidates(at: now).isEmpty)
    }
    func testClearlyNotSentRecoversAcrossNormalCloseAndColdStartWithOriginalTimeAndNoCredentials() throws {
        let store = try store(), queue = try ready(store), instance = UUID()
        _ = try enqueue(queue, .start, instance: instance)
        _ = try enqueue(queue, .end, instance: instance)
        let ids = try queue.candidates(at: now)
        let (start, _) = try XCTUnwrap(queue.reserve(ids[0]))
        try queue.complete(start, confirmed: false, notSent: true)
        queue.suspend(preserving: true, closing: true)
        let data = try XCTUnwrap(store.read(key: MusicNeteaseFeedbackOutbox.key))
        let text = String(decoding: data, as: UTF8.self)
        for forbidden in ["MUSIC_U", "Cookie", "token", "credential", "synthetic-user-"] {
            XCTAssertFalse(text.contains(forbidden), forbidden)
        }
        let restored = try ready(store)
        let coldIDs = try restored.candidates(at: now)
        XCTAssertEqual(coldIDs, ids)
        let (cold, _) = try XCTUnwrap(restored.reserve(coldIDs[0]))
        XCTAssertEqual(cold.occurredAt, 1_800_000_000)
        XCTAssertEqual(try cold.records(), try start.records())
        try restored.complete(cold, confirmed: true, notSent: false)
        let (end, _) = try XCTUnwrap(restored.reserve(coldIDs[1]))
        XCTAssertEqual(end.occurredAt, 1_800_000_001.25)
    }
    func testUnknownStartOrInterruptedInflightNeverReplaysAndNeverLeavesOrphanEnd() throws {
        for interrupted in [false, true] {
            let store = try store(), queue = try ready(store), instance = UUID()
            _ = try enqueue(queue, .start, instance: instance)
            _ = try enqueue(queue, .end, instance: instance)
            let first = try XCTUnwrap(queue.candidates(at: now).first)
            let (start, _) = try XCTUnwrap(queue.reserve(first))
            if !interrupted { try queue.complete(start, confirmed: false, notSent: false) }
            let cold = try ready(store)
            XCTAssertTrue(try cold.candidates(at: now).isEmpty)
            XCTAssertNil(try store.read(key: MusicNeteaseFeedbackOutbox.key))
        }
    }
    func testUnknownEndNeverReplaysButAnAcknowledgedStartCanPairWithSafeColdEnd() throws {
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
    func testFrozenHeartbeatSceneAndActualSourceSurviveSafeColdRecovery() throws {
        let store = try store(), queue = try ready(store), instance = UUID()
        let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "actual-heart-source", sourceID: "actual-source-id", algorithm: "actual-alg",
            origin: .init(kind: .heartbeat, platform: .netease, sourceID: "501"))
        var expected: [Data] = []
        for event in [MusicPlaybackFeedbackReport.Event.start, .end] {
            let value = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: event,
                duration: 0, listened: event == .start ? 0 : 4, mediaListened: event == .start ? 0 : 8,
                position: event == .start ? 0 : 8, rate: 2, reason: event == .start ? nil : .ui,
                startedAt: now - 5, occurredAt: event == .start ? now - 5 : now - 1)
            XCTAssertEqual(try queue.enqueue(value, scope: "original-account", setting: setting, at: now), .registered)
            expected.append(try MusicAccountFeedbackNeteaseWire.records(value))
        }
        queue.suspend(preserving: true, closing: true)
        let cold = try ready(store), ids = try cold.candidates(at: now)
        XCTAssertEqual(ids.count, 2)
        for (index, id) in ids.enumerated() {
            let (entry, _) = try XCTUnwrap(cold.reserve(id))
            XCTAssertEqual(try entry.records(), expected[index])
            let text = String(decoding: try entry.records(), as: UTF8.self)
            XCTAssertTrue(text.contains("\"play_scene\":\"heartbeat\""))
            XCTAssertTrue(text.contains("\"source\":\"actual-heart-source\""))
            XCTAssertFalse(text.contains("\"sourceId\":\"501\""), "The local favorite list is not an upstream source ID")
            try cold.complete(entry, confirmed: true, notSent: false)
        }
    }
    func testNormalCloseKeepsSafeEndAfterAcknowledgedStartAndRejectsFurtherAdmissions() throws {
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
    func testAccountSettingAndConsentRevocationFenceQueuedAndPhysicalWork() throws {
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
    func testExpiryAndCapacityAreResourceLimitsAndEvictWholeUnconfirmedPairs() throws {
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
        XCTAssertNil(try store.read(key: MusicNeteaseFeedbackOutbox.key))
    }
    func testStorageFailureCannotAdmitOrDispatchAndMalformedDocumentIsNotSilentlyOverwritten() throws {
        let limited = try store(maximum: 16), queue = try ready(limited)
        XCTAssertThrowsError(try enqueue(queue, .start, instance: UUID()))
        XCTAssertTrue(try queue.candidates(at: now).isEmpty)
        let store = try store()
        let original = Data("not-a-pending-document".utf8)
        try store.write(original, key: MusicNeteaseFeedbackOutbox.key)
        let corrupted = MusicNeteaseFeedbackOutbox(store: store)
        XCTAssertThrowsError(try corrupted.configure(setting, at: now))
        XCTAssertEqual(try store.read(key: MusicNeteaseFeedbackOutbox.key), original)
        corrupted.stopDispatching()
        corrupted.suspend(preserving: true, closing: true)
        XCTAssertEqual(try store.read(key: MusicNeteaseFeedbackOutbox.key), original,
            "Normal close cannot silently erase an unreadable document")
        corrupted.suspend(preserving: false, revokingConsent: true)
        XCTAssertNil(try store.read(key: MusicNeteaseFeedbackOutbox.key), "Explicit revocation may safely unlink its own unreadable queue")
    }
    func testInflightWriteFailurePreventsSendAndACKWriteFailureCannotMakeInflightReplayable() throws {
        for ack in [false, true] {
            let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            addTeardownBlock { try? FileManager.default.removeItem(at: root) }
            let store = try DocumentStore(baseURL: root, namespace: "pending")
            let queue = try ready(store), instance = UUID()
            _ = try enqueue(queue, .start, instance: instance)
            let id = try XCTUnwrap(queue.candidates(at: now).first)
            var dispatched: MusicNeteaseFeedbackOutbox.Entry?
            if ack { dispatched = try XCTUnwrap(queue.reserve(id)).0 }
            let directory = root.appendingPathComponent("pending"), displaced = root.appendingPathComponent("displaced")
            try FileManager.default.moveItem(at: directory, to: displaced)
            try Data("synthetic-path-fault".utf8).write(to: directory)
            if let dispatched { XCTAssertThrowsError(try queue.complete(dispatched, confirmed: true, notSent: false)) }
            else { XCTAssertThrowsError(try queue.reserve(id), "A sender must never receive an entry before inflight is durable") }
            try FileManager.default.removeItem(at: directory)
            try FileManager.default.moveItem(at: displaced, to: directory)
            let cold = try ready(store), candidates = try cold.candidates(at: now)
            XCTAssertEqual(candidates.count, ack ? 0 : 1,
                "Failed ACK persistence retains only a spent inflight record; failure before send remains safe")
        }
    }
}

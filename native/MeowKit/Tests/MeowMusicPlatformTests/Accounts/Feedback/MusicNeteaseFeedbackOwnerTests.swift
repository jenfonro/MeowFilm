import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

@MainActor
final class MusicNeteaseFeedbackOwnerTests: XCTestCase {
    private struct Fixture {
        let identity: AccountIdentity
        let credentials = MusicUpdateStore()
        let document: DocumentStore
        let clock = MusicAccountTestClock()
        let provider = MusicAccountFeedbackProviderFixture()
        let service: NativeMusicAccountService
        init(document: DocumentStore) throws {
            identity = try MusicAccountFixture.identity("wy"); self.document = document
            credentials.seed(try MusicUpdateProvider.credential(), for: identity)
            service = .init(accounts: AccountSessionCoordinator(store: credentials), scopes: [.netease: identity],
                providers: [.netease: provider], urlCacheStore: document, clock: { [clock] in clock.now() })
        }
        func cold() -> NativeMusicAccountService {
            .init(accounts: AccountSessionCoordinator(store: credentials), scopes: [.netease: identity],
                providers: [.netease: provider], urlCacheStore: document, clock: { [clock] in clock.now() })
        }
    }
    private let settings = MusicPlaybackFeedbackSettings(revision: 1,
        platforms: [.netease: .init(enabled: true, generation: 1)])
    private func fixture(maximumBytes: Int = 8 * 1024 * 1024) throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return try .init(document: .init(baseURL: root, namespace: "feedback-owner", maximumDocumentBytes: maximumBytes))
    }
    private func ready(_ owner: NativeMusicAccountService) async throws {
        _ = try await owner.setAccess(available: true, generation: 1)
        try await owner.configurePlaybackFeedback(settings)
        _ = try await owner.validate(platform: .netease)
        await owner.flushPendingPlaybackFeedback()
    }
    private func report(_ event: MusicPlaybackFeedbackReport.Event, instance: UUID) -> MusicPlaybackFeedbackReport {
        MusicAccountFeedbackFixture.report(event, platform: .netease, instance: instance)
    }
    func testOfflinePairRegistersLocallyThenRecoversInOrderWithoutMovingOriginalTime() async throws {
        let f = try fixture(); try await ready(f.service)
        await f.provider.setUploadOutcome(.notSent)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        XCTAssertTrue(lease.registersLocally)
        let instance = UUID()
        let started = try await lease.report(report(.start, instance: instance))
        XCTAssertEqual(started, .registered)
        await f.service.flushPendingPlaybackFeedback()
        f.clock.advance(2)
        let finished = try await lease.report(report(.end, instance: instance))
        XCTAssertEqual(finished, .registered)
        await f.service.flushPendingPlaybackFeedback()
        let before = await f.provider.uploads()
        XCTAssertTrue(before.allSatisfy { String(decoding: $0, as: UTF8.self).contains("\u{1}_plv\u{1}") })
        let credentials = f.credentials.peek(f.identity)
        await f.provider.setUploadOutcome(.confirmed)
        _ = try await f.service.validate(platform: .netease) // Account-ready trigger, no timer.
        await f.service.flushPendingPlaybackFeedback()
        let sent = Array((await f.provider.uploads()).dropFirst(before.count))
        XCTAssertEqual(sent.count, 2)
        XCTAssertTrue(String(decoding: sent[0], as: UTF8.self).hasPrefix("1800000000\u{1}_plv\u{1}"))
        XCTAssertTrue(String(decoding: sent[1], as: UTF8.self).hasPrefix("1800000001\u{1}_pld\u{1}"))
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        XCTAssertEqual(f.credentials.peek(f.identity)?.revision, credentials?.revision)
        _ = await f.service.close()
    }
    func testNormalCloseRetainsSafePairAndColdStartupWaitsForOriginalVerifiedAccountAndEnabledSetting() async throws {
        let f = try fixture(); try await ready(f.service)
        await f.provider.setUploadOutcome(.notSent)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID()
        _ = try await lease.report(report(.start, instance: instance))
        await f.service.flushPendingPlaybackFeedback()
        f.clock.advance(2)
        _ = try await lease.report(report(.end, instance: instance))
        await f.service.flushPendingPlaybackFeedback()
        await f.service.preparePlaybackFeedbackForClose()
        _ = await f.service.close()
        XCTAssertNotNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        let before = await f.provider.uploads().count
        await f.provider.setUploadOutcome(.confirmed)
        let cold = f.cold()
        _ = try await cold.setAccess(available: true, generation: 1)
        try await cold.configurePlaybackFeedback(settings)
        try await cold.prepareLocalAccounts()
        await cold.flushPendingPlaybackFeedback()
        let withoutValidation = await f.provider.uploads().count
        XCTAssertEqual(withoutValidation, before)
        _ = try await cold.validate(platform: .netease)
        await cold.flushPendingPlaybackFeedback()
        let recovered = Array((await f.provider.uploads()).dropFirst(before))
        XCTAssertEqual(recovered.count, 2)
        XCTAssertTrue(String(decoding: recovered[0], as: UTF8.self).contains("_plv"))
        XCTAssertTrue(String(decoding: recovered[1], as: UTF8.self).contains("_pld"))
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        _ = await cold.close()
    }
    func testActualEndTriggersOneBoundedRecoveryAfterConnectionReturns() async throws {
        let f = try fixture(); try await ready(f.service)
        await f.provider.setUploadOutcome(.notSent)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID()
        _ = try await lease.report(report(.start, instance: instance))
        await f.service.flushPendingPlaybackFeedback()
        let offline = await f.provider.uploads().count
        await f.service.flushPendingPlaybackFeedback()
        let idle = await f.provider.uploads().count
        XCTAssertEqual(offline, 1); XCTAssertEqual(idle, offline)
        await f.provider.setUploadOutcome(.confirmed); f.clock.advance(2)
        let ended = try await lease.report(report(.end, instance: instance))
        XCTAssertEqual(ended, .registered)
        await f.service.flushPendingPlaybackFeedback()
        let recovered = Array((await f.provider.uploads()).dropFirst(offline))
        XCTAssertEqual(recovered.count, 2)
        XCTAssertTrue(String(decoding: recovered[0], as: UTF8.self).contains("_plv"))
        XCTAssertTrue(String(decoding: recovered[1], as: UTF8.self).contains("_pld"))
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        _ = await f.service.close()
    }
    func testColdDisabledSettingClearsPendingBeforeAccountBecomesReady() async throws {
        let f = try fixture(); try await ready(f.service)
        await f.provider.setUploadOutcome(.notSent)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        _ = try await lease.report(report(.start, instance: UUID()))
        await f.service.flushPendingPlaybackFeedback()
        _ = await f.service.close()
        let before = await f.provider.uploads().count, cold = f.cold()
        _ = try await cold.setAccess(available: true, generation: 1)
        try await cold.configurePlaybackFeedback(.init(revision: 2,
            platforms: [.netease: .init(enabled: false, generation: 2)]))
        _ = try await cold.validate(platform: .netease)
        await cold.flushPendingPlaybackFeedback()
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        let after = await f.provider.uploads().count; XCTAssertEqual(before, after)
        _ = await cold.close()
    }
    func testSourceProofRejectsBeforeAdmissionButRetirementAfterRegistrationKeepsOriginalEndProvenance() async throws {
        for beforeAdmission in [false, true] {
            let f = try fixture(); try await ready(f.service)
            await f.provider.setUploadOutcome(.notSent)
            let queue = MusicQueueAuthorization()
            let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
                source: "moreRcmd", sourceID: "", algorithm: "original-per-song-alg", authorization: queue)
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101", source: proof.source,
                sourceID: proof.sourceID, algorithm: proof.algorithm, recommendationProof: proof)
            let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
            let instance = UUID()
            func value(_ event: MusicPlaybackFeedbackReport.Event) -> MusicPlaybackFeedbackReport {
                .init(instanceID: instance, target: target, event: event, duration: 0,
                    listened: event == .start ? 0 : 2, mediaListened: event == .start ? 0 : 4,
                    position: event == .start ? 0 : 4, rate: 2, reason: event == .start ? nil : .ui,
                    startedAt: 1_800_000_000, occurredAt: event == .start ? 1_800_000_000 : 1_800_000_002)
            }
            if beforeAdmission {
                queue.revoke()
                do { _ = try await lease.report(value(.start)); XCTFail("Retired proof cannot admit a start") }
                catch {
                    let failure = error as? MusicServiceError
                    XCTAssertTrue(failure == .staleAccess || failure == .cancelled)
                }
                XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
            } else {
                _ = try await lease.report(value(.start))
                await f.service.flushPendingPlaybackFeedback()
                queue.revoke(); f.clock.advance(2)
                let receipt = try await lease.report(value(.end))
                XCTAssertEqual(receipt, .registered)
                await f.service.flushPendingPlaybackFeedback()
                let before = await f.provider.uploads().count
                await f.provider.setUploadOutcome(.confirmed)
                _ = try await f.service.validate(platform: .netease)
                await f.service.flushPendingPlaybackFeedback()
                let recovered = Array((await f.provider.uploads()).dropFirst(before))
                XCTAssertEqual(recovered.count, 2)
                XCTAssertTrue(recovered.allSatisfy { String(decoding: $0, as: UTF8.self).contains("original-per-song-alg") })
                XCTAssertTrue(String(decoding: recovered[1], as: UTF8.self).contains("_pld"))
            }
            _ = await f.service.close()
        }
    }
    func testTimeoutAndBadACKDoNotReplayEitherPhase() async throws {
        for outcome in [MusicAccountFeedbackProviderFixture.UploadOutcome.unknown, .badACK] {
            for event in [MusicPlaybackFeedbackReport.Event.start, .end] {
                let f = try fixture(); try await ready(f.service)
                let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
                let instance = UUID(), start = report(.start, instance: instance)
                if event == .end {
                    _ = try await lease.report(start)
                    await f.service.flushPendingPlaybackFeedback()
                    f.clock.advance(2)
                }
                await f.provider.setUploadOutcome(outcome)
                let value = event == .start ? start : report(.end, instance: instance)
                let admitted = try await lease.report(value)
                XCTAssertEqual(admitted, .registered)
                await f.service.flushPendingPlaybackFeedback()
                let repeated = try await lease.report(value)
                XCTAssertEqual(repeated, .registered, "Duplicate local receipt cannot replay unknown physical work")
                if event == .start {
                    f.clock.advance(2)
                    let ended = try await lease.report(report(.end, instance: instance))
                    XCTAssertEqual(ended, .unconfirmed)
                }
                _ = try await f.service.validate(platform: .netease)
                await f.service.flushPendingPlaybackFeedback()
                let calls = await f.provider.uploads()
                XCTAssertEqual(calls.count, event == .start ? 1 : 2)
                XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
                _ = await f.service.close()
            }
        }
    }
    func testClearlyUnsentEndRecoversWithoutResendingItsAcknowledgedStart() async throws {
        let f = try fixture(); try await ready(f.service)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID()
        _ = try await lease.report(report(.start, instance: instance))
        await f.service.flushPendingPlaybackFeedback()
        f.clock.advance(2); await f.provider.setUploadOutcome(.notSent)
        let receipt = try await lease.report(report(.end, instance: instance))
        XCTAssertEqual(receipt, .registered)
        await f.service.flushPendingPlaybackFeedback()
        _ = await f.service.close()
        let before = await f.provider.uploads().count
        await f.provider.setUploadOutcome(.confirmed)
        let cold = f.cold(); try await ready(cold)
        let recovered = Array((await f.provider.uploads()).dropFirst(before))
        XCTAssertEqual(recovered.count, 1)
        XCTAssertTrue(String(decoding: recovered[0], as: UTF8.self).hasPrefix("1800000001\u{1}_pld\u{1}"))
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        _ = await cold.close()
    }
    func testDisableLogoutAccountReplacementAndAccessRevocationClearPendingWithoutCrossAccountRecovery() async throws {
        for change in ["disable", "logout", "replacement", "access", "consent"] {
            let f = try fixture(); try await ready(f.service)
            await f.provider.setUploadOutcome(.notSent)
            let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
            _ = try await lease.report(report(.start, instance: UUID()))
            await f.service.flushPendingPlaybackFeedback()
            XCTAssertNotNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
            let before = await f.provider.uploads().count
            switch change {
            case "disable":
                f.service.cancelPendingPlaybackFeedback(platform: .netease)
                try await f.service.configurePlaybackFeedback(.init(revision: 2,
                    platforms: [.netease: .init(enabled: false, generation: 2)]))
            case "logout": try await f.service.logout(platform: .netease)
            case "replacement":
                await f.provider.setQRPlatform(.netease)
                _ = try await f.service.beginLogin(platform: .netease)
            case "access": _ = try await f.service.setAccess(available: false, generation: 2)
            default: f.service.cancelPendingPlaybackFeedback(platform: .netease)
            }
            await f.service.flushPendingPlaybackFeedback()
            XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key), change)
            let count = await f.provider.uploads().count; XCTAssertEqual(count, before, change)
            _ = await f.service.close()
        }
    }
    func testColdReplacementRecordCannotInheritOriginalAccountsPendingEvents() async throws {
        let f = try fixture(); try await ready(f.service)
        await f.provider.setUploadOutcome(.notSent)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        _ = try await lease.report(report(.start, instance: UUID()))
        await f.service.flushPendingPlaybackFeedback()
        _ = await f.service.close()
        f.credentials.seed(try MusicUpdateProvider.credential("replacement", uid: "99"), for: f.identity)
        let before = await f.provider.uploads().count
        await f.provider.setUploadOutcome(.confirmed)
        let replacement = f.cold()
        try await ready(replacement)
        let count = await f.provider.uploads().count; XCTAssertEqual(count, before)
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        _ = await replacement.close()
    }
    func testCloseCancelsHeldPhysicalUploadWithoutWaitingOrRetainingItsAmbiguousPair() async throws {
        let f = try fixture(); try await ready(f.service)
        let gate = MusicAccountFeedbackGate(expectation(description: "Upload owns an inflight record"))
        await f.provider.holdFeedback(gate)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID()
        _ = try await lease.report(report(.start, instance: instance))
        await fulfillment(of: [gate.entered], timeout: 3)
        f.clock.advance(2)
        _ = try await lease.report(report(.end, instance: instance))
        let closed = await f.service.close()
        XCTAssertFalse(closed.cleanupComplete); XCTAssertEqual(closed.inFlight, 1)
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        await gate.release(); await f.service.flushPendingPlaybackFeedback()
        let calls = await f.provider.uploads(); XCTAssertEqual(calls.count, 1)
        let complete = await f.service.close(); XCTAssertTrue(complete.cleanupComplete)
    }
    func testStorageAdmissionFailureNeverReturnsRegisteredOrStartsPhysicalUpload() async throws {
        let f = try fixture(maximumBytes: 16)
        try await ready(f.service)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID(), start = report(.start, instance: instance)
        do { _ = try await lease.report(start); XCTFail("Unpersisted start must not register") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let repeated = try await lease.report(start)
        XCTAssertEqual(repeated, .unconfirmed)
        let end = try await lease.report(report(.end, instance: instance))
        XCTAssertEqual(end, .unconfirmed)
        await f.service.flushPendingPlaybackFeedback()
        let calls = await f.provider.uploads()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        _ = await f.service.close()
    }
    func testDisableCancelsHeldUploadAndLateACKCannotRestoreEventsUnderNewSetting() async throws {
        let f = try fixture(); try await ready(f.service)
        let gate = MusicAccountFeedbackGate(expectation(description: "Upload is physically active"))
        await f.provider.holdFeedback(gate)
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
        let instance = UUID()
        _ = try await lease.report(report(.start, instance: instance))
        await fulfillment(of: [gate.entered], timeout: 3)
        let before = try XCTUnwrap(f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        XCTAssertTrue(String(decoding: before, as: UTF8.self).contains("\"state\":\"inflight\""))
        f.service.cancelPendingPlaybackFeedback(platform: .netease)
        try await f.service.configurePlaybackFeedback(.init(revision: 2,
            platforms: [.netease: .init(enabled: false, generation: 2)]))
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        await gate.release(); await f.service.flushPendingPlaybackFeedback()
        XCTAssertNil(try f.document.read(key: MusicNeteaseFeedbackOutbox.key))
        do { _ = try await lease.report(report(.end, instance: instance)); XCTFail("Disabled old lease") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        try await f.service.configurePlaybackFeedback(.init(revision: 3,
            platforms: [.netease: .init(enabled: true, generation: 3)]))
        await f.service.flushPendingPlaybackFeedback()
        let calls = await f.provider.uploads(); XCTAssertEqual(calls.count, 1)
        _ = await f.service.close()
    }
}

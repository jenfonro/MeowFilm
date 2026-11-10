import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFeedbackListeningTests: XCTestCase {
    func testAccountAdmissionClipsOnlyPreQualificationNotFirstAdmittedInterval() async throws {
        let clock = NativeFeedbackClock(), accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
        let controller = MusicNativeFeedbackController(clock: { clock.now() })
        await accounts.blockAdmission(gate)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await gate.started()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 2, position: 2))
        clock.set(3)
        await gate.release(); await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 5, position: 5))
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(4, at: 6, position: 6,
            kind: .stopped, running: false))
        await controller.flush()
        let rows = await accounts.recorded(), end = try XCTUnwrap(rows.last)
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(end.listened, 3); XCTAssertEqual(end.mediaListened, 3)
        XCTAssertEqual(end.startedAt, 1000); XCTAssertEqual(end.occurredAt, 1006)
        XCTAssertEqual(rows.first?.occurredAt, 1003, "The start event time is qualified admission, not a backfilled first frame")
        XCTAssertEqual(accounts.releases.count(), 1)
    }

    func testDelayedStartReceiptKeepsFrozenEndAndCannotAffectNextPlayback() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        let gate = NativeMusicGate()
        await accounts.configure(result: .confirmed, gate: gate)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await gate.started()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 3, position: 3))
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 7, position: 7,
            kind: .stopped, running: false))
        await accounts.configure(result: .confirmed)
        controller.replaceAttempt(attemptID: "next", occurrenceID: "entry-2", preserving: false)
        let native = UUID()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 10, position: 0,
            kind: .playing, attempt: "next", native: native, occurrence: "entry-2"))
        // A retired native fact is not authority to switch the controller back.
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(4, at: 11, position: 8))
        await gate.release(); await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 12, position: 2,
            kind: .ended, running: false, attempt: "next", native: native, occurrence: "entry-2"))
        await controller.flush()
        let rows = await accounts.recorded(), starts = rows.filter { $0.event == .start }
        XCTAssertEqual(rows.count, 4); XCTAssertEqual(starts.count, 2)
        let old = try XCTUnwrap(rows.first { $0.event == .end && $0.instanceID == starts[0].instanceID })
        let next = try XCTUnwrap(rows.first { $0.event == .end && $0.instanceID == starts[1].instanceID })
        XCTAssertEqual(old.listened, 7); XCTAssertEqual(old.position, 7)
        XCTAssertEqual(old.occurredAt, 1007); XCTAssertEqual(old.reason, .interrupt)
        XCTAssertEqual(next.listened, 2); XCTAssertEqual(next.reason, .playend)
        XCTAssertNotEqual(old.instanceID, next.instanceID)
        XCTAssertEqual(accounts.releases.count(), 2)
    }

    func testPauseBufferSmallSeekAndActualRateKeepWallAndMediaSeparate() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await controller.flush()
        let values = [
            NativeFeedbackFixture.sample(2, at: 2, position: 2),
            NativeFeedbackFixture.sample(3, at: 3, position: 3, running: false),
            NativeFeedbackFixture.sample(4, at: 103, position: 3, running: false),
            NativeFeedbackFixture.sample(5, at: 104, position: 3, kind: .playing),
            NativeFeedbackFixture.sample(6, at: 106, position: 5),
            NativeFeedbackFixture.sample(7, at: 107, position: 5, running: false, broken: true),
            NativeFeedbackFixture.sample(8, at: 108, position: 5.1, running: false, broken: true),
            NativeFeedbackFixture.sample(9, at: 109, position: 5.1, kind: .playing),
            NativeFeedbackFixture.sample(10, at: 109, position: 5.1, broken: true, rate: 2),
            NativeFeedbackFixture.sample(11, at: 111, position: 9.1, rate: 2),
            NativeFeedbackFixture.sample(12, at: 112, position: 11.1, kind: .paused, running: false, rate: 2),
            NativeFeedbackFixture.sample(13, at: 120, position: 11.1, kind: .playing, rate: 2),
            NativeFeedbackFixture.sample(14, at: 121, position: 13.1, kind: .stopped, running: false, rate: 2)
        ]
        values.forEach { NativeFeedbackFixture.feed(controller, accounts, $0) }
        await controller.flush()
        let rows = await accounts.recorded(), end = try XCTUnwrap(rows.last)
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(end.listened, 9, accuracy: 0.000001)
        XCTAssertEqual(end.mediaListened, 13, accuracy: 0.000001)
    }

    func testUnknownDurationAndOneProviderFailureDoNotBlockOtherTargets() async throws {
        for duration in [0.0, 180.0] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
            await accounts.configure(platform: .netease, result: .confirmed)
            await accounts.configure(platform: .qq, result: .unconfirmed, failure: .upstreamFailed)
            await accounts.configure(platform: .kugou, result: .confirmed)
            let targets: [MusicPlaybackFeedbackTarget] = [
                .init(platform: .netease, nativeID: "101"), .init(platform: .qq, nativeID: "SyntheticMID"),
                .init(platform: .kugou, nativeID: String(repeating: "A", count: 32), mixsongID: "22")
            ]
            let settings = MusicPlaybackFeedbackSettings(revision: 1, platforms: [
                .netease: .init(enabled: true, generation: 1), .qq: .init(enabled: true, generation: 1),
                .kugou: .init(enabled: true, generation: 1)
            ])
            func feed(_ sample: MusicPlaybackFeedbackSample) {
                controller.consume(sample, settings: settings, targets: targets, accounts: accounts) { _, _ in }
            }
            feed(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing, rate: 2, duration: duration))
            await controller.flush()
            feed(NativeFeedbackFixture.sample(2, at: 3, position: 6, kind: .stopped, running: false, rate: 2, duration: duration))
            await controller.flush()
            let rows = await accounts.recorded()
            let netease = rows.filter { $0.target.platform == .netease }
            XCTAssertEqual(netease.map(\.event), [.start, .end])
            XCTAssertEqual(netease.last?.listened, 3); XCTAssertEqual(netease.last?.mediaListened, 6)
            XCTAssertEqual(rows.filter { $0.target.platform == .qq }.map(\.event), [.start])
            XCTAssertEqual(rows.filter { $0.target.platform == .kugou }.map(\.event), duration == 0 ? [] : [.start, .end])
            XCTAssertEqual(accounts.releases.count(), duration == 0 ? 2 : 3)
        }
    }

    func testIndependentSettingStartsAtItsNewAdmissionWithoutBackfill() async throws {
        let clock = NativeFeedbackClock(), controller = MusicNativeFeedbackController(clock: { clock.now() })
        let accounts = NativeFeedbackAccounts()
        await accounts.configure(result: .confirmed)
        let targets: [MusicPlaybackFeedbackTarget] = [.init(platform: .netease, nativeID: "101"),
            .init(platform: .qq, nativeID: "SyntheticMID")]
        var settings = MusicPlaybackFeedbackSettings(revision: 1, platforms: [.netease: .init(enabled: true, generation: 1)])
        func feed(_ sample: MusicPlaybackFeedbackSample) {
            controller.consume(sample, settings: settings, targets: targets, accounts: accounts) { _, _ in }
        }
        feed(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await controller.flush()
        clock.set(3)
        settings = .init(revision: 2, platforms: [.netease: .init(enabled: true, generation: 1),
                                                .qq: .init(enabled: true, generation: 1)])
        feed(NativeFeedbackFixture.sample(2, at: 3, position: 3))
        await controller.flush()
        feed(NativeFeedbackFixture.sample(3, at: 5, position: 5, kind: .stopped, running: false))
        await controller.flush()
        let ends = await accounts.recorded().filter { $0.event == .end }
        XCTAssertEqual(ends.first { $0.target.platform == .netease }?.listened, 5)
        XCTAssertEqual(ends.first { $0.target.platform == .qq }?.listened, 2)
        XCTAssertTrue(ends.allSatisfy { $0.startedAt == 1000 })
        let starts = await accounts.recorded().filter { $0.event == .start }
        XCTAssertEqual(starts.first { $0.target.platform == .netease }?.occurredAt, 1000)
        XCTAssertEqual(starts.first { $0.target.platform == .qq }?.occurredAt, 1003)
    }

    func testAccountOrSettingRevocationCancelsFrozenPairAndReleasesOnce() async throws {
        for accountRevocation in [true, false] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
            await accounts.configure(result: .confirmed, gate: gate)
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
            await gate.started()
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 3, position: 3,
                kind: .stopped, running: false))
            if accountRevocation { await accounts.revoke(platform: .qq) }
            else { controller.revoke(platform: .qq) }
            await gate.release(); await controller.flush()
            let rows = await accounts.recorded(), active = await accounts.activeCount()
            XCTAssertEqual(rows.map(\.event), [.start]); XCTAssertEqual(active, 0)
            controller.invalidate()
            XCTAssertEqual(accounts.releases.count(), 1)
        }
    }

    func testHandoffKeepsInstanceAndLastFactEvenBeforeNextObservation() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 3, position: 3))
        XCTAssertTrue(controller.replaceAttempt(attemptID: "renewed", occurrenceID: "entry-1", preserving: true))
        controller.finishCurrent(attemptID: "renewed", reason: .ui)
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.first?.instanceID, rows.last?.instanceID)
        XCTAssertEqual(rows.last?.listened, 3); XCTAssertEqual(rows.last?.position, 3)
        XCTAssertEqual(rows.last?.occurredAt, 1003); XCTAssertEqual(rows.last?.reason, .ui)
    }

    func testInvalidObservationBreaksContinuityButPreservesEndAndLongProgressIsNotSuspension() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing, duration: 0))
        await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 90000, position: 90000, duration: 0))
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 90001, position: .nan, duration: 0))
        controller.finishCurrent(reason: .exception)
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.last?.listened, 90000); XCTAssertEqual(rows.last?.mediaListened, 90000)
        XCTAssertEqual(rows.last?.position, 90000); XCTAssertEqual(rows.last?.occurredAt, 91000)
        XCTAssertEqual(rows.last?.reason, .exception)
    }

    func testSourceCapabilityIsRequiredAtAdmissionButRetirementKeepsCapturedProvenance() async throws {
        for retireBeforeAdmission in [true, false] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
            let source = MusicQueueAuthorization(), gate = NativeMusicGate()
            let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
                source: "", sourceID: "", algorithm: "actual-alg", authorization: source)
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                algorithm: "actual-alg", recommendationProof: proof,
                origin: .init(kind: .similar, platform: .netease, seedIDs: ["500"]))
            let settings = MusicPlaybackFeedbackSettings(revision: 1, platforms: [.netease: .init(enabled: true, generation: 1)])
            if retireBeforeAdmission { await accounts.blockAdmission(gate); await accounts.configure(result: .confirmed) }
            else { await accounts.configure(result: .confirmed, gate: gate) }
            controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
                settings: settings, targets: [target], accounts: accounts) { _, _ in }
            await gate.started()
            source.revoke()
            let ordinary = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101")
            controller.consume(NativeFeedbackFixture.sample(2, at: 3, position: 3),
                settings: settings, targets: [ordinary], accounts: accounts) { _, _ in }
            controller.consume(NativeFeedbackFixture.sample(3, at: 4, position: 4, kind: .stopped, running: false),
                settings: settings, targets: [ordinary], accounts: accounts) { _, _ in }
            await gate.release(); await controller.flush()
            let rows = await accounts.recorded()
            XCTAssertEqual(rows.map(\.event), retireBeforeAdmission ? [] : [.start, .end])
            if !retireBeforeAdmission {
                XCTAssertEqual(rows.last?.target.algorithm, "actual-alg")
                XCTAssertEqual(rows.last?.target.origin?.seedIDs, ["500"])
                XCTAssertEqual(rows.last?.listened, 4)
            }
        }
    }

    func testRestoredPositionAndClaimedProgressCannotReplaceActualStartedFact() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 40))
        await controller.flush()
        let prepared = await accounts.recorded(); XCTAssertTrue(prepared.isEmpty)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 1, position: 40, kind: .playing))
        await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 2, position: 41,
            kind: .stopped, running: false))
        await controller.flush()
        let rows = await accounts.recorded()
        XCTAssertEqual(rows.map(\.event), [.start, .end])
        XCTAssertEqual(rows.last?.listened, 1); XCTAssertEqual(rows.last?.startedAt, 1001)
    }
}

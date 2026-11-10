import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFeedbackControllerTests: XCTestCase {
    func testIntervalsCountLongContinuousAdvancementButNotSeekPauseOrRateChange() {
        let old = NativeFeedbackFixture.sample(1, at: 1, position: 1)
        let next = NativeFeedbackFixture.sample(2, at: 4, position: 4)
        XCTAssertEqual(MusicNativeFeedbackController.interval(old, next).wall, 3)
        let rejected = [
            NativeFeedbackFixture.sample(2, at: 4, position: 100),
            NativeFeedbackFixture.sample(2, at: 4, position: 0),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, kind: .playing),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, broken: true),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, rate: 2)
        ]
        for sample in rejected { XCTAssertEqual(MusicNativeFeedbackController.interval(old, sample).wall, 0) }
        let long = MusicNativeFeedbackController.interval(old, NativeFeedbackFixture.sample(2, at: 100, position: 100))
        XCTAssertEqual(long.wall, 99); XCTAssertEqual(long.media, 99)
        XCTAssertEqual(MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, running: false), next).wall, 0)
        XCTAssertEqual(MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, played: false), next).wall, 0)
        let fast = MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, rate: 2),
            NativeFeedbackFixture.sample(2, at: 4, position: 7, rate: 2))
        XCTAssertEqual(fast.wall, 3); XCTAssertEqual(fast.media, 6)
    }

    func testConfirmedInstanceIncludesFirstAdmittedIntervalAndEndsOnce() async throws {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, .init(attemptID: "synthetic", occurrenceID: "entry-1",
            resourceID: "music:synthetic", nativeAttemptID: NativeFeedbackFixture.nativeID, sequence: 1, observedAt: 0,
            kind: .playing, position: 0, duration: 180, running: true, hasPlayed: true, occurredAt: 1000))
        await controller.flush() // QQ registered; admission preserves the real Started baseline.
        let values = [
            NativeFeedbackFixture.sample(2, at: 1, position: 1),
            NativeFeedbackFixture.sample(3, at: 4, position: 4),
            NativeFeedbackFixture.sample(4, at: 5, position: 5, kind: .paused, running: false),
            NativeFeedbackFixture.sample(5, at: 50, position: 100, running: false),
            NativeFeedbackFixture.sample(6, at: 51, position: 100, kind: .playing),
            NativeFeedbackFixture.sample(7, at: 53, position: 102),
            NativeFeedbackFixture.sample(8, at: 54, position: 160, broken: true),
            NativeFeedbackFixture.sample(9, at: 55, position: 161),
            NativeFeedbackFixture.sample(10, at: 56, position: 162, kind: .stopped, running: false)
        ]
        for value in values { NativeFeedbackFixture.feed(controller, accounts, value) }
        await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, values.last!)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(11, at: 57, position: 163))
        controller.finishCurrent(reason: .ui); await controller.flush()
        let reports = await accounts.recorded()
        XCTAssertEqual(reports.map(\.event), [.start, .end])
        XCTAssertEqual(reports[0].instanceID, reports[1].instanceID)
        XCTAssertEqual(reports[1].listened, 9)
        XCTAssertEqual(reports[1].mediaListened, 9)
        XCTAssertEqual(reports[1].duration, 180); XCTAssertEqual(reports[1].position, 162)
        XCTAssertEqual(reports[1].reason, .interrupt)
        let active = await accounts.activeCount(); XCTAssertEqual(active, 0)
    }

    func testUnknownOrFailedStartNeverCreatesEndOrAutomaticRetryButNewPlaybackMayStart() async {
        for failure in [nil, MusicServiceError.upstreamFailed] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
            await accounts.configure(result: .unconfirmed, failure: failure)
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
            await controller.flush()
            for sequence in 2...5 {
                NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(UInt64(sequence),
                    at: Double(sequence), position: Double(sequence)))
            }
            controller.finishCurrent(reason: .ui); await controller.flush()
            let reports = await accounts.recorded(), count = await accounts.beginCount()
            XCTAssertEqual(reports.map(\.event), [.start]); XCTAssertEqual(count, 1)
            await accounts.configure(result: .confirmed, failure: nil)
            let nextAttempt = UUID().uuidString.lowercased()
            controller.replaceAttempt(attemptID: nextAttempt, occurrenceID: "entry-1", preserving: false)
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 10, position: 0,
                kind: .playing, attempt: nextAttempt))
            await controller.flush()
            let next = await accounts.recorded(), nextCount = await accounts.beginCount()
            XCTAssertEqual(next.map(\.event), [.start, .start])
            XCTAssertNotEqual(next[0].instanceID, next[1].instanceID)
            XCTAssertEqual(nextCount, 2)
        }
    }

    func testPendingStartPairsFrozenTerminalButRevocationRejectsLateAcceptance() async {
        for revoke in [false, true] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
            await accounts.configure(result: .confirmed, gate: gate)
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
            await gate.started()
            if revoke { controller.invalidate() }
            else {
                NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped))
            }
            await gate.release(); await controller.flush()
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 2, position: 2))
            await controller.flush()
            let reports = await accounts.recorded(), active = await accounts.activeCount()
            XCTAssertEqual(reports.map(\.event), revoke ? [.start] : [.start, .end])
            if !revoke {
                XCTAssertEqual(reports.first?.instanceID, reports.last?.instanceID)
                XCTAssertEqual(reports.last?.listened, 1); XCTAssertEqual(reports.last?.position, 1)
                XCTAssertEqual(reports.last?.reason, .interrupt)
            }
            XCTAssertEqual(active, 0)
        }
    }

    func testQualifiedLocalStartStillRegistersFrozenCloseButRevocationRejectsIt() async throws {
        for revoke in [false, true] {
            let gate = NativeMusicGate(), accounts = LocalFeedbackAdmissionAccounts(gate: gate)
            let controller = MusicNativeFeedbackController(clock: { 0 })
            let settings = MusicPlaybackFeedbackSettings(revision: 1, platforms: [.netease: .init(enabled: true, generation: 1)])
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101", source: "search", sourceID: "original")
            func feed(_ sample: MusicPlaybackFeedbackSample) {
                controller.consume(sample, settings: settings, targets: [target], accounts: accounts) { _, _ in
                    await accounts.checkFinalAuthorization()
                }
            }
            feed(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
            // Local qualification is installed, but its final async check has
            // not returned and no start registration has been dispatched yet.
            await gate.started()
            feed(NativeFeedbackFixture.sample(2, at: 3, position: 3))
            controller.finishCurrent(reason: .interrupt)
            controller.invalidate(preservingEnds: true)
            if revoke { controller.revoke(platform: .netease) }
            let drained = expectation(description: "Local start/end registrations finish before close drain returns")
            let draining = Task { await controller.flushRegistrations(); drained.fulfill() }
            await gate.release()
            await fulfillment(of: [drained], timeout: 3)
            await draining.value
            let rows = await accounts.recorded()
            XCTAssertEqual(rows.map(\.event), revoke ? [] : [.start, .end])
            if !revoke {
                let end = try XCTUnwrap(rows.last)
                XCTAssertEqual(rows.first?.instanceID, end.instanceID)
                XCTAssertEqual(rows.first?.occurredAt, 1000)
                XCTAssertEqual(end.occurredAt, 1003); XCTAssertEqual(end.startedAt, 1000)
                XCTAssertEqual(end.listened, 3); XCTAssertEqual(end.mediaListened, 3)
                XCTAssertEqual(end.reason, .interrupt); XCTAssertEqual(end.target.sourceID, "original")
            }
            XCTAssertEqual(accounts.releases.count(), 1)
        }
    }

    func testCommittedReplacementUsesUIButNeverOverwritesObservedTerminalReason() async throws {
        for kind in [MusicPlaybackFeedbackSample.Kind.progress, .ended, .failed] {
            let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
            await controller.flush()
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 3, position: 3,
                kind: kind, running: kind == .progress))
            controller.replaceAttempt(attemptID: "next", occurrenceID: "entry-2", preserving: false)
            await controller.flush()
            let rows = await accounts.recorded(), end = try XCTUnwrap(rows.last)
            XCTAssertEqual(rows.map(\.event), [.start, .end])
            XCTAssertEqual(end.reason, kind == .ended ? .playend : kind == .failed ? .exception : .ui)
            XCTAssertEqual(end.listened, 3); XCTAssertEqual(end.occurredAt, 1003)
        }
    }

    func testManualFinishBindsAttemptAndNeverCreditsUIElapsedOrNewNativeGeneration() async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing))
        await controller.flush()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 1, position: 1))
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 4, position: 4))
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(99, at: 99, position: 99, native: UUID()))
        controller.finishCurrent(attemptID: "foreign", reason: .ui)
        let before = await accounts.recorded(); XCTAssertEqual(before.count, 1)
        controller.finishCurrent(attemptID: "synthetic", reason: .ui)
        controller.invalidate(attemptID: "synthetic", preservingEnds: true)
        await controller.flush()
        let reports = await accounts.recorded()
        XCTAssertEqual(reports.count, 2)
        XCTAssertEqual(reports[1].listened, 4); XCTAssertEqual(reports[1].position, 4)
        XCTAssertEqual(reports[1].reason, .ui)
    }

    func testDisabledSettingAndRevokedGenerationCannotStartUntilExplicitNewGeneration() async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
                                   settings: .init(revision: 0, platforms: [:]))
        await controller.flush()
        var count = await accounts.beginCount(); XCTAssertEqual(count, 0)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(2, at: 1, position: 1))
        await controller.flush()
        controller.revoke(platform: .qq)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(3, at: 2, position: 2))
        await controller.flush()
        count = await accounts.beginCount(); XCTAssertEqual(count, 1)
        NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(4, at: 3, position: 3),
            settings: .init(revision: 2, platforms: [.qq: .init(enabled: true, generation: 2)]))
        await controller.flush()
        count = await accounts.beginCount(); XCTAssertEqual(count, 2)
        controller.invalidate()
    }

    private func recommendationTarget(_ authorization: MusicQueueAuthorization) -> MusicPlaybackFeedbackTarget {
        let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
            source: "moreRcmd", sourceID: "", algorithm: "synthetic-alg", authorization: authorization)
        return .init(platform: .netease, nativeID: "101",
            source: "moreRcmd", algorithm: "synthetic-alg", recommendationProof: proof)
    }
    private let recommendationSettings = MusicPlaybackFeedbackSettings(
        revision: 1, platforms: [.netease: .init(enabled: true, generation: 1)])

    func testRecommendationProofRequiresConsentAndMatchingOccurrence() async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        let target = recommendationTarget(MusicQueueAuthorization())
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
            settings: .init(revision: 0, platforms: [:]), targets: [target], accounts: accounts) { _, _ in }
        await controller.flush()
        var starts = await accounts.beginCount(); XCTAssertEqual(starts, 0)
        controller.replaceAttempt(attemptID: "foreign", occurrenceID: "entry-2", preserving: false)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: "foreign", occurrence: "entry-2"), settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        await controller.flush()
        starts = await accounts.beginCount(); XCTAssertEqual(starts, 0)
        let reports = await accounts.recorded(); XCTAssertTrue(reports.isEmpty)
        controller.invalidate()
    }

    func testRecommendationProofRevokedBeforeAdmissionRejectsStartAndEnd() async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        let authorization = MusicQueueAuthorization(), gate = NativeMusicGate()
        let target = recommendationTarget(authorization)
        await accounts.blockAdmission(gate)
        await accounts.configure(result: .confirmed)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
            settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        await gate.started()
        authorization.revoke()
        controller.consume(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped, running: false),
            settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        await gate.release(); await controller.flush()
        let reports = await accounts.recorded(), active = await accounts.activeCount()
        XCTAssertTrue(reports.isEmpty); XCTAssertEqual(active, 0)
    }

    func testRecommendationReadRetiredAfterAdmissionKeepsCapturedSourceAndFrozenEnd() async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        let authorization = MusicQueueAuthorization(), gate = NativeMusicGate()
        let target = recommendationTarget(authorization)
        await accounts.configure(result: .confirmed, gate: gate)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
            settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        await gate.started()
        authorization.revoke()
        let ordinary = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101")
        controller.consume(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped, running: false),
            settings: recommendationSettings, targets: [ordinary], accounts: accounts) { _, _ in }
        await gate.release(); await controller.flush()
        let reports = await accounts.recorded(), active = await accounts.activeCount()
        XCTAssertEqual(reports.map(\.event), [.start, .end])
        XCTAssertEqual(reports.first?.instanceID, reports.last?.instanceID)
        XCTAssertEqual(reports.last?.listened, 1); XCTAssertEqual(reports.last?.target.algorithm, "synthetic-alg")
        XCTAssertEqual(reports.last?.target.source, "moreRcmd"); XCTAssertEqual(active, 0)
    }

    func testRecommendationAccountRevocationAfterAdmissionRejectsFrozenEnd() async {
        await recommendationRevocationRejectsEnd(account: true)
    }

    func testRecommendationSettingRevocationAfterAdmissionRejectsFrozenEnd() async {
        await recommendationRevocationRejectsEnd(account: false)
    }

    private func recommendationRevocationRejectsEnd(account: Bool) async {
        let controller = MusicNativeFeedbackController(clock: { 0 }), accounts = NativeFeedbackAccounts()
        let target = recommendationTarget(MusicQueueAuthorization()), gate = NativeMusicGate()
        await accounts.configure(result: .confirmed, gate: gate)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
            settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        await gate.started()
        controller.consume(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped, running: false),
            settings: recommendationSettings, targets: [target], accounts: accounts) { _, _ in }
        if account { await accounts.revoke(platform: .netease) }
        else { controller.revoke(platform: .netease) }
        let activeBeforeACK = await accounts.activeCount(); XCTAssertEqual(activeBeforeACK, 0)
        await gate.release(); await controller.flush()
        let reports = await accounts.recorded(), active = await accounts.activeCount()
        XCTAssertEqual(reports.map(\.event), [.start], "Account/setting revocation cannot authorize a frozen end")
        XCTAssertEqual(active, 0)
        controller.invalidate()
        XCTAssertEqual(accounts.releases.count(), 1)
    }
}

private actor LocalFeedbackAdmissionAccounts: MusicNativeFeedbackProviding {
    private let gate: NativeMusicGate
    private var admitted = false, checked = false
    private var rows: [MusicPlaybackFeedbackReport] = []
    nonisolated let releases = NativeFeedbackReleases()
    init(gate: NativeMusicGate) { self.gate = gate }
    func checkFinalAuthorization() async {
        guard admitted, !checked else { return }
        checked = true
        await gate.wait()
    }
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) throws -> MusicPlaybackFeedbackLease {
        try authorization.checkLocal()
        admitted = true
        let own = MusicPlaybackFeedbackAuthorization { try authorization.checkLocal() }
        let releases = self.releases
        return .init(platform: platform, scope: "synthetic-local-account", generation: UUID(),
            authorization: own, registersLocally: true, send: { value in
                try authorization.checkLocal()
                await self.record(value)
                return .registered
            }, release: { releases.release() })
    }
    private func record(_ value: MusicPlaybackFeedbackReport) { rows.append(value) }
    func recorded() -> [MusicPlaybackFeedbackReport] { rows }
}

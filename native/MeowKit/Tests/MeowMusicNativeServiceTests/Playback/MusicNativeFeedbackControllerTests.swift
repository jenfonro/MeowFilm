import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFeedbackControllerTests: XCTestCase {
    func testIntervalsUseObservedAdvancementNotSeekPauseSuspensionOrRateChange() {
        let old = NativeFeedbackFixture.sample(1, at: 1, position: 1)
        let next = NativeFeedbackFixture.sample(2, at: 4, position: 4)
        XCTAssertEqual(MusicNativeFeedbackController.interval(old, next).wall, 3)
        let rejected = [
            NativeFeedbackFixture.sample(2, at: 4, position: 100),
            NativeFeedbackFixture.sample(2, at: 4, position: 0),
            NativeFeedbackFixture.sample(2, at: 100, position: 100),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, kind: .playing),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, broken: true),
            NativeFeedbackFixture.sample(2, at: 4, position: 4, rate: 2)
        ]
        for sample in rejected { XCTAssertEqual(MusicNativeFeedbackController.interval(old, sample).wall, 0) }
        XCTAssertEqual(MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, running: false), next).wall, 0)
        XCTAssertEqual(MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, played: false), next).wall, 0)
        let fast = MusicNativeFeedbackController.interval(
            NativeFeedbackFixture.sample(1, at: 1, position: 1, rate: 2),
            NativeFeedbackFixture.sample(2, at: 4, position: 7, rate: 2))
        XCTAssertEqual(fast.wall, 3); XCTAssertEqual(fast.media, 6)
    }

    func testConfirmedInstanceCountsOnlyNativeIntervalsAndEndsOnce() async throws {
        let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts()
        NativeFeedbackFixture.feed(controller, accounts, .init(attemptID: "synthetic", occurrenceID: "entry-1",
            resourceID: "music:synthetic", nativeAttemptID: NativeFeedbackFixture.nativeID, sequence: 1, observedAt: 0,
            kind: .playing, position: 0, duration: 180, running: true, hasPlayed: true))
        await controller.flush() // QQ registered; first admitted observation starts the baseline.
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
        XCTAssertEqual(reports[1].listened, 8)
        XCTAssertEqual(reports[1].mediaListened, 8)
        XCTAssertEqual(reports[1].duration, 180); XCTAssertEqual(reports[1].position, 162)
        XCTAssertEqual(reports[1].reason, .interrupt)
        let active = await accounts.activeCount(); XCTAssertEqual(active, 0)
    }

    func testUnknownOrFailedStartNeverCreatesEndOrAutomaticRetryButNewPlaybackMayStart() async {
        for failure in [nil, MusicServiceError.upstreamFailed] {
            let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts()
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
            NativeFeedbackFixture.feed(controller, accounts, NativeFeedbackFixture.sample(1, at: 10, position: 0,
                kind: .playing, attempt: UUID().uuidString.lowercased()))
            await controller.flush()
            let next = await accounts.recorded(), nextCount = await accounts.beginCount()
            XCTAssertEqual(next.map(\.event), [.start, .start])
            XCTAssertNotEqual(next[0].instanceID, next[1].instanceID)
            XCTAssertEqual(nextCount, 2)
        }
    }

    func testTerminalOrRevocationWhileStartPendingRejectsLateAcceptance() async {
        for revoke in [false, true] {
            let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts(), gate = NativeMusicGate()
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
            XCTAssertEqual(reports.map(\.event), [.start]); XCTAssertEqual(active, 0)
        }
    }

    func testManualFinishBindsAttemptAndNeverCreditsUIElapsedOrNewNativeGeneration() async {
        let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts()
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
        XCTAssertEqual(reports[1].listened, 3); XCTAssertEqual(reports[1].position, 4)
        XCTAssertEqual(reports[1].reason, .ui)
    }

    func testDisabledSettingAndRevokedGenerationCannotStartUntilExplicitNewGeneration() async {
        let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts()
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

    func testRecommendationProofKeepsConsentAndOccurrenceGatesAndRevokesPendingFeedback() async {
        let controller = MusicNativeFeedbackController(), accounts = NativeFeedbackAccounts()
        let authorization = MusicQueueAuthorization(), gate = NativeMusicGate()
        let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
            source: "moreRcmd", sourceID: "", algorithm: "synthetic-alg", authorization: authorization)
        let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "moreRcmd", algorithm: "synthetic-alg", recommendationProof: proof)
        let enabled = MusicPlaybackFeedbackSettings(revision: 1, platforms: [.netease: .init(enabled: true, generation: 1)])
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing),
            settings: .init(revision: 0, platforms: [:]), targets: [target], accounts: accounts) { _, _ in }
        await controller.flush()
        var starts = await accounts.beginCount(); XCTAssertEqual(starts, 0)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: "foreign", occurrence: "entry-2"), settings: enabled, targets: [target], accounts: accounts) { _, _ in }
        await controller.flush()
        starts = await accounts.beginCount(); XCTAssertEqual(starts, 0)

        await accounts.configure(result: .confirmed, gate: gate)
        controller.consume(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
            attempt: "live"), settings: enabled, targets: [target], accounts: accounts) { _, _ in }
        await gate.started()
        authorization.revoke()
        await gate.release(); await controller.flush()
        controller.consume(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped,
            running: false, attempt: "live"), settings: enabled, targets: [target], accounts: accounts) { _, _ in }
        await controller.flush()
        let reports = await accounts.recorded(), active = await accounts.activeCount()
        XCTAssertEqual(reports.map(\.event), [.start], "a revoked pending start cannot later authorize an end")
        XCTAssertEqual(active, 0)
    }
}

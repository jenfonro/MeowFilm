import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicAccountFeedbackChannelTests: XCTestCase {
    private func makeProvider(_ platform: MusicAccountPlatform = .netease) async throws -> MusicAccountFeedbackProviderFixture {
        let provider = MusicAccountFeedbackProviderFixture()
        try await provider.publish(MusicAccountCredential.make(platform: platform, userID: "42",
            cookie: "synthetic=feedback"), scope: "synthetic-feedback", generation: 1, authorization: .init())
        return provider
    }
    func testStartMustRegisterOrConfirmBeforeEndAndExactDuplicatesNeverSendTwice() async throws {
        for platform in MusicAccountPlatform.allCases {
            let provider = try await makeProvider(platform), clock = MusicAccountTestClock()
            let channel = MusicAccountFeedbackChannel(platform: platform, provider: provider,
                authorization: .init(), clock: { clock.now().timeIntervalSince1970 })
            let instance = UUID()
            let start = MusicAccountFeedbackFixture.report(.start, platform: platform, instance: instance)
            let end = MusicAccountFeedbackFixture.report(.end, platform: platform, instance: instance)
            let orphan = try await channel.report(end)
            XCTAssertEqual(orphan, .unconfirmed)
            let gate = MusicAccountFeedbackGate(expectation(description: "pending start: " + platform.rawValue))
            defer { Task { await gate.release() } }
            await provider.holdFeedback(gate)
            let task = Task { try await channel.report(start) }
            await fulfillment(of: [gate.entered], timeout: 3)
            let pendingStart = try await channel.report(start), pendingEnd = try await channel.report(end)
            XCTAssertEqual(pendingStart, .unconfirmed); XCTAssertEqual(pendingEnd, .unconfirmed)
            let whilePending = await provider.received(); XCTAssertEqual(whilePending, [start])
            await gate.release()
            let first = try await task.value
            XCTAssertEqual(first, platform == .qq ? .registered : .confirmed)
            let repeatedStart = try await channel.report(start)
            XCTAssertEqual(repeatedStart, first)
            // A foreign occurrence/recording cannot borrow the confirmed start.
            let foreignEnd = try await channel.report(MusicAccountFeedbackFixture.report(.end, platform: platform, instance: UUID()))
            XCTAssertEqual(foreignEnd, .unconfirmed)
            if platform != .netease {
                let overElapsed = MusicAccountFeedbackFixture.report(.end, platform: platform, instance: instance, listened: 20)
                do { _ = try await channel.report(overElapsed); XCTFail("QQ/Kugou retain their dispatch-time bound") }
                catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
            }
            clock.advance(2)
            let finished = try await channel.report(end), duplicate = try await channel.report(end)
            XCTAssertEqual(finished, .confirmed); XCTAssertEqual(duplicate, .confirmed)
            do {
                _ = try await channel.report(MusicAccountFeedbackFixture.report(.end, platform: platform, instance: instance, listened: 1.5))
                XCTFail("An already reserved end cannot change its snapshot")
            } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            do {
                _ = try await channel.report(MusicAccountFeedbackFixture.report(.start, platform: platform, instance: UUID()))
                XCTFail("A lease has one listening instance")
            } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let sent = await provider.received(); XCTAssertEqual(sent, [start, end])
        }
    }

    func testAmbiguousStartAndEndAreReservedBeforeSendAndNeverAutomaticallyReplay() async throws {
        for event in [MusicPlaybackFeedbackReport.Event.start, .end] {
            for throwing in [false, true] {
                let provider = try await makeProvider()
                if throwing { await provider.fail(event) } else { await provider.unconfirmed(event) }
                let channel = MusicAccountFeedbackChannel(platform: .netease, provider: provider,
                    authorization: .init(), clock: { 100 })
                let instance = UUID()
                let start = MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: instance)
                let end = MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: instance)
                if event == .end {
                    let confirmed = try await channel.report(start); XCTAssertEqual(confirmed, .confirmed)
                }
                let value = event == .start ? start : end
                if throwing {
                    do { _ = try await channel.report(value); XCTFail("Ambiguous transport failure") }
                    catch { XCTAssertEqual(error as? MusicServiceError, .timeout) }
                } else {
                    let result = try await channel.report(value); XCTAssertEqual(result, .unconfirmed)
                }
                let duplicate = try await channel.report(value), finish = try await channel.report(end)
                XCTAssertEqual(duplicate, .unconfirmed); XCTAssertEqual(finish, .unconfirmed)
                let sent = await provider.received()
                XCTAssertEqual(sent, event == .start ? [start] : [start, end])
                let accounts = await provider.counts(); XCTAssertEqual(accounts, .init())
            }
        }
    }

    func testNeteaseUsesOriginalListeningTotalsWithoutDispatchClockOrDurationAndRateCaps() async throws {
        let provider = try await makeProvider()
        let channel = MusicAccountFeedbackChannel(platform: .netease, provider: provider,
            authorization: .init(), clock: { 100 })
        let instance = UUID(), target = MusicAccountFeedbackFixture.target(.netease)
        let start = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: .start,
            duration: 0, listened: 0, mediaListened: 0, position: 0, rate: 32,
            startedAt: 1_800_000_000, occurredAt: 1_800_000_000)
        let end = MusicPlaybackFeedbackReport(instanceID: instance, target: target, event: .end,
            duration: 0, listened: 90_000, mediaListened: 2_880_000, position: 2_880_000, rate: 32, reason: .ui,
            startedAt: start.startedAt, occurredAt: 1_800_090_000)
        let began = try await channel.report(start), ended = try await channel.report(end)
        XCTAssertEqual(began, .confirmed); XCTAssertEqual(ended, .confirmed)
        let sent = await provider.received(); XCTAssertEqual(sent, [start, end])
    }

    func testNonfiniteNegativeAndZeroRateFactsCannotEnterFeedback() throws {
        for platform in MusicAccountPlatform.allCases {
            func value(listened: Double = 1, rate: Double = 1, duration: Double = 180) -> MusicPlaybackFeedbackReport {
                .init(instanceID: UUID(), target: MusicAccountFeedbackFixture.target(platform), event: .end,
                    duration: duration, listened: listened, mediaListened: listened, position: 1,
                    rate: rate, reason: .ui, startedAt: 1_800_000_000, occurredAt: 1_800_000_001)
            }
            for invalid in [Double.nan, .infinity, -1] {
                XCTAssertThrowsError(try MusicAccountFeedbackFacts.consume(value(listened: invalid)))
                XCTAssertThrowsError(try MusicAccountFeedbackFacts.consume(value(rate: invalid)))
            }
            XCTAssertThrowsError(try MusicAccountFeedbackFacts.consume(value(rate: 0)))
        }
    }

    func testSourceRetirementKeepsFrozenEndAndRevocationDoesNotEraseDispatchedReceipt() async throws {
        for revokeConsent in [false, true] {
            let provider = try await makeProvider()
            let queue = MusicQueueAuthorization(), authorization = MusicPlaybackFeedbackAuthorization()
            let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
                source: "moreRcmd", sourceID: "", algorithm: "synthetic-alg", authorization: queue)
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                source: "moreRcmd", algorithm: "synthetic-alg", recommendationProof: proof)
            let channel = MusicAccountFeedbackChannel(platform: .netease, provider: provider,
                authorization: authorization)
            let gate = MusicAccountFeedbackGate(expectation(description: "recommendation feedback in flight"))
            await provider.holdFeedback(gate)
            let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .start,
                duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1,
                startedAt: 1_800_000_000, occurredAt: 1_800_000_000)
            let task = Task { try await channel.report(report) }
            await fulfillment(of: [gate.entered], timeout: 3)
            XCTAssertEqual(authorization.inFlight, 1)
            queue.revoke()
            XCTAssertEqual(authorization.inFlight, 1, "cancel is not physical completion")
            if revokeConsent { authorization.revoke() }
            await gate.release()
            let receipt = try await task.value
            XCTAssertEqual(receipt, .confirmed, "Revocation is not rollback of a dispatched write")
            if revokeConsent {
                do { _ = try await channel.report(report); XCTFail("Revoked consent cannot dispatch again") }
                catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            } else {
                let end = MusicPlaybackFeedbackReport(instanceID: report.instanceID, target: target, event: .end,
                    duration: 0, listened: 4, mediaListened: 8, position: 8, rate: 2, reason: .ui,
                    startedAt: report.startedAt, occurredAt: report.occurredAt + 4)
                let receipt = try await channel.report(end)
                XCTAssertEqual(receipt, .confirmed)
            }
            XCTAssertEqual(authorization.inFlight, 0)
            let sent = await provider.received()
            XCTAssertEqual(sent.count, revokeConsent ? 1 : 2)
            XCTAssertEqual(sent.first, report)
        }
    }
}

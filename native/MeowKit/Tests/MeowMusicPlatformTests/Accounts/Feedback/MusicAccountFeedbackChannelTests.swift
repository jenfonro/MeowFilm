import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicAccountFeedbackChannelTests: XCTestCase {
    func testStartMustRegisterOrConfirmBeforeEndAndExactDuplicatesNeverSendTwice() async throws {
        for platform in MusicAccountPlatform.allCases {
            let provider = MusicAccountFeedbackProviderFixture(), clock = MusicAccountTestClock()
            let channel = MusicAccountFeedbackChannel(platform: platform, provider: provider,
                credential: try MusicUpdateProvider.credential(), authorization: .init(),
                clock: { clock.now().timeIntervalSince1970 })
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
            let overElapsed = MusicAccountFeedbackFixture.report(.end, platform: platform, instance: instance, listened: 20)
            do { _ = try await channel.report(overElapsed); XCTFail("No fabricated elapsed listening") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
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
                let provider = MusicAccountFeedbackProviderFixture()
                if throwing { await provider.fail(event) } else { await provider.unconfirmed(event) }
                let channel = MusicAccountFeedbackChannel(platform: .netease, provider: provider,
                    credential: try MusicUpdateProvider.credential(), authorization: .init(), clock: { 100 })
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

    func testRecommendationRevocationKeepsPhysicalFeedbackRegisteredUntilWorkerFinishes() async throws {
        let provider = MusicAccountFeedbackProviderFixture()
        let queue = MusicQueueAuthorization(), authorization = MusicPlaybackFeedbackAuthorization()
        let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
            source: "moreRcmd", sourceID: "", algorithm: "synthetic-alg", authorization: queue)
        let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "moreRcmd", algorithm: "synthetic-alg", recommendationProof: proof)
        let channel = MusicAccountFeedbackChannel(platform: .netease, provider: provider,
            credential: try MusicUpdateProvider.credential(), authorization: authorization)
        let gate = MusicAccountFeedbackGate(expectation(description: "recommendation feedback in flight"))
        await provider.holdFeedback(gate)
        let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .start,
            duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1)
        let task = Task { try await channel.report(report) }
        await fulfillment(of: [gate.entered], timeout: 3)
        XCTAssertEqual(authorization.inFlight, 1)
        queue.revoke()
        XCTAssertEqual(authorization.inFlight, 1, "cancel is not physical completion")
        await gate.release()
        do { _ = try await task.value; XCTFail("revoked recommendation cannot confirm late feedback") }
        catch {
            let failure = error as? MusicServiceError
            XCTAssertTrue(failure == .staleAccess || failure == .cancelled)
        }
        XCTAssertEqual(authorization.inFlight, 0)
        let sent = await provider.received()
        XCTAssertEqual(sent, [report])
    }
}

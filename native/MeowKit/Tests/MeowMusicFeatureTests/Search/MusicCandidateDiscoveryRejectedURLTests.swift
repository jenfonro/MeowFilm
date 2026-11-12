import Foundation
import XCTest
import MeowMusicFeature

private actor RejectedURLDiscoverySession: MusicCandidateDiscoverySession {
    let known: MusicCandidateDiscoverySnapshot
    let discovered: MusicCandidateDiscoverySnapshot
    let urls: [String: URL]
    let failures: [String: MusicServiceError]
    private var targets: [MusicCandidateDiscoveryTarget] = []
    private var snapshots = 0
    private var cancellations: [String] = []
    private var retained: String?
    init(options: [MusicCandidateDiscoveryOption], discovered: [MusicCandidateDiscoveryOption]? = nil,
         urls: [String: URL], failures: [String: MusicServiceError] = [:]) {
        known = MusicCandidateDiscoveryFixture.snapshot(options)
        self.discovered = MusicCandidateDiscoveryFixture.snapshot(discovered ?? options)
        self.urls = urls; self.failures = failures
    }
    func snapshot(discover: Bool, force: Bool) -> MusicCandidateDiscoverySnapshot {
        snapshots += 1
        return discover ? discovered : known
    }
    func resolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String) throws -> MusicPlaybackCandidate {
        targets.append(target)
        let installation = target.selection.installationId
        if let failure = failures[installation] { throw failure }
        guard let url = urls[installation] else { throw MusicServiceError.invalidResponse }
        return .init(attemptId: attemptID, occurrenceId: "entry-1", resourceId: "music:" + attemptID,
            url: url, title: "Synthetic", requestedQuality: target.quality, resolvedSelection: target.selection)
    }
    func cancel(attemptID: String) { cancellations.append(attemptID) }
    func close(retainingAttemptID: String?) { retained = retainingAttemptID }
    func facts() -> (targets: [MusicCandidateDiscoveryTarget], snapshots: Int,
                     cancellations: [String], retained: String?) {
        (targets, snapshots, cancellations, retained)
    }
}

private struct RejectedURLDiscoveryProvider: MusicCandidateDiscoveryServing {
    let session: RejectedURLDiscoverySession
    func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) throws -> any MusicCandidateDiscoverySession {
        guard occurrenceID == "entry-1", expectedQueueRevision == 1 else { throw MusicServiceError.staleRevision }
        return session
    }
}

final class MusicCandidateDiscoveryRejectedURLTests: XCTestCase {
    private let rejected = URL(string: "https://media.invalid/synthetic.flac?revision=old")!
    private let replacement = URL(string: "https://media.invalid/synthetic.flac?revision=new")!

    func testAutomaticTreatsRejectedSuccessAsResolutionFailureAndKeepsExistingPolicy() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let c = MusicCandidateDiscoveryFixture.option("c", qualities: ["flac"])
        let session = RejectedURLDiscoverySession(options: [a, b, c],
            urls: ["a": rejected, "b": rejected, "c": replacement])
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        let value = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
            mode: .automatic(), rejectedURLs: [rejected])
        XCTAssertEqual(value.candidate.url, replacement)
        XCTAssertEqual(value.target, c.target(quality: "flac"))
        XCTAssertEqual(value.attempts, 3)
        XCTAssertEqual(value.issues.map(\.stage), [.resolution, .resolution])
        XCTAssertEqual(value.issues.map(\.failure), [.invalidMediaCandidate, .invalidMediaCandidate])
        XCTAssertFalse(value.playbackVerified); XCTAssertFalse(value.qualityVerified)
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b", "c"])
        XCTAssertEqual(facts.cancellations.count, 2)
        XCTAssertEqual(facts.retained, value.candidate.attemptId)
    }

    func testManualRejectedSuccessCancelsOnlyThatTargetAndNeverSearches() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac", "128k"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let session = RejectedURLDiscoverySession(options: [a, b], urls: ["a": rejected, "b": replacement])
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        do {
            _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                mode: .manual(a.target(quality: "flac")), rejectedURLs: [rejected])
            XCTFail("a known failed URL must not be published")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets, [a.target(quality: "flac")])
        XCTAssertEqual(facts.snapshots, 0)
        XCTAssertEqual(facts.cancellations.count, 1); XCTAssertNil(facts.retained)
    }

    func testDiscoveredReplacementIsAdoptedOnceWithoutRetryingRejectedKnownTarget() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let session = RejectedURLDiscoverySession(options: [a], discovered: [a, b],
            urls: ["a": rejected, "b": replacement])
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        let value = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
            mode: .automatic(forceDiscovery: true), rejectedURLs: [rejected])
        XCTAssertEqual(value.attempts, 2); XCTAssertEqual(value.candidate.url, replacement)
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"])
        XCTAssertEqual(facts.cancellations.count, 1)
    }

    func testRepeatedRejectedURLsExhaustAllCandidatesOnceAndReleaseEveryAttempt() async throws {
        let options = (0..<32).map { MusicCandidateDiscoveryFixture.option("source-\($0)", qualities: ["flac"]) }
        let urls = Dictionary(uniqueKeysWithValues: options.map { ($0.playback.installationId, rejected) })
        let session = RejectedURLDiscoverySession(options: options, urls: urls)
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        do {
            _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                mode: .automatic(), rejectedURLs: [rejected])
            XCTFail("all candidates returned a rejected URL")
        } catch {
            let failure = try XCTUnwrap(error as? MusicCandidateDiscoveryFailure)
            XCTAssertEqual(failure.attempts, options.count)
            XCTAssertEqual(failure.issues.count, options.count)
            XCTAssertTrue(failure.issues.allSatisfy { $0.stage == .resolution && $0.failure == .invalidMediaCandidate })
        }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.count, options.count)
        XCTAssertEqual(facts.cancellations.count, options.count)
        XCTAssertEqual(Set(facts.targets.map(\.selection.installationId)),
                       Set(options.map(\.playback.installationId)))
        XCTAssertEqual(Set(facts.cancellations).count, options.count)
        XCTAssertNil(facts.retained)
    }

    func testRejectedURLDoesNotTurnFollowingAuthorityFailureIntoFallbackPermission() async throws {
        let options = ["a", "b", "c"].map { MusicCandidateDiscoveryFixture.option($0, qualities: ["flac"]) }
        let session = RejectedURLDiscoverySession(options: options,
            urls: ["a": rejected, "c": replacement], failures: ["b": .authRequired])
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        do {
            _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                mode: .automatic(), rejectedURLs: [rejected])
            XCTFail("authority failure must stop the existing policy")
        } catch {
            XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.failure, .authRequired)
            XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.attempts, 2)
        }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"])
        XCTAssertNil(facts.retained)
    }

    func testOmittedRejectionsPreserveExistingCallersAndNewURLQueryIsNotRejected() async throws {
        let option = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let session = RejectedURLDiscoverySession(options: [option], urls: ["a": rejected])
        let consumer = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: session))
        let ordinary = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
            mode: .manual(option.target(quality: "flac")))
        XCTAssertEqual(ordinary.candidate.url, rejected)

        let renewed = RejectedURLDiscoverySession(options: [option], urls: ["a": replacement])
        let next = MusicCandidateDiscoveryConsumer(provider: RejectedURLDiscoveryProvider(session: renewed))
        let value = try await next.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
            mode: .manual(option.target(quality: "flac")), rejectedURLs: [rejected])
        XCTAssertEqual(value.candidate.url, replacement)
        XCTAssertEqual(value.attempts, 1)
    }
}

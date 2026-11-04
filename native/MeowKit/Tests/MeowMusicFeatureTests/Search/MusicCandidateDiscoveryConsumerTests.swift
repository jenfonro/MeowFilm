import XCTest
import MeowMusicFeature

final class MusicCandidateDiscoveryConsumerTests: XCTestCase {
    func testManualFailureNeverDiscoversOrChangesTarget() async throws {
        let target = MusicCandidateDiscoveryFixture.option().target(quality: "flac")
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([target.option]), failures: [.upstreamFailed])
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .manual(target)); XCTFail("must fail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets, [target]); XCTAssertEqual(facts.snapshots, 0); XCTAssertEqual(facts.commits, 0)
        XCTAssertNil(facts.retained)
    }
    func testExplicitAutomaticModeRetriesAnotherExactProviderOnlyForAllowedFailure() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: [.resolutionFailed])
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.installationId, "b")
        XCTAssertEqual(result.attempts, 2); XCTAssertFalse(result.playbackVerified); XCTAssertFalse(result.qualityVerified)
        XCTAssertEqual(result.issues.last?.failure, .resolutionFailed)
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"])
        XCTAssertEqual(facts.retained, result.candidate.attemptId)
    }
    func testAuthorizationAndRevisionFailuresAreNotFallbackPermission() async throws {
        for failure in [MusicServiceError.authRequired, .verificationRequired, .rateLimited, .targetChanged, .staleRevision, .storageConflict] {
            let a = MusicCandidateDiscoveryFixture.option("a"), b = MusicCandidateDiscoveryFixture.option("b")
            let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: [failure])
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()); XCTFail("must stop") }
            catch { XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.failure, failure) }
            let facts = await session.facts()
            XCTAssertEqual(facts.targets.count, 1); XCTAssertEqual(facts.commits, 0)
        }
    }
    func testKnownCandidateDoesNotWaitForDiscoveryAndDoesNotCommitItsLateMetadata() async throws {
        let gate = MusicCandidateDiscoveryTestGate()
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([MusicCandidateDiscoveryFixture.option()]), discoveryGate: gate)
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.attempts, 1)
        let facts = await session.facts()
        XCTAssertEqual(facts.commits, 0); XCTAssertEqual(facts.retained, result.candidate.attemptId)
    }
    func testDiscoveredSourceIsConsumedOnlyAfterExplicitCASAdoption() async throws {
        let found = MusicCandidateDiscoveryFixture.option("b", platform: "wy", source: "new-source", qualities: ["128k"])
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([]),
            discovered: MusicCandidateDiscoveryFixture.snapshot([found]))
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.siteId, "wy")
        let facts = await session.facts()
        XCTAssertEqual(facts.commits, 1); XCTAssertEqual(facts.targets.map(\.selection.sourceKey), ["new-source"])
    }
    func testRepeatedSnapshotsDoNotRetrySameRequestOrTruncateValidCandidates() async throws {
        let options = (0..<32).map { MusicCandidateDiscoveryFixture.option("script-\($0)", qualities: ["flac"]) }
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot(options),
            failures: Array(repeating: .upstreamFailed, count: 32))
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()); XCTFail("no candidate") }
        catch { XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.attempts, 32) }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.count, 32)
        XCTAssertEqual(Set(facts.targets.map(\.selection.installationId)).count, 32)
    }
    func testCancellationClosesScopeAndNeverPublishesLateResolution() async throws {
        let gate = MusicCandidateDiscoveryTestGate(), option = MusicCandidateDiscoveryFixture.option()
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([option]), resolutionGate: gate)
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let task = Task { try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()) }
        await gate.started(); await consumer.cancel()
        do { _ = try await task.value; XCTFail("cancelled") } catch {}
        let facts = await session.facts()
        XCTAssertNil(facts.retained); XCTAssertEqual(facts.targets.count, 1)
    }
    func testDeadlineReturnsWithoutWaitingForUncooperativeWork() async throws {
        let timer = MusicCandidateDiscoveryTestGate(), work = MusicCandidateDiscoveryTestGate()
        let task = Task {
            try await MusicCandidateDiscoveryDeadline.run(seconds: 12, timing: .init(sleep: { _ in await timer.wait() })) {
                await work.wait(); return 1
            }
        }
        await work.started(); await timer.started(); await timer.release()
        do { _ = try await task.value; XCTFail("timeout") }
        catch { XCTAssertEqual(error as? MusicServiceError, .timeout) }
        await work.release()
    }
}

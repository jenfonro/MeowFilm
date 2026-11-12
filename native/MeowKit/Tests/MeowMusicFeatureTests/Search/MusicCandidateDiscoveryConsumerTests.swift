import XCTest
import MeowMusicFeature

final class MusicCandidateDiscoveryConsumerTests: XCTestCase {
    func testColdResolutionKeepsTheOriginalRequestTargetAndReturnsTheActualSelection() async throws {
        let option = MusicCandidateDiscoveryFixture.option(generation: 0)
        let target = option.target(quality: "flac")
        let actual = MusicPlaybackSelection(installationId: "script-a", generation: 7, siteId: "tx",
            sourceKey: "source-a", quality: "flac",
            scriptIdentity: .init(version: "1", contentHash: String(repeating: "a", count: 64), revisionHash: "r1"))
        for automatic in [false, true] {
            let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([option]),
                resultSelection: { _ in actual })
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                mode: automatic ? .automatic() : .manual(target))
            XCTAssertEqual(result.candidate.resolvedSelection, actual)
            XCTAssertEqual(result.target, target)
            XCTAssertEqual(result.target.selection.generation, 0)
            XCTAssertTrue(result.target.sameRequest(as: target))
            let facts = await session.facts()
            XCTAssertEqual(facts.targets, [target])
            XCTAssertEqual(facts.retained, result.candidate.attemptId)
            XCTAssertTrue(facts.cancellations.isEmpty)
        }
    }

    func testMismatchedResolvedSelectionIsTerminalAndCancelsOnlyTheExactAttempt() async throws {
        let hash = String(repeating: "a", count: 64)
        let identity = MusicScriptIdentity(version: "1", contentHash: hash, revisionHash: "r1")
        let cold = MusicCandidateDiscoveryFixture.option(generation: 0)
        let ready = MusicCandidateDiscoveryFixture.option(generation: 7)
        let official = MusicCandidateDiscoveryFixture.option("official:fixture")
        let officialCold = MusicCandidateDiscoveryFixture.option("official:fixture", generation: 0)
        let cases: [(String, MusicCandidateDiscoveryOption, MusicPlaybackSelection)] = [
            ("installation", cold, .init(installationId: "other", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "flac", scriptIdentity: identity)),
            ("site", cold, .init(installationId: "script-a", generation: 7, siteId: "wy",
                sourceKey: "source-a", quality: "flac", scriptIdentity: identity)),
            ("source", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-b", quality: "flac", scriptIdentity: identity)),
            ("quality", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "128k", scriptIdentity: identity)),
            ("version", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "flac",
                scriptIdentity: .init(version: "2", contentHash: hash, revisionHash: "r1"))),
            ("content", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "flac",
                scriptIdentity: .init(version: "1", contentHash: String(repeating: "b", count: 64), revisionHash: "r1"))),
            ("revision", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "flac",
                scriptIdentity: .init(version: "1", contentHash: hash, revisionHash: "r2"))),
            ("missing identity", cold, .init(installationId: "script-a", generation: 7, siteId: "tx",
                sourceKey: "source-a", quality: "flac")),
            ("still cold", cold, cold.target(quality: "flac").selection),
            ("positive generation changed", ready, .init(installationId: "script-a", generation: 8, siteId: "tx",
                sourceKey: "source-a", quality: "flac", scriptIdentity: identity)),
            ("official generation changed", official, .init(installationId: "official:fixture", generation: 2, siteId: "tx",
                sourceKey: "source-a", quality: "flac")),
            ("official cannot cold-bind", officialCold, official.target(quality: "flac").selection),
        ]
        for (name, option, actual) in cases {
            for automatic in [false, true] {
                let target = option.target(quality: "flac")
                let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([option]),
                    resultSelection: { _ in actual })
                let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
                do {
                    _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                        mode: automatic ? .automatic() : .manual(target))
                    XCTFail("Invalid resolved selection: \(name)")
                } catch {
                    let failure = (error as? MusicCandidateDiscoveryFailure)?.failure ?? error as? MusicServiceError
                    XCTAssertEqual(failure, .invalidResponse, name)
                }
                let facts = await session.facts()
                XCTAssertEqual(facts.targets, [target], name)
                XCTAssertEqual(facts.attempts.count, 1, name)
                XCTAssertEqual(facts.cancellations, facts.attempts, name)
                XCTAssertNil(facts.retained, name)
                XCTAssertEqual(facts.discoveries, 0, name)
            }
        }
    }

    func testTargetRevisionMismatchIsNotPermissionToIgnoreThePin() async throws {
        let playback = MusicCandidateDiscoveryFixture.option().playback
        let option = MusicCandidateDiscoveryOption(playback: playback, revisionHash: "different")
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([option]))
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        do {
            _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                mode: .manual(option.target(quality: "flac")))
            XCTFail("Inconsistent request pin")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        let facts = await session.facts()
        XCTAssertEqual(facts.cancellations, facts.attempts); XCTAssertNil(facts.retained)
    }

    func testManualFailureNeverDiscoversOrChangesTarget() async throws {
        let target = MusicCandidateDiscoveryFixture.option().target(quality: "flac")
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([target.option]), failures: [.upstreamFailed])
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .manual(target)); XCTFail("must fail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let facts = await session.facts()
        XCTAssertEqual(facts.targets, [target]); XCTAssertEqual(facts.snapshots, 0)
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
        for failure in [MusicServiceError.authRequired, .verificationRequired, .targetChanged, .staleRevision, .storageConflict] {
            let a = MusicCandidateDiscoveryFixture.option("a"), b = MusicCandidateDiscoveryFixture.option("b")
            let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: [failure])
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()); XCTFail("must stop") }
            catch { XCTAssertEqual((error as? MusicCandidateDiscoveryFailure)?.failure, failure) }
            let facts = await session.facts()
            XCTAssertEqual(facts.targets.count, 1)
            XCTAssertEqual(facts.discoveries, 0)
        }
    }
    func testProviderFailuresTryAnotherTargetWithoutTreatingThemAsContentFacts() async throws {
        for failure in [MusicServiceError.connectionFailed, .networkOffline, .scriptFailed, .scriptRuntimeUnavailable,
                        .rateLimited, .busy, .requestRejected] {
            let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
            let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
            let session = MusicCandidateDiscoveryTestSession(
                known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: [failure])
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
            XCTAssertEqual(result.target.selection.installationId, "b")
            XCTAssertEqual(result.issues.map(\.failure), [failure])
            let facts = await session.facts()
            XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"])
            XCTAssertEqual(facts.discoveries, 0)
            XCTAssertFalse(MusicCandidateDiscoveryFailures.isContentFailure(failure))
        }
    }
    func testKnownCandidateDoesNotWaitForDiscoveryAndDoesNotCommitItsLateMetadata() async throws {
        let gate = MusicCandidateDiscoveryTestGate()
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([MusicCandidateDiscoveryFixture.option()]), discoveryGate: gate)
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.attempts, 1)
        let facts = await session.facts()
        XCTAssertEqual(facts.retained, result.candidate.attemptId)
        XCTAssertEqual(facts.discoveries, 0, "A playable known source never starts platform searches")
    }
    func testContentFailureTriesNextKnownTargetBeforeAnyDiscovery() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        for failure in [MusicServiceError.notFound, .contentUnavailable, .audioUnavailable, .permissionDenied, .previewOnly] {
            let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: [failure])
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
            XCTAssertEqual(result.target.selection.installationId, "b")
            XCTAssertEqual(result.issues.map(\.failure), [failure])
            let facts = await session.facts()
            XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"])
            XCTAssertEqual(facts.discoveries, 0)
        }
    }
    func testDiscoveryStartsOnlyAfterEveryKnownTargetFailed() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let found = MusicCandidateDiscoveryFixture.option("b", platform: "wy", source: "new-source", qualities: ["flac"])
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a]),
            discovered: MusicCandidateDiscoveryFixture.snapshot([a, found]), failures: [.previewOnly])
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.sourceKey, "new-source"); XCTAssertEqual(result.attempts, 2)
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.installationId), ["a", "b"], "the known target is not retried")
        XCTAssertEqual(facts.discoveries, 1)
    }
    func testExhaustedContentFailuresKeepThePlatformExplanation() async throws {
        let a = MusicCandidateDiscoveryFixture.option("a", qualities: ["flac"])
        let b = MusicCandidateDiscoveryFixture.option("b", qualities: ["flac"])
        let cases: [([MusicServiceError], MusicServiceError)] = [([.permissionDenied, .notFound], .permissionDenied),
                                                                  ([.previewOnly, .upstreamFailed], .sourceUnavailable)]
        for (failures, expected) in cases {
            let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([a, b]), failures: failures)
            let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
            do { _ = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic()); XCTFail("exhausted") }
            catch {
                let failure = try XCTUnwrap(error as? MusicCandidateDiscoveryFailure)
                XCTAssertEqual(failure.failure, expected); XCTAssertEqual(failure.attempts, 2)
            }
            let facts = await session.facts()
            XCTAssertEqual(facts.discoveries, 1); XCTAssertNil(facts.retained)
        }
    }
    func testDiscoveredSourceIsConsumedFromItsSessionWithoutQueueAdoption() async throws {
        let found = MusicCandidateDiscoveryFixture.option("b", platform: "wy", source: "new-source", qualities: ["128k"])
        let session = MusicCandidateDiscoveryTestSession(known: MusicCandidateDiscoveryFixture.snapshot([]),
            discovered: MusicCandidateDiscoveryFixture.snapshot([found]))
        let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCandidateDiscoveryTestProvider(session: session))
        let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
        XCTAssertEqual(result.target.selection.siteId, "wy")
        let facts = await session.facts()
        XCTAssertEqual(facts.targets.map(\.selection.sourceKey), ["new-source"])
        XCTAssertEqual(facts.discoveries, 1)
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

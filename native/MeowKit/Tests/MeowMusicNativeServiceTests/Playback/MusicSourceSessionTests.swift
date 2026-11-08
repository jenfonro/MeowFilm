import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor SourceSessionScripts: MusicScriptResolving {
    var requests: [MusicScriptRequest] = []
    var failQualities = Set<String>()
    var failProviders = Set<String>()
    var snapshots: [MusicScriptSource]
    init(_ declarations: [(String, [String])] = [("a", ["tx"])]) {
        snapshots = declarations.map { id, sites in
            .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
                contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
                capabilities: sites.map { .init(siteID: $0, title: id, actions: ["musicUrl"], qualities: ["128k", "flac"]) })
        }
    }
    func configure(failQualities: Set<String> = [], failProviders: Set<String> = []) {
        self.failQualities = failQualities; self.failProviders = failProviders
    }
    func sources() -> [MusicScriptSource] { snapshots }
    func source(installationID: String) throws -> MusicScriptSource {
        guard let value = snapshots.first(where: { $0.installationID == installationID }) else { throw MusicServiceError.sourceUnavailable }
        return value
    }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if failQualities.contains(request.quality) || failProviders.contains(request.installationID) { throw MusicServiceError.audioUnavailable }
        return .init(value: .init(url: URL(string: "https://media.invalid/" + request.installationID + "." + request.quality)!,
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
            source: try source(installationID: request.installationID))
    }
    func lyrics(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: try source(installationID: request.installationID))
    }
    func calls() -> [MusicScriptRequest] { requests }
}

private final class SourceSessionCancellationDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    let base: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var remaining = 0, arms = 0, reads = 0, cancellations = 0
    init(_ base: NativeMusicMemoryDocument) { self.base = base }
    func arm(afterReads: Int) {
        lock.lock(); defer { lock.unlock() }
        remaining = afterReads; arms += 1
    }
    func read() throws -> Data? {
        lock.lock()
        var cancel = false
        if remaining > 0 {
            reads += 1; remaining -= 1
            cancel = remaining == 0
        }
        lock.unlock()
        if cancel {
            let cancelled = withUnsafeCurrentTask { task in
                guard let task else { return false }
                task.cancel()
                return true
            }
            if cancelled {
                lock.lock(); cancellations += 1; lock.unlock()
            }
        }
        return try base.read()
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try base.compareAndSwap(expected: expected, replacement: replacement)
    }
    func injectionCounts() -> (arms: Int, reads: Int, cancellations: Int) {
        lock.lock(); defer { lock.unlock() }
        return (arms, reads, cancellations)
    }
}

private actor SourceSessionPublicationScripts: MusicScriptResolving {
    private var cancellationDocument: SourceSessionCancellationDocument?
    private var requests: [MusicScriptRequest] = []
    init(cancellingPublicationWith document: SourceSessionCancellationDocument? = nil) {
        cancellationDocument = document
    }
    private func snapshot() -> MusicScriptSource {
        .init(installationID: "a", generation: 1, enabled: true, state: .ready, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: "a", actions: ["musicUrl"], qualities: ["flac"])])
    }
    func sources() -> [MusicScriptSource] { [snapshot()] }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installationID == "a" else { throw MusicServiceError.sourceUnavailable }
        if !requests.isEmpty, let document = cancellationDocument {
            cancellationDocument = nil
            // The first post-resolve declaration has only three reads left:
            // final inspectionCheck, provider admission, session.check's owner
            // inspectionCheck. The last read runs in exploreProvider's child,
            // after the resolve deadline has already returned.
            document.arm(afterReads: 3)
        }
        return snapshot()
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        return .init(value: .init(url: URL(string: "https://media.invalid/a/\(requests.count).flac")!,
                     delivery: .init(quality: "flac", evidence: .officialResponse, duration: 180)), source: snapshot())
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot())
    }
    func calls() -> [MusicScriptRequest] { requests }
}

final class MusicSourceSessionTests: XCTestCase {
    func testColdSharedSessionUsesExecutedGenerationForAutomaticQualityExactAndURLHistory() async throws {
        for quality in [nil, "flac"] as [String?] {
            let scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported(["tx"]); await scripts.directory(.idle)
            let cold = try await scripts.source(installationID: MusicNativeCandidateDiscoveryFixture.installation)
            XCTAssertTrue(cold.enabled); XCTAssertTrue(cold.isCandidate)
            XCTAssertEqual(cold.state, .idle); XCTAssertEqual(cold.generation, 0)
            XCTAssertEqual(cold.capabilities.first?.actions, ["musicUrl"])
            let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
                scripts: scripts, qq: qq, http: http)
            let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
            var initialUpdates = await inspection.updates().makeAsyncIterator()
            let initial = try await initialUpdates.next()
            XCTAssertTrue(initial?.sources.isEmpty == true)
            let before = await scripts.calls()
            let stillCold = try await scripts.source(installationID: cold.installationID)
            XCTAssertTrue(before.isEmpty); XCTAssertEqual(stillCold, cold)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let first = try await session.match(quality: quality, sourceID: nil)
            let executed = try await scripts.source(installationID: cold.installationID)
            XCTAssertEqual(executed.state, .ready); XCTAssertGreaterThan(executed.generation, 0)
            XCTAssertEqual(first.selection.generation, executed.generation)
            XCTAssertEqual(first.selection.scriptIdentity, executed.identity)
            XCTAssertEqual(first.selection.quality, "flac")
            XCTAssertEqual(first.candidate.resolvedSelection, first.selection)
            let initialCalls = await scripts.calls()
            XCTAssertEqual(initialCalls.count, 1); XCTAssertEqual(initialCalls.first?.source, cold)
            await service.discardSource(attemptID: first.candidate.attemptId)
            for intent in [MusicSourceIntent.exact(first.selection), .quality("flac"), .automatic] {
                let reused = try await session.match(intent: intent)
                XCTAssertEqual(reused.selection, first.selection)
                XCTAssertEqual(reused.candidate.resolvedSelection, reused.selection)
                XCTAssertEqual(reused.candidate.url, first.candidate.url)
                await service.discardSource(attemptID: reused.candidate.attemptId)
            }
            let sameSessionCalls = await scripts.calls()
            XCTAssertEqual(sameSessionCalls.count, 1, "The shared session must reuse the executed generation")
            var updates = await inspection.updates().makeAsyncIterator()
            let snapshot = try await updates.next()
            XCTAssertEqual(snapshot?.sources.map(\.selection), [first.selection])
            await service.sourceConfigurationChanged()
            let reopened = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let history = try await reopened.match(intent: .exact(first.selection))
            XCTAssertEqual(history.selection, first.selection)
            XCTAssertEqual(history.candidate.resolvedSelection, history.selection)
            XCTAssertEqual(history.candidate.url, first.candidate.url)
            let calls = await scripts.calls(), searches = await http.calls(), qqSearches = await qq.calls()
            XCTAssertEqual(calls.count, 1); XCTAssertTrue(searches.isEmpty); XCTAssertTrue(qqSearches.isEmpty)
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            await inspection.close(); await service.close()
        }
    }

    func testColdSharedSessionRejectsGenerationChangeAndNonCandidateAfterExecution() async throws {
        for change in ["generation", "disabled", "failed"] {
            let scripts = MusicNativeCandidateDiscoveryScripts(), gate = MusicNativeCandidateDiscoveryBarrier()
            await scripts.supported(["tx"]); await scripts.configure(barrier: gate); await scripts.directory(.idle)
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let match = Task { try await session.match(intent: .quality("flac")) }
            await gate.started(1)
            let executed = try await scripts.source(installationID: MusicNativeCandidateDiscoveryFixture.installation)
            XCTAssertEqual(executed.state, .ready); XCTAssertGreaterThan(executed.generation, 0)
            if change == "generation" { await scripts.configure(generation: executed.generation + 1) }
            else { await scripts.directory(change == "disabled" ? .disabled : .failed) }
            await gate.release()
            do { _ = try await match.value; XCTFail("Obsolete execution returned prepared media: " + change) }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            var updates = await session.updates().makeAsyncIterator()
            let snapshot = try await updates.next()
            XCTAssertTrue(snapshot?.sources.isEmpty == true)
            let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
            var inspectedUpdates = await inspection.updates().makeAsyncIterator()
            let inspected = try await inspectedUpdates.next()
            XCTAssertTrue(inspected?.sources.isEmpty == true, "Rejected executions must not populate URL history")
            let calls = await scripts.calls(), played = try await service.playedSongs()
            XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls.first?.generation, 0)
            XCTAssertTrue(played.isEmpty)
            await inspection.close(); await service.close()
        }
    }

    func testDefaultLosslessSavedPreferenceAndDisabledTierAreIndependent() async throws {
        let service = try await NativeMusicFixture.service()
        let initial = try await service.state()
        XCTAssertEqual(initial.requestQuality, "flac")
        let saved = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "320k"))
        XCTAssertEqual(saved.requestQuality, "320k")
        let policy = MusicPlaybackState(qualityEnabled: ["lossless", "standard"], qualityOrder: ["lossless", "high", "standard"])
        XCTAssertEqual(MusicSourceQualityPolicy.ordered(policy, automatic: true), ["flac", "128k"])
        await service.close()
    }
    func testOpeningListIsZeroIOAndAutomaticResultIsSharedWithExplicitChoice() async throws {
        let scripts = SourceSessionScripts(), store = try MusicNativeCandidateDiscoveryFixture.store()
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, http: http)
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        var iterator = await lease.updates().makeAsyncIterator()
        let empty = try await iterator.next(); XCTAssertTrue(empty?.sources.isEmpty == true)
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = try await session.match(quality: nil, sourceID: nil)
        XCTAssertEqual(first.selection.quality, "flac")
        var updates = await session.updates().makeAsyncIterator()
        let view = try await updates.next(), row = try XCTUnwrap(view?.sources.first)
        XCTAssertEqual(row.selection, first.selection)
        let explicit = try await session.match(quality: nil, sourceID: row.id)
        XCTAssertEqual(explicit.candidate.url, first.candidate.url)
        let calls = await scripts.calls(), searches = await http.calls(), qqSearches = await qq.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertTrue(searches.isEmpty); XCTAssertTrue(qqSearches.isEmpty)
        await lease.close()
        _ = try await session.match(quality: "flac", sourceID: nil)
        let reused = await scripts.calls(); XCTAssertEqual(reused.count, 1)
        await service.close()
    }
    func testScriptPlatformIntersectionAndNoSpeculativeMissingPlatformSearch() async throws {
        let wy = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        let kg = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kg", id: String(repeating: "A", count: 32)))
        let song = try MusicModel.mergeSources(wy, adding: kg["sources"].array)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let scripts = SourceSessionScripts([("a", ["wy", "kg"]), ("b", ["wy", "mg"]), ("c", ["mg"])])
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.loadMore(refresh: false)
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await state in await session.updates() where !state.isLoading { return }
        }
        let calls = await scripts.calls()
        XCTAssertEqual(calls.filter { $0.installationID == "a" }.map(\.siteID).sorted(), ["kg", "wy"])
        XCTAssertEqual(calls.filter { $0.installationID == "b" }.map(\.siteID), ["wy"])
        XCTAssertFalse(calls.contains { $0.installationID == "c" })
        // Explicit expansion may discover mg; use automatic fresh session below to assert no speculative search.
        await service.close()
        let nextScripts = SourceSessionScripts([("a", ["wy", "kg"]), ("b", ["wy", "mg"]), ("c", ["mg"])])
        let next = try await NativeMusicFixture.service(store, scripts: nextScripts)
        let automatic = try await next.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await automatic.match(quality: nil, sourceID: nil)
        let sent = await nextScripts.calls()
        XCTAssertFalse(sent.isEmpty)
        XCTAssertTrue(sent.allSatisfy { ($0.installationID == "a" && ["wy", "kg"].contains($0.siteID)) ||
            ($0.installationID == "b" && $0.siteID == "wy") })
        XCTAssertFalse(calls.contains { $0.installationID == "c" && $0.siteID != "mg" })
        await next.close()
    }
    func testEveryLosslessPeerIsTriedBeforeStandardAndManualNeverDegrades() async throws {
        let scripts = SourceSessionScripts([("a", ["tx"]), ("b", ["tx"])])
        await scripts.configure(failQualities: ["flac"])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        do { _ = try await session.match(quality: "flac", sourceID: nil); XCTFail("manual downgrade") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        let manual = await scripts.calls(); XCTAssertEqual(manual.count, 2); XCTAssertTrue(manual.allSatisfy { $0.quality == "flac" })
        let auto = try await session.match(quality: nil, sourceID: nil)
        XCTAssertEqual(auto.selection.quality, "128k")
        let calls = await scripts.calls(); XCTAssertTrue(calls.prefix(2).allSatisfy { $0.quality == "flac" })
        let state = try await service.state(); XCTAssertEqual(state.requestQuality, "flac")
        await service.close()
    }
    func testExclusionIsDurableAcrossOwnerRestartAndQualityButNotOtherProvider() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = SourceSessionScripts([("a", ["tx"]), ("b", ["tx"])])
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        try await service.excludeSource(for: .init(kind: .queue, id: "entry-1"), provider: "a", platform: "tx")
        let rows = try await service.sourceExclusions(); XCTAssertEqual(rows.count, 1)
        await service.close()
        let reopened = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await reopened.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let result = try await session.match(quality: "128k", sourceID: nil)
        XCTAssertEqual(result.selection.installationId, "b")
        let calls = await scripts.calls(); XCTAssertFalse(calls.contains { $0.installationID == "a" })
        try await reopened.restoreSourceExclusion(id: rows[0].id)
        let cleared = try await reopened.sourceExclusions(); XCTAssertTrue(cleared.isEmpty)
        await reopened.close()
    }
    func testPreparedAndArmedReplacementCannotRetireOldAttemptBeforeNativeFact() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: SourceSessionScripts())
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = try await session.match(quality: "flac", sourceID: nil)
        try await service.activateSource(attemptID: first.candidate.attemptId)
        func fact(_ value: MusicPreparedSource, _ kind: MusicPlaybackFact.Kind) -> MusicPlaybackFact {
            .init(attemptId: value.candidate.attemptId, occurrenceId: "entry-1", resourceId: value.candidate.resourceId, kind: kind, position: 20)
        }
        _ = try await service.record(fact(first, .playing))
        let next = try await session.match(quality: "128k", sourceID: nil)
        try await service.activateSource(attemptID: next.candidate.attemptId)
        let stillPlaying = try await service.record(fact(first, .progress)); XCTAssertTrue(stillPlaying)
        await service.discardSource(attemptID: next.candidate.attemptId)
        let retained = try await service.record(fact(first, .paused)); XCTAssertTrue(retained)
        let history = try await service.playedSongs(); XCTAssertEqual(history.count, 1)
        await service.close()
    }
    func testSettingsBeforeActivationRejectPreparationButAfterActivationKeepNativeFactOwner() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: SourceSessionScripts())
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let stale = try await session.match(quality: "flac", sourceID: nil)
        let initial = try await service.state()
        let changed = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        do { try await service.activateSource(attemptID: stale.candidate.attemptId); XCTFail("Stale preparation activated") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        await service.discardSource(attemptID: stale.candidate.attemptId)
        let current = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: changed.revision)
        let live = try await current.match(quality: "128k", sourceID: nil)
        try await service.activateSource(attemptID: live.candidate.attemptId)
        _ = try await service.updateSettings(.init(ifRevision: changed.settingsRevision, requestQuality: "flac"))
        await service.sourceConfigurationChanged()
        let accepted = try await service.record(.init(attemptId: live.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: live.candidate.resourceId, kind: .playing, position: 21))
        XCTAssertTrue(accepted)
        let history = try await service.playedSongs(); XCTAssertEqual(history.count, 1)
        await service.close()
    }
    func testArmedFileCacheKeepsOldAttemptUntilFactAndSurvivesLaterPreferenceChange() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        let session = try await f.service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let next = try await session.match(quality: "flac", sourceID: nil)
        XCTAssertTrue(next.candidate.isCachedAudio)
        try await f.service.activateSource(attemptID: next.candidate.attemptId)
        let old = try await f.service.record(f.fact(f.original, kind: .playing)); XCTAssertTrue(old)
        let state = try await f.service.state()
        _ = try await f.service.updateSettings(.init(ifRevision: state.settingsRevision, requestQuality: "128k"))
        await f.service.sourceConfigurationChanged()
        let accepted = try await f.service.record(f.fact(next.candidate, kind: .playing)); XCTAssertTrue(accepted)
        let counts = await f.accounts.counts(); XCTAssertEqual(counts.resolves, 0)
        await f.service.close()
    }
    func testActivatedOfficialSourceCanRefreshExactURLAfterItsFirstNativeFact() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let session = try await f.service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let next = try await session.match(quality: "flac", sourceID: nil)
        try await f.service.activateSource(attemptID: next.candidate.attemptId)
        let accepted = try await f.service.record(f.fact(next.candidate, kind: .playing)); XCTAssertTrue(accepted)
        let recovered = try await f.service.recoverPlayback(previousAttemptID: next.candidate.attemptId,
            attemptID: UUID().uuidString.lowercased(), rejectedURL: next.candidate.url, position: 42)
        XCTAssertEqual(recovered.selection, next.selection)
        XCTAssertEqual(recovered.candidate.startPosition, 42)
        XCTAssertEqual(recovered.candidate.url, MusicRecoveryTestAccounts.renewedURL)
        await f.service.close()
    }
    func testCancelledPublicationDoesNotConsumeTheOnlyLosslessPeerInTheSameSession() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let historyBefore = try MusicDocument(store.read(), runID: "session-cancellation").history
        let document = SourceSessionCancellationDocument(store)
        let scripts = SourceSessionPublicationScripts(cancellingPublicationWith: document)
        let service = NativeMusicService(document: document, scripts: scripts,
            qq: MusicNativeCandidateDiscoveryQQ(), enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = Task { try await session.match(intent: .quality("flac")) }
        do { _ = try await first.value; XCTFail("Cancellation before publication must reject the first match") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let injected = document.injectionCounts()
        XCTAssertEqual(injected.arms, 1); XCTAssertEqual(injected.reads, 3); XCTAssertEqual(injected.cancellations, 1)
        let firstCalls = await scripts.calls()
        XCTAssertEqual(firstCalls.count, 1)
        var updates = await session.updates().makeAsyncIterator()
        let update = try await updates.next(), cancelled = try XCTUnwrap(update)
        XCTAssertFalse(cancelled.isLoading); XCTAssertTrue(cancelled.sources.isEmpty)
        let cancelledHistory = try await service.playedSongs(), cancelledExclusions = try await service.sourceExclusions()
        XCTAssertTrue(cancelledHistory.isEmpty); XCTAssertTrue(cancelledExclusions.isEmpty)

        // The final owner's read finishes after its cancellation checks, so
        // publication must reject this response without exhausting its peer.
        // Retry this exact session without beginSelection, refresh, or reconstruction.
        let retried = try await session.match(intent: .quality("flac"))
        XCTAssertEqual(retried.selection.installationId, "a")
        XCTAssertEqual(retried.selection.siteId, "tx"); XCTAssertEqual(retried.selection.quality, "flac")
        XCTAssertEqual(retried.candidate.url, URL(string: "https://media.invalid/a/2.flac"))
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 2)
        XCTAssertTrue(calls.allSatisfy { $0.installationID == "a" && $0.siteID == "tx" && $0.quality == "flac" })
        XCTAssertEqual(document.injectionCounts().cancellations, 1)
        let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
        let discovery = await service.sourceDiscoveryState(song)
        XCTAssertTrue(history.isEmpty); XCTAssertTrue(exclusions.isEmpty)
        XCTAssertTrue(discovery.entries.isEmpty); XCTAssertTrue(discovery.excluded.isEmpty)
        XCTAssertEqual(try MusicDocument(store.read(), runID: "session-cancellation").history, historyBefore)
        await service.discardSource(attemptID: retried.candidate.attemptId)
        await service.close()
    }
    func testUncancelledPublicationReusesTheOnlyLosslessPeerInTheSameSession() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let historyBefore = try MusicDocument(store.read(), runID: "session-publication").history
        let document = SourceSessionCancellationDocument(store), scripts = SourceSessionPublicationScripts()
        let service = NativeMusicService(document: document, scripts: scripts,
            qq: MusicNativeCandidateDiscoveryQQ(), enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = try await session.match(intent: .quality("flac"))
        let reused = try await session.match(intent: .quality("flac"))
        XCTAssertEqual(first.selection.installationId, "a")
        XCTAssertEqual(first.selection.siteId, "tx"); XCTAssertEqual(first.selection.quality, "flac")
        XCTAssertEqual(reused.selection, first.selection); XCTAssertEqual(reused.candidate.url, first.candidate.url)
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1)
        XCTAssertTrue(calls.allSatisfy { $0.installationID == "a" && $0.siteID == "tx" && $0.quality == "flac" })
        let injected = document.injectionCounts()
        XCTAssertEqual(injected.arms, 0); XCTAssertEqual(injected.reads, 0); XCTAssertEqual(injected.cancellations, 0)
        let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
        let discovery = await service.sourceDiscoveryState(song)
        XCTAssertTrue(history.isEmpty); XCTAssertTrue(exclusions.isEmpty)
        XCTAssertTrue(discovery.entries.isEmpty); XCTAssertTrue(discovery.excluded.isEmpty)
        XCTAssertEqual(try MusicDocument(store.read(), runID: "session-publication").history, historyBefore)
        await service.discardSource(attemptID: first.candidate.attemptId)
        await service.discardSource(attemptID: reused.candidate.attemptId)
        await service.close()
    }
}

@MainActor private final class SourceHandoffPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var stops = 0, commits = 0
    var fail = false
    var gate: NativeMusicGate?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt { resources.append(resource); return .init(id: UUID(), resourceID: resource.id) }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        if let gate { self.gate = nil; await gate.wait(); try Task.checkCancellation() }
        if fail { throw MusicServiceError.invalidMediaCandidate }
        return .init(duration: 180, synchronize: {}, commit: { self.commits += 1; return self.play(resource) }, cancel: {})
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}
@MainActor final class MusicSourceHandoffTests: XCTestCase {
    func testSwitchFailureKeepsOldResourceAndSuccessCommitsPreparedMediaWithoutStop() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: SourceSessionScripts())
        let player = SourceHandoffPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let first = model.activeResourceID; XCTAssertNotNil(first)
        player.fail = true
        await model.switchQuality("128k")
        XCTAssertEqual(model.activeResourceID, first); XCTAssertEqual(player.stops, 0); XCTAssertEqual(player.commits, 0)
        player.fail = false
        await model.switchQuality("128k")
        XCTAssertNotEqual(model.activeResourceID, first); XCTAssertEqual(player.stops, 0); XCTAssertEqual(player.commits, 1)
        XCTAssertEqual(model.state.requestQuality, "flac")
        await model.close(); await service.close()
    }
    func testSettingsCancelPendingMediaPreparationWithoutStoppingOldResource() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: SourceSessionScripts())
        let player = SourceHandoffPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let first = model.activeResourceID, gate = NativeMusicGate()
        player.gate = gate
        let pending = Task { await model.switchQuality("128k") }
        await gate.started()
        await model.updateSettings(.init(ifRevision: model.state.settingsRevision, lyricsEnabled: false))
        await gate.release(); await pending.value
        XCTAssertEqual(model.activeResourceID, first)
        XCTAssertEqual(player.stops, 0); XCTAssertEqual(player.commits, 0)
        XCTAssertFalse(model.isResolving)
        await model.close(); await service.close()
    }
    func testWrongSourceWithoutSameQualityAlternativePausesAndDoesNotNavigate() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: SourceSessionScripts())
        let player = SourceHandoffPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        XCTAssertNotNil(model.activeResourceID)
        await model.rejectCurrentSource()
        XCTAssertEqual(model.current?.id, "entry-1"); XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(model.phase, .paused); XCTAssertEqual(player.stops, 1); XCTAssertEqual(player.commits, 0)
        let excluded = try await service.sourceExclusions(); XCTAssertEqual(excluded.count, 1)
        await model.close(); await service.close()
    }
}

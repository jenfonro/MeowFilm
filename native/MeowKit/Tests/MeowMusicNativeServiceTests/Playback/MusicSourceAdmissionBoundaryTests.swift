import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

// Synthetic ports exercise admission boundaries without media/network/Node.
private actor SourceAdmissionBoundaryGate {
    private var entered = false, released = false
    func wait() async throws {
        entered = true
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while !released {
            try Task.checkCancellation()
            guard ContinuousClock.now < deadline else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func started() async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        while !entered {
            guard ContinuousClock.now < deadline else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func release() { released = true }
}

private actor SourceAdmissionBoundaryScripts: MusicScriptResolving {
    let capabilities: [String: [String]]
    private var requests: [String] = []
    private var failures: [String: MusicServiceError] = [:]
    private var sourcesFailure: MusicServiceError?
    init(_ capabilities: [String: [String]] = ["healthy": ["128k", "flac"]]) {
        self.capabilities = capabilities
    }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: capabilities[id] ?? [])])
    }
    func failSources(_ failure: MusicServiceError) { sourcesFailure = failure }
    func sources() throws -> [MusicScriptSource] {
        if let sourcesFailure { throw sourcesFailure }
        return capabilities.keys.sorted().map(snapshot)
    }
    func source(installationID: String) -> MusicScriptSource { snapshot(installationID) }
    func fail(_ id: String, with failure: MusicServiceError?) { failures[id] = failure }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request.installationID + ":" + request.quality)
        if let failure = failures[request.installationID] { throw failure }
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
                     source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> [String] { requests }
}

@MainActor private final class SourceAdmissionBoundaryPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var commits = 0, stops = 0, syncs = 0
    var synchronizationGate: SourceAdmissionBoundaryGate?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        let gate = synchronizationGate; synchronizationGate = nil
        return .init(duration: 180, synchronize: {
            self.syncs += 1
            try await gate?.wait()
        }, commit: {
            self.commits += 1
            return self.play(resource)
        }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicSourceAdmissionBoundaryTests: XCTestCase {
    func testFeedbackReadbackPreservesSavedSourcePreferences() async throws {
        try await feedbackReadback(requested: "128k", official: [])
    }
    func testDefaultSourcePreferencesControl() async throws {
        try await feedbackReadback(requested: "flac", official: MusicAccountPlatform.allCases)
    }
    private func feedbackReadback(requested: String, official: [MusicAccountPlatform]) async throws {
        let service = try await NativeMusicFixture.service()
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.updateSettings(.init(ifRevision: model.state.settingsRevision,
            officialSources: official, requestQuality: requested))
        XCTAssertNil(model.error)
        let page = MusicAccountsSettingsState(owner: model)
        let feedback = try XCTUnwrap(page.feedback)
        feedback.setAccess(available: true, generation: model.accountAccessGeneration)
        await feedback.load()
        feedback.update(.qq, true); await feedback.flush()
        XCTAssertNil(feedback.error)
        XCTAssertEqual(feedback.value?.platforms[.qq]?.enabled, true)
        let saved = try await service.state()
        let qualities = MusicQualitySettingsState(model: model)
        let sources = MusicSourceManagementState(model: model)
        XCTAssertEqual(saved.requestQuality, requested)
        XCTAssertEqual(saved.officialSources, official)
        XCTAssertEqual(model.state.requestQuality, requested, "Feedback changes must carry all source preference fields through readback")
        XCTAssertEqual(qualities.value.requestQuality, requested, "The picker must reflect the saved request quality")
        XCTAssertEqual(model.state.officialSources, official, "An unrelated setting cannot re-enable the official source UI")
        XCTAssertEqual(sources.official, official)
        XCTAssertTrue(player.resources.isEmpty)
        page.close(); await model.close(); await service.close()
    }

    func testOfficialConnectionFailureCannotBlockHealthyScript() async throws {
        try await capabilityFailure(.connectionFailed)
    }
    func testOfficialTimeoutControl() async throws {
        try await capabilityFailure(.timeout)
    }
    func testOfficialOfflineFailureCannotBlockHealthyScript() async throws {
        try await capabilityFailure(.networkOffline)
    }
    func testScriptLocalFailuresAllowPeersAndDoNotPersistExclusions() async throws {
        for failure in [MusicServiceError.connectionFailed, .networkOffline, .scriptFailed, .scriptRuntimeUnavailable,
                        .rateLimited, .busy, .requestRejected] {
            let scripts = SourceAdmissionBoundaryScripts(["bad": ["flac"], "healthy": ["flac"]])
            await scripts.fail("bad", with: failure)
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            // Explicit expansion waits for all providers, independent of winner order.
            _ = try await session.loadMore(refresh: false)
            try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
                for try await state in await session.updates() where !state.isLoading { return }
            }
            let result = try await session.match(intent: .quality("flac"))
            XCTAssertEqual(result.selection.installationId, "healthy")
            let calls = await scripts.calls()
            XCTAssertEqual(Set(calls), ["bad:flac", "healthy:flac"])
            let exclusions = try await service.sourceExclusions()
            XCTAssertTrue(exclusions.isEmpty)
            // A new explicit selection can retry the temporarily unavailable provider.
            await scripts.fail("bad", with: nil)
            await session.beginSelection()
            let queue = try await service.queue()
            let source = try XCTUnwrap(queue.entries.first?.track.sources.first)
            let retried = try await session.match(intent: .exact(.init(installationId: "bad", generation: 1,
                siteId: "tx", sourceKey: source.sourceKey, quality: "flac")))
            XCTAssertEqual(retried.selection.installationId, "bad")
            await service.close()
        }
    }
    func testUnavailableScriptHostDoesNotBlockOfficialInEitherCapabilityConsumer() async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let scripts = SourceAdmissionBoundaryScripts()
        await scripts.failSources(.scriptRuntimeUnavailable)
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let independent = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await independent.snapshot(discover: false, force: false)
        XCTAssertEqual(snapshot.options.count, 1)
        XCTAssertTrue(snapshot.options.first?.playback.installationId.hasPrefix("official:") == true)
        XCTAssertTrue(snapshot.issues.contains { $0.platform == "lx" && $0.failure == .scriptRuntimeUnavailable })
        await independent.close(retainingAttemptID: nil)
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        XCTAssertTrue(model.activePlaybackSelection?.installationId.hasPrefix("official:") == true)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertNil(model.error)
        await model.close(); await service.close()
    }
    func testOfficialAdmissionFailureStillAbortsMatching() async throws {
        for failure in [MusicServiceError.staleAccess, .accessUnavailable, .cancelled] {
            let clock = MusicRecoveryTestClock()
            let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
            await accounts.failOptions(failure)
            let scripts = SourceAdmissionBoundaryScripts()
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, accounts: accounts, clock: { clock.now() })
            let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
            await model.load(); await model.prepareCurrent()
            XCTAssertTrue(player.resources.isEmpty)
            let calls = await scripts.calls()
            XCTAssertTrue(calls.isEmpty)
            await model.close(); await service.close()
        }
    }
    func testIndependentCapabilityConsumerAlsoRetainsHealthyCandidates() async throws {
        for failure in [MusicServiceError.connectionFailed, .networkOffline] {
            let clock = MusicRecoveryTestClock()
            let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
            await accounts.failOptions(failure)
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: SourceAdmissionBoundaryScripts(), accounts: accounts, clock: { clock.now() })
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            XCTAssertEqual(snapshot.options.map(\.playback.installationId), ["healthy"])
            XCTAssertTrue(snapshot.issues.contains { $0.stage == .capability && $0.platform == "tx" && $0.failure == failure })
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }
    func testPreparedAdmissionReleaseDoesNotRevokeSharedAccountGrantOrCommittedPlayback() async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let grant = await accounts.enablePlaybackAuthorization()
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            accounts: accounts, clock: { clock.now() })
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let discarded = try await session.match(intent: .quality("flac"))
        let cancelled = try await session.match(intent: .quality("128k"))
        await service.discardSource(attemptID: discarded.candidate.attemptId)
        XCTAssertThrowsError(try discarded.candidate.resource())
        XCTAssertNoThrow(try cancelled.candidate.resource())
        XCTAssertTrue(grant.isActive)
        try await service.cancel(attemptId: nil)
        XCTAssertThrowsError(try cancelled.candidate.resource())
        XCTAssertTrue(grant.isActive)
        let committed = try await session.match(intent: .quality("flac"))
        try await service.activateSource(attemptID: committed.candidate.attemptId)
        let fact = MusicPlaybackFact(attemptId: committed.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: committed.candidate.resourceId, kind: .playing, position: 23, duration: 180)
        let accepted = try await service.record(fact)
        XCTAssertTrue(accepted)
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, requestQuality: "128k"))
        await service.sourceConfigurationChanged()
        await service.discardSource(attemptID: committed.candidate.attemptId)
        XCTAssertNoThrow(try committed.candidate.resource())
        XCTAssertTrue(grant.isActive)
        grant.revoke()
        XCTAssertThrowsError(try committed.candidate.resource(), "Parent account revocation still reaches admitted media")
        await service.close()
    }
    private func capabilityFailure(_ failure: MusicServiceError) async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        await accounts.failOptions(failure)
        let scripts = SourceAdmissionBoundaryScripts()
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let calls = await scripts.calls(), counts = await accounts.counts()
        XCTAssertGreaterThan(counts.options, 0)
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "healthy", "One provider connection failure is not a global admission failure")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(calls, ["healthy:flac"])
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertNil(model.error)
        await model.close(); await service.close()
    }

    func testExclusionDuringFinalSynchronizationPreventsCommit() async throws {
        try await exclusionDuringSynchronization(platform: "tx")
    }
    func testOtherPlatformExclusionControl() async throws {
        try await exclusionDuringSynchronization(platform: "wy")
    }
    func testRejectBeforeFirstPlaybackFactStillFindsSameTierReplacement() async throws {
        let scripts = SourceAdmissionBoundaryScripts(["a": ["flac"], "b": ["flac"]])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let rejected = try XCTUnwrap(model.activePlaybackSelection?.installationId)
        await model.rejectCurrentSource()
        let replacement = try XCTUnwrap(model.activePlaybackSelection)
        XCTAssertNotEqual(replacement.installationId, rejected)
        XCTAssertEqual(replacement.quality, "flac")
        XCTAssertEqual(player.stops, 1)
        XCTAssertEqual(player.commits, 0, "A rejected item is never a handoff donor")
        XCTAssertEqual(model.phase, .preparing)
        let rows = try await service.sourceExclusions()
        XCTAssertEqual(rows.map(\.provider), [rejected])
        await model.close(); await service.close()
    }
    func testFailedExclusionWriteRetiresRejectedMediaWithoutPretendingItWasSaved() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: SourceAdmissionBoundaryScripts())
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        store.fail(.storageFailed)
        await model.rejectCurrentSource()
        XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(model.phase, .paused)
        let message = try XCTUnwrap(model.error)
        XCTAssertNotNil(message.range(of: "^QQ 音乐 · 播放歌曲：保存失败（诊断 [A-Fa-f0-9]{8}）$", options: .regularExpression))
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertEqual(player.stops, 1)
        store.fail(nil)
        let exclusions = try await service.sourceExclusions()
        XCTAssertTrue(exclusions.isEmpty)
        await model.close(); await service.close()
    }
    private func fact(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind) async throws {
        let c = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: c.attemptId, occurrenceId: c.occurrenceId,
            resourceId: c.resourceId, kind: kind, position: 23, duration: 180))
        await model.flushPlaybackFacts()
    }
    private func exclusionDuringSynchronization(platform: String) async throws {
        let scripts = SourceAdmissionBoundaryScripts(["a": ["128k"], "b": ["flac"]])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let initial = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        let player = SourceAdmissionBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "a")
        let donor = try XCTUnwrap(model.activeResourceID)
        let gate = SourceAdmissionBoundaryGate(); player.synchronizationGate = gate
        let task = Task { await model.switchQuality("flac") }
        try await gate.started()
        // The production ViewModel invokes synchronize only AFTER activateSource.
        // Use the same public exclusion port as the settings page while it waits.
        try await service.excludeSource(for: .init(kind: .queue, id: "entry-1"), provider: "b", platform: platform)
        let exclusions = try await service.sourceExclusions()
        await gate.release(); await task.value
        if model.activeResourceID != donor { try await fact(model, .playing) }
        XCTAssertEqual(exclusions.count, 1)
        XCTAssertEqual(exclusions.first?.provider, "b")
        XCTAssertEqual(player.stops, 0)
        if platform == "tx" {
            XCTAssertEqual(player.commits, 0, "A matching durable exclusion was committed before native handoff")
            XCTAssertEqual(model.activeResourceID, donor, "Keep the still-allowed healthy donor")
            XCTAssertEqual(model.activePlaybackSelection?.installationId, "a")
            XCTAssertEqual(player.resources.count, 1, "The excluded pending media must never reach playback")
        } else {
            XCTAssertEqual(player.commits, 1)
            XCTAssertEqual(model.activePlaybackSelection?.installationId, "b")
            XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
            XCTAssertNil(model.error)
        }
        await model.close(); await service.close()
    }
}

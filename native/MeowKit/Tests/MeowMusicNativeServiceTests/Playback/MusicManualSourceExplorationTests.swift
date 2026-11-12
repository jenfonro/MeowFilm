import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService
@testable import MeowMusicPlatform

private actor ManualExplorationScripts: MusicScriptResolving {
    static let qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master"]
    let clock: MusicRecoveryTestClock
    private var requests: [String] = []
    private var listings = 0
    private var enabled = true
    init(_ clock: MusicRecoveryTestClock) { self.clock = clock }
    func disable() { enabled = false }
    func calls() -> (listings: Int, requests: [String]) { (listings, requests) }
    private func snapshot() -> MusicScriptSource {
        .init(installationID: "manual", generation: 1, enabled: enabled,
              state: enabled ? .ready : .disabled, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: "Manual source", actions: ["musicUrl"],
                                   qualities: Self.qualities)])
    }
    func sources() -> [MusicScriptSource] { listings += 1; return [snapshot()] }
    func source(installationID: String) -> MusicScriptSource { snapshot() }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request.quality)
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.quality)/\(requests.count)")!,
            expiresAt: clock.now().addingTimeInterval(60),
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
            source: snapshot())
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot())
    }
}

private struct ManualExplorationFileCache: MusicAudioFileCaching {
    let hit: MusicAudioFileCacheHit
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? {
        hit.identity.scope == scope && accepts(hit.identity) ? hit : nil
    }
    func preparePlayback(_ request: MusicAudioFilePlaybackRequest) async throws -> any MusicAudioFilePlayback { MusicTestByteSource() }
    func storeAfterPlayback(_ request: MusicAudioFileCacheRequest) -> MusicAudioFileCacheVersion? { nil }
    func touch(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion,
               confirmedAt: Date, authorization: MusicQueueAuthorization) {}
    func remove(_ identity: MusicAudioFileCacheIdentity, version: MusicAudioFileCacheVersion) {}
    func cancelPending() {}
    func clear() {}
    func clearOfficial() {}
    func close() {}
}

@MainActor private final class ManualExplorationPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var preparations = 0, commits = 0
    var gate: NativeMusicGate?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        try await preparePlayback(resource, autoplay: true)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        preparations += 1
        if let gate { self.gate = nil; await gate.wait(); try Task.checkCancellation() }
        return .init(duration: 180, synchronize: {}, commit: {
            self.commits += 1; return self.play(resource)
        }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) {}
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicManualSourceExplorationTests: XCTestCase {
    private func song(types: [String] = ManualExplorationScripts.qualities) throws -> MJ {
        var raw = try MJ.parse(MusicNativeCandidateDiscoveryFixture.track(id: "101").data, maximum: 16 * 1024)
        raw["types"] = .array(types.map { .obj([("type", .string($0))]) })
        return try MusicModel.normalize(.init(platform: "tx", data: raw.bytes))
    }
    private func policy(_ service: NativeMusicService) async throws {
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision,
            qualityEnabled: ["lossless", "standard"], qualityOrder: ["standard", "lossless"],
            spatialEnabled: [], requestQuality: "flac"))
    }
    private func page(_ session: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await session.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
    }
    func testManualPagesReachAllScriptQualitiesAndNeverRepeatSuccessfulCandidates() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ManualExplorationScripts(clock)
        let qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: song()), scripts: scripts, qq: qq, clock: { clock.now() })
        try await policy(service)
        let settingsBefore = try await service.state()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        var updates = await session.updates().makeAsyncIterator()
        let opened = try await updates.next()
        XCTAssertTrue(try XCTUnwrap(opened).sources.isEmpty)
        let openingCalls = await scripts.calls(), openingSearches = await qq.calls()
        XCTAssertEqual(openingCalls.listings, 0); XCTAssertTrue(openingCalls.requests.isEmpty)
        XCTAssertTrue(openingSearches.isEmpty, "Opening the sheet is read-only")
        let automatic = try await session.match(intent: .automatic)
        XCTAssertEqual(automatic.selection.quality, "flac")
        await service.discardSource(attemptID: automatic.candidate.attemptId)
        var result = try await page(session)
        for _ in 0..<ManualExplorationScripts.qualities.count where result.hasMore { result = try await page(session) }
        XCTAssertFalse(result.hasMore)
        XCTAssertEqual(Set(result.sources.map(\.selection.quality)), Set(ManualExplorationScripts.qualities))
        XCTAssertTrue(result.sources.allSatisfy { $0.selection.quality == $0.delivery?.quality })
        let calls = await scripts.calls()
        XCTAssertEqual(calls.requests.count, ManualExplorationScripts.qualities.count)
        XCTAssertEqual(Set(calls.requests), Set(ManualExplorationScripts.qualities))
        _ = try await page(session)
        await session.beginSelection()
        _ = try await page(session)
        let after = await scripts.calls(), searches = await qq.calls()
        XCTAssertEqual(after.requests, calls.requests, "Successful candidates remain complete after viewing or choosing")
        XCTAssertTrue(searches.isEmpty)
        let unchanged = try await service.state()
        XCTAssertEqual(unchanged.requestQuality, settingsBefore.requestQuality)
        XCTAssertEqual(unchanged.qualityEnabled, settingsBefore.qualityEnabled)
        XCTAssertEqual(unchanged.qualityOrder, settingsBefore.qualityOrder)
        XCTAssertEqual(unchanged.settingsRevision, settingsBefore.settingsRevision)
        await service.close()
    }
    func testDisabledAutomaticTierCanBeChosenFromHistoryAndRenewedWithoutSearching() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ManualExplorationScripts(clock)
        let qq = MusicNativeCandidateDiscoveryQQ()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: song()), scripts: scripts, qq: qq, clock: { clock.now() })
        try await policy(service)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let queue = try await service.queue(), source = try XCTUnwrap(queue.entries.first?.track.sources.first)
        let selected = MusicPlaybackSelection(installationId: "manual", generation: 1, siteId: "tx",
                                             sourceKey: source.sourceKey, quality: "master")
        let first = try await session.match(intent: .exact(selected))
        let cached = try await session.match(intent: .exact(first.selection))
        XCTAssertEqual(cached.candidate.url, first.candidate.url)
        let beforeExpiry = await scripts.calls()
        XCTAssertEqual(beforeExpiry.requests, ["master"])
        clock.advance(61)
        let renewed = try await session.match(intent: .exact(first.selection))
        XCTAssertEqual(renewed.selection, first.selection)
        XCTAssertNotEqual(renewed.candidate.url, first.candidate.url)
        let after = await scripts.calls(), searches = await qq.calls()
        XCTAssertEqual(after.requests, ["master", "master"]); XCTAssertTrue(searches.isEmpty)
        await service.close()
    }
    func testExplicitInitialSelectionPublishesIntentOnlyAfterMediaPreparation() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ManualExplorationScripts(clock)
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: song()), scripts: scripts, clock: { clock.now() })
        try await policy(service)
        let player = ManualExplorationPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let gate = NativeMusicGate(); player.gate = gate
        let task = Task { await model.switchQuality("master") }
        await gate.started()
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNil(model.candidate)
        if case .automatic = model.sourceIntent {} else { XCTFail("Unprepared master is not committed intent") }
        await gate.release(); await task.value
        XCTAssertEqual(model.activePlaybackSelection?.quality, "master")
        XCTAssertEqual(player.preparations, 1); XCTAssertEqual(player.commits, 1)
        XCTAssertEqual(model.state.requestQuality, "flac")
        await model.close(); await service.close()
    }
    func testRecordingDeclaredQualitiesStillConstrainManualExploration() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ManualExplorationScripts(clock)
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: song(types: ["192k", "master"])),
            scripts: scripts, clock: { clock.now() })
        try await policy(service)
        let session = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        var result = try await page(session)
        for _ in 0..<3 where result.hasMore { result = try await page(session) }
        XCTAssertEqual(Set(result.sources.map(\.selection.quality)), ["192k", "master"])
        let calls = await scripts.calls()
        XCTAssertEqual(Set(calls.requests), ["192k", "master"])
        await service.close()
    }
    func testDisabledScriptAndDurableProviderExclusionPreventManualResolution() async throws {
        for disabled in [false, true] {
            let clock = MusicRecoveryTestClock(), scripts = ManualExplorationScripts(clock)
            let service = try await NativeMusicFixture.service(
                MusicNativeCandidateDiscoveryFixture.store(song: song()), scripts: scripts, clock: { clock.now() })
            if disabled { await scripts.disable() }
            else { try await service.excludeSource(for: .init(kind: .queue, id: "entry-1"), provider: "manual", platform: "tx") }
            let session = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
            let result = try await page(session), calls = await scripts.calls()
            XCTAssertTrue(result.sources.isEmpty); XCTAssertTrue(calls.requests.isEmpty)
            await service.close()
        }
    }
    func testManualOfficialTierKeepsAuthorizationAndSameTierURLRecovery() async throws {
        let clock = MusicRecoveryTestClock()
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        await accounts.allow(["master"])
        let authorization = await accounts.enablePlaybackAuthorization()
        let service = try await NativeMusicFixture.service(
            MusicNativeCandidateDiscoveryFixture.store(song: song()), accounts: accounts, clock: { clock.now() })
        try await policy(service)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let prepared = try await session.match(intent: .quality("master"))
        try await service.activateSource(attemptID: prepared.candidate.attemptId)
        _ = try await service.record(.init(attemptId: prepared.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: prepared.candidate.resourceId, kind: .playing, position: 37, duration: 180))
        let renewed = try await service.recoverPlayback(previousAttemptID: prepared.candidate.attemptId,
            attemptID: UUID().uuidString.lowercased(), rejectedURL: prepared.candidate.url, position: 37)
        XCTAssertEqual(renewed.selection.quality, "master"); XCTAssertEqual(renewed.candidate.startPosition, 37)
        let counts = await accounts.counts()
        XCTAssertEqual(counts.refreshes, 1)
        authorization.revoke()
        do { _ = try renewed.candidate.resource(); XCTFail("Manual selection cannot bypass revoked authorization") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        await service.close()
    }
    func testManualCompleteFileIgnoresAutomaticTierAndPolicySignature() async throws {
        for official in [false, true] {
            let clock = MusicRecoveryTestClock()
            let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: song())
            let document = try MusicDocument(store.read(), runID: "manual-file")
            let recording = try song(), source = try XCTUnwrap(MusicModel.sources(recording).first)
            let scope: String
            if official { scope = try await accounts.nativeCacheScope().scope }
            else { scope = "script-history" }
            let identity = MusicAudioFileCacheIdentity(scope: scope, songID: recording["songId"].string,
                sourceKey: source.sourceKey, platform: "tx", nativeID: source.nativeId, requestedQuality: "master",
                playbackSignature: official ? NativeMusicService.audioCacheSignature(song: recording, playback: document.playback) :
                    NativeMusicService.scriptAudioSignature(recording),
                provider: official ? "official:recovery-synthetic" : "manual")
            let cache = ManualExplorationFileCache(hit: .init(identity: identity,
                fileURL: URL(fileURLWithPath: "/synthetic/manual-master.flac"), duration: 180,
                measuredQuality: "master", expiresAt: clock.now().addingTimeInterval(3600)))
            let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
                audioFileCache: cache, transport: NativeMusicNoNetwork(), clock: { clock.now() })
            try await service.setAccess(available: true, generation: 1)
            try await policy(service)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let file = try await session.match(intent: .quality("master"))
            XCTAssertTrue(file.candidate.isCachedAudio); XCTAssertEqual(file.selection.quality, "master")
            let counts = await accounts.counts()
            XCTAssertEqual(counts.options, 0); XCTAssertEqual(counts.resolves, 0)
            await service.close()
        }
    }
    func testAutomaticHighTierRetains320Before192() {
        let state = MusicPlaybackState(qualityEnabled: ["lossless", "high", "standard"],
                                       qualityOrder: ["lossless", "high", "standard"], requestQuality: "flac")
        let order = MusicSourceQualityPolicy.ordered(state, automatic: true)
        XCTAssertEqual(order, ["flac", "320k", "192k", "128k"])
    }
}

import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor ConsumerBoundaryScripts: MusicScriptResolving {
    let clock: MusicRecoveryTestClock
    private var listed = 0, lookedUp: [String] = [], resolved: [String] = []
    private var generation = 1
    init(_ clock: MusicRecoveryTestClock) { self.clock = clock }
    func changeContent() { generation += 1 }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: generation, enabled: true, state: .ready, version: String(generation),
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: ["tx", "wy"].map {
                  .init(siteID: $0, title: id, actions: ["musicUrl"], qualities: ["flac", "128k"])
              })
    }
    func sources() -> [MusicScriptSource] { listed += 1; return [snapshot("script")] }
    func source(installationID: String) -> MusicScriptSource {
        lookedUp.append(installationID); return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        resolved.append(request.siteID + ":" + request.quality)
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.siteID).\(request.quality).\(resolved.count)")!,
                     expiresAt: clock.now().addingTimeInterval(60),
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
                     source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> (listed: Int, lookedUp: [String], resolved: [String]) { (listed, lookedUp, resolved) }
}

private actor ConsumerBoundaryAccounts: MusicNativeAccountProviding, MusicNativeCacheScopeProviding {
    let clock: MusicRecoveryTestClock
    private var options: [String] = [], resolutions: [String] = []
    private var failure: MusicServiceError?
    private var scopeFailure: MusicServiceError?
    private let scope = MusicNativeCacheScope(scope: "consumer-official")
    init(_ clock: MusicRecoveryTestClock) { self.clock = clock }
    func fail(_ failure: MusicServiceError?) { self.failure = failure }
    func failScope(_ failure: MusicServiceError) { scopeFailure = failure }
    func nativeCacheScope() throws -> MusicNativeCacheScope {
        if let scopeFailure { throw scopeFailure }
        return scope
    }
    func checkNativeCacheScope(_ scope: MusicNativeCacheScope) throws { try scope.authorization.check() }
    func calls() -> (options: [String], resolutions: [String]) { (options, resolutions) }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        options.append(platform.rawValue)
        return [.init(installationId: "official:consumer", generation: 1, siteId: platform.rawValue,
            sourceKey: sourceKey, qualities: ["flac", "128k"], title: "Official", version: "1",
            contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        resolutions.append(platform.rawValue + ":" + selection.quality)
        if let failure { throw failure }
        return .init(url: URL(string: "https://media.invalid/official.\(platform.rawValue).\(resolutions.count)")!,
                     expiresAt: clock.now().addingTimeInterval(60),
                     delivery: .init(quality: selection.quality, evidence: .officialResponse, duration: 180))
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
}

private struct ConsumerBoundaryEmptyCache: MusicAudioFileCaching {
    func lookup(scope: String, songID: String, authorization: MusicQueueAuthorization,
                accepts: @escaping @Sendable (MusicAudioFileCacheIdentity) -> Bool) -> MusicAudioFileCacheHit? { nil }
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

@MainActor private final class ConsumerBoundaryPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var stops = 0, resumes = 0, handoffs = 0
    var onPause: (() -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        handoffs += 1
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func pause(_ attempt: MusicPlayerAttempt) { onPause?() }
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicSourceConsumerBoundaryTests: XCTestCase {
    private func song() throws -> MJ {
        var song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        song["sources"] = .array(song["sources"].array + peer["sources"].array)
        return song
    }
    private func settled(_ session: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await session.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
    }
    private func emit(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind) throws {
        let candidate = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: kind, position: 41, duration: 180))
    }
    func testRejectedPlayingRecordingReusesSameSessionAndPosition() async throws {
        try await rejectedRecording(playingPeer: true, paused: false)
    }
    func testRejectedPausedRecordingDoesNotResume() async throws {
        try await rejectedRecording(playingPeer: true, paused: true)
    }
    func testRejectingUnplayedRecordingDoesNotReplaceCurrentMedia() async throws {
        try await rejectedRecording(playingPeer: false, paused: false)
    }
    private func rejectedRecording(playingPeer: Bool, paused: Bool) async throws {
        let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
            scripts: scripts, clock: { clock.now() })
        let player = ConsumerBoundaryPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        let session = try await model.inspectSources(for: model.current!.track.ref)
        let results = try await settled(session)
        let rejected = try XCTUnwrap(results.sources.first { $0.selection.siteId == "wy" })
        let selected = try XCTUnwrap(results.sources.first { $0.selection.siteId == (playingPeer ? "wy" : "tx") })
        await model.chooseInspectedSource(selected, session: session)
        try emit(model, paused ? .paused : .playing); await model.flushPlaybackFacts()
        let old = model.activeResourceID, before = await scripts.calls()
        player.onPause = { [weak model] in if let model { try? self.emit(model, .paused) } }
        await model.rejectInspectedSource(rejected, session: session)
        await model.flushPlaybackFacts()
        let after = await scripts.calls()
        XCTAssertNil(model.error)
        XCTAssertEqual(after.resolved, before.resolved, "A valid same-song result must not be resolved again")
        XCTAssertEqual(model.activePlaybackSelection?.siteId, "tx")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(model.current?.track.sources.map(\.platform), ["tx"])
        XCTAssertEqual(model.current?.id, "entry-1")
        XCTAssertEqual(player.stops, playingPeer ? 1 : 0)
        XCTAssertEqual(player.handoffs, 0, "A rejected recording is not a handoff donor")
        XCTAssertEqual(player.resumes, playingPeer && !paused ? 1 : 0)
        if playingPeer {
            XCTAssertNotEqual(model.activeResourceID, old)
            XCTAssertEqual(model.candidate?.startPosition, 41)
            if case .quality("flac") = model.sourceIntent {} else { XCTFail("Recording replacement must keep the same flac tier") }
            if paused { XCTAssertEqual(model.phase, .paused) }
        } else { XCTAssertEqual(model.activeResourceID, old) }
        let exclusions = try await service.sourceExclusions()
        XCTAssertTrue(exclusions.isEmpty, "Recording exclusion must not become provider exclusion")
        await session.close(); await model.close(); await service.close()
    }
    func testExpiredOfficialExactResultDoesNotEnumerateOtherProviders() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let queue = try await service.queue(), source = try XCTUnwrap(queue.entries[0].track.sources.first)
        let selection = MusicPlaybackSelection(installationId: "official:consumer", generation: 1,
            siteId: "tx", sourceKey: source.sourceKey, quality: "flac")
        let first = try await session.match(intent: .exact(selection))
        await service.discardSource(attemptID: first.candidate.attemptId)
        clock.advance(61); await session.beginSelection()
        let second = try await session.match(intent: .exact(selection))
        XCTAssertEqual(second.selection, first.selection)
        XCTAssertNotEqual(second.candidate.url, first.candidate.url)
        let calls = await accounts.calls(), scriptCalls = await scripts.calls()
        XCTAssertEqual(Set(calls.options), ["tx"])
        XCTAssertEqual(calls.resolutions, ["tx:flac", "tx:flac"])
        XCTAssertEqual(scriptCalls.listed, 0)
        XCTAssertTrue(scriptCalls.lookedUp.isEmpty)
        XCTAssertTrue(scriptCalls.resolved.isEmpty)
        await service.close()
    }
    func testExpiredScriptExactResultAndContentUpdateStayTargeted() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let queue = try await service.queue(), source = try XCTUnwrap(queue.entries[0].track.sources.first)
        let selection = MusicPlaybackSelection(installationId: "script", generation: 1,
            siteId: "tx", sourceKey: source.sourceKey, quality: "flac")
        var session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = try await session.match(intent: .exact(selection))
        await service.discardSource(attemptID: first.candidate.attemptId)
        clock.advance(61); await session.beginSelection()
        let second = try await session.match(intent: .exact(selection))
        XCTAssertNotEqual(first.candidate.url, second.candidate.url)
        await service.discardSource(attemptID: second.candidate.attemptId)
        await scripts.changeContent(); await service.sourceConfigurationChanged()
        session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let updated = try await session.match(intent: .exact(selection))
        XCTAssertEqual(updated.selection.installationId, "script")
        XCTAssertEqual(updated.selection.siteId, "tx")
        XCTAssertEqual(updated.selection.generation, 2)
        let calls = await accounts.calls(), scriptCalls = await scripts.calls()
        XCTAssertTrue(calls.options.isEmpty); XCTAssertTrue(calls.resolutions.isEmpty)
        XCTAssertEqual(scriptCalls.listed, 0)
        XCTAssertEqual(Set(scriptCalls.lookedUp), ["script"])
        XCTAssertEqual(scriptCalls.resolved, ["tx:flac", "tx:flac", "tx:flac"])
        await service.close()
    }
    func testExactFailureDoesNotFallBackToHealthyPeer() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
        await accounts.fail(.rateLimited)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
            scripts: scripts, accounts: accounts, clock: { clock.now() })
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let queue = try await service.queue(), source = try XCTUnwrap(queue.entries[0].track.sources.first)
        do {
            _ = try await session.match(intent: .exact(.init(installationId: "official:consumer", generation: 1,
                siteId: "tx", sourceKey: source.sourceKey, quality: "flac")))
            XCTFail("Exact selection must report its own failure, not switch provider")
        } catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let calls = await scripts.calls()
        XCTAssertTrue(calls.resolved.isEmpty)
        await service.close()
    }
    func testOfficialResolveLocalFailuresDoNotAbortHealthySameTierScript() async throws {
        for failure in [MusicServiceError.rateLimited, .busy, .requestRejected] {
            let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
            await accounts.fail(failure)
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
                scripts: scripts, accounts: accounts, clock: { clock.now() })
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let results = try await settled(session)
            XCTAssertTrue(results.sources.allSatisfy { $0.selection.installationId == "script" })
            XCTAssertFalse(results.sources.isEmpty)
            let selected = try await session.match(intent: .quality("flac"))
            XCTAssertEqual(selected.selection.installationId, "script")
            XCTAssertEqual(selected.selection.quality, "flac")
            let exclusions = try await service.sourceExclusions()
            XCTAssertTrue(exclusions.isEmpty)
            XCTAssertFalse(MusicCandidateDiscoveryFailures.isContentFailure(failure))
            await service.close()
        }
    }
    func testBusyOfficialCacheScopeDoesNotPreventScriptSelection() async throws {
        let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
        await accounts.failScope(.busy); await accounts.fail(.busy)
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song()),
            scripts: scripts, accounts: accounts, qq: NativeMusicQQFixture(), audioFileCache: ConsumerBoundaryEmptyCache(),
            transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let result = try await session.match(intent: .quality("flac"))
        XCTAssertEqual(result.selection.installationId, "script")
        XCTAssertEqual(result.selection.quality, "flac")
        await service.close()
    }
    func testOfficialResolutionAuthorityFailuresStillAbortSelection() async throws {
        for failure in [MusicServiceError.staleAccess, .accessUnavailable, .storageFailed, .targetChanged, .cancelled,
                        .authRequired, .sessionExpired, .verificationRequired] {
            let clock = MusicRecoveryTestClock(), scripts = ConsumerBoundaryScripts(clock), accounts = ConsumerBoundaryAccounts(clock)
            await accounts.fail(failure)
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song()),
                scripts: scripts, accounts: accounts, clock: { clock.now() })
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            do { _ = try await settled(session); XCTFail("Authority/storage failures are not provider fallback permission") }
            catch { XCTAssertEqual(error as? MusicServiceError, failure) }
            await service.close()
        }
    }
}

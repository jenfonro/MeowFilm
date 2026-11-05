#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private final class MusicCandidateIntegrationPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    var stopped: [MusicPlayerAttempt] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt) }
}

private actor MusicCandidateIntegrationGate {
    let entered: @Sendable () -> Void
    private var open = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !open { await withCheckedContinuation { waiters.append($0) } }
    }
    func release() { open = true; waiters.forEach { $0.resume() }; waiters = [] }
}

private actor MusicCandidateIntegrationScripts: MusicScriptResolving {
    var gate: MusicCandidateIntegrationGate?
    var requests: [MusicScriptRequest] = []
    func hold(_ gate: MusicCandidateIntegrationGate) { self.gate = gate }
    func sources() -> [MusicScriptSource] { [NativeMusicFixture.source()] }
    func source(installationID: String) throws -> MusicScriptSource {
        guard installationID == NativeMusicFixture.installation else { throw MusicServiceError.targetChanged }
        return NativeMusicFixture.source()
    }
    func resolve(request: MusicScriptRequest) async -> MusicNativeAudio {
        requests.append(request)
        if let gate { self.gate = nil; await gate.wait() }
        return .init(url: URL(string: "https://media.invalid/synthetic.flac")!)
    }
    func lyrics(request: MusicScriptRequest) -> MusicNativeLyrics { .init(text: "") }
}


final class MusicCandidateIntegrationTests: XCTestCase {
    @MainActor func testNativeServiceDiscoveryCASViewModelPlayerAndFactOwnerAreOneChain() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["wy"]) // Seed is tx; the existing-only picker cannot resolve it.
        let service = try await NativeMusicFixture.service(store, scripts: scripts,
            qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeCandidateDiscoveryHTTP())
        let player = MusicCandidateIntegrationPlayer(), model = MusicViewModel(service: service, player: player)
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        await model.load(); XCTAssertTrue(player.resources.isEmpty)
        await model.togglePlayback()
        let calls = await scripts.calls(), snapshot = try await service.queue()
        XCTAssertEqual(calls.count, 1, "No choose/second resolve after discovery")
        XCTAssertEqual(calls.first?.siteID, "wy")
        XCTAssertEqual(model.state.revision, 2); XCTAssertEqual(model.queue, snapshot.entries)
        XCTAssertEqual(model.position, 0, "Cold restoration does not restore playback time")
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertTrue(requests.isEmpty); XCTAssertFalse(model.isResolving)
        XCTAssertEqual(store.snapshot().2, 1)
        let before = try await service.playedSongs(); XCTAssertTrue(before.isEmpty)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        model.onPlayback(.init(attemptId: String(resource.dropFirst(6)), occurrenceId: occurrence,
            resourceId: resource, kind: .playing, position: 23))
        await model.flushPlaybackFacts()
        let after = try await service.playedSongs(); XCTAssertEqual(after.count, 1)
        let persisted = String(decoding: try XCTUnwrap(store.snapshot().0), as: UTF8.self)
        XCTAssertFalse(persisted.contains("signature=")); XCTAssertFalse(persisted.contains("media.invalid"))
        await model.close(); await service.close()
    }

    @MainActor func testNativeManualFailureDoesNotSearchSwitchQualityOrTryAnotherSource() async throws {
        let scripts = NativeMusicScriptFixture()
        await scripts.configure(error: .upstreamFailed)
        let service = try await NativeMusicFixture.service(scripts: scripts)
        _ = try await MusicAccountQueueFixture.seed(service)
        let player = MusicCandidateIntegrationPlayer(), model = MusicViewModel(service: service, player: player)
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        await model.load(); await model.prepareCurrent(forceSelection: true)
        XCTAssertEqual(requests, [.sources])
        // The UI now inspects resolved official sources rather than populating
        // declared script qualities. Exercise the retained explicit script API
        // directly; it must still never fall back after a pinned failure.
        let current = try XCTUnwrap(model.current)
        let options = try await service.playbackOptions(for: .init(kind: .queue, id: current.id))
        let choice = try XCTUnwrap(options.first).selection(quality: "128k")
        await model.choose(choice)
        let calls = await scripts.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].installationID, choice.installationId)
        XCTAssertEqual(calls[0].sourceKey, choice.sourceKey); XCTAssertEqual(calls[0].quality, "128k")
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNotNil(model.error)
        await model.close(); await service.close()
    }

    @MainActor func testFMAdmissionPauseRevocationAndNonFMDepartureRejectLateAutomaticCandidate() async throws {
        for action in ["pause", "revoke", "departure"] {
            let scripts = MusicCandidateIntegrationScripts()
            let service = try await NativeMusicFixture.service(scripts: scripts)
            let player = MusicCandidateIntegrationPlayer(), model = MusicViewModel(service: service, player: player)
            await model.load()
            let pages = MusicAccountQueuePages(roots: [
                [NativeMusicFixture.track("First")], [NativeMusicFixture.track("Second")]
            ])
            let source = MusicAccountQueueFixture.source(pages, kind: action == "departure" ? .playlist : .fm)
            let start = try await MusicAccountQueueFixture.start(service, source)
            if action != "departure" {
                await model.playRemoteQueue(source, start: start)
                XCTAssertEqual(player.resources.count, 1)
                model.releaseRemoteQueueSource(source)
                XCTAssertTrue(source.authorization.isActive, "FM ownership transfers to the model")
            }
            let entered = expectation(description: "automatic LX \(action)")
            let gate = MusicCandidateIntegrationGate { entered.fulfill() }
            await scripts.hold(gate)
            let task = Task {
                if action == "departure" { await model.playRemoteQueue(source, start: start) }
                else { await model.next() }
            }
            await fulfillment(of: [entered], timeout: 2)
            if action == "pause" { await model.togglePlayback() }
            else if action == "revoke" { source.revoke() }
            else { model.releaseRemoteQueueSource(source) }
            await gate.release(); await task.value
            XCTAssertEqual(player.resources.count, action == "departure" ? 0 : 1)
            XCTAssertNil(model.activeResourceID); XCTAssertFalse(model.isResolving)
            XCTAssertFalse(model.isChangingQueue); XCTAssertFalse(model.isPreparingRemoteQueue)
            if action == "pause" {
                XCTAssertEqual(model.phase, .paused); XCTAssertEqual(model.queue.count, 2)
                await model.togglePlayback()
                XCTAssertEqual(player.resources.count, 2, "Explicit resume can use automatic selection")
            }
            await model.close(); await service.close()
        }
    }

    @MainActor func testRejectingThePlayingSourceExcludesItAndSelectsAnotherAutomatically() async throws {
        var song = try MusicNativeCandidateDiscoveryFixture.song()
        let first = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "501"))
        let second = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "502"))
        song = try MusicModel.mergeSources(song, adding: first["sources"].array + second["sources"].array)
        let rejected = try XCTUnwrap(first["sources"].array.first)["sourceKey"].string
        let kept = try XCTUnwrap(second["sources"].array.first)["sourceKey"].string
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song), scripts = MusicNativeCandidateDiscoveryScripts()
        await scripts.supported(["wy"])
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: MusicNativeCandidateDiscoveryQQ(),
            http: MusicNativeCandidateDiscoveryHTTP(), sourceDiscovery: cache)
        let player = MusicCandidateIntegrationPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        await model.togglePlayback()
        var calls = await scripts.calls()
        XCTAssertEqual(calls.map(\.sourceKey), [rejected]); XCTAssertEqual(player.resources.count, 1)
        let current = try XCTUnwrap(model.current)
        let session = try await model.inspectSources(for: current.track.ref)
        let source = MusicResolvedSource(id: "rejected", selection: .init(installationId: MusicNativeCandidateDiscoveryFixture.installation,
            generation: 1, siteId: "wy", sourceKey: rejected, quality: "128k"), title: "Same name", delivery: nil)
        await model.rejectInspectedSource(source, session: session)
        XCTAssertNil(model.error)
        calls = await scripts.calls()
        XCTAssertEqual(calls.map(\.sourceKey), [rejected, kept], "the replacement is chosen automatically")
        XCTAssertEqual(player.resources.count, 2); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertEqual(model.state.revision, 2)
        XCTAssertFalse(try XCTUnwrap(model.current).track.sources.contains { $0.sourceKey == rejected })
        let excluded = await cache.excluded(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(excluded, [rejected])
        await session.close(); await model.close(); await service.close()
    }

}
#endif

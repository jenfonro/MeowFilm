#if os(iOS) || os(macOS)
import Foundation
import XCTest
import Combine
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private class MusicCandidateIntegrationPlayer: MusicPlaybackDriving {
    var resources: [PlaybackResource] = []
    var stopped: [MusicPlayerAttempt] = []
    var onStop: (() -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt); onStop?() }
}

@MainActor private final class MusicCandidateIntegrationHandoffPlayer: MusicCandidateIntegrationPlayer, MusicPlaybackHandoffDriving {
    var handoffs: [MusicPlayerAttempt] = []
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        handoffs.append(replacing)
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
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
    func resolve(request: MusicScriptRequest) async -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if let gate { self.gate = nil; await gate.wait() }
        return .init(value: .init(url: URL(string: "https://media.invalid/synthetic.flac")!), source: request.source)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: ""), source: request.source) }
}

@MainActor private func waitForMusicCandidateSignal(_ signal: XCTestExpectation,
                                                   file: StaticString = #filePath, line: UInt = #line) async throws {
    let result = await XCTWaiter.fulfillment(of: [signal], timeout: 2)
    guard result == .completed else {
        XCTFail("Did not receive \(signal.expectationDescription): \(result)", file: file, line: line)
        throw CancellationError()
    }
    try Task.checkCancellation()
}

@MainActor private final class MusicCandidateIntegrationFMFixture {
    let service: NativeMusicService
    let scripts: MusicCandidateIntegrationScripts
    let player: MusicCandidateIntegrationPlayer
    let model: MusicViewModel
    let entered: XCTestExpectation
    let gate: MusicCandidateIntegrationGate
    var source: MusicRemoteQueueSource?
    private var gates: [MusicCandidateIntegrationGate] = []
    private var tasks: [Task<Void, Never>] = []
    private var closing: Task<Void, Never>?
    private let closed = XCTestExpectation(description: "FM tasks, model and service closed")

    init(service: NativeMusicService, scripts: MusicCandidateIntegrationScripts) {
        let player = MusicCandidateIntegrationPlayer()
        let entered = XCTestExpectation(description: "new queue candidate resolve entered")
        self.service = service; self.scripts = scripts
        self.player = player; self.entered = entered
        model = MusicViewModel(service: service, player: player)
        gate = MusicCandidateIntegrationGate { entered.fulfill() }
        gates = [gate]
    }
    func start(_ description: String, _ operation: @escaping @MainActor () async -> Void) throws -> XCTestExpectation {
        try Task.checkCancellation()
        let completed = XCTestExpectation(description: description)
        tasks.append(Task {
            await operation()
            completed.fulfill()
        })
        return completed
    }
    func holdPlaybackFactDrain() throws -> (entered: XCTestExpectation, gate: MusicCandidateIntegrationGate) {
        try Task.checkCancellation()
        let entered = XCTestExpectation(description: "playback fact drain held")
        let gate = MusicCandidateIntegrationGate { entered.fulfill() }
        let previous = model.eventTail
        let task = Task { await previous?.value; await gate.wait() }
        gates.append(gate); tasks.append(task); model.eventTail = task
        return (entered, gate)
    }
    func beginClose() {
        if closing == nil {
            player.onStop = nil
            model.prepareForClose()
            source?.revoke()
            tasks.forEach { $0.cancel() }
            let tasks = tasks, gates = gates, model = model, service = service, source = source, closed = closed
            closing = Task {
                for gate in gates { await gate.release() }
                await model.close(); await source?.close(); await service.close()
                for task in tasks { await task.value }
                closed.fulfill()
            }
        }
    }
    func close() async {
        beginClose()
        let result = await XCTWaiter.fulfillment(of: [closed], timeout: 2)
        if result != .completed {
            closing?.cancel()
            XCTFail("FM cleanup did not finish: \(result)")
        }
    }
}

final class MusicCandidateIntegrationTests: XCTestCase {
    @MainActor func testColdOrdinaryAndAutomaticPlaybackCommitActualIdentityAfterFactsAndRefreshExactGeneration() async throws {
        for automatic in [false, true] {
            let scripts = NativeMusicScriptFixture()
            await scripts.coldStart()
            let service = try await NativeMusicFixture.service(try MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let options = try await service.sourceAvailability(for: .init(kind: .queue, id: "entry-1")).options
            let requested = try XCTUnwrap(options.first).selection(quality: "flac")
            XCTAssertEqual(requested.generation, 0)
            let player = MusicCandidateIntegrationHandoffPlayer(), model = MusicViewModel(service: service, player: player)
            await model.load()
            if automatic { await model.togglePlayback() }
            else { await model.choose(requested) }
            let live = try await scripts.source(installationID: requested.installationId)
            let actual = MusicPlaybackSelection(installationId: live.installationID, generation: 7,
                siteId: requested.siteId, sourceKey: requested.sourceKey, quality: requested.quality,
                scriptIdentity: live.identity)
            let candidate = try XCTUnwrap(model.candidate)
            let attempt = await service.attempt
            XCTAssertEqual(live.generation, 7)
            XCTAssertEqual(candidate.resolvedSelection, actual)
            XCTAssertNil(attempt, "Preparation cannot create an actual playback fact owner")
            XCTAssertEqual(model.lastSelection, actual)
            XCTAssertEqual(model.activePlaybackSelection, actual)
            XCTAssertEqual(player.resources.count, 1)
            let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
            let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
            model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: .playing, position: 23, duration: 180))
            await model.flushPlaybackFacts()
            let committed = await service.attempt
            XCTAssertEqual(committed?.id, candidate.attemptId)
            XCTAssertEqual(committed?.selection, actual)
            XCTAssertEqual(committed?.selection.scriptIdentity, live.identity)
            let played = try await service.playedSongs(); XCTAssertEqual(played.count, 1)

            await scripts.configure(generation: 8)
            await model.choose(actual)
            let refreshed = MusicPlaybackSelection(installationId: actual.installationId, generation: 8,
                siteId: actual.siteId, sourceKey: actual.sourceKey, quality: actual.quality,
                scriptIdentity: live.identity)
            let replacement = try XCTUnwrap(model.candidate)
            XCTAssertNil(model.error)
            XCTAssertEqual(player.resources.count, 2)
            XCTAssertEqual(player.handoffs.count, 1); XCTAssertTrue(player.stopped.isEmpty)
            XCTAssertNotEqual(replacement.attemptId, candidate.attemptId)
            XCTAssertEqual(replacement.resolvedSelection, refreshed)
            XCTAssertEqual(model.activePlaybackSelection, refreshed)
            let afterReload = await scripts.calls(); XCTAssertEqual(afterReload.count, 2)
            XCTAssertEqual(afterReload.last?.source.generation, 8)
            XCTAssertEqual(afterReload.last?.installationID, actual.installationId)
            XCTAssertEqual(afterReload.last?.siteID, actual.siteId)
            XCTAssertEqual(afterReload.last?.sourceKey, actual.sourceKey)
            XCTAssertEqual(afterReload.last?.quality, actual.quality)
            let beforeReplacementFact = await service.attempt
            XCTAssertEqual(beforeReplacementFact?.id, candidate.attemptId)
            XCTAssertEqual(beforeReplacementFact?.selection, actual)
            model.onPlayback(.init(attemptId: replacement.attemptId, occurrenceId: replacement.occurrenceId,
                resourceId: replacement.resourceId, kind: .playing, position: 23, duration: 180))
            await model.flushPlaybackFacts()
            let replaced = await service.attempt
            XCTAssertEqual(replaced?.id, replacement.attemptId)
            XCTAssertEqual(replaced?.selection, refreshed)
            await model.close(); await service.close()
        }
    }

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
        // Exercise the explicit selection API independently of the source
        // inspector's probes; it must never fall back after a pinned failure.
        let current = try XCTUnwrap(model.current)
        let options = (try await service.sourceAvailability(for: .init(kind: .queue, id: current.id))).options
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
            try await withFMFixture { fixture in
                let service = fixture.service, player = fixture.player, model = fixture.model
                let pages = MusicAccountQueuePages(roots: [
                    [NativeMusicFixture.track("First")], [NativeMusicFixture.track("Second")]
                ])
                let source = MusicAccountQueueFixture.source(pages, kind: action == "departure" ? .playlist : .fm)
                fixture.source = source
                let start = try await MusicAccountQueueFixture.start(service, source)
                if action != "departure" {
                    try await waitForMusicCandidateSignal(fixture.start("initial FM playback prepared") {
                        await model.playRemoteQueue(source, start: start)
                    })
                    XCTAssertEqual(player.resources.count, 1)
                    model.releaseRemoteQueueSource(source)
                    XCTAssertTrue(source.authorization.isActive, "FM ownership transfers to the model")
                    try await commitFMPlayingAndWaitForBatch(fixture)
                }
                let before = try await service.playedSongs()
                await fixture.scripts.hold(fixture.gate)
                let completed = try fixture.start("automatic LX \(action) completed") {
                    if action == "departure" { await model.playRemoteQueue(source, start: start) }
                    else { await model.next() }
                }
                try await waitForMusicCandidateSignal(fixture.entered)
                let requests = await fixture.scripts.requests
                XCTAssertEqual(requests.last?.sourceKey, model.current?.track.sources.first?.sourceKey)
                if action == "pause" {
                    try await waitForMusicCandidateSignal(fixture.start("pause accepted") { await model.togglePlayback() })
                }
                else if action == "revoke" { source.revoke() }
                else { model.releaseRemoteQueueSource(source) }
                await fixture.gate.release()
                try await waitForMusicCandidateSignal(completed)
                XCTAssertEqual(player.resources.count, action == "departure" ? 0 : 1)
                XCTAssertNil(model.activeResourceID); XCTAssertFalse(model.isResolving)
                XCTAssertFalse(model.isChangingQueue); XCTAssertFalse(model.isPreparingRemoteQueue)
                let after = try await service.playedSongs()
                XCTAssertEqual(after.map(\.id), before.map(\.id), "Rejected candidates cannot create playback history")
                if action == "pause" {
                    XCTAssertEqual(model.phase, .paused); XCTAssertEqual(model.queue.count, 2)
                    try await waitForMusicCandidateSignal(fixture.start("explicit resume completed") { await model.togglePlayback() })
                    XCTAssertEqual(player.resources.count, 2, "Explicit resume can use automatic selection")
                }
            }
        }
    }

    @MainActor func testRetiringOldMediaDuringFMAdvanceDoesNotRevokeNewQueueIntent() async throws {
        for lateFact in [false, true] {
            try await advanceAfterOldMediaRevocation(revokeFM: false, lateFact: lateFact)
        }
    }
    @MainActor func testFMSourceRevocationStillStopsAdvanceAfterOldMediaRetirement() async throws {
        for lateFact in [false, true] {
            try await advanceAfterOldMediaRevocation(revokeFM: true, lateFact: lateFact)
        }
    }
    @MainActor private func advanceAfterOldMediaRevocation(revokeFM: Bool, lateFact: Bool) async throws {
        try await withFMFixture { fixture in
            let service = fixture.service, player = fixture.player, model = fixture.model
            let pages = MusicAccountQueuePages(roots: [
                [NativeMusicFixture.track("First")], [NativeMusicFixture.track("Second")]
            ])
            let source = MusicAccountQueueFixture.source(pages, kind: .fm)
            fixture.source = source
            let start = try await MusicAccountQueueFixture.start(service, source)
            try await waitForMusicCandidateSignal(fixture.start("initial FM playback prepared") {
                await model.playRemoteQueue(source, start: start)
            })
            let old = try XCTUnwrap(model.candidate)
            let oldAuthorization = try XCTUnwrap(old.playbackAuthorization)
            try await commitFMPlayingAndWaitForBatch(fixture)
            let before = try await service.playedSongs()
            let revision = model.state.revision
            // next() owns its new intent before draining accepted playback facts.
            // Hold that boundary so the old grant still owns the native player.
            let drain = try fixture.holdPlaybackFactDrain()
            try await waitForMusicCandidateSignal(drain.entered)
            let navigating = XCTestExpectation(description: "FM advance admitted before old media revocation")
            let observation = model.playbackState.$isChangingQueue.filter { $0 }.prefix(1)
                .sink { _ in navigating.fulfill() }
            defer { observation.cancel() }
            let stopped = XCTestExpectation(description: "old media retired")
            player.onStop = { stopped.fulfill() }
            await fixture.scripts.hold(fixture.gate)
            let completed = try fixture.start("FM advance completed") { await model.next() }
            try await waitForMusicCandidateSignal(navigating)
            oldAuthorization.revoke()
            XCTAssertFalse(oldAuthorization.isActive)
            if lateFact {
                // Deliver synchronously before the asynchronous revocation observer.
                model.onPlayback(.init(attemptId: old.attemptId, occurrenceId: old.occurrenceId,
                    resourceId: old.resourceId, kind: .playing, position: 1, duration: 180))
            }
            try await waitForMusicCandidateSignal(stopped)
            player.onStop = nil
            XCTAssertNil(model.activeResourceID)
            XCTAssertEqual(player.stopped.count, 1)
            XCTAssertTrue(model.isChangingQueue, "Old media cannot cancel the new queue transaction")
            await drain.gate.release()
            try await waitForMusicCandidateSignal(fixture.entered)
            try await waitForMusicCandidateSignal(fixture.start("FM advance facts drained") { await model.flushPlaybackFacts() })
            XCTAssertEqual(model.state.revision, revision + 1)
            let requests = await fixture.scripts.requests
            XCTAssertEqual(requests.last?.sourceKey, model.queue.last?.track.sources.first?.sourceKey)
            let history = try await service.playedSongs()
            XCTAssertEqual(history.map(\.id), before.map(\.id), "Revoked late facts must not become valid plays")
            if revokeFM { source.revoke() }
            await fixture.gate.release()
            try await waitForMusicCandidateSignal(completed)
            XCTAssertFalse(model.isChangingQueue)
            if revokeFM {
                XCTAssertEqual(player.resources.count, 1)
                XCTAssertNil(model.activeResourceID)
            } else {
                XCTAssertNil(model.error)
                XCTAssertEqual(model.queue.count, 2)
                XCTAssertEqual(model.current?.id, model.queue[1].id)
                XCTAssertEqual(player.resources.count, 2)
                XCTAssertNotNil(model.activeResourceID)
            }
            let after = try await service.playedSongs(), snapshot = try await service.state()
            XCTAssertEqual(after.map(\.id), before.map(\.id))
            XCTAssertEqual(snapshot.position, 0, "Revoked late facts cannot update playback progress")
            let calls = await pages.events(); XCTAssertEqual(calls, ["root", "root"])
        }
    }

    @MainActor private func withFMFixture(_ operation: @MainActor (MusicCandidateIntegrationFMFixture) async throws -> Void) async throws {
        let scripts = MusicCandidateIntegrationScripts()
        let service = try await NativeMusicFixture.service(scripts: scripts)
        let fixture = MusicCandidateIntegrationFMFixture(service: service, scripts: scripts)
        do {
            try await withTaskCancellationHandler {
                try await waitForMusicCandidateSignal(fixture.start("FM model loaded") { await fixture.model.load() })
                try await operation(fixture)
            } onCancel: {
                Task { @MainActor in fixture.beginClose() }
            }
        } catch {
            await fixture.close()
            throw error
        }
        await fixture.close()
    }

    @MainActor private func commitFMPlayingAndWaitForBatch(_ fixture: MusicCandidateIntegrationFMFixture) async throws {
        let model = fixture.model
        let candidate = try XCTUnwrap(model.candidate)
        XCTAssertEqual(model.queue.count, 1)
        XCTAssertEqual(model.current?.id, model.queue.last?.id, "The playing occurrence must be the FM tail")
        let revision = model.state.revision
        let appended = XCTestExpectation(description: "FM continuation published to the queue")
        let observation = model.playbackState.$queue.filter { $0.count == 2 }.prefix(1)
            .sink { _ in appended.fulfill() }
        defer { observation.cancel() }
        model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 0, duration: 180))
        try await waitForMusicCandidateSignal(fixture.start("initial playing fact committed") { await model.flushPlaybackFacts() })
        try await waitForMusicCandidateSignal(appended)
        let snapshot = try await fixture.service.queue()
        XCTAssertEqual(snapshot.entries.count, 2); XCTAssertEqual(snapshot.revision, revision + 1)
        XCTAssertEqual(model.state.revision, snapshot.revision); XCTAssertEqual(model.queue, snapshot.entries)
        XCTAssertEqual(model.current?.id, candidate.occurrenceId, "Continuation cannot select the appended candidate")
        XCTAssertEqual(fixture.player.resources.count, 1)
        let history = try await fixture.service.playedSongs()
        XCTAssertEqual(history.count, 1)
        XCTAssertEqual(history.first?.track.songId, model.current?.track.songId)
    }

    @MainActor func testRejectingThePlayingSourceExcludesItAndSelectsSameTierReplacement() async throws {
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
        let player = MusicCandidateIntegrationHandoffPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        await model.togglePlayback()
        var calls = await scripts.calls()
        XCTAssertEqual(calls.map(\.sourceKey), [rejected]); XCTAssertEqual(player.resources.count, 1)
        let current = try XCTUnwrap(model.current)
        // Opening inspection is read-only; rejection requests the same-tier replacement.
        let session = try await model.inspectSources(for: current.track.ref)
        let openingCalls = await scripts.calls()
        XCTAssertEqual(openingCalls.count, calls.count)
        let source = MusicResolvedSource(id: "rejected", selection: .init(installationId: MusicNativeCandidateDiscoveryFixture.installation,
            generation: 1, siteId: "wy", sourceKey: rejected, quality: "128k"), title: "Same name", delivery: nil)
        await model.rejectInspectedSource(source, session: session)
        XCTAssertNil(model.error)
        calls = await scripts.calls()
        let replacement = try XCTUnwrap(model.activePlaybackSelection)
        XCTAssertEqual(replacement.installationId, MusicNativeCandidateDiscoveryFixture.installation)
        XCTAssertEqual(replacement.siteId, "wy"); XCTAssertEqual(replacement.sourceKey, kept)
        XCTAssertEqual(replacement.quality, "flac", "Replacement keeps the playing source's quality tier")
        XCTAssertEqual(calls.filter { $0.sourceKey == rejected }.count, 1)
        XCTAssertTrue(calls.dropFirst().allSatisfy { $0.sourceKey == kept })
        XCTAssertEqual(player.resources.count, 2); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertEqual(model.state.revision, 2)
        XCTAssertFalse(try XCTUnwrap(model.current).track.sources.contains { $0.sourceKey == rejected })
        let excluded = try await service.recordingSourceExclusions(song)
        XCTAssertEqual(excluded, [rejected])
        await session.close(); await model.close(); await service.close()
    }

}
#endif

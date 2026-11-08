import Foundation
import XCTest
import AVFoundation
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PlaybackInteractionScripts: MusicScriptResolving {
    var ids: [String]
    var held: (String, NativeMusicGate)?
    var requests: [String] = []
    init(_ ids: [String] = ["a", "b"]) { self.ids = ids }
    func add(_ id: String) { ids.append(id) }
    func hold(_ key: String, gate: NativeMusicGate) { held = (key, gate) }
    func sources() -> [MusicScriptSource] { ids.map(snapshot) }
    func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: ["128k", "flac"])])
    }
    func source(installationID: String) -> MusicScriptSource { snapshot(installationID) }
    func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        let executed = snapshot(request.installationID)
        let key = request.installationID + ":" + request.quality
        requests.append(key)
        if let (match, gate) = held, key == match { held = nil; await gate.wait() }
        try Task.checkCancellation()
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)), source: executed)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> [String] { requests }
}

@MainActor private class PlaybackInteractionPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var resumes = 0, pauses = 0
    var gate: NativeMusicGate?
    var onPause: (() -> Void)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource)
        return .init(id: UUID(), resourceID: resource.id)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        if let gate { self.gate = nil; await gate.wait(); try Task.checkCancellation() }
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) {}
    func pause(_ attempt: MusicPlayerAttempt) { pauses += 1; onPause?() }
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicPlaybackInteractionTests: XCTestCase {
    private func setup(_ scripts: PlaybackInteractionScripts, player: PlaybackInteractionPlayer) async throws -> (NativeMusicService, MusicViewModel) {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let initial = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        let model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        emit(model, .playing); await model.flushPlaybackFacts()
        return (service, model)
    }
    private func emit(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind) {
        guard let c = model.candidate else { XCTFail("Missing candidate"); return }
        model.onPlayback(.init(attemptId: c.attemptId, occurrenceId: c.occurrenceId,
                               resourceId: c.resourceId, kind: kind, position: 23, duration: 180))
    }
    func testPendingManualQualitySurvivesDonorFailure() async throws {
        let scripts = PlaybackInteractionScripts(), player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(scripts, player: player)
        let gate = NativeMusicGate(); player.gate = gate
        let choice = Task { await model.switchQuality("flac") }
        await gate.started()
        emit(model, .failed)
        // Failure is delivered while the explicit flac request owns a prepared
        // candidate but has not committed. No timing sleep determines intent.
        await gate.release(); await choice.value; await model.flushPlaybackFacts()
        print("REGRESSION E1 quality=\(model.lastSelection?.quality ?? "nil") intent=\(model.sourceIntent) opened=\(player.resources.count) error=\(model.error ?? "nil")")
        XCTAssertEqual(model.lastSelection?.quality, "flac", "Latest explicit quality must survive the old stream's failure")
        await model.close(); await service.close()
    }
    func testManualQualityWithoutDonorFailureControl() async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(), player: player)
        await model.switchQuality("flac")
        XCTAssertEqual(model.lastSelection?.quality, "flac")
        await model.close(); await service.close()
    }
    func testPlayCannotResumeConfirmedWrongSource() async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(["a"]), player: player)
        player.onPause = { [weak model] in if let model { self.emit(model, .paused) } }
        await model.rejectCurrentSource(); await model.flushPlaybackFacts()
        let excluded = try await service.sourceExclusions()
        XCTAssertEqual(excluded.count, 1); XCTAssertEqual(model.phase, .paused)
        let old = model.activeResourceID
        await model.togglePlayback()
        print("REGRESSION E2 excluded=\(excluded.count) resumes=\(player.resumes) oldRetained=\(old == model.activeResourceID) occurrence=\(model.current?.id ?? "nil")")
        XCTAssertEqual(player.resumes, 0, "Normal play cannot bypass the durable wrong-source exclusion")
        XCTAssertEqual(model.current?.id, "entry-1")
        XCTAssertNil(model.activeResourceID)
        try await service.restoreSourceExclusion(id: excluded[0].id)
        await model.togglePlayback()
        XCTAssertNotNil(model.activeResourceID, "Explicit restoration permits a fresh match")
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(player.resumes, 0, "Restoration opens fresh media, never revives the rejected attempt")
        await model.close(); await service.close()
    }
    func testSettingsExclusionIsCheckedOnResume() async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(["a"]), player: player)
        emit(model, .paused); await model.flushPlaybackFacts()
        try await service.excludeSource(for: model.current!.track.ref, provider: "a", platform: "tx")
        await model.togglePlayback()
        XCTAssertEqual(player.resumes, 0)
        XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(model.phase, .paused)
        await model.close(); await service.close()
    }
    func testWrongSourceReplacementRetainsPauseAndPosition() async throws {
        try await wrongSourceReplacement(paused: true)
    }
    func testWrongSourceReplacementResumesWhenOriginallyPlaying() async throws {
        try await wrongSourceReplacement(paused: false)
    }
    private func wrongSourceReplacement(paused: Bool) async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(), player: player)
        let old = try XCTUnwrap(model.activePlaybackSelection)
        if paused { emit(model, .paused); await model.flushPlaybackFacts() }
        player.onPause = { [weak model] in if let model { self.emit(model, .paused) } }
        await model.rejectCurrentSource()
        XCTAssertNotEqual(model.activePlaybackSelection?.installationId, old.installationId)
        XCTAssertEqual(model.activePlaybackSelection?.quality, old.quality)
        XCTAssertEqual(model.candidate?.startPosition, 23)
        XCTAssertEqual(player.resumes, paused ? 0 : 1)
        XCTAssertEqual(model.current?.id, "entry-1")
        await model.close(); await service.close()
    }
    func testPauseWhileWrongSourceReplacementIsResolvingCancelsIt() async throws {
        let scripts = PlaybackInteractionScripts(["a"]), player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(scripts, player: player)
        let gate = NativeMusicGate()
        // B is newly installed, so there is no earlier B URL to bypass the gate.
        await scripts.add("b"); await service.sourceConfigurationChanged()
        await scripts.hold("b:128k", gate: gate)
        let task = Task { await model.rejectCurrentSource() }
        await gate.started()
        await model.togglePlayback()
        await gate.release(); await task.value
        XCTAssertNil(model.activeResourceID); XCTAssertEqual(model.phase, .paused)
        XCTAssertEqual(player.resumes, 0); XCTAssertEqual(player.resources.count, 1)
        await model.close(); await service.close()
    }
    func testPendingExactResultSurvivesDonorFailure() async throws {
        let scripts = PlaybackInteractionScripts(), player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(scripts, player: player)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: model.state.revision)
        _ = try await session.loadMore(refresh: false)
        let view = try await settled(session)
        let row = try XCTUnwrap(view.sources.first { $0.selection.installationId == "b" && $0.selection.quality == "flac" })
        let gate = NativeMusicGate(); player.gate = gate
        let task = Task { await model.chooseInspectedSource(row, session: session) }
        await gate.started(); emit(model, .failed)
        await gate.release(); await task.value; await model.flushPlaybackFacts()
        XCTAssertEqual(model.activePlaybackSelection, row.selection)
        await model.close(); await service.close()
    }
    func testPendingQualitySupersedesOfficialURLRenewal() async throws {
        let fixture = try await MusicRecoveryTestFixture.make(), player = PlaybackInteractionPlayer()
        // A distinct healthy flac item must not reuse the failed 128k URL.
        // The common recovery fixture otherwise returns one URL for all tiers.
        await fixture.accounts.setResolveURL(URL(string: "https://media.invalid/healthy.flac")!, quality: "flac")
        let initial = try await fixture.service.state()
        _ = try await fixture.service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        let model = MusicViewModel(service: fixture.service, player: player)
        await model.load(); await model.prepareCurrent(); emit(model, .playing); await model.flushPlaybackFacts()
        let gate = NativeMusicGate(); player.gate = gate
        let task = Task { await model.switchQuality("flac") }
        await gate.started(); emit(model, .failed)
        await gate.release(); await task.value; await model.flushPlaybackFacts()
        let counts = await fixture.accounts.counts()
        XCTAssertEqual(counts.refreshes, 0, "Do not renew the obsolete official 128k intent")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        await model.close(); await fixture.service.close()
    }
    func testNewerSelectionWinsOverPendingDonorRecovery() async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(), player: player)
        let gate = NativeMusicGate(); player.gate = gate
        let task = Task { await model.switchQuality("flac") }
        await gate.started(); emit(model, .failed)
        let latest = Task { await model.switchQuality("128k") }
        await gate.release(); await latest.value; await task.value; await model.flushPlaybackFacts()
        XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
        XCTAssertNil(model.error)
        await model.close(); await service.close()
    }
    func testCancellingPendingChoiceAlsoCancelsDonorRecovery() async throws {
        let player = PlaybackInteractionPlayer()
        let (service, model) = try await setup(PlaybackInteractionScripts(), player: player)
        let gate = NativeMusicGate(); player.gate = gate
        let task = Task { await model.switchQuality("flac") }
        await gate.started(); emit(model, .failed)
        task.cancel()
        await gate.release(); await task.value; await model.flushPlaybackFacts()
        XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertEqual(model.position, 23)
        XCTAssertEqual(player.resources.count, 1, "A cancelled intent cannot open replacement media through recovery")
        XCTAssertNil(model.error)
        await model.close(); await service.close()
    }
    private func settled(_ session: any MusicSourcePlaybackSession) async throws -> MusicResolvedSourcesSnapshot {
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
    }
    func testOverlappingPagesKeepRemainingPeerReachable() async throws {
        let scripts = PlaybackInteractionScripts(), gate = NativeMusicGate()
        await scripts.hold("a:flac", gate: gate)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.loadMore(refresh: false)
        await gate.started()
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates()
                where value.sources.contains(where: { $0.selection.installationId == "b" }) { return }
        }
        // Scheduling slice for the fast provider's return; a/flac is still
        // explicitly gated, so timing cannot complete the first page.
        try await Task.sleep(for: .milliseconds(50))
        _ = try await session.loadMore(refresh: false)
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates()
                where value.sources.contains(where: { $0.selection.quality == "128k" }) { return }
        }
        await gate.release()
        let view = try await settled(session), calls = await scripts.calls()
        print("REGRESSION E3 hasMore=\(view.hasMore) calls=\(calls.sorted())")
        XCTAssertTrue(view.hasMore || calls.contains("a:128k"), "The UI must still expose the untried a/128k result")
        _ = try await session.loadMore(refresh: false)
        let finished = try await settled(session), all = await scripts.calls()
        XCTAssertEqual(Set(all), ["a:flac", "b:flac", "a:128k", "b:128k"])
        XCTAssertEqual(all.count, 4, "Do not re-request the completed peers")
        XCTAssertFalse(finished.hasMore)
        await service.close()
    }
    func testSequentialPagesControl() async throws {
        let scripts = PlaybackInteractionScripts()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.loadMore(refresh: false); _ = try await settled(session)
        _ = try await session.loadMore(refresh: false); _ = try await settled(session)
        let calls = await scripts.calls()
        XCTAssertEqual(Set(calls), ["a:flac", "b:flac", "a:128k", "b:128k"])
        await service.close()
    }
    func testCancelledPageLeavesUnfinishedCandidateReachable() async throws {
        let scripts = PlaybackInteractionScripts(["a"]), gate = NativeMusicGate()
        await scripts.hold("a:flac", gate: gate)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.loadMore(refresh: false); await gate.started()
        await session.cancelInspection(); await gate.release()
        let cancelled = try await settled(session)
        XCTAssertTrue(cancelled.hasMore)
        _ = try await session.loadMore(refresh: false)
        let next = try await settled(session)
        XCTAssertTrue(next.sources.contains { $0.selection.quality == "flac" })
        _ = try await session.loadMore(refresh: false)
        let finished = try await settled(session)
        XCTAssertFalse(finished.hasMore)
        await service.close()
    }
}

@MainActor private final class NativeHandoffPlayer: MusicPlaybackHandoffDriving {
    let controller = PlaybackController()
    let file: URL
    var onPrepared: (() -> Void)?
    var synchronizationGate: NativeMusicGate?
    var seekAfterSynchronization = false
    var commitFailure: Error?
    init(file: URL) { self.file = file }
    private func local(_ resource: PlaybackResource) throws -> PlaybackResource {
        try .init(id: resource.id, url: file, title: resource.title)
    }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let native = controller.playAttempt(resource: try! local(resource), owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: native)
        return .init(id: native.id, resourceID: native.resourceID)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        let native = PlayAttempt(id: replacing.id, owner: "music", resourceID: replacing.resourceID)
        let prepared = try await controller.prepareReplacement(resource: local(resource), replacing: native)
        onPrepared?()
        return .init(duration: prepared.duration, synchronize: {
            if let gate = self.synchronizationGate {
                self.synchronizationGate = nil
                await gate.wait()
                try Task.checkCancellation()
            }
            try await self.controller.synchronizeReplacement(prepared)
            if self.seekAfterSynchronization, let attempt = self.controller.currentAttempt {
                self.seekAfterSynchronization = false
                await withCheckedContinuation { continuation in
                    _ = self.controller.seek(to: 60, attempt: attempt) { _ in continuation.resume() }
                }
            }
        }, commit: {
            do {
                let next = try self.controller.commitReplacement(prepared)
                self.commitFailure = nil
                return .init(id: next.id, resourceID: next.resourceID)
            } catch { self.commitFailure = error; throw error }
        }, cancel: { prepared.cancel() })
    }
    func stop(_ attempt: MusicPlayerAttempt) { controller.stop(owner: "music") }
    func pause(_ attempt: MusicPlayerAttempt) { controller.pause() }
    func resume(_ attempt: MusicPlayerAttempt) { controller.resume() }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { controller.seek(to: position); return true }
}

@MainActor final class MusicNativeHandoffInteractionTests: XCTestCase {
    func testSeekDuringAsyncAdmissionDoesNotCancelQualitySwitch() async throws {
        try await change(seekDuringWait: true)
    }
    func testNativeSwitchWithoutInterveningSeekControl() async throws {
        try await change(seekDuringWait: false)
    }
    func testCancellationDuringFinalSynchronizationKeepsPausedDonor() async throws {
        try await change(seekDuringWait: true, cancelSynchronization: true)
    }
    func testAdvancingDonorDuringAsyncAdmissionIsRealigned() async throws {
        try await change(seekDuringWait: false, advanceDuringWait: true)
    }
    func testPositionChangeAfterAlignmentIsRetriedWithoutLosingDonor() async throws {
        try await change(seekDuringWait: false, seekAfterSynchronization: true)
    }
    private func change(seekDuringWait: Bool, cancelSynchronization: Bool = false,
                        advanceDuringWait: Bool = false, seekAfterSynchronization: Bool = false) async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("meow-handoff-silence.wav")
        // 180 seconds of 8kHz mono PCM silence, no upstream or audible content.
        var wave = Data()
        func text(_ value: String) { wave.append(contentsOf: value.utf8) }
        func integer<T: FixedWidthInteger>(_ value: T) { var le = value.littleEndian; withUnsafeBytes(of: &le) { wave.append(contentsOf: $0) } }
        let length: UInt32 = 180 * 8000 * 2
        text("RIFF"); integer(UInt32(36) + length); text("WAVEfmt "); integer(UInt32(16))
        integer(UInt16(1)); integer(UInt16(1)); integer(UInt32(8000)); integer(UInt32(16000))
        integer(UInt16(2)); integer(UInt16(16)); text("data"); integer(length)
        wave.append(Data(count: Int(length))); try wave.write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: PlaybackInteractionScripts())
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, requestQuality: "128k"))
        let player = NativeHandoffPlayer(file: file), model = MusicViewModel(service: service, player: player)
        player.seekAfterSynchronization = seekAfterSynchronization
        let ready = expectation(description: "Initial local item ready")
        var first = true
        let lease = try player.controller.bind(owner: "music") { event in
            model.onPlaybackEvent(event)
            if event.kind == .prepared, first { first = false; ready.fulfill() }
        }
        defer { lease.cancel(); player.controller.stop() }
        await model.load(); await model.prepareCurrent()
        await fulfillment(of: [ready], timeout: 5)
        let original = try XCTUnwrap(player.controller.currentAttempt)
        _ = player.controller.setPreferredRate(1.5, attempt: original)
        _ = player.controller.setVolume(0.37, attempt: original)
        let prepared = expectation(description: "Replacement ready"), gate = NativeMusicGate()
        player.onPrepared = {
            model.eventTail = Task { await gate.wait() }
            prepared.fulfill()
        }
        let task = Task { await model.switchQuality("flac") }
        await fulfillment(of: [prepared], timeout: 5)
        let cancellation = NativeMusicGate()
        if cancelSynchronization { player.synchronizationGate = cancellation }
        if seekDuringWait {
            let seek = expectation(description: "Donor moved to 50 seconds")
            let donor = try XCTUnwrap(player.controller.currentAttempt)
            _ = player.controller.seek(to: 50, attempt: donor) { _ in seek.fulfill() }
            await fulfillment(of: [seek], timeout: 5)
        }
        if advanceDuringWait {
            let advanced = expectation(description: "Donor really advanced beyond the synchronization tolerance")
            let observation = player.controller.player.addBoundaryTimeObserver(
                forTimes: [NSValue(time: CMTime(seconds: 0.5, preferredTimescale: 600))], queue: .main) {
                    advanced.fulfill()
                }
            _ = player.controller.resume(attempt: original)
            await fulfillment(of: [advanced], timeout: 5)
            player.controller.player.removeTimeObserver(observation)
            _ = player.controller.pause(attempt: original)
            XCTAssertGreaterThan(player.controller.player.currentTime().seconds, 0.25)
        }
        let position = player.controller.player.currentTime().seconds
        await gate.release()
        if cancelSynchronization { await cancellation.started(); task.cancel(); await cancellation.release() }
        await task.value; await model.flushPlaybackFacts()
        print("REGRESSION E4 seek=\(seekDuringWait) quality=\(model.lastSelection?.quality ?? "nil") position=\(player.controller.player.currentTime().seconds) nativeError=\(String(describing: player.commitFailure)) error=\(model.error ?? "nil")")
        if cancelSynchronization {
            XCTAssertEqual(player.controller.currentAttempt, original)
            XCTAssertEqual(model.lastSelection?.quality, "128k")
        } else {
            XCTAssertEqual(model.lastSelection?.quality, "flac", "An intervening user seek must be synchronized, not abort the switch")
            XCTAssertNotEqual(player.controller.currentAttempt, original)
        }
        XCTAssertEqual(player.controller.player.currentTime().seconds, seekAfterSynchronization ? 60 : position, accuracy: 0.25)
        XCTAssertEqual(player.controller.preferredRate, 1.5)
        XCTAssertEqual(player.controller.volume, 0.37)
        XCTAssertEqual(player.controller.snapshot.phase, .paused)
        XCTAssertNil(player.commitFailure)
        await model.close(); await service.close()
    }
}

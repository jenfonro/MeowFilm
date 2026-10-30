#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor private final class MusicFMPlayer: MusicPlaybackDriving {
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

final class MusicFMConsumerTests: XCTestCase {
    @MainActor private func setup(scripts: NativeMusicScriptFixture = .init()) async throws -> (NativeMusicService, MusicFMPlayer, MusicViewModel) {
        let service = try await NativeMusicFixture.service(scripts: scripts)
        // This suite fixes a single quality to isolate FM/pause/queue ownership;
        // candidate-policy tests independently cover quality preference ordering.
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["standard"]))
        let player = MusicFMPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        return (service, player, model)
    }
    @MainActor private func fact(_ model: MusicViewModel, kind: MusicPlaybackFact.Kind) throws -> MusicPlaybackFact {
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        return .init(attemptId: String(resource.dropFirst("music:".count)), occurrenceId: occurrence,
            resourceId: resource, kind: kind, position: kind == .ended ? 180 : 0, duration: 180)
    }
    @MainActor private func begin(_ model: MusicViewModel, service: NativeMusicService,
                                  source: MusicRemoteQueueSource) async throws {
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        let start = try await MusicAccountQueueFixture.start(service, source)
        await model.playRemoteQueue(source, start: start)
        XCTAssertFalse(model.isChangingQueue); XCTAssertFalse(model.isPreparingRemoteQueue)
        XCTAssertNotNil(model.activeResourceID)
        XCTAssertTrue(requests.isEmpty)
    }

    @MainActor func testNaturalEndAndManualNextUseLiveFMWhilePageDepartureTransfersOwnership() async throws {
        let (service, player, model) = try await setup()
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")],
            [NativeMusicFixture.track("B")], [NativeMusicFixture.track("C")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        try await begin(model, service: service, source: source)
        XCTAssertEqual(player.resources.count, 1); XCTAssertTrue(model.isFMActive)
        model.releaseRemoteQueueSource(source)
        XCTAssertTrue(source.authorization.isActive)
        model.onPlayback(try fact(model, kind: .playing)); await model.flushPlaybackFacts()
        let oldEnd = try fact(model, kind: .ended)
        model.onPlayback(oldEnd); await model.flushPlaybackFacts(); await model.flushNavigation()
        XCTAssertEqual(model.queue.count, 2); XCTAssertEqual(model.current?.id, model.queue[1].id)
        XCTAssertEqual(player.resources.count, 2)
        model.onPlayback(oldEnd); await model.flushPlaybackFacts(); await model.flushNavigation()
        let afterDuplicate = await loader.events(); XCTAssertEqual(afterDuplicate, ["root", "root"])
        await model.next()
        XCTAssertEqual(model.queue.count, 3); XCTAssertEqual(player.resources.count, 3)
        await model.close()
        XCTAssertFalse(source.authorization.isActive)
    }

    @MainActor func testPauseDuringBoundaryFillCommitsBatchButDoesNotAutoSoundOrChangeSource() async throws {
        let (service, player, model) = try await setup()
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        try await begin(model, service: service, source: source)
        let gate = NativeMusicGate(); await loader.hold("root", gate: gate)
        let task = Task { await model.next() }
        await gate.started()
        await model.togglePlayback()
        await gate.release(); await task.value
        XCTAssertEqual(model.queue.count, 2); XCTAssertEqual(model.current?.id, model.queue[1].id)
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(model.phase, .paused)
        await model.togglePlayback()
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(model.requestedQuality, "128k")
        await model.close()
    }

    @MainActor func testPageDepartureDuringFullLoadPreservesOldPlayerAndQueue() async throws {
        let (service, player, model) = try await setup()
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        _ = try await MusicAccountQueueFixture.seed(service); await model.load()
        await model.prepareCurrent()
        XCTAssertNotNil(model.activeResourceID)
        XCTAssertTrue(requests.isEmpty)
        let oldQueue = model.queue, oldCurrent = model.state.currentId
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
            pages: ["p1": .init(items: [NativeMusicFixture.track("Second")])], rootCursor: "p1")
        let source = MusicAccountQueueFixture.source(loader), gate = NativeMusicGate()
        let start = try await MusicAccountQueueFixture.start(service, source)
        await loader.hold("p1", gate: gate)
        let task = Task { await model.playRemoteQueue(source, start: start) }
        await gate.started(); model.releaseRemoteQueueSource(source)
        await gate.release(); await task.value
        XCTAssertEqual(model.queue, oldQueue); XCTAssertEqual(model.state.currentId, oldCurrent)
        XCTAssertEqual(player.resources.count, 1); XCTAssertTrue(player.stopped.isEmpty)
        let current = try await service.queue(); XCTAssertEqual(current.currentId, oldCurrent)
        await model.close()
    }

    @MainActor func testFMFailureDoesNotWrapRetryOrPlayAndExplicitRetryCanContinue() async throws {
        let (service, player, model) = try await setup()
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]])
        let source = MusicAccountQueueFixture.source(loader, kind: .fm)
        try await begin(model, service: service, source: source)
        let before = model.queue, current = model.state.currentId
        await loader.fail("root"); await model.next()
        XCTAssertEqual(model.queue, before); XCTAssertEqual(model.state.currentId, current)
        XCTAssertEqual(player.resources.count, 1); XCTAssertNotNil(model.error)
        let calls = await loader.events(); XCTAssertEqual(calls, ["root", "root"])
        await loader.fail(nil); await model.next()
        XCTAssertEqual(model.queue.count, 2); XCTAssertEqual(player.resources.count, 2)
        await model.close()
    }

    @MainActor func testPageDepartureAfterQueueCommitBeforeLXReplyCannotSoundLate() async throws {
        let scripts = NativeMusicScriptFixture()
        let (service, player, model) = try await setup(scripts: scripts)
        var requests: [MusicPlaybackDestination] = []
        model.onRequestPresentation = { requests.append($0) }
        _ = try await MusicAccountQueueFixture.seed(service); await model.load()
        await model.prepareCurrent()
        XCTAssertNotNil(model.activeResourceID)
        XCTAssertTrue(requests.isEmpty)
        let source = MusicAccountQueueFixture.source(.init(roots: [[NativeMusicFixture.track("New")]]))
        let start = try await MusicAccountQueueFixture.start(service, source)
        let gate = NativeMusicGate(); await scripts.configure(gate: gate)
        let task = Task { await model.playRemoteQueue(source, start: start) }
        await gate.started()
        // Commit already succeeded; leaving cannot claim to roll it back, but
        // neither may its non-cooperative resolution start a new player.
        XCTAssertEqual(model.queue.count, 1)
        model.releaseRemoteQueueSource(source)
        await gate.release(); await task.value
        XCTAssertEqual(player.resources.count, 1); XCTAssertFalse(source.authorization.isActive)
        XCTAssertFalse(model.isChangingQueue); XCTAssertFalse(model.isPreparingRemoteQueue)
        XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    @MainActor func testPauseAfterFMAppendDuringResolutionStillPreventsLateAutoSound() async throws {
        let scripts = NativeMusicScriptFixture()
        let (service, player, model) = try await setup(scripts: scripts)
        let source = MusicAccountQueueFixture.source(.init(roots: [
            [NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]
        ]), kind: .fm)
        try await begin(model, service: service, source: source)
        let gate = NativeMusicGate(); await scripts.configure(gate: gate)
        let task = Task { await model.next() }
        await gate.started()
        XCTAssertEqual(model.queue.count, 2)
        await model.togglePlayback()
        await gate.release(); await task.value
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(model.phase, .paused)
        XCTAssertNil(model.activeResourceID)
        await model.togglePlayback()
        XCTAssertEqual(player.resources.count, 2); XCTAssertEqual(model.requestedQuality, "128k")
        await model.close()
    }

    @MainActor func testAccountRevocationAfterFMCommitRejectsLateLXAndNavigation() async throws {
        let scripts = NativeMusicScriptFixture()
        let (service, player, model) = try await setup(scripts: scripts)
        let source = MusicAccountQueueFixture.source(.init(roots: [
            [NativeMusicFixture.track("A")], [NativeMusicFixture.track("B")]
        ]), kind: .fm)
        try await begin(model, service: service, source: source)
        let gate = NativeMusicGate(); await scripts.configure(gate: gate)
        let task = Task { await model.next() }
        await gate.started()
        source.revoke()
        XCTAssertFalse(model.canNext); XCTAssertFalse(model.canPrevious)
        await gate.release(); await task.value
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertNil(model.activeResourceID)
        await model.close()
    }
}
#endif

#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
@testable import MeowPlayback

/// Controlled OS-boundary tests, NOT proof of iPhone PiP continuity. Real
/// decoder/output commits precede the transfer just as in the public play path.
@MainActor
final class PlaybackVideoPictureInPictureTests: XCTestCase {
    private func input(_ id: String) throws -> PlaybackVideoInput {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
            .appendingPathComponent("ordinary.mp4")
        return try PlaybackVideoInput(resourceID: id, url: url, title: "synthetic")
    }
    private func first(_ controller: PlaybackController, lease: PlaybackVideoLease, window: PlaybackVideoTestWindow,
                       pip: ControlledPictureInPicture) async throws -> (PlaybackVideoTransport, PlayAttempt) {
        let input = try input("first")
        let media = try PlaybackVideoMediaLease(attemptID: "first-node", input: input) {}
        let transport = try PlaybackVideoTransport(media: media, input: XCTUnwrap(media.takeInput()))
        addTeardownBlock { try await transport.close() }
        try await transport.prepare()
        window.stage(transport)
        try await transport.renderFirstVideoFrame()
        let admission = controller.admitVideo(owner: lease.owner, candidate: transport)
        let attempt = try controller.commitVideo(transport, lease: lease, admission: admission, paused: true)
        transport.adoptPictureInPicture(pip)
        transport.installPictureInPicture(lease: lease, controller: controller, attempt: attempt)
        return (transport, attempt)
    }

    func testSameLeaseMovesSameControllerAndOldRetirementCannotStopIt() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let window = try PlaybackVideoTestWindow()
        let film = try bindWindowedVideo(controller, owner: "film", window: window) { events.append($0) }
        let pip = ControlledPictureInPicture(active: true)
        let (_, old) = try await first(controller, lease: film, window: window, pip: pip)
        let oldSource = try XCTUnwrap(pip.sources.last)
        let media = try PlaybackVideoMediaLease(attemptID: "second-node", input: input("second")) {}
        let current = try await film.play(media, paused: true)
        try await controller.drainVideo()
        XCTAssertEqual(pip.bindings, [old, current], "The same injected controller is rebound, not restarted")
        XCTAssertEqual(pip.stops, 0); XCTAssertEqual(pip.closes, 0)
        XCTAssertTrue(film.canContinueInBackground)
        XCTAssertEqual(events.last { $0.kind == .started }?.pictureInPicture, .active)
        XCTAssertFalse(events.contains { $0.kind == .systemOwnerEnded })
        XCTAssertFalse(controller.wantsPlayback)
        oldSource.setPlaying(true)
        XCTAssertFalse(controller.wantsPlayback, "Late old ContentSource controls must not resume the new attempt")
        var completed = 0
        oldSource.skip(by: 1) { completed += 1 }
        XCTAssertEqual(completed, 1)
        XCTAssertTrue(pip.sources.last?.isPaused == true)

        try await film.close()
        XCTAssertGreaterThan(pip.stops, 0); XCTAssertEqual(pip.closes, 1)
    }

    func testLossOnRebindDoesNotInheritActiveAndLaterLossNamesNewAttempt() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let window = try PlaybackVideoTestWindow()
        let film = try bindWindowedVideo(controller, owner: "film", window: window) { events.append($0) }
        let pip = ControlledPictureInPicture(active: true)
        let (oldTransport, old) = try await first(controller, lease: film, window: window, pip: pip)
        pip.loseOnRebind = true
        let media = try PlaybackVideoMediaLease(attemptID: "second-node", input: input("second")) {}
        let current = try await film.play(media, paused: true)
        XCTAssertEqual(pip.bindings, [old, current])
        XCTAssertFalse(film.canContinueInBackground)
        XCTAssertNotEqual(events.last { $0.kind == .started }?.pictureInPicture, .active)
        pip.reportOwnerEnded()
        let ended = try XCTUnwrap(events.last { $0.kind == .systemOwnerEnded })
        XCTAssertEqual(ended.attemptID, "second-node"); XCTAssertEqual(ended.nativeAttempt, current)
        let count = events.count
        film.updatePictureInPicture(.active, transport: oldTransport, attempt: old)
        XCTAssertEqual(events.count, count, "Late old lifecycle observations cannot restore background eligibility")
        XCTAssertFalse(film.canContinueInBackground)
        try await film.close()
    }

    func testPendingStartIsNotTransferredOrBackgroundPermission() async throws {
        let controller = PlaybackController()
        let window = try PlaybackVideoTestWindow()
        let film = try bindWindowedVideo(controller, owner: "film", window: window) { _ in }
        let pip = ControlledPictureInPicture(active: false)
        pip.pendingStart = true
        let (_, old) = try await first(controller, lease: film, window: window, pip: pip)
        XCTAssertFalse(film.canContinueInBackground)
        let media = try PlaybackVideoMediaLease(attemptID: "second-node", input: input("second")) {}
        _ = try await film.play(media, paused: true)
        try await controller.drainVideo()
        XCTAssertEqual(pip.bindings, [old])
        XCTAssertGreaterThan(pip.stops, 0); XCTAssertEqual(pip.closes, 1)
        XCTAssertFalse(film.canContinueInBackground)
        try await film.close()
    }

    func testOtherLeaseAndMusicNeverInheritSystemController() async throws {
        for music in [false, true] {
            let controller = PlaybackController()
            var events: [PlaybackVideoEvent] = []
            let window = try PlaybackVideoTestWindow()
            let film = try bindWindowedVideo(controller, owner: "film", window: window) { events.append($0) }
            let pip = ControlledPictureInPicture(active: true)
            let (_, old) = try await first(controller, lease: film, window: window, pip: pip)
            let other = try bindWindowedVideo(controller, owner: "other") { _ in }
            if music {
                let value = try input("music")
                _ = controller.playAttempt(resource: try .init(id: "music", url: value.url, title: "synthetic"),
                                           owner: "music", autoplay: false)
            } else {
                let media = try PlaybackVideoMediaLease(attemptID: "other-node", input: input("other")) {}
                _ = try await other.play(media, paused: true)
            }
            try await controller.drainVideo()
            XCTAssertEqual(pip.bindings, [old])
            XCTAssertGreaterThan(pip.stops, 0); XCTAssertEqual(pip.closes, 1)
            XCTAssertFalse(film.canContinueInBackground)
            XCTAssertFalse(other.canContinueInBackground)
            XCTAssertEqual(events.filter { $0.kind == .systemOwnerEnded }.map(\.nativeAttempt), [old])
            try await other.close(); try await film.close(); controller.stop()
        }
    }

    func testFailedCandidateLeavesDonorSystemControllerUntouched() async throws {
        let controller = PlaybackController()
        let window = try PlaybackVideoTestWindow()
        let film = try bindWindowedVideo(controller, owner: "film", window: window) { _ in }
        let pip = ControlledPictureInPicture(active: true)
        let (_, old) = try await first(controller, lease: film, window: window, pip: pip)
        let missing = try PlaybackVideoInput(resourceID: "missing",
            url: URL(fileURLWithPath: "/missing/synthetic-video.mp4"), title: "synthetic")
        let media = try PlaybackVideoMediaLease(attemptID: "failed", input: missing) {}
        do { _ = try await film.play(media); XCTFail("A failed candidate cannot take PiP") } catch {}
        XCTAssertEqual(controller.currentAttempt, old)
        XCTAssertEqual(pip.bindings, [old]); XCTAssertEqual(pip.stops, 0); XCTAssertEqual(pip.closes, 0)
        XCTAssertTrue(film.canContinueInBackground)
        try await film.close()
    }

    func testCommittedFailureKeepsTerminalEventAfterFinalProgressAndEndsExactOwner() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let window = try PlaybackVideoTestWindow()
        let film = try bindWindowedVideo(controller, owner: "film", window: window) { events.append($0) }
        let pip = ControlledPictureInPicture(active: true)
        let (transport, attempt) = try await first(controller, lease: film, window: window, pip: pip)
        XCTAssertTrue(controller.resume(attempt: attempt))
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while transport.position <= controller.snapshot.position, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertGreaterThan(transport.position, controller.snapshot.position)
        events.removeAll()
        transport.kernel.onFailure?(.decodeFailed)
        XCTAssertEqual(events.filter { $0.kind == .failed }.map(\.nativeAttempt), [attempt])
        XCTAssertEqual(events.filter { $0.kind == .systemOwnerEnded }.map(\.nativeAttempt), [attempt])
        XCTAssertEqual(events.last?.kind, .systemOwnerEnded)
        XCTAssertFalse(film.canContinueInBackground)
        try await film.close()
    }
}

@MainActor
private final class ControlledPictureInPicture: PlaybackVideoPictureInPictureControlling {
    var isActive: Bool
    var pendingStart = false
    var loseOnRebind = false
    var hasSystemOwner: Bool { isActive }
    var state: PlaybackVideoPictureInPictureState { isActive ? .active : pendingStart ? .starting : .possible }
    private(set) var bindings: [PlayAttempt] = []
    private(set) var sources: [PlaybackVideoPictureInPictureSource] = []
    private(set) var stops = 0
    private(set) var closes = 0
    init(active: Bool) { isActive = active }
    func rebind(transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
                controller: PlaybackController, attempt: PlayAttempt) {
        bindings.append(attempt)
        sources.append(.init(transport: transport, lease: lease, playback: controller, attempt: attempt))
        if loseOnRebind { isActive = false }
    }
    func reportOwnerEnded() {
        guard let source = sources.last, let transport = source.transport else { return }
        isActive = false
        source.lease?.updatePictureInPicture(.possible, transport: transport, attempt: source.attempt,
                                             systemOwnerEnded: true)
    }
    func start() -> Bool { pendingStart = true; return true }
    func stop() { stops += 1; isActive = false; pendingStart = false }
    func invalidateState() {}
    func close() async { closes += 1; isActive = false; pendingStart = false }
}
#endif

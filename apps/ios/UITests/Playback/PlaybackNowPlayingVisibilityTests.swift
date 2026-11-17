#if os(iOS)
import XCTest
import AVFoundation
import MediaPlayer
import UIKit
import MeowContracts
@testable import MeowPlayback

/// Uses the real iOS metadata/command adapter with controlled audio admission.
/// This does not assert lock-screen rendering or real decoder output.
@MainActor
final class PlaybackNowPlayingVisibilityTests: XCTestCase {
    func testHidingCardRetainsCommandTargetsAndActiveAudioAdmission() async {
        let targets = VisibilityCommandTargets()
        let session = PlaybackSystemSession(audioActions: .init(activate: {}, deactivate: {}),
            onEvent: { _, _ in }, onCommand: { _, _, _ in true },
            onNavigation: { _ in true }, notificationCenter: NotificationCenter(),
            registerCommandTarget: targets.register)
        let attempt = PlayAttempt(id: UUID(), owner: "film", resourceID: "episode")
        let route = PlaybackPlayPauseRoute(attempt: attempt, bindingID: UUID(), revision: UUID())
        let navigation = PlaybackNavigationRoutes(
            next: .init(attempt: attempt, bindingID: UUID(), revision: UUID(), direction: .next))
        let snapshot = PlaybackSnapshot(owner: "film", resourceID: "episode", phase: .playing,
                                        position: 12, duration: 120)
        let metadata = PlaybackNowPlayingMetadata(title: "Episode", artist: "Series")
        session.beginSession(generation: attempt.id)
        let failure = await session.activate().value
        XCTAssertNil(failure)
        session.update(snapshot: snapshot, attempt: attempt, metadata: metadata, rate: 2, canSeek: true,
                       navigation: navigation, playPause: route)
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "Episode")
        let retained = targets.activeIDs
        session.update(snapshot: snapshot, attempt: attempt, metadata: metadata, rate: 2, canSeek: true,
                       navigation: navigation, playPause: route, showsNowPlaying: false)
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
        XCTAssertTrue(session.isActive)
        XCTAssertEqual(targets.activeIDs, retained, "Hiding a card cannot remove or replace command targets")
        let center = MPRemoteCommandCenter.shared()
        XCTAssertTrue(center.changePlaybackPositionCommand.isEnabled)
        XCTAssertTrue(center.nextTrackCommand.isEnabled)
        XCTAssertTrue(center.playCommand.isEnabled)
        XCTAssertTrue(center.pauseCommand.isEnabled)
        XCTAssertTrue(center.togglePlayPauseCommand.isEnabled)
        session.update(snapshot: snapshot, attempt: attempt, metadata: metadata, rate: 2, canSeek: true,
                       navigation: navigation, playPause: route, showsNowPlaying: true)
        let info = MPNowPlayingInfoCenter.default().nowPlayingInfo
        XCTAssertEqual(info?[MPMediaItemPropertyArtist] as? String, "Series")
        XCTAssertEqual((info?[MPNowPlayingInfoPropertyPlaybackRate] as? NSNumber)?.doubleValue, 2)
        XCTAssertEqual(targets.activeIDs, retained)
        XCTAssertTrue(session.isActive)
        _ = await session.endSession().value
        XCTAssertTrue(targets.activeIDs.isEmpty)
    }

    func testShowingAfterRevocationCannotRecoverFieldsAndLateUpdateCannotHideNewCard() async {
        let targets = VisibilityCommandTargets()
        let session = PlaybackSystemSession(audioActions: .init(activate: {}, deactivate: {}),
            onEvent: { _, _ in }, onCommand: { _, _, _ in true }, onNavigation: { _ in true },
            notificationCenter: NotificationCenter(), registerCommandTarget: targets.register)
        let old = PlayAttempt(id: UUID(), owner: "film", resourceID: "same-resource")
        let snapshot = PlaybackSnapshot(owner: old.owner, resourceID: old.resourceID, phase: .paused)
        session.beginSession(generation: old.id)
        session.update(snapshot: snapshot, attempt: old, metadata: .init(title: "Authorized"), rate: 0,
                       canSeek: false, navigation: .init(), playPause: nil, showsNowPlaying: false)
        session.update(snapshot: snapshot, attempt: old, metadata: nil, rate: 0,
                       canSeek: false, navigation: .init(), playPause: nil, showsNowPlaying: true)
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
        let next = PlayAttempt(id: UUID(), owner: "music", resourceID: "song")
        session.beginSession(generation: next.id)
        session.update(snapshot: .init(owner: next.owner, resourceID: next.resourceID, phase: .paused),
                       attempt: next, metadata: .init(title: "Song"), rate: 0, canSeek: false,
                       navigation: .init(), playPause: nil)
        session.update(snapshot: snapshot, attempt: old, metadata: nil, rate: 0,
                       canSeek: false, navigation: .init(), playPause: nil, showsNowPlaying: false)
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "Song")
        _ = await session.endSession().value
    }

    func testCandidateVisibilityIsAppliedAtCommitAndShowingCannotRestoreRevokedMetadata() async throws {
        let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}),
                                            videoDecoderFactory: { VisibilityVideoDecoder() })
        var startedCards: [String?] = []
        let lease = try controller.bindVideo(owner: "film") { event in
            if event.kind == .started {
                startedCards.append(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String)
            }
        }
        let first = try await lease.play(media("first"), paused: true, initialRate: 1.25)
        XCTAssertTrue(controller.setNowPlayingMetadata(.init(title: "First"), attempt: first))
        let hidden = try await lease.play(media("hidden"), paused: true, initialRate: 1.5, showsNowPlaying: false)
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
        XCTAssertTrue(controller.setNowPlayingMetadata(.init(title: "Hidden", artist: "Series"), attempt: hidden))
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
        XCTAssertEqual(controller.nowPlayingMetadata?.title, "Hidden")
        XCTAssertTrue(controller.setNowPlayingVisible(true, attempt: hidden))
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "Hidden")
        XCTAssertTrue(controller.clearNowPlayingMetadata(attempt: hidden))
        XCTAssertTrue(controller.setNowPlayingVisible(false, attempt: hidden))
        XCTAssertTrue(controller.setNowPlayingVisible(true, attempt: hidden))
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
        XCTAssertEqual(controller.preferredRate, 1.5)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(controller.actualRate, 0)
        _ = try await lease.play(media("next"), paused: true)
        XCTAssertEqual(startedCards.count, 3)
        XCTAssertEqual(startedCards[0], "first")
        XCTAssertNil(startedCards[1], "A hidden candidate cannot briefly publish the donor's visible policy")
        XCTAssertEqual(startedCards[2], "next")
        XCTAssertFalse(controller.setNowPlayingVisible(false, attempt: hidden))
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "next")
        try await lease.close()
        XCTAssertNil(MPNowPlayingInfoCenter.default().nowPlayingInfo)
    }

    func testHiddenFilmPolicyDoesNotTransferToMusic() async throws {
        let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}),
                                            videoDecoderFactory: { VisibilityVideoDecoder() })
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let video = try await lease.play(media("hidden"), paused: true, showsNowPlaying: false)
        let music = controller.playAttempt(resource: try .init(id: "song",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/visibility.wav"), title: "Music"),
            owner: "music", autoplay: false)
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "Music")
        XCTAssertFalse(controller.setNowPlayingVisible(false, attempt: video))
        XCTAssertEqual(controller.currentAttempt, music)
        try await lease.close()
        controller.stop()
    }

    private func media(_ title: String) throws -> PlaybackVideoMediaLease {
        try .init(attemptID: UUID().uuidString, input: .init(resourceID: title,
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/visibility.mp4"), title: title)) {}
    }
}

@MainActor
private final class VisibilityCommandTargets {
    private var commands: [MPRemoteCommand] = []
    private var removed: Set<Int> = []
    var activeIDs: Set<Int> { Set(commands.indices).subtracting(removed) }
    func register(_ command: MPRemoteCommand, handler: @escaping PlaybackSystemSession.CommandHandler)
        -> (@MainActor () -> Void) {
        let id = commands.count
        commands.append(command)
        let target = command.addTarget(handler: handler)
        return { [self] in command.removeTarget(target); removed.insert(id) }
    }
}

@MainActor
private final class VisibilityVideoDecoder: PlaybackVideoDecoding {
    var engine: PlaybackVideoEngine { .aether }
    let display = VisibilityVideoOutput()
    var output: any PlaybackVideoOutput { display }
    var position: Double = 0
    var duration: Double? { 120 }
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)?
    var onTransportRate: (@MainActor (Float) -> Void)?
    var onEnd: (@MainActor () -> Void)?
    func prepare(url: URL, format: String?, clearKeys: [String: Data], position: Double,
                 headers: [String: String]) async throws { self.position = position }
    func align(to position: Double) async throws { self.position = position }
    func renderFirstVideoFrame() async throws {}
    func validateForCommit() throws {}
    func commit(rate: Float, volume: Float) { display.rate = rate; display.volume = volume }
    func setRate(_ rate: Float) { display.rate = rate; onTransportRate?(rate) }
    func seek(to position: Double) async throws -> Double { self.position = position; return position }
    func cancel() { display.rate = 0 }
    func close() async { cancel() }
}

@MainActor
private final class VisibilityVideoOutput: PlaybackVideoOutput {
    var volume: Float = 1
    var rate: Float = 0
    let presentationView = UIView()
    var displayLayer: AVSampleBufferDisplayLayer? { nil }
    var onDisplayLayerChanged: (() -> Void)?
}
#endif

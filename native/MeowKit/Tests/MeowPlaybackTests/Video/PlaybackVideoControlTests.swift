#if os(iOS) || os(macOS)
import XCTest
import Combine
import AVFoundation
import MeowContracts
#if os(iOS)
import UIKit
#else
import AppKit
#endif
@testable import MeowPlayback

/// Decoder observations are controlled here; these tests verify the real
/// coordinator, not decoded media-time accuracy or actual system card delivery.
@MainActor
final class PlaybackVideoControlTests: XCTestCase {
    func testExternalVideoIntentUsesTheCurrentControllerAndNotTheStallSignal() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .aether)
        let attempt = try await commit(controller, lease: lease, decoder: decoder)
        decoder.onPlaybackIntent?(true)
        XCTAssertTrue(controller.wantsPlayback)
        decoder.onTransportRate?(0)
        XCTAssertTrue(controller.wantsPlayback, "A stopped output clock is not a pause command")
        decoder.onPlaybackIntent?(false)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(controller.actualRate, 0)
        decoder.onPlaybackIntent?(true)
        XCTAssertTrue(controller.wantsPlayback)
        XCTAssertEqual(controller.currentAttempt, attempt)
        try await lease.close()
    }

    func testRetiredVideoIntentCannotControlItsReplacementOrReopenAClosedLease() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .aether)
        _ = try await commit(controller, lease: lease, decoder: decoder)
        let late = decoder.onPlaybackIntent
        let next = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        late?(true)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertFalse(controller.wantsPlayback)
        try await lease.close()
        late?(true)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertNil(controller.currentAttempt)
    }

    func testBaseAndTemporaryRatesUseActualEngineWithoutChangingPauseIntent() async throws {
        for engine in PlaybackVideoEngine.allCases {
            let controller = controller()
            let lease = try controller.bindVideo(owner: "film") { _ in }
            let decoder = ControlVideoDecoder(engine: engine)
            let attempt = try await commit(controller, lease: lease, decoder: decoder)
            let base = min(3, engine.maximumRate)
            XCTAssertTrue(controller.setPreferredRate(base, attempt: attempt))
            XCTAssertFalse(controller.setPreferredRate(base + 0.25, attempt: attempt))
            XCTAssertTrue(controller.setTemporaryVideoRate(engine.maximumRate, attempt: attempt))
            XCTAssertEqual(controller.preferredRate, base)
            XCTAssertEqual(controller.effectiveRate, engine.maximumRate)
            XCTAssertFalse(controller.wantsPlayback)
            XCTAssertEqual(controller.actualRate, 0)
            XCTAssertEqual(decoder.output.rate, 0)
            for invalid in [Double.nan, .infinity, -.infinity, 0, engine.maximumRate + 0.25] {
                XCTAssertFalse(controller.setTemporaryVideoRate(invalid, attempt: attempt))
            }
            XCTAssertEqual(controller.effectiveRate, engine.maximumRate)
            XCTAssertTrue(controller.setPreferredRate(1.25, attempt: attempt))
            XCTAssertEqual(controller.effectiveRate, engine.maximumRate, "Base edits cannot end a held acceleration")
            XCTAssertTrue(controller.setTemporaryVideoRate(nil, attempt: attempt))
            XCTAssertEqual(controller.effectiveRate, 1.25)
            XCTAssertEqual(controller.actualRate, 0)
            try await lease.close()
        }
    }

    func testTemporaryRateSurvivesSeekAndRestoresTheLatestBaseRate() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .ksPlayer)
        let attempt = try await commit(controller, lease: lease, decoder: decoder)
        XCTAssertTrue(controller.setPreferredRate(1.5, attempt: attempt))
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: attempt))
        let playing = expectation(description: "Coordinator applied temporary rate")
        var sawPlaying = false
        let observation = controller.$snapshot.sink { snapshot in
            if snapshot.phase == .playing, !sawPlaying { sawPlaying = true; playing.fulfill() }
        }
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [playing], timeout: 3)
        observation.cancel()
        XCTAssertEqual(controller.actualRate, 8)
        XCTAssertEqual(controller.preferredRate, 1.5)
        let sought = expectation(description: "Video seek settled")
        XCTAssertTrue(controller.seek(to: 12, attempt: attempt) { result in
            XCTAssertTrue(result.succeeded)
            sought.fulfill()
        })
        XCTAssertEqual(decoder.output.rate, 0, "Seek suspends output, not its selected effective rate")
        await fulfillment(of: [sought], timeout: 3)
        XCTAssertEqual(controller.currentPosition, 12)
        XCTAssertEqual(controller.actualRate, 8)
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        XCTAssertEqual(controller.actualRate, 8)
        XCTAssertTrue(controller.setTemporaryVideoRate(nil, attempt: attempt))
        XCTAssertEqual(controller.actualRate, 2)
        XCTAssertTrue(controller.pause(attempt: attempt))
        XCTAssertTrue(controller.setTemporaryVideoRate(6, attempt: attempt))
        XCTAssertTrue(controller.setTemporaryVideoRate(nil, attempt: attempt))
        XCTAssertEqual(controller.actualRate, 0)
        XCTAssertFalse(controller.wantsPlayback)
        try await lease.close()
    }

    func testNaturalEndClearsTemporaryRateWithoutReactivatingOutput() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .ksPlayer)
        let attempt = try await commit(controller, lease: lease, decoder: decoder, initialRate: 1.5)
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: attempt))
        decoder.onEnd?()
        XCTAssertEqual(controller.snapshot.phase, .ended)
        XCTAssertEqual(controller.effectiveRate, 1.5)
        XCTAssertEqual(controller.actualRate, 0)
        XCTAssertFalse(controller.setTemporaryVideoRate(6, attempt: attempt))
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: attempt))
        try await lease.close()
    }

    func testCandidateOverridesDefaultForSameOwnerAndClearsOnlyTemporaryRate() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let first = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                     initialRate: 1.25)
        XCTAssertEqual(controller.preferredRate, 1.25)
        XCTAssertTrue(controller.setPreferredRate(3, attempt: first))
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: first))
        let inherited = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        XCTAssertEqual(controller.effectiveRate, 3, "Same-owner inheritance is the base speed, never the hold")
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: first))
        XCTAssertFalse(controller.setPreferredRate(1, attempt: first))
        XCTAssertTrue(controller.setTemporaryVideoRate(6, attempt: inherited))
        let overridden = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                          initialRate: 1)
        XCTAssertEqual(controller.preferredRate, 1, "A new film session can apply its default despite the same owner")
        XCTAssertEqual(controller.effectiveRate, 1)
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: inherited))
        XCTAssertEqual(controller.currentAttempt, overridden)
        try await lease.close()
    }

    func testCandidateClampsInheritedAndExplicitRatesToActualEngine() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let first = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                     initialRate: 3)
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: first))
        let aether = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .aether))
        XCTAssertEqual(controller.preferredRate, 2)
        XCTAssertEqual(controller.effectiveRate, 2)
        XCTAssertFalse(controller.setPreferredRate(3, attempt: aether))
        XCTAssertFalse(controller.setTemporaryVideoRate(3, attempt: aether))
        _ = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        XCTAssertEqual(controller.effectiveRate, 2, "Returning to KS does not revive the previous high rate")
        _ = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .aether),
                             initialRate: 3)
        XCTAssertEqual(controller.preferredRate, 2)
        XCTAssertEqual(controller.actualRate, 0)
        try await lease.close()
    }

    func testInvalidCandidateRateDoesNotDisplaceDonorOrItsControls() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let donor = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                     initialRate: 1.5, showsNowPlaying: false)
        XCTAssertTrue(controller.setTemporaryVideoRate(6, attempt: donor))
        for invalid in [Double.nan, .infinity, -1, 0] {
            do {
                _ = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .aether),
                                     initialRate: invalid)
                XCTFail("An invalid candidate rate cannot commit")
            } catch {
                XCTAssertEqual(error as? PlaybackVideoError, .invalidInput)
            }
            XCTAssertEqual(controller.currentAttempt, donor)
            XCTAssertEqual(controller.preferredRate, 1.5)
            XCTAssertEqual(controller.effectiveRate, 6)
        }
        try await lease.close()
    }

    func testStoppedFailedAndForgedAttemptsCannotChangeVideoControls() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .ksPlayer)
        let attempt = try await commit(controller, lease: lease, decoder: decoder)
        for forged in [
            PlayAttempt(id: attempt.id, owner: "music", resourceID: attempt.resourceID),
            PlayAttempt(id: attempt.id, owner: attempt.owner, resourceID: "other-resource")
        ] {
            XCTAssertFalse(controller.setTemporaryVideoRate(8, attempt: forged))
            XCTAssertFalse(controller.setNowPlayingVisible(false, attempt: forged))
        }
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: attempt))
        decoder.onFailure?(PlaybackVideoError.decodingFailed)
        XCTAssertEqual(controller.snapshot.phase, .failed)
        XCTAssertEqual(controller.effectiveRate, controller.preferredRate)
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: attempt))
        XCTAssertFalse(controller.setNowPlayingVisible(true, attempt: attempt))
        let next = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        XCTAssertTrue(controller.setTemporaryVideoRate(6, attempt: next))
        XCTAssertTrue(controller.stop(attempt: next))
        XCTAssertEqual(controller.effectiveRate, controller.preferredRate)
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: next))
        XCTAssertFalse(controller.setNowPlayingVisible(false, attempt: next))
        try await lease.close()
    }

    func testVideoTemporaryRateCannotLeakIntoMusicEvenForSameOwner() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let video = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                     initialRate: 1.5)
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: video))
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "Music"),
            owner: "film", autoplay: false)
        XCTAssertEqual(controller.effectiveRate, 1.5)
        XCTAssertTrue(controller.setPreferredRate(3, attempt: music))
        XCTAssertFalse(controller.setPreferredRate(4, attempt: music))
        XCTAssertFalse(controller.setTemporaryVideoRate(2, attempt: music))
        XCTAssertFalse(controller.setTemporaryVideoRate(nil, attempt: video))
        XCTAssertEqual(controller.actualRate, 0)
        try await lease.close()
        XCTAssertEqual(controller.currentAttempt, music)
        controller.stop()
    }

    func testVisibilityDoesNotRevokeMetadataAndCannotUndoPrivacySuppression() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let video = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer),
                                     initialRate: 1.5, showsNowPlaying: false)
        let metadata = PlaybackNowPlayingMetadata(title: "Episode", artist: "Series")
        XCTAssertTrue(controller.setNowPlayingMetadata(metadata, attempt: video))
        XCTAssertTrue(controller.setNowPlayingVisible(false, attempt: video))
        XCTAssertEqual(controller.nowPlayingMetadata, metadata, "Presentation policy does not revoke authorized fields")
        XCTAssertTrue(controller.setNowPlayingVisible(true, attempt: video))
        XCTAssertTrue(controller.clearNowPlayingMetadata(attempt: video))
        XCTAssertTrue(controller.setNowPlayingVisible(false, attempt: video))
        XCTAssertTrue(controller.setNowPlayingVisible(true, attempt: video))
        XCTAssertNil(controller.nowPlayingMetadata, "Showing a card cannot restore revoked fields or fallback title")
        XCTAssertEqual(controller.preferredRate, 1.5)
        XCTAssertEqual(controller.actualRate, 0)
        XCTAssertFalse(controller.wantsPlayback)
        let next = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        XCTAssertFalse(controller.setNowPlayingVisible(false, attempt: video))
        XCTAssertEqual(controller.nowPlayingMetadata?.title, "Synthetic video")
        XCTAssertEqual(controller.currentAttempt, next)
        try await lease.close()
    }

    func testReentrantBaseRateSubscriberCannotApplyOlderRateToNewOwner() async throws {
        let controller = controller()
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let attempt = try await commit(controller, lease: lease, decoder: ControlVideoDecoder(engine: .ksPlayer))
        let resource = try PlaybackResource(id: "music",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.wav"), title: "Music")
        var successor: PlayAttempt?
        let observation = controller.$preferredRate.sink { rate in
            if rate == 2, successor == nil {
                successor = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
            }
        }
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        observation.cancel()
        XCTAssertEqual(controller.currentAttempt, successor)
        XCTAssertEqual(controller.preferredRate, 1)
        XCTAssertEqual(controller.effectiveRate, 1)
        XCTAssertFalse(controller.setTemporaryVideoRate(8, attempt: attempt))
        try await lease.close()
        controller.stop()
    }

    #if os(iOS)
    func testInterruptionAndPausedSeekNeverStartOutputWhenTemporaryRateChanges() async throws {
        let notifications = NotificationCenter()
        let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}),
                                            systemNotifications: notifications)
        let lease = try controller.bindVideo(owner: "film") { _ in }
        let decoder = ControlVideoDecoder(engine: .ksPlayer)
        let attempt = try await commit(controller, lease: lease, decoder: decoder)
        notifications.post(name: AVAudioSession.interruptionNotification,
            object: AVAudioSession.sharedInstance(), userInfo: [
                AVAudioSessionInterruptionTypeKey: NSNumber(value: AVAudioSession.InterruptionType.began.rawValue)
            ])
        XCTAssertTrue(controller.setTemporaryVideoRate(8, attempt: attempt))
        XCTAssertEqual(controller.actualRate, 0)
        let sought = expectation(description: "Paused seek settled")
        XCTAssertTrue(controller.seek(to: 10, attempt: attempt) { result in
            XCTAssertTrue(result.succeeded)
            sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 3)
        XCTAssertTrue(controller.setTemporaryVideoRate(nil, attempt: attempt))
        XCTAssertEqual(controller.actualRate, 0)
        XCTAssertFalse(controller.wantsPlayback)
        try await lease.close()
    }
    #endif

    private func controller() -> PlaybackController {
        #if os(iOS)
        return PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}))
        #else
        return PlaybackController()
        #endif
    }

    private func commit(_ controller: PlaybackController, lease: PlaybackVideoLease, decoder: ControlVideoDecoder,
                        initialRate: Double? = nil, showsNowPlaying: Bool = true) async throws -> PlayAttempt {
        let media = try PlaybackVideoMediaLease(attemptID: UUID().uuidString, input: .init(
            resourceID: UUID().uuidString, url: URL(fileURLWithPath: "/synthetic-test-not-opened/control.mp4"),
            title: "Synthetic video")) {}
        let transport = try PlaybackVideoTransport(media: media, input: XCTUnwrap(media.takeInput()),
            decoder: decoder, initialRate: initialRate, showsNowPlaying: showsNowPlaying)
        try await transport.prepare()
        try await transport.renderFirstVideoFrame()
        let admission = controller.admitVideo(owner: lease.owner, candidate: transport)
        do {
            return try controller.commitVideo(transport, lease: lease, admission: admission, paused: true)
        } catch {
            controller.cancelVideoAdmission(candidate: transport)
            try await transport.close()
            throw error
        }
    }
}

@MainActor
private final class ControlVideoDecoder: PlaybackVideoDecoding {
    let engine: PlaybackVideoEngine
    let display = ControlVideoOutput()
    var output: any PlaybackVideoOutput { display }
    var position: Double = 0
    var duration: Double? { 120 }
    var onFailure: (@MainActor (Error) -> Void)?
    var onProgress: (@MainActor (Double) -> Void)?
    var onTransportRate: (@MainActor (Float) -> Void)?
    var onPlaybackIntent: (@MainActor (Bool) -> Void)?
    var onEnd: (@MainActor () -> Void)?
    init(engine: PlaybackVideoEngine) { self.engine = engine }
    func prepare(url: URL, format: String?, clearKeys: [String: Data], position: Double,
                 headers: [String: String]) async throws { self.position = position }
    func align(to position: Double) async throws { self.position = position }
    func renderFirstVideoFrame() async throws {}
    func validateForCommit() throws {}
    func commit(rate: Float, volume: Float) { display.volume = volume; setRate(rate) }
    func setRate(_ rate: Float) { display.rate = rate; onTransportRate?(rate) }
    func seek(to position: Double) async throws -> Double { self.position = position; return position }
    func cancel() { display.rate = 0 }
    func close() async { cancel() }
}

@MainActor
private final class ControlVideoOutput: PlaybackVideoOutput {
    var volume: Float = 1
    var rate: Float = 0
    var displayLayer: AVSampleBufferDisplayLayer? { nil }
    var onDisplayLayerChanged: (() -> Void)?
    #if os(iOS)
    let presentationView = UIView()
    #else
    let presentationView = NSView()
    #endif
}
#endif

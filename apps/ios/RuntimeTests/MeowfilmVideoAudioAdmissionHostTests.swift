import XCTest
import UIKit
import MeowContracts
@testable import MeowPlayback

/// Real decoding, first-frame presentation and media ownership. Only the
/// fallible system audio operation is controlled; this is not device audio proof.
@MainActor
final class MeowfilmVideoAudioAdmissionHostTests: XCTestCase {
    func testAudioActivationFailureAfterPictureReadinessPreservesMusicUntilAnExplicitRetry() async throws {
        var failActivation = true, activations = 0, deactivations = 0
        let controller = PlaybackController(audioSessionActions: .init(activate: {
            activations += 1
            if failActivation { throw NativePlaybackError.audioSessionUnavailable }
        }, deactivate: { deactivations += 1 }))
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        var events: [PlaybackVideoEvent] = []
        let film = try controller.bindVideo(owner: "film") { events.append($0) }
        let fixture = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock { try await fixture.close() }
        let donor = controller.playAttempt(resource: try .init(id: "audio-admission-music",
            url: movie, title: "Synthetic paused music"), owner: "music", autoplay: false)
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { controller.snapshot.phase == .paused }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 15)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        let donorItem = try XCTUnwrap(controller.player.currentItem)
        let donorPosition = controller.snapshot.position
        XCTAssertEqual(activations, 0, "A paused donor has not acquired audio focus")

        var rejectedReleases = 0
        let rejected = try PlaybackVideoMediaLease(attemptID: "audio-rejected",
            input: .init(resourceID: "audio-rejected", url: movie, title: "Synthetic candidate")) {
                rejectedReleases += 1
            }
        do {
            _ = try await film.play(rejected)
            XCTFail("System activation must reject an otherwise prepared video")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        XCTAssertEqual(activations, 1)
        XCTAssertTrue(events.contains { $0.kind == .prepared && $0.attemptID == "audio-rejected" })
        XCTAssertEqual(events.filter { $0.kind == .failed }.map(\.error), [.outputFailed])
        XCTAssertFalse(events.contains { $0.kind == .started || $0.kind == .ended })
        XCTAssertEqual(rejectedReleases, 1)
        try await rejected.close()
        XCTAssertEqual(rejectedReleases, 1, "The failed native candidate has one drained receipt")
        XCTAssertEqual(controller.currentAttempt, donor)
        XCTAssertTrue(controller.player.currentItem === donorItem)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.snapshot.position, donorPosition, accuracy: 0.15)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertNil(film.currentAttempt)
        XCTAssertEqual(deactivations, 0, "A failed acquire cannot release someone else's system focus")

        failActivation = false
        var acceptedReleases = 0
        let accepted = try PlaybackVideoMediaLease(attemptID: "audio-accepted",
            input: .init(resourceID: "audio-accepted", url: movie, title: "Synthetic retry")) {
                acceptedReleases += 1
            }
        let current = try await film.play(accepted)
        XCTAssertEqual(activations, 2)
        XCTAssertEqual(controller.currentAttempt, current)
        XCTAssertEqual(current.owner, "film")
        XCTAssertNil(controller.player.currentItem)
        XCTAssertEqual(events.filter { $0.kind == .started }.map(\.attemptID), ["audio-accepted"])
        XCTAssertTrue(controller.wantsPlayback)
        XCTAssertEqual(acceptedReleases, 0)
        try await film.close()
        XCTAssertEqual(acceptedReleases, 1)
        XCTAssertEqual(deactivations, 1)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(controller.wantsPlayback)
    }

    func testFailedAudioReleaseRemainsFailedAfterEmptyCloseSystemRecoveryOrNewMusic() async throws {
        for next in ["still-failing", "system-recovered", "music"] {
            var failDeactivation = true, activations = 0, deactivations = 0
            let controller = PlaybackController(audioSessionActions: .init(activate: {
                activations += 1
            }, deactivate: {
                deactivations += 1
                if failDeactivation { throw NativePlaybackError.audioSessionDeactivationFailed }
            }))
            let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
            let film = try controller.bindVideo(owner: "film") { _ in }
            let fixture = try AudioAdmissionHost(controller: controller, film: film)
            addTeardownBlock {
                failDeactivation = false
                do { try await fixture.close() }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            var releases = 0
            let media = try PlaybackVideoMediaLease(attemptID: "audio-release",
                input: .init(resourceID: "audio-release", url: movie, title: "Synthetic video")) {
                    releases += 1
                }
            _ = try await film.play(media)
            XCTAssertEqual(activations, 1)
            do { try await film.close(); XCTFail("failed system release cannot acknowledge close") }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            XCTAssertEqual(deactivations, 1)
            XCTAssertEqual(releases, 1)
            XCTAssertNil(film.currentAttempt)
            XCTAssertNil(controller.currentAttempt)

            var music: PlayAttempt?
            if next != "still-failing" { failDeactivation = false }
            if next == "system-recovered" {
                controller.stop()
                XCTAssertEqual(deactivations, 2, "the actual controller can release system focus later")
                XCTAssertNil(controller.lastError)
            } else if next == "music" {
                music = controller.playAttempt(resource: try .init(id: "next-music",
                    url: movie, title: "Synthetic music"), owner: "music")
                let ready = expectation(for: NSPredicate { _, _ in
                    MainActor.assumeIsolated { controller.snapshot.phase == .playing }
                }, evaluatedWith: nil)
                await fulfillment(of: [ready], timeout: 15)
                XCTAssertEqual(controller.currentAttempt, music)
                XCTAssertTrue(controller.wantsPlayback)
                XCTAssertEqual(activations, 2)
            }
            let before = deactivations
            for _ in 0..<2 {
                do { try await film.close(); XCTFail("the same failed close receipt cannot turn into success") }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            XCTAssertEqual(deactivations, before, "an old lease must not retry against a new system owner")
            XCTAssertEqual(releases, 1)
            XCTAssertEqual(controller.currentAttempt, music)
            if music != nil { XCTAssertTrue(controller.wantsPlayback) }
            failDeactivation = false
            do { try await fixture.close() }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        }
    }

    func testControllerFirstRetirementRecordsAudioReleaseFailureForTheExactOldLease() async throws {
        for trigger in ["stop", "failure", "music"] {
            var failDeactivation = true, deactivations = 0
            let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {
                deactivations += 1
                if failDeactivation { throw NativePlaybackError.audioSessionDeactivationFailed }
            }))
            let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
            let film = try controller.bindVideo(owner: "film") { _ in }
            let fixture = try AudioAdmissionHost(controller: controller, film: film)
            addTeardownBlock {
                failDeactivation = false
                do { try await fixture.close() }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            let media = try PlaybackVideoMediaLease(attemptID: "controller-first",
                input: .init(resourceID: "controller-first", url: movie, title: "Synthetic video")) {}
            let transport = try PlaybackVideoTransport(media: media, input: XCTUnwrap(media.takeInput()))
            try await transport.prepare()
            try await transport.renderFirstVideoFrame()
            let admission = controller.admitVideo(owner: film.owner, candidate: transport)
            _ = try controller.commitVideo(transport, lease: film, admission: admission, paused: false)
            if trigger == "stop" { controller.stop() }
            else if trigger == "failure" { transport.kernel.onFailure?(.decodeFailed) }
            else {
                _ = controller.playAttempt(resource: try .init(id: "next-music",
                    url: movie, title: "Synthetic music"), owner: "music", autoplay: false)
            }
            let next = controller.currentAttempt
            XCTAssertEqual(deactivations, 1)
            XCTAssertNil(film.currentAttempt)
            failDeactivation = false
            if trigger == "music" {
                // Wait for the new paused owner to finish its own successful
                // system release before measuring the retired lease's close.
                let released = expectation(for: NSPredicate { _, _ in
                    MainActor.assumeIsolated { deactivations == 2 && controller.player.currentItem?.status == .readyToPlay }
                }, evaluatedWith: nil)
                await fulfillment(of: [released], timeout: 15)
                XCTAssertEqual(deactivations, 2)
                XCTAssertEqual(controller.player.currentItem?.status, .readyToPlay)
            }
            let before = deactivations
            for _ in 0..<2 {
                do { try await film.close(); XCTFail("controller-first failure belongs to the retired lease") }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            XCTAssertEqual(controller.currentAttempt, next)
            XCTAssertEqual(deactivations, before, "old close cannot retry the shared system session")
            do { try await fixture.close() }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        }
    }
}

@MainActor
private final class AudioAdmissionHost {
    let controller: PlaybackController
    let film: PlaybackVideoLease
    private let window: UIWindow
    private weak var previousWindow: UIWindow?

    init(controller: PlaybackController, film: PlaybackVideoLease) throws {
        self.controller = controller; self.film = film
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        previousWindow = scene.windows.first { $0.isKeyWindow }
        let host = UIViewController()
        host.loadViewIfNeeded()
        film.surface.frame = host.view.bounds
        film.surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        host.view.addSubview(film.surface)
        window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = host
        window.makeKeyAndVisible()
        window.layoutIfNeeded()
    }

    func close() async throws {
        defer {
            controller.stop()
            window.isHidden = true
            window.rootViewController = nil
            previousWindow?.makeKey()
        }
        try await film.close()
    }
}

#if os(iOS)
import Foundation
import Combine
import XCTest
import AVFoundation
import MediaPlayer
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature

/// These tests post through NotificationCenter into the iOS adapter. Listener
/// readiness is controlled and media is a local WAV, not a real OS interruption
/// or an upstream HLS/physical-headphone acceptance test.
@MainActor
final class PlaybackSystemSessionTests: XCTestCase {
    func testPlayerReadsAndSeeksTheOwnedByteSourceWithoutOpeningTheRemoteURL() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let source = SystemSessionBytes(data: try Data(contentsOf: file))
        let controller = PlaybackController(audioSessionActions: SystemAudioProbe().actions)
        let played = expectation(description: "cached source decoded")
        let seeked = expectation(description: "cached source sought while paused")
        var sawPlay = false, sawSeek = false
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .failed { XCTFail("Owned bytes must not fall through to a remote request") }
            if event.kind == .played, !sawPlay { sawPlay = true; played.fulfill() }
            if event.snapshot.position >= 19, !sawSeek { sawSeek = true; seeked.fulfill() }
        }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "shared-file",
            url: URL(string: "https://unused.invalid/never-requested.wav")!, title: "Bytes", byteSource: source), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [played], timeout: 5)
        XCTAssertTrue(controller.requiresLocalMediaFetch)
        XCTAssertFalse(controller.player.allowsExternalPlayback)
        _ = controller.pause(attempt: attempt)
        _ = controller.seek(to: 20, attempt: attempt)
        await fulfillment(of: [seeked], timeout: 5)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertGreaterThan(source.readCount, 0)
        controller.stop()
        XCTAssertTrue(source.isRetired)
    }

    func testControllerReleaseFromMainQueueDrainsOwnedScopesWithoutNestedDeinitTasks() async throws {
        let released = expectation(description: "UIKit-style main queue release completed")
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                defer { released.fulfill() }
                let notifications = NotificationCenter()
                var controller: PlaybackController? = PlaybackController(systemNotifications: notifications)
                weak var weakController = controller
                var outputs = 0
                let lease: PlaybackOwnerLease
                do {
                    lease = try XCTUnwrap(controller).bind(owner: "release-fixture",
                        onOutputUnavailable: { outputs += 1 }) { _ in }
                } catch {
                    XCTFail("Could not establish the release fixture: \(error)")
                    return
                }
                controller = nil
                XCTAssertNil(weakController)
                XCTAssertTrue(lease.isCancelled, "The router must be released in the same main-queue turn")
                notifications.post(name: AVAudioSession.routeChangeNotification,
                    object: AVAudioSession.sharedInstance(), userInfo: [
                        AVAudioSessionRouteChangeReasonKey:
                            NSNumber(value: AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue)
                    ])
                XCTAssertEqual(outputs, 0, "The released controller must remove system observers")
                lease.cancel()
            }
        }
        await fulfillment(of: [released], timeout: 3)
    }

    func testOwnerLeaseFinalReleaseRemovesObserversAndCapturesOnMainWithoutRetainingController() async throws {
        for offMain in [false, true] {
            let notifications = ReleaseNotificationCenter()
            let controller = PlaybackController(systemNotifications: notifications)
            var probe: ControllerReleaseProbe? = ControllerReleaseProbe()
            weak var weakProbe = probe
            var lease: PlaybackOwnerLease? = try controller.bind(owner: "release-owner",
                onOutputUnavailable: { XCTFail("A released owner must not receive output changes") }) { [capture = probe!] _ in
                    capture.touch()
                }
            probe = nil
            XCTAssertGreaterThan(notifications.activeCount, 0)
            weak var weakLease = lease
            let box = FinalReleaseBox(try XCTUnwrap(lease))
            lease = nil
            let cleaned = expectation(description: "Owner lease cleanup completed")
            if offMain {
                releaseOffMain(box, then: cleaned)
                XCTAssertNil(weakLease, "The deferred removal cannot retain the lease")
            } else {
                DispatchQueue.main.async {
                    box.release()
                    XCTAssertNil(weakLease)
                    XCTAssertNil(weakProbe)
                    XCTAssertEqual(notifications.activeCount, 0)
                    cleaned.fulfill()
                }
            }
            await fulfillment(of: [cleaned], timeout: 3)
            XCTAssertNil(weakLease)
            XCTAssertNil(weakProbe)
            XCTAssertEqual(notifications.activeCount, 0)
            XCTAssertEqual(notifications.removalCount, notifications.registrationCount)
            postOutputUnavailable(to: notifications)
            withExtendedLifetime(controller) {}
        }
    }

    func testOffMainOwnerLeaseReleaseCannotRemoveNewBindingWithSameOwner() async throws {
        let notifications = ReleaseNotificationCenter()
        let controller = PlaybackController(systemNotifications: notifications)
        var old: PlaybackOwnerLease? = try controller.bind(owner: "same-owner",
            onOutputUnavailable: { XCTFail("The replaced owner cannot receive output changes") }) { _ in }
        let box = FinalReleaseBox(try XCTUnwrap(old))
        old = nil
        let cleaned = expectation(description: "Old lease cleanup drained")
        releaseOffMain(box, then: cleaned)
        var outputs = 0
        let successor = try controller.bind(owner: "same-owner", onOutputUnavailable: { outputs += 1 }) { _ in }
        defer { successor.cancel() }
        await fulfillment(of: [cleaned], timeout: 3)
        XCTAssertFalse(successor.isCancelled)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 1)
        successor.cancel()
        XCTAssertEqual(notifications.activeCount, 0)
    }

    func testMainQueueControllerReleaseSettlesPendingSeekAfterRemovingSystemObservers() async throws {
        try await controllerReleaseSettlesPendingSeek(offMain: false)
    }

    func testOffMainControllerReleaseTransfersCleanupWithoutRetainingControllerOrBlockingMain() async throws {
        try await controllerReleaseSettlesPendingSeek(offMain: true)
    }

    private func controllerReleaseSettlesPendingSeek(offMain: Bool) async throws {
        let notifications = ReleaseNotificationCenter(), startup = HeldSystemHLSStartup()
        var controller: PlaybackController? = PlaybackController(
            audioSessionActions: .init(activate: { XCTFail("Held preparation must not activate audio") }, deactivate: {}),
            systemNotifications: notifications, startScopedHLS: startup.start)
        weak var weakController = controller
        var ownerProbe: ControllerReleaseProbe? = ControllerReleaseProbe()
        weak var weakOwnerProbe = ownerProbe
        let lease = try XCTUnwrap(controller).bind(owner: "release-pending-seek",
            onOutputUnavailable: { XCTFail("No output event is posted before final release") }) { [probe = ownerProbe!] _ in
                probe.touch()
            }
        ownerProbe = nil
        let attempt = try XCTUnwrap(controller).playAttempt(resource: .init(id: "held-release",
            url: URL(string: "https://media.invalid/held-release.m3u8")!, title: "Held release",
            headers: ["X-Media-Test": "synthetic"]), owner: "release-pending-seek", autoplay: false)
        XCTAssertTrue(startup.isWaiting)
        XCTAssertGreaterThan(notifications.activeCount, 0)
        var seekProbe: ControllerReleaseProbe? = ControllerReleaseProbe()
        weak var weakSeekProbe = seekProbe
        var results: [PlaybackSeekResult] = []
        XCTAssertTrue(try XCTUnwrap(controller).seek(to: 12, attempt: attempt) { [probe = seekProbe!] result in
            XCTAssertTrue(Thread.isMainThread)
            XCTAssertEqual(notifications.activeCount, 0, "Resource removal must precede client cancellation")
            probe.touch()
            results.append(result)
        })
        seekProbe = nil
        let box = FinalReleaseBox(try XCTUnwrap(controller))
        controller = nil
        let cleaned = expectation(description: "Final cleanup completed on main")
        if offMain {
            releaseOffMain(box, then: cleaned)
            XCTAssertNil(weakController, "The deferred cleanup may retain resources, never the controller")
        } else {
            DispatchQueue.main.async {
                box.release()
                XCTAssertNil(weakController)
                XCTAssertTrue(lease.isCancelled)
                XCTAssertNil(weakOwnerProbe)
                XCTAssertNil(weakSeekProbe)
                XCTAssertEqual(results.map(\.outcome), [.stopped])
                cleaned.fulfill()
            }
        }
        await fulfillment(of: [cleaned], timeout: 3)
        XCTAssertNil(weakController)
        XCTAssertTrue(lease.isCancelled)
        XCTAssertNil(weakOwnerProbe)
        XCTAssertNil(weakSeekProbe)
        XCTAssertEqual(results.map(\.outcome), [.stopped])
        XCTAssertEqual(results.first?.attempt, attempt)
        XCTAssertNil(results.first?.actualPosition)
        XCTAssertEqual(notifications.activeCount, 0)
        XCTAssertEqual(notifications.removalCount, notifications.registrationCount)
        postOutputUnavailable(to: notifications)
        try startup.complete(with: URL(fileURLWithPath: "/not-read/after-controller-release.m3u8"))
        await Task.yield()
        XCTAssertEqual(results.map(\.outcome), [.stopped], "Late preparation cannot settle a retired seek twice")
        lease.cancel()
        XCTAssertEqual(notifications.removalCount, notifications.registrationCount)
    }

    func testOffMainControllerReleaseDetachesRetainedNativePlayerAndReleasesOwner() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = ReleaseNotificationCenter()
        var controller: PlaybackController? = PlaybackController(
            audioSessionActions: .init(activate: { XCTFail("Paused preparation must not activate audio") }, deactivate: {}),
            systemNotifications: notifications)
        weak var weakController = controller
        let player = try XCTUnwrap(controller).player
        let prepared = expectation(description: "Native item ready without audio activation")
        var probe: ControllerReleaseProbe? = ControllerReleaseProbe()
        weak var weakProbe = probe
        let lease = try XCTUnwrap(controller).bind(owner: "release-native",
            onOutputUnavailable: { XCTFail("Released native owner cannot receive output changes") }) { [capture = probe!] event in
                capture.touch()
                if event.kind == .prepared { prepared.fulfill() }
            }
        probe = nil
        _ = try XCTUnwrap(controller).playAttempt(resource: .init(id: "release-native",
            url: file, title: "Paused native release"), owner: "release-native", autoplay: false)
        await fulfillment(of: [prepared], timeout: 5)
        XCTAssertNotNil(player.currentItem)
        XCTAssertEqual(player.rate, 0)
        XCTAssertGreaterThan(notifications.activeCount, 0)
        let box = FinalReleaseBox(try XCTUnwrap(controller))
        controller = nil
        let cleaned = expectation(description: "Native player cleanup completed")
        releaseOffMain(box, then: cleaned)
        XCTAssertNil(weakController)
        await fulfillment(of: [cleaned], timeout: 3)
        XCTAssertNil(player.currentItem, "An externally retained AVPlayer must not retain the old media item")
        XCTAssertEqual(player.rate, 0)
        XCTAssertTrue(lease.isCancelled)
        XCTAssertNil(weakProbe)
        XCTAssertEqual(notifications.activeCount, 0)
        XCTAssertEqual(notifications.removalCount, notifications.registrationCount)
        postOutputUnavailable(to: notifications)
        lease.cancel()
    }

    private func releaseOffMain<Value: Sendable>(_ box: FinalReleaseBox<Value>, then cleaned: XCTestExpectation) {
        let released = DispatchSemaphore(value: 0)
        DispatchQueue.global().async {
            XCTAssertFalse(Thread.isMainThread)
            box.release()
            released.signal()
            // FIFO after the cleanup submitted by the controller's deinit.
            DispatchQueue.main.async { cleaned.fulfill() }
        }
        // Deliberately hold main until the background final release returns.
        // A synchronous hop back to main would fail this bounded assertion.
        XCTAssertEqual(released.wait(timeout: .now() + 2), .success)
    }

    func testInterruptionEndDuringHLSPreparationKeepsNewOwnerPausedUntilExplicitPlay() async throws {
        try await interruptedHandoff(endedBeforeReady: true)
    }

    func testHLSExplicitPlayReactivatesWithoutInterruptionEnded() async throws {
        try await interruptedHandoff(endedBeforeReady: false)
    }

    private func interruptedHandoff(endedBeforeReady: Bool) async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter(), startup = HeldSystemHLSStartup()
        let controller = PlaybackController(systemNotifications: notifications, startScopedHLS: startup.start)
        let musicPlayed = expectation(description: "Music is playing before interruption")
        let filmReady = expectation(description: "Replacement is ready")
        let filmPlayed = expectation(description: "Explicit replacement playback")
        var played: [PlayAttempt] = []
        let music = try controller.bind(owner: "music") { event in
            if event.kind == .played { played.append(event.attempt); musicPlayed.fulfill() }
        }
        let film = try controller.bind(owner: "film") { event in
            if event.kind == .prepared { filmReady.fulfill() }
            if event.kind == .played { played.append(event.attempt); filmPlayed.fulfill() }
        }
        defer { controller.stop(); music.cancel(); film.cancel() }
        let old = controller.playAttempt(resource: try .init(id: "music", url: file, title: "Music"), owner: "music")
        _ = controller.setVolume(0, attempt: old)
        await fulfillment(of: [musicPlayed], timeout: 5)
        postInterruption(.began, to: notifications)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.player.rate, 0)

        let next = controller.playAttempt(resource: try .init(id: "film",
            url: URL(string: "https://media.invalid/scoped.m3u8")!, title: "Film",
            headers: ["X-Media-Test": "synthetic"]), owner: "film")
        _ = controller.setVolume(0, attempt: next)
        XCTAssertTrue(startup.isWaiting)
        XCTAssertNil(controller.player.currentItem, "The interruption end must arrive before installing the replacement item")
        if endedBeforeReady { postInterruption(.ended, shouldResume: true, to: notifications) }
        try startup.complete(with: file)
        await fulfillment(of: [filmReady], timeout: 5)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(played, [old], "The new owner cannot inherit the old owner's automatic-resume permission")
        XCTAssertFalse(controller.systemCommand(.play, generation: old.id))
        XCTAssertTrue(controller.systemCommand(.play, generation: next.id))
        await fulfillment(of: [filmPlayed], timeout: 5)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertEqual(controller.snapshot.phase, .playing)
        XCTAssertGreaterThan(controller.player.rate, 0)
        XCTAssertEqual(played, [old, next])
        if !endedBeforeReady {
            postInterruption(.ended, shouldResume: false, to: notifications)
            XCTAssertEqual(controller.snapshot.phase, .playing, "A late end cannot cancel newer explicit playback")
        }
    }

    func testOutputUnavailablePausesImmediatelyAndNotifiesOwnerEvenWhenAlreadyPaused() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter()
        let controller = PlaybackController(systemNotifications: notifications)
        let playing = expectation(description: "Output starts")
        var outputs = 0
        var wasPausedBeforeDelivery: [Bool] = []
        let lease = try controller.bind(owner: "music", onPlayPause: { _, _ in
            XCTFail("Output loss must not fabricate a play/pause command"); return false
        }, onOutputUnavailable: {
            outputs += 1
            wasPausedBeforeDelivery.append(controller.player.rate == 0)
        }) { event in if event.kind == .played { playing.fulfill() } }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "output", url: file, title: "Output"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [playing], timeout: 5)

        notifications.post(name: AVAudioSession.routeChangeNotification, object: AVAudioSession.sharedInstance(),
            userInfo: [AVAudioSessionRouteChangeReasonKey: NSNumber(value: AVAudioSession.RouteChangeReason.newDeviceAvailable.rawValue)])
        XCTAssertEqual(outputs, 0)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2, "An already-paused item may still own pending business navigation")
        XCTAssertEqual(wasPausedBeforeDelivery, [true, true])
        controller.stop()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "An owner lease still covers source work after its native attempt stops")
        lease.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "The final lease cancellation releases output delivery")
    }

    func testOutputSubscriptionRejectsDisabledCancelledAndReplacedLeasesWithoutAttempt() async throws {
        let notifications = NotificationCenter()
        var controller: PlaybackController? = PlaybackController(systemNotifications: notifications)
        weak var released = controller
        var oldCalls = 0, currentCalls = 0
        let old = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { oldCalls += 1 }) { _ in
            XCTFail("Output subscription alone must not produce a native event")
        }
        XCTAssertNil(controller?.currentAttempt)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 1)
        old.setEnabled(false)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 1)
        old.setEnabled(true)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 2)
        let current = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { currentCalls += 1 }) { _ in }
        old.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 2)
        XCTAssertEqual(currentCalls, 1)
        current.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(currentCalls, 1)
        let final = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { currentCalls += 1 }) { _ in }
        controller = nil
        XCTAssertNil(released)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(currentCalls, 1)
        final.cancel()
    }

    func testOutputObservationIsPassiveAndEndsAfterSessionAndSubscriptionRelease() async {
        let notifications = NotificationCenter(), targets = SystemRemoteTargetRecorder()
        let before = MPNowPlayingInfoCenter.default().nowPlayingInfo as NSDictionary?
        var outputs = 0
        let session = PlaybackSystemSession(audioActions: .live,
            onEvent: { _, _ in XCTFail("No attempt-scoped notification was posted") },
            onCommand: { _, _, _ in true }, onNavigation: { _ in true },
            onOutputUnavailable: { outputs += 1 }, notificationCenter: notifications,
            registerCommandTarget: targets.register)
        defer { session.invalidate() }
        session.updateOutputSubscription(true)
        XCTAssertTrue(targets.registered.isEmpty, "An environment subscription cannot install transport commands")
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo as NSDictionary?, before)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 1)
        session.endSession()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2)
        session.updateOutputSubscription(false)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2)
        session.beginSession(generation: UUID())
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "Unbound native transport still observes output loss")
        session.endSession()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3)
        session.updateOutputSubscription(true)
        session.invalidate()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3)
    }

    func testOutputUnavailableDuringIdleSourceResolutionRejectsLatePlaybackAndAllowsExplicitPlay() async throws {
        try await outputUnavailableDuringSourceWait(preparedMedia: false)
    }

    func testOutputUnavailableDuringPreparedMediaWaitRejectsLatePlaybackAndAllowsExplicitPlay() async throws {
        try await outputUnavailableDuringSourceWait(preparedMedia: true)
    }

    private func outputUnavailableDuringSourceWait(preparedMedia: Bool) async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter()
        let controller = PlaybackController(systemNotifications: notifications)
        let service = SystemOutputMusicService(file: file)
        let model = MusicViewModel(service: service, playback: controller)
        let firstPlayed = expectation(description: "Original music is playing")
        let newPlayed = expectation(description: "New explicit music playback")
        var played: [PlayAttempt] = []
        let lease = try controller.bind(owner: "music", onPlayPause: { [weak model] attempt, command in
            model?.acceptPlayPause(command, attempt: attempt) ?? false
        }, onOutputUnavailable: { [weak model] in model?.onOutputUnavailable() }) { [weak model] event in
            model?.onPlaybackEvent(event)
            if event.kind == .played {
                played.append(event.attempt)
                if played.count == 1 { firstPlayed.fulfill() }
                else { newPlayed.fulfill() }
            }
        }
        defer { lease.cancel(); controller.stop() }
        await model.load()
        await model.prepareCurrent()
        let original = try XCTUnwrap(controller.currentAttempt)
        _ = controller.setVolume(0, attempt: original)
        await fulfillment(of: [firstPlayed], timeout: 5)
        let entered = expectation(description: preparedMedia ? "Prepared media waiting for activation" : "Committed next waiting for source")
        let gate = SystemOutputWait { entered.fulfill() }
        defer { Task { await gate.release() } }
        if preparedMedia {
            model.stopForMediaHandoff()
            await service.holdActivation(gate)
        } else { await service.holdResolution(gate) }
        let request = Task {
            if preparedMedia { await model.switchQuality("128k") }
            else { await model.next() }
        }
        await fulfillment(of: [entered], timeout: 5)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertNil(model.activeNativeAttemptID)
        XCTAssertEqual(model.phase, .idle)
        XCTAssertNotNil(model.pendingSourceSelection)
        let queue = model.queue, current = model.current?.id
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertNil(model.pendingSourceSelection)
        await gate.release()
        await request.value
        await model.flushPlaybackFacts()
        XCTAssertNil(controller.currentAttempt, "The old source receipt cannot commit a native attempt")
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(played, [original])
        XCTAssertEqual(model.queue, queue)
        XCTAssertEqual(model.current?.id, current)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertNil(model.error)
        await model.togglePlayback()
        await fulfillment(of: [newPlayed], timeout: 5)
        XCTAssertEqual(played.count, 2)
        XCTAssertNotEqual(controller.currentAttempt?.id, original.id)
        XCTAssertEqual(model.current?.id, current)
        XCTAssertEqual(model.phase, .playing)
        XCTAssertNil(model.error)
        await model.close()
    }

    func testInterruptionPauseDoesNotBecomeOwnerPauseAndResetClearsPendingPlayback() async throws {
        let notifications = NotificationCenter(), startup = HeldSystemHLSStartup()
        let controller = PlaybackController(systemNotifications: notifications, startScopedHLS: startup.start)
        var ownerPauses = 0
        let lease = try controller.bind(owner: "music", onPlayPause: { _, _ in ownerPauses += 1; return true }) { _ in }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "pending",
            url: URL(string: "https://media.invalid/pending.m3u8")!, title: "Pending",
            headers: ["X-Media-Test": "synthetic"]), owner: "music")
        postInterruption(.began, to: notifications)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(ownerPauses, 0)
        notifications.post(name: AVAudioSession.mediaServicesWereResetNotification, object: AVAudioSession.sharedInstance())
        XCTAssertEqual(controller.snapshot.phase, .failed)
        XCTAssertEqual(controller.lastError as? NativePlaybackError, .mediaServicesReset)
        XCTAssertNil(controller.player.currentItem)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
        postInterruption(.ended, shouldResume: true, to: notifications)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(ownerPauses, 0)
        XCTAssertEqual(controller.snapshot.phase, .failed)
        controller.stop()
        XCTAssertNil(controller.currentAttempt)
    }

    func testStandardCommandTargetsFollowCapabilitiesAndAreRemovedOnReplacementAndEnd() async throws {
        let targets = SystemRemoteTargetRecorder()
        let session = PlaybackSystemSession(audioActions: .live, onEvent: { _, _ in },
            onCommand: { _, _, _ in true }, onNavigation: { _ in true },
            notificationCenter: NotificationCenter(), registerCommandTarget: targets.register)
        defer { session.endSession() }
        let center = MPRemoteCommandCenter.shared()
        let transport: [MPRemoteCommand] = [center.playCommand, center.pauseCommand, center.togglePlayPauseCommand,
                                            center.stopCommand, center.changePlaybackPositionCommand]
        let music = PlayAttempt(id: UUID(), owner: "music", resourceID: "song")
        let binding = UUID(), revision = UUID()
        let navigation = PlaybackNavigationRoutes(
            previous: .init(attempt: music, bindingID: binding, revision: revision, direction: .previous),
            next: .init(attempt: music, bindingID: binding, revision: revision, direction: .next))
        let playPause = PlaybackPlayPauseRoute(attempt: music, bindingID: binding, revision: revision)
        session.beginSession(generation: music.id)
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)))
        XCTAssertFalse(center.previousTrackCommand.isEnabled)
        XCTAssertFalse(center.nextTrackCommand.isEnabled)
        XCTAssertFalse(center.changePlaybackPositionCommand.isEnabled)
        session.update(snapshot: .init(owner: "music", resourceID: "song", phase: .paused),
            attempt: music, metadata: nil, rate: 0, canSeek: true, navigation: navigation, playPause: playPause)
        XCTAssertEqual(targets.activeCommands,
            Set((transport + [center.previousTrackCommand, center.nextTrackCommand]).map(ObjectIdentifier.init)))
        XCTAssertTrue(center.previousTrackCommand.isEnabled)
        XCTAssertTrue(center.nextTrackCommand.isEnabled)
        XCTAssertTrue(center.changePlaybackPositionCommand.isEnabled)
        let musicTargets = targets.activeIDs

        let film = PlayAttempt(id: UUID(), owner: "film", resourceID: "episode")
        session.beginSession(generation: film.id)
        XCTAssertTrue(musicTargets.isSubset(of: Set(targets.removed)))
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)))
        session.updateNavigation(navigation)
        session.updatePlayPause(playPause)
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)), "Foreign routes cannot install targets")
        XCTAssertFalse(center.previousTrackCommand.isEnabled)
        XCTAssertFalse(center.nextTrackCommand.isEnabled)
        XCTAssertFalse(center.changePlaybackPositionCommand.isEnabled)
        session.endSession()
        XCTAssertTrue(targets.activeIDs.isEmpty)
        XCTAssertEqual(targets.removed.count, targets.registered.count)
        XCTAssertEqual(Set(targets.removed).count, targets.removed.count)
        for command in transport + [center.previousTrackCommand, center.nextTrackCommand] {
            XCTAssertFalse(command.isEnabled)
        }
        let removals = targets.removed
        session.endSession()
        XCTAssertEqual(targets.removed, removals)
    }

    private func postInterruption(_ type: AVAudioSession.InterruptionType, shouldResume: Bool = false,
                                  to center: NotificationCenter) {
        center.post(name: AVAudioSession.interruptionNotification, object: AVAudioSession.sharedInstance(), userInfo: [
            AVAudioSessionInterruptionTypeKey: NSNumber(value: type.rawValue),
            AVAudioSessionInterruptionOptionKey: NSNumber(value: shouldResume ? AVAudioSession.InterruptionOptions.shouldResume.rawValue : 0)
        ])
    }

    private func postOutputUnavailable(to center: NotificationCenter) {
        center.post(name: AVAudioSession.routeChangeNotification, object: AVAudioSession.sharedInstance(), userInfo: [
            AVAudioSessionRouteChangeReasonKey: NSNumber(value: AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue)
        ])
    }

    private func silentFile() throws -> URL {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func number<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        let length: UInt32 = 30 * 8000 * 2
        text("RIFF"); number(36 + length); text("WAVEfmt "); number(UInt32(16))
        number(UInt16(1)); number(UInt16(1)); number(UInt32(8000)); number(UInt32(16000))
        number(UInt16(2)); number(UInt16(16)); text("data"); number(length)
        data.append(Data(count: Int(length))); try data.write(to: file)
        return file
    }
}

private final class SystemSessionBytes: PlaybackByteSource, @unchecked Sendable {
    let id = UUID()
    let data: Data
    private let lock = NSLock()
    private var reads = 0, retired = false
    init(data: Data) { self.data = data }
    var progress: PlaybackCacheProgress { .init(received: Int64(data.count), length: Int64(data.count), complete: true) }
    var readCount: Int { lock.withLock { reads } }
    var isRetired: Bool { lock.withLock { retired } }
    func information() async throws -> PlaybackByteInformation { .init(length: Int64(data.count), mime: "audio/wav") }
    func read(offset: Int64, count: Int) async throws -> Data {
        try lock.withLock {
            if retired { throw CancellationError() }
            reads += 1
            return data.subdata(in: Int(offset)..<min(data.count, Int(offset) + count))
        }
    }
    func updates() -> AsyncStream<PlaybackCacheProgress> { AsyncStream { $0.yield(progress); $0.finish() } }
    func retire() { lock.withLock { retired = true } }
}

/// Initialized on main, then transferred to one release site. The lock protects
/// ownership transfer; releasing outside the lock permits reentrant cleanup.
private final class FinalReleaseBox<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value?

    init(_ value: Value) { self.value = value }

    func release() {
        lock.lock()
        let current = value
        value = nil
        lock.unlock()
        withExtendedLifetime(current) {}
    }
}

@MainActor
private final class ControllerReleaseProbe {
    private var calls = 0
    func touch() { calls += 1 }
    nonisolated deinit { XCTAssertTrue(Thread.isMainThread, "Owned captures must be released on MainActor") }
}

private final class ReleaseNotificationCenter: NotificationCenter, @unchecked Sendable {
    private let lock = NSLock()
    private var active: Set<ObjectIdentifier> = []
    private var added = 0, removed = 0

    var activeCount: Int { lock.lock(); defer { lock.unlock() }; return active.count }
    var registrationCount: Int { lock.lock(); defer { lock.unlock() }; return added }
    var removalCount: Int { lock.lock(); defer { lock.unlock() }; return removed }

    override func addObserver(forName name: NSNotification.Name?, object: Any?, queue: OperationQueue?,
                              using block: @escaping @Sendable (Notification) -> Void) -> NSObjectProtocol {
        let token = super.addObserver(forName: name, object: object, queue: queue, using: block)
        lock.lock()
        active.insert(ObjectIdentifier(token))
        added += 1
        lock.unlock()
        return token
    }

    override func removeObserver(_ observer: Any) {
        super.removeObserver(observer)
        lock.lock()
        active.remove(ObjectIdentifier(observer as AnyObject))
        removed += 1
        lock.unlock()
    }
}

@MainActor
private final class HeldSystemHLSStartup {
    private var completion: (@Sendable (Result<URL, PlaybackNetworkError>) -> Void)?
    var isWaiting: Bool { completion != nil }
    func start(_ route: HLSLoopbackRoute, completion: @escaping @Sendable (Result<URL, PlaybackNetworkError>) -> Void) {
        self.completion = completion
    }
    func complete(with file: URL) throws {
        let callback = try XCTUnwrap(completion)
        completion = nil
        callback(.success(file))
    }
}

@MainActor
private final class SystemRemoteTargetRecorder {
    private(set) var registered: [MPRemoteCommand] = []
    private(set) var removed: [Int] = []
    var activeIDs: Set<Int> { Set(registered.indices).subtracting(removed) }
    var activeCommands: Set<ObjectIdentifier> { Set(activeIDs.map { ObjectIdentifier(registered[$0]) }) }
    func register(_ command: MPRemoteCommand, handler: @escaping PlaybackSystemSession.CommandHandler) -> (@MainActor () -> Void) {
        let id = registered.count
        registered.append(command)
        let target = command.addTarget(handler: handler)
        return { [self] in
            command.removeTarget(target)
            removed.append(id)
        }
    }
}

private actor SystemOutputWait {
    private let entered: @Sendable () -> Void
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        guard !released else { return }
        await withCheckedContinuation { continuation = $0; entered() }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

/// Real controller/source admission with only the source receipts held. The
/// local media and player preparation remain AVFoundation, not a fake player.
private actor SystemOutputMusicService: MusicServing, MusicSourcePlaybackServing {
    let file: URL
    private let entries: [MusicQueueEntry] = ["first", "second"].map {
        .init(id: $0, track: .init(ref: .init(kind: .queue, id: $0), songId: $0, title: $0,
            duration: 30, sources: [.init(sourceKey: "synthetic", platform: "wy", nativeId: $0)]))
    }
    private var index = 0, revision = 1
    private var position = 0.0
    private var resolution: SystemOutputWait?, activation: SystemOutputWait?
    init(file: URL) { self.file = file }
    func holdResolution(_ gate: SystemOutputWait) { resolution = gate }
    func holdActivation(_ gate: SystemOutputWait) { activation = gate }
    func state() -> MusicPlaybackState {
        .init(revision: revision, mode: .loop, currentId: entries[index].id, total: entries.count,
            position: position, canNext: index == 0, canPrevious: index == 1)
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: entries[index].id, entries: entries) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult {
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        index = direction == .next ? 1 : 0; revision += 1; position = 0
        return .init(state: state(), selectedId: entries[index].id)
    }
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) throws -> any MusicSourcePlaybackSession {
        guard occurrenceID == entries[index].id, expectedQueueRevision == revision else { throw MusicServiceError.staleResult }
        return SystemOutputMusicSession(service: self, occurrence: occurrenceID)
    }
    func preparedSource(_ occurrence: String) async -> MusicPreparedSource {
        let gate = resolution; resolution = nil
        await gate?.wait() // Intentionally returns a receipt even after cancellation.
        let selection = MusicPlaybackSelection(installationId: "official:wy", generation: 1,
            siteId: "wy", sourceKey: "synthetic", quality: "128k")
        let id = UUID().uuidString
        return .init(candidate: .init(attemptId: id, occurrenceId: occurrence, resourceId: "music:" + id,
            url: file, title: occurrence, startPosition: position, requestedQuality: "128k",
            resolvedSelection: selection, delivery: .init(quality: "128k", evidence: .officialResponse, duration: 30)),
            selection: selection)
    }
    func activateSource(attemptID: String) async throws {
        let gate = activation; activation = nil
        await gate?.wait()
    }
    func discardSource(attemptID: String) {}
    func sourceConfigurationChanged() {}
    func inspectSources(for reference: MusicReference) throws -> any MusicSourceInspectionSession { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { position = fact.position; return true }
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot { .init(occurrenceId: occurrenceId, state: .none, lines: []) }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor SystemOutputMusicSession: MusicSourcePlaybackSession {
    let service: SystemOutputMusicService
    let occurrence: String
    init(service: SystemOutputMusicService, occurrence: String) { self.service = service; self.occurrence = occurrence }
    func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource { await service.preparedSource(occurrence) }
    func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource { await service.preparedSource(occurrence) }
    func beginSelection() {}
    func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {}
    func cancelInspection() {}
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> { .init { $0.finish() } }
    func loadMore(refresh: Bool) throws -> MusicResolvedSourcesSnapshot { throw MusicServiceError.unsupported }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) throws -> MusicPlaybackSelection { throw MusicServiceError.unsupported }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) throws { throw MusicServiceError.unsupported }
    func close() {}
}
// The player remains real; only the blocking AVAudioSession boundary is held.
@MainActor
extension PlaybackSystemSessionTests {
    func testOffMainControllerReleaseDuringActivationRejectsLatePlayAndReleasesAudio() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let gate = SystemAudioGate(entered: expectation(description: "Activation held during final release"))
        defer { gate.open() }
        let audioReleased = expectation(description: "Late activation followed by actual system release")
        let probe = SystemAudioProbe(activate: { _ in try gate.wait() },
                                     deactivate: { _ in audioReleased.fulfill() })
        let notifications = ReleaseNotificationCenter()
        var controller: PlaybackController? = PlaybackController(audioSessionActions: probe.actions,
            systemNotifications: notifications)
        weak var weakController = controller
        let player = try XCTUnwrap(controller).player
        let lease = try XCTUnwrap(controller).bind(owner: "release-activation") { event in
            if event.kind == .played { XCTFail("A released controller cannot start after late activation") }
        }
        let attempt = try XCTUnwrap(controller).playAttempt(resource: .init(id: "release-activation",
            url: file, title: "Held activation release"), owner: "release-activation")
        _ = try XCTUnwrap(controller).setVolume(0, attempt: attempt)
        await fulfillment(of: [gate.entered], timeout: 5)
        XCTAssertEqual(player.rate, 0)
        let box = FinalReleaseBox(try XCTUnwrap(controller))
        controller = nil
        let cleaned = expectation(description: "Controller cleanup submitted its audio release")
        releaseOffMain(box, then: cleaned)
        XCTAssertNil(weakController)
        await fulfillment(of: [cleaned], timeout: 3)
        XCTAssertTrue(lease.isCancelled)
        XCTAssertNil(player.currentItem)
        XCTAssertEqual(notifications.activeCount, 0)
        XCTAssertEqual(probe.counts.deactivations, 0, "Release must serialize behind the held activation")
        gate.open()
        await fulfillment(of: [audioReleased], timeout: 3)
        XCTAssertEqual(probe.counts.activations, 1)
        XCTAssertEqual(probe.counts.deactivations, 1)
        XCTAssertEqual(player.rate, 0)
        XCTAssertNil(player.currentItem)
        XCTAssertEqual(notifications.removalCount, notifications.registrationCount)
        lease.cancel()
    }

    func testBeginSessionCannotReuseAudioWhileOldReleaseIsQueued() async throws {
        let gate = SystemAudioGate(entered: expectation(description: "old session release held"))
        defer { gate.open() }
        let probe = SystemAudioProbe(deactivate: { number in if number == 1 { try gate.wait() } })
        let session = PlaybackSystemSession(audioActions: probe.actions,
            onEvent: { _, _ in }, onCommand: { _, _, _ in true }, onNavigation: { _ in true })
        defer { session.invalidate() }
        session.beginSession(generation: UUID())
        let first = await session.activate().value
        XCTAssertNil(first); XCTAssertTrue(session.isActive)
        let release = session.endSession()
        session.beginSession(generation: UUID())
        XCTAssertFalse(session.isActive, "Beginning a new owner cannot restore the retired owner's active flag")
        let next = session.activate()
        await fulfillment(of: [gate.entered], timeout: 3)
        XCTAssertFalse(session.isActive)
        XCTAssertEqual(probe.counts.activations, 1)
        gate.open()
        let oldFailure = await release.value, newFailure = await next.value
        XCTAssertNil(oldFailure); XCTAssertNil(newFailure)
        XCTAssertTrue(session.isActive)
        XCTAssertNil(session.lastError)
        XCTAssertEqual(probe.counts.activations, 2)
        let final = await session.endSession().value
        XCTAssertNil(final)
    }

    func testSlowDeactivationPublishesActualPauseAndResumeWaitsForActivation() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let releaseGate = SystemAudioGate(entered: expectation(description: "slow release"))
        let activationGate = SystemAudioGate(entered: expectation(description: "resume activation"))
        defer { releaseGate.open(); activationGate.open() }
        let probe = SystemAudioProbe(activate: { number in
            if number == 2 { try activationGate.wait() }
        }, deactivate: { number in
            if number == 1 { try releaseGate.wait() }
        })
        let controller = PlaybackController(audioSessionActions: probe.actions)
        let first = expectation(description: "initial playback"), resumed = expectation(description: "resumed playback")
        var plays = 0, pauses: [PlaybackEvent] = []
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .played {
                plays += 1
                if plays == 1 { first.fulfill() } else if plays == 2 { resumed.fulfill() }
            }
            if event.kind == .paused { pauses.append(event) }
        }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "slow", url: file, title: "Slow"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [first], timeout: 5)
        controller.pause()
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(pauses.last?.snapshot.phase, .paused, "Actual pause is delivered before returning to the caller")
        await fulfillment(of: [releaseGate.entered], timeout: 3)
        controller.resume()
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(probe.counts.activations, 1, "Resume cannot overlap the held release")
        releaseGate.open()
        await fulfillment(of: [activationGate.entered], timeout: 3)
        XCTAssertTrue(controller.setPreferredRate(1.5, attempt: attempt))
        XCTAssertEqual(controller.player.rate, 0, "Rate changes must obey the same audio admission")
        XCTAssertNotEqual(controller.snapshot.phase, .playing)
        XCTAssertEqual(plays, 1)
        activationGate.open()
        await fulfillment(of: [resumed], timeout: 5)
        XCTAssertGreaterThan(controller.player.rate, 0)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertNil(controller.lastError)
    }

    func testOldActivationFailureCannotFailOrStartPausedSuccessorAndExplicitPlayRetries() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let oldGate = SystemAudioGate(entered: expectation(description: "old activation"))
        let newGate = SystemAudioGate(entered: expectation(description: "new activation"))
        defer { oldGate.open(); newGate.open() }
        let probe = SystemAudioProbe(activate: { number in
            if number == 1 { try oldGate.wait(); throw NativePlaybackError.audioSessionUnavailable }
            if number == 2 { try newGate.wait() }
        })
        let controller = PlaybackController(audioSessionActions: probe.actions)
        let ready = expectation(description: "successor ready"), playing = expectation(description: "successor played")
        var played: [PlayAttempt] = [], failed: [PlayAttempt] = []
        let music = try controller.bind(owner: "music") { event in
            if event.kind == .played { played.append(event.attempt) }
            if event.kind == .failed { failed.append(event.attempt) }
        }
        let film = try controller.bind(owner: "film") { event in
            if event.kind == .prepared { ready.fulfill() }
            if event.kind == .played { played.append(event.attempt); playing.fulfill() }
            if event.kind == .failed { failed.append(event.attempt) }
        }
        defer { controller.stop(); music.cancel(); film.cancel() }
        let old = controller.playAttempt(resource: try .init(id: "old", url: file, title: "Old"), owner: "music")
        await fulfillment(of: [oldGate.entered], timeout: 5)
        let next = controller.playAttempt(resource: try .init(id: "new", url: file, title: "New"), owner: "film", autoplay: false)
        _ = controller.setVolume(0, attempt: next)
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertFalse(controller.resume(attempt: old))
        XCTAssertEqual(controller.snapshot.phase, .paused)
        oldGate.open()
        XCTAssertTrue(controller.resume(attempt: next))
        await fulfillment(of: [newGate.entered], timeout: 3)
        XCTAssertTrue(played.isEmpty); XCTAssertTrue(failed.isEmpty)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertNil(controller.lastError, "A retired owner's activation error is not the successor's error")
        newGate.open()
        await fulfillment(of: [playing], timeout: 5)
        XCTAssertEqual(played, [next]); XCTAssertTrue(failed.isEmpty)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertNil(controller.lastError)
    }

    func testPauseResumePauseRejectsBothOldActivationsUntilExplicitPlay() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let firstGate = SystemAudioGate(entered: expectation(description: "first activation"))
        let secondGate = SystemAudioGate(entered: expectation(description: "second activation"))
        let drained = expectation(description: "both cancelled activations released")
        defer { firstGate.open(); secondGate.open() }
        let probe = SystemAudioProbe(activate: { number in
            if number == 1 { try firstGate.wait() }
            if number == 2 { try secondGate.wait() }
        }, deactivate: { number in if number == 2 { drained.fulfill() } })
        let controller = PlaybackController(audioSessionActions: probe.actions)
        let playing = expectation(description: "explicit final play")
        var played: [PlayAttempt] = []
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .played { played.append(event.attempt); playing.fulfill() }
        }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "rapid", url: file, title: "Rapid"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [firstGate.entered], timeout: 5)
        controller.pause(); controller.resume()
        firstGate.open()
        await fulfillment(of: [secondGate.entered], timeout: 3)
        controller.pause()
        XCTAssertEqual(controller.snapshot.phase, .paused)
        secondGate.open()
        await fulfillment(of: [drained], timeout: 3)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertFalse(controller.wantsPlayback); XCTAssertTrue(played.isEmpty)
        controller.resume()
        await fulfillment(of: [playing], timeout: 5)
        XCTAssertEqual(played, [attempt])
    }

    func testStopAndInterruptionInvalidatePendingActivation() async throws {
        for stopped in [true, false] {
            let file = try silentFile()
            defer { try? FileManager.default.removeItem(at: file) }
            let gate = SystemAudioGate(entered: expectation(description: "pending activation"))
            defer { gate.open() }
            let released = expectation(description: "invalid activation released")
            let probe = SystemAudioProbe(activate: { _ in try gate.wait() }, deactivate: { _ in released.fulfill() })
            let notifications = NotificationCenter()
            let controller = PlaybackController(audioSessionActions: probe.actions, systemNotifications: notifications)
            var played: [PlayAttempt] = []
            let lease = try controller.bind(owner: "music") { if $0.kind == .played { played.append($0.attempt) } }
            defer { controller.stop(); lease.cancel() }
            _ = controller.playAttempt(resource: try .init(id: "cancel", url: file, title: "Cancel"), owner: "music")
            await fulfillment(of: [gate.entered], timeout: 5)
            if stopped { controller.stop(owner: "music") }
            else { postInterruption(.began, to: notifications) }
            gate.open()
            await fulfillment(of: [released], timeout: 3)
            XCTAssertTrue(played.isEmpty)
            XCTAssertEqual(controller.player.rate, 0)
            XCTAssertEqual(controller.snapshot.phase, stopped ? .idle : .paused)
            if stopped { XCTAssertNil(controller.currentAttempt) }
        }
    }

    func testActivationFailurePreservesAttemptMediaAndPositionForExplicitRetry() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let probe = SystemAudioProbe(activate: { number in
            if number == 1 { throw NativePlaybackError.audioSessionUnavailable }
        })
        let controller = PlaybackController(audioSessionActions: probe.actions)
        let rejected = expectation(description: "current activation rejected"), playing = expectation(description: "same media retry played")
        var failures: [PlayAttempt] = [], plays: [PlayAttempt] = []
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .failed { failures.append(event.attempt) }
            if event.kind == .paused, event.error as? NativePlaybackError == .audioSessionUnavailable { rejected.fulfill() }
            if event.kind == .played { plays.append(event.attempt); playing.fulfill() }
        }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "retry", url: file, title: "Retry"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [rejected], timeout: 5)
        XCTAssertTrue(failures.isEmpty); XCTAssertTrue(plays.isEmpty)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.lastError as? NativePlaybackError, .audioSessionUnavailable)
        let item = try XCTUnwrap(controller.player.currentItem), position = controller.snapshot.position
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [playing], timeout: 5)
        XCTAssertEqual(plays, [attempt]); XCTAssertTrue(failures.isEmpty)
        XCTAssertTrue(controller.player.currentItem === item)
        XCTAssertGreaterThanOrEqual(controller.snapshot.position, position)
        XCTAssertNil(controller.lastError)
        XCTAssertEqual(probe.counts.activations, 2)
    }

    func testReleaseFailureIsPublishedOnPausedFactAndExplicitPauseRetries() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let failure = expectation(description: "release failure"), recovered = expectation(description: "release retried")
        let probe = SystemAudioProbe(deactivate: { number in
            if number == 1 { throw NativePlaybackError.audioSessionDeactivationFailed }
        })
        let controller = PlaybackController(audioSessionActions: probe.actions)
        let playing = expectation(description: "playing before release failure")
        let lease = try controller.bind(owner: "music") { if $0.kind == .played { playing.fulfill() } }
        defer { controller.stop(); lease.cancel() }
        var sawFailure = false
        let observation = controller.$lastError.sink { error in
            if error as? NativePlaybackError == .audioSessionDeactivationFailed, !sawFailure {
                sawFailure = true; failure.fulfill()
            } else if error == nil, sawFailure { recovered.fulfill() }
        }
        defer { observation.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "release-error", url: file, title: "Error"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [playing], timeout: 5)
        controller.pause()
        await fulfillment(of: [failure], timeout: 3)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.player.rate, 0)
        controller.pause()
        await fulfillment(of: [recovered], timeout: 3)
        XCTAssertGreaterThanOrEqual(probe.counts.deactivations, 2)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertFalse(controller.wantsPlayback)
    }
}

private final class SystemAudioGate: @unchecked Sendable {
    let entered: XCTestExpectation
    private let semaphore = DispatchSemaphore(value: 0)
    init(entered: XCTestExpectation) { self.entered = entered }
    func wait() throws {
        entered.fulfill()
        guard semaphore.wait(timeout: .now() + 10) == .success else { throw NativePlaybackError.audioSessionUnavailable }
    }
    func open() { semaphore.signal() }
}

private final class SystemAudioProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var activations = 0, deactivations = 0
    private let onActivate: @Sendable (Int) throws -> Void
    private let onDeactivate: @Sendable (Int) throws -> Void
    init(activate: @escaping @Sendable (Int) throws -> Void = { _ in },
         deactivate: @escaping @Sendable (Int) throws -> Void = { _ in }) {
        onActivate = activate; onDeactivate = deactivate
    }
    var counts: (activations: Int, deactivations: Int) {
        lock.lock(); defer { lock.unlock() }; return (activations, deactivations)
    }
    var actions: PlaybackAudioSessionActions {
        .init(activate: { [self] in
            XCTAssertFalse(Thread.isMainThread)
            lock.lock(); activations += 1; let number = activations; lock.unlock()
            try onActivate(number)
        }, deactivate: { [self] in
            XCTAssertFalse(Thread.isMainThread)
            lock.lock(); deactivations += 1; let number = deactivations; lock.unlock()
            try onDeactivate(number)
        })
    }
}
#endif

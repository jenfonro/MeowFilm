import XCTest
import UIKit
import AVFoundation
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature

/// Real decoding, first-frame presentation and media ownership. Only the
/// fallible system audio operation is controlled; this is not device audio proof.
@MainActor
final class MeowfilmVideoAudioAdmissionHostTests: XCTestCase {
    func testAudioActivationFailureAfterPictureReadinessPreservesMusicUntilAnExplicitRetry() async throws {
        let audio = AudioAdmissionActions()
        audio.failActivation = true
        let controller = PlaybackController(audioSessionActions: audio.actions)
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        var events: [PlaybackVideoEvent] = []
        var handoffCaptures = 0, handoffs = 0
        let film = try controller.bindVideo(owner: "film", willCommit: {
            handoffCaptures += 1
            return { handoffs += 1 }
        }) { events.append($0) }
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
        XCTAssertEqual(audio.activations, 0, "A paused donor has not acquired audio focus")

        var rejectedReleases = 0
        let rejected = try PlaybackVideoMediaLease(attemptID: "audio-rejected",
            input: .init(resourceID: "audio-rejected", url: movie, title: "Synthetic candidate")) {
                rejectedReleases += 1
            }
        do {
            _ = try await film.play(rejected)
            XCTFail("System activation must reject an otherwise prepared video")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        XCTAssertEqual(audio.activations, 1)
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
        XCTAssertEqual(audio.deactivations, 0, "A failed acquire cannot release someone else's system focus")
        XCTAssertEqual(handoffCaptures, 0)
        XCTAssertEqual(handoffs, 0, "Audio admission failure cannot retire music business work")

        audio.failActivation = false
        var acceptedReleases = 0
        let accepted = try PlaybackVideoMediaLease(attemptID: "audio-accepted",
            input: .init(resourceID: "audio-accepted", url: movie, title: "Synthetic retry")) {
                acceptedReleases += 1
            }
        let current = try await film.play(accepted)
        XCTAssertEqual(audio.activations, 2)
        XCTAssertEqual(controller.currentAttempt, current)
        XCTAssertEqual(current.owner, "film")
        XCTAssertNil(controller.player.currentItem)
        XCTAssertEqual(events.filter { $0.kind == .started }.map(\.attemptID), ["audio-accepted"])
        XCTAssertTrue(controller.wantsPlayback)
        XCTAssertEqual(acceptedReleases, 0)
        XCTAssertEqual(handoffCaptures, 1)
        XCTAssertEqual(handoffs, 1)
        try await film.close()
        XCTAssertEqual(acceptedReleases, 1)
        XCTAssertEqual(audio.deactivations, 1)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(controller.wantsPlayback)
    }

    func testSlowVideoActivationCancellationPreservesDonorAndNeverCommitsLate() async throws {
        let gate = AudioAdmissionGate(entered: expectation(description: "video activation blocked"))
        defer { gate.open() }
        let audio = AudioAdmissionActions()
        audio.beforeActivation = { _ in try gate.wait() }
        let controller = PlaybackController(audioSessionActions: audio.actions)
        var handoffs = 0, events: [PlaybackVideoEvent] = []
        let film = try controller.bindVideo(owner: "film", willCommit: { handoffs += 1; return nil }) { events.append($0) }
        let host = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock { try await host.close() }
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let donor = controller.playAttempt(resource: try .init(id: "donor", url: movie, title: "Donor"),
            owner: "music", autoplay: false)
        let item = try XCTUnwrap(controller.player.currentItem)
        let drained = expectation(description: "cancelled candidate decoded resources drained")
        let media = try PlaybackVideoMediaLease(attemptID: "held-audio",
            input: .init(resourceID: "held-audio", url: movie, title: "Held video")) { drained.fulfill() }
        let work = Task { try await film.play(media) }
        defer { work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        XCTAssertEqual(controller.currentAttempt, donor)
        XCTAssertTrue(controller.player.currentItem === item)
        XCTAssertFalse(controller.wantsPlayback)
        var closed = false
        let close = Task { try await film.close(); closed = true }
        await fulfillment(of: [drained], timeout: 5)
        XCTAssertFalse(closed, "Close must join the pending acquisition and its release")
        gate.open()
        try await close.value
        do { _ = try await work.value; XCTFail("A cancelled audio admission cannot commit") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .staleAttempt) }
        XCTAssertEqual(controller.currentAttempt, donor)
        XCTAssertTrue(controller.player.currentItem === item)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(handoffs, 0)
        XCTAssertFalse(events.contains { $0.kind == .started })
        XCTAssertEqual(audio.activations, 1)
        XCTAssertEqual(audio.deactivations, 1)
    }

    func testCancelPendingAndCancelledPlayJoinSlowAudioAcquisitionAndRelease() async throws {
        for cancelTask in [false, true] {
            let gate = AudioAdmissionGate(entered: expectation(description: "pending audio acquisition"))
            defer { gate.open() }
            let audio = AudioAdmissionActions()
            audio.beforeActivation = { _ in try gate.wait() }
            let controller = PlaybackController(audioSessionActions: audio.actions)
            let film = try controller.bindVideo(owner: "film") { event in
                XCTAssertNotEqual(event.kind, .started)
            }
            let host = try AudioAdmissionHost(controller: controller, film: film)
            addTeardownBlock { try await host.close() }
            let drained = expectation(description: "cancelled receipt drained")
            let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
            let media = try PlaybackVideoMediaLease(attemptID: "cancel-audio",
                input: .init(resourceID: "cancel-audio", url: movie, title: "Cancelled video")) { drained.fulfill() }
            var returned = false
            let work = Task { () -> PlaybackVideoError? in
                defer { returned = true }
                do { _ = try await film.play(media); return nil }
                catch { return error as? PlaybackVideoError }
            }
            defer { work.cancel() }
            await fulfillment(of: [gate.entered], timeout: 10)
            if cancelTask { work.cancel() }
            var cancelled = false
            let cancelling = Task { try await film.cancelPending(); cancelled = true }
            await fulfillment(of: [drained], timeout: 5)
            XCTAssertFalse(returned); XCTAssertFalse(cancelled)
            gate.open()
            try await cancelling.value
            let failure = await work.value
            XCTAssertTrue(failure == .cancelled || failure == .staleAttempt)
            XCTAssertEqual(audio.activations, 1)
            XCTAssertEqual(audio.deactivations, 1)
            XCTAssertNil(controller.currentAttempt)
            try await film.close()
        }
    }

    func testMediaResetDuringIdleVideoAudioAdmissionRejectsLateCommitAndAllowsExplicitRetry() async throws {
        let gate = AudioAdmissionGate(entered: expectation(description: "idle audio admission blocked"))
        defer { gate.open() }
        let audio = AudioAdmissionActions()
        audio.beforeActivation = { number in if number == 1 { try gate.wait() } }
        let notifications = NotificationCenter()
        let controller = PlaybackController(audioSessionActions: audio.actions, systemNotifications: notifications)
        var started: [String] = []
        let film = try controller.bindVideo(owner: "film") { event in
            if event.kind == .started { started.append(event.attemptID) }
        }
        let host = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock { try await host.close() }
        let work = Task { try await film.play(movieReceipt("reset-during-admission")) }
        defer { work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        notifications.post(name: AVAudioSession.mediaServicesWereResetNotification, object: AVAudioSession.sharedInstance())
        XCTAssertNil(controller.currentAttempt)
        gate.open()
        do { _ = try await work.value; XCTFail("Reset invalidates an admission even before a native attempt exists") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .staleAttempt) }
        XCTAssertTrue(started.isEmpty)
        let next = try await film.play(movieReceipt("explicit-after-reset"))
        XCTAssertEqual(film.currentAttempt, next)
        XCTAssertEqual(started, ["explicit-after-reset"])
        XCTAssertEqual(audio.activations, 2)
        try await film.close()
    }

    func testSlowFailedVideoReleaseBelongsToOldLeaseWhileNewMusicStarts() async throws {
        let gate = AudioAdmissionGate(entered: expectation(description: "video release blocked"))
        defer { gate.open() }
        let audio = AudioAdmissionActions()
        audio.failDeactivation = true
        audio.beforeDeactivation = { number in if number == 1 { try? gate.wait() } }
        let controller = PlaybackController(audioSessionActions: audio.actions)
        let film = try controller.bindVideo(owner: "film") { _ in }
        let host = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock {
            audio.failDeactivation = false
            do { try await host.close() } catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        }
        _ = try await film.play(movieReceipt("slow-video-release"))
        var closed = false
        let close = Task { () -> PlaybackVideoError? in
            defer { closed = true }
            do { try await film.close(); return nil } catch { return error as? PlaybackVideoError }
        }
        await fulfillment(of: [gate.entered], timeout: 5)
        XCTAssertFalse(closed)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertNil(film.currentAttempt)
        let musicReady = expectation(description: "new music audible")
        var played: [PlayAttempt] = []
        let music = try controller.bind(owner: "music") { event in
            if event.kind == .played { played.append(event.attempt); musicReady.fulfill() }
        }
        defer { controller.stop(); music.cancel() }
        let file = try musicFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let next = controller.playAttempt(resource: try .init(id: "next", url: file, title: "Next"), owner: "music")
        _ = controller.setVolume(0, attempt: next)
        XCTAssertEqual(controller.player.rate, 0)
        gate.open()
        let releaseError = await close.value
        XCTAssertEqual(releaseError, .outputFailed)
        await fulfillment(of: [musicReady], timeout: 10)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertEqual(played, [next])
        XCTAssertNil(controller.lastError, "An old release failure cannot overwrite the new owner's success")
        let before = audio.deactivations
        do { try await film.close(); XCTFail("Old release failure remains observable") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        XCTAssertEqual(audio.deactivations, before)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertTrue(controller.wantsPlayback)
    }

    func testSuccessfulVideoCommitRetiresPendingOnlyMusicAndRejectsLateResolutionWithoutStoppingVideo() async throws {
        let file = try musicFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}))
        let service = VideoHandoffMusicService(file: file)
        let music = MusicViewModel(service: service, playback: controller)
        let binding = try bindMusic(music, to: controller)
        defer { binding.cancel(); controller.stop() }
        await music.load()
        music.lastSelection = VideoHandoffMusicService.selection
        let resolving = VideoHandoffMusicGate(entered: expectation(description: "Music source held without a native attempt"))
        await service.holdResolution(resolving)
        let request = Task { await music.prepareCurrent() }
        defer { Task { await resolving.release() }; request.cancel() }
        await fulfillment(of: [resolving.entered], timeout: 5)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertTrue(music.isResolving)
        let queue = music.queue
        var prepared = false
        let film = try controller.bindVideo(owner: "film", willCommit: {
            XCTAssertTrue(prepared)
            XCTAssertTrue(music.isResolving)
            XCTAssertNil(controller.currentAttempt)
            return music.mediaHandoffCompletion()
        }) { event in if event.kind == .prepared { prepared = true } }
        let host = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock {
            do { try await host.close() }
            catch { await music.close(); throw error }
            await music.close()
        }
        let media = try movieReceipt("pending-only")
        let video = try await film.play(media, paused: true)
        XCTAssertEqual(controller.currentAttempt, video)
        XCTAssertNil(music.activeNativeAttemptID)
        XCTAssertFalse(music.isResolving)
        XCTAssertEqual(music.queue, queue)
        await resolving.release()
        await request.value
        await music.flushPlaybackFacts()
        XCTAssertEqual(controller.currentAttempt, video, "A late, non-cooperative source receipt cannot steal video")
        XCTAssertNil(controller.player.currentItem)
        XCTAssertNil(music.activeNativeAttemptID)
        XCTAssertEqual(music.phase, .idle)
        XCTAssertNil(music.error)
        let cancelled = await service.cancelledAttempts()
        XCTAssertEqual(cancelled.count, 1)
        try await film.close()
        XCTAssertNil(controller.currentAttempt, "Video close does not automatically resume music")

        await music.togglePlayback()
        await waitForMusic(controller, model: music)
        XCTAssertEqual(controller.currentAttempt?.owner, "music")
        XCTAssertEqual(controller.currentAttempt?.id, music.activeNativeAttemptID)
        XCTAssertEqual(music.queue, queue)
    }

    func testSuccessfulVideoCommitPersistsMusicExitAndRetiresDelayedNextWithoutTakingNewVideo() async throws {
        let file = try musicFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}))
        let service = VideoHandoffMusicService(file: file)
        let music = MusicViewModel(service: service, playback: controller)
        let binding = try bindMusic(music, to: controller)
        defer { binding.cancel(); controller.stop() }
        await music.load()
        music.lastSelection = VideoHandoffMusicService.selection
        await music.prepareCurrent()
        await waitForMusic(controller, model: music)
        let donor = try XCTUnwrap(controller.currentAttempt)
        let candidate = try XCTUnwrap(music.candidate)
        XCTAssertTrue(controller.setNowPlayingMetadata(.init(title: "Old music"), attempt: donor))
        let gate = VideoHandoffMusicGate(entered: expectation(description: "Old queue advance awaiting acknowledgement"))
        await service.holdAdvance(gate)
        let next = Task { await music.next() }
        defer { Task { await gate.release() }; next.cancel() }
        await fulfillment(of: [gate.entered], timeout: 5)
        let queue = music.queue
        let film = try controller.bindVideo(owner: "film", willCommit: {
            XCTAssertEqual(controller.currentAttempt, donor)
            return music.mediaHandoffCompletion()
        }) { _ in }
        let host = try AudioAdmissionHost(controller: controller, film: film)
        addTeardownBlock {
            do { try await host.close() }
            catch { await music.close(); throw error }
            await music.close()
        }
        let media = try movieReceipt("music-next")
        let video = try await film.play(media, paused: true)
        XCTAssertEqual(controller.currentAttempt, video)
        XCTAssertNil(music.activeNativeAttemptID)
        XCTAssertFalse(music.isChangingQueue)
        XCTAssertEqual(music.phase, .idle)
        await gate.release()
        await next.value
        await music.flushPlaybackFacts()
        XCTAssertEqual(controller.currentAttempt, video)
        XCTAssertEqual(music.queue, queue, "A late queue acknowledgement cannot overwrite the retired UI selection")
        XCTAssertNil(music.error)
        let facts = await service.recordedFacts()
        let exit = try XCTUnwrap(facts.last { $0.kind == .stopped })
        XCTAssertEqual(exit.attemptId, candidate.attemptId)
        XCTAssertEqual(exit.occurrenceId, candidate.occurrenceId)
        XCTAssertEqual(exit.resourceId, candidate.resourceId)
        XCTAssertEqual(exit.position, music.position, accuracy: 0.15)
        XCTAssertTrue(facts.contains { $0.kind == .playing && $0.attemptId == candidate.attemptId })
        XCTAssertFalse(controller.setNowPlayingMetadata(.init(title: "Late old music"), attempt: donor))
        XCTAssertEqual(controller.nowPlayingMetadata?.title, "Synthetic video")
        XCTAssertFalse(controller.systemCommand(.play, generation: donor.id))
        XCTAssertEqual(controller.currentAttempt, video)
        try await film.close()
        XCTAssertNil(controller.currentAttempt)
        XCTAssertNil(music.activeNativeAttemptID)
    }

    func testFailedOrCancelledVideoPreparationKeepsMusicBusinessIdentityAndOriginalIntent() async throws {
        for paused in [false, true] {
            let file = try musicFile()
            defer { try? FileManager.default.removeItem(at: file) }
            let controller = PlaybackController(audioSessionActions: .init(activate: {}, deactivate: {}))
            let service = VideoHandoffMusicService(file: file)
            let music = MusicViewModel(service: service, playback: controller)
            let binding = try bindMusic(music, to: controller)
            await music.load()
            music.lastSelection = VideoHandoffMusicService.selection
            await music.prepareCurrent()
            await waitForMusic(controller, model: music)
            let donor = try XCTUnwrap(controller.currentAttempt)
            if paused { XCTAssertTrue(controller.systemCommand(.pause, generation: donor.id)) }
            let item = controller.player.currentItem, candidate = music.candidate, selection = music.selectionID
            var handoffs = 0
            let film = try controller.bindVideo(owner: "film", willCommit: {
                handoffs += 1
                return music.mediaHandoffCompletion()
            }) { _ in }
            let host = try AudioAdmissionHost(controller: controller, film: film)
            do {
                let missing = try PlaybackVideoMediaLease(attemptID: "missing",
                    input: .init(resourceID: "missing", url: URL(fileURLWithPath: "/missing/synthetic.mp4"),
                                 title: "Missing video")) {}
                do { _ = try await film.play(missing); XCTFail("The missing input cannot commit") } catch {}
                let cancelled = try movieReceipt("cancelled")
                do {
                    _ = try await film.play(cancelled, waitForCommit: { throw CancellationError() })
                    XCTFail("A cancelled preparation cannot commit")
                } catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
                XCTAssertEqual(handoffs, 0)
                XCTAssertEqual(controller.currentAttempt, donor)
                XCTAssertTrue(controller.player.currentItem === item)
                XCTAssertEqual(controller.wantsPlayback, !paused)
                XCTAssertEqual(music.activeNativeAttemptID, donor.id)
                XCTAssertEqual(music.candidate?.attemptId, candidate?.attemptId)
                XCTAssertEqual(music.selectionID, selection)
                XCTAssertNil(music.error)
                try await film.close()
                XCTAssertEqual(controller.currentAttempt, donor)
                XCTAssertEqual(controller.wantsPlayback, !paused)
            } catch {
                try? await host.close()
                await music.close()
                binding.cancel()
                throw error
            }
            try await host.close()
            await music.close()
            binding.cancel()
        }
    }

    private func bindMusic(_ music: MusicViewModel, to controller: PlaybackController) throws -> PlaybackOwnerLease {
        try controller.bind(owner: "music", onPlayPause: { [weak music] attempt, command in
            music?.acceptPlayPause(command, attempt: attempt) ?? false
        }, onOutputUnavailable: { [weak music] in music?.onOutputUnavailable() }) { [weak music] event in
            music?.onPlayback(event)
        }
    }

    private func waitForMusic(_ controller: PlaybackController, model: MusicViewModel) async {
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated {
                controller.currentAttempt?.owner == "music" && controller.snapshot.phase == .playing &&
                    model.phase == .playing
            }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
        XCTAssertEqual(controller.snapshot.phase, .playing)
        XCTAssertEqual(model.phase, .playing)
    }

    private func movieReceipt(_ id: String) throws -> PlaybackVideoMediaLease {
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        return try .init(attemptID: id,
            input: .init(resourceID: id, url: movie, title: "Synthetic video")) {}
    }

    private func musicFile() throws -> URL {
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

    func testFailedAudioReleaseRemainsFailedAfterEmptyCloseSystemRecoveryOrNewMusic() async throws {
        for next in ["still-failing", "system-recovered", "music"] {
            let audio = AudioAdmissionActions()
            audio.failDeactivation = true
            let controller = PlaybackController(audioSessionActions: audio.actions)
            let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
            let film = try controller.bindVideo(owner: "film") { _ in }
            let fixture = try AudioAdmissionHost(controller: controller, film: film)
            addTeardownBlock {
                audio.failDeactivation = false
                do { try await fixture.close() }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            var releases = 0
            let media = try PlaybackVideoMediaLease(attemptID: "audio-release",
                input: .init(resourceID: "audio-release", url: movie, title: "Synthetic video")) {
                    releases += 1
                }
            _ = try await film.play(media)
            XCTAssertEqual(audio.activations, 1)
            do { try await film.close(); XCTFail("failed system release cannot acknowledge close") }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            XCTAssertEqual(audio.deactivations, 1)
            XCTAssertEqual(releases, 1)
            XCTAssertNil(film.currentAttempt)
            XCTAssertNil(controller.currentAttempt)

            var music: PlayAttempt?
            if next != "still-failing" { audio.failDeactivation = false }
            if next == "system-recovered" {
                controller.stop()
                let released = expectation(for: NSPredicate { _, _ in
                    MainActor.assumeIsolated { audio.deactivations == 2 && controller.lastError == nil }
                }, evaluatedWith: nil)
                await fulfillment(of: [released], timeout: 5)
                XCTAssertEqual(audio.deactivations, 2, "the actual controller can release system focus later")
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
                XCTAssertEqual(audio.activations, 2)
            }
            let before = audio.deactivations
            for _ in 0..<2 {
                do { try await film.close(); XCTFail("the same failed close receipt cannot turn into success") }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            XCTAssertEqual(audio.deactivations, before, "an old lease must not retry against a new system owner")
            XCTAssertEqual(releases, 1)
            XCTAssertEqual(controller.currentAttempt, music)
            if music != nil { XCTAssertTrue(controller.wantsPlayback) }
            audio.failDeactivation = false
            do { try await fixture.close() }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        }
    }

    func testControllerFirstRetirementRecordsAudioReleaseFailureForTheExactOldLease() async throws {
        for trigger in ["stop", "failure", "music"] {
            let audio = AudioAdmissionActions()
            audio.failDeactivation = true
            let failed = expectation(description: "old system audio release completed for \(trigger)")
            audio.afterDeactivation = { number in
                if number == 1 { failed.fulfill() }
            }
            let retry = DispatchSemaphore(value: 0)
            let secondRelease = trigger == "music" ? expectation(description: "new owner release waiting") : nil
            audio.beforeDeactivation = { number in
                if number == 2 {
                    secondRelease?.fulfill()
                    XCTAssertEqual(retry.wait(timeout: .now() + 15), .success)
                }
            }
            defer { retry.signal() }
            let controller = PlaybackController(audioSessionActions: audio.actions)
            let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
            let film = try controller.bindVideo(owner: "film") { _ in }
            let fixture = try AudioAdmissionHost(controller: controller, film: film)
            addTeardownBlock {
                audio.failDeactivation = false
                do { try await fixture.close() }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            let media = try PlaybackVideoMediaLease(attemptID: "controller-first",
                input: .init(resourceID: "controller-first", url: movie, title: "Synthetic video")) {}
            let transport = try PlaybackVideoTransport(media: media, input: XCTUnwrap(media.takeInput()))
            try await transport.prepare()
            film.surface.stage(transport.output.presentationLayer)
            try await transport.renderFirstVideoFrame()
            let admission = controller.admitVideo(owner: film.owner, candidate: transport)
            try await controller.prepareVideoAudio(admission: admission, owner: film.owner, paused: false)
            _ = try controller.commitVideo(transport, lease: film, admission: admission, paused: false)
            if trigger == "stop" { controller.stop() }
            else if trigger == "failure" { transport.kernel.onFailure?(.decodeFailed) }
            else {
                _ = controller.playAttempt(resource: try .init(id: "next-music",
                    url: movie, title: "Synthetic music"), owner: "music", autoplay: false)
            }
            let next = controller.currentAttempt
            await fulfillment(of: [failed], timeout: 5)
            XCTAssertEqual(audio.deactivations, 1)
            XCTAssertNil(film.currentAttempt)
            if let secondRelease { await fulfillment(of: [secondRelease], timeout: 5) }
            audio.failDeactivation = false
            retry.signal()
            if trigger == "music" {
                // Wait for the new paused owner to finish its own successful
                // system release before measuring the retired lease's close.
                let released = expectation(for: NSPredicate { _, _ in
                    MainActor.assumeIsolated { audio.deactivations == 2 && controller.player.currentItem?.status == .readyToPlay }
                }, evaluatedWith: nil)
                await fulfillment(of: [released], timeout: 15)
                XCTAssertEqual(audio.deactivations, 2)
                XCTAssertEqual(controller.player.currentItem?.status, .readyToPlay)
            }
            let before = audio.deactivations
            for _ in 0..<2 {
                do { try await film.close(); XCTFail("controller-first failure belongs to the retired lease") }
                catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
            }
            XCTAssertEqual(controller.currentAttempt, next)
            XCTAssertEqual(audio.deactivations, before, "old close cannot retry the shared system session")
            do { try await fixture.close() }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .outputFailed) }
        }
    }
}

/// Only business receipts are controlled. Music uses the shared AVPlayer and
/// video uses real decoding/first-frame output on that same controller.
private actor VideoHandoffMusicService: MusicServing {
    static let selection = MusicPlaybackSelection(installationId: "official:wy", generation: 1,
        siteId: "wy", sourceKey: "synthetic", quality: "128k")
    private let file: URL
    private let entries: [MusicQueueEntry] = ["first", "second"].map {
        .init(id: $0, track: .init(ref: .init(kind: .queue, id: $0), songId: $0, title: $0,
            duration: 30, sources: [.init(sourceKey: "synthetic", platform: "wy", nativeId: $0)]))
    }
    private var index = 0, revision = 1, position = 0.0
    private var resolution: VideoHandoffMusicGate?, advanceGate: VideoHandoffMusicGate?
    private var facts: [MusicPlaybackFact] = []
    private var cancelled: [String] = []
    init(file: URL) { self.file = file }
    func holdResolution(_ gate: VideoHandoffMusicGate) { resolution = gate }
    func holdAdvance(_ gate: VideoHandoffMusicGate) { advanceGate = gate }
    func recordedFacts() -> [MusicPlaybackFact] { facts }
    func cancelledAttempts() -> [String] { cancelled }
    func state() -> MusicPlaybackState {
        .init(revision: revision, mode: .loop, currentId: entries[index].id, total: entries.count,
            position: position, canNext: index == 0, canPrevious: index == 1)
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: entries[index].id, entries: entries) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        index = direction == .next ? 1 : 0; revision += 1; position = 0
        let result = MusicAdvanceResult(state: state(), selectedId: entries[index].id)
        let gate = advanceGate; advanceGate = nil
        await gate?.wait()
        return result
    }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability {
        .init(options: [.init(installationId: Self.selection.installationId, generation: 1, siteId: "wy",
            sourceKey: Self.selection.sourceKey, qualities: ["128k"], title: "Synthetic", version: "1",
            contentHash: "")], scriptState: .ready)
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async -> MusicPlaybackCandidate {
        let gate = resolution; resolution = nil
        await gate?.wait() // Deliberately returns a receipt after cancellation.
        return .init(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
            url: file, title: occurrenceId, requestedQuality: "128k", resolvedSelection: selection)
    }
    func cancel(attemptId: String?) { if let attemptId { cancelled.append(attemptId) } }
    func record(_ fact: MusicPlaybackFact) -> Bool { facts.append(fact); position = fact.position; return true }
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
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot { .init(occurrenceId: occurrenceId, state: .none, lines: []) }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor VideoHandoffMusicGate {
    nonisolated let entered: XCTestExpectation
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    init(entered: XCTestExpectation) { self.entered = entered }
    func wait() async {
        guard !released else { return }
        await withCheckedContinuation { continuation = $0; entered.fulfill() }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
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

/// AVAudioSession actions now run on a serial background lane. Test flags and
/// counts cross that boundary too; business state remains on MainActor.
private final class AudioAdmissionActions: @unchecked Sendable {
    private let lock = NSLock()
    private var activationFailure = false, deactivationFailure = false
    private var activationCount = 0, deactivationCount = 0
    var beforeActivation: @Sendable (Int) throws -> Void = { _ in }
    var beforeDeactivation: @Sendable (Int) -> Void = { _ in }
    var afterDeactivation: @Sendable (Int) -> Void = { _ in }
    var failActivation: Bool {
        get { lock.lock(); defer { lock.unlock() }; return activationFailure }
        set { lock.lock(); defer { lock.unlock() }; activationFailure = newValue }
    }
    var failDeactivation: Bool {
        get { lock.lock(); defer { lock.unlock() }; return deactivationFailure }
        set { lock.lock(); defer { lock.unlock() }; deactivationFailure = newValue }
    }
    var activations: Int { lock.lock(); defer { lock.unlock() }; return activationCount }
    var deactivations: Int { lock.lock(); defer { lock.unlock() }; return deactivationCount }
    var actions: PlaybackAudioSessionActions {
        .init(activate: { [self] in
            XCTAssertFalse(Thread.isMainThread)
            try beforeActivation(activations + 1)
            lock.lock(); activationCount += 1; let fail = activationFailure; lock.unlock()
            if fail { throw NativePlaybackError.audioSessionUnavailable }
        }, deactivate: { [self] in
            XCTAssertFalse(Thread.isMainThread)
            beforeDeactivation(deactivations + 1)
            lock.lock()
            deactivationCount += 1
            let number = deactivationCount, fail = deactivationFailure
            lock.unlock()
            defer { afterDeactivation(number) }
            if fail { throw NativePlaybackError.audioSessionDeactivationFailed }
        })
    }
}

private final class AudioAdmissionGate: @unchecked Sendable {
    let entered: XCTestExpectation
    private let semaphore = DispatchSemaphore(value: 0)
    init(entered: XCTestExpectation) { self.entered = entered }
    func wait() throws {
        entered.fulfill()
        guard semaphore.wait(timeout: .now() + 15) == .success else {
            throw NativePlaybackError.audioSessionUnavailable
        }
    }
    func open() { semaphore.signal() }
}

#if os(iOS) || os(macOS)
import XCTest
import AVFoundation
import Combine
import MeowContracts
@testable import MeowPlayback

@MainActor
final class PlaybackVideoOwnershipTests: XCTestCase {
    private func input(_ id: String = "video", missing: Bool = false) throws -> PlaybackVideoInput {
        let url = missing ? URL(fileURLWithPath: "/missing/synthetic-video.mp4") :
            try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil)).appendingPathComponent("ordinary.mp4")
        return try PlaybackVideoInput(resourceID: id, url: url, title: "synthetic")
    }

    func testCommitHandoffFollowsDonorExitAndPrecedesStartedButNeverRunsForFailureOrCancellation() async throws {
        let controller = PlaybackController()
        var order: [String] = []
        let music = try controller.bind(owner: "music") { event in
            if event.kind == .replaced { order.append("exit") }
        }
        defer { music.cancel(); controller.stop() }
        let donor = controller.playAttempt(resource: try .init(id: "donor", url: input().url,
            title: "Synthetic music"), owner: "music", autoplay: false)
        let film = try bindWindowedVideo(controller, owner: "film", willCommit: {
            XCTAssertEqual(controller.currentAttempt, donor)
            order.append("capture")
            return {
                XCTAssertEqual(controller.currentAttempt?.owner, "film")
                order.append("handoff")
            }
        }) { event in
            if event.kind == .prepared {
                XCTAssertEqual(controller.currentAttempt, donor)
                XCTAssertTrue(order.isEmpty)
            }
            if event.kind == .started { order.append("started") }
        }
        let failed = try PlaybackVideoMediaLease(attemptID: "failed", input: input(missing: true)) {}
        do { _ = try await film.play(failed); XCTFail("The missing candidate cannot commit") } catch {}
        XCTAssertEqual(controller.currentAttempt, donor)
        XCTAssertTrue(order.isEmpty)
        let cancelled = try PlaybackVideoMediaLease(attemptID: "cancelled", input: input()) {}
        do {
            _ = try await film.play(cancelled, waitForCommit: { throw CancellationError() })
            XCTFail("The cancelled candidate cannot commit")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
        XCTAssertEqual(controller.currentAttempt, donor)
        XCTAssertTrue(order.isEmpty)
        let accepted = try PlaybackVideoMediaLease(attemptID: "accepted", input: input()) {}
        _ = try await film.play(accepted, paused: true)
        XCTAssertEqual(order, ["capture", "exit", "handoff", "started"])
        try await film.close()
        XCTAssertNil(controller.currentAttempt, "Closing video cannot resume its donor")
    }

    func testReentrantNewMusicDuringCommitPublicationSkipsTheOlderHandoff() async throws {
        let controller = PlaybackController()
        let resource = try PlaybackResource(id: "new-music", url: input().url, title: "New explicit music")
        var handoffs = 0, started = 0
        let film = try bindWindowedVideo(controller, owner: "film", willCommit: {
            { handoffs += 1 }
        }) { event in if event.kind == .started { started += 1 } }
        var next: PlayAttempt?
        let observation = controller.$currentAttempt.sink { attempt in
            guard attempt?.owner == "film", next == nil else { return }
            next = controller.playAttempt(resource: resource, owner: "music", autoplay: false)
        }
        defer { observation.cancel(); controller.stop() }
        let media = try PlaybackVideoMediaLease(attemptID: "superseded", input: input()) {}
        do { _ = try await film.play(media, paused: true); XCTFail("A reentrant new owner supersedes commit") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .staleAttempt) }
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertNotNil(next)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(handoffs, 0)
        XCTAssertEqual(started, 0)
        try await film.close()
        XCTAssertEqual(controller.currentAttempt, next)
    }

    func testVideoSystemPlayPauseAndToggleUseCurrentIntentAndRejectOldAttempt() async throws {
        let controller = PlaybackController()
        let film = try bindWindowedVideo(controller, owner: "film") { _ in }
        let firstMedia = try PlaybackVideoMediaLease(attemptID: "commands", input: input()) {}
        let first = try await film.play(firstMedia, paused: true)
        XCTAssertTrue(controller.systemCommand(.toggle, generation: first.id))
        XCTAssertTrue(controller.wantsPlayback)
        XCTAssertTrue(controller.systemCommand(.play, generation: first.id))
        XCTAssertTrue(controller.systemCommand(.play, generation: first.id))
        XCTAssertTrue(controller.wantsPlayback)
        XCTAssertTrue(controller.systemCommand(.toggle, generation: first.id))
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertTrue(controller.systemCommand(.pause, generation: first.id))
        XCTAssertFalse(controller.wantsPlayback)
        let nextMedia = try PlaybackVideoMediaLease(attemptID: "next", input: input("next")) {}
        let next = try await film.play(nextMedia, paused: true)
        for command in [PlaybackTransportCommand.play, .pause, .toggle] {
            XCTAssertFalse(controller.systemCommand(command, generation: first.id))
            XCTAssertEqual(controller.currentAttempt, next)
            XCTAssertFalse(controller.wantsPlayback)
        }
        try await film.close()
        XCTAssertFalse(controller.systemCommand(.toggle, generation: next.id))
    }

    func testLeaseAndAddressReceiptArePassiveAndMediaReleaseCoalesces() async throws {
        let controller = PlaybackController()
        let film = try bindWindowedVideo(controller, owner: "film") { _ in XCTFail("No formal play, no event") }
        var releases = 0
        let media = try PlaybackVideoMediaLease(attemptID: "node", input: input()) { releases += 1 }
        XCTAssertNil(controller.currentAttempt); XCTAssertNil(controller.player.currentItem)
        XCTAssertNil(film.currentAttempt); XCTAssertEqual(controller.snapshot.phase, .idle)
        async let first: Void = media.close()
        async let second: Void = media.close()
        _ = try await (first, second)
        XCTAssertEqual(releases, 1)
        try await film.close()
    }

    func testFailedCandidateRetainsTheMusicOwnerAndPauseIntent() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent.Kind] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0.kind) }
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: input().url, title: "synthetic"), owner: "music", autoplay: false)
        let donor = controller.player
        var releases = 0
        let media = try PlaybackVideoMediaLease(attemptID: "failed", input: input(missing: true)) { releases += 1 }
        do { _ = try await film.play(media); XCTFail("missing media cannot commit") }
        catch { XCTAssertNotNil(error as? PlaybackVideoError) }
        XCTAssertEqual(controller.currentAttempt, music)
        XCTAssertTrue(controller.player === donor)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertFalse(events.contains(.started))
        XCTAssertTrue(events.contains(.failed)); XCTAssertEqual(releases, 1)
        try await film.close()
        XCTAssertEqual(controller.currentAttempt, music, "closing passive film must not stop music")
        controller.stop()
    }

    func testPreparedVideoCannotCommitAfterNewerMusicCommand() async throws {
        let controller = PlaybackController()
        let resource = try PlaybackResource(id: "music", url: input().url, title: "synthetic")
        var started = 0
        let film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .prepared { controller.playAttempt(resource: resource, owner: "music", autoplay: false) }
            if event.kind == .started { started += 1 }
        }
        var releases = 0
        let media = try PlaybackVideoMediaLease(attemptID: "late", input: input()) { releases += 1 }
        do { _ = try await film.play(media); XCTFail("newer music owns admission") } catch {}
        XCTAssertEqual(controller.currentAttempt?.owner, "music")
        XCTAssertEqual(started, 0); XCTAssertEqual(releases, 1)
        try await film.close(); controller.stop()
    }

    func testScopedStopOfVideoCandidateDoesNotStopMusicDonor() async throws {
        let controller = PlaybackController()
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: input().url, title: "synthetic"), owner: "music", autoplay: false)
        let film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .prepared { controller.stop(owner: "film") }
            XCTAssertNotEqual(event.kind, .started)
        }
        var releases = 0
        let media = try PlaybackVideoMediaLease(attemptID: "scoped-stop", input: input()) { releases += 1 }
        do { _ = try await film.play(media); XCTFail("stopped video candidate") } catch {}
        XCTAssertEqual(controller.currentAttempt, music)
        XCTAssertNotNil(controller.player.currentItem)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(releases, 1)
        try await film.close(); controller.stop()
    }

    func testRealFirstFrameCommitsOneOwnerAndControlsAreAttemptFenced() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: input().url, title: "synthetic"), owner: "music", autoplay: false)
        var released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "node-start", input: input()) { released += 1 }
        let attempt = try await film.play(media, paused: true)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(film.currentAttemptID, "node-start")
        XCTAssertNil(controller.player.currentItem, "film has no AVPlayer bypass")
        XCTAssertEqual(events.filter { $0.kind == .started }.map(\.attemptID), ["node-start"])
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(events.last { $0.kind == .started }?.wantsPlayback, false)
        do { _ = try await film.play(media); XCTFail("A media receipt is single-use") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .staleAttempt) }
        XCTAssertEqual(released, 0, "A duplicate receipt must not release the live owner's media")
        XCTAssertFalse(controller.pause(attempt: music))
        XCTAssertFalse(controller.setVolume(0.2, attempt: music))
        XCTAssertTrue(controller.setPreferredRate(1.5, attempt: attempt))
        XCTAssertTrue(controller.setVolume(0, attempt: attempt))
        XCTAssertEqual(controller.snapshot.phase, .paused)
        let sought = expectation(description: "actual video seek")
        XCTAssertTrue(controller.seek(to: 1.4, attempt: attempt) { result in
            XCTAssertTrue(result.succeeded); XCTAssertEqual(result.actualPosition ?? -1, 1.4, accuracy: 0.15)
            sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 10)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        try await film.stop()
        XCTAssertEqual(released, 1)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertTrue(events.contains { $0.kind == .stopped && $0.attemptID == "node-start" })
        try await film.close()
    }

    func testVideoReplacementPreservesPositionPauseAndHealthyDonorOnFailure() async throws {
        let controller = PlaybackController()
        let film = try bindWindowedVideo(controller, owner: "film") { _ in }
        var releases = 0
        let first = try PlaybackVideoMediaLease(attemptID: "first", input: input()) { releases += 1 }
        let donor = try await film.play(first, paused: true)
        let sought = expectation(description: "donor position")
        XCTAssertTrue(controller.seek(to: 1.2, attempt: donor) { result in
            XCTAssertTrue(result.succeeded); sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 10)
        let missing = try PlaybackVideoMediaLease(attemptID: "missing", input: input(missing: true)) { releases += 1 }
        do { _ = try await film.play(missing, preservePosition: true); XCTFail("failed replacement") } catch {}
        XCTAssertEqual(film.currentAttempt, donor)
        XCTAssertEqual(controller.snapshot.position, 1.2, accuracy: 0.15)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        let replacement = try PlaybackVideoMediaLease(attemptID: "second", input: input()) { releases += 1 }
        let second = try await film.play(replacement, preservePosition: true)
        XCTAssertNotEqual(second, donor)
        XCTAssertEqual(controller.snapshot.position, 1.2, accuracy: 0.15)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(controller.resume(attempt: donor))
        try await film.stop()
        XCTAssertEqual(releases, 3)
        try await film.close()
    }

    func testActualResumePublishesIntentThroughVideoProgressWithoutWaitingForTimeAdvance() async throws {
        let controller = PlaybackController()
        let resumed = expectation(description: "actual resume event")
        var observedResume = false
        let film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .progress, event.snapshot.phase == .playing, event.wantsPlayback, !observedResume {
                observedResume = true; resumed.fulfill()
            }
        }
        let media = try PlaybackVideoMediaLease(attemptID: "resume", input: input()) {}
        let attempt = try await film.play(media, paused: true)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [resumed], timeout: 8)
        XCTAssertTrue(controller.wantsPlayback)
        try await film.close()
    }

    func testCancelPreparedCandidateDrainsWithoutPublishingStarted() async throws {
        let controller = PlaybackController()
        var film: PlaybackVideoLease!
        var cancellation: Task<Void, Error>?
        var started = 0, released = 0
        film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .prepared { cancellation = Task { try await film.cancelPending() } }
            if event.kind == .started { started += 1 }
        }
        let media = try PlaybackVideoMediaLease(attemptID: "cancelled", input: input()) { released += 1 }
        do { _ = try await film.play(media); XCTFail("cancelled candidate") }
        catch {
            let failure = error as? PlaybackVideoError
            XCTAssertTrue(failure == .cancelled || failure == .staleAttempt)
        }
        try await cancellation?.value
        XCTAssertEqual(started, 0); XCTAssertEqual(released, 1)
        XCTAssertNil(controller.currentAttempt)
        try await film.close()
    }

    func testResourceReleaseFailureRemainsObservableAcrossRepeatedClose() async throws {
        let controller = PlaybackController()
        let film = try bindWindowedVideo(controller, owner: "film") { _ in }
        var releases = 0
        let media = try PlaybackVideoMediaLease(attemptID: "release-failure", input: input(missing: true)) {
            releases += 1; throw PlaybackVideoError.resourceReleaseFailed
        }
        do { _ = try await film.play(media); XCTFail("release failed") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed) }
        do { try await media.close(); XCTFail("failed receipt cannot turn into success") }
        catch { XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed) }
        XCTAssertEqual(releases, 1)
        for _ in 0..<2 {
            do { try await film.close(); XCTFail("lease must retain its failed candidate drain") }
            catch { XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed) }
        }
    }
}
#endif

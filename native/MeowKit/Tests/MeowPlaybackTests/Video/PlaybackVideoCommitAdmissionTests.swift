#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
@testable import MeowPlayback

@MainActor
final class PlaybackVideoCommitAdmissionTests: XCTestCase {
    private func input(_ id: String) throws -> PlaybackVideoInput {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
            .appendingPathComponent("ordinary.mp4")
        return try PlaybackVideoInput(resourceID: id, url: url, title: "synthetic")
    }

    private func seek(_ controller: PlaybackController, attempt: PlayAttempt, to position: Double) async {
        let sought = expectation(description: "actual donor seek")
        XCTAssertTrue(controller.seek(to: position, attempt: attempt) { result in
            XCTAssertTrue(result.succeeded)
            XCTAssertEqual(result.actualPosition ?? -1, position, accuracy: 0.15)
            sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 10)
    }

    private func assertRevoked(_ result: Result<PlayAttempt, Error>,
                               file: StaticString = #filePath, line: UInt = #line) {
        switch result {
        case .success: XCTFail("revoked candidate cannot commit", file: file, line: line)
        case .failure(let error):
            let failure = error as? PlaybackVideoError
            XCTAssertTrue(failure == .cancelled || failure == .staleAttempt, file: file, line: line)
        }
    }

    func testBusinessWaitKeepsPreparedReceiptAndDonorThenUsesLatestPositionAndPause() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
        let donorMedia = try PlaybackVideoMediaLease(attemptID: "donor", input: input("donor")) {}
        let donor = try await film.play(donorMedia)
        let gate = CommitGate(entered: expectation(description: "prepared business wait"))
        var released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "replacement", input: input("replacement")) { released += 1 }
        let work = Task {
            try await film.play(media, preservePosition: true, waitForCommit: {
                XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .prepared }.count, 1)
                try await gate.wait()
            }, canCommit: { gate.isOpen })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        XCTAssertTrue(controller.pause(attempt: donor))
        await seek(controller, attempt: donor, to: 1.2)
        XCTAssertEqual(film.currentAttempt, donor)
        XCTAssertEqual(film.currentAttemptID, "donor")
        XCTAssertNil(media.input, "the same receipt remains claimed by its one prepared candidate")
        XCTAssertEqual(released, 0)
        XCTAssertFalse(events.contains { $0.attemptID == "replacement" && $0.kind == .started })
        gate.open()
        let replacement = try await work.value
        XCTAssertNotEqual(replacement, donor)
        XCTAssertEqual(film.currentAttempt, replacement)
        XCTAssertEqual(controller.snapshot.position, 1.2, accuracy: 0.15)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .started }.count, 1)
        XCTAssertEqual(released, 0)
        try await film.close()
        XCTAssertEqual(released, 1)
    }

    func testTemporaryFalseAfterWaitReusesPreparedCandidateRatherThanFailing() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent.Kind] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0.kind) }
        let gate = CommitGate(entered: expectation(description: "reentered business wait"))
        var waits = 0, released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "gated", input: input("gated")) { released += 1 }
        let work = Task {
            try await film.play(media, paused: true, waitForCommit: {
                waits += 1
                if waits > 1 { try await gate.wait() }
            }, canCommit: { gate.isOpen })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        XCTAssertEqual(waits, 2)
        XCTAssertEqual(events.filter { $0 == .prepared }.count, 1)
        XCTAssertFalse(events.contains(.started)); XCTAssertFalse(events.contains(.failed))
        XCTAssertEqual(released, 0)
        gate.open()
        let attempt = try await work.value
        XCTAssertEqual(film.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(events.filter { $0 == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0 == .started }.count, 1)
        try await film.close()
        XCTAssertEqual(released, 1)
    }

    func testGateClosingAtRealAlignmentCheckpointReturnsToWaitWithSameDonor() async throws {
        try await assertGateClosesDuringNativeOutput(preservePosition: true)
    }

    func testGateClosingAtRealFirstFrameCheckpointReturnsToWaitWithSameDonor() async throws {
        try await assertGateClosesDuringNativeOutput(preservePosition: false)
    }

    private func assertGateClosesDuringNativeOutput(preservePosition: Bool) async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
        let donorMedia = try PlaybackVideoMediaLease(attemptID: "donor", input: input("donor")) {}
        let donor = try await film.play(donorMedia, paused: true)
        await seek(controller, attempt: donor, to: 0.6)
        let gate = CommitGate(entered: expectation(description: "new request during native output"))
        var waits = 0, checkpoints = 0, released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "replacement", input: input("replacement")) { released += 1 }
        let work = Task {
            try await film.play(media, paused: true, preservePosition: preservePosition, waitForCommit: {
                waits += 1
                if waits > 1 { try await gate.wait() }
            }, canCommit: {
                checkpoints += 1
                // Admit the first pass, then inject a new request at the next
                // checkpoint after actual align (or render, without alignment).
                return checkpoints == 1 || gate.isOpen
            })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        XCTAssertEqual(waits, 2)
        XCTAssertEqual(checkpoints, 2)
        XCTAssertEqual(film.currentAttempt, donor)
        XCTAssertEqual(released, 0)
        XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .prepared }.count, 1)
        XCTAssertFalse(events.contains { $0.attemptID == "replacement" && ($0.kind == .started || $0.kind == .failed) })
        await seek(controller, attempt: donor, to: 1.4)
        gate.open()
        let replacement = try await work.value
        XCTAssertEqual(film.currentAttempt, replacement)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.snapshot.position, preservePosition ? 1.4 : 0, accuracy: 0.15)
        XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0.attemptID == "replacement" && $0.kind == .started }.count, 1)
        XCTAssertEqual(released, 0)
        try await film.close()
        XCTAssertEqual(released, 1)
    }

    func testTaskCancellationDuringBusinessWaitDrainsWithoutStarted() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent.Kind] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0.kind) }
        let gate = CommitGate(entered: expectation(description: "waiting for business request"))
        let completed = expectation(description: "cancelled play completed")
        var released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "cancelled", input: input("cancelled")) { released += 1 }
        let work = Task {
            defer { completed.fulfill() }
            return try await film.play(media, waitForCommit: { try await gate.wait() }, canCommit: { gate.isOpen })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        work.cancel()
        await fulfillment(of: [completed], timeout: 10)
        gate.open()
        assertRevoked(await work.result)
        XCTAssertEqual(released, 1)
        XCTAssertNil(film.currentAttempt)
        XCTAssertFalse(events.contains(.started))
        try await film.close()
        XCTAssertEqual(released, 1)
    }

    func testCancelPendingDrainsBlockedCandidateWithoutWaitingForBusinessAndKeepsDonor() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
        let donorMedia = try PlaybackVideoMediaLease(attemptID: "donor", input: input("donor")) {}
        let donor = try await film.play(donorMedia, paused: true)
        let gate = CommitGate(entered: expectation(description: "candidate waiting"))
        var released = 0, drained = false
        let media = try PlaybackVideoMediaLease(attemptID: "pending", input: input("pending")) { released += 1 }
        let work = Task {
            try await film.play(media, waitForCommit: { try await gate.wait() }, canCommit: { gate.isOpen })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        let completed = expectation(description: "cancelPending independently drains")
        let cancellation = Task {
            defer { completed.fulfill() }
            try await film.cancelPending()
            drained = true
        }
        await fulfillment(of: [completed], timeout: 10)
        XCTAssertTrue(drained, "native drain must not wait for the business callback")
        XCTAssertFalse(gate.isOpen)
        XCTAssertEqual(released, 1)
        XCTAssertEqual(film.currentAttempt, donor)
        XCTAssertFalse(events.contains { $0.attemptID == "pending" && $0.kind == .started })
        gate.open()
        try await cancellation.value
        assertRevoked(await work.result)
        XCTAssertEqual(released, 1)
        await seek(controller, attempt: donor, to: 1.2)
        try await film.close()
    }

    func testCloseIndependentlyDrainsBlockedCandidateAndDonorBeforeGateReturns() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
        var donorReleased = 0
        let donorMedia = try PlaybackVideoMediaLease(attemptID: "donor", input: input("donor")) { donorReleased += 1 }
        _ = try await film.play(donorMedia, paused: true)
        let gate = CommitGate(entered: expectation(description: "candidate waiting"))
        var released = 0, drained = false
        let media = try PlaybackVideoMediaLease(attemptID: "pending", input: input("pending")) { released += 1 }
        let work = Task {
            try await film.play(media, waitForCommit: { try await gate.wait() }, canCommit: { gate.isOpen })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        let completed = expectation(description: "close independently drains")
        let closing = Task {
            defer { completed.fulfill() }
            try await film.close()
            drained = true
        }
        await fulfillment(of: [completed], timeout: 10)
        XCTAssertTrue(drained, "native close must not await the still-blocked business callback")
        XCTAssertFalse(gate.isOpen)
        XCTAssertEqual(released, 1); XCTAssertEqual(donorReleased, 1)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(events.contains { $0.attemptID == "pending" && $0.kind == .started })
        gate.open()
        try await closing.value
        assertRevoked(await work.result)
        try await film.close()
        XCTAssertEqual(released, 1); XCTAssertEqual(donorReleased, 1)
    }

    func testNewMusicAdmissionDuringBusinessWaitStillRejectsCandidateAfterRelease() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent.Kind] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0.kind) }
        let gate = CommitGate(entered: expectation(description: "candidate waiting"))
        var released = 0, gateChecks = 0
        let media = try PlaybackVideoMediaLease(attemptID: "stale", input: input("stale")) { released += 1 }
        let work = Task {
            try await film.play(media, waitForCommit: { try await gate.wait() }, canCommit: {
                gateChecks += 1; return true
            })
        }
        defer { gate.open(); work.cancel() }
        await fulfillment(of: [gate.entered], timeout: 10)
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: input("music").url, title: "synthetic"), owner: "music", autoplay: false)
        gate.open()
        assertRevoked(await work.result)
        XCTAssertEqual(gateChecks, 0, "native admission must be checked before business canCommit")
        XCTAssertEqual(controller.currentAttempt, music)
        XCTAssertFalse(events.contains(.started)); XCTAssertEqual(released, 1)
        try await film.close()
        XCTAssertEqual(controller.currentAttempt, music)
        controller.stop()
    }

    func testDefaultCallbacksStillCommitActualFrameAndPreservePause() async throws {
        let controller = PlaybackController()
        var events: [PlaybackVideoEvent.Kind] = []
        let film = try bindWindowedVideo(controller, owner: "film") { events.append($0.kind) }
        var released = 0
        let media = try PlaybackVideoMediaLease(attemptID: "default", input: input("default")) { released += 1 }
        let attempt = try await film.play(media, paused: true)
        XCTAssertEqual(film.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(events.filter { $0 == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0 == .started }.count, 1)
        XCTAssertEqual(released, 0)
        try await film.close()
        XCTAssertEqual(released, 1)
    }

    func testCloseJoinsCandidateAlreadyRetiringFromCancelFailureOrReplacement() async throws {
        for trigger in ["cancel", "failure", "replacement"] {
            for releaseFails in [false, true] {
                let controller = PlaybackController()
                var events: [PlaybackVideoEvent] = []
                let film = try bindWindowedVideo(controller, owner: "film") { events.append($0) }
                let business = CommitGate(entered: expectation(description: "business wait"))
                let release = CommitGate(entered: expectation(description: "real media release entered"))
                var releases = 0, nextReleases = 0
                let media = try PlaybackVideoMediaLease(attemptID: "pending", input: input("pending")) {
                    releases += 1
                    try await release.wait()
                    if releaseFails { throw PlaybackVideoError.resourceReleaseFailed }
                }
                let play = Task {
                    try await film.play(media, paused: true, waitForCommit: {
                        try await business.wait()
                        if trigger == "failure" { throw PlaybackVideoError.inputFailed }
                    }, canCommit: { business.isOpen })
                }
                defer { business.open(); release.open(); play.cancel() }
                await fulfillment(of: [business.entered], timeout: 10)
                let retirement: Task<Void, Error>?
                if trigger == "cancel" {
                    retirement = Task { try await film.cancelPending() }
                } else if trigger == "replacement" {
                    let next = try PlaybackVideoMediaLease(attemptID: "next", input: input("next")) {
                        nextReleases += 1
                    }
                    retirement = Task { _ = try await film.play(next, paused: true) }
                } else {
                    retirement = nil
                    business.open()
                }
                await fulfillment(of: [release.entered], timeout: 10)
                let closingStarted = expectation(description: "overlapping close started")
                let prematurelyClosed = expectation(description: "close cannot skip retiring candidate")
                prematurelyClosed.isInverted = true
                var holdingRelease = true
                let closing = Task {
                    closingStarted.fulfill()
                    defer { if holdingRelease { prematurelyClosed.fulfill() } }
                    try await film.close()
                }
                await fulfillment(of: [closingStarted], timeout: 2)
                await fulfillment(of: [prematurelyClosed], timeout: 0.1)
                holdingRelease = false
                release.open()
                let closeResult = await closing.result
                if releaseFails {
                    if case .failure(let error) = closeResult {
                        XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed)
                    } else {
                        XCTFail("failed in-flight release cannot become successful close")
                    }
                } else {
                    try closeResult.get()
                }
                if trigger != "failure" {
                    XCTAssertFalse(business.isOpen, "close waits for native resources, not the business callback")
                }
                business.open()
                let playResult = await play.result
                if case .failure(let error) = playResult {
                    if releaseFails { XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed) }
                    else if trigger == "failure" { XCTAssertEqual(error as? PlaybackVideoError, .inputFailed) }
                    else { assertRevoked(playResult) }
                } else {
                    XCTFail("a retired candidate must not commit")
                }
                if let retirement { _ = await retirement.result }
                XCTAssertEqual(releases, 1)
                XCTAssertEqual(nextReleases, trigger == "replacement" ? 1 : 0)
                XCTAssertFalse(events.contains { $0.kind == .started })
                do {
                    try await film.close()
                    XCTAssertFalse(releaseFails, "repeated close must retain failure")
                } catch {
                    XCTAssertTrue(releaseFails)
                    XCTAssertEqual(error as? PlaybackVideoError, .resourceReleaseFailed)
                }
            }
        }
    }

    func testRetiringCandidateCloseDoesNotStopNewMusicOwner() async throws {
        let controller = PlaybackController()
        let film = try bindWindowedVideo(controller, owner: "film") { _ in }
        let business = CommitGate(entered: expectation(description: "business wait"))
        let release = CommitGate(entered: expectation(description: "release wait"))
        let media = try PlaybackVideoMediaLease(attemptID: "pending", input: input("pending")) {
            try await release.wait()
        }
        let work = Task {
            try await film.play(media, waitForCommit: { try await business.wait() })
        }
        defer { business.open(); release.open(); work.cancel(); controller.stop() }
        await fulfillment(of: [business.entered], timeout: 10)
        let cancellation = Task { try await film.cancelPending() }
        await fulfillment(of: [release.entered], timeout: 10)
        let music = controller.playAttempt(resource: try .init(id: "music",
            url: input("music").url, title: "synthetic"), owner: "music", autoplay: false)
        let closing = Task { try await film.close() }
        release.open()
        try await closing.value
        try await cancellation.value
        XCTAssertEqual(controller.currentAttempt, music)
        XCTAssertFalse(controller.wantsPlayback)
        business.open()
        assertRevoked(await work.result)
    }

    func testNativeBudgetAccumulatesAcrossBusinessGatesAndRejectsExpiredFinalCheckpoint() async throws {
        for preservePosition in [false, true] {
            for closeGateAtDeadline in [false, true] {
                let controller = PlaybackController()
                var instant = ContinuousClock.now
                var events: [PlaybackVideoEvent] = []
                let window = try PlaybackVideoTestWindow()
                let film = PlaybackVideoLease(controller: controller, owner: "film",
                    now: { instant }, onEvent: window.preparedEvents(for: controller) { events.append($0) })
                hostVideoLease(film, in: window)
                try film.bind(commands: .init())
                let donorMedia = try PlaybackVideoMediaLease(attemptID: "donor", input: input("donor")) {}
                let donor = try await film.play(donorMedia, paused: true)
                var waits = 0, checkpoints = 0, releases = 0
                let media = try PlaybackVideoMediaLease(attemptID: "replacement", input: input("replacement")) {
                    releases += 1
                }
                do {
                    _ = try await film.play(media, paused: true, preservePosition: preservePosition,
                        waitForCommit: {
                            waits += 1
                            // The business owner may wait far beyond the native
                            // budget. This interval is deliberately not billed.
                            instant = instant.advanced(by: .seconds(100))
                        }, canCommit: {
                            checkpoints += 1
                            if checkpoints == 2 {
                                instant = instant.advanced(by: .seconds(8))
                                return false
                            }
                            if checkpoints == 4 {
                                instant = instant.advanced(by: .seconds(7))
                                return !closeGateAtDeadline
                            }
                            return true
                        })
                    XCTFail("the same 15-second native budget must cover every admission pass")
                } catch { XCTAssertEqual(error as? PlaybackVideoError, .timedOut) }
                XCTAssertEqual(waits, 2)
                XCTAssertEqual(checkpoints, 4)
                XCTAssertEqual(film.currentAttempt, donor)
                XCTAssertEqual(controller.snapshot.phase, .paused)
                XCTAssertFalse(events.contains { $0.attemptID == "replacement" && $0.kind == .started })
                XCTAssertEqual(releases, 1)
                try await film.close()
            }
        }
    }

    func testBusinessWaitAloneDoesNotConsumeNativeAlignmentBudget() async throws {
        let controller = PlaybackController()
        var instant = ContinuousClock.now
        let window = try PlaybackVideoTestWindow()
        let film = PlaybackVideoLease(controller: controller, owner: "film", now: { instant },
            onEvent: window.preparedEvents(for: controller) { _ in })
        hostVideoLease(film, in: window)
        try film.bind(commands: .init())
        let media = try PlaybackVideoMediaLease(attemptID: "slow-business", input: input("slow-business")) {}
        let attempt = try await film.play(media, paused: true, waitForCommit: {
            instant = instant.advanced(by: .seconds(60))
        })
        XCTAssertEqual(film.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        try await film.close()
    }

    /// Only the asynchronous business request is controlled. All media prepare,
    /// seek, frame output and native drain above use the real VideoKernel.
    @MainActor
    private final class CommitGate {
        let entered: XCTestExpectation
        private var continuation: CheckedContinuation<Void, Error>?
        private var result: Result<Void, Error>?
        var isOpen: Bool {
            if case .success? = result { return true }
            return false
        }

        init(entered: XCTestExpectation) { self.entered = entered }

        func wait() async throws {
            try Task.checkCancellation()
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in
                    if let result { continuation.resume(with: result) }
                    else {
                        self.continuation = continuation
                        entered.fulfill()
                    }
                }
            } onCancel: {
                Task { @MainActor in self.finish(.failure(CancellationError())) }
            }
        }

        func open() { finish(.success(())) }

        private func finish(_ result: Result<Void, Error>) {
            guard self.result == nil else { return }
            self.result = result
            let continuation = continuation
            self.continuation = nil
            continuation?.resume(with: result)
        }
    }
}
#endif

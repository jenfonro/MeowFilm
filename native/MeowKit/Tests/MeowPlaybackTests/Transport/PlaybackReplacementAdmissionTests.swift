#if os(iOS) || os(macOS)
import Foundation
import AVFoundation
import XCTest
import MeowContracts
@testable import MeowPlayback

@MainActor final class PlaybackReplacementAdmissionTests: XCTestCase {
    func testStoppedOutputPreparesAndSeeksBeforePublishingAnyAttempt() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        var preparedFacts = 0
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .prepared { preparedFacts += 1 }
        }
        defer { lease.cancel(); controller.stop() }
        let prepared = try await controller.preparePlayback(
            resource: .init(id: "replacement", url: file, title: "test", startPosition: 12),
            owner: "music", autoplay: false)
        defer { prepared.cancel() }
        XCTAssertNil(controller.currentAttempt)
        XCTAssertEqual(preparedFacts, 0)
        try await controller.synchronizeReplacement(prepared)
        let accepted = try controller.commitReplacement(prepared)
        XCTAssertEqual(controller.currentAttempt, accepted)
        XCTAssertEqual(controller.snapshot.position, 12, accuracy: 0.25)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(preparedFacts, 1)
    }

    func testStopInvalidatesPreparedPlaybackEvenWhenThereIsNoCurrentItem() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        defer { controller.stop() }
        let prepared = try await controller.preparePlayback(
            resource: .init(id: "replacement", url: file, title: "test"), owner: "music", autoplay: false)
        defer { prepared.cancel() }
        try await controller.synchronizeReplacement(prepared)
        controller.stop(owner: "music")
        XCTAssertThrowsError(try controller.commitReplacement(prepared))
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(prepared.committed)
    }

    func testAlreadyPreparedCandidateSurvivesDonorFailureWithoutReopening() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Donor ready")
        let failed = expectation(description: "Donor failed")
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .prepared, event.attempt.resourceID == "donor" { ready.fulfill() }
            if event.kind == .failed { failed.fulfill() }
        }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(
            resource: try .init(id: "donor", url: file, title: "test"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        let prepared = try await controller.prepareReplacement(
            resource: .init(id: "replacement", url: file, title: "test"), replacing: attempt)
        defer { prepared.cancel() }
        try await controller.synchronizeReplacement(prepared)
        let donorItem = try XCTUnwrap(controller.player.currentItem)
        NotificationCenter.default.post(name: .AVPlayerItemFailedToPlayToEndTime, object: donorItem)
        await fulfillment(of: [failed], timeout: 5)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .failed)
        XCTAssertTrue(controller.pause(attempt: attempt))
        try await controller.synchronizeReplacement(prepared)
        let accepted = try controller.commitReplacement(prepared)
        XCTAssertEqual(controller.currentAttempt, accepted)
        XCTAssertEqual(accepted.resourceID, "replacement")
        XCTAssertEqual(controller.snapshot.phase, .paused)
    }

    func testInputFailureAfterReadinessCannotCommitOverHealthyDonor() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Donor ready")
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .prepared { ready.fulfill() }
        }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(
            resource: try .init(id: "donor", url: file, title: "test"), owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        let donor = controller.player
        let prepared = try await controller.prepareReplacement(
            resource: .init(id: "replacement", url: file, title: "test"), replacing: attempt)
        defer { prepared.cancel() }
        try await controller.synchronizeReplacement(prepared)
        // Inject the real loader's failure callback after ready and seek have succeeded.
        prepared.failures.receive?(.transportFailed)
        XCTAssertThrowsError(try controller.commitReplacement(prepared)) { error in
            XCTAssertEqual(error as? PlaybackNetworkError, .transportFailed)
        }
        XCTAssertTrue(controller.player === donor)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(prepared.committed)
    }

    func testInputFailureDuringLastSynchronizationStaysARejection() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Donor ready")
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .prepared { ready.fulfill() }
        }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(
            resource: try .init(id: "donor", url: file, title: "test"), owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        let donor = controller.player
        let prepared = try await controller.prepareReplacement(
            resource: .init(id: "replacement", url: file, title: "test"), replacing: attempt)
        defer { prepared.cancel() }
        prepared.failures.receive?(.resourceChanged)
        do {
            try await controller.synchronizeReplacement(prepared)
            XCTFail("A failed input cannot finish synchronization")
        } catch {
            XCTAssertEqual(error as? PlaybackNetworkError, .resourceChanged)
        }
        XCTAssertTrue(controller.player === donor)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertFalse(prepared.committed)
    }

    func testSuccessfulPreparedReplacementCommitsOnceWithoutChangingPause() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Donor ready")
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .prepared, event.attempt.resourceID == "donor" { ready.fulfill() }
        }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(
            resource: try .init(id: "donor", url: file, title: "test"), owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        let prepared = try await controller.prepareReplacement(
            resource: .init(id: "replacement", url: file, title: "test"), replacing: attempt)
        defer { prepared.cancel() }
        try await controller.synchronizeReplacement(prepared)
        let accepted = try controller.commitReplacement(prepared)
        XCTAssertEqual(controller.currentAttempt, accepted)
        XCTAssertEqual(accepted.resourceID, "replacement")
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertTrue(prepared.committed)
        XCTAssertThrowsError(try controller.commitReplacement(prepared))
    }

    func testRealNativeStartRateAndSmallSeekCarryListeningBoundaries() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Local silent media ready")
        let played = expectation(description: "Real time-control playing at selected rate")
        let sought = expectation(description: "Small native seek completed")
        let jumped = expectation(description: "Small external native seek observed")
        var events: [PlaybackEvent] = [], didPlay = false
        var externalPosition: Double?
        let lease = try controller.bind(owner: "music") { event in
            events.append(event)
            if event.kind == .prepared { ready.fulfill() }
            if event.kind == .played, !didPlay { didPlay = true; played.fulfill() }
            if let target = externalPosition, event.kind == .progress, event.discontinuity,
               event.snapshot.position >= target {
                externalPosition = nil; jumped.fulfill()
            }
        }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(resource: try .init(id: "listening", url: file, title: "Synthetic"),
            owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertFalse(events.contains { $0.kind == .played })
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        XCTAssertFalse(events.contains { $0.kind == .played }, "Selecting rate while paused cannot start media")
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [played], timeout: 5)
        XCTAssertEqual(events.first { $0.kind == .played }?.rate, 2)
        XCTAssertEqual(events.first { $0.kind == .played }?.wantsPlayback, true)
        XCTAssertTrue(controller.setPreferredRate(1.5, attempt: attempt))
        let beforeSeek = events.count
        let position = controller.snapshot.position + 0.1
        XCTAssertTrue(controller.seek(to: position, attempt: attempt) { result in
            XCTAssertEqual(result.outcome, .completed); sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 5)
        XCTAssertTrue(events.dropFirst(beforeSeek).contains { $0.kind == .progress && $0.discontinuity })
        XCTAssertTrue(events.contains { $0.kind == .progress && $0.discontinuity && $0.rate == 1.5 })
        let external = CMTime(seconds: controller.player.currentTime().seconds + 0.1, preferredTimescale: 600)
        externalPosition = external.seconds
        let completed = await controller.player.seek(to: external, toleranceBefore: .zero, toleranceAfter: .zero)
        XCTAssertTrue(completed)
        await fulfillment(of: [jumped], timeout: 5)
        XCTAssertTrue(controller.pause(attempt: attempt))
        let paused = try XCTUnwrap(events.last { $0.kind == .paused })
        XCTAssertEqual(paused.attempt, attempt)
        XCTAssertFalse(paused.wantsPlayback)
        XCTAssertEqual(paused.rate, 1.5)
        XCTAssertTrue(controller.stop(attempt: attempt))
        let stopped = try XCTUnwrap(events.last)
        XCTAssertEqual(stopped.kind, .stopped)
        XCTAssertEqual(stopped.attempt, attempt)
        XCTAssertFalse(stopped.wantsPlayback)
        XCTAssertEqual(stopped.rate, 1.5)
        XCTAssertEqual(stopped.snapshot.position, paused.snapshot.position, accuracy: 0.05)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertTrue(events.allSatisfy { $0.observedAt > 0 && $0.occurredAt > 0 })
        XCTAssertTrue(zip(events, events.dropFirst()).allSatisfy { pair in pair.0.observedAt <= pair.1.observedAt })
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
#endif

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

import XCTest
import UIKit
import MeowAudio
import MeowContracts
@testable import MeowPlayback

/// Actual App-hosted PCM output with synthetic silence, not a listening or
/// physical Bluetooth/AirPlay acceptance test.
@MainActor
final class MeowfilmMusicAudioCoreHostTests: XCTestCase {
    func testSilentPreparationThenPreferredRateOutputEndsOnlyAfterPCMDrain() async throws {
        await waitUntil { UIApplication.shared.applicationState == .active }
        XCTAssertEqual(Bundle.main.bundleURL.lastPathComponent, "Meowfilm.app")
        let frameworks = try XCTUnwrap(Bundle.main.privateFrameworksURL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: frameworks
            .appendingPathComponent("CMeowAudioKernel.framework/CMeowAudioKernel").path))
        let file = try silentFile(seconds: 2)
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ended = expectation(description: "actual PCM output drained")
        var ends = 0
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .ended { ends += 1; ended.fulfill() }
        }
        addTeardownBlock { @MainActor in try await controller.close(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "pcm-drain",
            url: file, title: "Synthetic silence"), owner: "music", autoplay: false)
        await waitUntil { controller.snapshot.phase == .paused }
        let session = try XCTUnwrap(controller.activeAudioMedia?.session)
        XCTAssertFalse(session.hasOutput)
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertTrue(controller.setVolume(0, attempt: attempt))
        XCTAssertTrue(controller.setPreferredRate(1.5, attempt: attempt))
        XCTAssertTrue(controller.resume(attempt: attempt))
        await waitUntil { controller.snapshot.phase == .playing }
        XCTAssertTrue(session.hasOutput)
        XCTAssertTrue(session.isPlaying)
        XCTAssertEqual(controller.actualRate, 1.5, accuracy: 0.001)
        XCTAssertLessThanOrEqual(session.bufferedFrames, 8 * 4_096)
        await fulfillment(of: [ended], timeout: 10)
        XCTAssertEqual(ends, 1)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .ended)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertEqual(session.position, 2, accuracy: 1 / 48_000)
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(controller.actualRate, 0)
        try await controller.close()
        XCTAssertFalse(session.hasOutput)
    }

    func testPausedFractionalSeekAndResumeKeepOneOwnerAndCloseActualOutput() async throws {
        let file = try silentFile(seconds: 10)
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        var failures = 0
        let lease = try controller.bind(owner: "music") { event in
            if event.kind == .failed { failures += 1 }
        }
        addTeardownBlock { @MainActor in try await controller.close(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "pcm-seek",
            url: file, title: "Synthetic seek"), owner: "music", autoplay: false)
        await waitUntil { controller.snapshot.phase == .paused }
        let session = try XCTUnwrap(controller.activeAudioMedia?.session)
        XCTAssertTrue(controller.setVolume(0, attempt: attempt))
        XCTAssertTrue(controller.resume(attempt: attempt))
        await waitUntil { controller.snapshot.phase == .playing && controller.snapshot.position > 0.1 }
        XCTAssertTrue(controller.pause(attempt: attempt))
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(controller.actualRate, 0)
        let target = 3.312_567
        let sought = expectation(description: "fractional PCM seek completed")
        XCTAssertTrue(controller.seek(to: target, attempt: attempt) { result in
            XCTAssertEqual(result.outcome, .completed)
            XCTAssertEqual(result.actualPosition ?? -1, target, accuracy: 1 / 48_000)
            sought.fulfill()
        })
        await fulfillment(of: [sought], timeout: 5)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(controller.snapshot.position, target, accuracy: 1 / 48_000)
        XCTAssertTrue(controller.setPreferredRate(1.75, attempt: attempt))
        XCTAssertTrue(controller.resume(attempt: attempt))
        await waitUntil { controller.snapshot.phase == .playing && controller.snapshot.position > target + 0.1 }
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertTrue(controller.activeAudioMedia?.session === session)
        XCTAssertEqual(controller.actualRate, 1.75, accuracy: 0.001)
        XCTAssertLessThanOrEqual(session.bufferedFrames, 8 * 4_096)
        try await controller.close()
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(session.hasOutput)
        XCTAssertFalse(session.isPlaying)
        XCTAssertEqual(session.bufferedFrames, 0)
        XCTAssertEqual(failures, 0)
    }

    private func waitUntil(_ predicate: @escaping @MainActor () -> Bool) async {
        let ready = expectation(for: NSPredicate { _, _ in
            MainActor.assumeIsolated { predicate() }
        }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 10)
    }

    private func silentFile(seconds: UInt32) throws -> URL {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func number<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        let length = seconds * 48_000 * 4
        text("RIFF"); number(36 + length); text("WAVEfmt "); number(UInt32(16))
        number(UInt16(1)); number(UInt16(2)); number(UInt32(48_000)); number(UInt32(192_000))
        number(UInt16(4)); number(UInt16(16)); text("data"); number(length)
        data.append(Data(count: Int(length)))
        try data.write(to: file)
        return file
    }
}

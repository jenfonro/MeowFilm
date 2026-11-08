#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback

@MainActor final class PlaybackSystemAdmissionTests: XCTestCase {
    func testSystemPlayAndPausedToggleRequireOwnerAdmissionWithoutRawResume() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Paused local media is ready")
        var calls: [PlaybackPlayPauseCommand] = []
        var accepts = false
        let lease = try controller.bind(owner: "music", onPlayPause: { _, command in
            calls.append(command); return accepts
        }) { event in if event.kind == .prepared { ready.fulfill() } }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(resource: try .init(id: "owner-media", url: file, title: "test"),
                                             owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
        XCTAssertFalse(controller.systemCommand(.toggle, generation: attempt.id))
        XCTAssertEqual(calls, [.play, .play])
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.player.rate, 0)
        accepts = true
        XCTAssertTrue(controller.systemCommand(.play, generation: attempt.id))
        XCTAssertEqual(controller.player.rate, 0, "Scheduling admission must not also resume native media")
        XCTAssertTrue(controller.systemCommand(.pause, generation: attempt.id))
        XCTAssertEqual(calls.last, .pause, "An already-paused native item still forwards a newer pause intent")
        let count = calls.count
        lease.setEnabled(false)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
        XCTAssertEqual(calls.count, count, "Disabled admission must not fall through to raw transport")
        XCTAssertEqual(controller.player.rate, 0)
        lease.setEnabled(true)
        controller.stop(attempt: attempt)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
    }

    func testSystemPlayingTogglePassesPauseAndUnboundTransportStillWorks() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let controller = PlaybackController()
        let ready = expectation(description: "Local media ready")
        var calls: [PlaybackPlayPauseCommand] = []
        let lease = try controller.bind(owner: "music", onPlayPause: { _, command in
            calls.append(command); return true
        }) { event in if event.kind == .prepared { ready.fulfill() } }
        defer { lease.cancel(); controller.stop() }
        let attempt = controller.playAttempt(resource: try .init(id: "toggle-media", url: file, title: "test"),
                                             owner: "music", autoplay: false)
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [ready], timeout: 5)
        XCTAssertTrue(controller.resume(attempt: attempt))
        XCTAssertTrue(controller.systemCommand(.toggle, generation: attempt.id))
        XCTAssertEqual(calls, [.pause])
        // Native controls remain available to owners without domain admission.
        lease.cancel()
        XCTAssertTrue(controller.systemCommand(.pause, generation: attempt.id))
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertTrue(controller.systemCommand(.play, generation: attempt.id))
        XCTAssertEqual(calls, [.pause])
        XCTAssertTrue(controller.systemCommand(.toggle, generation: attempt.id))
        XCTAssertEqual(controller.snapshot.phase, .paused)
        let replacement = controller.playAttempt(resource: try .init(id: "other-media", url: file, title: "test"),
                                                 owner: "film", autoplay: false)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
        XCTAssertEqual(controller.currentAttempt, replacement)
    }

    func testCapturedOwnerRouteCannotCrossDisableRebindOrNewAttempt() async throws {
        let router = PlaybackOwnerRouter()
        let attempt = PlayAttempt(id: UUID(), owner: "music", resourceID: "media")
        router.updateContext(.init(attempt: attempt, phase: .paused))
        var calls: [PlaybackPlayPauseCommand] = []
        let first = try router.bind(owner: "music", onPlayPause: { _, command in
            calls.append(command); return true
        }) { _ in }
        defer { first.cancel() }
        let old = try XCTUnwrap(router.playPauseRoute)
        XCTAssertTrue(router.perform(.play, route: old))
        first.updateCommands(.init(next: { _ in true }))
        XCTAssertTrue(router.perform(.pause, route: old), "Queue navigation does not replace the resume owner")
        first.setEnabled(false)
        XCTAssertNotNil(router.playPauseRoute, "Disabled policy must not become unguarded transport")
        XCTAssertFalse(router.perform(.play, route: old))
        first.setEnabled(true)
        XCTAssertFalse(router.perform(.play, route: old))
        let enabled = try XCTUnwrap(router.playPauseRoute)
        let second = try router.bind(owner: "music", onPlayPause: { _, _ in XCTFail("Stale route"); return true }) { _ in }
        defer { second.cancel() }
        XCTAssertFalse(router.perform(.play, route: enabled))
        let rebound = try XCTUnwrap(router.playPauseRoute)
        router.updateContext(.init(attempt: .init(id: UUID(), owner: "music", resourceID: "media"), phase: .paused))
        XCTAssertFalse(router.perform(.play, route: rebound))
        XCTAssertEqual(calls, [.play, .pause])
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

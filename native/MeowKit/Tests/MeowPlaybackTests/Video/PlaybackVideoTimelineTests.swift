#if os(iOS) || os(macOS)
import XCTest
import Network
import MeowContracts
import MeowVideoKernel
@testable import MeowPlaybackVideo
@testable import MeowPlayback

@MainActor
final class PlaybackVideoTimelineTests: XCTestCase {
    private func fixture() throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
            .appendingPathComponent("ordinary.mp4")
    }

    func testActualEOFTailSampleKeepsEndedAndExplicitResumeRewinds() async throws {
        let native = try VideoSession()
        let decoder = makePlaybackVideoDecoder(session: native)
        let controller = PlaybackController(videoDecoderFactory: { decoder })
        let ended = expectation(description: "real video EOF")
        let sampled = expectation(description: "native periodic tail sample after EOF")
        let rewound = expectation(description: "explicit resume completes native rewind")
        var didEnd = false, didSample = false, replaying = false, didRewind = false
        var tail: Double?
        var failures = 0, releases = 0
        let film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .failed { failures += 1 }
            if event.kind == .ended, !didEnd { didEnd = true; ended.fulfill() }
            if replaying, !didRewind, event.kind == .progress, event.snapshot.position < 0.25 {
                didRewind = true
                controller.pause()
                rewound.fulfill()
            }
        }
        let media = try PlaybackVideoMediaLease(attemptID: "timeline",
            input: .init(resourceID: "timeline", url: fixture(), title: "synthetic")) { releases += 1 }
        let attempt = try await film.play(media, paused: true)
        XCTAssertTrue(decoder.seekable)
        let duration = try XCTUnwrap(controller.snapshot.duration)
        let forward = native.onProgress
        native.onProgress = { position in
            forward?(position)
            if didEnd, !didSample {
                didSample = true; tail = position; sampled.fulfill()
            }
        }
        XCTAssertTrue(controller.setVolume(0, attempt: attempt))
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [ended, sampled], timeout: 10)
        XCTAssertLessThan(try XCTUnwrap(tail), duration - 0.001, "Use a real below-duration tail frame")
        XCTAssertEqual(controller.snapshot.phase, .ended)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(native.output.rate, 0)
        replaying = true
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [rewound], timeout: 10)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertLessThan(native.position, 0.25)
        XCTAssertEqual(failures, 0)
        try await film.close()
        XCTAssertEqual(releases, 1)
    }

    func testKnownDurationNonRangeVideoRejectsSeekWithoutDisruptingSequentialPlayback() async throws {
        let peer = try SequentialVideoPeer(body: Data(contentsOf: fixture()))
        defer { peer.stop() }
        try await peer.start()
        let native = try VideoSession()
        let decoder = makePlaybackVideoDecoder(session: native)
        let controller = PlaybackController(videoDecoderFactory: { decoder })
        let ended = expectation(description: "nonseekable video still reaches actual EOF")
        var failures = 0, ends = 0, releases = 0
        let film = try bindWindowedVideo(controller, owner: "film") { event in
            if event.kind == .failed { failures += 1 }
            if event.kind == .ended { ends += 1; ended.fulfill() }
        }
        let media = try PlaybackVideoMediaLease(attemptID: "sequential",
            input: .init(resourceID: "sequential", url: peer.url, title: "synthetic")) { releases += 1 }
        let attempt = try await film.play(media, paused: true)
        XCTAssertGreaterThan(try XCTUnwrap(controller.snapshot.duration), 0)
        XCTAssertFalse(native.seekable)
        XCTAssertFalse(decoder.seekable)
        let before = controller.snapshot
        var completions = 0
        XCTAssertFalse(controller.systemCommand(.seek(1.5), generation: attempt.id))
        XCTAssertFalse(controller.seek(to: 1.5, attempt: attempt) { _ in completions += 1 })
        XCTAssertEqual(controller.snapshot, before)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(completions, 0)
        XCTAssertEqual(releases, 0)
        do {
            _ = try await decoder.seek(to: 1.5)
            XCTFail("The native boundary must also reject a nonseekable timeline")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .seekFailed) }
        XCTAssertTrue(native.output.hasPresentedFrame)
        XCTAssertTrue(controller.setVolume(0, attempt: attempt))
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        XCTAssertTrue(controller.resume(attempt: attempt))
        await fulfillment(of: [ended], timeout: 10)
        XCTAssertEqual(ends, 1)
        XCTAssertEqual(failures, 0)
        XCTAssertEqual(completions, 0)
        // Replay queues a rewind. Unsupported accepted work fails and retires
        // its owner instead of restarting from an unconfirmed position.
        XCTAssertTrue(controller.resume(attempt: attempt))
        XCTAssertEqual(controller.snapshot.phase, .failed)
        XCTAssertEqual(controller.lastError as? NativePlaybackError, .seekFailed)
        try await controller.drain()
        XCTAssertEqual(failures, 1)
        XCTAssertEqual(releases, 1)
        try await film.close()
        XCTAssertEqual(releases, 1)
    }
}

/// Fixed synthetic MP4 served as HTTP 200, even when FFmpeg requests Range.
private final class SequentialVideoPeer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meow.video.sequential.test")
    private let listener: NWListener
    private let body: Data
    private var connections: [UUID: NWConnection] = [:]
    private var port: UInt16 = 0

    init(body: Data) throws {
        self.body = body
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    var url: URL { URL(string: "http://127.0.0.1:\(port)/ordinary.mp4")! }

    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            var completed = false
            listener.stateUpdateHandler = { [self] state in
                guard !completed else { return }
                if case .ready = state {
                    completed = true; port = listener.port!.rawValue; continuation.resume()
                } else if case .failed(let error) = state {
                    completed = true; continuation.resume(throwing: error)
                }
            }
            listener.newConnectionHandler = { [self] connection in
                let id = UUID(); connections[id] = connection
                connection.start(queue: queue)
                receive(connection, id: id, accumulated: Data())
            }
            listener.start(queue: queue)
        }
    }

    private func receive(_ connection: NWConnection, id: UUID, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [self] data, _, complete, error in
            var header = accumulated
            if let data { header.append(data) }
            guard header.range(of: Data("\r\n\r\n".utf8)) != nil else {
                if complete || error != nil { finish(connection, id: id) }
                else { receive(connection, id: id, accumulated: header) }
                return
            }
            let response = Data(("HTTP/1.1 200 OK\r\nContent-Length: \(body.count)\r\n" +
                "Content-Type: video/mp4\r\nConnection: close\r\n\r\n").utf8) + body
            connection.send(content: response, completion: .contentProcessed { [self] _ in
                finish(connection, id: id)
            })
        }
    }

    private func finish(_ connection: NWConnection, id: UUID) {
        connection.cancel(); connections[id] = nil
    }

    func stop() {
        queue.sync {
            listener.stateUpdateHandler = nil; listener.newConnectionHandler = nil
            listener.cancel()
            connections.values.forEach { $0.cancel() }; connections.removeAll()
        }
    }
}
#endif

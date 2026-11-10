import XCTest
import Network
import Darwin
import CoreMedia
import CoreVideo
@testable import MeowVideoKernel

private enum SegmentFault: Equatable { case forbidden, disconnected, recoveredPrefetch }

/// A real loopback HTTP peer serving independent synthetic media.
private final class VideoHTTPPeer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meow.video.http.test")
    private let listener: NWListener
    private let files: URL
    private let fault: SegmentFault?
    private let resourceID: String
    private let referencePrefix: String?
    private var connections: [UUID: NWConnection] = [:]
    private var requests: [String: Int] = [:]
    private var rangeRequests: [String: [Int: Int]] = [:]
    private var rangeGate: (path: String, start: Int)?
    private var rangeHeld: (() -> Void)?
    private var heldResponses: [() -> Void] = []
    private var firstSegmentsSent = 0
    private var stopped = false
    private(set) var port: UInt16 = 0

    init(files: URL, fault: SegmentFault? = nil, resourceID: String = "issued",
         referencePrefix: String? = nil) throws {
        self.files = files; self.fault = fault; self.resourceID = resourceID
        self.referencePrefix = referencePrefix
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

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

    var prefix: String { "http://127.0.0.1:\(port)/media/\(resourceID)/" }
    func count(_ path: String) -> Int { queue.sync { requests[path, default: 0] } }
    func count(_ path: String, rangeStart: Int) -> Int { queue.sync { rangeRequests[path]?[rangeStart] ?? 0 } }
    var outstanding: Int { queue.sync { connections.count } }
    var successfulFirstSegments: Int { queue.sync { firstSegmentsSent } }

    func holdRange(_ path: String, start: Int, onRequest: @escaping () -> Void) {
        queue.sync { rangeGate = (path, start); rangeHeld = onRequest }
    }

    func releaseRanges() {
        queue.sync {
            rangeGate = nil; rangeHeld = nil
            let responses = heldResponses; heldResponses.removeAll()
            responses.forEach { $0() }
        }
    }

    func stop() {
        queue.sync {
            guard !stopped else { return }
            stopped = true
            listener.newConnectionHandler = nil; listener.stateUpdateHandler = nil
            listener.cancel()
            connections.values.forEach { $0.cancel() }; connections.removeAll()
            rangeGate = nil; rangeHeld = nil; heldResponses.removeAll()
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
            let text = String(decoding: header, as: UTF8.self)
            let target = text.split(separator: " ").dropFirst().first.map(String.init) ?? ""
            let path = String(target.split(separator: "?", maxSplits: 1).first ?? "")
            requests[path, default: 0] += 1
            if path.hasSuffix("/stall") {
                // No response: observe the real decoder cancel closing its TCP
                // connection rather than pretending a task flag drained IO.
                connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [self] _, _, _, _ in
                    finish(connection, id: id)
                }
                return
            }
            var status = "200 OK", type = "application/octet-stream", extra = ""
            let name = String(path.split(separator: "/").last ?? "")
            var body = (try? Data(contentsOf: files.appendingPathComponent(name))) ?? Data()
            let referenced = referencePrefix ?? "http://127.0.0.1:\(port)/media/other/"
            if name == "opaque-master" {
                body = Data("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=160000\nopaque-variant\n".utf8)
            } else if name == "opaque-variant" {
                let playlist = try! String(contentsOf: files.appendingPathComponent("variant.m3u8"))
                body = Data(playlist.replacingOccurrences(of: ".ts", with: "").utf8)
            } else if name.hasPrefix("hls-"), !name.contains(".") {
                body = try! Data(contentsOf: files.appendingPathComponent(name + ".ts"))
            } else if name == "absolute.m3u8" {
                body = Data(("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n" +
                    "\(referenced)hls-0.ts?synthetic-query=a%2Fb\n#EXT-X-ENDLIST\n").utf8)
            } else if name == "absolute-late.m3u8" {
                body = Data(("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nhls-0.ts\n#EXTINF:1,\n" +
                    "\(referenced)hls-1.ts?synthetic-query=a%2Fb\n#EXT-X-ENDLIST\n").utf8)
            } else if name == "absolute.mpd" {
                let manifest = try! String(contentsOf: files.appendingPathComponent("ordinary.mpd"))
                body = Data(manifest.replacingOccurrences(of: "initialization=\"", with: "initialization=\"\(referenced)")
                    .replacingOccurrences(of: "media=\"", with: "media=\"\(referenced)").utf8)
            } else if name == "redirect" {
                status = "302 Found"; extra = "Location: \(referenced)ordinary.mp4?synthetic-query=a%2Fb\r\n"; body = Data()
            } else if name == "malformed.mpd" {
                body = Data("<MPD><synthetic-xml-canary value='&missing;'></MPD>".utf8)
            }
            let secondSegment = name == "hls-1.ts" || name == "chunk-stream0-00002.m4s"
            if secondSegment && (fault == .forbidden || (fault == .recoveredPrefetch && requests[path] == 1)) {
                status = "403 Forbidden"; body = Data()
            }
            if name.hasSuffix(".m3u8") { type = "application/vnd.apple.mpegurl" }
            if name.hasSuffix(".mpd") { type = "application/dash+xml" }
            var rangeStart: Int?
            if status == "200 OK",
               let range = text.components(separatedBy: "\r\n").first(where: { $0.lowercased().hasPrefix("range: bytes=") }),
               let start = Int(range.components(separatedBy: "=").last?.split(separator: "-").first ?? ""),
               start < body.count {
                let total = body.count
                let bounds = range.components(separatedBy: "=").last!.split(separator: "-", omittingEmptySubsequences: false)
                let end = min(Int(bounds.last ?? "") ?? total - 1, total - 1)
                body = body.subdata(in: start..<(end + 1)); status = "206 Partial Content"
                extra += "Content-Range: bytes \(start)-\(end)/\(total)\r\n"
                rangeRequests[path, default: [:]][start, default: 0] += 1
                rangeStart = start
            }
            let length = body.count
            if secondSegment && fault == .disconnected { body = Data(body.prefix(32)) }
            let response = Data(("HTTP/1.1 \(status)\r\nContent-Length: \(length)\r\n" +
                "Content-Type: \(type)\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\(extra)\r\n").utf8) + body
            let send = { [self] in
                connection.send(content: response, completion: .contentProcessed { [self] error in
                    if error == nil && (name == "hls-0.ts" || name == "chunk-stream0-00001.m4s") {
                        firstSegmentsSent += 1
                    }
                    finish(connection, id: id)
                })
            }
            if let rangeGate, rangeGate.path == path, rangeGate.start == rangeStart {
                heldResponses.append(send)
                let notify = rangeHeld; rangeHeld = nil; notify?()
            } else { send() }
        }
    }

    private func finish(_ connection: NWConnection, id: UUID) {
        connection.cancel(); connections[id] = nil
    }
}

final class VideoHTTPTests: XCTestCase {
    private func peer(fault: SegmentFault? = nil, resourceID: String = "issued",
                      referencePrefix: String? = nil) async throws -> VideoHTTPPeer {
        let files = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
        let peer = try VideoHTTPPeer(files: files, fault: fault, resourceID: resourceID, referencePrefix: referencePrefix)
        try await peer.start()
        return peer
    }

    func testHTTPHLSAndDASHReadTheirGraphAndSeek() async throws {
        let peer = try await peer(); defer { peer.stop() }
        for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
            let decoder = try VideoDecoder()
            _ = try await decoder.open(url: URL(string: peer.prefix + name)!, format: format,
                clearKeys: [:])
            var videos = 0, audios = 0
            while let sample = try await decoder.read() {
                if sample.kind == .video { videos += 1 } else { audios += 1 }
            }
            XCTAssertEqual(videos, 30); XCTAssertGreaterThan(audios, 100)
            try await decoder.seek(to: 1.5)
            var picture: VideoSample?
            while let sample = try await decoder.read() {
                if sample.kind == .video { picture = sample; break }
            }
            XCTAssertEqual(try XCTUnwrap(picture).position, 1.5, accuracy: 0.15)
            await decoder.close()
        }
        XCTAssertGreaterThan(peer.count("/media/issued/variant.m3u8"), 0)
        XCTAssertGreaterThan(peer.count("/media/issued/hls-0.ts"), 0)
        XCTAssertGreaterThan(peer.count("/media/issued/init-stream0.m4s"), 0)
        XCTAssertGreaterThan(peer.count("/media/issued/init-stream1.m4s"), 0)
    }

    func testAbsoluteManifestReferencesAndRedirectAcrossOriginsDecodeAndSeek() async throws {
        let media = try await peer(); defer { media.stop() }
        let peer = try await peer(referencePrefix: media.prefix); defer { peer.stop() }
        XCTAssertNotEqual(peer.port, media.port)
        for (name, format, expectedVideo, target) in [
            ("absolute.m3u8", "hls", 10, 0.5), ("absolute.mpd", "dash", 30, 1.5), ("redirect", "mov", 30, 1.5)
        ] {
            let decoder = try VideoDecoder()
            addTeardownBlock { await decoder.close() }
            let info = try await decoder.open(url: URL(string: peer.prefix + name)!, format: format, clearKeys: [:])
            XCTAssertEqual(info.codec, "h264"); XCTAssertTrue(info.hasAudio)
            var videos = 0, audios = 0
            while let sample = try await decoder.read() {
                if sample.kind == .video { videos += 1 } else { audios += 1 }
            }
            XCTAssertEqual(videos, expectedVideo); XCTAssertGreaterThan(audios, 0)
            try await decoder.seek(to: target)
            var picture: VideoSample?
            while let sample = try await decoder.read() {
                if sample.kind == .video { picture = sample; break }
            }
            XCTAssertEqual(try XCTUnwrap(picture).position, target, accuracy: 0.15)
            await decoder.close()
        }
        for name in ["hls-0.ts", "ordinary.mp4", "init-stream0.m4s", "init-stream1.m4s",
                     "chunk-stream0-00002.m4s", "chunk-stream1-00002.m4s"] {
            XCTAssertGreaterThan(media.count("/media/issued/" + name), 0)
        }
    }

    func testLaterAbsoluteHLSSegmentOutsideStartingPathDecodesAndSeeks() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let decoder = try VideoDecoder()
        addTeardownBlock { await decoder.close() }
        _ = try await decoder.open(url: URL(string: peer.prefix + "absolute-late.m3u8")!, format: "hls", clearKeys: [:])
        var videos = 0, audios = 0
        while let sample = try await decoder.read() {
            if sample.kind == .video { videos += 1 } else { audios += 1 }
        }
        XCTAssertEqual(videos, 20); XCTAssertGreaterThan(audios, 0)
        try await decoder.seek(to: 1.5)
        var picture: VideoSample?
        while let sample = try await decoder.read() {
            if sample.kind == .video { picture = sample; break }
        }
        XCTAssertEqual(try XCTUnwrap(picture).position, 1.5, accuracy: 0.15)
        await decoder.close()
        XCTAssertGreaterThan(peer.count("/media/issued/hls-0.ts"), 0)
        XCTAssertGreaterThan(peer.count("/media/other/hls-1.ts"), 0)
    }

    func testOpaqueRuntimeHLSPathsAreNotRejectedByFilenameGuessing() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let decoder = try VideoDecoder()
        _ = try await decoder.open(url: URL(string: peer.prefix + "opaque-master")!, format: "hls",
            clearKeys: [:])
        var pictures = 0
        while let sample = try await decoder.read() {
            if sample.kind == .video { pictures += 1 }
        }
        XCTAssertEqual(pictures, 30)
        XCTAssertGreaterThan(peer.count("/media/issued/hls-0"), 0)
        await decoder.close()
    }

    func testLaterHLSAndDASHSegment403OrDisconnectIsTypedFailureNotEOF() async throws {
        for fault in [SegmentFault.forbidden, .disconnected] {
            for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
                let peer = try await peer(fault: fault); defer { peer.stop() }
                let decoder = try VideoDecoder()
                var failure: VideoKernelFailure?
                do {
                    _ = try await decoder.open(url: URL(string: peer.prefix + name)!, format: format,
                        clearKeys: [:])
                    while let _ = try await decoder.read() {}
                } catch { failure = error as? VideoKernelFailure }
                await decoder.close()
                XCTAssertEqual(failure, .transportFailed, "\(format) \(fault) cannot be successful EOF")
                XCTAssertGreaterThan(peer.successfulFirstSegments, 0, "The valid first media segment was actually sent")
                let second = format == "hls" ? "hls-1.ts" : "chunk-stream0-00002.m4s"
                XCTAssertGreaterThan(peer.count("/media/issued/" + second), 0)
            }
        }
    }

    @MainActor
    func testLaterHLSAndDASHSegmentFailureNeverPublishesSessionEnd() async throws {
        for fault in [SegmentFault.forbidden, .disconnected] {
            for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
                let peer = try await peer(fault: fault); defer { peer.stop() }
                let session = try VideoSession()
                try await hostVideoOutput(session)
                var terminal: VideoKernelFailure?
                var ended = false
                let failed = expectation(description: "\(format) \(fault) is a failure")
                session.onEnd = { ended = true }
                session.onFailure = { error in terminal = error; failed.fulfill() }
                do {
                    try await session.prepare(url: URL(string: peer.prefix + name)!, format: format,
                        clearKeys: [:], position: 0)
                    try await session.renderFirstVideoFrame()
                    session.commit(rate: 1, volume: 0)
                } catch {
                    terminal = error as? VideoKernelFailure
                    failed.fulfill()
                }
                await fulfillment(of: [failed], timeout: 8)
                await session.close()
                XCTAssertEqual(terminal, .transportFailed)
                XCTAssertFalse(ended, "Transport failure must not become natural onEnd")
                XCTAssertGreaterThan(peer.successfulFirstSegments, 0)
            }
        }
    }

    @MainActor
    func testExplicitResumeAfterSeekCancelledBeforeOperationStartsReadsAndDisplaysLongStreamToEOF() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: URL(string: peer.prefix + "seek-cancel-long.m3u8")!, format: "hls",
            clearKeys: [:], position: 0)
        XCTAssertEqual(try XCTUnwrap(session.info?.duration), 24, accuracy: 0.1)
        try await session.renderFirstVideoFrame()
        let firstPixels = try XCTUnwrap(displayedLuma(session))
        let firstPlayback = expectation(description: "actual feeding displays a later moving picture")
        let ended = expectation(description: "resumed long stream reaches normal decoded EOF")
        var endCount = 0
        var failures: [VideoKernelFailure] = []
        var advanced = false
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        session.onEnd = { endCount += 1; ended.fulfill() }
        session.onProgress = { position in
            if !advanced, position >= 1 {
                self.checkDisplayedLuma(session, matches: { $0 != firstPixels }) {
                    advanced = true; firstPlayback.fulfill()
                }
            }
        }
        session.commit(rate: 1, volume: 0)
        await fulfillment(of: [firstPlayback], timeout: 8)
        session.setRate(0)
        let pausedPosition = session.position
        XCTAssertLessThan(pausedPosition, 4)
        let mediaPath = "/media/issued/seek-cancel-long.ts"
        // The independent fixture's final byte range begins at 22 seconds.
        let tailOffset = 296_476
        XCTAssertEqual(peer.count(mediaPath, rangeStart: tailOffset), 0, "The paused producer has not read the tail")

        var seeking: Task<Double, Error>?
        var interruptions = 0
        session.onSeekInterrupted = {
            interruptions += 1
            seeking?.cancel()
        }
        // The synchronous witness cancels this parent on MainActor after
        // interrupt and before the operation Task exists. Its cancellation
        // handler then cancels that Task before MainActor can run its check.
        seeking = Task { try await session.seek(to: 18) }
        let operation = try XCTUnwrap(seeking)
        switch await operation.result {
        case .success: XCTFail("The cancelled seek must not reach its target")
        case .failure(let error):
            XCTAssertTrue(error is CancellationError || (error as? VideoKernelFailure) == .cancelled)
        }
        session.onSeekInterrupted = nil
        XCTAssertEqual(interruptions, 1)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertEqual(session.position, pausedPosition, accuracy: 0.15)
        XCTAssertEqual(endCount, 0, "Interruption is not natural EOF")
        XCTAssertTrue(failures.isEmpty)
        let tailRequestsBeforeResume = peer.count(mediaPath, rangeStart: tailOffset)
        XCTAssertEqual(tailRequestsBeforeResume, 0)
        let requestsBeforeResume = peer.count(mediaPath)

        let resumedNearPause = expectation(description: "explicit resume keeps the displayed position")
        let displayedTail = expectation(description: "the actual renderer displays the fixture's late white frames")
        var resumed = false, displayedWhite = false
        session.onProgress = { position in
            if !resumed, position > pausedPosition + 0.2 {
                XCTAssertLessThan(position, pausedPosition + 2, "The old seek target must not revive")
                resumed = true; resumedNearPause.fulfill()
            }
            if !displayedWhite, position >= 20 {
                self.checkDisplayedLuma(session, matches: { $0 != firstPixels && $0.allSatisfy { $0 > 220 } }) {
                    displayedWhite = true; displayedTail.fulfill()
                }
            }
        }
        session.setRate(4)
        await fulfillment(of: [resumedNearPause, displayedTail, ended], timeout: 20, enforceOrder: true)
        let tailRequestsAfterResume = peer.count(mediaPath, rangeStart: tailOffset)
        XCTAssertGreaterThan(tailRequestsAfterResume, tailRequestsBeforeResume, "Resume must perform new real HTTP tail reads")
        XCTAssertGreaterThan(peer.count(mediaPath), requestsBeforeResume)
        XCTAssertTrue(displayedWhite, "Clock progress cannot substitute for displayed pixels")
        XCTAssertEqual(endCount, 1)
        XCTAssertGreaterThanOrEqual(session.position, 23.9)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertTrue(failures.isEmpty)
        print("IKF03 interrupts=\(interruptions) tailBefore=\(tailRequestsBeforeResume) tailAfter=\(tailRequestsAfterResume) " +
              "displayedWhite=\(displayedWhite) normalEOF=\(endCount) finalPosition=\(session.position) failures=\(failures.count)")
        await session.close()
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
        XCTAssertNil(session.info)
    }

    @MainActor
    func testRateChangeDuringBlockedSeekKeepsTargetAndDisplaysLongStreamToEOF() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: URL(string: peer.prefix + "seek-cancel-long.m3u8")!, format: "hls",
            clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        let firstPixels = try XCTUnwrap(displayedLuma(session))
        session.commit(rate: 0, volume: 0)
        let pausedPosition = session.position
        let mediaPath = "/media/issued/seek-cancel-long.ts"
        let targetOffset = 249_852, tailOffset = 296_476
        XCTAssertEqual(peer.count(mediaPath, rangeStart: targetOffset), 0)
        XCTAssertEqual(peer.count(mediaPath, rangeStart: tailOffset), 0)
        let targetHeld = expectation(description: "seek to 18 is waiting on actual HTTP bytes")
        peer.holdRange(mediaPath, start: targetOffset) { targetHeld.fulfill() }
        var interruptions = 0, completed = false, endCount = 0
        var failures: [VideoKernelFailure] = []
        let ended = expectation(description: "rate two seek feeds to real decoded EOF")
        let extraSeek = expectation(description: "rate change cannot create a replacement seek")
        extraSeek.isInverted = true
        session.onSeekInterrupted = {
            interruptions += 1
            if interruptions > 1 { extraSeek.fulfill() }
        }
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        session.onEnd = { endCount += 1; ended.fulfill() }
        let seeking = Task {
            defer { completed = true }
            return try await session.seek(to: 18)
        }
        await fulfillment(of: [targetHeld], timeout: 8)
        XCTAssertFalse(completed)
        XCTAssertEqual(interruptions, 1)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertEqual(session.position, pausedPosition, accuracy: 0.15)
        session.setRate(2)
        XCTAssertEqual(session.output.rate, 0, "Do not run the old-position clock while the seek needs bytes")
        // The real target request remains held while queued commands get a
        // turn; this observation interval does not choose a cancellation race.
        await fulfillment(of: [extraSeek], timeout: 0.2)
        XCTAssertFalse(completed)
        peer.releaseRanges()
        let position = try await seeking.value
        XCTAssertEqual(position, 18, accuracy: 0.15, "A rate command cannot replace the user's seek target")
        XCTAssertEqual(interruptions, 1, "No current-position resume seek may be started")
        XCTAssertEqual(session.output.rate, 2)
        XCTAssertEqual(endCount, 0)
        let displayedTail = expectation(description: "rate two feeding displays actual late white frames")
        var displayedWhite = false
        session.onProgress = { position in
            if !displayedWhite, position >= 20 {
                self.checkDisplayedLuma(session, matches: { $0 != firstPixels && $0.allSatisfy { $0 > 220 } }) {
                    displayedWhite = true; displayedTail.fulfill()
                }
            }
        }
        await fulfillment(of: [displayedTail, ended], timeout: 12, enforceOrder: true)
        XCTAssertTrue(displayedWhite)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: targetOffset), 0)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: tailOffset), 0)
        XCTAssertEqual(endCount, 1)
        XCTAssertTrue(failures.isEmpty)
        XCTAssertGreaterThanOrEqual(session.position, 23.9)
        XCTAssertEqual(session.output.rate, 0)
        print("IKF03 rateDuringSeek target=\(position) requestedRate=2 interrupts=\(interruptions) " +
              "tailReads=\(peer.count(mediaPath, rangeStart: tailOffset)) displayedWhite=\(displayedWhite) " +
              "normalEOF=\(endCount) failures=\(failures.count)")
        await session.close()
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
        XCTAssertNil(session.info)
    }

    @MainActor
    func testSupersededSeekReceiptCannotClearNewBlockedSeek() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: URL(string: peer.prefix + "seek-cancel-long.m3u8")!, format: "hls",
            clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        session.commit(rate: 0, volume: 0)
        let mediaPath = "/media/issued/seek-cancel-long.ts"
        let firstHeld = expectation(description: "old seek holds real IO before replacement")
        peer.holdRange(mediaPath, start: 82_908) { firstHeld.fulfill() }
        var interruptions = 0
        var failures: [VideoKernelFailure] = []
        let extraSeek = expectation(description: "old completion cannot trigger a third seek")
        extraSeek.isInverted = true
        session.onSeekInterrupted = {
            interruptions += 1
            if interruptions > 2 { extraSeek.fulfill() }
        }
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        let first = Task { try await session.seek(to: 6) }
        await fulfillment(of: [firstHeld], timeout: 8)
        let secondHeld = expectation(description: "new seek holds IO after old operation has drained")
        peer.holdRange(mediaPath, start: 249_852) { secondHeld.fulfill() }
        let second = Task { try await session.seek(to: 18) }
        await fulfillment(of: [secondHeld], timeout: 8)
        switch await first.result {
        case .success: XCTFail("Superseded seek cannot publish its old target")
        case .failure(let error):
            let failure = error as? VideoKernelFailure
            XCTAssertTrue(error is CancellationError || failure == .cancelled || failure == .interrupted)
        }
        session.setRate(2)
        await fulfillment(of: [extraSeek], timeout: 0.2)
        peer.releaseRanges()
        let position = try await second.value
        XCTAssertEqual(position, 18, accuracy: 0.15)
        XCTAssertEqual(interruptions, 2, "Old completion cannot hide the newer active seek")
        XCTAssertEqual(session.output.rate, 2)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
        XCTAssertNil(session.info)
    }

    @MainActor
    func testQueuedResumeCannotSupersedeNewUserSeek() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: URL(string: peer.prefix + "seek-cancel-long.m3u8")!, format: "hls",
            clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        session.commit(rate: 0, volume: 0)
        var cancelled: Task<Double, Error>?
        session.onSeekInterrupted = { cancelled?.cancel() }
        cancelled = Task { try await session.seek(to: 6) }
        let operation = try XCTUnwrap(cancelled)
        switch await operation.result {
        case .success: XCTFail("Early-cancelled operation cannot seek")
        case .failure(let error):
            XCTAssertTrue(error is CancellationError || (error as? VideoKernelFailure) == .cancelled)
        }
        var interruptions = 0
        var failures: [VideoKernelFailure] = []
        session.onSeekInterrupted = { interruptions += 1 }
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        session.setRate(2)
        // Enter the newer seek on this MainActor task before the queued
        // automatic resume can run. Its captured revision is now stale.
        let position = try await session.seek(to: 18)
        XCTAssertEqual(position, 18, accuracy: 0.15)
        XCTAssertEqual(interruptions, 1)
        XCTAssertEqual(session.output.rate, 2)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
        XCTAssertNil(session.info)
    }

    @MainActor private var pixelRead: Task<Void, Never>?

    /// copyDisplayedPixelBuffer returns nil at a nonzero rate. Pause the actual
    /// renderer timebase before reading, then restore the test's playback rate.
    @MainActor
    private func checkDisplayedLuma(_ session: VideoSession,
                                    matches: @escaping @MainActor ([UInt8]) -> Bool,
                                    onMatch: @escaping @MainActor () -> Void) {
        guard pixelRead == nil else { return }
        let task = Task {
            defer { self.pixelRead = nil }
            let rate = session.output.rate
            let timebase = session.output.synchronizer.timebase
            let updates = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
            let observer = NotificationCenter.default.addObserver(
                forName: Notification.Name(rawValue: kCMTimebaseNotification_EffectiveRateChanged as String),
                object: timebase, queue: nil
            ) { _ in updates.continuation.yield(()) }
            defer {
                NotificationCenter.default.removeObserver(observer)
                updates.continuation.finish()
                if !Task.isCancelled { session.setRate(rate) }
            }
            // setRate updates its rate property synchronously but applies the
            // timebase change asynchronously. Observe before requesting pause.
            session.setRate(0)
            var iterator = updates.stream.makeAsyncIterator()
            while CMTimebaseGetEffectiveRate(timebase) != 0 {
                guard let _ = await iterator.next() else { return }
            }
            guard !Task.isCancelled, let pixels = self.displayedLuma(session), matches(pixels) else { return }
            onMatch()
        }
        pixelRead = task
        addTeardownBlock { task.cancel(); await task.value }
    }

    @MainActor
    private func displayedLuma(_ session: VideoSession) -> [UInt8]? {
        if #available(iOS 17.4, macOS 14.4, *),
           let pixel = session.output.displayLayer.sampleBufferRenderer.displayedPixelBuffer(),
           CVPixelBufferIsPlanar(pixel),
           [kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelFormatType_420YpCbCr8BiPlanarFullRange].contains(CVPixelBufferGetPixelFormatType(pixel)),
           CVPixelBufferLockBaseAddress(pixel, .readOnly) == kCVReturnSuccess {
            defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
            guard let bytes = CVPixelBufferGetBaseAddressOfPlane(pixel, 0)?.assumingMemoryBound(to: UInt8.self) else { return nil }
            let width = CVPixelBufferGetWidthOfPlane(pixel, 0), height = CVPixelBufferGetHeightOfPlane(pixel, 0)
            let stride = CVPixelBufferGetBytesPerRowOfPlane(pixel, 0)
            return (height / 4..<height * 3 / 4).flatMap { row in
                Array(UnsafeBufferPointer(start: bytes.advanced(by: row * stride + width / 4), count: width / 2))
            }
        }
        return nil
    }

    func testFailedHLSReadAheadThenSuccessfulSameSegmentDoesNotLatchFailure() async throws {
        let peer = try await peer(fault: .recoveredPrefetch); defer { peer.stop() }
        let decoder = try VideoDecoder()
        _ = try await decoder.open(url: URL(string: peer.prefix + "master.m3u8")!, format: "hls",
            clearKeys: [:])
        var pictures = 0
        while let sample = try await decoder.read() {
            if sample.kind == .video { pictures += 1 }
        }
        await decoder.close()
        XCTAssertEqual(pictures, 30, "A failed speculative request must not poison the successful real read")
        XCTAssertGreaterThanOrEqual(peer.count("/media/issued/hls-1.ts"), 2, "The exact failed URL was successfully retried")
    }

    func testMalformedDASHHasFixedFailureAndNoRawXMLOrAddressOnStderr() async throws {
        let peer = try await peer(resourceID: "synthetic-capability-canary"); defer { peer.stop() }
        let captured = try await captureStandardError {
            for suffix in ["?signature=synthetic-token-canary", ""] {
                let decoder = try VideoDecoder()
                var failure: VideoKernelFailure?
                do {
                    _ = try await decoder.open(url: URL(string: peer.prefix + "malformed.mpd" + suffix)!,
                        format: "dash", clearKeys: [:])
                } catch { failure = error as? VideoKernelFailure }
                await decoder.close()
                XCTAssertEqual(failure, .formatFailed, "Malformed XML must take the fixed failure path")
            }
        }
        XCTAssertTrue(captured.isEmpty, "No raw XML diagnostics may escape the actual DASH parser")
        for canary in ["synthetic-token-canary", "synthetic-capability-canary", "synthetic-xml-canary"] {
            XCTAssertNil(captured.range(of: Data(canary.utf8)))
        }
        XCTAssertEqual(peer.count("/media/synthetic-capability-canary/malformed.mpd"), 2)
    }

    private func captureStandardError(_ operation: () async throws -> Void) async throws -> Data {
        fflush(stderr)
        let saved = dup(STDERR_FILENO)
        guard saved >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        defer { Darwin.close(saved) }
        let pipe = Pipe()
        guard dup2(pipe.fileHandleForWriting.fileDescriptor, STDERR_FILENO) >= 0 else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
        }
        var failure: Error?
        do { try await operation() } catch { failure = error }
        fflush(stderr)
        let restored = dup2(saved, STDERR_FILENO)
        pipe.fileHandleForWriting.closeFile()
        let captured = pipe.fileHandleForReading.readDataToEndOfFile()
        pipe.fileHandleForReading.closeFile()
        guard restored >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        if let failure { throw failure }
        return captured
    }

    func testCancellationClosesActualStalledHTTPConnectionBeforeCloseReturns() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let decoder = try VideoDecoder()
        let operation = Task {
            try await decoder.open(url: URL(string: peer.prefix + "stall")!, format: nil,
                clearKeys: [:])
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while peer.count("/media/issued/stall") == 0, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(peer.count("/media/issued/stall"), 1)
        decoder.cancel()
        do { _ = try await operation.value; XCTFail("cancelled IO cannot prepare") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        await decoder.close()
        while peer.outstanding != 0, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(peer.outstanding, 0)
    }
}

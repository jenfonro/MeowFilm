#if os(macOS)
import XCTest
import AVFoundation
import CoreVideo
import Foundation
import Network
import MeowContracts
import MeowPlaybackVideo
@testable import MeowPlayback

/// Serves only the named synthetic fixtures; every media request must carry the
/// receipt's exact credentials. All mutable server state stays on its queue.
private final class EngineIntegrationHTTPPeer: @unchecked Sendable {
    enum ResponseStatus: Int { case ok = 200, forbidden = 403 }

    struct Request {
        let path: String
        let headers: [String: String]
        let authorized: Bool
    }

    private let queue = DispatchQueue(label: "meow.video.engine.integration.http")
    private let listener: NWListener
    private let files: [String: Data]
    private let requiredHeaders: [String: String]
    private let responseStatus: ResponseStatus
    private var connections: [UUID: NWConnection] = [:]
    private var requests: [Request] = []
    private var startup: CheckedContinuation<UInt16, Error>?
    private var stopWaiters: [CheckedContinuation<Void, Never>] = []
    private var stopping = false
    private var listenerCancelled = false

    init(files: [String: Data], requiredHeaders: [String: String],
         responseStatus: ResponseStatus = .ok) throws {
        self.files = files
        self.requiredHeaders = requiredHeaders
        self.responseStatus = responseStatus
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            startup = continuation
            listener.stateUpdateHandler = { [self] state in
                switch state {
                case .ready:
                    let continuation = startup; startup = nil
                    continuation?.resume(returning: listener.port!.rawValue)
                case .failed(let error):
                    let continuation = startup; startup = nil
                    continuation?.resume(throwing: error)
                    listener.cancel()
                case .cancelled:
                    listenerCancelled = true
                    let continuation = startup; startup = nil
                    continuation?.resume(throwing: CancellationError())
                    finishStopIfDrained()
                default: break
                }
            }
            listener.newConnectionHandler = { [self] connection in
                let id = UUID()
                connections[id] = connection
                connection.stateUpdateHandler = { [self] state in
                    if case .failed = state { connection.cancel() }
                    if case .cancelled = state {
                        connection.stateUpdateHandler = nil
                        connections[id] = nil
                        finishStopIfDrained()
                    }
                }
                connection.start(queue: queue)
                receive(connection, accumulated: Data())
            }
            listener.start(queue: queue)
        }
    }

    var observations: [Request] { queue.sync { requests } }
    var activeConnectionCount: Int { queue.sync { connections.count } }

    func stop() async {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                stopWaiters.append(continuation)
                stopping = true
                listener.newConnectionHandler = nil
                listener.cancel()
                connections.values.forEach { $0.cancel() }
                finishStopIfDrained()
            }
        }
    }

    private func finishStopIfDrained() {
        guard stopping, listenerCancelled, connections.isEmpty else { return }
        listener.stateUpdateHandler = nil
        let waiters = stopWaiters; stopWaiters.removeAll()
        waiters.forEach { $0.resume() }
    }

    private func receive(_ connection: NWConnection, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [self] data, _, complete, error in
            guard !stopping else { connection.cancel(); return }
            var header = accumulated
            if let data { header.append(data) }
            guard let end = header.range(of: Data("\r\n\r\n".utf8)) else {
                if complete || error != nil { connection.cancel() }
                else { receive(connection, accumulated: header) }
                return
            }
            let lines = String(decoding: header[..<end.lowerBound], as: UTF8.self)
                .components(separatedBy: "\r\n")
            let requestLine = (lines.first ?? "").split(separator: " ")
            let method = requestLine.first.map(String.init) ?? ""
            let target = requestLine.dropFirst().first.map(String.init) ?? ""
            let path = String(target.split(separator: "?", maxSplits: 1).first ?? "")
            var fields: [String: String] = [:]
            for line in lines.dropFirst() {
                guard let colon = line.firstIndex(of: ":") else { continue }
                let name = line[..<colon].lowercased()
                let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
                fields[name] = fields[name].map { $0 + ", " + value } ?? value
            }
            let authorized = files[path] != nil && requiredHeaders.allSatisfy { fields[$0.key] == $0.value }
            requests.append(Request(path: path, headers: fields, authorized: authorized))
            guard authorized, let file = files[path] else {
                send(connection, status: "403 Forbidden", body: Data(), length: 0)
                return
            }
            if responseStatus == .forbidden {
                send(connection, status: "403 Forbidden", body: Data(), length: 0)
                return
            }
            var body = file, status = "200 OK", extra = ""
            if let range = fields["range"], range.hasPrefix("bytes=") {
                let bounds = range.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false)
                guard bounds.count == 2, let start = Int(bounds[0]), start >= 0, start < file.count else {
                    send(connection, status: "416 Range Not Satisfiable", body: Data(), length: 0,
                         extra: "Content-Range: bytes */\(file.count)\r\n")
                    return
                }
                let last = min(Int(bounds[1]) ?? file.count - 1, file.count - 1)
                guard last >= start else { connection.cancel(); return }
                body = file.subdata(in: start..<(last + 1))
                status = "206 Partial Content"
                extra = "Content-Range: bytes \(start)-\(last)/\(file.count)\r\n"
            }
            let type = path.hasSuffix(".mpd") ? "application/dash+xml" :
                path.hasSuffix(".mp4") ? "video/mp4" : "application/octet-stream"
            send(connection, status: status, body: method == "HEAD" ? Data() : body,
                 length: body.count, extra: extra + "Content-Type: \(type)\r\nAccept-Ranges: bytes\r\n")
        }
    }

    private func send(_ connection: NWConnection, status: String, body: Data, length: Int, extra: String = "") {
        let response = Data(("HTTP/1.1 \(status)\r\nContent-Length: \(length)\r\n" +
            "\(extra)Connection: close\r\n\r\n").utf8) + body
        connection.send(content: response, completion: .contentProcessed { _ in connection.cancel() })
    }
}

@MainActor
final class PlaybackVideoEngineIntegrationTests: XCTestCase {
    private enum TestFailure: Error { case deadline, pixelStorage }

    private var fixtures: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("VideoKernel/Tests/VideoKernelTests/Fixtures")
    }

    private func bounded<T: Sendable>(_ name: String, timeout: Double = 30,
                                     operation: @escaping @MainActor () async throws -> T) async throws -> T {
        let done = expectation(description: name)
        var result: Result<T, Error>?
        let task = Task { @MainActor in
            do { result = .success(try await operation()) }
            catch { result = .failure(error) }
            done.fulfill()
        }
        defer { task.cancel() }
        let waited = await XCTWaiter.fulfillment(of: [done], timeout: timeout)
        XCTAssertEqual(waited, .completed, name)
        guard let result else { throw TestFailure.deadline }
        return try result.get()
    }

    private func displayedBytes(_ decoder: any PlaybackVideoDecoding) throws -> [UInt8] {
        let pixel: CVPixelBuffer
        if let layer = decoder.output.playerLayer {
            pixel = try XCTUnwrap(layer.displayedPixelBuffer(), "Native output must have displayed pixels")
        } else {
            let layer = try XCTUnwrap(decoder.output.displayLayer)
            pixel = try XCTUnwrap(layer.sampleBufferRenderer.displayedPixelBuffer(),
                                 "KS output must have displayed pixels, not just a decoded frame")
        }
        let width = CVPixelBufferGetWidth(pixel), height = CVPixelBufferGetHeight(pixel)
        XCTAssertEqual(width, 96)
        XCTAssertEqual(height, 64)
        let locked = CVPixelBufferLockBaseAddress(pixel, .readOnly)
        XCTAssertEqual(locked, kCVReturnSuccess)
        guard locked == kCVReturnSuccess else { throw TestFailure.pixelStorage }
        defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
        let planar = CVPixelBufferIsPlanar(pixel)
        let base = planar ? CVPixelBufferGetBaseAddressOfPlane(pixel, 0) : CVPixelBufferGetBaseAddress(pixel)
        let stride = planar ? CVPixelBufferGetBytesPerRowOfPlane(pixel, 0) : CVPixelBufferGetBytesPerRow(pixel)
        let bytes = try XCTUnwrap(base).assumingMemoryBound(to: UInt8.self)
        let picture = (0..<height).flatMap {
            Array(UnsafeBufferPointer(start: bytes.advanced(by: $0 * stride), count: min(width, stride)))
        }
        XCTAssertGreaterThan(Set(picture).count, 4, "Synthetic testsrc output must not be blank")
        return picture
    }

    /// Uses the real factory, controller and window-backed lease. No decoder or
    /// first-frame callback is substituted, including during the fallback.
    private func play(_ name: String, graph: [String], id: String,
                      preferredEngine: PlaybackVideoEngine = .aether,
                      actualEngine: PlaybackVideoEngine,
                      decryption: PlaybackVideoDecryption? = nil) async throws -> [UInt8] {
        executionTimeAllowance = 90
        let resourceID = "integration-\(id)", attemptID = "attempt-\(id)"
        let context = try PlaybackVideoRequestContext(headers: [
            "Authorization": "Bearer synthetic-\(id)",
            "Referer": "https://referer.invalid/\(id)",
            "User-Agent": "MeowPlaybackIntegration/1.0",
            "X-Fixture-Receipt": resourceID
        ], cookie: "integration_fixture=\(id)")
        var files: [String: Data] = [:]
        for file in graph {
            let data = try Data(contentsOf: fixtures.appendingPathComponent(file))
            XCTAssertFalse(data.isEmpty, "The real fixture must exist and contain media")
            files["/media/\(resourceID)/\(file)"] = data
        }
        let peer = try EngineIntegrationHTTPPeer(files: files, requiredHeaders: context.headers)
        addTeardownBlock { [self] in
            try await bounded("Drain loopback listener and connections", timeout: 10) { await peer.stop() }
        }
        let port = try await bounded("Start loopback server", timeout: 10) { try await peer.start() }
        let url = try XCTUnwrap(URL(string: "http://127.0.0.1:\(port)/media/\(resourceID)/\(name)"))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        let unauthorized = URLSession(configuration: configuration)
        defer { unauthorized.invalidateAndCancel() }
        let (_, denial) = try await unauthorized.data(from: url)
        XCTAssertEqual((denial as? HTTPURLResponse)?.statusCode, 403,
                       "The peer must reject a real request without the receipt's credentials")

        var decoder: (any PlaybackVideoDecoding)?
        var factories = 0, releases = 0
        var events: [PlaybackVideoEvent] = []
        let controller = PlaybackController(videoDecoderFactory: {
            factories += 1
            let real = try MeowPlaybackVideo.makePlaybackVideoDecoder(engine: preferredEngine)
            XCTAssertEqual(real.engine, preferredEngine)
            decoder = real
            return real
        })
        let lease = try bindWindowedVideo(controller, owner: "film") { event in
            XCTAssertEqual(event.attemptID, attemptID)
            XCTAssertEqual(event.snapshot.resourceID, resourceID)
            if [.preparing, .prepared, .started].contains(event.kind) {
                XCTAssertEqual(releases, 0, "An internal kernel replacement cannot release the media receipt")
            }
            events.append(event)
        }
        let input = try PlaybackVideoInput(resourceID: resourceID, url: url, title: "Synthetic integration",
            format: name.hasSuffix(".mpd") ? .dash : .progressive,
            decryption: decryption, requestContext: context)
        let media = try PlaybackVideoMediaLease(attemptID: attemptID, input: input) {
            releases += 1
            XCTAssertEqual(decoder?.output.rate, 0)
            XCTAssertNil(decoder?.output.presentationView.superview,
                         "Receipt release follows actual native output drain")
        }
        let attempt = try await bounded("Prepare, display and commit \(name)") {
            try await lease.play(media, paused: true, initialRate: 1, waitForCommit: {
                XCTAssertEqual(releases, 0)
                XCTAssertEqual(decoder?.engine, actualEngine)
            })
        }
        let real = try XCTUnwrap(decoder)
        XCTAssertEqual(real.engine, actualEngine)
        XCTAssertEqual(events.filter { $0.kind == .preparing }.map(\.engine), [preferredEngine])
        XCTAssertEqual(events.filter { $0.kind == .prepared }.map(\.engine), [actualEngine])
        XCTAssertEqual(events.filter { $0.kind == .started }.map(\.engine), [actualEngine])
        XCTAssertEqual(events.filter { $0.kind == .started }.compactMap(\.nativeAttempt), [attempt])
        XCTAssertTrue(events.compactMap(\.nativeAttempt).allSatisfy { $0 == attempt })
        XCTAssertEqual(lease.currentAttempt, attempt)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertEqual(lease.currentAttemptID, attemptID)
        XCTAssertEqual(controller.snapshot.resourceID, resourceID)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(real.output.rate, 0)
        XCTAssertEqual(real.position, 0, accuracy: 0.15)
        XCTAssertNotNil(real.output.presentationView.window)
        let picture = try displayedBytes(real)
        XCTAssertEqual(releases, 0)
        do {
            _ = try await lease.play(media)
            XCTFail("The one media receipt cannot create a second attempt")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .staleAttempt) }
        XCTAssertEqual(factories, 1, "Internal Aether-to-KS fallback cannot reacquire a business candidate")
        XCTAssertEqual(releases, 0)
        XCTAssertFalse(events.contains { $0.kind == .failed || $0.kind == .stopped || $0.kind == .ended })

        try await bounded("Close and drain \(name)", timeout: 10) { try await lease.close() }
        XCTAssertEqual(releases, 1)
        XCTAssertNil(lease.currentAttempt)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertEqual(events.filter { $0.kind == .stopped }.map(\.nativeAttempt), [attempt])
        XCTAssertFalse(events.contains { $0.kind == .failed })
        try await media.close()
        try await lease.close()
        XCTAssertEqual(releases, 1, "Repeated close coalesces the same receipt release")
        XCTAssertEqual(events.filter { $0.kind == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0.kind == .started }.count, 1)
        XCTAssertEqual(events.filter { $0.kind == .stopped }.count, 1)
        do {
            try await real.renderFirstVideoFrame()
            XCTFail("Drained native output cannot return another first-frame receipt")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
        try await bounded("Drain HTTP \(name)", timeout: 10) { await peer.stop() }
        let requests = peer.observations
        XCTAssertEqual(requests.filter { !$0.authorized }.count, 1,
                       "Only the deliberate unauthenticated probe may be rejected")
        for request in requests.filter(\.authorized) {
            for (field, value) in context.headers { XCTAssertEqual(request.headers[field], value) }
        }
        // A paused first-frame commit needs the initialization and first media
        // segments. Serve the full graph because the real demuxer may prefetch.
        for file in graph where !file.hasPrefix("chunk-") || file.contains("00001") {
            let authorized = requests.filter { $0.path == "/media/\(resourceID)/\(file)" && $0.authorized }
            XCTAssertFalse(authorized.isEmpty, "The real kernel must read \(file)")
        }
        if name.hasSuffix(".mpd") {
            XCTAssertGreaterThanOrEqual(requests.filter { $0.path == url.path && $0.authorized }.count, 2,
                                        "Both the unsupported Aether preparation and the KS fallback read the same MPD")
        }
        return picture
    }

    func testAetherProgressiveKeepsActualEngineAndAuthorizedMediaReceipt() async throws {
        _ = try await play("ordinary.mp4", graph: ["ordinary.mp4"], id: "aether", actualEngine: .aether)
    }

    func testClearKeyFallbackDecodesEncryptedPictureUsingTheSameReceiptAndAttempt() async throws {
        let reference = try await play("ordinary.mp4", graph: ["ordinary.mp4"], id: "ks-reference",
                                      preferredEngine: .ksPlayer, actualEngine: .ksPlayer)
        // These public synthetic KID/key bytes are the fixture generator's CENC
        // material, not an invented key attached to unencrypted media.
        let decryption = try PlaybackVideoDecryption(clearKeys: [
            "000102030405060708090a0b0c0d0e0f": "101112131415161718191a1b1c1d1e1f"
        ])
        let decrypted = try await play("cenc.mp4", graph: ["cenc.mp4"], id: "cenc",
                                       actualEngine: .ksPlayer, decryption: decryption)
        XCTAssertEqual(decrypted, reference, "Real CENC decryption must display the independent clear picture")
    }

    func testSegmentTemplateDASHFallbackKeepsAuthorizedGraphAndSingleReceiptLifecycle() async throws {
        _ = try await play("ordinary.mpd", graph: [
            "ordinary.mpd", "init-stream0.m4s", "init-stream1.m4s",
            "chunk-stream0-00001.m4s", "chunk-stream0-00002.m4s", "chunk-stream0-00003.m4s",
            "chunk-stream1-00001.m4s", "chunk-stream1-00002.m4s",
            "chunk-stream1-00003.m4s", "chunk-stream1-00004.m4s"
        ], id: "dash", actualEngine: .ksPlayer)
    }

    private func rejectDASH(_ name: String, manifest: Data, id: String,
                            responseStatus: EngineIntegrationHTTPPeer.ResponseStatus) async throws {
        executionTimeAllowance = 90
        let resourceID = "integration-\(id)", attemptID = "attempt-\(id)"
        let context = try PlaybackVideoRequestContext(headers: [
            "Authorization": "Bearer synthetic-\(id)",
            "Referer": "https://referer.invalid/\(id)",
            "User-Agent": "MeowPlaybackIntegration/1.0",
            "X-Fixture-Receipt": resourceID
        ], cookie: "integration_fixture=\(id)")
        let path = "/media/\(resourceID)/\(name)"
        let peer = try EngineIntegrationHTTPPeer(files: [path: manifest], requiredHeaders: context.headers,
                                                responseStatus: responseStatus)
        addTeardownBlock { [self] in
            try await bounded("Drain rejected DASH listener and connections", timeout: 10) { await peer.stop() }
        }
        let port = try await bounded("Start rejected DASH server", timeout: 10) { try await peer.start() }
        let url = try XCTUnwrap(URL(string: "http://127.0.0.1:\(port)\(path)"))
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 10
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        let (_, denial) = try await session.data(from: url)
        XCTAssertEqual((denial as? HTTPURLResponse)?.statusCode, 403,
                       "Failure fixtures must not relax the receipt's strict authentication")
        var request = URLRequest(url: url)
        for (field, value) in context.headers { request.setValue(value, forHTTPHeaderField: field) }
        let (body, response) = try await session.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, responseStatus.rawValue)
        XCTAssertEqual(body, responseStatus == .ok ? manifest : Data())
        let controlRequests = peer.observations.count
        XCTAssertEqual(controlRequests, 2, "Only the explicit unauthorized and authorized probes ran")

        var decoder: (any PlaybackVideoDecoding)?
        var initialDecoder: (any PlaybackVideoDecoding)?
        var factories = 0, releases = 0
        var events: [PlaybackVideoEvent] = []
        let controller = PlaybackController(videoDecoderFactory: {
            factories += 1
            let real = try MeowPlaybackVideo.makePlaybackVideoDecoder(engine: .aether)
            XCTAssertEqual(real.engine, .aether)
            // Observe the real candidate's adapter identity without substituting
            // either decoder or its first-frame/output behavior.
            initialDecoder = try XCTUnwrap(Mirror(reflecting: real).descendant("decoder")
                as? any PlaybackVideoDecoding)
            decoder = real
            return real
        })
        let lease = try bindWindowedVideo(controller, owner: "film") { event in
            XCTAssertEqual(event.attemptID, attemptID)
            XCTAssertEqual(event.snapshot.resourceID, resourceID)
            XCTAssertEqual(event.engine, .aether, "An input failure must not select KS")
            if event.kind == .preparing { XCTAssertEqual(releases, 0) }
            if event.kind == .failed { XCTAssertEqual(releases, 1) }
            events.append(event)
        }
        let input = try PlaybackVideoInput(resourceID: resourceID, url: url, title: "Synthetic rejected DASH",
                                          format: .dash, requestContext: context)
        let media = try PlaybackVideoMediaLease(attemptID: attemptID, input: input) {
            releases += 1
            XCTAssertEqual(decoder?.engine, .aether)
            XCTAssertEqual(decoder?.output.rate, 0)
            XCTAssertNil(decoder?.output.presentationView.superview,
                         "The failed native output must drain before releasing the receipt")
        }
        do {
            _ = try await bounded("Reject \(name) without KS fallback") {
                try await lease.play(media, paused: true, initialRate: 1, waitForCommit: {
                    XCTFail("Rejected HTTP or malformed DASH cannot reach commit admission")
                })
            }
            XCTFail("The real Aether input must fail instead of committing or falling back")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .inputFailed) }
        let real = try XCTUnwrap(decoder)
        let initial = try XCTUnwrap(initialDecoder)
        let remaining = try XCTUnwrap(Mirror(reflecting: real).descendant("decoder")
            as? any PlaybackVideoDecoding)
        XCTAssertEqual(ObjectIdentifier(remaining), ObjectIdentifier(initial),
                       "Input failure must not replace the actual Aether adapter with KS")
        XCTAssertEqual(real.engine, .aether)
        XCTAssertEqual(factories, 1, "An input failure cannot reacquire a business candidate")
        XCTAssertEqual(events.map(\.kind), [.preparing, .failed])
        XCTAssertEqual(events.filter { $0.kind == .failed }.map(\.error), [.inputFailed])
        XCTAssertTrue(events.allSatisfy { $0.engine == .aether && $0.nativeAttempt == nil })
        XCTAssertNil(lease.currentAttempt)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(releases, 1, "Failed preparation must close its one media receipt")

        try await bounded("Close rejected DASH and receipt", timeout: 10) {
            try await lease.close()
            try await media.close()
            try await lease.close()
        }
        XCTAssertEqual(releases, 1, "Repeated close cannot release the failed receipt twice")
        XCTAssertEqual(events.map(\.kind), [.preparing, .failed])
        XCTAssertEqual(real.engine, .aether)
        do {
            try await real.renderFirstVideoFrame()
            XCTFail("A drained failed candidate cannot return a first-frame receipt")
        } catch { XCTAssertEqual(error as? PlaybackVideoError, .cancelled) }
        try await bounded("Drain rejected DASH HTTP", timeout: 10) { await peer.stop() }
        XCTAssertEqual(peer.activeConnectionCount, 0)
        let requests = peer.observations
        XCTAssertEqual(requests.filter { !$0.authorized }.count, 1,
                       "Only the deliberate unauthenticated probe may lack valid credentials")
        for request in requests.filter(\.authorized) {
            for (field, value) in context.headers { XCTAssertEqual(request.headers[field], value) }
        }
        let kernelRequests = Array(requests.dropFirst(controlRequests))
        XCTAssertEqual(kernelRequests.count, 1, "Aether must not reopen the failed MPD through KS")
        XCTAssertEqual(kernelRequests.map(\.path), [path])
        XCTAssertTrue(kernelRequests.allSatisfy(\.authorized))
    }

    func testAetherHTTPRejectionDoesNotFallbackOrReacquireMediaReceipt() async throws {
        let manifest = try Data(contentsOf: fixtures.appendingPathComponent("ordinary.mpd"))
        XCTAssertFalse(manifest.isEmpty)
        try await rejectDASH("ordinary.mpd", manifest: manifest, id: "http-rejected", responseStatus: .forbidden)
    }

    func testAetherMalformedDASHDoesNotFallbackOrReacquireMediaReceipt() async throws {
        // Starts as an MPD, so this reaches the real DASH XML parser; it is not
        // a supported/unsupported representation decision or a non-HTTP input.
        let manifest = Data("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"><Period>".utf8)
        try await rejectDASH("malformed.mpd", manifest: manifest, id: "malformed-dash", responseStatus: .ok)
    }
}
#endif

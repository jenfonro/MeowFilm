import XCTest
import Network
import Darwin
import CoreVideo
import CommonCrypto
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
    private let requiredHeaders: [String: [String: String]]
    private let setsResponseCookies: Bool
    private let syntheticAESKey = Data((0..<16).map(UInt8.init))
    private var connections: [UUID: NWConnection] = [:]
    private var requests: [String: Int] = [:]
    private var receivedHeaders: [String: [[String: String]]] = [:]
    private var rejected = 0
    private var rangeRequests: [String: [Int: Int]] = [:]
    private var rangeGate: (path: String, start: Int)?
    private var rangeHeld: (() -> Void)?
    private var heldResponses: [() -> Void] = []
    private var firstSegmentsSent = 0
    private var stopped = false
    private(set) var port: UInt16 = 0

    init(files: URL, fault: SegmentFault? = nil, resourceID: String = "issued",
         referencePrefix: String? = nil, requiredHeaders: [String: [String: String]] = [:],
         setsResponseCookies: Bool = false) throws {
        self.files = files; self.fault = fault; self.resourceID = resourceID
        self.referencePrefix = referencePrefix
        self.setsResponseCookies = setsResponseCookies
        self.requiredHeaders = requiredHeaders.mapValues { fields in
            Dictionary(uniqueKeysWithValues: fields.map { ($0.key.lowercased(), $0.value) })
        }
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
    func headers(_ path: String) -> [[String: String]] { queue.sync { receivedHeaders[path] ?? [] } }
    var rejectedRequests: Int { queue.sync { rejected } }
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

    private func encryptedSegment(_ sequence: Int) -> Data {
        let plain = try! Data(contentsOf: files.appendingPathComponent("hls-\(sequence).ts"))
        // HLS uses the big-endian media sequence as the IV; CCCrypt defaults to CBC.
        var iv = Data(repeating: 0, count: kCCBlockSizeAES128)
        iv[iv.count - 1] = UInt8(sequence)
        let capacity = plain.count + kCCBlockSizeAES128
        var encrypted = Data(count: capacity), written = 0
        let status = syntheticAESKey.withUnsafeBytes { keyBytes in
            iv.withUnsafeBytes { ivBytes in
                plain.withUnsafeBytes { source in
                    encrypted.withUnsafeMutableBytes { destination in
                        CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES),
                            CCOptions(kCCOptionPKCS7Padding), keyBytes.baseAddress, syntheticAESKey.count,
                            ivBytes.baseAddress, source.baseAddress, plain.count,
                            destination.baseAddress, capacity, &written)
                    }
                }
            }
        }
        XCTAssertEqual(status, CCCryptorStatus(kCCSuccess), "Synthetic HLS fixture encryption must succeed")
        encrypted.count = written
        return encrypted
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
            var fields: [String: String] = [:]
            for line in text.components(separatedBy: "\r\n").dropFirst() {
                guard let colon = line.firstIndex(of: ":") else { continue }
                let name = line[..<colon].lowercased()
                let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
                fields[name] = fields[name].map { $0 + ", " + value } ?? value
            }
            receivedHeaders[path, default: []].append(fields)
            let mediaID = String(path.split(separator: "/").dropFirst().first ?? "")
            if let expected = requiredHeaders[mediaID],
               !expected.allSatisfy({ fields[$0.key] == $0.value }) {
                rejected += 1
                connection.send(content: Data(("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n" +
                    "Connection: close\r\n\r\n").utf8), completion: .contentProcessed { [self] _ in
                    finish(connection, id: id)
                })
                return
            }
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
            if name == "encrypted.m3u8" {
                let segments = (0..<3).map { sequence in
                    "#EXT-X-KEY:METHOD=AES-128,URI=\"synthetic.key\",IV=0x" +
                    String(format: "%032x", UInt32(sequence)) +
                    "\n#EXTINF:1.000000,\nencrypted-\(sequence).ts\n"
                }.joined()
                body = Data(("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n" +
                    "#EXT-X-MEDIA-SEQUENCE:0\n" + segments + "#EXT-X-ENDLIST\n").utf8)
            } else if name == "synthetic.key" {
                body = syntheticAESKey
            } else if name.hasPrefix("encrypted-"), name.hasSuffix(".ts") {
                body = encryptedSegment(Int(name.dropFirst("encrypted-".count).dropLast(3))!)
            } else if name == "opaque-master" {
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
            } else if name == "redirect" || name == "media-redirect" {
                let destination = name == "media-redirect" ? "http://127.0.0.1:\(port)/media/\(mediaID)/" : referenced
                status = "302 Found"; extra = "Location: \(destination)ordinary.mp4?synthetic-query=a%2Fb\r\n"; body = Data()
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
            if setsResponseCookies {
                let scope = "Path=/media/\(mediaID)/"
                extra += "Set-Cookie: synthetic_session=response-\(mediaID); \(scope)\r\n" +
                    "Set-Cookie: synthetic_response=\(mediaID); \(scope)\r\n"
            }
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

@MainActor
final class VideoHTTPTests: XCTestCase {
    private func peer(fault: SegmentFault? = nil, resourceID: String = "issued",
                      referencePrefix: String? = nil,
                      requiredHeaders: [String: [String: String]] = [:],
                      setsResponseCookies: Bool = false) async throws -> VideoHTTPPeer {
        let files = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
        let peer = try VideoHTTPPeer(files: files, fault: fault, resourceID: resourceID,
            referencePrefix: referencePrefix, requiredHeaders: requiredHeaders, setsResponseCookies: setsResponseCookies)
        try await peer.start()
        return peer
    }

    private func prepared(_ url: URL, format: String?, headers: [String: String] = [:]) async throws -> VideoSession {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: url, format: format, clearKeys: [:], position: 0, headers: headers)
        try await session.renderFirstVideoFrame()
        XCTAssertEqual(session.info?.codec, "avc1")
        XCTAssertEqual(session.info?.hasAudio, true)
        XCTAssertEqual(session.info?.width, 96)
        XCTAssertEqual(session.info?.height, 64)
        XCTAssertTrue(session.tracks.contains { $0.kind == .audio && $0.isEnabled })
        XCTAssertTrue(session.tracks.contains { $0.kind == .video && $0.isEnabled })
        XCTAssertTrue(session.output.hasPresentedFrame)
        XCTAssertNotNil(decodedLuma(session))
        XCTAssertEqual(session.output.rate, 0)
        return session
    }

    private func playToEnd(_ session: VideoSession, duration: Double) async {
        let terminal = expectation(description: "actual HTTP media playback terminates")
        var failure: VideoKernelFailure?
        var ends = 0
        session.onFailure = { failure = $0; terminal.fulfill() }
        session.onEnd = { ends += 1; terminal.fulfill() }
        session.commit(rate: 2, volume: 0)
        await fulfillment(of: [terminal], timeout: 8)
        XCTAssertNil(failure)
        XCTAssertEqual(ends, 1, "HTTP media must reach natural EOF exactly once")
        XCTAssertGreaterThanOrEqual(session.position, duration - 0.15)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertNotNil(decodedLuma(session))
        session.onFailure = nil; session.onEnd = nil
    }

    private func syntheticHeaders(_ mediaID: String) -> [String: String] {
        ["Authorization": "Bearer synthetic-\(mediaID)",
         "Cookie": "synthetic_session=\(mediaID); synthetic_scope=media",
         "User-Agent": "MeowVideoKernel-synthetic-\(mediaID)",
         "Referer": "https://synthetic.invalid/media/\(mediaID)",
         "X-Media-Test": mediaID]
    }

    private func assertHeaders(_ peer: VideoHTTPPeer, mediaID: String, names: [String],
                               expected: [String: String], file: StaticString = #filePath, line: UInt = #line) {
        let expected = Dictionary(uniqueKeysWithValues: expected.map { ($0.key.lowercased(), $0.value) })
        for name in names {
            let path = "/media/\(mediaID)/" + name
            let requests = peer.headers(path)
            XCTAssertFalse(requests.isEmpty, "The real media graph must request \(name)", file: file, line: line)
            for headers in requests {
                for field in ["authorization", "cookie", "referer", "x-media-test"] {
                    XCTAssertEqual(headers[field], expected[field], "\(name): \(field)", file: file, line: line)
                }
                XCTAssertEqual(headers["user-agent"], expected["user-agent"] ?? "KSPlayer",
                    "\(name): user-agent", file: file, line: line)
            }
        }
        XCTAssertEqual(peer.rejectedRequests, 0, file: file, line: line)
    }

    func testHTTPHLSAndDASHReadTheirGraphAndSeek() async throws {
        let peer = try await peer(); defer { peer.stop() }
        for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
            let session = try await prepared(URL(string: peer.prefix + name)!, format: format)
            XCTAssertEqual(try XCTUnwrap(session.info?.duration), 3, accuracy: 0.1)
            await playToEnd(session, duration: 3)
            let position = try await session.seek(to: 1.5)
            XCTAssertEqual(position, 1.5, accuracy: 0.15)
            XCTAssertTrue(session.output.hasPresentedFrame)
            XCTAssertNotNil(decodedLuma(session))
            XCTAssertEqual(session.output.rate, 0)
            await session.close()
        }
        for name in ["master.m3u8", "variant.m3u8", "hls-0.ts", "hls-1.ts", "hls-2.ts", "ordinary.mpd",
                     "init-stream0.m4s", "init-stream1.m4s", "chunk-stream0-00003.m4s", "chunk-stream1-00004.m4s"] {
            XCTAssertGreaterThan(peer.count("/media/issued/" + name), 0)
        }
    }

    func testAbsoluteManifestReferencesAndRedirectCarryHeadersAndCookieAcrossOriginsAndSeek() async throws {
        let headers = syntheticHeaders("cross-origin")
        let media = try await peer(requiredHeaders: ["issued": headers]); defer { media.stop() }
        let peer = try await peer(referencePrefix: media.prefix, requiredHeaders: ["issued": headers])
        defer { peer.stop() }
        XCTAssertNotEqual(peer.port, media.port)
        for (name, format, duration, target) in [
            ("absolute.m3u8", "hls", 1.0, 0.5), ("absolute.mpd", "dash", 3.0, 1.5), ("redirect", "mov", 3.0, 1.5)
        ] {
            let session = try await prepared(URL(string: peer.prefix + name)!, format: format, headers: headers)
            await playToEnd(session, duration: duration)
            let position = try await session.seek(to: target)
            XCTAssertEqual(position, target, accuracy: 0.15)
            XCTAssertTrue(session.output.hasPresentedFrame)
            XCTAssertNotNil(decodedLuma(session))
            await session.close()
        }
        assertHeaders(peer, mediaID: "issued", names: ["absolute.m3u8", "absolute.mpd", "redirect"], expected: headers)
        assertHeaders(media, mediaID: "issued",
            names: ["hls-0.ts", "ordinary.mp4", "init-stream0.m4s", "init-stream1.m4s",
                    "chunk-stream0-00002.m4s", "chunk-stream1-00002.m4s"], expected: headers)
        XCTAssertGreaterThan(media.count("/media/issued/ordinary.mp4", rangeStart: 0), 0)
    }

    func testLaterAbsoluteHLSSegmentOutsideStartingPathDecodesAndSeeks() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try await prepared(URL(string: peer.prefix + "absolute-late.m3u8")!, format: "hls")
        await playToEnd(session, duration: 2)
        let position = try await session.seek(to: 1.5)
        XCTAssertEqual(position, 1.5, accuracy: 0.15)
        XCTAssertTrue(session.output.hasPresentedFrame)
        XCTAssertNotNil(decodedLuma(session))
        await session.close()
        XCTAssertGreaterThan(peer.count("/media/issued/hls-0.ts"), 0)
        XCTAssertGreaterThan(peer.count("/media/other/hls-1.ts"), 0)
    }

    func testOpaqueRuntimeHLSPathsAreNotRejectedByFilenameGuessing() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try await prepared(URL(string: peer.prefix + "opaque-master")!, format: "hls")
        await playToEnd(session, duration: 3)
        XCTAssertGreaterThan(peer.count("/media/issued/hls-0"), 0)
        await session.close()
    }

    func testHeadersAndCookieFollowMediaGraphsAndRedirectsWithoutCrossMediaState() async throws {
        let alphaHeaders = syntheticHeaders("alpha"), betaHeaders = syntheticHeaders("beta")
        let peer = try await peer(requiredHeaders: ["alpha": alphaHeaders, "beta": betaHeaders],
            setsResponseCookies: true)
        defer { peer.stop() }
        for (name, format, graph) in [
            ("master.m3u8", "hls", ["master.m3u8", "variant.m3u8", "hls-0.ts", "hls-1.ts", "hls-2.ts"]),
            ("ordinary.mpd", "dash", ["ordinary.mpd", "init-stream0.m4s", "init-stream1.m4s",
                "chunk-stream0-00001.m4s", "chunk-stream0-00002.m4s", "chunk-stream0-00003.m4s",
                "chunk-stream1-00001.m4s", "chunk-stream1-00002.m4s", "chunk-stream1-00004.m4s"]),
            ("media-redirect", "mov", ["media-redirect", "ordinary.mp4"])
        ] {
            let alpha = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/alpha/" + name)!,
                format: format, headers: alphaHeaders)
            let beta = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/beta/" + name)!,
                format: format, headers: betaHeaders)
            let anonymous = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/anonymous/" + name)!,
                format: format)
            // All three cores coexist. Neither another media nor Set-Cookie
            // on manifests, segments or redirects may replace an explicit
            // Cookie or populate the anonymous context.
            for session in [alpha, beta, anonymous] {
                await playToEnd(session, duration: 3)
                let position = try await session.seek(to: 1.5)
                XCTAssertEqual(position, 1.5, accuracy: 0.15)
                XCTAssertTrue(session.output.hasPresentedFrame)
                XCTAssertNotNil(decodedLuma(session))
            }
            for session in [alpha, beta, anonymous] { await session.close() }
            assertHeaders(peer, mediaID: "alpha", names: graph, expected: alphaHeaders)
            assertHeaders(peer, mediaID: "beta", names: graph, expected: betaHeaders)
            assertHeaders(peer, mediaID: "anonymous", names: graph, expected: [:])
        }
    }

    func testAES128HLSDecryptsAndSeeksWithAuthenticatedKeyRequest() async throws {
        let headers = syntheticHeaders("encrypted")
        let peer = try await peer(resourceID: "encrypted", requiredHeaders: ["encrypted": headers])
        defer { peer.stop() }
        let session = try await prepared(URL(string: peer.prefix + "encrypted.m3u8")!,
            format: "hls", headers: headers)
        XCTAssertEqual(try XCTUnwrap(session.info?.duration), 3, accuracy: 0.1)
        let firstPixels = try XCTUnwrap(decodedLuma(session))
        let position = try await session.seek(to: 1.5)
        XCTAssertEqual(position, 1.5, accuracy: 0.15)
        XCTAssertTrue(session.output.hasPresentedFrame)
        XCTAssertNotEqual(try XCTUnwrap(decodedLuma(session)), firstPixels)
        XCTAssertEqual(session.output.rate, 0)
        await playToEnd(session, duration: 3)
        await session.close()
        assertHeaders(peer, mediaID: "encrypted",
            names: ["encrypted.m3u8", "synthetic.key", "encrypted-0.ts", "encrypted-1.ts", "encrypted-2.ts"],
            expected: headers)
    }

    func testHeadersAndCookieRemainPerMediaDuringActualHTTPRangeSeeks() async throws {
        let alphaHeaders = syntheticHeaders("alpha"), betaHeaders = syntheticHeaders("beta")
        let peer = try await peer(requiredHeaders: ["alpha": alphaHeaders, "beta": betaHeaders],
            setsResponseCookies: true)
        defer { peer.stop() }
        let name = "seek-cancel-long.m3u8"
        let alpha = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/alpha/" + name)!,
            format: "hls", headers: alphaHeaders)
        let beta = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/beta/" + name)!,
            format: "hls", headers: betaHeaders)
        let anonymous = try await prepared(URL(string: "http://127.0.0.1:\(peer.port)/media/anonymous/" + name)!,
            format: "hls")
        for session in [alpha, beta, anonymous] { session.commit(rate: 0, volume: 0) }
        let alphaPath = "/media/alpha/seek-cancel-long.ts", betaPath = "/media/beta/seek-cancel-long.ts"
        let anonymousPath = "/media/anonymous/seek-cancel-long.ts"
        let alphaBefore = peer.count(alphaPath, rangeStart: 249_852)
        let betaBefore = peer.count(betaPath, rangeStart: 82_908)
        let anonymousBefore = peer.count(anonymousPath, rangeStart: 167_132)
        let held = expectation(description: "authenticated alpha seek waits on real ranged HTTP")
        peer.holdRange(alphaPath, start: 249_852) { held.fulfill() }
        let seeking = Task { try await alpha.seek(to: 18) }
        await fulfillment(of: [held], timeout: 8)
        XCTAssertEqual(alpha.output.rate, 0)
        // Beta remains independently usable while alpha's authorized bytes
        // are held; neither session may borrow the other's request context.
        let betaPosition = try await beta.seek(to: 6)
        XCTAssertEqual(betaPosition, 6, accuracy: 0.15)
        XCTAssertGreaterThan(peer.count(betaPath, rangeStart: 82_908), betaBefore)
        XCTAssertNotNil(decodedLuma(beta))
        let anonymousPosition = try await anonymous.seek(to: 12)
        XCTAssertEqual(anonymousPosition, 12, accuracy: 0.15)
        XCTAssertGreaterThan(peer.count(anonymousPath, rangeStart: 167_132), anonymousBefore)
        XCTAssertNotNil(decodedLuma(anonymous))
        peer.releaseRanges()
        let alphaPosition = try await seeking.value
        XCTAssertEqual(alphaPosition, 18, accuracy: 0.15)
        XCTAssertGreaterThan(peer.count(alphaPath, rangeStart: 249_852), alphaBefore)
        XCTAssertNotNil(decodedLuma(alpha))
        await alpha.close(); await beta.close(); await anonymous.close()
        assertHeaders(peer, mediaID: "alpha", names: [name, "seek-cancel-long.ts"], expected: alphaHeaders)
        assertHeaders(peer, mediaID: "beta", names: [name, "seek-cancel-long.ts"], expected: betaHeaders)
        assertHeaders(peer, mediaID: "anonymous", names: [name, "seek-cancel-long.ts"], expected: [:])
        XCTAssertTrue(peer.headers(alphaPath).contains { $0["range"]?.hasPrefix("bytes=249852-") == true })
        XCTAssertTrue(peer.headers(betaPath).contains { $0["range"]?.hasPrefix("bytes=82908-") == true })
        XCTAssertTrue(peer.headers(anonymousPath).contains { $0["range"]?.hasPrefix("bytes=167132-") == true })
    }

    func testLaterHLSAndDASHSegment403OrDisconnectIsTypedFailureNotEOF() async throws {
        for fault in [SegmentFault.forbidden, .disconnected] {
            for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
                let peer = try await peer(fault: fault); defer { peer.stop() }
                let session = try VideoSession()
                try await hostVideoOutput(session)
                var failure: VideoKernelFailure?
                var ended = false
                let terminal = expectation(description: "\(format) \(fault) terminates with a typed failure")
                session.onFailure = { failure = $0; terminal.fulfill() }
                session.onEnd = { ended = true; terminal.fulfill() }
                do {
                    try await session.prepare(url: URL(string: peer.prefix + name)!, format: format,
                        clearKeys: [:], position: 0)
                    try await session.renderFirstVideoFrame()
                    session.commit(rate: 2, volume: 0)
                } catch { failure = error as? VideoKernelFailure; terminal.fulfill() }
                await fulfillment(of: [terminal], timeout: 8)
                await session.close()
                XCTAssertEqual(failure, .transportFailed, "\(format) \(fault) cannot be successful EOF")
                XCTAssertFalse(ended)
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
        let firstPixels = try XCTUnwrap(decodedLuma(session))
        let firstPlayback = expectation(description: "actual core output produces a later moving picture")
        let ended = expectation(description: "resumed long stream reaches normal decoded EOF")
        var endCount = 0
        var failures: [VideoKernelFailure] = []
        var advanced = false
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        session.onEnd = { endCount += 1; ended.fulfill() }
        session.onProgress = { position in
            if !advanced, position >= 1 {
                self.checkDecodedLuma(session, matches: { $0 != firstPixels }) {
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
        XCTAssertEqual(peer.count(mediaPath, rangeStart: tailOffset), 0, "The paused core has not read the tail")

        var seeking: Task<Double, Error>?
        var interruptions = 0
        session.onSeekInterrupted = {
            interruptions += 1
            seeking?.cancel()
        }
        // The synchronous witness cancels this parent on MainActor after
        // seek registration with its cancellation handler installed, but
        // before the queued operation Task can start native work.
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

        let resumedNearPause = expectation(description: "explicit resume keeps the playback position")
        let decodedTail = expectation(description: "the actual core output produces the fixture's late white frames")
        var resumed = false, decodedWhite = false
        session.onProgress = { position in
            if !resumed, position > pausedPosition + 0.2 {
                XCTAssertLessThan(position, pausedPosition + 2, "The old seek target must not revive")
                resumed = true; resumedNearPause.fulfill()
            }
            if !decodedWhite, position >= 20 {
                self.checkDecodedLuma(session, matches: { $0 != firstPixels && $0.allSatisfy { $0 > 220 } }) {
                    decodedWhite = true; decodedTail.fulfill()
                }
            }
        }
        session.setRate(4)
        await fulfillment(of: [resumedNearPause, decodedTail, ended], timeout: 20, enforceOrder: true)
        let tailRequestsAfterResume = peer.count(mediaPath, rangeStart: tailOffset)
        XCTAssertGreaterThan(tailRequestsAfterResume, tailRequestsBeforeResume, "Resume must perform new real HTTP tail reads")
        XCTAssertGreaterThan(peer.count(mediaPath), requestsBeforeResume)
        XCTAssertTrue(decodedWhite, "Clock progress cannot substitute for actual decoded output pixels")
        XCTAssertEqual(endCount, 1)
        XCTAssertGreaterThanOrEqual(session.position, 23.9)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertTrue(failures.isEmpty)
        print("IKF03 interrupts=\(interruptions) tailBefore=\(tailRequestsBeforeResume) tailAfter=\(tailRequestsAfterResume) " +
              "decodedWhite=\(decodedWhite) normalEOF=\(endCount) finalPosition=\(session.position) failures=\(failures.count)")
        await session.close()
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
        XCTAssertNil(session.info)
    }

    @MainActor
    func testImmediateResumeAfterBlockedNativeSeekCancellationRestoresPositionAndReachesEOF() async throws {
        let peer = try await peer(); defer { peer.stop() }
        let session = try await prepared(URL(string: peer.prefix + "seek-cancel-long.m3u8")!, format: "hls")
        defer {
            session.onProgress = nil; session.onEnd = nil; session.onFailure = nil
            session.cancel()
        }
        XCTAssertEqual(try XCTUnwrap(session.info?.duration), 24, accuracy: 0.1)
        session.commit(rate: 0, volume: 0)
        let pausedPosition = session.position
        let pausedPixels = try XCTUnwrap(decodedLuma(session))
        XCTAssertLessThan(pausedPosition, 4)
        XCTAssertEqual(session.output.rate, 0)
        let mediaPath = "/media/issued/seek-cancel-long.ts"
        let targetOffset = 249_852, tailOffset = 296_476
        XCTAssertEqual(peer.count(mediaPath, rangeStart: targetOffset), 0)
        XCTAssertEqual(peer.count(mediaPath, rangeStart: tailOffset), 0)
        let targetHeld = expectation(description: "native seek18 is blocked on real ranged HTTP")
        let joined = expectation(description: "cancelled blocked-native seek actually returns")
        let resumedNearPause = expectation(description: "new decoded pixels advance near the old confirmed position")
        let decodedTail = expectation(description: "restored feeding produces the actual late white frames")
        let ended = expectation(description: "restored long stream reaches natural EOF exactly once")
        ended.assertForOverFulfill = true
        var seekResult: Result<Double, Error>?
        var restoredPosition: Double?
        var decodedWhite = false, endCount = 0
        var failures: [VideoKernelFailure] = []
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        session.onEnd = { endCount += 1; ended.fulfill() }
        peer.holdRange(mediaPath, start: targetOffset) { targetHeld.fulfill() }
        let seeking = Task {
            do { seekResult = .success(try await session.seek(to: 18)) }
            catch { seekResult = .failure(error) }
            joined.fulfill()
        }
        await fulfillment(of: [targetHeld], timeout: 8)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: targetOffset), 0)
        XCTAssertNil(seekResult, "The cancellation must interrupt native IO, not an already completed seek")
        XCTAssertEqual(session.position, pausedPosition, accuracy: 0.15)
        XCTAssertEqual(session.output.rate, 0)
        session.onProgress = { position in
            if restoredPosition == nil, position > pausedPosition + 0.2, position < pausedPosition + 4 {
                self.checkDecodedLuma(session, matches: { $0 != pausedPixels }) {
                    XCTAssertEqual(session.output.rate, 4)
                    restoredPosition = position; resumedNearPause.fulfill()
                }
            }
            if !decodedWhite, position >= 20 {
                self.checkDecodedLuma(session, matches: { $0 != pausedPixels && $0.allSatisfy { $0 > 220 } }) {
                    decodedWhite = true; decodedTail.fulfill()
                }
            }
        }
        // Do not join or yield between cancellation and the explicit resume.
        // The old HTTP response stays held through both completion witnesses.
        seeking.cancel()
        session.setRate(4)
        await fulfillment(of: [joined, resumedNearPause], timeout: 8)
        switch try XCTUnwrap(seekResult, "Blocked native cancellation must join within the bounded wait") {
        case .success: XCTFail("A cancelled seek cannot publish target18")
        case .failure(let error):
            XCTAssertTrue(error is CancellationError || (error as? VideoKernelFailure) == .cancelled)
        }
        await seeking.value
        let position = try XCTUnwrap(restoredPosition, "The held response cannot be released without a real recovery frame")
        XCTAssertGreaterThan(position, pausedPosition + 0.2)
        XCTAssertLessThan(position, pausedPosition + 4, "The cancelled target18 must not revive")
        XCTAssertEqual(endCount, 0)
        XCTAssertTrue(failures.isEmpty)
        let tailBeforeRelease = peer.count(mediaPath, rangeStart: tailOffset)
        XCTAssertEqual(tailBeforeRelease, 0, "Recovery has not bypassed the still-held target18 bytes")
        peer.releaseRanges()
        await fulfillment(of: [decodedTail, ended], timeout: 20, enforceOrder: true)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: tailOffset), tailBeforeRelease,
            "Restored feeding must perform new real HTTP tail reads")
        XCTAssertTrue(decodedWhite, "Clock progress cannot substitute for real decoded tail pixels")
        XCTAssertEqual(endCount, 1)
        XCTAssertGreaterThanOrEqual(session.position, 23.9)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
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
        let firstPixels = try XCTUnwrap(decodedLuma(session))
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
        let decodedTail = expectation(description: "rate two core output produces actual late white frames")
        var decodedWhite = false
        session.onProgress = { position in
            if !decodedWhite, position >= 20 {
                self.checkDecodedLuma(session, matches: { $0 != firstPixels && $0.allSatisfy { $0 > 220 } }) {
                    decodedWhite = true; decodedTail.fulfill()
                }
            }
        }
        await fulfillment(of: [decodedTail, ended], timeout: 12, enforceOrder: true)
        XCTAssertTrue(decodedWhite)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: targetOffset), 0)
        XCTAssertGreaterThan(peer.count(mediaPath, rangeStart: tailOffset), 0)
        XCTAssertEqual(endCount, 1)
        XCTAssertTrue(failures.isEmpty)
        XCTAssertGreaterThanOrEqual(session.position, 23.9)
        XCTAssertEqual(session.output.rate, 0)
        print("IKF03 rateDuringSeek target=\(position) requestedRate=2 interrupts=\(interruptions) " +
              "tailReads=\(peer.count(mediaPath, rangeStart: tailOffset)) decodedWhite=\(decodedWhite) " +
              "normalEOF=\(endCount) failures=\(failures.count)")
        await session.close()
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
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
        let replacementAccepted = expectation(description: "new user seek owns the session before old IO completes")
        let extraSeek = expectation(description: "old completion cannot trigger a third seek")
        extraSeek.isInverted = true
        session.onSeekInterrupted = {
            interruptions += 1
            if interruptions == 2 { replacementAccepted.fulfill() }
            if interruptions > 2 { extraSeek.fulfill() }
        }
        session.onFailure = { error in failures.append(error); XCTFail("Real output failed: \(error)") }
        let first = Task { try await session.seek(to: 6) }
        await fulfillment(of: [firstHeld], timeout: 8)
        let secondHeld = expectation(description: "new seek issues HTTP while the old response remains held")
        peer.holdRange(mediaPath, start: 249_852) { secondHeld.fulfill() }
        let second = Task { try await session.seek(to: 18) }
        // Keep the old response held. Superseding it must cancel the blocked
        // native IO and issue the new target without help from the peer.
        await fulfillment(of: [replacementAccepted, secondHeld], timeout: 8, enforceOrder: true)
        switch await first.result {
        case .success: XCTFail("Superseded seek cannot publish its old target")
        case .failure(let error):
            let failure = error as? VideoKernelFailure
            XCTAssertTrue(error is CancellationError || failure == .cancelled || failure == .interrupted)
        }
        session.setRate(2)
        XCTAssertEqual(session.output.rate, 0, "An old completion cannot clear the newer blocked seek")
        await fulfillment(of: [extraSeek], timeout: 0.2)
        XCTAssertEqual(session.output.rate, 0)
        peer.releaseRanges()
        let position = try await second.value
        XCTAssertEqual(position, 18, accuracy: 0.15)
        XCTAssertEqual(interruptions, 2, "Old completion cannot hide the newer active seek")
        XCTAssertEqual(session.output.rate, 2)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
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
        // Enter the user seek on this MainActor task before any work queued
        // by the resume command can replace its newer target.
        let position = try await session.seek(to: 18)
        XCTAssertEqual(position, 18, accuracy: 0.15)
        XCTAssertEqual(interruptions, 1)
        XCTAssertEqual(session.output.rate, 2)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
        XCTAssertNil(session.info)
    }

    private func checkDecodedLuma(_ session: VideoSession,
                                  matches: ([UInt8]) -> Bool, onMatch: () -> Void) {
        guard session.output.hasPresentedFrame, let pixels = decodedLuma(session), matches(pixels) else { return }
        onMatch()
    }

    private func decodedLuma(_ session: VideoSession) -> [UInt8]? {
        if let pixel = session.output.pixelBuffer,
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
        let session = try await prepared(URL(string: peer.prefix + "master.m3u8")!, format: "hls")
        await playToEnd(session, duration: 3)
        await session.close()
        XCTAssertGreaterThanOrEqual(peer.count("/media/issued/hls-1.ts"), 2, "The exact failed URL was successfully retried")
    }

    func testMalformedDASHHasFixedFailureAndNoRawXMLOrAddressOnStderr() async throws {
        let headers = syntheticHeaders("synthetic-credential-canary")
        let peer = try await peer(resourceID: "synthetic-capability-canary",
            requiredHeaders: ["synthetic-capability-canary": headers])
        defer { peer.stop() }
        let captured = try await captureStandardError {
            for suffix in ["?signature=synthetic-token-canary", ""] {
                let session = try VideoSession()
                var failure: VideoKernelFailure?
                do {
                    try await session.prepare(url: URL(string: peer.prefix + "malformed.mpd" + suffix)!,
                        format: "dash", clearKeys: [:], position: 0, headers: headers)
                } catch { failure = error as? VideoKernelFailure }
                await session.close()
                XCTAssertEqual(failure, .formatFailed, "Malformed XML must take the fixed failure path")
            }
        }
        XCTAssertTrue(captured.isEmpty, "No raw XML diagnostics may escape the actual DASH parser")
        for canary in ["synthetic-token-canary", "synthetic-capability-canary",
                       "synthetic-xml-canary", "synthetic-credential-canary"] {
            XCTAssertNil(captured.range(of: Data(canary.utf8)))
        }
        XCTAssertEqual(peer.count("/media/synthetic-capability-canary/malformed.mpd"), 2)
        assertHeaders(peer, mediaID: "synthetic-capability-canary", names: ["malformed.mpd"], expected: headers)
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
        let session = try VideoSession()
        try await hostVideoOutput(session)
        var ends = 0, failures = 0
        session.onEnd = { ends += 1 }
        session.onFailure = { _ in failures += 1 }
        let operation = Task {
            try await session.prepare(url: URL(string: peer.prefix + "stall")!, format: nil,
                clearKeys: [:], position: 0)
        }
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        while peer.count("/media/issued/stall") == 0, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(peer.count("/media/issued/stall"), 1)
        XCTAssertEqual(peer.outstanding, 1, "The test must cancel an actually connected HTTP request")
        session.cancel()
        do { _ = try await operation.value; XCTFail("cancelled IO cannot prepare") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        async let first: Void = session.close()
        async let second: Void = session.close()
        _ = await (first, second)
        let drained = ContinuousClock.now.advanced(by: .seconds(5))
        while peer.outstanding != 0, ContinuousClock.now < drained {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertEqual(peer.outstanding, 0)
        XCTAssertEqual(ends, 0)
        XCTAssertEqual(failures, 0)
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.output.presentationView.superview)
        XCTAssertNil(session.info)
    }
}

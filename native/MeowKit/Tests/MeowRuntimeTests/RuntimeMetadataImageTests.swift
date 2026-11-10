import Foundation
import Network
import XCTest
@testable import MeowRuntime

/// A real loopback HTTP peer, not URLProtocol or a replacement image reader.
private final class MetadataImageHTTPPeer: @unchecked Sendable {
    struct Request: Sendable {
        let method: String
        let path: String
        let headers: [String: String]
    }
    struct Reply: Sendable {
        let status: Int
        let body: Data
        var headers: [String: String] = [:]
    }
    let ready = XCTestExpectation(description: "loopback HTTP listener ready")
    private let listener: NWListener
    private let queue = DispatchQueue(label: "MeowRuntimeTests.metadata-image-http")
    private let reply: @Sendable (Request) -> Reply
    private var connections: [NWConnection] = []
    private var received: [Request] = []
    private var startupError: NWError?

    init(reply: @escaping @Sendable (Request) -> Reply) throws {
        self.reply = reply
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: .any)
        listener = try NWListener(using: parameters)
        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready: ready.fulfill()
            case .failed(let error): startupError = error; ready.fulfill()
            default: break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            connections.append(connection)
            connection.start(queue: queue)
            receive(connection, bytes: Data())
        }
        listener.start(queue: queue)
    }
    func endpoint() throws -> URL {
        try queue.sync {
            if let startupError { throw startupError }
            let port = try XCTUnwrap(listener.port)
            return try XCTUnwrap(URL(string: "http://127.0.0.1:\(port.rawValue)/"))
        }
    }
    var requests: [Request] { queue.sync { received } }
    func stop() {
        queue.sync {
            listener.cancel()
            for connection in connections { connection.cancel() }
            connections.removeAll()
        }
    }
    private func receive(_ connection: NWConnection, bytes: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, complete, error in
            guard let self else { connection.cancel(); return }
            var bytes = bytes
            if let data { bytes.append(data) }
            guard let end = bytes.range(of: Data("\r\n\r\n".utf8)) else {
                if complete || error != nil { connection.cancel() }
                else { receive(connection, bytes: bytes) }
                return
            }
            let lines = String(decoding: bytes[..<end.lowerBound], as: UTF8.self).components(separatedBy: "\r\n")
            let target = lines[0].split(separator: " ")
            guard target.count == 3 else { connection.cancel(); return }
            var headers: [String: String] = [:]
            for line in lines.dropFirst() {
                guard let colon = line.firstIndex(of: ":") else { continue }
                headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            }
            let request = Request(method: String(target[0]), path: String(target[1]), headers: headers)
            received.append(request)
            let response = reply(request)
            var fields = ["Content-Type": "image/png", "Content-Length": String(response.body.count),
                          "Connection": "close", "X-Meow-Request-Id": headers["x-meow-request-id"] ?? ""]
            fields.merge(response.headers) { $1 }
            var packet = Data(("HTTP/1.1 \(response.status) Fixture\r\n" +
                fields.map { "\($0.key): \($0.value)\r\n" }.joined() + "\r\n").utf8)
            packet.append(response.body)
            connection.send(content: packet, contentContext: .finalMessage, isComplete: true,
                completion: .contentProcessed { _ in connection.cancel() })
        }
    }
}

final class RuntimeMetadataImageTests: XCTestCase {
    func testRealHTTPImagesRequireNoCredentialsAndBindErrorsToTheRequestedProvider() async throws {
        let douban = "/v1/douban/images/view/photo/s_ratio_poster/public/p123.jpg"
        let tmdb = "/v1/tmdb/images/w500/poster.jpg"
        let cases: [(String, String?, String?, RuntimeHTTPError?)] = [
            (tmdb, nil, nil, nil), (douban, nil, nil, nil),
            ("/v1/douban/images/poster.jpg", nil, nil, nil),
            ("/v1/douban/images/~/.poster/.../p-1_2.jpg", nil, nil, nil),
            (douban, "douban.data.v1", "UPSTREAM_HTTP", .remote("UPSTREAM_HTTP")),
            (douban, "douban.data.v1", "UPSTREAM_FORMAT", .remote("UPSTREAM_FORMAT")),
            (douban, "runtime.http.v1", "PERMISSION_DENIED", .remote("PERMISSION_DENIED")),
            (douban, "tmdb.data.v1", "UPSTREAM_HTTP", .invalidResponse),
            (tmdb, "douban.data.v1", "UPSTREAM_HTTP", .invalidResponse),
            (douban, "douban.data.v1", "SCRIPT_FAILURE", .invalidResponse),
            (douban, "runtime.http.v1", "UPSTREAM_HTTP", .invalidResponse)
        ]
        var requestIDs = Set<String>()
        for (path, contract, code, expected) in cases {
            let body: Data
            if let contract, let code {
                body = try RuntimeJSON.encode(.object(["ok": .bool(false), "contract": .string(contract), "code": .string(code)]))
            } else { body = Data([137, 80, 78, 71]) }
            let peer = try MetadataImageHTTPPeer { request in
                XCTAssertEqual(request.method, "GET")
                XCTAssertEqual(request.path, path)
                XCTAssertNil(request.headers["authorization"])
                XCTAssertNil(request.headers["x-meow-session"])
                XCTAssertNil(request.headers["cookie"])
                return .init(status: contract == nil ? 200 : contract == "runtime.http.v1" ? 403 : 502, body: body,
                             headers: ["Content-Type": contract == nil ? "image/png" : "application/json"])
            }
            defer { peer.stop() }
            await fulfillment(of: [peer.ready], timeout: 3)
            let client = try RuntimeHTTPClient(baseURL: peer.endpoint())
            let reference = try XCTUnwrap(RuntimeHTTPClient.metadataImageURL(path))
            XCTAssertNil(reference.baseURL); XCTAssertNil(reference.scheme)
            do {
                let actual = try await client.readMetadataImage(reference)
                XCTAssertNil(expected, path); XCTAssertEqual(actual, body, path)
            } catch {
                if let expected { XCTAssertEqual(error as? RuntimeHTTPError, expected, path) }
                else { XCTFail("Expected image bytes, not an HTTP failure: \(path)") }
            }
            await client.close()
            let requests = peer.requests
            XCTAssertEqual(requests.count, 1, path)
            let requestID = try XCTUnwrap(requests.first?.headers["x-meow-request-id"])
            XCTAssertNotNil(UUID(uuidString: requestID))
            XCTAssertTrue(requestIDs.insert(requestID).inserted)
            XCTAssertEqual(reference.absoluteString, path)
        }
    }

    func testRealHTTPRejectsMalformedDoubanReferencesWithoutSendingAnyRequest() async throws {
        let valid = "/v1/douban/images/view/photo/public/poster.jpg"
        let peer = try MetadataImageHTTPPeer { request in
            XCTAssertEqual(request.path, valid, "Only the final valid reference may reach the HTTP peer")
            return .init(status: 200, body: Data([137, 80, 78, 71]))
        }
        defer { peer.stop() }
        await fulfillment(of: [peer.ready], timeout: 3)
        let client = try RuntimeHTTPClient(baseURL: peer.endpoint())
        for path in ["/v1/douban/images", "/v1/douban/images/", "/v1/douban/images//a.jpg",
                     "/v1/douban/images/a//b.jpg", "/v1/douban/images/./a.jpg",
                     "/v1/douban/images/a/../b.jpg", "/v1/douban/images/.", "/v1/douban/images/..",
                     "/v1/douban/images/%2e%2e/a.jpg", "/v1/douban/images/a%2fb.jpg",
                     "/v1/douban/images/a?x=1", "/v1/douban/images/a#x", "/v1/douban/images/a\\b.jpg",
                     "//127.0.0.1/v1/douban/images/a.jpg",
                     "https://example.invalid/v1/douban/images/a.jpg"] {
            XCTAssertNil(RuntimeHTTPClient.metadataImageURL(path), path)
            if let reference = URL(string: path) {
                do { _ = try await client.readMetadataImage(reference); XCTFail(path) }
                catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidRequest, path) }
            }
        }
        let bytes = try await client.readMetadataImage(try XCTUnwrap(RuntimeHTTPClient.metadataImageURL(valid)))
        XCTAssertEqual(bytes, Data([137, 80, 78, 71]))
        await client.close()
        XCTAssertEqual(peer.requests.map(\.path), [valid])
    }
}

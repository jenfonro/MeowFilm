import Foundation
import Network

/// A real loopback origin with an exact whitelist of public synthetic HLS files.
/// All mutable transport state is confined to queue, including shutdown receipts.
final class AetherFixtureHTTPServer: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meow.aether.fixture.http")
    private let listener: NWListener
    private let files: [String: Data]
    private let holdsResponses: Bool
    private var connections: [UUID: NWConnection] = [:]
    private var startup: CheckedContinuation<UInt16, Error>?
    private var stopWaiters: [CheckedContinuation<Void, Never>] = []
    private var stopping = false
    private var listenerCancelled = false
    private var served = Set<String>()
    private var rejected = 0
    private var heldRequests = Set<UUID>()

    init(files: [String: Data], holdsResponses: Bool = false) throws {
        self.files = files
        self.holdsResponses = holdsResponses
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    var servedPaths: Set<String> { queue.sync { served } }
    var rejectedRequestCount: Int { queue.sync { rejected } }
    var connectionCount: Int { queue.sync { connections.count } }
    var heldRequestCount: Int { queue.sync { heldRequests.count } }
    var isDrained: Bool { queue.sync { listenerCancelled && connections.isEmpty } }

    func start() async throws -> UInt16 {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
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
                            heldRequests.remove(id)
                            finishStopIfDrained()
                        }
                    }
                    connection.start(queue: queue)
                    if stopping { connection.cancel() }
                    else { receive(connection, id: id, accumulated: Data()) }
                }
                listener.start(queue: queue)
            }
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                stopping = true
                stopWaiters.append(continuation)
                listener.cancel()
                connections.values.forEach { $0.cancel() }
                finishStopIfDrained()
            }
        }
    }

    private func finishStopIfDrained() {
        guard stopping, listenerCancelled, connections.isEmpty else { return }
        listener.stateUpdateHandler = nil
        listener.newConnectionHandler = nil
        let waiters = stopWaiters; stopWaiters.removeAll()
        waiters.forEach { $0.resume() }
    }

    private func receive(_ connection: NWConnection, id: UUID, accumulated: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [self] data, _, complete, error in
            guard !stopping else { connection.cancel(); return }
            var header = accumulated
            if let data { header.append(data) }
            guard header.count <= 32_768 else {
                rejected += 1
                send(connection, status: "431 Request Header Fields Too Large")
                return
            }
            guard let end = header.range(of: Data("\r\n\r\n".utf8)) else {
                if complete || error != nil { connection.cancel() }
                else { receive(connection, id: id, accumulated: header) }
                return
            }
            let lines = String(decoding: header[..<end.lowerBound], as: UTF8.self)
                .components(separatedBy: "\r\n")
            let request = (lines.first ?? "").split(separator: " ")
            guard request.count == 3, request[2] == "HTTP/1.1" || request[2] == "HTTP/1.0" else {
                rejected += 1
                send(connection, status: "400 Bad Request")
                return
            }
            let method = String(request[0]), path = String(request[1])
            guard method == "GET" || method == "HEAD" else {
                rejected += 1
                send(connection, status: "405 Method Not Allowed", extra: "Allow: GET, HEAD\r\n")
                return
            }
            // No directory serving, URL decoding, query stripping, redirect or
            // filesystem fallback. An unlisted request can never expose a file.
            guard let file = files[path] else {
                rejected += 1
                send(connection, status: "404 Not Found")
                return
            }
            var fields: [String: String] = [:]
            for line in lines.dropFirst() {
                guard let colon = line.firstIndex(of: ":") else { continue }
                let name = line[..<colon].lowercased()
                let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
                fields[name] = fields[name].map { $0 + "," + value } ?? value
            }
            var body = file, status = "200 OK", extra = ""
            if let range = fields["range"] {
                guard let bounds = byteRange(range, length: file.count) else {
                    rejected += 1
                    send(connection, status: "416 Range Not Satisfiable",
                         extra: "Content-Range: bytes */\(file.count)\r\n")
                    return
                }
                body = file.subdata(in: bounds)
                status = "206 Partial Content"
                extra = "Content-Range: bytes \(bounds.lowerBound)-\(bounds.upperBound - 1)/\(file.count)\r\n"
            }
            if holdsResponses {
                heldRequests.insert(id)
                // Observe EOF/error from the actual blocked client rather than
                // retaining a historical request count until server teardown.
                // No second/pipelined request is accepted on this held socket.
                connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { _, _, _, _ in
                    connection.cancel()
                }
                return
            }
            served.insert(path)
            let type = path.hasSuffix(".m3u8") ? "application/vnd.apple.mpegurl" :
                path.hasSuffix(".mkv") ? "video/x-matroska" : "video/mp2t"
            send(connection, status: status, body: method == "HEAD" ? Data() : body, length: body.count,
                 extra: extra + "Content-Type: \(type)\r\nAccept-Ranges: bytes\r\n")
        }
    }

    private func byteRange(_ value: String, length: Int) -> Range<Int>? {
        guard value.hasPrefix("bytes="), length > 0 else { return nil }
        let parts = value.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 2 else { return nil }
        if parts[0].isEmpty {
            guard let suffix = Int(parts[1]), suffix > 0 else { return nil }
            return max(0, length - suffix)..<length
        }
        guard let first = Int(parts[0]), first >= 0, first < length else { return nil }
        let last: Int
        if parts[1].isEmpty { last = length - 1 }
        else {
            guard let requested = Int(parts[1]), requested >= first else { return nil }
            last = min(requested, length - 1)
        }
        return first..<(last + 1)
    }

    private func send(_ connection: NWConnection, status: String, body: Data = Data(),
                      length: Int = 0, extra: String = "") {
        let response = Data(("HTTP/1.1 \(status)\r\nContent-Length: \(length)\r\n" +
            "\(extra)Connection: close\r\n\r\n").utf8) + body
        connection.send(content: response, contentContext: .finalMessage, isComplete: true,
                        completion: .contentProcessed { _ in connection.cancel() })
    }
}

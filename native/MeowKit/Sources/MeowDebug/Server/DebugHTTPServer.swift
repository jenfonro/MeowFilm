import Foundation
import Network

/// Zero-dependency HTTP/1.1 Control Server built on Apple `Network.framework`.
public final class DebugHTTPServer: @unchecked Sendable {
    private let runtime: DebugRuntime
    private weak var delegate: (any DebugControlDelegate)?
    private let queue = DispatchQueue(label: "dev.meowfilm.debug.http", qos: .userInitiated)
    private let lock = NSLock()
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var actualPort: UInt16 = 0

    public init(runtime: DebugRuntime, delegate: any DebugControlDelegate) {
        self.runtime = runtime
        self.delegate = delegate
    }

    public var port: UInt16 {
        lock.lock(); defer { lock.unlock() }
        return actualPort
    }

    public func start() throws {
        let bind = runtime.configuration.bindAddress
        let requestedPort = runtime.configuration.port
        let nwListener: NWListener
        if bind == "0.0.0.0" || bind == "::" {
            if requestedPort == 0 {
                nwListener = try NWListener(using: .tcp)
            } else if let nwPort = NWEndpoint.Port(rawValue: requestedPort) {
                nwListener = try NWListener(using: .tcp, on: nwPort)
            } else {
                throw DebugArgumentError.invalidPort(String(requestedPort))
            }
        } else {
            let params = NWParameters.tcp
            let nwPort: NWEndpoint.Port = requestedPort == 0 ? .any : (NWEndpoint.Port(rawValue: requestedPort) ?? .any)
            params.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(bind), port: nwPort)
            nwListener = try NWListener(using: params)
        }

        nwListener.stateUpdateHandler = { [weak self, weak nwListener] state in
            guard let self else { return }
            switch state {
            case .ready:
                let assigned = nwListener?.port?.rawValue ?? requestedPort
                self.lock.lock()
                self.actualPort = assigned
                self.lock.unlock()
                self.runtime.session.updateControlEndpoint(bind: bind, port: assigned)
                DebugRuntime.log(
                    .lifecycle,
                    sub: "debug.server",
                    event: "listen.ready",
                    data: .object([
                        "bind": .string(bind),
                        "port": .int(Int(assigned)),
                        "controlUrl": .string(self.runtime.session.currentMetadata.controlUrl)
                    ])
                )
            case .failed(let error):
                DebugRuntime.log(
                    .lifecycle,
                    sub: "debug.server",
                    event: "listen.failed",
                    level: .error,
                    error: DebugErrorPayload(error: error, code: "DEBUG_SERVER_FAILED")
                )
            case .cancelled:
                DebugRuntime.log(.lifecycle, sub: "debug.server", event: "listen.stopped")
            default:
                break
            }
        }

        nwListener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }

        lock.lock()
        listener = nwListener
        lock.unlock()
        nwListener.start(queue: queue)
    }

    public func stop() {
        lock.lock()
        let activeListener = listener
        let activeConnections = Array(connections.values)
        listener = nil
        connections.removeAll()
        lock.unlock()

        activeListener?.stateUpdateHandler = nil
        activeListener?.newConnectionHandler = nil
        activeListener?.cancel()
        for connection in activeConnections {
            connection.stateUpdateHandler = nil
            connection.cancel()
        }
    }

    private func accept(_ connection: NWConnection) {
        let key = ObjectIdentifier(connection)
        lock.lock()
        connections[key] = connection
        lock.unlock()

        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled:
                self?.removeConnection(key)
            default:
                break
            }
        }
        connection.start(queue: queue)
        readRequest(from: connection, buffer: Data())
    }

    private func removeConnection(_ key: ObjectIdentifier) {
        lock.lock()
        connections.removeValue(forKey: key)
        lock.unlock()
    }

    private func readRequest(from connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] chunk, _, isComplete, error in
            guard let self else { return }
            if error != nil {
                connection.cancel()
                return
            }
            var next = buffer
            if let chunk, !chunk.isEmpty {
                next.append(chunk)
            }
            switch Self.parseHTTPRequest(next) {
            case .incomplete:
                if isComplete {
                    connection.cancel()
                } else {
                    self.readRequest(from: connection, buffer: next)
                }
            case .invalid(let message):
                self.sendJSONError(
                    on: connection,
                    traceId: DebugTraceContext.nextTraceID(),
                    error: .invalidArgument(message)
                )
            case .parsed(let request):
                Task { [weak self] in
                    await self?.handle(request, on: connection)
                }
            }
        }
    }

    private struct HTTPRequest: Sendable {
        let method: String
        let rawTarget: String
        let path: String
        let query: [String: String]
        let headers: [String: String]
        let body: Data
    }

    private enum ParseResult {
        case incomplete
        case invalid(String)
        case parsed(HTTPRequest)
    }

    private static func parseHTTPRequest(_ data: Data) -> ParseResult {
        let separator = Data([0x0D, 0x0A, 0x0D, 0x0A])
        guard let headerRange = data.range(of: separator) else {
            return .incomplete
        }
        let headerData = data[..<headerRange.lowerBound]
        guard let headerText = String(data: headerData, encoding: .utf8) else {
            return .invalid("Malformed UTF-8 HTTP header")
        }
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first, !requestLine.isEmpty else {
            return .invalid("Empty HTTP request line")
        }
        let parts = requestLine.split(separator: " ", omittingEmptySubsequences: true)
        guard parts.count >= 2 else { return .invalid("Invalid HTTP request line") }
        let method = String(parts[0]).uppercased()
        let rawTarget = String(parts[1])

        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            headers[key] = value
        }

        let bodyStart = headerRange.upperBound
        let body: Data
        if let transferEncoding = headers["transfer-encoding"]?.lowercased(),
           transferEncoding.contains("chunked") {
            switch Self.decodeChunkedBody(data[bodyStart...]) {
            case .incomplete: return .incomplete
            case .invalid(let msg): return .invalid(msg)
            case .decoded(let decoded): body = decoded
            }
        } else if let rawLen = headers["content-length"] {
            guard let parsedLen = Int(rawLen), parsedLen >= 0 else {
                return .invalid("Invalid Content-Length")
            }
            let availableBody = data.count - bodyStart
            guard availableBody >= parsedLen else { return .incomplete }
            body = Data(data[bodyStart..<(bodyStart + parsedLen)])
        } else {
            body = Data()
        }

        let path: String
        var query: [String: String] = [:]
        if let qIdx = rawTarget.firstIndex(of: "?") {
            path = String(rawTarget[..<qIdx])
            let rawQuery = String(rawTarget[rawTarget.index(after: qIdx)...])
            for pair in rawQuery.split(separator: "&", omittingEmptySubsequences: true) {
                if let eq = pair.firstIndex(of: "=") {
                    let rawKey = String(pair[..<eq]).replacingOccurrences(of: "+", with: " ")
                    let rawVal = String(pair[pair.index(after: eq)...]).replacingOccurrences(of: "+", with: " ")
                    let k = rawKey.removingPercentEncoding ?? rawKey
                    let v = rawVal.removingPercentEncoding ?? rawVal
                    query[k] = v
                } else {
                    let rawKey = String(pair).replacingOccurrences(of: "+", with: " ")
                    let k = rawKey.removingPercentEncoding ?? rawKey
                    query[k] = ""
                }
            }
        } else {
            path = rawTarget
        }

        return .parsed(HTTPRequest(method: method, rawTarget: rawTarget, path: path, query: query, headers: headers, body: body))
    }

    private enum ChunkedDecodeResult {
        case incomplete
        case invalid(String)
        case decoded(Data)
    }

    private static func decodeChunkedBody(_ slice: Data) -> ChunkedDecodeResult {
        let crlf = Data([0x0D, 0x0A])
        var cursor = slice.startIndex
        var output = Data()
        while true {
            guard let lineEnd = slice[cursor...].range(of: crlf) else {
                return .incomplete
            }
            guard let sizeLine = String(data: slice[cursor..<lineEnd.lowerBound], encoding: .ascii) else {
                return .invalid("Malformed chunk size header")
            }
            let hexPart = sizeLine.split(separator: ";", maxSplits: 1).first?
                .trimmingCharacters(in: .whitespaces) ?? ""
            guard !hexPart.isEmpty, let chunkSize = Int(hexPart, radix: 16), chunkSize >= 0 else {
                return .invalid("Invalid chunk size: \(sizeLine)")
            }
            let dataStart = lineEnd.upperBound
            if chunkSize == 0 {
                if slice[dataStart...].starts(with: crlf) {
                    return .decoded(output)
                }
                let trailerEnd = Data([0x0D, 0x0A, 0x0D, 0x0A])
                if slice[dataStart...].range(of: trailerEnd) != nil {
                    return .decoded(output)
                }
                return .incomplete
            }
            guard slice.endIndex - dataStart >= chunkSize + 2 else {
                return .incomplete
            }
            output.append(slice[dataStart..<(dataStart + chunkSize)])
            let trailerStart = dataStart + chunkSize
            guard slice[trailerStart] == 0x0D, slice[trailerStart + 1] == 0x0A else {
                return .invalid("Malformed chunk terminator")
            }
            cursor = trailerStart + 2
        }
    }

    private func handle(_ request: HTTPRequest, on connection: NWConnection) async {
        let traceId = request.headers["x-meow-debug-trace"]?.isEmpty == false
            ? request.headers["x-meow-debug-trace"]!
            : DebugTraceContext.nextTraceID()
        let opId = request.headers["x-meow-debug-op"]?.isEmpty == false
            ? request.headers["x-meow-debug-op"]!
            : (request.method == "POST" ? DebugTraceContext.nextOpID() : nil)
        let started = DispatchTime.now().uptimeNanoseconds

        await DebugTraceContext.withTrace(traceId: traceId, opId: opId) {
            do {
                let response = try await dispatch(request)
                let elapsedMs = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                if request.path.hasPrefix("/debug/v1/nav/") || request.path == "/debug/v1/ui/action" ||
                    request.path == "/debug/v1/playback/command" || request.path == "/debug/v1/novel/command" {
                    DebugRuntime.log(
                        .uiAction,
                        sub: "debug.api",
                        event: "request.end",
                        durationMs: elapsedMs,
                        data: .object([
                            "method": .string(request.method),
                            "path": .string(request.path),
                            "ok": .bool(true)
                        ])
                    )
                }
                switch response {
                case .json(let dataValue):
                    self.sendJSONSuccess(on: connection, traceId: traceId, data: dataValue)
                case .binary(let bin):
                    self.sendBinary(on: connection, status: 200, binary: bin, traceId: traceId)
                }
            } catch let apiError as DebugAPIError {
                self.sendJSONError(on: connection, traceId: traceId, error: apiError)
            } catch {
                self.sendJSONError(
                    on: connection,
                    traceId: traceId,
                    error: .internalError(
                        error.localizedDescription,
                        details: .object([
                            "type": .string(String(describing: type(of: error))),
                            "stack": .string(Thread.callStackSymbols.joined(separator: "\n"))
                        ])
                    )
                )
            }
        }
    }

    private enum DispatchResult {
        case json(DebugJSONValue)
        case binary(DebugBinaryResponse)
    }

    private func decodeJSONObjectBody(_ request: HTTPRequest) throws -> [String: DebugJSONValue] {
        guard !request.body.isEmpty else { return [:] }
        let decoded: DebugJSONValue
        do {
            decoded = try DebugJSONValue.decode(data: request.body)
        } catch {
            throw DebugAPIError.invalidArgument("Invalid JSON request body: \(error.localizedDescription)")
        }
        guard case .object(let fields) = decoded else {
            throw DebugAPIError.invalidArgument("Request body must be a JSON object")
        }
        return fields
    }

    private func dispatch(_ request: HTTPRequest) async throws -> DispatchResult {
        guard let delegate else {
            throw DebugAPIError.internalError("Debug host delegate detached")
        }

        // Direct session / log / file / blob endpoints served by MeowDebug core
        if request.method == "GET", request.path == "/debug/v1/sessions" {
            return .json(.object([
                "currentSessionId": .string(runtime.session.sessionId),
                "debugRoot": .string(runtime.session.debugRoot.path),
                "sessions": .array(runtime.session.listSessions())
            ]))
        }

        if request.method == "GET", request.path.hasPrefix("/debug/v1/files/") {
            let encodedRel = String(request.path.dropFirst("/debug/v1/files/".count))
            guard let rel = encodedRel.removingPercentEncoding else {
                throw DebugAPIError.invalidArgument("Invalid percent-encoding in file path")
            }
            let requestedSession = request.query["sessionId"]
            if requestedSession == nil || requestedSession?.isEmpty == true || requestedSession == runtime.session.sessionId {
                runtime.logger.flush()
            }
            guard let resolved = runtime.session.resolveSessionFile(sessionId: requestedSession, relativePath: rel) else {
                throw DebugAPIError.notFound("Session file not found or forbidden: \(rel)")
            }
            let bytes = try Data(contentsOf: resolved.url)
            return .binary(DebugBinaryResponse(
                data: bytes,
                contentType: resolved.contentType,
                headers: ["X-Meow-File-Path": rel]
            ))
        }

        if request.method == "GET", request.path == "/debug/v1/logs/categories" {
            let requestedSession = request.query["sessionId"]
            guard let sessionDir = runtime.session.resolveSessionDirectory(sessionId: requestedSession) else {
                throw DebugAPIError.notFound("Session not found")
            }
            return .json(.object([
                "sessionId": .string(sessionDir.lastPathComponent),
                "categories": .array(runtime.logger.categoriesSummary(in: sessionDir))
            ]))
        }

        if request.method == "GET", request.path == "/debug/v1/logs/tail" {
            guard let cat = request.query["cat"], !cat.isEmpty else {
                throw DebugAPIError.invalidArgument("Query parameter 'cat' is required (category name or 'all')")
            }
            guard let sessionDir = runtime.session.resolveSessionDirectory(sessionId: request.query["sessionId"]) else {
                throw DebugAPIError.notFound("Session not found")
            }
            let limit = request.query["limit"].flatMap(Int.init) ?? 100
            let sinceSeq = request.query["sinceSeq"].flatMap(Int64.init)
            let tailQuery = DebugLogger.TailQuery(
                cat: cat,
                limit: limit,
                sinceSeq: sinceSeq,
                level: request.query["level"],
                sub: request.query["sub"],
                event: request.query["event"],
                opId: request.query["opId"],
                traceId: request.query["traceId"],
                q: request.query["q"]
            )
            let events = try runtime.logger.tail(tailQuery, in: sessionDir)
            return .json(.object([
                "cat": .string(cat),
                "count": .int(events.count),
                "events": .array(events)
            ]))
        }

        if request.method == "GET", request.path.hasPrefix("/debug/v1/blobs/") {
            let sha = String(request.path.dropFirst("/debug/v1/blobs/".count))
            guard let sessionDir = runtime.session.resolveSessionDirectory(sessionId: request.query["sessionId"]) else {
                throw DebugAPIError.notFound("Session not found")
            }
            guard let blob = DebugBlobStore.readBlob(sessionDirectory: sessionDir, sha256: sha) else {
                throw DebugAPIError.notFound("Blob not found: \(sha)")
            }
            return .binary(DebugBinaryResponse(
                data: blob.data,
                contentType: blob.contentType,
                headers: ["X-Meow-Blob-Path": blob.relativePath]
            ))
        }

        // Delegate-backed Control API endpoints
        switch (request.method, request.path) {
        case ("GET", "/debug/v1/health"):
            return .json(try await delegate.debugHealth())
        case ("GET", "/debug/v1/capabilities"):
            return .json(try await delegate.debugCapabilities())
        case ("POST", "/debug/v1/config"):
            return .json(try await delegate.debugUpdateConfig(try decodeJSONObjectBody(request)))

        case ("POST", "/debug/v1/nav/open"):
            return .json(try await delegate.debugNavOpen(try decodeJSONObjectBody(request)))
        case ("POST", "/debug/v1/nav/back"):
            return .json(try await delegate.debugNavBack())
        case ("POST", "/debug/v1/nav/forward"):
            return .json(try await delegate.debugNavForward())
        case ("POST", "/debug/v1/nav/refresh"):
            return .json(try await delegate.debugNavRefresh())

        case ("GET", "/debug/v1/ui/state"):
            return .json(try await delegate.debugUIState())
        case ("GET", "/debug/v1/ui/content"):
            let maxItems = min(max(request.query["maxItems"].flatMap(Int.init) ?? 200, 1), 2000)
            let includeRaw = request.query["includeRaw"] == "true" || request.query["includeRaw"] == "1"
            return .json(try await delegate.debugUIContent(maxItems: maxItems, includeRaw: includeRaw))
        case ("GET", "/debug/v1/ui/tree"):
            let maxDepth = min(max(request.query["maxDepth"].flatMap(Int.init) ?? 8, 1), 32)
            let visibleOnly = request.query["visibleOnly"] != "false" && request.query["visibleOnly"] != "0"
            return .json(try await delegate.debugUITree(maxDepth: maxDepth, visibleOnly: visibleOnly))
        case ("GET", "/debug/v1/ui/screenshot"):
            let save = request.query["save"] != "false" && request.query["save"] != "0"
            if let format = request.query["format"], format.lowercased() != "png" {
                throw DebugAPIError.invalidArgument("Unsupported screenshot format: \(format) (only 'png' supported)")
            }
            return .binary(try await delegate.debugUIScreenshot(save: save))
        case ("POST", "/debug/v1/ui/action"):
            return .json(try await delegate.debugUIAction(try decodeJSONObjectBody(request)))
        case ("POST", "/debug/v1/ui/wait"):
            return .json(try await delegate.debugUIWait(try decodeJSONObjectBody(request)))

        case ("GET", "/debug/v1/playback/state"):
            return .json(try await delegate.debugPlaybackState())
        case ("POST", "/debug/v1/playback/command"):
            return .json(try await delegate.debugPlaybackCommand(try decodeJSONObjectBody(request)))
        case ("POST", "/debug/v1/novel/command"):
            return .json(try await delegate.debugNovelCommand(try decodeJSONObjectBody(request)))

        case ("POST", "/debug/v1/runtime/invoke"):
            return .json(try await delegate.debugRuntimeInvoke(try decodeJSONObjectBody(request)))
        case ("POST", "/debug/v1/storage/query"):
            return .json(try await delegate.debugStorageQuery(try decodeJSONObjectBody(request)))

        case ("GET", "/debug/v1/diag/summary"):
            return .json(try await delegate.debugDiagSummary())
        case ("GET", "/debug/v1/diag/threads"):
            return .json(try await delegate.debugDiagThreads())
        case ("POST", "/debug/v1/diag/gc"):
            return .json(try await delegate.debugDiagGC())
        case ("POST", "/debug/v1/diag/dump"):
            return .json(try await delegate.debugDiagDump(try decodeJSONObjectBody(request)))
        case ("POST", "/debug/v1/diag/node/heapsnapshot"):
            return .json(try await delegate.debugDiagNodeHeapSnapshot())
        case ("POST", "/debug/v1/diag/node/cpu-profile"):
            return .json(try await delegate.debugDiagNodeCPUProfile(try decodeJSONObjectBody(request)))
        case ("GET", "/debug/v1/diag/exceptions"):
            let limit = min(max(request.query["limit"].flatMap(Int.init) ?? 100, 1), 500)
            let kind = request.query["kind"] ?? "all"
            return .json(try await delegate.debugDiagExceptions(limit: limit, kind: kind))

        default:
            throw DebugAPIError.notFound("Unknown debug endpoint: \(request.method) \(request.path)")
        }
    }

    private func sendJSONSuccess(on connection: NWConnection, traceId: String, data: DebugJSONValue) {
        let envelope = DebugJSONValue.object([
            "ok": .bool(true),
            "ts": .string(DebugTimestamp.iso8601UTC()),
            "traceId": .string(traceId),
            "data": data
        ])
        let body = (try? envelope.encode()) ?? Data("{\"ok\":true}".utf8)
        sendRaw(
            on: connection,
            status: 200,
            contentType: "application/json; charset=utf-8",
            headers: ["X-Meow-Debug-Trace": traceId],
            body: body
        )
    }

    private func sendJSONError(on connection: NWConnection, traceId: String, error: DebugAPIError) {
        var errFields: [String: DebugJSONValue] = [
            "code": .string(error.code.rawValue),
            "message": .string(error.message)
        ]
        if let details = error.details {
            errFields["details"] = details
        } else {
            errFields["details"] = .object([:])
        }
        let envelope = DebugJSONValue.object([
            "ok": .bool(false),
            "ts": .string(DebugTimestamp.iso8601UTC()),
            "traceId": .string(traceId),
            "error": .object(errFields)
        ])
        let body = (try? envelope.encode()) ?? Data("{\"ok\":false}".utf8)
        sendRaw(
            on: connection,
            status: error.code.httpStatus,
            contentType: "application/json; charset=utf-8",
            headers: ["X-Meow-Debug-Trace": traceId],
            body: body
        )
    }

    private func sendBinary(on connection: NWConnection, status: Int, binary: DebugBinaryResponse, traceId: String) {
        var headers = binary.headers
        headers["X-Meow-Debug-Trace"] = traceId
        sendRaw(on: connection, status: status, contentType: binary.contentType, headers: headers, body: binary.data)
    }

    private func sendRaw(
        on connection: NWConnection,
        status: Int,
        contentType: String,
        headers: [String: String],
        body: Data
    ) {
        let reason: String
        switch status {
        case 200: reason = "OK"
        case 400: reason = "Bad Request"
        case 404: reason = "Not Found"
        case 409: reason = "Conflict"
        case 500: reason = "Internal Server Error"
        case 501: reason = "Not Implemented"
        case 503: reason = "Service Unavailable"
        case 504: reason = "Gateway Timeout"
        default: reason = "Status"
        }
        var headerLines = [
            "HTTP/1.1 \(status) \(reason)",
            "Content-Type: \(contentType)",
            "Content-Length: \(body.count)",
            "Connection: close",
            "Cache-Control: no-store"
        ]
        for (k, v) in headers.sorted(by: { $0.key < $1.key }) {
            headerLines.append("\(k): \(v)")
        }
        let head = headerLines.joined(separator: "\r\n") + "\r\n\r\n"
        var payload = Data(head.utf8)
        payload.append(body)
        connection.send(content: payload, isComplete: true, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }
}

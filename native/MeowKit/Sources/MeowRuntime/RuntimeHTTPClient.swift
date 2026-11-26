import Foundation
import MeowDebug

public enum RuntimeHTTPError: Error, Sendable, Equatable {
    case cancelled, timeout, closed, invalidRequest, invalidResponse, remote(String)
}

/// One HTTP client with ordinary and rules-only transports. Business modules
/// borrow it; only its App owner closes and drains the URLSessions.
public final class RuntimeHTTPClient: @unchecked Sendable {
    private static let protocolName = "runtime.http.v1"
    private static let neteaseTokenTimeout: TimeInterval = 60
    private static let catProbeTimeout: TimeInterval = 180
    private let baseURL: URL
    private let timeout: TimeInterval
    private let state: RuntimeHTTPState
    private let session: URLSession
    private let imageSession: URLSession?
    private let eventConfiguration: URLSessionConfiguration
    private let delegateQueue: OperationQueue?

    public convenience init(baseURL: URL, timeout: TimeInterval = 30) throws {
        try self.init(baseURL: baseURL, timeout: timeout, configuration: .ephemeral)
    }

    init(baseURL: URL, timeout: TimeInterval = 30,
         configuration: URLSessionConfiguration, delegateQueue: OperationQueue? = nil) throws {
        guard let components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty,
              timeout.isFinite, timeout > 0 else { throw RuntimeHTTPError.invalidRequest }
        self.baseURL = baseURL
        self.timeout = timeout
        self.delegateQueue = delegateQueue
        state = RuntimeHTTPState(hasImageSession: true)
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = timeout
        // Each read owns its complete deadline so a single call can extend it.
        configuration.timeoutIntervalForResource = 0
        eventConfiguration = configuration.copy() as! URLSessionConfiguration
        // Events have an idle timeout, but no whole-observation RPC deadline.
        eventConfiguration.timeoutIntervalForResource = 0
        let imageConfiguration = configuration.copy() as! URLSessionConfiguration
        session = URLSession(configuration: configuration, delegate: RuntimeHTTPDelegate(state: state, isImage: false),
                             delegateQueue: delegateQueue)
        imageSession = URLSession(configuration: imageConfiguration, delegate: RuntimeHTTPDelegate(state: state, isImage: true),
                                  delegateQueue: delegateQueue)
    }

    /// Rule execution starts after an unbounded shared queue wait. Its Node
    /// owner enforces each execution budget; HTTP must not add a batch timer.
    private init(rulesFor parent: RuntimeHTTPClient) {
        baseURL = parent.baseURL
        timeout = 0
        delegateQueue = parent.delegateQueue
        state = RuntimeHTTPState(hasImageSession: false)
        let configuration = parent.eventConfiguration.copy() as! URLSessionConfiguration
        configuration.timeoutIntervalForRequest = 0
        configuration.timeoutIntervalForResource = 0
        eventConfiguration = configuration
        session = URLSession(configuration: configuration, delegate: RuntimeHTTPDelegate(state: state, isImage: false),
                             delegateQueue: delegateQueue)
        imageSession = nil
    }

    public var isClosed: Bool { state.isClosed }
    public var endpointURL: URL { baseURL }
    var debugBaseURL: URL { baseURL }
    var debugSession: URLSession { session }
    var debugDefaultTimeout: TimeInterval { timeout }

    /// Observes irreversible client revocation, not HTTP drain or executor exit.
    /// Cancelling a borrower only removes its wait; it never closes this client.
    public func waitUntilClosed() async throws {
        try await state.waitUntilClosed()
    }

    public func describe() async throws -> RuntimeValue {
        try await request(method: "GET", path: "/v1/describe")
    }

    /// The relative reference is both the public image identity and cache key.
    public static func metadataImageURL(_ path: String) -> URL? {
        guard metadataImageProvider(path) != nil else { return nil }
        return URL(string: path)
    }

    private static let uploadAllowedCharacters = CharacterSet(
        charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"
    )

    private static func metadataImageProvider(_ path: String) -> RuntimeModule? {
        if path.hasPrefix("/v1/tmdb/images/"), path.count > "/v1/tmdb/images/".count { return .tmdb }
        if path.hasPrefix("/v1/douban/images/"), path.count > "/v1/douban/images/".count { return .douban }
        return nil
    }

    public func readMetadataImage(_ reference: URL) async throws -> Data {
        guard reference.baseURL == nil, reference.scheme == nil,
              let provider = Self.metadataImageProvider(reference.absoluteString) else { throw RuntimeHTTPError.invalidRequest }
        return try await readTrustedImage(reference, provider: provider)
    }

    public static func sourceImageURL(_ path: String) -> URL? {
        guard path.hasPrefix("/v1/source/images/"), path.count > "/v1/source/images/".count else { return nil }
        return URL(string: path)
    }

    public func readSourceImage(_ reference: String) async throws -> Data {
        guard let url = Self.sourceImageURL(reference) else { throw RuntimeHTTPError.invalidRequest }
        return try await readTrustedImage(url, provider: .source)
    }

    private func readTrustedImage(_ reference: URL, provider: RuntimeModule) async throws -> Data {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        let requestID = UUID().uuidString.lowercased()
        var request = URLRequest(url: URL(string: reference.absoluteString, relativeTo: baseURL)!.absoluteURL,
                                 cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpShouldHandleCookies = false
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        request.setValue("image/*", forHTTPHeaderField: "Accept")
        let debugEnabled = DebugRuntime.isEnabled
        let debugOp = debugEnabled ? (DebugTraceContext.current.opId ?? DebugTraceContext.nextOpID()) : nil
        let debugTrace = debugEnabled ? DebugTraceContext.current.traceId : nil
        if let debugOp { request.setValue(debugOp, forHTTPHeaderField: "X-Meow-Debug-Op") }
        if let debugTrace { request.setValue(debugTrace, forHTTPHeaderField: "X-Meow-Debug-Trace") }
        let started = debugEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        let read = RuntimeHTTPImageRead(provider: provider, state: state)
        let outgoing = request
        let data: Data = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.start(continuation: continuation) {
                    let transport = imageSession ?? session
                    let task = transport.dataTask(with: outgoing)
                    task.delegate = read
                    read.install(task, continuation: continuation, timeout: timeout)
                    task.resume()
                }
            }
        } onCancel: { read.cancel() }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        if debugEnabled {
            let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
            DebugRuntime.log(.runtimeBridge, sub: "http.image", event: "response", level: .debug,
                             opId: debugOp, traceId: debugTrace, durationMs: ms, data: .object([
                                "path": .string(reference.absoluteString),
                                "provider": .string(provider.rawValue),
                                "bytes": .int(data.count)
                             ]))
        }
        return data
    }

    public func call(module: RuntimeModule, operation: String,
                     parameters: RuntimeValue = .object([:]), timeout: TimeInterval? = nil,
                     onDispatch: (@Sendable () throws -> Void)? = nil) async throws -> RuntimeValue {
        if let timeout, !timeout.isFinite || timeout <= 0 { throw RuntimeHTTPError.invalidRequest }
        guard !operation.isEmpty else {
            throw RuntimeHTTPError.invalidRequest
        }
        let body: Data
        do { body = try RuntimeJSON.encode(parameters) }
        catch { throw RuntimeHTTPError.invalidRequest }
        if module == .source, operation == "rules.process" {
            let transport = try state.rulesClient { RuntimeHTTPClient(rulesFor: self) }
            let value = try await transport.request(method: "POST", path: "/v1/source/rules.process",
                                                    body: body, module: .source, onDispatch: onDispatch)
            guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
            guard !isClosed else { throw RuntimeHTTPError.closed }
            return value
        }
        let defaultTimeout = module == .netease ? max(self.timeout, Self.neteaseTokenTimeout)
            : module == .cat && operation == "probe" ? max(self.timeout, Self.catProbeTimeout)
            : self.timeout
        let effectiveTimeout = timeout ?? defaultTimeout
        let preserveReceipt = module == .source && operation == "library.image"
        if effectiveTimeout > self.timeout {
            let transport = try state.rulesClient { RuntimeHTTPClient(rulesFor: self) }
            let value = try await transport.request(method: "POST", path: "/v1/\(module.rawValue)/\(operation)",
                                                    body: body, module: module, timeout: effectiveTimeout,
                                                    preservingAcceptedReceipt: preserveReceipt, onDispatch: onDispatch)
            guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
            guard !isClosed else { throw RuntimeHTTPError.closed }
            return value
        }
        return try await request(method: "POST", path: "/v1/\(module.rawValue)/\(operation)",
                                 body: body, module: module,
                                 timeout: effectiveTimeout,
                                 preservingAcceptedReceipt: preserveReceipt,
                                 onDispatch: onDispatch)
    }

    /// Observes snapshots without owning the business session or the App runtime.
    /// The module identifier is independent of the finite RPC module registry.
    public func events(moduleID: String, sessionID: UUID, after: Int64 = 0) throws -> RuntimeHTTPEvents {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard let first = moduleID.utf8.first, (97...122).contains(first),
              moduleID.utf8.allSatisfy({ (97...122).contains($0) || (48...57).contains($0) || $0 == 45 }),
              after >= 0, after <= 9_007_199_254_740_991 else { throw RuntimeHTTPError.invalidRequest }
        let requestID = UUID().uuidString.lowercased(), id = UUID()
        let url = URL(string: "/v1/\(moduleID)/events/\(sessionID.uuidString.lowercased())?after=\(after)",
                      relativeTo: baseURL)!.absoluteURL
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpShouldHandleCookies = false
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        if DebugRuntime.isEnabled {
            let op = DebugTraceContext.current.opId ?? DebugTraceContext.nextOpID()
            request.setValue(op, forHTTPHeaderField: "X-Meow-Debug-Op")
            if let trace = DebugTraceContext.current.traceId {
                request.setValue(trace, forHTTPHeaderField: "X-Meow-Debug-Trace")
            }
            DebugRuntime.log(.runtimeBridge, sub: "http.events", event: "open", opId: op, data: .object([
                "module": .string(moduleID),
                "sessionId": .string(sessionID.uuidString.lowercased()),
                "after": .integer(after)
            ]))
        }
        let state = state
        let events = RuntimeHTTPEvents(sessionID: sessionID, after: after) { state.endEvents(id) }
        guard state.startEvents(id, events.connection) else { throw RuntimeHTTPError.closed }
        events.connection.start(request: request, configuration: eventConfiguration)
        return events
    }

    public func upload(module: RuntimeModule, installationID: String, sourceRevision: Int,
                       source: Data, basename: String? = nil) async throws -> RuntimeValue {
        guard sourceRevision > 0, sourceRevision <= 9_007_199_254_740_991 else {
            throw RuntimeHTTPError.invalidRequest
        }
        guard let id = installationID.addingPercentEncoding(withAllowedCharacters: Self.uploadAllowedCharacters),
              let name = (basename ?? "").addingPercentEncoding(withAllowedCharacters: Self.uploadAllowedCharacters) else {
            throw RuntimeHTTPError.invalidRequest
        }
        let query = basename == nil ? "" : "?basename=\(name)"
        return try await request(method: "PUT",
            path: "/v1/\(module.rawValue)/sources/\(id)/\(sourceRevision)\(query)",
            body: source, raw: true, module: module)
    }

    /// The public receipt confirms business cleanup; the launcher still owns
    /// the listener and must separately wait for node_start to return.
    public func shutdown() async throws {
        _ = try await request(method: "POST", path: "/v1/close", body: Data("{}".utf8))
    }

    public func settingsChanged(_ block: String) async throws {
        let body = try RuntimeJSON.encode(.object(["block": .string(block)]))
        let value = try await request(method: "POST", path: "/v1/settings/changed", body: body)
        guard value == .object(["notified": .bool(true)]) else { throw RuntimeHTTPError.invalidResponse }
    }

    public func close() async {
        invalidate()
        await state.acceptedRulesClient?.close()
        await state.waitForInvalidation()
    }

    func invalidate() {
        if state.beginClose() {
            session.invalidateAndCancel()
            imageSession?.invalidateAndCancel()
        }
    }

    private func request(method: String, path: String, body: Data? = nil, raw: Bool = false,
                         module: RuntimeModule? = nil, timeout: TimeInterval? = nil,
                         preservingAcceptedReceipt: Bool = false,
                         onDispatch: (@Sendable () throws -> Void)? = nil) async throws -> RuntimeValue {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        guard let url = URL(string: path, relativeTo: baseURL)?.absoluteURL else {
            throw RuntimeHTTPError.invalidRequest
        }
        let requestID = UUID().uuidString.lowercased()
        let deadline = timeout ?? self.timeout
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: deadline)
        request.httpMethod = method
        request.httpBody = body
        request.httpShouldHandleCookies = false
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        let debugEnabled = DebugRuntime.isEnabled
        let debugOp = debugEnabled ? (DebugTraceContext.current.opId ?? DebugTraceContext.nextOpID()) : nil
        let debugTrace = debugEnabled ? DebugTraceContext.current.traceId : nil
        if let debugOp { request.setValue(debugOp, forHTTPHeaderField: "X-Meow-Debug-Op") }
        if let debugTrace { request.setValue(debugTrace, forHTTPHeaderField: "X-Meow-Debug-Trace") }
        let started = debugEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        if let body {
            request.setValue(raw ? "application/octet-stream" : "application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(String(body.count), forHTTPHeaderField: "Content-Length")
        }
        let outgoingRequest = request
        let owner = RuntimeHTTPRequest()
        let state = state
        let (data, response): (Data, HTTPURLResponse)
        do {
            (data, response) = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.start(continuation: continuation) {
                    do { try onDispatch?() }
                    catch { continuation.resume(throwing: error); return }
                    let task = session.dataTask(with: outgoingRequest) { data, response, error in
                        let cancellation = owner.finish()
                        if state.isClosed { continuation.resume(throwing: RuntimeHTTPError.closed) }
                        else if let cancellation, !(preservingAcceptedReceipt && cancellation == .cancelled
                            && error == nil && data != nil && response is HTTPURLResponse) {
                            continuation.resume(throwing: cancellation)
                        }
                        else if let error {
                            let code = (error as NSError).code
                            continuation.resume(throwing: code == NSURLErrorTimedOut ? RuntimeHTTPError.timeout
                                : code == NSURLErrorCancelled ? RuntimeHTTPError.cancelled : RuntimeHTTPError.closed)
                        } else if let data, let response = response as? HTTPURLResponse {
                            continuation.resume(returning: (data, response))
                        } else { continuation.resume(throwing: RuntimeHTTPError.invalidResponse) }
                    }
                    owner.install(task, timeout: deadline)
                    task.resume()
                }
            }
        } onCancel: { owner.cancel() }
        } catch {
            if debugEnabled {
                let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.logError(.runtimeBridge, sub: module?.rawValue ?? "http", event: "rpc.transport_error",
                                      error: error, opId: debugOp, traceId: debugTrace, durationMs: ms, data: .object([
                                        "method": .string(method),
                                        "path": .string(path)
                                      ]))
            }
            throw error
        }
        // A successfully completed resource-acquisition response transfers a
        // lease to its owner. Late cancellation must not hide that receipt.
        guard preservingAcceptedReceipt || !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        let result = Result { try Self.value(data: data, response: response, module: module) }
        if debugEnabled {
            let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
            let reqText: DebugJSONValue = raw
                ? .string("<binary \(body?.count ?? 0) bytes>")
                : (body.flatMap { String(data: $0, encoding: .utf8) }.map(DebugJSONValue.string) ?? .null)
            let respText: DebugJSONValue = String(data: data, encoding: .utf8).map(DebugJSONValue.string)
                ?? .string("<binary \(data.count) bytes>")
            switch result {
            case .success:
                DebugRuntime.log(.runtimeBridge, sub: module?.rawValue ?? "http", event: "rpc.ok", level: .debug,
                                 opId: debugOp, traceId: debugTrace, durationMs: ms, data: .object([
                                    "method": .string(method),
                                    "path": .string(path),
                                    "status": .int(response.statusCode),
                                    "request": reqText,
                                    "response": respText
                                 ]))
            case .failure(let error):
                DebugRuntime.log(.runtimeBridge, sub: module?.rawValue ?? "http", event: "rpc.error", level: .error,
                                 opId: debugOp, traceId: debugTrace, durationMs: ms, data: .object([
                                    "method": .string(method),
                                    "path": .string(path),
                                    "status": .int(response.statusCode),
                                    "request": reqText,
                                    "response": respText
                                 ]),
                                 error: DebugErrorPayload(error: error, code: String(describing: error)))
            }
        }
        guard preservingAcceptedReceipt || !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        return try result.get()
    }

    fileprivate static func value(data: Data, response: HTTPURLResponse,
                                  module: RuntimeModule?, imageFailure: Bool = false) throws -> RuntimeValue {
        guard response.value(forHTTPHeaderField: "Content-Type") == "application/json",
              ([200, 400, 401, 403, 404, 413, 415].contains(response.statusCode) ||
                imageFailure && response.statusCode == 502),
              let decoded = try? RuntimeJSON.envelope(data), case .object(let fields) = decoded,
              case .bool(let ok)? = fields["ok"],
              case .string(let contract)? = fields["contract"] else { throw RuntimeHTTPError.invalidResponse }
        guard contract == protocolName || (module != nil && contract == module!.contract) else {
            throw RuntimeHTTPError.invalidResponse
        }
        if ok {
            guard contract == (module?.contract ?? protocolName), response.statusCode == 200,
                  let value = fields["value"] else {
                throw RuntimeHTTPError.invalidResponse
            }
            return value
        }
        guard case .string(let code)? = fields["code"], !code.isEmpty else {
            throw RuntimeHTTPError.invalidResponse
        }
        throw RuntimeHTTPError.remote(code)
    }

    deinit { invalidate() }
}

private final class RuntimeHTTPRequest: @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancellation: RuntimeHTTPError?
    private var timer: DispatchWorkItem?
    func install(_ task: URLSessionDataTask, timeout: TimeInterval) {
        lock.lock()
        self.task = task
        let timer = timeout > 0 ? DispatchWorkItem { [weak self] in self?.cancel(.timeout) } : nil
        self.timer = timer
        let cancelled = cancellation != nil
        lock.unlock()
        if let timer { DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: timer) }
        if cancelled { task.cancel() }
    }
    func cancel(_ reason: RuntimeHTTPError = .cancelled) {
        lock.lock()
        if cancellation == nil { cancellation = reason }
        let task = task
        lock.unlock()
        task?.cancel()
    }
    func finish() -> RuntimeHTTPError? {
        lock.lock()
        defer { lock.unlock() }
        timer?.cancel(); timer = nil
        task = nil
        return cancellation
    }
}

private final class RuntimeHTTPState: @unchecked Sendable {
    private let lock = NSLock()
    private var closed = false
    private var rpcInvalidated = false
    private var imageInvalidated: Bool
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var closureWaiters: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var events: [UUID: RuntimeHTTPEventConnection] = [:]
    private var rulesTransport: RuntimeHTTPClient?
    init(hasImageSession: Bool = true) {
        imageInvalidated = !hasImageSession
    }
    private var isFullyInvalidated: Bool { rpcInvalidated && imageInvalidated }
    var isClosed: Bool {
        lock.lock(); defer { lock.unlock() }
        return closed
    }
    var acceptedRulesClient: RuntimeHTTPClient? {
        lock.lock(); defer { lock.unlock() }
        return rulesTransport
    }
    func rulesClient(create: () -> RuntimeHTTPClient) throws -> RuntimeHTTPClient {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw RuntimeHTTPError.closed }
        if let rulesTransport { return rulesTransport }
        let client = create()
        rulesTransport = client
        return client
    }
    func start<T>(continuation: CheckedContinuation<T, Error>, body: () -> Void) {
        lock.lock()
        if closed { lock.unlock(); continuation.resume(throwing: RuntimeHTTPError.closed); return }
        body()
        lock.unlock()
    }
    func beginClose() -> Bool {
        lock.lock()
        guard !closed else { lock.unlock(); return false }
        closed = true
        let pending = Array(closureWaiters.values)
        closureWaiters.removeAll()
        let active = Array(events.values)
        let rules = rulesTransport
        lock.unlock()
        for waiter in pending { waiter.resume() }
        for connection in active { connection.cancel(.closed) }
        rules?.invalidate()
        return true
    }
    func waitUntilClosed() async throws {
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                lock.lock()
                if Task.isCancelled { lock.unlock(); continuation.resume(throwing: CancellationError()); return }
                if closed { lock.unlock(); continuation.resume(); return }
                closureWaiters[id] = continuation
                lock.unlock()
            }
        } onCancel: { self.cancelClosureWait(id) }
        try Task.checkCancellation()
    }
    private func cancelClosureWait(_ id: UUID) {
        lock.lock()
        let waiter = closureWaiters.removeValue(forKey: id)
        lock.unlock()
        waiter?.resume(throwing: CancellationError())
    }
    func startEvents(_ id: UUID, _ connection: RuntimeHTTPEventConnection) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { return false }
        events[id] = connection
        return true
    }
    func endEvents(_ id: UUID) {
        lock.lock()
        events.removeValue(forKey: id)
        let pending = isFullyInvalidated && events.isEmpty ? waiters : []
        if isFullyInvalidated && events.isEmpty { waiters.removeAll() }
        lock.unlock()
        for waiter in pending { waiter.resume() }
    }
    func waitForInvalidation() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if isFullyInvalidated && events.isEmpty { lock.unlock(); continuation.resume(); return }
            waiters.append(continuation)
            lock.unlock()
        }
    }
    func didInvalidate(isImage: Bool) {
        lock.lock()
        closed = true
        if isImage { imageInvalidated = true } else { rpcInvalidated = true }
        let fullyInvalidated = isFullyInvalidated
        let closing = Array(closureWaiters.values)
        closureWaiters.removeAll()
        let pending = fullyInvalidated && events.isEmpty ? waiters : []
        if fullyInvalidated && events.isEmpty { waiters.removeAll() }
        lock.unlock()
        for waiter in closing { waiter.resume() }
        for waiter in pending { waiter.resume() }
    }
}

/// A task delegate on the same owner session: cancellation and failure stop the read,
/// but its continuation settles only in didComplete, never at cancel().
private final class RuntimeHTTPImageRead: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let provider: RuntimeModule
    private let state: RuntimeHTTPState
    private var task: URLSessionDataTask?
    private var continuation: CheckedContinuation<Data, Error>?
    private var response: HTTPURLResponse?
    private var bytes = Data()
    private var failure: Error?
    private var cancelled = false
    private var timer: DispatchWorkItem?
    init(provider: RuntimeModule, state: RuntimeHTTPState) {
        self.provider = provider; self.state = state
    }
    func install(_ task: URLSessionDataTask, continuation: CheckedContinuation<Data, Error>, timeout: TimeInterval) {
        lock.lock()
        self.task = task; self.continuation = continuation
        let timer = DispatchWorkItem { [weak self] in self?.expire() }
        self.timer = timer
        let cancelled = cancelled
        lock.unlock()
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: timer)
        if cancelled { task.cancel() }
    }
    private func expire() {
        lock.lock()
        guard continuation != nil else { lock.unlock(); return }
        if failure == nil { failure = RuntimeHTTPError.timeout }
        let task = task
        lock.unlock()
        task?.cancel()
    }
    func cancel() {
        lock.lock(); cancelled = true; let task = task; lock.unlock()
        task?.cancel()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        if let response = response as? HTTPURLResponse {
            self.response = response
            let type = response.mimeType?.lowercased() ?? ""
            if response.statusCode == 200 && type.hasPrefix("image/") {
                // Native ImageIO still validates and decodes the complete image.
            } else if type != "application/json" || response.statusCode == 200 {
                failure = RuntimeHTTPError.invalidResponse
            }
        } else { failure = RuntimeHTTPError.invalidResponse }
        let allow = failure == nil && !cancelled
        lock.unlock()
        completionHandler(allow ? .allow : .cancel)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard !cancelled, failure == nil else { lock.unlock(); return }
        bytes.append(data)
        lock.unlock()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        guard let continuation else { lock.unlock(); return }
        self.continuation = nil; self.task = nil
        timer?.cancel(); timer = nil
        let result: Result<Data, Error>
        if cancelled { result = .failure(RuntimeHTTPError.cancelled) }
        else if let failure { result = .failure(failure) }
        else if let error {
            result = .failure((error as NSError).code == NSURLErrorTimedOut
                ? RuntimeHTTPError.timeout : RuntimeHTTPError.invalidResponse)
        } else if let response {
            result = Result {
                guard !bytes.isEmpty else { throw RuntimeHTTPError.invalidResponse }
                if response.statusCode != 200 {
                    _ = try RuntimeHTTPClient.value(data: bytes, response: response,
                                                    module: provider, imageFailure: true)
                    throw RuntimeHTTPError.invalidResponse
                }
                return bytes
            }
        } else { result = .failure(RuntimeHTTPError.invalidResponse) }
        bytes.removeAll()
        lock.unlock()
        let finalResult: Result<Data, Error> = state.isClosed ? .failure(RuntimeHTTPError.closed) : result
        continuation.resume(with: finalResult)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        lock.lock(); failure = RuntimeHTTPError.invalidResponse; lock.unlock()
        completionHandler(nil)
    }
}

private final class RuntimeHTTPDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let state: RuntimeHTTPState
    let isImage: Bool
    init(state: RuntimeHTTPState, isImage: Bool) {
        self.state = state
        self.isImage = isImage
    }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) {
        state.didInvalidate(isImage: isImage)
    }
}

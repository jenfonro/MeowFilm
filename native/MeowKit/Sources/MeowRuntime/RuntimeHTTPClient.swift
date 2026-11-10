import Foundation

public enum RuntimeHTTPError: Error, Sendable, Equatable {
    case cancelled, timeout, closed, invalidRequest, invalidResponse, remote(String)
}

/// One HTTP client. Business modules borrow this client; only its
/// App owner closes it. URLSession owns the actual HTTP tasks and their drain.
public final class RuntimeHTTPClient: @unchecked Sendable {
    private static let protocolName = "runtime.http.v1"
    private let baseURL: URL
    private let timeout: TimeInterval
    private let state = RuntimeHTTPState()
    private let session: URLSession
    private let eventConfiguration: URLSessionConfiguration

    public convenience init(baseURL: URL, timeout: TimeInterval = 30) throws {
        try self.init(baseURL: baseURL, timeout: timeout, configuration: .ephemeral)
    }

    init(baseURL: URL, timeout: TimeInterval = 30,
         configuration: URLSessionConfiguration) throws {
        guard let components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty,
              components.port == nil || (1...65535).contains(components.port!),
              components.path.isEmpty || components.path == "/",
              components.query == nil, components.fragment == nil,
              timeout.isFinite, timeout > 0 else { throw RuntimeHTTPError.invalidRequest }
        self.baseURL = baseURL
        self.timeout = timeout
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        eventConfiguration = configuration.copy() as! URLSessionConfiguration
        // Events have an idle timeout, but no whole-observation RPC deadline.
        eventConfiguration.timeoutIntervalForResource = 0
        session = URLSession(configuration: configuration, delegate: RuntimeHTTPDelegate(state: state),
                             delegateQueue: nil)
    }

    public var isClosed: Bool { state.isClosed }

    /// Observes irreversible client revocation, not HTTP drain or executor exit.
    /// Cancelling a borrower only removes its wait; it never closes this client.
    public func waitUntilClosed() async throws {
        try await state.waitUntilClosed()
    }

    public func describe() async throws -> RuntimeValue {
        try await request(method: "GET", path: "/v1/describe")
    }

    /// Validate the actual App assembly, not the generic runtime's default.
    public func validateAppModules() async throws {
        guard case .object(let description) = try await describe(),
              description["protocol"] == .string(Self.protocolName),
              description["product"] == .string("aggregate"),
              case .array(let modules)? = description["modules"], modules.count == 7 else {
            throw RuntimeHTTPError.invalidResponse
        }
        let expected: [(RuntimeModule, String)] = [(.music, "0.8.0"), (.cat, "0.2.0"), (.source, "0.2.0"),
                                                   (.pan, "0.1.0"), (.tmdb, "0.1.0"), (.douban, "0.1.0"),
                                                   (.film, "0.1.0")]
        for (module, version) in expected {
            let matches = modules.filter {
                guard case .object(let fields) = $0 else { return false }
                return fields["id"] == .string(module.rawValue) &&
                    fields["version"] == .string(version) && fields["contract"] == .string(module.contract)
            }
            guard matches.count == 1 else { throw RuntimeHTTPError.invalidResponse }
            if module == .tmdb {
                guard case .object(let fields) = matches[0], case .array(let methods)? = fields["methods"],
                      methods.count == 3, Set(methods.compactMap {
                          if case .string(let value) = $0 { return value }; return nil
                      }) == ["tmdb.data.request", "tmdb.data.settings.get", "tmdb.data.settings.update"] else {
                    throw RuntimeHTTPError.invalidResponse
                }
            }
        }
    }

    /// The relative reference is both the public image identity and cache key.
    /// Image references are protocol paths, not alternate runtime endpoints.
    public static func metadataImageURL(_ path: String) -> URL? {
        guard metadataImageProvider(path) != nil else { return nil }
        return URL(string: path)
    }

    private static func metadataImageProvider(_ path: String) -> RuntimeModule? {
        if path.range(of: #"^/v1/tmdb/images/(original|[wh][1-9][0-9]*)/[A-Za-z0-9_-]+\.[A-Za-z0-9]+$"#,
                      options: .regularExpression)?.upperBound == path.endIndex { return .tmdb }
        guard path.range(of: #"\A/v1/douban/images/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+(?:/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+)*\z"#,
                         options: .regularExpression)?.upperBound == path.endIndex else { return nil }
        return .douban
    }

    public func readMetadataImage(_ reference: URL) async throws -> Data {
        guard reference.baseURL == nil, reference.scheme == nil,
              let provider = Self.metadataImageProvider(reference.absoluteString) else { throw RuntimeHTTPError.invalidRequest }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        let requestID = UUID().uuidString.lowercased()
        var request = URLRequest(url: URL(string: reference.absoluteString, relativeTo: baseURL)!.absoluteURL,
                                 cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpShouldHandleCookies = false
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        request.setValue("image/*", forHTTPHeaderField: "Accept")
        let read = RuntimeHTTPImageRead(requestID: requestID, provider: provider, state: state)
        let outgoing = request
        let data: Data = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.start(continuation: continuation) {
                    let task = session.dataTask(with: outgoing)
                    task.delegate = read
                    read.install(task, continuation: continuation)
                    task.resume()
                }
            }
        } onCancel: { read.cancel() }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        return data
    }

    public func call(module: RuntimeModule, operation: String,
                     parameters: RuntimeValue = .object([:])) async throws -> RuntimeValue {
        guard operation.range(of: #"^[a-z]+(?:\.[a-z]+)*$"#, options: .regularExpression)?.upperBound == operation.endIndex else {
            throw RuntimeHTTPError.invalidRequest
        }
        let body: Data
        do { body = try RuntimeJSON.encode(parameters) }
        catch { throw RuntimeHTTPError.invalidRequest }
        return try await request(method: "POST", path: "/v1/\(module.rawValue)/\(operation)",
                                 body: body, module: module)
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
        let state = state
        let events = RuntimeHTTPEvents(sessionID: sessionID, requestID: requestID) { state.endEvents(id) }
        guard state.startEvents(id, events.connection) else { throw RuntimeHTTPError.closed }
        events.connection.start(request: request, configuration: eventConfiguration)
        return events
    }

    public func upload(module: RuntimeModule, installationID: String, sourceRevision: Int,
                       source: Data, basename: String? = nil) async throws -> RuntimeValue {
        guard sourceRevision > 0, sourceRevision <= 9_007_199_254_740_991 else {
            throw RuntimeHTTPError.invalidRequest
        }
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        guard let id = installationID.addingPercentEncoding(withAllowedCharacters: allowed),
              let name = (basename ?? "").addingPercentEncoding(withAllowedCharacters: allowed) else {
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

    public func close() async {
        invalidate()
        await state.waitForInvalidation()
    }

    func invalidate() {
        if state.beginClose() { session.invalidateAndCancel() }
    }

    private func request(method: String, path: String, body: Data? = nil, raw: Bool = false,
                         module: RuntimeModule? = nil) async throws -> RuntimeValue {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        guard let url = URL(string: path, relativeTo: baseURL)?.absoluteURL else {
            throw RuntimeHTTPError.invalidRequest
        }
        let requestID = UUID().uuidString.lowercased()
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpMethod = method
        request.httpBody = body
        request.httpShouldHandleCookies = false
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        if let body {
            request.setValue(raw ? "application/octet-stream" : "application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(String(body.count), forHTTPHeaderField: "Content-Length")
        }
        let outgoingRequest = request
        let owner = RuntimeHTTPRequest()
        let state = state
        let (data, response): (Data, HTTPURLResponse) = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                state.start(continuation: continuation) {
                    let task = session.dataTask(with: outgoingRequest) { data, response, error in
                        let cancelled = owner.finish()
                        if state.isClosed { continuation.resume(throwing: RuntimeHTTPError.closed) }
                        else if cancelled { continuation.resume(throwing: RuntimeHTTPError.cancelled) }
                        else if let error {
                            let code = (error as NSError).code
                            continuation.resume(throwing: code == NSURLErrorTimedOut ? RuntimeHTTPError.timeout
                                : code == NSURLErrorCancelled ? RuntimeHTTPError.cancelled : RuntimeHTTPError.closed)
                        } else if let data, let response = response as? HTTPURLResponse {
                            continuation.resume(returning: (data, response))
                        } else { continuation.resume(throwing: RuntimeHTTPError.invalidResponse) }
                    }
                    owner.install(task)
                    task.resume()
                }
            }
        } onCancel: { owner.cancel() }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        let result = Result { try Self.value(data: data, response: response, requestID: requestID, module: module) }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        return try result.get()
    }

    fileprivate static func value(data: Data, response: HTTPURLResponse, requestID: String,
                                  module: RuntimeModule?, imageFailure: Bool = false) throws -> RuntimeValue {
        guard response.value(forHTTPHeaderField: "X-Meow-Request-Id") == requestID,
              response.value(forHTTPHeaderField: "Content-Type") == "application/json",
              ([200, 400, 401, 403, 404, 413, 415].contains(response.statusCode) ||
                imageFailure && response.statusCode == 502),
              let decoded = try? RuntimeJSON.envelope(data), case .object(let fields) = decoded,
              fields.count == 3, case .bool(let ok)? = fields["ok"],
              case .string(let contract)? = fields["contract"] else { throw RuntimeHTTPError.invalidResponse }
        let codes: Set<String>
        if contract == protocolName { codes = RuntimeModule.publicCodes }
        else if let module, contract == module.contract { codes = module.codes }
        else { throw RuntimeHTTPError.invalidResponse }
        if ok {
            guard contract == (module?.contract ?? protocolName), response.statusCode == 200,
                  let value = fields["value"], (try? RuntimeJSON.validate(value)) != nil else {
                throw RuntimeHTTPError.invalidResponse
            }
            return value
        }
        guard case .string(let code)? = fields["code"], codes.contains(code) else {
            throw RuntimeHTTPError.invalidResponse
        }
        throw RuntimeHTTPError.remote(code)
    }

    deinit { invalidate() }
}

private final class RuntimeHTTPRequest: @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false
    func install(_ task: URLSessionDataTask) {
        lock.lock()
        self.task = task
        let cancelled = cancelled
        lock.unlock()
        if cancelled { task.cancel() }
    }
    func cancel() {
        lock.lock()
        cancelled = true
        let task = task
        lock.unlock()
        task?.cancel()
    }
    func finish() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        task = nil
        return cancelled
    }
}

private final class RuntimeHTTPState: @unchecked Sendable {
    private let lock = NSLock()
    private var closed = false
    private var invalidated = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var closureWaiters: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var events: [UUID: RuntimeHTTPEventConnection] = [:]
    var isClosed: Bool {
        lock.lock(); defer { lock.unlock() }
        return closed
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
        lock.unlock()
        for waiter in pending { waiter.resume() }
        for connection in active { connection.cancel(.closed) }
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
        let pending = invalidated && events.isEmpty ? waiters : []
        if invalidated && events.isEmpty { waiters.removeAll() }
        lock.unlock()
        for waiter in pending { waiter.resume() }
    }
    func waitForInvalidation() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if invalidated && events.isEmpty { lock.unlock(); continuation.resume(); return }
            waiters.append(continuation)
            lock.unlock()
        }
    }
    func didInvalidate() {
        lock.lock()
        closed = true; invalidated = true
        let closing = Array(closureWaiters.values)
        closureWaiters.removeAll()
        let pending = events.isEmpty ? waiters : []
        if events.isEmpty { waiters.removeAll() }
        lock.unlock()
        for waiter in closing { waiter.resume() }
        for waiter in pending { waiter.resume() }
    }
}

/// A task delegate on the same owner session: budget and failure stop the read,
/// but its continuation settles only in didComplete, never at cancel().
private final class RuntimeHTTPImageRead: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private static let maximumBytes = 4 * 1_024 * 1_024
    private let lock = NSLock()
    private let requestID: String
    private let provider: RuntimeModule
    private let state: RuntimeHTTPState
    private var task: URLSessionDataTask?
    private var continuation: CheckedContinuation<Data, Error>?
    private var response: HTTPURLResponse?
    private var bytes = Data()
    private var failure: Error?
    private var cancelled = false
    init(requestID: String, provider: RuntimeModule, state: RuntimeHTTPState) {
        self.requestID = requestID; self.provider = provider; self.state = state
    }
    func install(_ task: URLSessionDataTask, continuation: CheckedContinuation<Data, Error>) {
        lock.lock()
        self.task = task; self.continuation = continuation
        let cancelled = cancelled
        lock.unlock()
        if cancelled { task.cancel() }
    }
    func cancel() {
        lock.lock(); cancelled = true; let task = task; lock.unlock()
        task?.cancel()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        if let response = response as? HTTPURLResponse,
           response.value(forHTTPHeaderField: "X-Meow-Request-Id") == requestID {
            self.response = response
            let type = response.mimeType?.lowercased() ?? ""
            if response.expectedContentLength > Self.maximumBytes {
                failure = URLError(.dataLengthExceedsMaximum)
            } else if response.statusCode == 200 && type.hasPrefix("image/") {
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
        if data.count > Self.maximumBytes - bytes.count {
            failure = URLError(.dataLengthExceedsMaximum)
            lock.unlock(); dataTask.cancel(); return
        }
        bytes.append(data)
        lock.unlock()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        guard let continuation else { lock.unlock(); return }
        self.continuation = nil; self.task = nil
        let result: Result<Data, Error>
        if state.isClosed { result = .failure(RuntimeHTTPError.closed) }
        else if cancelled { result = .failure(RuntimeHTTPError.cancelled) }
        else if let failure { result = .failure(failure) }
        else if let error {
            result = .failure((error as NSError).code == NSURLErrorTimedOut
                ? RuntimeHTTPError.timeout : RuntimeHTTPError.invalidResponse)
        } else if let response {
            result = Result {
                guard !bytes.isEmpty, response.expectedContentLength < 0 ||
                    response.expectedContentLength == bytes.count else { throw RuntimeHTTPError.invalidResponse }
                if response.statusCode != 200 {
                    _ = try RuntimeHTTPClient.value(data: bytes, response: response, requestID: requestID,
                                                    module: provider, imageFailure: true)
                    throw RuntimeHTTPError.invalidResponse
                }
                return bytes
            }
        } else { result = .failure(RuntimeHTTPError.invalidResponse) }
        bytes.removeAll()
        lock.unlock()
        continuation.resume(with: result)
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
    init(state: RuntimeHTTPState) { self.state = state }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) { state.didInvalidate() }
}

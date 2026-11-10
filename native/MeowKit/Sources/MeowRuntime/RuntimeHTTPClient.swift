import Foundation

public enum RuntimeHTTPError: Error, Sendable, Equatable {
    case cancelled, timeout, closed, invalidRequest, invalidResponse, remote(String)
}

/// One trusted loopback session. Business modules borrow this client; only its
/// App owner closes it. URLSession owns the actual HTTP tasks and their drain.
public final class RuntimeHTTPClient: @unchecked Sendable {
    private static let protocolName = "runtime.http.v1"
    private static let neteaseTokenTimeout: TimeInterval = 60
    private let baseURL: URL
    private let sessionID: String
    private let requestKey: String
    private let timeout: TimeInterval
    private let state = RuntimeHTTPState()
    private let session: URLSession

    public convenience init(baseURL: URL, sessionID: String, requestKey: String,
                            timeout: TimeInterval = 30) throws {
        try self.init(baseURL: baseURL, sessionID: sessionID, requestKey: requestKey,
                      timeout: timeout, configuration: .ephemeral)
    }

    init(baseURL: URL, sessionID: String, requestKey: String, timeout: TimeInterval = 30,
         configuration: URLSessionConfiguration) throws {
        guard let components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false),
              components.scheme == "http", components.host == "127.0.0.1",
              let port = components.port, (1...65535).contains(port),
              components.user == nil, components.password == nil,
              components.path.isEmpty || components.path == "/",
              components.query == nil, components.fragment == nil,
              Self.validUUID(sessionID),
              requestKey.utf8.count == 64,
              requestKey.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
              timeout.isFinite, timeout > 0 else { throw RuntimeHTTPError.invalidRequest }
        self.baseURL = baseURL
        self.sessionID = sessionID
        self.requestKey = requestKey
        self.timeout = timeout
        configuration.httpShouldSetCookies = false
        configuration.httpCookieStorage = nil
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = max(timeout, Self.neteaseTokenTimeout)
        session = URLSession(configuration: configuration, delegate: RuntimeHTTPDelegate(state: state),
                             delegateQueue: nil)
    }

    public var isClosed: Bool { state.isClosed }

    public func describe() async throws -> RuntimeValue {
        try await request(method: "GET", path: "/v1/describe")
    }

    /// Validate the actual App assembly, not the generic runtime's default.
    public func validateAppModules() async throws {
        guard case .object(let description) = try await describe(),
              description["protocol"] == .string(Self.protocolName),
              description["product"] == .string("aggregate"),
              case .array(let modules)? = description["modules"], modules.count == 5 else {
            throw RuntimeHTTPError.invalidResponse
        }
        let expected: [(RuntimeModule, String)] = [(.music, "0.8.0"), (.cat, "0.2.0"),
                                                   (.source, "0.2.0"), (.tmdb, "0.1.0"), (.netease, "0.1.0")]
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
            if module == .netease {
                guard case .object(let fields) = matches[0],
                      fields["methods"] == .array([.string("netease.token.get")]) else {
                    throw RuntimeHTTPError.invalidResponse
                }
            }
        }
    }

    /// The relative reference is both the public image identity and cache key.
    /// Never accept a URL capable of replacing the trusted session endpoint.
    public static func tmdbImageURL(_ path: String) -> URL? {
        guard path.range(of: #"^/v1/tmdb/images/(original|[wh][1-9][0-9]*)/[A-Za-z0-9_-]+\.[A-Za-z0-9]+$"#,
                         options: .regularExpression)?.upperBound == path.endIndex else { return nil }
        return URL(string: path)
    }

    public func readTMDBImage(_ reference: URL) async throws -> Data {
        guard reference.baseURL == nil, reference.scheme == nil,
              Self.tmdbImageURL(reference.absoluteString) == reference else { throw RuntimeHTTPError.invalidRequest }
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        let requestID = UUID().uuidString.lowercased()
        var request = URLRequest(url: URL(string: reference.absoluteString, relativeTo: baseURL)!.absoluteURL,
                                 cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpShouldHandleCookies = false
        request.setValue("Bearer \(requestKey)", forHTTPHeaderField: "Authorization")
        request.setValue(sessionID, forHTTPHeaderField: "X-Meow-Session")
        request.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        request.setValue("image/*", forHTTPHeaderField: "Accept")
        let read = RuntimeHTTPImageRead(requestID: requestID, state: state)
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
        guard operation.range(of: #"^[a-z]+(?:\.[a-z]+)?$"#, options: .regularExpression)?.upperBound == operation.endIndex else {
            throw RuntimeHTTPError.invalidRequest
        }
        if module == .netease {
            guard operation == "get", parameters == .object([:]) else { throw RuntimeHTTPError.invalidRequest }
        }
        let body: Data
        do { body = try RuntimeJSON.encode(parameters) }
        catch { throw RuntimeHTTPError.invalidRequest }
        return try await request(method: "POST", path: "/v1/\(module.rawValue)/\(operation)",
                                 body: body, module: module,
                                 timeout: module == .netease ? max(timeout, Self.neteaseTokenTimeout) : timeout)
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
                         module: RuntimeModule? = nil, timeout: TimeInterval? = nil) async throws -> RuntimeValue {
        guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
        guard !isClosed else { throw RuntimeHTTPError.closed }
        guard let url = URL(string: path, relativeTo: baseURL)?.absoluteURL else {
            throw RuntimeHTTPError.invalidRequest
        }
        let requestID = UUID().uuidString.lowercased()
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout ?? self.timeout)
        request.httpMethod = method
        request.httpBody = body
        request.httpShouldHandleCookies = false
        request.setValue("Bearer \(requestKey)", forHTTPHeaderField: "Authorization")
        request.setValue(sessionID, forHTTPHeaderField: "X-Meow-Session")
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

    private static func validUUID(_ value: String) -> Bool {
        value.utf8.count == 36 && UUID(uuidString: value) != nil
    }

    deinit { session.invalidateAndCancel() }
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
        lock.lock(); defer { lock.unlock() }
        guard !closed else { return false }
        closed = true
        return true
    }
    func waitForInvalidation() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if invalidated { lock.unlock(); continuation.resume(); return }
            waiters.append(continuation)
            lock.unlock()
        }
    }
    func didInvalidate() {
        lock.lock()
        closed = true; invalidated = true
        let pending = waiters
        waiters.removeAll()
        lock.unlock()
        for waiter in pending { waiter.resume() }
    }
}

/// A task delegate on the same owner session: budget and failure stop the read,
/// but its continuation settles only in didComplete, never at cancel().
private final class RuntimeHTTPImageRead: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private static let maximumBytes = 4 * 1_024 * 1_024
    private let lock = NSLock()
    private let requestID: String
    private let state: RuntimeHTTPState
    private var task: URLSessionDataTask?
    private var continuation: CheckedContinuation<Data, Error>?
    private var response: HTTPURLResponse?
    private var bytes = Data()
    private var failure: Error?
    private var cancelled = false
    init(requestID: String, state: RuntimeHTTPState) { self.requestID = requestID; self.state = state }
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
                                                    module: .tmdb, imageFailure: true)
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

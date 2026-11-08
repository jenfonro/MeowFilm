import Foundation

public enum RuntimeHTTPError: Error, Sendable, Equatable {
    case cancelled, timeout, closed, invalidRequest, invalidResponse, remote(String)
}

/// One trusted loopback session. Business modules borrow this client; only its
/// App owner closes it. URLSession owns the actual HTTP tasks and their drain.
public final class RuntimeHTTPClient: @unchecked Sendable {
    private static let protocolName = "runtime.http.v1"
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
        configuration.timeoutIntervalForResource = timeout
        session = URLSession(configuration: configuration, delegate: RuntimeHTTPDelegate(state: state),
                             delegateQueue: nil)
    }

    public var isClosed: Bool { state.isClosed }

    public func describe() async throws -> RuntimeValue {
        try await request(method: "GET", path: "/v1/describe")
    }

    public func call(module: RuntimeModule, operation: String,
                     parameters: RuntimeValue = .object([:])) async throws -> RuntimeValue {
        guard operation.range(of: #"^[a-z]+(?:\.[a-z]+)?$"#, options: .regularExpression)?.upperBound == operation.endIndex else {
            throw RuntimeHTTPError.invalidRequest
        }
        let body: Data
        do { body = try RuntimeJSON.encode(parameters) }
        catch { throw RuntimeHTTPError.invalidRequest }
        return try await request(method: "POST", path: "/v1/\(module.rawValue)/\(operation)",
                                 body: body, module: module)
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

    private static func value(data: Data, response: HTTPURLResponse, requestID: String,
                              module: RuntimeModule?) throws -> RuntimeValue {
        guard response.value(forHTTPHeaderField: "X-Meow-Request-Id") == requestID,
              response.value(forHTTPHeaderField: "Content-Type") == "application/json",
              [200, 400, 401, 403, 404, 413, 415].contains(response.statusCode),
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
    func start(continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>, body: () -> Void) {
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

private final class RuntimeHTTPDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let state: RuntimeHTTPState
    init(state: RuntimeHTTPState) { self.state = state }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) { state.didInvalidate() }
}

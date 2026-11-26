import Foundation
import MeowDebug
import MeowRuntime

/// Borrows the App's existing runtime. Account publication follows the native
/// durable receipt; no credential or song store is owned by this adapter.
public actor MusicPlatformClient {
    private let connect: @Sendable () async throws -> RuntimeHTTPClient
    private var connection: RuntimeHTTPClient?
    private var connecting: Task<RuntimeHTTPClient, Error>?
    private var prepareSession: (@Sendable (String) async throws -> Void)?
    private struct Publication {
        let task: Task<Void, Error>
        let authorization: MusicQueueAuthorization
    }
    private var publications: [String: Publication] = [:]
    private var publicationOrder: [String: UUID] = [:]
    private var sessions: [String: RuntimeValue] = [:]
    private var work: [UUID: (task: Task<RuntimeValue, Error>, writing: Bool)] = [:]
    private var closed = false
    private var shuttingDown = false
    private var closing: Task<Void, Error>?
    private static let writes: Set<String> = [
        "dailyStyle.save", "sync.write", "feedback.send", "feedback.upload", "vip.checkIn", "sms.send"
    ]

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        connect = connection
    }
    private func runtime() async throws -> RuntimeHTTPClient {
        guard !closed else { throw MusicServiceError.closed }
        if let connection { return connection }
        if let connecting {
            let value = try await connecting.value
            guard !closed else { throw MusicServiceError.closed }
            return value
        }
        let task = Task { try await connect() }
        connecting = task
        defer { connecting = nil }
        let value = try await task.value
        guard !closed else { throw MusicServiceError.closed }
        connection = value; return value
    }
    public func bindSessions(_ prepare: @escaping @Sendable (String) async throws -> Void) {
        prepareSession = prepare
    }
    /// Local metadata reuse is scoped to the same published account session as reads.
    public func contentRevision(platform: String) async throws -> UUID? {
        guard !closed, !shuttingDown else { throw MusicServiceError.closed }
        try await prepareSession?(platform)
        let revision = publicationOrder[platform]
        if let publication = publications[platform] { try await publication.task.value }
        guard !closed, !shuttingDown, revision == publicationOrder[platform] else { throw MusicServiceError.staleResult }
        try Task.checkCancellation()
        return revision
    }

    public func publish(platform: String, session: RuntimeValue?,
                        authorize: (@Sendable () throws -> Void)? = nil) async throws {
        if session == nil, closed || connection?.isClosed == true {
            sessions.removeValue(forKey: platform)
            return
        }
        guard !closed, !shuttingDown || session == nil else { throw MusicServiceError.closed }
        try authorize?()
        let order = UUID(), previous = publications[platform]
        previous?.authorization.revoke()
        let authorization = MusicQueueAuthorization()
        publicationOrder[platform] = order
        let task = Task { [self] in
            if let previous { _ = await previous.task.result }
            try authorization.check()
            // There is no remote session to retire before this borrower has
            // ever connected. Closing an unused owner must not launch Node.
            if session == nil, connection == nil, connecting == nil {
                sessions.removeValue(forKey: platform)
                return
            }
            let runtime = try await runtime()
            let receipt = try await runtime.call(module: .musicPlatform, operation: "session.update", parameters: .object([
                "platform": .string(platform), "session": session ?? .null
            ]), onDispatch: { try authorization.perform { try authorize?() } })
            guard receipt[music: "published"].musicBool else {
                throw MusicServiceError.upstreamFailed
            }
            try authorization.check()
            guard publicationOrder[platform] == order, !closed else { throw MusicServiceError.staleResult }
            sessions[platform] = session
        }
        publications[platform] = .init(task: task, authorization: authorization)
        do { try await task.value }
        catch { throw Self.error(error) }
    }

    public func request(platform: String, operation: String, parameters: RuntimeObject = [:],
                        candidate: RuntimeValue? = nil,
                        authorize: (@Sendable () throws -> Void)? = nil) async throws -> RuntimeValue {
        let writing = Self.writes.contains(operation), dispatch = MusicPlatformDispatch()
        let debugEnabled = DebugRuntime.isEnabled
        let started = debugEnabled ? DispatchTime.now().uptimeNanoseconds : 0
        do {
            guard !closed, !shuttingDown else { throw MusicServiceError.closed }
            try Task.checkCancellation()
            if candidate == nil { try await prepareSession?(platform) }
            guard !closed, !shuttingDown else { throw MusicServiceError.closed }
            let order = publicationOrder[platform]
            if let publication = publications[platform], candidate == nil { try await publication.task.value }
            let runtime = try await runtime()
            guard !closed, !shuttingDown else { throw MusicServiceError.closed }
            guard candidate != nil || order == publicationOrder[platform] else {
                throw MusicServiceError.staleResult
            }
            try Task.checkCancellation()
            var fields: RuntimeObject = [
                "platform": .string(platform), "operation": .string(operation), "params": .object(parameters)
            ]
            if let candidate { fields["candidate"] = candidate }
            else if let session = sessions[platform] {
                fields["expectedSession"] = .object([
                    "scope": session[music: "scope"], "generation": session[music: "generation"]
                ])
            } else { fields["expectedSession"] = .null }
            let id = UUID(), payload = RuntimeValue.object(fields)
            // This operation waits for the existing token owner's 45s worker.
            // It is not an extension of ordinary platform-request budgets.
            let timeout: TimeInterval? = platform == "wy" && operation == "sync.prepare" ? 60 : nil
            let task = Task { try await runtime.call(module: .musicPlatform, operation: "request", parameters: payload,
                timeout: timeout, onDispatch: { try authorize?(); try dispatch.begin() }) }
            work[id] = (task, writing)
            defer { work.removeValue(forKey: id) }
            let value = try await withTaskCancellationHandler {
                try await task.value
            } onCancel: {
                if !writing || dispatch.stopBeforeDispatch() { task.cancel() }
            }
            // A dispatched write receipt must survive page/account cancellation.
            if !writing {
                try Task.checkCancellation()
                guard !closed, candidate != nil || order == publicationOrder[platform] else {
                    throw MusicServiceError.staleResult
                }
            }
            if debugEnabled {
                let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.log(.musicPlatform, sub: "\(platform).\(operation)", event: "request.ok",
                                 level: .debug, durationMs: ms, data: .object([
                                    "platform": .string(platform),
                                    "operation": .string(operation),
                                    "writing": .bool(writing)
                                 ]))
            }
            return value
        } catch {
            if debugEnabled {
                let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
                DebugRuntime.logError(.musicPlatform, sub: "\(platform).\(operation)", event: "request.error",
                                      error: error, durationMs: ms, data: .object([
                                        "platform": .string(platform),
                                        "operation": .string(operation)
                                      ]))
            }
            if writing && operation != "sms.send" {
                // Node returns dispatched outcomes in a receipt. A valid remote
                // error envelope rejects the operation before that boundary.
                if dispatch.hasStarted, let remote = error as? RuntimeHTTPError,
                   case .remote(let code) = remote {
                    return .object(["status": .string("notSent"), "errorCode": .string(code)])
                }
                let status = dispatch.hasStarted ? "unknown" : "notSent"
                return .object(["status": .string(status), "errorCode": .string(
                    dispatch.hasStarted ? "UPSTREAM_HTTP" : Self.error(error).rawValue)])
            }
            if let remote = error as? RuntimeHTTPError, case .remote(let code) = remote {
                if code == "INVALID_CODE" {
                    throw MusicSMSFailure(reason: .requestRejected, detail: .invalidCode)
                }
                if code == "INVALID_PHONE" {
                    throw MusicSMSFailure(reason: .invalidRequest, detail: .invalidPhone)
                }
                if code == "ACCOUNT_SELECTION_REQUIRED" {
                    throw MusicSMSFailure(reason: .verificationRequired, detail: .accountSelectionRequired)
                }
                if code == "SMS_SEND_UNCONFIRMED" {
                    throw MusicSMSFailure(reason: .upstreamFailed, retryAt: Date().addingTimeInterval(60))
                }
            } else if operation == "sms.send", dispatch.hasStarted {
                throw MusicSMSFailure(reason: .upstreamFailed, retryAt: Date().addingTimeInterval(60))
            }
            throw Self.error(error)
        }
    }
    public func clear(platform: String? = nil) async throws {
        guard !closed, !shuttingDown else { throw MusicServiceError.closed }
        try Task.checkCancellation()
        let runtime = try await runtime()
        guard !closed, !shuttingDown else { throw MusicServiceError.closed }
        try Task.checkCancellation()
        var parameters: RuntimeObject = [:]
        if let platform { parameters["platform"] = .string(platform) }
        let id = UUID(), payload = RuntimeValue.object(parameters)
        let task = Task { try await runtime.call(module: .musicPlatform, operation: "cache.clear", parameters: payload) }
        work[id] = (task, false)
        defer { work.removeValue(forKey: id) }
        do {
            _ = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        } catch { throw Self.error(error) }
    }
    public func revoke() async throws {
        var failure: Error?
        for platform in ["wy", "tx", "kg", "kw", "mg"] {
            do { try await publish(platform: platform, session: nil) }
            catch {
                // The App owns executor exit. A closed transport cannot retain
                // a usable published session in this borrower.
                if connection?.isClosed != true, failure == nil { failure = error }
            }
        }
        if let failure, connection?.isClosed != true { throw failure }
    }
    public func drain() async throws {
        var failure: Error?
        do { try await drainPublications() }
        catch { failure = error }
        for entry in Array(work.values) { _ = await entry.task.result }
        if let failure { throw Self.error(failure) }
    }
    // Account close drains publication, but reports its own retained business
    // tasks as pending. The App's client close still waits for every request.
    func drainPublications() async throws {
        var failure: Error?
        for publication in Array(publications.values) {
            do { try await publication.task.value }
            catch { if failure == nil { failure = error } }
        }
        if let failure, connection?.isClosed != true { throw failure }
    }
    public func close() async throws {
        if let closing { return try await closing.value }
        shuttingDown = true
        let running = Array(work.values)
        for entry in running { entry.task.cancel() }
        for publication in publications.values {
            publication.authorization.revoke()
            publication.task.cancel()
        }
        connecting?.cancel()
        let task = Task { [self] in
            let result: Result<Void, Error>
            do { try await revoke(); result = .success(()) }
            catch { result = .failure(error) }
            closed = true
            prepareSession = nil
            for entry in running { _ = await entry.task.result }
            for publication in publications.values { _ = await publication.task.result }
            sessions.removeAll(); publications.removeAll()
            try result.get()
        }
        closing = task
        try await task.value
    }
    public static func error(_ error: Error) -> MusicServiceError {
        if let value = error as? MusicServiceError { return value }
        if error is CancellationError { return .cancelled }
        if let value = error as? RuntimeHTTPError {
            switch value {
            case .cancelled: return .cancelled
            case .closed: return .closed
            case .timeout: return .timeout
            case .invalidRequest: return .invalidRequest
            case .invalidResponse: return .invalidResponse
            case .remote(let code):
                switch code {
                case "CANCELLED": return .cancelled
                case "CLOSED": return .closed
                case "TIMEOUT": return .timeout
                case "AUTH_REQUIRED", "INVALID_CREDENTIALS": return .authRequired
                case "AUTH_EXPIRED": return .sessionExpired
                case "STALE_SESSION": return .staleResult
                case "INVALID_REQUEST": return .invalidRequest
                case "INVALID_RESPONSE", "REDIRECT_DENIED": return .upstreamInvalid
                case "NETWORK_ERROR": return .connectionFailed
                case "UPSTREAM_HTTP": return .upstreamFailed
                case "UPSTREAM_ERROR": return .requestRejected
                case "RATE_LIMITED": return .rateLimited
                case "MEDIA_UNAVAILABLE": return .audioUnavailable
                case "QUALITY_UNAVAILABLE": return .qualityDisabled
                case "FORBIDDEN": return .permissionDenied
                case "TRIAL_ONLY": return .previewOnly
                case "STORAGE_ERROR": return .storageFailed
                case "STORAGE_CONFLICT": return .storageConflict
                case "VERIFICATION_REQUIRED", "SIGNATURE_REQUIRED": return .verificationRequired
                case "NOT_FOUND": return .notFound
                case "LIMIT": return .limit
                case "UNSUPPORTED": return .unsupported
                default: return MusicServiceError(rawValue: code) ?? .upstreamFailed
                }
            }
        }
        return .upstreamFailed
    }
}

/// Linearizes cancellation with the actual runtime HTTP dispatch. A write
/// which crossed this boundary waits for its receipt rather than being replayed.
private final class MusicPlatformDispatch: @unchecked Sendable {
    private let lock = NSLock()
    private var started = false, stopped = false
    func begin() throws {
        lock.lock(); defer { lock.unlock() }
        guard !stopped, !Task.isCancelled else { throw MusicServiceError.cancelled }
        started = true
    }
    func stopBeforeDispatch() -> Bool {
        lock.lock(); defer { lock.unlock() }
        stopped = true
        return !started
    }
    var hasStarted: Bool { lock.lock(); defer { lock.unlock() }; return started }
}

public extension RuntimeValue {
    subscript(music key: String) -> RuntimeValue {
        if case .object(let fields) = self { return fields[key] ?? .null }
        return .null
    }
    var musicText: String { if case .string(let value) = self { return value }; return "" }
    var musicNumber: Double? { if case .number(let value) = self { return value }; return nil }
    var musicBool: Bool { if case .bool(let value) = self { return value }; return false }
    var musicArray: [RuntimeValue] { if case .array(let values) = self { return values }; return [] }
    func musicRequiredText() throws -> String {
        guard case .string(let value) = self, !value.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return value
    }
    func musicRequiredArray() throws -> [RuntimeValue] {
        guard case .array(let values) = self else { throw MusicServiceError.upstreamInvalid }
        return values
    }
    func musicData() throws -> Data { try RuntimeJSON.encode(self) }
}

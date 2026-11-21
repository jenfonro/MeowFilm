import Foundation
import Darwin

public struct DebugErrorPayload: Sendable, Equatable {
    public let code: String?
    public let type: String
    public let message: String
    public let stack: String?

    public init(code: String? = nil, type: String, message: String, stack: String? = nil) {
        self.code = code
        self.type = type
        self.message = message
        self.stack = stack
    }

    public init(error: Error, code: String? = nil, captureStack: Bool = false) {
        let nsError = error as NSError
        self.code = code ?? (nsError.domain == NSCocoaErrorDomain ? "\(nsError.domain):\(nsError.code)" : String(describing: error))
        self.type = String(describing: Swift.type(of: error))
        self.message = error.localizedDescription
        self.stack = captureStack ? Thread.callStackSymbols.joined(separator: "\n") : nil
    }

    public func toJSON() -> DebugJSONValue {
        var fields: [String: DebugJSONValue] = [
            "type": .string(type),
            "message": .string(message)
        ]
        if let code { fields["code"] = .string(code) }
        if let stack { fields["stack"] = .string(stack) }
        return .object(fields)
    }
}

public struct DebugLogEventRecord: Sendable {
    public let seq: Int64
    public let ts: String
    public let monoMs: Double
    public let proc: String
    public let level: DebugLogLevel
    public let cat: DebugLogCategory
    public let sub: String
    public let event: String
    public let opId: String?
    public let traceId: String?
    public let durationMs: Double?
    public let data: DebugJSONValue?
    public let error: DebugJSONValue?
    public let json: DebugJSONValue
}

public final class DebugLogger: @unchecked Sendable {
    public let session: DebugSessionStore
    public let blobStore: DebugBlobStore
    public let exceptionBuffer: DebugExceptionBuffer

    private let writeQueue = DispatchQueue(label: "dev.meowfilm.debug.logger", qos: .utility)
    private let notifyQueue = DispatchQueue(label: "dev.meowfilm.debug.logger.notify", qos: .utility)
    private let writeQueueKey = DispatchSpecificKey<Bool>()
    private let stateLock = NSLock()
    private var seq: Int64 = 0
    private var fileDescriptors: [DebugLogCategory: Int32] = [:]
    private var listeners: [UUID: @Sendable (DebugLogEventRecord) -> Void] = [:]

    public init(session: DebugSessionStore, exceptionBuffer: DebugExceptionBuffer) {
        self.session = session
        self.blobStore = DebugBlobStore(sessionDirectory: session.sessionDir)
        self.exceptionBuffer = exceptionBuffer
        self.writeQueue.setSpecific(key: writeQueueKey, value: true)
    }

    deinit {
        for fd in fileDescriptors.values where fd >= 0 {
            _ = Darwin.close(fd)
        }
        fileDescriptors.removeAll()
    }

    public func subscribe(_ handler: @escaping @Sendable (DebugLogEventRecord) -> Void) -> UUID {
        let id = UUID()
        stateLock.lock()
        listeners[id] = handler
        stateLock.unlock()
        return id
    }

    public func unsubscribe(_ id: UUID) {
        stateLock.lock()
        listeners.removeValue(forKey: id)
        stateLock.unlock()
    }

    public func log(
        cat: DebugLogCategory,
        sub: String,
        event: String,
        level: DebugLogLevel = .info,
        opId: String? = nil,
        traceId: String? = nil,
        durationMs: Double? = nil,
        data: DebugJSONValue? = nil,
        error: DebugErrorPayload? = nil,
        recordException: Bool = true
    ) {
        let options = session.currentOptions
        guard options.accepts(cat) else { return }

        let resolvedTrace = traceId ?? DebugTraceContext.current.traceId
        let resolvedOp = opId ?? DebugTraceContext.current.opId
        let now = Date()
        let ts = DebugTimestamp.iso8601UTC(now)
        let monoMs = session.uptimeMs

        if recordException, let error, (level == .error || level == .fatal || options.firstChanceExceptions) {
            exceptionBuffer.record(
                kind: level == .fatal ? "unhandled" : "first_chance",
                type: error.type,
                message: error.message,
                stack: error.stack ?? Thread.callStackSymbols.joined(separator: "\n"),
                opId: resolvedOp,
                traceId: resolvedTrace
            )
        }

        stateLock.lock()
        seq += 1
        let currentSeq = seq
        stateLock.unlock()

        writeQueue.async { [self] in
            let foldedData = data.map {
                blobStore.foldData($0, inlineLimit: options.inlineLimit, eventBytesLimit: options.eventBytesLimit)
            }
            let errorValue: DebugJSONValue = error?.toJSON() ?? .null

            var fields: [String: DebugJSONValue] = [
                "seq": .integer(currentSeq),
                "ts": .string(ts),
                "monoMs": .number((monoMs * 1000).rounded() / 1000),
                "proc": .string("app"),
                "level": .string(level.rawValue),
                "cat": .string(cat.rawValue),
                "sub": .string(sub),
                "event": .string(event),
                "error": errorValue
            ]
            if let resolvedOp { fields["opId"] = .string(resolvedOp) }
            if let resolvedTrace { fields["traceId"] = .string(resolvedTrace) }
            if let durationMs, durationMs.isFinite {
                fields["durationMs"] = .number((durationMs * 1000).rounded() / 1000)
            }
            if let foldedData { fields["data"] = foldedData }

            let payload = DebugJSONValue.object(fields)
            guard var bytes = try? payload.encode() else { return }
            bytes.append(0x0A)
            appendLineLocked(bytes, category: cat)

            stateLock.lock()
            let currentListeners = Array(listeners.values)
            stateLock.unlock()
            if !currentListeners.isEmpty {
                let record = DebugLogEventRecord(
                    seq: currentSeq,
                    ts: ts,
                    monoMs: monoMs,
                    proc: "app",
                    level: level,
                    cat: cat,
                    sub: sub,
                    event: event,
                    opId: resolvedOp,
                    traceId: resolvedTrace,
                    durationMs: durationMs,
                    data: foldedData,
                    error: error?.toJSON(),
                    json: payload
                )
                notifyQueue.async {
                    for listener in currentListeners { listener(record) }
                }
            }
        }
    }

    public func flush() {
        if DispatchQueue.getSpecific(key: writeQueueKey) == true { return }
        writeQueue.sync {}
    }

    public func closeAll() {
        let closeFDs = {
            for fd in self.fileDescriptors.values where fd >= 0 {
                _ = Darwin.close(fd)
            }
            self.fileDescriptors.removeAll()
        }
        if DispatchQueue.getSpecific(key: writeQueueKey) == true {
            closeFDs()
        } else {
            writeQueue.sync(execute: closeFDs)
        }
    }

    private func appendLineLocked(_ bytes: Data, category: DebugLogCategory) {
        let fd: Int32
        if let existing = fileDescriptors[category], existing >= 0 {
            fd = existing
        } else {
            try? FileManager.default.createDirectory(at: session.logsDir, withIntermediateDirectories: true)
            let fileURL = session.logsDir.appendingPathComponent("\(category.rawValue).jsonl", isDirectory: false)
            let opened = fileURL.path.withCString { path in
                Darwin.open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0o600)
            }
            guard opened >= 0 else { return }
            fileDescriptors[category] = opened
            fd = opened
        }

        // Single-buffer POSIX O_APPEND write is vnode-atomic on Darwin APFS,
        // keeping same-process Swift and NodeMobile JSONL appends intact.
        bytes.withUnsafeBytes { raw in
            guard let base = raw.baseAddress else { return }
            var written = 0
            while written < raw.count {
                let n = Darwin.write(fd, base.advanced(by: written), raw.count - written)
                if n < 0 {
                    if errno == EINTR { continue }
                    break
                }
                if n == 0 { break }
                written += n
            }
        }
    }

    public func categoriesSummary(in sessionDirectory: URL? = nil) -> [DebugJSONValue] {
        let targetDir = sessionDirectory ?? session.sessionDir
        if targetDir.standardizedFileURL == session.sessionDir.standardizedFileURL {
            flush()
        }
        let logsDir = targetDir.appendingPathComponent("logs", isDirectory: true)
        var results: [DebugJSONValue] = []
        let fm = FileManager.default
        for cat in DebugLogCategory.allCases {
            let fileURL = logsDir.appendingPathComponent("\(cat.rawValue).jsonl", isDirectory: false)
            guard fm.fileExists(atPath: fileURL.path) else {
                continue
            }
            let attrs = try? fm.attributesOfItem(atPath: fileURL.path)
            let size = (attrs?[.size] as? NSNumber)?.uint64Value ?? 0
            var lines = 0
            var latestSeq: Int64?
            var levels: [String: Int] = [:]
            if let text = try? String(contentsOf: fileURL, encoding: .utf8) {
                for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
                    lines += 1
                    guard let data = line.data(using: .utf8),
                          let obj = try? DebugJSONValue.decode(data: data) else { continue }
                    if let level = obj["level"]?.stringValue {
                        levels[level, default: 0] += 1
                    }
                    if case .integer(let s)? = obj["seq"] {
                        latestSeq = max(latestSeq ?? s, s)
                    }
                }
            }
            let levelMap = levels.mapValues { DebugJSONValue.int($0) }
            results.append(.object([
                "cat": .string(cat.rawValue),
                "exists": .bool(true),
                "sizeBytes": .uint64(size),
                "lines": .int(lines),
                "latestSeq": latestSeq.map(DebugJSONValue.integer) ?? .null,
                "levels": .object(levelMap)
            ]))
        }
        return results
    }

    public struct TailQuery: Sendable {
        public let cat: String
        public let limit: Int
        public let sinceSeq: Int64?
        public let level: String?
        public let sub: String?
        public let event: String?
        public let opId: String?
        public let traceId: String?
        public let q: String?

        public init(
            cat: String,
            limit: Int = 100,
            sinceSeq: Int64? = nil,
            level: String? = nil,
            sub: String? = nil,
            event: String? = nil,
            opId: String? = nil,
            traceId: String? = nil,
            q: String? = nil
        ) {
            self.cat = cat
            self.limit = min(max(limit, 1), 2000)
            self.sinceSeq = sinceSeq
            self.level = level
            self.sub = sub
            self.event = event
            self.opId = opId
            self.traceId = traceId
            self.q = q
        }
    }

    public func tail(_ query: TailQuery, in sessionDirectory: URL? = nil) throws -> [DebugJSONValue] {
        let targetDir = sessionDirectory ?? session.sessionDir
        if targetDir.standardizedFileURL == session.sessionDir.standardizedFileURL {
            flush()
        }
        let logsDir = targetDir.appendingPathComponent("logs", isDirectory: true)
        let targets: [DebugLogCategory]
        if query.cat == "all" {
            targets = DebugLogCategory.allCases
        } else if let specific = DebugLogCategory(rawValue: query.cat) {
            targets = [specific]
        } else {
            throw DebugAPIError.invalidArgument("Unknown log category: \(query.cat)")
        }

        let allowedLevels: Set<String>? = query.level.map { raw in
            Set(raw.split(separator: ",").map { $0.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }.filter { !$0.isEmpty })
        }

        var matched: [(ts: String, seq: Int64, value: DebugJSONValue)] = []
        let needle = query.q?.lowercased()
        for cat in targets {
            let fileURL = logsDir.appendingPathComponent("\(cat.rawValue).jsonl", isDirectory: false)
            guard let text = try? String(contentsOf: fileURL, encoding: .utf8) else { continue }
            for rawLine in text.split(separator: "\n", omittingEmptySubsequences: true) {
                let line = String(rawLine)
                if let needle, !line.lowercased().contains(needle) { continue }
                guard let data = line.data(using: .utf8),
                      let obj = try? DebugJSONValue.decode(data: data) else { continue }
                let seqVal: Int64
                if case .integer(let s)? = obj["seq"] { seqVal = s } else { seqVal = 0 }
                if let sinceSeq = query.sinceSeq, seqVal <= sinceSeq { continue }
                if let allowedLevels, !allowedLevels.isEmpty {
                    guard let actual = obj["level"]?.stringValue?.lowercased(), allowedLevels.contains(actual) else { continue }
                }
                if let sub = query.sub, obj["sub"]?.stringValue != sub { continue }
                if let event = query.event, obj["event"]?.stringValue != event { continue }
                if let opId = query.opId, obj["opId"]?.stringValue != opId { continue }
                if let traceId = query.traceId, obj["traceId"]?.stringValue != traceId { continue }
                let tsVal = obj["ts"]?.stringValue ?? ""
                matched.append((tsVal, seqVal, obj))
            }
        }
        if targets.count > 1 {
            matched.sort { lhs, rhs in
                if lhs.ts != rhs.ts { return lhs.ts < rhs.ts }
                return lhs.seq < rhs.seq
            }
        }
        if matched.count > query.limit {
            matched = Array(matched.suffix(query.limit))
        }
        return matched.map(\.value)
    }
}

/// Process-wide zero-overhead debug runtime accessor.
/// Remains `nil` whenever `--debug` is not passed on launch.
public final class DebugRuntime: @unchecked Sendable {
    private static let lock = NSLock()
    private static var storage: DebugRuntime?

    public static var current: DebugRuntime? {
        lock.lock(); defer { lock.unlock() }
        return storage
    }

    public static var isEnabled: Bool { current != nil }

    public static func install(_ runtime: DebugRuntime) {
        lock.lock()
        storage = runtime
        lock.unlock()
    }

    public static func uninstall() {
        lock.lock()
        storage = nil
        lock.unlock()
    }

    public let configuration: DebugLaunchConfiguration
    public let session: DebugSessionStore
    public let logger: DebugLogger
    public let exceptionBuffer: DebugExceptionBuffer
    public let diagnostics: DebugMachDiagnostics

    public init(configuration: DebugLaunchConfiguration, dataRoot: URL, appVersion: String) throws {
        self.configuration = configuration
        self.session = try DebugSessionStore(configuration: configuration, dataRoot: dataRoot, appVersion: appVersion)
        self.exceptionBuffer = DebugExceptionBuffer()
        self.logger = DebugLogger(session: self.session, exceptionBuffer: self.exceptionBuffer)
        self.diagnostics = DebugMachDiagnostics(exceptionBuffer: self.exceptionBuffer)
        self.exceptionBuffer.installUncaughtHandler()
    }

    public static func log(
        _ cat: DebugLogCategory,
        sub: String,
        event: String,
        level: DebugLogLevel = .info,
        opId: String? = nil,
        traceId: String? = nil,
        durationMs: Double? = nil,
        data: DebugJSONValue? = nil,
        error: DebugErrorPayload? = nil,
        recordException: Bool = true
    ) {
        guard let runtime = current else { return }
        runtime.logger.log(
            cat: cat,
            sub: sub,
            event: event,
            level: level,
            opId: opId,
            traceId: traceId,
            durationMs: durationMs,
            data: data,
            error: error,
            recordException: recordException
        )
    }

    public static func logError(
        _ cat: DebugLogCategory,
        sub: String,
        event: String,
        error: Error,
        code: String? = nil,
        opId: String? = nil,
        traceId: String? = nil,
        durationMs: Double? = nil,
        data: DebugJSONValue? = nil,
        level: DebugLogLevel = .error
    ) {
        guard let runtime = current else { return }
        let payload = DebugErrorPayload(error: error, code: code, captureStack: true)
        runtime.logger.log(
            cat: cat,
            sub: sub,
            event: event,
            level: level,
            opId: opId,
            traceId: traceId,
            durationMs: durationMs,
            data: data,
            error: payload
        )
    }
}

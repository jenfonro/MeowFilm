import Foundation
import Darwin
#if os(iOS)
import UIKit
#endif

public struct DebugActiveSessionMetadata: Codable, Sendable {
    public var schemaVersion: Int = 1
    public var status: String
    public var platform: String
    public var appVersion: String
    public var pid: Int
    public var sessionId: String
    public var sessionDir: String
    public var controlUrl: String
    public var bind: String
    public var port: Int
    public var runtimeBaseUrl: String?
    public var runtimePid: Int?
    public var dataRoot: String
    public var startedAt: String
    public var endedAt: String?
    public var options: DebugRuntimeOptions
}

public struct DebugSessionEnvironment: Codable, Sendable {
    public let osVersion: String
    public let arch: String
    public let deviceModel: String
    public let isSimulator: Bool
    public let screenWidth: Double?
    public let screenHeight: Double?
    public let screenScale: Double?
}

public struct DebugExitSummary: Codable, Sendable {
    public let code: Int
    public let clean: Bool
    public let lastError: String?

    public init(code: Int, clean: Bool, lastError: String?) {
        self.code = code
        self.clean = clean
        self.lastError = lastError
    }
}

public struct DebugSessionFileMetadata: Codable, Sendable {
    public var schemaVersion: Int = 1
    public var status: String
    public var platform: String
    public var appVersion: String
    public var pid: Int
    public var sessionId: String
    public var sessionDir: String
    public var controlUrl: String
    public var bind: String
    public var port: Int
    public var runtimeBaseUrl: String?
    public var runtimePid: Int?
    public var dataRoot: String
    public var startedAt: String
    public var endedAt: String?
    public var options: DebugRuntimeOptions
    public var environment: DebugSessionEnvironment
    public var exitSummary: DebugExitSummary?
}

public enum DebugTimestamp {
    public static func iso8601UTC(_ date: Date = Date()) -> String {
        let interval = date.timeIntervalSince1970
        let wholeSeconds = floor(interval)
        var sec = time_t(wholeSeconds)
        let ms = Int(((interval - wholeSeconds) * 1000.0).rounded(.towardZero))
        let clampedMs = min(max(ms, 0), 999)
        var utc = tm()
        gmtime_r(&sec, &utc)
        return String(
            format: "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ",
            Int(utc.tm_year) + 1900,
            Int(utc.tm_mon) + 1,
            Int(utc.tm_mday),
            Int(utc.tm_hour),
            Int(utc.tm_min),
            Int(utc.tm_sec),
            clampedMs
        )
    }

    public static func compactUTC(_ date: Date = Date()) -> String {
        var sec = time_t(floor(date.timeIntervalSince1970))
        var utc = tm()
        gmtime_r(&sec, &utc)
        return String(
            format: "%04d%02d%02dT%02d%02d%02dZ",
            Int(utc.tm_year) + 1900,
            Int(utc.tm_mon) + 1,
            Int(utc.tm_mday),
            Int(utc.tm_hour),
            Int(utc.tm_min),
            Int(utc.tm_sec)
        )
    }
}

public final class DebugSessionStore: @unchecked Sendable {
    public let debugRoot: URL
    public let sessionsRoot: URL
    public let sessionId: String
    public let sessionDir: URL
    public let logsDir: URL
    public let blobsDir: URL
    public let dumpsDir: URL
    public let screenshotsDir: URL
    public let startedAtDate: Date
    public let startedAt: String
    public let startUptimeNs: UInt64
    public let pid: Int
    public let appVersion: String

    private let lock = NSLock()
    private var metadata: DebugSessionFileMetadata
    private var screenshotSeq: Int = 0
    private var dumpSeq: Int = 0

    public init(
        configuration: DebugLaunchConfiguration,
        dataRoot: URL,
        appVersion: String
    ) throws {
        let fm = FileManager.default
        let debugRoot = try (configuration.debugRootOverride ?? DebugLaunchConfiguration.defaultDebugRoot())
            .standardizedFileURL
        try fm.createDirectory(at: debugRoot, withIntermediateDirectories: true)
        Self.excludeFromBackup(debugRoot)

        let sessionsRoot = debugRoot.appendingPathComponent("sessions", isDirectory: true)
        try fm.createDirectory(at: sessionsRoot, withIntermediateDirectories: true)

        let now = Date()
        let pid = Int(getpid())
        let sessionId = "\(DebugTimestamp.compactUTC(now))-\(pid)"
        let sessionDir = sessionsRoot.appendingPathComponent(sessionId, isDirectory: true)
        let logsDir = sessionDir.appendingPathComponent("logs", isDirectory: true)
        let blobsDir = sessionDir.appendingPathComponent("blobs", isDirectory: true)
        let dumpsDir = sessionDir.appendingPathComponent("dumps", isDirectory: true)
        let screenshotsDir = sessionDir.appendingPathComponent("screenshots", isDirectory: true)
        for dir in [sessionDir, logsDir, blobsDir, dumpsDir, screenshotsDir] {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        Self.excludeFromBackup(sessionDir)

        self.debugRoot = debugRoot
        self.sessionsRoot = sessionsRoot
        self.sessionId = sessionId
        self.sessionDir = sessionDir
        self.logsDir = logsDir
        self.blobsDir = blobsDir
        self.dumpsDir = dumpsDir
        self.screenshotsDir = screenshotsDir
        self.startedAtDate = now
        self.startedAt = DebugTimestamp.iso8601UTC(now)
        self.startUptimeNs = DispatchTime.now().uptimeNanoseconds
        self.pid = pid
        self.appVersion = appVersion

        let env = Self.collectEnvironment()
        let initialBind = configuration.bindAddress
        let initialPort = Int(configuration.port)
        let controlUrl = Self.formatControlURL(bind: initialBind, port: initialPort)
        self.metadata = DebugSessionFileMetadata(
            status: "running",
            platform: "ios",
            appVersion: appVersion,
            pid: pid,
            sessionId: sessionId,
            sessionDir: sessionDir.path,
            controlUrl: controlUrl,
            bind: initialBind,
            port: initialPort,
            runtimeBaseUrl: nil,
            runtimePid: nil,
            dataRoot: dataRoot.standardizedFileURL.path,
            startedAt: self.startedAt,
            endedAt: nil,
            options: configuration.options,
            environment: env,
            exitSummary: nil
        )

        pruneOldSessions(keep: configuration.options.keepSessions)
        persistMetadataLocked()
    }

    public var currentMetadata: DebugSessionFileMetadata {
        lock.lock(); defer { lock.unlock() }
        return metadata
    }

    public var currentOptions: DebugRuntimeOptions {
        lock.lock(); defer { lock.unlock() }
        return metadata.options
    }

    public var uptimeMs: Double {
        let elapsed = DispatchTime.now().uptimeNanoseconds - startUptimeNs
        return Double(elapsed) / 1_000_000.0
    }

    public func nodeHandshake() -> DebugNodeHandshake {
        lock.lock(); defer { lock.unlock() }
        return DebugNodeHandshake(
            enabled: true,
            sessionId: sessionId,
            sessionDir: sessionDir.path,
            inlineLimit: metadata.options.inlineLimit,
            eventBytesLimit: metadata.options.eventBytesLimit,
            modules: metadata.options.modules
        )
    }

    public func updateControlEndpoint(bind: String, port: UInt16) {
        lock.lock()
        metadata.bind = bind
        metadata.port = Int(port)
        metadata.controlUrl = Self.formatControlURL(bind: bind, port: Int(port))
        persistMetadataLocked()
        lock.unlock()
    }

    private static func formatControlURL(bind: String, port: Int) -> String {
        let host: String
        switch bind {
        case "0.0.0.0": host = "127.0.0.1"
        case "::": host = "::1"
        default: host = bind
        }
        if host.contains(":") && !host.hasPrefix("[") {
            return "http://[\(host)]:\(port)"
        }
        return "http://\(host):\(port)"
    }

    public func updateRuntimeEndpoint(_ baseURL: URL?) {
        lock.lock()
        metadata.runtimeBaseUrl = baseURL?.absoluteString
        metadata.runtimePid = baseURL == nil ? nil : pid
        persistMetadataLocked()
        lock.unlock()
    }

    public func updateDataRoot(_ dataRoot: URL) {
        lock.lock()
        metadata.dataRoot = dataRoot.standardizedFileURL.path
        persistMetadataLocked()
        lock.unlock()
    }

    public func updateOptions(_ mutate: (inout DebugRuntimeOptions) -> Void) -> DebugRuntimeOptions {
        lock.lock()
        mutate(&metadata.options)
        let updated = metadata.options
        persistMetadataLocked()
        lock.unlock()
        return updated
    }

    public func markExited(code: Int = 0, clean: Bool = true, lastError: String? = nil) {
        lock.lock()
        metadata.status = "exited"
        metadata.endedAt = DebugTimestamp.iso8601UTC()
        metadata.exitSummary = DebugExitSummary(code: code, clean: clean, lastError: lastError)
        persistMetadataLocked()
        lock.unlock()
    }

    public func saveScreenshotPNG(_ data: Data) throws -> (absoluteURL: URL, relativePath: String) {
        lock.lock()
        screenshotSeq += 1
        let seq = screenshotSeq
        lock.unlock()
        let fileName = String(format: "%@-%04d.png", DebugTimestamp.compactUTC(), seq)
        let url = screenshotsDir.appendingPathComponent(fileName, isDirectory: false)
        try Self.atomicWrite(data: data, to: url)
        return (url, "screenshots/\(fileName)")
    }

    public func saveDump(kind: String, ext: String, data: Data) throws -> (absoluteURL: URL, relativePath: String) {
        lock.lock()
        dumpSeq += 1
        let seq = dumpSeq
        lock.unlock()
        let safeKind = kind.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }
        let safeExt = ext.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
        let fileName = String(
            format: "%@-%04d-%@.%@",
            DebugTimestamp.compactUTC(),
            seq,
            safeKind.isEmpty ? "dump" : safeKind,
            safeExt.isEmpty ? "json" : safeExt
        )
        let url = dumpsDir.appendingPathComponent(fileName, isDirectory: false)
        try Self.atomicWrite(data: data, to: url)
        return (url, "dumps/\(fileName)")
    }

    public func resolveSessionDirectory(sessionId requested: String?) -> URL? {
        guard let requested, !requested.isEmpty else { return sessionDir }
        let candidate = sessionsRoot.appendingPathComponent(requested, isDirectory: true).standardizedFileURL
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDir), isDir.boolValue else {
            return nil
        }
        return candidate
    }

    public func resolveSessionFile(sessionId requested: String?, relativePath: String) -> (url: URL, contentType: String)? {
        guard let base = resolveSessionDirectory(sessionId: requested) else { return nil }
        let trimmed = relativePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !trimmed.isEmpty else { return nil }
        let target = base.appendingPathComponent(trimmed, isDirectory: false)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: target.path, isDirectory: &isDir), !isDir.boolValue else {
            return nil
        }
        return (target, Self.contentType(for: target.pathExtension))
    }

    public func listSessions() -> [DebugJSONValue] {
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: sessionsRoot.path) else { return [] }
        let sorted = names.sorted(by: >)
        var result: [DebugJSONValue] = []
        for name in sorted {
            let dir = sessionsRoot.appendingPathComponent(name, isDirectory: true)
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: dir.path, isDirectory: &isDir), isDir.boolValue else { continue }
            let metaURL = dir.appendingPathComponent("session.json")
            let metaJSON = (try? Data(contentsOf: metaURL)).flatMap { try? DebugJSONValue.decode(data: $0) }
            let stats = Self.directoryStats(dir)
            let files = Self.listDownloadableFiles(in: dir)
            result.append(.object([
                "sessionId": .string(name),
                "active": .bool(name == sessionId),
                "status": metaJSON?["status"] ?? .string("unknown"),
                "startedAt": metaJSON?["startedAt"] ?? .null,
                "endedAt": metaJSON?["endedAt"] ?? .null,
                "pid": metaJSON?["pid"] ?? .null,
                "sizeBytes": .uint64(stats.totalBytes),
                "logLines": .int(stats.logLines),
                "errorCount": .int(stats.errorCount),
                "files": .array(files.map(DebugJSONValue.string))
            ]))
        }
        return result
    }

    private static func listDownloadableFiles(in sessionDir: URL) -> [String] {
        let fm = FileManager.default
        var results: [String] = []
        for subdir in ["logs", "dumps", "screenshots"] {
            let dir = sessionDir.appendingPathComponent(subdir, isDirectory: true)
            guard let entries = try? fm.contentsOfDirectory(atPath: dir.path) else { continue }
            for name in entries.sorted() where !name.hasPrefix(".") {
                var isDir: ObjCBool = false
                let full = dir.appendingPathComponent(name)
                if fm.fileExists(atPath: full.path, isDirectory: &isDir), !isDir.boolValue {
                    results.append("\(subdir)/\(name)")
                }
            }
        }
        return results
    }

    private func pruneOldSessions(keep: Int) {
        let fm = FileManager.default
        guard keep >= 1,
              let names = try? fm.contentsOfDirectory(atPath: sessionsRoot.path) else { return }
        let candidates = names.filter { name in
            var isDir: ObjCBool = false
            let url = sessionsRoot.appendingPathComponent(name, isDirectory: true)
            return fm.fileExists(atPath: url.path, isDirectory: &isDir) && isDir.boolValue
        }.sorted(by: >)
        guard candidates.count > keep else { return }
        for old in candidates.dropFirst(keep) where old != sessionId {
            let target = sessionsRoot.appendingPathComponent(old, isDirectory: true)
            try? fm.removeItem(at: target)
        }
    }

    private func persistMetadataLocked() {
        let active = DebugActiveSessionMetadata(
            schemaVersion: metadata.schemaVersion,
            status: metadata.status,
            platform: metadata.platform,
            appVersion: metadata.appVersion,
            pid: metadata.pid,
            sessionId: metadata.sessionId,
            sessionDir: metadata.sessionDir,
            controlUrl: metadata.controlUrl,
            bind: metadata.bind,
            port: metadata.port,
            runtimeBaseUrl: metadata.runtimeBaseUrl,
            runtimePid: metadata.runtimePid,
            dataRoot: metadata.dataRoot,
            startedAt: metadata.startedAt,
            endedAt: metadata.endedAt,
            options: metadata.options
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        if metadata.port > 0, let data = try? encoder.encode(active) {
            try? Self.atomicWrite(data: data, to: debugRoot.appendingPathComponent("active-session.json"))
        }
        if let data = try? encoder.encode(metadata) {
            try? Self.atomicWrite(data: data, to: sessionDir.appendingPathComponent("session.json"))
        }
    }

    public static func atomicWrite(data: Data, to target: URL) throws {
        let dir = target.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let staging = dir.appendingPathComponent(".\(target.lastPathComponent).\(UUID().uuidString).tmp")
        do {
            try data.write(to: staging, options: [.atomic])
            guard Darwin.rename(staging.path, target.path) == 0 else {
                try? FileManager.default.removeItem(at: staging)
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
        } catch {
            try? FileManager.default.removeItem(at: staging)
            throw error
        }
    }

    private static func excludeFromBackup(_ url: URL) {
        var mutable = url
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? mutable.setResourceValues(values)
    }

    private static func collectEnvironment() -> DebugSessionEnvironment {
        var sysinfo = utsname()
        uname(&sysinfo)
        let machine = withUnsafePointer(to: &sysinfo.machine) {
            $0.withMemoryRebound(to: CChar.self, capacity: Int(_SYS_NAMELEN)) {
                String(cString: $0)
            }
        }
        #if arch(arm64)
        let arch = "arm64"
        #elseif arch(x86_64)
        let arch = "x86_64"
        #else
        let arch = "unknown"
        #endif
        #if targetEnvironment(simulator)
        let isSim = true
        #else
        let isSim = false
        #endif
        var width: Double?
        var height: Double?
        var scale: Double?
        #if os(iOS)
        if Thread.isMainThread {
            if let screen = MainActor.assumeIsolated({
                UIApplication.shared.connectedScenes
                    .compactMap { $0 as? UIWindowScene }
                    .first?.screen
            }) {
                width = Double(screen.bounds.width)
                height = Double(screen.bounds.height)
                scale = Double(screen.scale)
            }
        }
        #endif
        return DebugSessionEnvironment(
            osVersion: ProcessInfo.processInfo.operatingSystemVersionString,
            arch: arch,
            deviceModel: machine,
            isSimulator: isSim,
            screenWidth: width,
            screenHeight: height,
            screenScale: scale
        )
    }

    private static func directoryStats(_ dir: URL) -> (totalBytes: UInt64, logLines: Int, errorCount: Int) {
        let fm = FileManager.default
        var total: UInt64 = 0
        var logLines = 0
        var errorCount = 0
        if let enumerator = fm.enumerator(at: dir, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey]) {
            for case let fileURL as URL in enumerator {
                guard let values = try? fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                      values.isRegularFile == true else { continue }
                total += UInt64(max(0, values.fileSize ?? 0))
                if fileURL.pathExtension == "jsonl",
                   let text = try? String(contentsOf: fileURL, encoding: .utf8) {
                    for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
                        logLines += 1
                        if line.contains("\"level\":\"error\"") || line.contains("\"level\":\"fatal\"") {
                            errorCount += 1
                        }
                    }
                }
            }
        }
        return (total, logLines, errorCount)
    }

    private static func contentType(for pathExtension: String) -> String {
        switch pathExtension.lowercased() {
        case "json": return "application/json; charset=utf-8"
        case "jsonl": return "application/x-ndjson; charset=utf-8"
        case "txt", "log": return "text/plain; charset=utf-8"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "webp": return "image/webp"
        default: return "application/octet-stream"
        }
    }
}

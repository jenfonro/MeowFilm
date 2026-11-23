import Foundation

public enum DebugLogCategory: String, CaseIterable, Codable, Sendable, Hashable {
    case lifecycle
    case uiNav = "ui-nav"
    case uiAction = "ui-action"
    case runtimeBridge = "runtime-bridge"
    case runtimeNode = "runtime-node"
    case musicPlatform = "music-platform"
    case musicAccount = "music-account"
    case musicCatalog = "music-catalog"
    case musicLibrary = "music-library"
    case musicPlayback = "music-playback"
    case filmCatalog = "film-catalog"
    case filmPlayback = "film-playback"
    case novel
    case search
    case storage
    case diagnostics

    public static let allNames: [String] = allCases.map(\.rawValue)
    public static let allSet: Set<String> = Set(allNames)
}

public enum DebugLogLevel: String, CaseIterable, Codable, Sendable, Hashable {
    case trace
    case debug
    case info
    case warn
    case error
    case fatal
}

public enum DebugArgumentError: Error, LocalizedError, Sendable, Equatable {
    case unknownDebugOption(String)
    case missingValue(String)
    case invalidPort(String)
    case invalidBindAddress(String)
    case invalidPath(String, String)
    case overlappingPaths(String, String)
    case invalidModules(String)
    case invalidNonNegativeInt(String, String)
    case invalidPositiveInt(String, String)

    public var errorDescription: String? {
        switch self {
        case .unknownDebugOption(let opt): return "unknown debug option: \(opt)"
        case .missingValue(let opt): return "missing value for \(opt)"
        case .invalidPort(let raw): return "invalid --debug-port value: \(raw) (expected 0..65535)"
        case .invalidBindAddress(let raw): return "invalid --debug-bind value: \(raw)"
        case .invalidPath(let opt, let raw): return "invalid \(opt) path (must be absolute without control chars): \(raw)"
        case .overlappingPaths(let debug, let data): return "--debug-dir (\(debug)) must not overlap with --data-root (\(data))"
        case .invalidModules(let raw): return "invalid --debug-modules value: \(raw)"
        case .invalidNonNegativeInt(let opt, let raw): return "invalid \(opt) value: \(raw) (expected >= 0)"
        case .invalidPositiveInt(let opt, let raw): return "invalid \(opt) value: \(raw) (expected >= 1)"
        }
    }
}

public struct DebugRuntimeOptions: Codable, Sendable, Equatable {
    public var modules: [String]
    public var inlineLimit: Int
    public var eventBytesLimit: Int
    public var keepSessions: Int
    public var firstChanceExceptions: Bool

    public init(
        modules: [String] = ["all"],
        inlineLimit: Int = 2048,
        eventBytesLimit: Int = 16384,
        keepSessions: Int = 10,
        firstChanceExceptions: Bool = false
    ) {
        self.modules = modules
        self.inlineLimit = inlineLimit
        self.eventBytesLimit = eventBytesLimit
        self.keepSessions = keepSessions
        self.firstChanceExceptions = firstChanceExceptions
    }

    public func accepts(_ category: DebugLogCategory) -> Bool {
        modules.contains("all") || modules.contains(category.rawValue)
    }
}

public struct DebugNodeHandshake: Codable, Sendable, Equatable {
    public let enabled: Bool
    public let sessionId: String
    public let sessionDir: String
    public let inlineLimit: Int
    public let eventBytesLimit: Int
    public let modules: [String]

    public init(
        enabled: Bool = true,
        sessionId: String,
        sessionDir: String,
        inlineLimit: Int,
        eventBytesLimit: Int,
        modules: [String]
    ) {
        self.enabled = enabled
        self.sessionId = sessionId
        self.sessionDir = sessionDir
        self.inlineLimit = inlineLimit
        self.eventBytesLimit = eventBytesLimit
        self.modules = modules
    }
}

public struct DebugLaunchConfiguration: Sendable, Equatable {
    public let port: UInt16
    public let bindAddress: String
    public let debugRootOverride: URL?
    public let dataRootOverride: URL?
    public let options: DebugRuntimeOptions

    public init(
        port: UInt16 = 0,
        bindAddress: String = "127.0.0.1",
        debugRootOverride: URL? = nil,
        dataRootOverride: URL? = nil,
        options: DebugRuntimeOptions = DebugRuntimeOptions()
    ) {
        self.port = port
        self.bindAddress = bindAddress
        self.debugRootOverride = debugRootOverride
        self.dataRootOverride = dataRootOverride
        self.options = options
    }

    /// Parses process arguments according to the cross-platform contract.
    /// Returns `nil` when `--debug` is not present (zero side effects).
    /// Unknown non-`--debug*` platform arguments (e.g. `-AppleLanguages`) are ignored.
    public static func parse(arguments: [String] = ProcessInfo.processInfo.arguments) throws -> DebugLaunchConfiguration? {
        let args = arguments.count > 1 ? Array(arguments.dropFirst()) : []
        let enabled = args.contains("--debug")
        guard enabled else { return nil }

        var port: UInt16 = 0
        var bindAddress = "127.0.0.1"
        var debugRootOverride: URL?
        var dataRootOverride: URL?
        var modules: [String] = ["all"]
        var inlineLimit = 2048
        var eventBytesLimit = 16384
        var keepSessions = 10
        var firstChance = false

        var index = 0
        func nextValue(for flag: String) throws -> String {
            let next = index + 1
            guard next < args.count else { throw DebugArgumentError.missingValue(flag) }
            index = next
            return args[next]
        }

        while index < args.count {
            let token = args[index]
            let flag: String
            let inlineValue: String?
            if token.hasPrefix("--debug-") || token.hasPrefix("--data-root="),
               let eq = token.firstIndex(of: "=") {
                flag = String(token[..<eq])
                inlineValue = String(token[token.index(after: eq)...])
            } else {
                flag = token
                inlineValue = nil
            }

            func value() throws -> String {
                if let inlineValue { return inlineValue }
                return try nextValue(for: flag)
            }

            switch flag {
            case "--debug":
                break
            case "--debug-first-chance":
                if let inlineValue {
                    switch inlineValue.lowercased() {
                    case "true", "1": firstChance = true
                    case "false", "0": firstChance = false
                    default: throw DebugArgumentError.unknownDebugOption("\(flag)=\(inlineValue)")
                    }
                } else if index + 1 < args.count, !args[index + 1].hasPrefix("-") {
                    let nextToken = args[index + 1].lowercased()
                    switch nextToken {
                    case "true", "1":
                        firstChance = true
                        index += 1
                    case "false", "0":
                        firstChance = false
                        index += 1
                    default:
                        firstChance = true
                    }
                } else {
                    firstChance = true
                }
            case "--debug-port":
                let raw = try value()
                guard Self.isASCIIDigits(raw), let parsed = UInt16(raw) else {
                    throw DebugArgumentError.invalidPort(raw)
                }
                port = parsed
            case "--debug-bind":
                let raw = try value().trimmingCharacters(in: .whitespacesAndNewlines)
                let normalized = raw.lowercased() == "localhost" ? "127.0.0.1" : raw
                guard Self.isValidBindAddress(normalized) else { throw DebugArgumentError.invalidBindAddress(raw) }
                bindAddress = normalized
            case "--debug-dir":
                let raw = try value()
                guard Self.isValidAbsolutePath(raw) else { throw DebugArgumentError.invalidPath(flag, raw) }
                debugRootOverride = URL(fileURLWithPath: raw, isDirectory: true).standardizedFileURL
            case "--data-root":
                let raw = try value()
                guard Self.isValidAbsolutePath(raw) else { throw DebugArgumentError.invalidPath(flag, raw) }
                dataRootOverride = URL(fileURLWithPath: raw, isDirectory: true).standardizedFileURL
            case "--debug-modules":
                let raw = try value()
                modules = try Self.parseModules(raw)
            case "--debug-inline-limit":
                let raw = try value()
                guard Self.isASCIIDigits(raw), let parsed = Int(raw), parsed >= 0 else {
                    throw DebugArgumentError.invalidNonNegativeInt(flag, raw)
                }
                inlineLimit = parsed
            case "--debug-event-limit":
                let raw = try value()
                guard Self.isASCIIDigits(raw), let parsed = Int(raw), parsed >= 0 else {
                    throw DebugArgumentError.invalidNonNegativeInt(flag, raw)
                }
                eventBytesLimit = parsed
            case "--debug-keep":
                let raw = try value()
                guard Self.isASCIIDigits(raw), let parsed = Int(raw), parsed >= 1 else {
                    throw DebugArgumentError.invalidPositiveInt(flag, raw)
                }
                keepSessions = parsed
            default:
                if flag.hasPrefix("--debug-") {
                    throw DebugArgumentError.unknownDebugOption(flag)
                }
            }
            index += 1
        }

        let effectiveDebugRoot = debugRootOverride ?? (try? Self.defaultDebugRoot())
        let effectiveDataRoot: URL?
        if let dataRootOverride {
            effectiveDataRoot = dataRootOverride
        } else if let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            effectiveDataRoot = support.standardizedFileURL.appendingPathComponent("meowfilm", isDirectory: true)
        } else {
            effectiveDataRoot = nil
        }
        if let effectiveDebugRoot, let effectiveDataRoot,
           Self.pathsOverlap(effectiveDebugRoot, effectiveDataRoot) {
            throw DebugArgumentError.overlappingPaths(effectiveDebugRoot.path, effectiveDataRoot.path)
        }

        return DebugLaunchConfiguration(
            port: port,
            bindAddress: bindAddress,
            debugRootOverride: debugRootOverride,
            dataRootOverride: dataRootOverride,
            options: DebugRuntimeOptions(
                modules: modules,
                inlineLimit: inlineLimit,
                eventBytesLimit: eventBytesLimit,
                keepSessions: keepSessions,
                firstChanceExceptions: firstChance
            )
        )
    }

    private static func pathsOverlap(_ a: URL, _ b: URL) -> Bool {
        let pa = a.standardizedFileURL.path.hasSuffix("/") ? String(a.standardizedFileURL.path.dropLast()) : a.standardizedFileURL.path
        let pb = b.standardizedFileURL.path.hasSuffix("/") ? String(b.standardizedFileURL.path.dropLast()) : b.standardizedFileURL.path
        return pa == pb || pa.hasPrefix(pb + "/") || pb.hasPrefix(pa + "/")
    }

    public static func parseModules(_ csv: String) throws -> [String] {
        let parts = csv.split(separator: ",", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard !parts.isEmpty, parts.allSatisfy({ !$0.isEmpty }) else {
            throw DebugArgumentError.invalidModules(csv)
        }
        if parts.contains("all") {
            guard parts.count == 1 else { throw DebugArgumentError.invalidModules(csv) }
            return ["all"]
        }
        var seen = Set<String>()
        var result: [String] = []
        for name in parts {
            guard DebugLogCategory.allSet.contains(name) else {
                throw DebugArgumentError.invalidModules(csv)
            }
            if seen.insert(name).inserted { result.append(name) }
        }
        return result
    }

    public static func parseModulesArray(_ values: [String]) throws -> [String] {
        guard !values.isEmpty else { throw DebugArgumentError.invalidModules("") }
        var seen = Set<String>()
        var result: [String] = []
        for raw in values {
            let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty, !name.contains(",") else {
                throw DebugArgumentError.invalidModules(values.joined(separator: ","))
            }
            if name == "all" {
                guard values.count == 1 else {
                    throw DebugArgumentError.invalidModules(values.joined(separator: ","))
                }
                return ["all"]
            }
            guard DebugLogCategory.allSet.contains(name) else {
                throw DebugArgumentError.invalidModules(name)
            }
            if seen.insert(name).inserted { result.append(name) }
        }
        return result
    }

    private static func isASCIIDigits(_ raw: String) -> Bool {
        !raw.isEmpty && raw.unicodeScalars.allSatisfy { $0.value >= 0x30 && $0.value <= 0x39 }
    }

    private static func isValidAbsolutePath(_ raw: String) -> Bool {
        guard raw.hasPrefix("/"), !raw.isEmpty else { return false }
        return !raw.unicodeScalars.contains { $0.value < 0x20 || $0.value == 0x7f }
    }

    private static func isValidBindAddress(_ raw: String) -> Bool {
        guard !raw.isEmpty, !raw.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7f }) else {
            return false
        }
        if raw == "localhost" || raw == "0.0.0.0" || raw == "127.0.0.1" || raw == "::1" || raw == "::" {
            return true
        }
        var ipv4 = in_addr()
        if raw.withCString({ inet_pton(AF_INET, $0, &ipv4) }) == 1 { return true }
        var ipv6 = in6_addr()
        if raw.withCString({ inet_pton(AF_INET6, $0, &ipv6) }) == 1 { return true }
        return false
    }

    public static func defaultDebugRoot() throws -> URL {
        let support = try FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )
        return support.standardizedFileURL.appendingPathComponent("meowfilm-debug", isDirectory: true)
    }
}

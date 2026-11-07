import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime

/// Thin adapter of music.script.v3. The shared module owns installations,
/// source bytes, checks and request-bound guest storage under the App data root.
public actor NativeMusicScriptService: MusicScriptResolving, MusicScriptSourceManaging {
    private let connect: @Sendable () async throws -> RuntimeBridgeConnection
    private var connection: RuntimeBridgeConnection?
    private var starting: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var closed = false
    private struct Work: Sendable {
        let cancel: @Sendable () -> Void
        let drain: @Sendable () async -> Void
    }
    private var pending: [UUID: Work] = [:]

    public init(connection: @escaping @Sendable () async throws -> RuntimeBridgeConnection) {
        connect = connection
    }

    /// App assembly starts the borrower explicitly. A failed start is final;
    /// management and playback never launch or retry the shared runtime.
    public func start() {
        guard starting == nil, !closed else { return }
        starting = Task {
            let value = try await connect()
            // Retain the borrowed connection even if the handshake fails: only
            // the module's close receipt can confirm its cleanup afterwards.
            connection = value
            try Task.checkCancellation()
            let description = try await value.call("runtime.describe", .null, timeout: 60)
            guard description["protocol"]?.scriptInteger == 1,
                  case .array(let modules)? = description["modules"],
                  modules.contains(where: { $0["id"] == .string("music") && $0["version"] == .string("0.8.0") }) else {
                throw MusicServiceError.scriptRuntimeUnavailable
            }
        }
    }

    /// Stops admission, drains local work including transfer release, then asks
    /// the module for its terminal receipt. The App alone closes the session.
    public func close() async throws {
        if let closing { return try await closing.value }
        closed = true
        starting?.cancel()
        let work = Array(pending.values)
        for item in work { item.cancel() }
        let task = Task {
            _ = await starting?.result
            for item in work { await item.drain() }
            guard let connection else { return }
            do {
                let result = try await MusicScriptTransport(connection: connection).call("close", [:], timeout: 35)
                guard result["closed"] == .bool(true), result["cleanupComplete"] == .bool(true) else {
                    throw MusicServiceError.scriptCleanupFailed
                }
            } catch { throw MusicServiceError.scriptCleanupFailed }
        }
        closing = task
        // Waiting caller cancellation must not cancel this shared terminal task.
        try await task.value
    }

    private func perform<T: Sendable>(
        _ operation: @escaping @Sendable (isolated NativeMusicScriptService) async throws -> T
    ) async throws -> T {
        try musicRequire(!closed, .closed)
        let id = UUID()
        let task = Task {
            try Task.checkCancellation()
            let value = try await operation(self)
            try Task.checkCancellation()
            return value
        }
        pending[id] = Work(cancel: { task.cancel() }, drain: { _ = await task.result })
        defer { pending[id] = nil }
        do {
            return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        } catch { throw Self.failure(error) }
    }

    private func transport() async throws -> MusicScriptTransport {
        try Task.checkCancellation()
        guard let starting else { throw MusicServiceError.scriptRuntimeUnavailable }
        do { try await starting.value }
        catch {
            try Task.checkCancellation()
            throw MusicServiceError.scriptRuntimeUnavailable
        }
        try Task.checkCancellation()
        guard let connection, !connection.isClosed else { throw MusicServiceError.scriptRuntimeUnavailable }
        return MusicScriptTransport(connection: connection)
    }

    private func call(_ operation: String, _ parameters: [String: JSONValue] = [:]) async throws -> [String: JSONValue] {
        do { return try await transport().call(operation, parameters, timeout: 35) }
        catch { throw Self.failure(error) }
    }

    private func row(_ operation: String, _ parameters: [String: JSONValue]) async throws -> [String: JSONValue] {
        let result = try await call(operation, parameters)
        guard case .object(let value)? = result["item"] else { throw MusicServiceError.invalidResponse }
        return value
    }

    public func scriptSources() async throws -> [MusicScriptSourceItem] {
        try await perform { service in
            try await service.rows().map(Self.item)
        }
    }
    private func rows() async throws -> [[String: JSONValue]] {
        guard case .array(let values)? = try await call("sources.list")["items"] else {
            throw MusicServiceError.invalidResponse
        }
        return try values.map { value in
            guard case .object(let row) = value else { throw MusicServiceError.invalidResponse }
            return row
        }
    }
    public func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try await service.upload(data, fileName: fileName, operation: "sources.add", parameters: [:])
        }
    }
    private func upload(_ data: Data, fileName: String, operation: String,
                        parameters: [String: JSONValue]) async throws -> MusicScriptSourceItem {
        do {
            let transport = try await transport()
            let token = try await transport.stage(data, basename: fileName)
            var parameters = parameters; parameters["token"] = .string(token)
            do {
                let result = try await row(operation, parameters)
                await transport.release("stage.abort", token)
                return try Self.item(result)
            } catch { await transport.release("stage.abort", token); throw error }
        } catch { throw Self.failure(error) }
    }
    public func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.download", ["url": .string(url.absoluteString)]))
        }
    }
    public func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row(enabled ? "sources.enable" : "sources.disable", ["installationId": .string(id)]))
        }
    }
    public func removeScriptSource(_ id: String) async throws {
        try await perform { service in
            _ = try await service.call("sources.remove", ["installationId": .string(id)])
        }
    }
    public func scriptSourceDetail(_ id: String) async throws -> MusicScriptSourceDetail {
        try await perform { service in
            let value = try await service.row("sources.detail", ["installationId": .string(id)])
            return .init(item: try Self.item(value), networkURL: value["origin"]?["url"]?.scriptText,
                         rollbackAvailable: value["rollbackAvailable"] == .bool(true))
        }
    }
    public func editScriptSource(_ id: String, name: String, networkURL: String? = nil) async throws -> MusicScriptSourceItem {
        try await perform { service in
            var parameters: [String: JSONValue] = ["installationId": .string(id), "name": .string(name)]
            if let networkURL { parameters["url"] = .string(networkURL) }
            return try Self.item(await service.row("sources.edit", parameters))
        }
    }
    public func updateScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.update", ["installationId": .string(id)]))
        }
    }
    public func updateScriptSource(_ id: String, data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try await service.upload(data, fileName: fileName, operation: "sources.update", parameters: ["installationId": .string(id)])
        }
    }
    public func rollbackScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.rollback", ["installationId": .string(id)]))
        }
    }
    public func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.check", ["installationId": .string(id)]))
        }
    }

    // Playback asks for live declarations only when it needs scripts. Startup
    // and management listing do not execute an installation.
    public func sources() async throws -> [MusicScriptSource] {
        try await perform { service in
            var result: [MusicScriptSource] = []
            for item in try await service.rows() where item["enabled"] == .bool(true) {
                guard let id = item["installationId"]?.scriptText else { throw MusicServiceError.invalidResponse }
                do { result.append(try await service.sourceSnapshot(installationID: id)) }
                catch MusicServiceError.scriptRuntimeUnavailable { throw MusicServiceError.scriptRuntimeUnavailable }
                catch MusicServiceError.cancelled { throw MusicServiceError.cancelled }
                catch { continue } // A broken script does not hide other enabled scripts.
            }
            return result
        }
    }
    public func source(installationID id: String) async throws -> MusicScriptSource {
        try await perform { service in
            try await service.sourceSnapshot(installationID: id)
        }
    }
    private func sourceSnapshot(installationID id: String) async throws -> MusicScriptSource {
        var item = try await row("sources.detail", ["installationId": .string(id)])
        try musicRequire(item["enabled"] == .bool(true), .sourceUnavailable)
        if item["state"] != .string("ready") {
            item = try await row("sources.enable", ["installationId": .string(id)])
        }
        return try Self.snapshot(item)
    }
    private func request(_ request: MusicScriptRequest, operation: String,
                         parameters: [String: JSONValue]) async throws -> [String: JSONValue] {
        let current = try await row("sources.detail", ["installationId": .string(request.installationID)])
        try musicRequire(current["enabled"] == .bool(true) && current["state"] == .string("ready") &&
            current["generation"]?.scriptInteger == request.generation, .sourceUnavailable)
        let result = try await call(operation, parameters)
        try musicRequire(result["installationId"] == .string(request.installationID) &&
            result["generation"]?.scriptInteger == request.generation, .sourceUnavailable)
        return result
    }
    public func resolve(request: MusicScriptRequest) async throws -> MusicNativeAudio {
        try await perform { service in
            let result = try await service.request(request, operation: "resolve", parameters: [
                "installationId": .string(request.installationID), "platform": .string(request.siteID),
                "quality": .string(request.quality), "musicInfo": try Self.musicInfo(request.musicInfo),
            ])
            guard let url = result["url"]?.scriptText, result["platform"] == .string(request.siteID),
                  result["quality"] == .string(request.quality), case .object(let values)? = result["headers"] else {
                throw MusicServiceError.invalidMediaCandidate
            }
            var headers: [String: String] = [:]
            for (key, value) in values {
                guard let text = value.scriptText else { throw MusicServiceError.invalidMediaCandidate }
                headers[key] = text
            }
            let expires = result["expiresAt"]?.scriptInteger.map { Date(timeIntervalSince1970: Double($0) / 1000) }
            return .init(url: try Self.mediaURL(url), headers: headers, expiresAt: expires, fileCachePolicy: .denied)
        }
    }
    public func lyrics(request: MusicScriptRequest) async throws -> MusicNativeLyrics {
        try await perform { service in
            let result = try await service.request(request, operation: "invoke", parameters: [
                "installationId": .string(request.installationID), "siteId": .string(request.siteID),
                "operation": .string("lyric"), "payload": .object(["musicInfo": try Self.musicInfo(request.musicInfo)]),
            ])
            guard case .object(let fields)? = result["value"], let text = fields["lyric"]?.scriptText else {
                throw MusicServiceError.invalidLyricResponse
            }
            func optional(_ key: String) throws -> String {
                switch fields[key] {
                case nil, .null?: return ""
                case .string(let value)?: return value
                default: throw MusicServiceError.invalidLyricResponse
                }
            }
            return .init(text: text, translation: try optional("tlyric"), romanization: try optional("rlyric"))
        }
    }

    private static func item(_ value: [String: JSONValue]) throws -> MusicScriptSourceItem {
        guard let id = value["installationId"]?.scriptText, let name = value["name"]?.scriptText,
              let version = value["version"]?.scriptText, case .bool(let enabled)? = value["enabled"],
              let state = value["state"]?.scriptText else { throw MusicServiceError.invalidResponse }
        let status: MusicScriptSourceItem.State
        switch state {
        case "ready": status = .ready
        case "idle": status = .idle
        case "disabled": status = .disabled
        case "failed": status = .failed
        default: throw MusicServiceError.invalidResponse
        }
        let origin: MusicScriptSourceItem.Origin
        switch value["origin"]?["kind"] {
        case .string("file"): origin = .file
        case .string("network"):
            guard let host = value["origin"]?["host"]?.scriptText else { throw MusicServiceError.invalidResponse }
            origin = .network(host: host)
        default: throw MusicServiceError.invalidResponse
        }
        let capabilities = try capabilities(value)
        var check: MusicScriptSourceCheck?
        if case .object(let checked)? = value["check"] {
            guard let summary = checked["summary"]?.scriptText, let at = checked["checkedAt"]?.scriptInteger,
                  case .object(let sites)? = checked["sites"] else { throw MusicServiceError.invalidResponse }
            var states: [String: String] = [:]
            for (platform, result) in sites {
                guard let state = result["state"]?.scriptText else { throw MusicServiceError.invalidResponse }
                states[platform] = state
            }
            check = .init(summary: summary, checkedAt: Date(timeIntervalSince1970: Double(at) / 1000), platforms: states)
        }
        return .init(id: id, name: name, version: version, author: value["author"]?.scriptText ?? "",
            summary: value["description"]?.scriptText ?? "", homepage: value["homepage"]?.scriptText ?? "",
            origin: origin, platforms: capabilities.compactMap { MusicPublicBrowsePlatform(rawValue: $0.siteID) },
            enabled: enabled, state: status, check: check)
    }
    private static func capabilities(_ value: [String: JSONValue]) throws -> [MusicScriptCapability] {
        if value["sites"] == .null { return [] }
        guard case .object(let sites)? = value["sites"] else { throw MusicServiceError.invalidResponse }
        return try MusicPublicBrowsePlatform.allCases.compactMap { platform in
            guard let site = sites[platform.rawValue] else { return nil }
            guard case .array(let actions)? = site["actions"], case .array(let qualities)? = site["qualitys"],
                  actions.allSatisfy({ $0.scriptText != nil }), qualities.allSatisfy({ $0.scriptText != nil }) else {
                throw MusicServiceError.invalidResponse
            }
            return .init(siteID: platform.rawValue, title: value["name"]?.scriptText ?? "",
                         actions: actions.compactMap(\.scriptText), qualities: qualities.compactMap(\.scriptText))
        }
    }
    private static func snapshot(_ value: [String: JSONValue]) throws -> MusicScriptSource {
        guard let id = value["installationId"]?.scriptText, let generation = value["generation"]?.scriptInteger,
              generation > 0, let version = value["version"]?.scriptText, let hash = value["contentHash"]?.scriptText,
              let execution = value["executionHash"]?.scriptText else { throw MusicServiceError.invalidResponse }
        return .init(installationID: id, generation: generation, enabled: true, ready: true, version: version,
                     contentHash: hash, revisionHash: execution, capabilities: try capabilities(value))
    }

    static func musicInfo(_ data: Data) throws -> JSONValue {
        guard let value = try? RuntimeBridgeJSON.decode(data, maximumBytes: 64 * 1024), case .object = value else {
            throw MusicServiceError.invalidRequest
        }
        return value
    }

    /// LX media is a direct URL. HTTP links are upgraded instead of adding an ATS exception.
    static func mediaURL(_ text: String) throws -> URL {
        guard text.utf8.count <= 8192, !text.unicodeScalars.contains(where: { $0.value <= 32 || $0.value == 127 }),
              var components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(), ["http", "https"].contains(scheme),
              components.host?.isEmpty == false, components.user == nil, components.password == nil else {
            throw MusicServiceError.invalidMediaCandidate
        }
        if scheme == "http" {
            components.scheme = "https"
            if components.port == 80 { components.port = nil }
        }
        components.fragment = nil
        guard let url = components.url else { throw MusicServiceError.invalidMediaCandidate }
        return url
    }

    private static func failure(_ error: Error) -> MusicServiceError {
        if Task.isCancelled || error is CancellationError { return .cancelled }
        if let error = error as? MusicServiceError { return error }
        if let error = error as? RuntimeBridgeError {
            switch error {
            case .cancelled: return .cancelled
            case .timeout: return .timeout
            case .closed: return .scriptRuntimeUnavailable
            default: return .invalidResponse
            }
        }
        if let error = error as? MusicScriptCallError {
            switch error.code {
            case "NETWORK_ERROR", "UPSTREAM_HTTP": return .upstreamFailed
            case "UPSTREAM_FORMAT": return .upstreamInvalid
            case "TIMEOUT": return .timeout
            case "CANCELLED": return .cancelled
            case "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED", "CAPABILITY_UNAVAILABLE": return .sourceUnavailable
            case "CLOSED": return .scriptRuntimeUnavailable
            case "SCRIPT_CLEANUP": return .scriptCleanupFailed
            case "CORRUPT_STORAGE": return .corruptDocument
            case "STORAGE", "STORAGE_ERROR": return .storageFailed
            case "SCRIPT_ENCODING", "SCRIPT_INTEGRITY", "INVALID_SCRIPT": return .invalidScript
            case "INVALID_MEDIA_CANDIDATE", "MEDIA_UNAVAILABLE": return .invalidMediaCandidate
            case "LIMIT": return .limit
            case "INVALID_REQUEST": return .invalidRequest
            default: return .scriptFailed
            }
        }
        return .scriptFailed
    }
}

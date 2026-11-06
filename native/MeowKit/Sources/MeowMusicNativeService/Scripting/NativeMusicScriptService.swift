import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime

/// Thin adapter of music.script.v3. The shared module owns installations,
/// source bytes, checks and request-bound guest storage under the App data root.
public actor NativeMusicScriptService: MusicScriptResolving, MusicScriptSourceManaging {
    private let launcher: any RuntimeBridgeLaunching
    private var connection: RuntimeBridgeConnection?
    private var starting: Task<RuntimeBridgeConnection, Error>?
    private var closed = false

    public init(launcher: any RuntimeBridgeLaunching) { self.launcher = launcher }

    /// Called by App assembly, not by a management or playback request. A failed
    /// start is final for this process and does not block native music services.
    public func start() {
        guard starting == nil, !closed else { return }
        starting = Task { [launcher] in
            let connection = try await launcher.connect(handlers: [:])
            do {
                let description = try await connection.call("runtime.describe", .null, timeout: 60)
                guard description["protocol"]?.scriptInteger == 1,
                      case .array(let modules)? = description["modules"],
                      modules.contains(where: { $0["id"] == .string("music") && $0["version"] == .string("0.8.0") }) else {
                    throw MusicServiceError.scriptRuntimeUnavailable
                }
                return connection
            } catch { connection.close(); throw error }
        }
    }

    public func close() async {
        closed = true
        starting?.cancel()
        connection?.close()
        if let starting, let value = try? await starting.value { value.close() }
    }

    private func transport() async throws -> MusicScriptTransport {
        try musicRequire(!closed, .closed)
        guard let starting else { throw MusicServiceError.scriptRuntimeUnavailable }
        do {
            let value = try await starting.value
            guard !closed else { value.close(); throw MusicServiceError.closed }
            guard !value.isClosed else { throw MusicServiceError.scriptRuntimeUnavailable }
            connection = value
            return MusicScriptTransport(connection: value)
        } catch let error as MusicServiceError { throw error }
        catch { throw MusicServiceError.scriptRuntimeUnavailable }
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
        try await rows().map(Self.item)
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
        try await upload(data, fileName: fileName, operation: "sources.add", parameters: [:])
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
        try Self.item(await row("sources.download", ["url": .string(url.absoluteString)]))
    }
    public func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        try Self.item(await row(enabled ? "sources.enable" : "sources.disable", ["installationId": .string(id)]))
    }
    public func removeScriptSource(_ id: String) async throws {
        _ = try await call("sources.remove", ["installationId": .string(id)])
    }
    public func scriptSourceDetail(_ id: String) async throws -> MusicScriptSourceDetail {
        let value = try await row("sources.detail", ["installationId": .string(id)])
        return .init(item: try Self.item(value), networkURL: value["origin"]?["url"]?.scriptText,
                     rollbackAvailable: value["rollbackAvailable"] == .bool(true))
    }
    public func editScriptSource(_ id: String, name: String, networkURL: String? = nil) async throws -> MusicScriptSourceItem {
        var parameters: [String: JSONValue] = ["installationId": .string(id), "name": .string(name)]
        if let networkURL { parameters["url"] = .string(networkURL) }
        return try Self.item(await row("sources.edit", parameters))
    }
    public func updateScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try Self.item(await row("sources.update", ["installationId": .string(id)]))
    }
    public func updateScriptSource(_ id: String, data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await upload(data, fileName: fileName, operation: "sources.update", parameters: ["installationId": .string(id)])
    }
    public func rollbackScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try Self.item(await row("sources.rollback", ["installationId": .string(id)]))
    }
    public func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try Self.item(await row("sources.check", ["installationId": .string(id)]))
    }

    // Playback asks for live declarations only when it needs scripts. Startup
    // and management listing do not execute an installation.
    public func sources() async throws -> [MusicScriptSource] {
        var result: [MusicScriptSource] = []
        for item in try await rows() where item["enabled"] == .bool(true) {
            guard let id = item["installationId"]?.scriptText else { throw MusicServiceError.invalidResponse }
            do { result.append(try await source(installationID: id)) }
            catch MusicServiceError.scriptRuntimeUnavailable { throw MusicServiceError.scriptRuntimeUnavailable }
            catch { continue } // A broken script does not hide other enabled scripts.
        }
        return result
    }
    public func source(installationID id: String) async throws -> MusicScriptSource {
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
        let result = try await self.request(request, operation: "resolve", parameters: [
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
    public func lyrics(request: MusicScriptRequest) async throws -> MusicNativeLyrics {
        let result = try await self.request(request, operation: "invoke", parameters: [
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
        if Task.isCancelled { return .cancelled }
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

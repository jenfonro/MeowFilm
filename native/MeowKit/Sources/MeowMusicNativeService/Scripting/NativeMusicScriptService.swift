import Foundation
import MeowMusicFeature
import MeowRuntime

/// Thin adapter of music.script.v3. The shared module owns installations,
/// source bytes, checks and request-bound guest storage under the App data root.
public actor NativeMusicScriptService: MusicScriptResolving, MusicScriptSourceManaging {
    private let connect: @Sendable () async throws -> RuntimeHTTPClient
    private var connection: RuntimeHTTPClient?
    private var starting: Task<Void, Error>?
    private var closing: Task<Void, Error>?
    private var closed = false
    private var sourceChanges: [String: UUID] = [:]
    private struct Work: Sendable {
        let cancel: @Sendable () -> Void
        let drain: @Sendable () async -> Void
    }
    private var pending: [UUID: Work] = [:]

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
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
            let description = try await value.describe()
            guard description.scriptField("protocol") == .string("runtime.http.v1"),
                  case .array(let modules)? = description.scriptField("modules"),
                  modules.contains(where: {
                      $0.scriptField("id") == .string("music") &&
                      $0.scriptField("version") == .string("0.8.0") &&
                      $0.scriptField("contract") == .string("music.script.v3")
                  }) else {
                throw MusicServiceError.scriptRuntimeUnavailable
            }
        }
    }

    /// Stops admission, drains local HTTP work, then asks
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
                let result = try await MusicScriptTransport(connection: connection).call("close")
                guard result.scriptField("closed") == .bool(true), result.scriptField("cleanupComplete") == .bool(true) else {
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

    private func call(_ operation: String, _ parameters: RuntimeObject = [:]) async throws -> RuntimeValue {
        do { return try await transport().call(operation, parameters) }
        catch { throw Self.failure(error) }
    }

    private func row(_ operation: String, _ parameters: RuntimeObject) async throws -> RuntimeObject {
        let result = try await call(operation, parameters)
        guard case .object(let value)? = result.scriptField("item") else { throw MusicServiceError.invalidResponse }
        return value
    }

    public func scriptSources() async throws -> [MusicScriptSourceItem] {
        try await perform { service in
            try await service.rows().map(Self.item)
        }
    }
    private func rows() async throws -> [RuntimeObject] {
        guard case .array(let values)? = try await call("sources.list").scriptField("items") else {
            throw MusicServiceError.invalidResponse
        }
        return try values.map { value in
            guard case .object(let row) = value else { throw MusicServiceError.invalidResponse }
            return row
        }
    }
    public func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try await service.upload(data, fileName: fileName, operation: "sources.add",
                                     installationID: UUID().uuidString.lowercased())
        }
    }
    private func upload(_ data: Data, fileName: String, operation: String,
                        installationID: String) async throws -> MusicScriptSourceItem {
        let revision = Int.random(in: 1...9_007_199_254_740_991)
        do {
            try await transport().upload(data, installationID: installationID, revision: revision, basename: fileName)
            try Task.checkCancellation()
            if operation == "sources.update" { invalidate(installationID) }
            let result = try await row(operation, ["installationId": .string(installationID),
                                                   "sourceRevision": .number(Double(revision))])
            return try Self.item(result)
        } catch { throw Self.failure(error) }
    }
    private func invalidate(_ id: String) { sourceChanges[id] = UUID() }
    public func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.download", ["url": .string(url.absoluteString)]))
        }
    }
    public func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        try await perform { service in
            service.invalidate(id)
            return try Self.item(await service.row(enabled ? "sources.enable" : "sources.disable", ["installationId": .string(id)]))
        }
    }
    public func removeScriptSource(_ id: String) async throws {
        try await perform { service in
            service.invalidate(id)
            _ = try await service.call("sources.remove", ["installationId": .string(id)])
        }
    }
    public func scriptSourceDetail(_ id: String) async throws -> MusicScriptSourceDetail {
        try await perform { service in
            let value = try await service.row("sources.detail", ["installationId": .string(id)])
            return .init(item: try Self.item(value), networkURL: value["origin"]?.scriptField("url")?.scriptText,
                         rollbackAvailable: value["rollbackAvailable"] == .bool(true))
        }
    }
    public func editScriptSource(_ id: String, name: String, networkURL: String? = nil) async throws -> MusicScriptSourceItem {
        try await perform { service in
            var parameters: RuntimeObject = ["installationId": .string(id), "name": .string(name)]
            if let networkURL { parameters["url"] = .string(networkURL) }
            return try Self.item(await service.row("sources.edit", parameters))
        }
    }
    public func updateScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            service.invalidate(id)
            return try Self.item(await service.row("sources.update", ["installationId": .string(id)]))
        }
    }
    public func updateScriptSource(_ id: String, data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try await service.upload(data, fileName: fileName, operation: "sources.update", installationID: id)
        }
    }
    public func rollbackScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            service.invalidate(id)
            return try Self.item(await service.row("sources.rollback", ["installationId": .string(id)]))
        }
    }
    public func checkScriptSource(_ id: String) async throws -> MusicScriptSourceItem {
        try await perform { service in
            try Self.item(await service.row("sources.check", ["installationId": .string(id)]))
        }
    }

    // Directory reads never enable, execute or retry an installation.
    public func sources() async throws -> [MusicScriptSource] {
        try await perform { service in
            try await service.rows().filter { $0["enabled"] == .bool(true) }.map(Self.snapshot)
        }
    }
    public func source(installationID id: String) async throws -> MusicScriptSource {
        try await perform { service in
            try await service.sourceSnapshot(installationID: id)
        }
    }
    private func sourceSnapshot(installationID id: String) async throws -> MusicScriptSource {
        try Self.snapshot(await row("sources.detail", ["installationId": .string(id)]))
    }
    private func request(_ request: MusicScriptRequest, operation: String,
                         parameters: RuntimeObject) async throws -> MusicScriptResult<RuntimeValue> {
        let change = sourceChanges[request.installationID]
        let before = try await sourceSnapshot(installationID: request.installationID)
        try Self.pin(before, to: request.source)
        let result = try await call(operation, parameters)
        let after = try await sourceSnapshot(installationID: request.installationID)
        try musicRequire(sourceChanges[request.installationID] == change, .sourceUnavailable)
        try Self.pin(after, to: before)
        try musicRequire(after.state == .ready && after.generation > 0, .sourceUnavailable)
        if operation == "resolve" {
            try musicRequire(result.scriptField("installationId") == .string(after.installationID) &&
                result.scriptField("generation")?.scriptInteger == after.generation &&
                result.scriptField("contentHash") == .string(after.contentHash), .sourceUnavailable)
        }
        // invoke is the raw business result. Its identity comes from the two
        // snapshots and the runtime's own generation fence, not result fields.
        return .init(value: result, source: after)
    }
    private static func pin(_ current: MusicScriptSource, to previous: MusicScriptSource) throws {
        try musicRequire(current.isCandidate && previous.isCandidate &&
            current.installationID == previous.installationID && current.identity == previous.identity &&
            (current.generation == previous.generation ||
             previous.generation == 0 && current.state == .ready && current.generation > 0),
            .sourceUnavailable)
    }
    public func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        try await perform { service in
            let execution = try await service.request(request, operation: "resolve", parameters: [
                "installationId": .string(request.installationID), "platform": .string(request.siteID),
                "quality": .string(request.quality), "musicInfo": try Self.musicInfo(request.musicInfo),
            ])
            let result = execution.value
            guard let url = result.scriptField("url")?.scriptText,
                  result.scriptField("platform") == .string(request.siteID),
                  result.scriptField("quality") == .string(request.quality),
                  case .object(let values)? = result.scriptField("headers") else {
                throw MusicServiceError.invalidMediaCandidate
            }
            var headers: [String: String] = [:]
            for (key, value) in values {
                guard key.utf8.allSatisfy({ $0 < 128 }), let text = value.scriptText else {
                    throw MusicServiceError.invalidMediaCandidate
                }
                headers[key] = text
            }
            let expires: Date?
            switch result.scriptField("expiresAt") {
            case nil, .null?: expires = nil
            case let value?:
                guard let milliseconds = value.scriptInteger else { throw MusicServiceError.invalidMediaCandidate }
                expires = Date(timeIntervalSince1970: Double(milliseconds) / 1000)
            }
            return .init(value: .init(url: try Self.mediaURL(url), headers: headers, expiresAt: expires,
                                     fileCachePolicy: .denied), source: execution.source)
        }
    }
    public func lyrics(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeLyrics> {
        try await perform { service in
            let execution = try await service.request(request, operation: "invoke", parameters: [
                "installationId": .string(request.installationID), "siteId": .string(request.siteID),
                "operation": .string("lyric"), "payload": .object(["musicInfo": try Self.musicInfo(request.musicInfo)]),
            ])
            guard case .object(let fields) = execution.value, let text = fields["lyric"]?.scriptText else {
                throw MusicServiceError.invalidLyricResponse
            }
            func optional(_ key: String) throws -> String {
                switch fields[key] {
                case nil, .null?: return ""
                case .string(let value)?: return value
                default: throw MusicServiceError.invalidLyricResponse
                }
            }
            return .init(value: .init(text: text, translation: try optional("tlyric"),
                                     romanization: try optional("rlyric")), source: execution.source)
        }
    }

    private static func item(_ value: RuntimeObject) throws -> MusicScriptSourceItem {
        guard let id = value["installationId"]?.scriptText, let name = value["name"]?.scriptText,
              let version = value["version"]?.scriptText, case .bool(let enabled)? = value["enabled"],
              let state = value["state"]?.scriptText else { throw MusicServiceError.invalidResponse }
        guard let status = MusicScriptSourceItem.State(rawValue: state) else { throw MusicServiceError.invalidResponse }
        let origin: MusicScriptSourceItem.Origin
        switch value["origin"]?.scriptField("kind") {
        case .string("file"): origin = .file
        case .string("network"):
            guard let host = value["origin"]?.scriptField("host")?.scriptText else { throw MusicServiceError.invalidResponse }
            origin = .network(host: host)
        default: throw MusicServiceError.invalidResponse
        }
        let capabilities = try capabilities(value)
        var check: MusicScriptSourceCheck?
        if case .object(let checked)? = value["check"] {
            guard let summary = checked["summary"]?.scriptText, let at = checked["checkedAt"]?.scriptInteger,
                  case .object(let sites)? = checked["sites"] else { throw MusicServiceError.invalidResponse }
            var states: [String: String] = [:]
            for platform in MusicPublicBrowsePlatform.allCases.map(\.rawValue) {
                guard let result = sites[platform] else { continue }
                guard let state = result.scriptField("state")?.scriptText else { throw MusicServiceError.invalidResponse }
                states[platform] = state
            }
            check = .init(summary: summary, checkedAt: Date(timeIntervalSince1970: Double(at) / 1000), platforms: states)
        }
        return .init(id: id, name: name, version: version, author: value["author"]?.scriptText ?? "",
            summary: value["description"]?.scriptText ?? "", homepage: value["homepage"]?.scriptText ?? "",
            origin: origin, platforms: capabilities.compactMap { MusicPublicBrowsePlatform(rawValue: $0.siteID) },
            enabled: enabled, state: status, check: check)
    }
    private static func capabilities(_ value: RuntimeObject) throws -> [MusicScriptCapability] {
        if value["sites"] == .null { return [] }
        guard case .object(let sites)? = value["sites"] else { throw MusicServiceError.invalidResponse }
        return try MusicPublicBrowsePlatform.allCases.compactMap { platform in
            guard let site = sites[platform.rawValue] else { return nil }
            guard case .array(let actions)? = site.scriptField("actions"), case .array(let qualities)? = site.scriptField("qualitys"),
                  actions.allSatisfy({ $0.scriptText != nil }), qualities.allSatisfy({ $0.scriptText != nil }) else {
                throw MusicServiceError.invalidResponse
            }
            return .init(siteID: platform.rawValue, title: value["name"]?.scriptText ?? "",
                         actions: actions.compactMap(\.scriptText), qualities: qualities.compactMap(\.scriptText))
        }
    }
    private static func snapshot(_ value: RuntimeObject) throws -> MusicScriptSource {
        guard let id = value["installationId"]?.scriptText, let generation = value["generation"]?.scriptInteger,
              let version = value["version"]?.scriptText, let hash = value["contentHash"]?.scriptText,
              let execution = value["executionHash"]?.scriptText, case .bool(let enabled)? = value["enabled"],
              let stateName = value["state"]?.scriptText, let state = MusicScriptSourceItem.State(rawValue: stateName),
              generation >= 0, (state == .ready ? generation > 0 : generation == 0) else {
            throw MusicServiceError.invalidResponse
        }
        return .init(installationID: id, generation: generation, enabled: enabled, state: state, version: version,
                     contentHash: hash, revisionHash: execution, capabilities: try capabilities(value))
    }

    static func musicInfo(_ data: Data) throws -> RuntimeValue {
        guard let value = try? RuntimeJSON.decode(data), case .object = value else {
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
        if let error = error as? RuntimeHTTPError {
            switch error {
            case .cancelled: return .cancelled
            case .timeout: return .timeout
            case .closed: return .scriptRuntimeUnavailable
            case .invalidRequest: return .invalidRequest
            case .invalidResponse: return .invalidResponse
            case .remote(let code):
                switch code {
                case "NETWORK_ERROR", "UPSTREAM_HTTP": return .upstreamFailed
                case "UPSTREAM_FORMAT": return .upstreamInvalid
                case "TIMEOUT": return .timeout
                case "CANCELLED": return .cancelled
                case "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED", "CAPABILITY_UNAVAILABLE": return .sourceUnavailable
                case "CLOSED": return .scriptRuntimeUnavailable
                case "SCRIPT_CLEANUP": return .scriptCleanupFailed
                case "CORRUPT_STORAGE": return .corruptDocument
                case "STORAGE_ERROR": return .storageFailed
                case "SCRIPT_ENCODING", "SCRIPT_INTEGRITY", "SCRIPT_PROTOCOL_ERROR": return .invalidScript
                case "INVALID_MEDIA_CANDIDATE", "MEDIA_UNAVAILABLE", "EXPIRED_MEDIA_CANDIDATE", "MEDIA_HEADERS_UNSUPPORTED":
                    return .invalidMediaCandidate
                case "TARGET_CHANGED", "RECORDING_MISMATCH": return .targetChanged
                case "INVALID_SOURCE_URL": return .insecureScriptURL
                case "SOURCE_DOWNLOAD_FAILED": return .scriptDownloadFailed
                case "SOURCE_REDIRECT": return .scriptRedirect
                case "ROLLBACK_UNAVAILABLE": return .sourceUnavailable
                case "PERMISSION_DENIED": return .permissionDenied
                case "LIMIT": return .limit
                case "INVALID_REQUEST": return .invalidRequest
                default: return .scriptFailed
                }
            }
        }
        return .scriptFailed
    }
}

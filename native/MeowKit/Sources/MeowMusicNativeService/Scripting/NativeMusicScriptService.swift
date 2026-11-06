import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import MeowStorage

/// The native owner of installed raw LX scripts: metadata, source bytes, private
/// storage, grants and generations. The App owns the shared runtime. Listing
/// and importing never start the runtime; enabling or an actual playback /
/// lyric request loads an installation through music.script.v2.
public actor NativeMusicScriptService: MusicScriptResolving, MusicScriptSourceManaging {
    public static let namespace = "music.scripts.v1"
    private static let catalogKey = "catalog"
    private struct Live {
        let revision: Int
        let generation: Int
        let grant: String
        /// Declared consumable sites; capabilities follow the shared LX contract.
        let capabilities: [MusicScriptCapability]
        /// Every declared site, for storage authorization.
        let declaredSites: Set<String>
        let sourceSHA256: String
        let version: String
    }
    /// Removal from `grants` is revocation; a stale grant is never reactivated.
    private struct Grant { let installationID: String; var generation: Int? }

    private let factory: @Sendable () throws -> DocumentStore
    private let getConnection: @Sendable () async throws -> RuntimeBridgeConnection
    private let download: MusicScriptDownload
    private let now: @Sendable () -> Date
    private var document: DocumentStore?
    private var connection: RuntimeBridgeConnection?
    private var connecting: Task<RuntimeBridgeConnection, Error>?
    private var runtimeFailed = false
    private var closed = false
    private var closing: Task<Void, Error>?
    private var cleanupPending: Set<String> = []
    private var invocations: [UUID: Task<[String: JSONValue], Error>] = [:]
    private var downloads: [UUID: Task<(data: Data, host: String), Error>] = [:]
    private var operations: [UUID: @Sendable () async -> Void] = [:]
    private var live: [String: Live] = [:]
    private var failed: Set<String> = []
    private var revisions: [String: Int] = [:]
    private var grants: [String: Grant] = [:]
    private var busy = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    /// Passive: no directory, document or runtime access until used.
    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore,
                connection: @escaping @Sendable () async throws -> RuntimeBridgeConnection,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        self.init(storeFactory: storeFactory, connection: connection, download: .init(), clock: clock)
    }

    init(storeFactory: @escaping @Sendable () throws -> DocumentStore,
         connection: @escaping @Sendable () async throws -> RuntimeBridgeConnection, download: MusicScriptDownload,
         clock: @escaping @Sendable () -> Date = { Date() }) {
        factory = storeFactory; getConnection = connection; self.download = download; now = clock
    }

    /// Revokes grants, drains accepted music work and unloads music instances.
    /// The borrowed connection remains available to other modules.
    public func close() async throws {
        if let closing { return try await closing.value }
        closed = true
        grants.removeAll()
        connecting?.cancel()
        let calls = Array(invocations.values)
        let fetches = Array(downloads.values)
        let mutations = Array(operations.values)
        for call in calls { call.cancel() }
        for fetch in fetches { fetch.cancel() }
        let task = Task {
            for call in calls { _ = await call.result }
            for fetch in fetches { _ = await fetch.result }
            _ = await self.connecting?.result
            for mutation in mutations { await mutation() }
            await self.acquire()
            defer { self.releaseLock() }
            // An in-flight load reconciles its late receipt before releasing
            // the operation lock; queued operations reject the closed owner.
            var cleanupFailed = false
            for id in Array(self.live.keys) {
                if !(await self.unload(id)) { cleanupFailed = true }
            }
            if let connection = self.connection {
                let transport = MusicScriptTransport(connection: connection)
                for id in self.cleanupPending {
                    if !(await self.reconcile(id, transport: transport)) { cleanupFailed = true }
                }
            }
            self.connection = nil
            self.connecting = nil
            self.cleanupPending.removeAll()
            try musicRequire(!cleanupFailed, .scriptCleanupFailed)
        }
        closing = task
        try await task.value
    }

    // MARK: Management

    public func scriptSources() async throws -> [MusicScriptSourceItem] {
        try musicRequire(!closed, .closed)
        observeRuntimeExit()
        return try readCatalog().installations.map(item)
    }

    public func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem {
        try musicRequire(fileName.lowercased().hasSuffix(".js"), .invalidScript)
        let inspection = try MusicScriptHeader.inspect(data, fileName: fileName)
        return try await owned { try await self.install(data, inspection: inspection, origin: "file", host: "") }
    }

    public func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem {
        try musicRequire(!closed, .closed)
        _ = try MusicScriptDownload.validate(url)
        try musicRequire(url.lastPathComponent.lowercased().hasSuffix(".js"), .invalidScript)
        let id = UUID()
        let task = Task { [download] in try await download.fetch(url) }
        downloads[id] = task
        defer { downloads[id] = nil }
        let fetched = try await withTaskCancellationHandler {
            try await task.value
        } onCancel: { task.cancel() }
        try musicRequire(!closed, .closed)
        let last = url.lastPathComponent
        let inspection = try MusicScriptHeader.inspect(fetched.data,
            fileName: last.isEmpty || last == "/" ? fetched.host : last)
        return try await owned { try await self.install(fetched.data, inspection: inspection, origin: "network", host: fetched.host) }
    }

    public func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        try await owned { try await self.changeEnabled(id, enabled: enabled) }
    }

    public func removeScriptSource(_ id: String) async throws {
        try await owned { try await self.remove(id) }
    }

    // MARK: MusicScriptResolving

    public func sources() async throws -> [MusicScriptSource] {
        try await owned { try await self.activateEnabled() }
    }

    public func source(installationID: String) async throws -> MusicScriptSource {
        try musicRequire(!closed, .closed)
        observeRuntimeExit()
        if let current = live[installationID] { return snapshot(installationID, current) }
        return try await owned { try await self.activateOne(installationID) }
    }

    public func resolve(request: MusicScriptRequest) async throws -> MusicNativeAudio {
        let value = try await invoke(request, operation: "musicUrl", payload: [
            "type": .string(request.quality), "musicInfo": try Self.musicInfo(request.musicInfo),
        ])
        guard case .string(let text) = value else { throw MusicServiceError.invalidMediaCandidate }
        return .init(url: try Self.mediaURL(text), fileCachePolicy: .denied)
    }

    public func lyrics(request: MusicScriptRequest) async throws -> MusicNativeLyrics {
        let value = try await invoke(request, operation: "lyric", payload: ["musicInfo": try Self.musicInfo(request.musicInfo)])
        guard case .object(let fields) = value, case .string(let text)? = fields["lyric"] else {
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

    // MARK: Owned operations

    /// Mutations are serialized and run to completion. Caller cancellation does
    /// not abandon a started load or CAS; the UI separately fences late results.
    private func owned<T: Sendable>(_ body: @escaping @Sendable () async throws -> T) async throws -> T {
        try musicRequire(!closed, .closed)
        let id = UUID()
        let task = Task { () async throws -> T in
            await self.acquire()
            do { let value = try await body(); self.releaseLock(); return value }
            catch { self.releaseLock(); throw error }
        }
        operations[id] = { _ = await task.result }
        defer { operations[id] = nil }
        return try await task.value
    }

    private func acquire() async {
        if !busy { busy = true; return }
        await withCheckedContinuation { waiters.append($0) }
    }

    private func releaseLock() {
        if waiters.isEmpty { busy = false } else { waiters.removeFirst().resume() }
    }

    private func install(_ data: Data, inspection: MusicScriptHeader.Inspection, origin: String,
                         host: String) async throws -> MusicScriptSourceItem {
        try musicRequire(!closed, .closed)
        var catalog = try readCatalog()
        let key = Self.sourceKey(inspection.sha256)
        do {
            // Content-addressed and immutable: an identical existing document is reused.
            if try store().read(key: key) != data { try store().write(data, key: key) }
        } catch { throw MusicNativeLibraryDocument.failure(error) }
        let installation = MusicScriptCatalog.Installation(
            id: UUID().uuidString.lowercased(), name: inspection.name, version: inspection.version,
            author: inspection.author, summary: inspection.summary, homepage: inspection.homepage,
            origin: origin, originHost: host, sourceSHA256: inspection.sha256, sourceBytes: inspection.bytes,
            enabled: false, sites: [], installedAt: now().timeIntervalSince1970 * 1000)
        catalog.installations.append(installation)
        try writeCatalog(&catalog)
        return item(installation)
    }

    private func activateOne(_ id: String) async throws -> MusicScriptSource {
        try musicRequire(!closed, .closed)
        if let current = live[id] { return snapshot(id, current) }
        guard let item = try readCatalog().installations.first(where: { $0.id == id }), item.enabled,
              !failed.contains(id) else { throw MusicServiceError.sourceUnavailable }
        do { return snapshot(id, try await activate(item)) }
        catch { throw MusicServiceError.sourceUnavailable }
    }

    private func changeEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem {
        try musicRequire(!closed, .closed)
        observeRuntimeExit()
        var catalog = try readCatalog()
        guard let index = catalog.installations.firstIndex(where: { $0.id == id }) else { throw MusicServiceError.notFound }
        if enabled {
            failed.remove(id)
            let current: Live
            if let value = live[id] { current = value } else { current = try await activate(catalog.installations[index]) }
            try musicRequire(!closed, .closed)
            catalog.installations[index].enabled = true
            catalog.installations[index].sites = current.capabilities.map(\.siteID)
            do { try writeCatalog(&catalog) }
            catch {
                // Never leave an executing instance behind an unpersisted enable.
                _ = await unload(id)
                throw error
            }
        } else {
            let released = await unload(id)
            try musicRequire(!closed, .closed)
            failed.remove(id)
            catalog.installations[index].enabled = false
            try writeCatalog(&catalog)
            try musicRequire(released, .scriptCleanupFailed)
        }
        return item(catalog.installations[index])
    }

    private func remove(_ id: String) async throws {
        try musicRequire(!closed, .closed)
        var catalog = try readCatalog()
        guard let index = catalog.installations.firstIndex(where: { $0.id == id }) else { throw MusicServiceError.notFound }
        let removed = catalog.installations[index]
        let released = await unload(id)
        try musicRequire(!closed, .closed)
        // Private data goes first: a removed installation never leaves it behind.
        do { try store().remove(key: Self.storageKey(id)) }
        catch { throw MusicNativeLibraryDocument.failure(error) }
        catalog.installations.remove(at: index)
        try writeCatalog(&catalog)
        failed.remove(id); revisions[id] = nil
        if !catalog.installations.contains(where: { $0.sourceSHA256 == removed.sourceSHA256 }) {
            try? store().remove(key: Self.sourceKey(removed.sourceSHA256))
        }
        try musicRequire(released, .scriptCleanupFailed)
    }

    private func activateEnabled() async throws -> [MusicScriptSource] {
        try musicRequire(!closed, .closed)
        observeRuntimeExit()
        var catalog = try readCatalog()
        var result: [MusicScriptSource] = [], unavailable = false, changed = false
        for (index, item) in catalog.installations.enumerated() where item.enabled {
            if let current = live[item.id] { result.append(snapshot(item.id, current)); continue }
            guard !failed.contains(item.id), !unavailable else { continue }
            do {
                let current = try await activate(item)
                result.append(snapshot(item.id, current))
                let sites = current.capabilities.map(\.siteID)
                if catalog.installations[index].sites != sites { catalog.installations[index].sites = sites; changed = true }
            } catch MusicServiceError.scriptRuntimeUnavailable { unavailable = true }
            catch { continue }
        }
        // Declared sites are display metadata; a failed write never fails playback.
        try musicRequire(!closed, .closed)
        if changed { try? writeCatalog(&catalog) }
        if result.isEmpty && (unavailable || runtimeFailed) &&
            catalog.installations.contains(where: \.enabled) { throw MusicServiceError.sourceUnavailable }
        return result
    }

    // MARK: Runtime instances

    private func activate(_ item: MusicScriptCatalog.Installation) async throws -> Live {
        let transport = try await runtime()
        try musicRequire(!closed, .closed)
        let bytes: Data
        do {
            guard let data = try store().read(key: Self.sourceKey(item.sourceSHA256)), data.count == item.sourceBytes,
                  musicScriptSHA256(data) == item.sourceSHA256 else { throw MusicServiceError.corruptDocument }
            bytes = data
        } catch let error as MusicServiceError { throw error }
        catch { throw MusicNativeLibraryDocument.failure(error) }
        let revision = (revisions[item.id] ?? 0) + 1
        revisions[item.id] = revision
        let grant = UUID().uuidString.lowercased()
        grants[grant] = Grant(installationID: item.id, generation: nil)
        var token: String?
        do {
            token = try await transport.stage(bytes)
            let reply = try await transport.call("load", [
                "token": .string(token!), "installationId": .string(item.id), "revision": .number(Double(revision)),
                "expectedRevision": .number(0), "version": .string(item.version), "config": .object([:]),
                "permissions": .object(["storage": .array([.string("read"), .string("write")])]),
                "grant": .string(grant),
            ], timeout: 30)
            await transport.release("stage.abort", token!)
            try musicRequire(!closed && !transport.connection.isClosed, .scriptRuntimeUnavailable)
            let current = try Self.instance(reply, item: item, revision: revision, grant: grant)
            try musicRequire(grants[grant]?.generation == nil || grants[grant]?.generation == current.generation,
                             .scriptFailed)
            grants[grant]?.generation = current.generation
            live[item.id] = current
            cleanupPending.remove(item.id)
            return current
        } catch {
            grants[grant] = nil
            if let token { await transport.release("stage.abort", token) }
            // A lost load receipt may still have published a binding.
            let reconciled = await reconcile(item.id, transport: transport)
            if !reconciled { cleanupPending.insert(item.id) }
            else { cleanupPending.remove(item.id) }
            failed.insert(item.id)
            throw Self.loadFailure(error)
        }
    }

    private static func instance(_ reply: [String: JSONValue], item: MusicScriptCatalog.Installation,
                                 revision: Int, grant: String) throws -> Live {
        guard case .object(let value)? = reply["instance"], value["installationId"] == .string(item.id),
              value["revision"]?.scriptInteger == revision,
              let generation = value["generation"]?.scriptInteger, generation > 0,
              value["state"] == .string("ready"), value["protocol"] == .string("lx"),
              value["contentHash"] == .string(item.sourceSHA256), value["version"] == .string(item.version),
              case .object(let declared)? = value["capabilities"], declared.count <= 32 else {
            throw MusicServiceError.invalidResponse
        }
        var capabilities: [MusicScriptCapability] = []
        for site in MusicScriptCatalog.platforms {
            guard let entry = declared[site] else { continue }
            guard case .object(let fields) = entry, case .array(let actions)? = fields["actions"],
                  case .array(let qualities)? = fields["qualitys"], actions.count <= 32, qualities.count <= 32 else {
                throw MusicServiceError.invalidResponse
            }
            let actionNames = actions.compactMap(\.scriptText), qualityNames = qualities.compactMap(\.scriptText)
            guard actionNames.count == actions.count, qualityNames.count == qualities.count,
                  Set(actionNames).count == actionNames.count, Set(qualityNames).count == qualityNames.count else {
                throw MusicServiceError.invalidResponse
            }
            capabilities.append(.init(siteID: site, title: item.name, actions: actionNames, qualities: qualityNames))
        }
        return Live(revision: revision, generation: generation, grant: grant, capabilities: capabilities,
                    declaredSites: Set(declared.keys), sourceSHA256: item.sourceSHA256, version: item.version)
    }

    /// Unloads and revokes the current grant. false: cleanup was not confirmed.
    private func unload(_ id: String) async -> Bool {
        guard let current = live.removeValue(forKey: id) else { return true }
        grants[current.grant] = nil
        guard let connection, !connection.isClosed else { return true }
        let transport = MusicScriptTransport(connection: connection)
        do {
            let reply = try await transport.call("unload", [
                "installationId": .string(id), "revision": .number(Double(current.revision)),
                "generation": .number(Double(current.generation)),
            ])
            try musicRequire(reply["unloaded"] == .bool(true) && reply["cleanupComplete"] == .bool(true), .scriptCleanupFailed)
            cleanupPending.remove(id)
            return true
        } catch {
            if connection.isClosed { return true }
            if !(await reconcile(id, transport: transport)) { cleanupPending.insert(id) }
            return false
        }
    }

    /// Retires any runtime binding for `id` that native state does not own.
    @discardableResult
    private func reconcile(_ id: String, transport: MusicScriptTransport) async -> Bool {
        if transport.connection.isClosed { return true }
        guard let status = try? await transport.call("status", [:]),
              case .array(let instances)? = status["instances"] else { return transport.connection.isClosed }
        var complete = true
        for case .object(let instance) in instances where instance["installationId"] == .string(id) {
            guard let revision = instance["revision"]?.scriptInteger,
                  let generation = instance["generation"]?.scriptInteger else { complete = false; continue }
            let reply = try? await transport.call("unload", [
                "installationId": .string(id), "revision": .number(Double(revision)),
                "generation": .number(Double(generation)),
            ])
            if reply?["unloaded"] != .bool(true) || reply?["cleanupComplete"] != .bool(true) { complete = false }
        }
        return complete || transport.connection.isClosed
    }

    private func invoke(_ request: MusicScriptRequest, operation: String,
                        payload: [String: JSONValue]) async throws -> JSONValue {
        try musicRequire(!closed, .closed)
        guard let current = live[request.installationID], current.generation == request.generation,
              current.capabilities.contains(where: { $0.siteID == request.siteID && $0.actions.contains(operation) }),
              let connection, !connection.isClosed else { throw MusicServiceError.sourceUnavailable }
        let reply: [String: JSONValue]
        let id = UUID()
        let task = Task {
            try await MusicScriptTransport(connection: connection).call("invoke", [
                "installationId": .string(request.installationID), "revision": .number(Double(current.revision)),
                "generation": .number(Double(current.generation)), "siteId": .string(request.siteID),
                "operation": .string(operation), "payload": .object(payload),
            ])
        }
        invocations[id] = task
        defer { invocations[id] = nil }
        do {
            reply = try await withTaskCancellationHandler {
                try await task.value
            } onCancel: { task.cancel() }
        } catch { throw Self.invokeFailure(error) }
        guard !closed, live[request.installationID]?.generation == request.generation else { throw MusicServiceError.sourceUnavailable }
        guard let value = reply["value"] else { throw MusicServiceError.invalidResponse }
        return value
    }

    private func runtime() async throws -> MusicScriptTransport {
        try musicRequire(!runtimeFailed && !closed, .scriptRuntimeUnavailable)
        if let connection, !connection.isClosed { return MusicScriptTransport(connection: connection) }
        let task = connecting ?? Task { [getConnection] in
            let connection = try await getConnection()
            let described = try await connection.call("runtime.describe", .null, timeout: 60)
            guard described["protocol"]?.scriptInteger == 1, case .array(let modules)? = described["modules"],
                  modules.contains(where: { $0["id"] == .string("music") && $0["version"] == .string("0.7.0") }) else {
                throw MusicServiceError.scriptRuntimeUnavailable
            }
            return connection
        }
        connecting = task
        do {
            let value = try await task.value
            guard !closed, !value.isClosed else { throw MusicServiceError.closed }
            if connection !== value {
                connection = value
                value.onClose { [weak self, weak value] in
                    guard let value else { return }
                    Task { await self?.runtimeClosed(value) }
                }
            }
            return MusicScriptTransport(connection: value)
        } catch {
            // The embedded runtime starts once per process; a failed start is final.
            runtimeFailed = true
            throw MusicServiceError.scriptRuntimeUnavailable
        }
    }

    private func runtimeClosed(_ value: RuntimeBridgeConnection) {
        guard connection === value else { return }
        runtimeFailed = true
        live.removeAll(); grants.removeAll()
    }

    /// EOF is synchronous on the bridge; do not wait for its actor callback to
    /// retire cached live snapshots if a caller arrives first.
    private func observeRuntimeExit() {
        if let connection, connection.isClosed { runtimeClosed(connection) }
    }

    // MARK: Request-bound storage (runtime → native)

    public nonisolated func storageHandlers() -> [String: RuntimeBridgeConnection.Handler] {
        var handlers: [String: RuntimeBridgeConnection.Handler] = [:]
        for operation in ["get", "set", "delete"] {
            handlers["platform.musicScript.storage." + operation] = { [weak self] params in
                guard let self else { return Self.storageReply(code: "CLOSED") }
                return await self.storage(operation, params)
            }
        }
        return handlers
    }

    private func storage(_ operation: String, _ params: JSONValue) -> JSONValue {
        guard !closed else { return Self.storageReply(code: "CLOSED") }
        guard !Task.isCancelled else { return Self.storageReply(code: "CANCELLED") }
        guard case .object(let fields) = params,
              Set(fields.keys) == Set(["grant", "installationId", "siteId", "generation", "key"] + (operation == "set" ? ["value"] : [])),
              let grantID = fields["grant"]?.scriptText, let id = fields["installationId"]?.scriptText,
              let site = fields["siteId"]?.scriptText, let generation = fields["generation"]?.scriptInteger, generation > 0,
              let key = fields["key"]?.scriptText, !key.isEmpty, key.utf16.count <= 256,
              site.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", options: .regularExpression) != nil else {
            return Self.storageReply(code: "PERMISSION_DENIED")
        }
        guard var grant = grants[grantID], grant.installationID == id,
              grant.generation == nil || grant.generation == generation else { return Self.storageReply(code: "STALE_SOURCE") }
        if let current = live[id] {
            guard current.grant == grantID, current.declaredSites.contains(site) else {
                return Self.storageReply(code: "PERMISSION_DENIED")
            }
        }
        grant.generation = generation; grants[grantID] = grant
        do {
            let documentKey = Self.storageKey(id)
            var value = try MusicScriptStorage.decode(try store().read(key: documentKey))
            if operation == "get" { return .object(["ok": .bool(true), "value": value.value(site: site, key: key)]) }
            try value.set(site: site, key: key, value: operation == "set" ? fields["value"] : nil)
            try store().write(try value.encoded(), key: documentKey)
            return .object(["ok": .bool(true), "value": .null])
        } catch {
            return Self.storageReply(code: MusicServiceError.sanitized(error) == .limit ? "LIMIT" : "STORAGE_ERROR")
        }
    }

    private static func storageReply(code: String) -> JSONValue {
        .object(["ok": .bool(false), "code": .string(code)])
    }

    // MARK: Documents

    private func store() throws -> DocumentStore {
        if let document { return document }
        do { let value = try factory(); document = value; return value }
        catch { throw MusicNativeLibraryDocument.failure(error) }
    }

    private func readCatalog() throws -> MusicScriptCatalog {
        let data: Data?
        do { data = try store().read(key: Self.catalogKey) }
        catch { throw MusicNativeLibraryDocument.failure(error) }
        return try MusicScriptCatalog.decode(data)
    }

    /// The single writer runs under the owned-operation lock.
    private func writeCatalog(_ catalog: inout MusicScriptCatalog) throws {
        try musicRequire(catalog.revision < 9_007_199_254_740_991, .limit)
        var next = catalog
        next.revision += 1
        let data = try next.encoded()
        do { try store().write(data, key: Self.catalogKey) }
        catch { throw MusicNativeLibraryDocument.failure(error) }
        catalog = next
    }

    private static func sourceKey(_ sha256: String) -> String { "source." + sha256 }
    private static func storageKey(_ id: String) -> String { "storage." + id }

    // MARK: Projection

    private func item(_ value: MusicScriptCatalog.Installation) -> MusicScriptSourceItem {
        let state: MusicScriptSourceItem.State
        if !value.enabled { state = .disabled }
        else if live[value.id] != nil { state = .ready }
        else if runtimeFailed || failed.contains(value.id) { state = .failed }
        else { state = .idle }
        return .init(id: value.id, name: value.name, version: value.version, author: value.author,
                     summary: value.summary, homepage: value.homepage,
                     origin: value.origin == "network" ? .network(host: value.originHost) : .file,
                     platforms: value.sites.compactMap(MusicPublicBrowsePlatform.init(rawValue:)),
                     enabled: value.enabled, state: state)
    }

    private func snapshot(_ id: String, _ current: Live) -> MusicScriptSource {
        let identity = [id, String(current.revision), String(current.generation), current.sourceSHA256, current.version]
        return .init(installationID: id, generation: current.generation, enabled: true, ready: true,
                     version: current.version, contentHash: current.sourceSHA256,
                     revisionHash: musicScriptSHA256(Data(identity.joined(separator: "\n").utf8)),
                     capabilities: current.capabilities)
    }

    // MARK: Validation and failures

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

    private static func loadFailure(_ error: Error) -> MusicServiceError {
        if let error = error as? MusicServiceError {
            return [.scriptRuntimeUnavailable, .corruptDocument, .accessUnavailable, .storageFailed].contains(error)
                ? error : .scriptFailed
        }
        if let error = error as? RuntimeBridgeError {
            return error == .closed ? .scriptRuntimeUnavailable : error == .timeout ? .timeout : .scriptFailed
        }
        if let error = error as? MusicScriptCallError {
            switch error.code {
            case "SCRIPT_ENCODING", "SCRIPT_INTEGRITY", "LIMIT": return .invalidScript
            case "TIMEOUT": return .timeout
            case "CLOSED": return .scriptRuntimeUnavailable
            default: return .scriptFailed
            }
        }
        return .scriptFailed
    }

    private static func invokeFailure(_ error: Error) -> MusicServiceError {
        if Task.isCancelled { return .cancelled }
        if let error = error as? MusicServiceError { return error }
        if let error = error as? RuntimeBridgeError {
            switch error {
            case .cancelled: return .cancelled
            case .timeout: return .timeout
            case .closed: return .sourceUnavailable
            default: return .resolutionFailed
            }
        }
        if let error = error as? MusicScriptCallError {
            switch error.code {
            case "NETWORK_ERROR", "UPSTREAM_HTTP": return .upstreamFailed
            case "UPSTREAM_FORMAT": return .upstreamInvalid
            case "TIMEOUT": return .timeout
            case "CANCELLED": return .cancelled
            case "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED", "CAPABILITY_UNAVAILABLE",
                 "PERMISSION_DENIED", "CLOSED", "SCRIPT_CLEANUP": return .sourceUnavailable
            case "LIMIT": return .limit
            case "INVALID_REQUEST": return .invalidRequest
            default: return .resolutionFailed
            }
        }
        return .resolutionFailed
    }
}

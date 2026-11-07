import Foundation
import MeowJSON
import MeowRuntime
import MeowStorage

/// App-scoped owner. Local state works before Node is available; all durable
/// mutations commit before publishing a snapshot. The runtime is borrowed.
public actor FilmSourceService {
    public static let documentNamespace = "film.video-sources.v1"
    public static let documentKey = "catalog"
    let transport: CatScriptTransport
    private let storeFactory: @Sendable () throws -> DocumentStore
    private var store: DocumentStore?
    private var expected: Data?
    private var document = FilmSourceDocument()
    // A fresh receipt for each durable deletion prevents restore/delete ABA.
    // These receipts live only as long as this owner; the document schema is unchanged.
    private var deletionReceipts: [FilmSourceKey: UUID] = [:]
    private let readSourceFile: @Sendable (URL) async throws -> String
    private var initialized = false
    private var closed = false
    private var preparing = false
    private var active: [UUID: @Sendable () -> Void] = [:]
    private var drains: [CheckedContinuation<Void, Never>] = []
    private var managementBusy = false
    private var managementWaiters: [CheckedContinuation<Void, Never>] = []
    // Read-only scheduling observation for deterministic concurrency tests.
    var queuedManagementCount: Int { managementWaiters.count }
    private var changingCats: Set<String> = []
    private var probes: [FilmSourceKey: (UUID, Task<FilmSourceDetection, Error>)] = [:]
    private var observers: [UUID: AsyncStream<FilmSourceSnapshot>.Continuation] = [:]
    public private(set) var snapshot = FilmSourceSnapshot(cats: [], videos: [], runtimeError: nil)

    public init(transport: CatScriptTransport, storeFactory: @escaping @Sendable () throws -> DocumentStore) {
        self.transport = transport; self.storeFactory = storeFactory
        self.readSourceFile = { url in try await Self.readSource(url) }
    }

    init(transport: CatScriptTransport, storeFactory: @escaping @Sendable () throws -> DocumentStore,
         readSourceFile: @escaping @Sendable (URL) async throws -> String) {
        self.transport = transport; self.storeFactory = storeFactory; self.readSourceFile = readSourceFile
    }

    public func snapshots() -> AsyncStream<FilmSourceSnapshot> {
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            if closed { continuation.finish(); return }
            observers[id] = continuation
            continuation.yield(snapshot)
            continuation.onTermination = { [weak self] _ in Task { await self?.removeObserver(id) } }
        }
    }

    private func removeObserver(_ id: UUID) { observers[id] = nil }
    private func publish(_ value: FilmSourceSnapshot) {
        guard snapshot != value else { return }
        snapshot = value
        for observer in observers.values { observer.yield(value) }
    }

    public func initialize() throws {
        try available()
        try load()
    }

    private func load() throws {
        if initialized { return }
        if store == nil { store = try storeFactory() }
        let data = try store!.read(key: Self.documentKey)
        let loaded: FilmSourceDocument
        do { loaded = try data.map { try JSONDecoder().decode(FilmSourceDocument.self, from: $0) } ?? FilmSourceDocument() }
        catch { throw FilmSourceError(code: "INVALID_DOCUMENT") }
        try loaded.validate()
        cancelDetections()
        deletionReceipts = Dictionary(uniqueKeysWithValues: Set(loaded.deleted).map { ($0, UUID()) })
        document = loaded; expected = data; initialized = true
        publish(.init(cats: loaded.cats.map(\.snapshot), videos: sorted(loaded.videos), runtimeError: snapshot.runtimeError))
    }

    private func commit(cats: [FilmCatSource], videos: [FilmVideoSource], deleted: Set<FilmSourceKey>) throws {
        try Task.checkCancellation()
        let candidate = FilmSourceDocument(cats: cats.map(StoredFilmCat.init), videos: videos,
            deleted: deleted.sorted(by: FilmSourceKey.ordered))
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(candidate)
        if bytes != expected {
            do { try store!.compareAndSwap(key: Self.documentKey, expected: expected, replacement: bytes) }
            catch {
                // Resolve a possibly committed receipt without claiming success on failure.
                guard let actual = try? store!.read(key: Self.documentKey), actual == bytes else {
                    initialized = false
                    throw error
                }
            }
        }
        deletionReceipts = Dictionary(uniqueKeysWithValues: deleted.map { ($0, deletionReceipts[$0] ?? UUID()) })
        document = candidate; expected = bytes; initialized = true
        publish(.init(cats: cats, videos: videos, runtimeError: snapshot.runtimeError))
    }

    public func start() async throws {
        try await run {
            do {
                try await self.managed {
                    _ = try await self.transport.call("start")
                    try await self.synchronize()
                }
                while await self.snapshot.cats.contains(where: { $0.state == "starting" }) {
                    try await Task.sleep(nanoseconds: 250_000_000)
                    try await self.managed { try await self.synchronize() }
                }
            } catch {
                if !(error is CancellationError), !Task.isCancelled { await self.reportRuntime(error) }
                throw error
            }
        }
    }

    public func refresh() async throws {
        try await run { try await self.managed { try await self.synchronize() } }
    }

    public func importLocal(name: String, fileURL: URL, installationId: String? = nil) async throws -> FilmCatSource {
        let id = installationId ?? UUID().uuidString.lowercased()
        let restoreReceipts = try captureRestoreReceipts(id)
        return try await run {
            let source = try await self.readSourceFile(fileURL)
            return try await self.importLocalCore(name: name, source: source, id: id, restoreReceipts: restoreReceipts)
        }
    }

    public func importLocal(name: String, source: String, installationId: String? = nil) async throws -> FilmCatSource {
        let id = installationId ?? UUID().uuidString.lowercased()
        let restoreReceipts = try captureRestoreReceipts(id)
        return try await run {
            try await self.importLocalCore(name: name, source: source, id: id, restoreReceipts: restoreReceipts)
        }
    }

    private func importLocalCore(name: String, source: String, id: String,
                                 restoreReceipts: [FilmSourceKey: UUID]) async throws -> FilmCatSource {
        try nonempty(name); try nonempty(source)
        return try await manage(id, reimport: true, restoreReceipts: restoreReceipts) {
            try await self.transport.withSource(source, operation: "install", params: [
                "installationId": .string(id), "name": .string(name), "sourceType": .string("local"),
                "sourceUrl": .null, "enabled": .bool(true)])
        }
    }

    public func importNetwork(name: String, sourceURL: String, installationId: String? = nil) async throws -> FilmCatSource {
        try nonempty(name); try nonempty(sourceURL)
        let id = installationId ?? UUID().uuidString.lowercased()
        let restoreReceipts = try captureRestoreReceipts(id)
        return try await run {
            try await self.manage(id, reimport: true, restoreReceipts: restoreReceipts) {
                try await self.transport.call("import", ["installationId": .string(id), "name": .string(name),
                    "sourceUrl": .string(sourceURL), "enabled": .bool(true)])
            }
        }
    }

    private func captureRestoreReceipts(_ id: String) throws -> [FilmSourceKey: UUID] {
        // No await between reading local state and capturing the import's eligibility:
        // neither file I/O nor waiting for another management command may move this boundary.
        try available(); try Task.checkCancellation(); try load()
        return deletionReceipts.filter { $0.key.installationId == id }
    }

    public func editCat(_ installationId: String, name: String, sourceURL: String? = nil) async throws -> FilmCatSource {
        try nonempty(name)
        var body: [String: JSONValue] = ["installationId": .string(installationId), "name": .string(name)]
        if let sourceURL { body["sourceUrl"] = .string(sourceURL) }
        let params = body
        return try await run { try await self.manage(installationId) { try await self.transport.call("edit", params) } }
    }

    public func updateCat(_ installationId: String, fileURL: URL? = nil) async throws -> FilmCatSource {
        try await run {
            let source: String?
            if let fileURL { source = try await Self.readSource(fileURL) } else { source = nil }
            return try await self.updateCore(installationId, source: source)
        }
    }

    public func updateCat(_ installationId: String, source: String?) async throws -> FilmCatSource {
        try await run { try await self.updateCore(installationId, source: source) }
    }

    private func updateCore(_ id: String, source: String?) async throws -> FilmCatSource {
        try await manage(id) {
            let cat = try await self.findCat(id)
            if cat.sourceType == "local" {
                guard let source else { throw FilmSourceError(code: "INVALID_SOURCE") }
                try await self.nonempty(source)
                return try await self.transport.withSource(source, operation: "update", params: ["installationId": .string(id)])
            }
            guard source == nil else { throw FilmSourceError(code: "INVALID_SOURCE") }
            return try await self.transport.call("update", ["installationId": .string(id)])
        }
    }

    public func restartCat(_ installationId: String) async throws -> FilmCatSource {
        try await run {
            try await self.manage(installationId) {
                guard try await self.findCat(installationId).enabled else { throw FilmSourceError(code: "DISABLED") }
                return try await self.transport.call("restart", ["installationId": .string(installationId)])
            }
        }
    }

    public func enableCat(_ installationId: String, enabled: Bool) async throws -> FilmCatSource {
        try await run {
            try await self.manage(installationId) {
                try await self.transport.call("enable", ["installationId": .string(installationId), "enabled": .bool(enabled)])
            }
        }
    }

    private func manage(_ id: String, reimport: Bool = false, restoreReceipts: [FilmSourceKey: UUID] = [:],
                        operation: @escaping @Sendable () async throws -> JSONValue) async throws -> FilmCatSource {
        try nonempty(id)
        return try await managed {
            try await self.manageCore(id, reimport: reimport, restoreReceipts: restoreReceipts, operation: operation)
        }
    }

    private func manageCore(_ id: String, reimport: Bool, restoreReceipts: [FilmSourceKey: UUID],
                            operation: @Sendable () async throws -> JSONValue) async throws -> FilmCatSource {
        if !reimport { _ = try findCat(id) }
        invalidateCat(id); changingCats.insert(id)
        defer { changingCats.remove(id) }
        let receipt: JSONValue
        do { receipt = try await operation() }
        catch {
            if !(error is CancellationError), !Task.isCancelled { try? await synchronize() }
            throw error
        }
        try Task.checkCancellation()
        var restored: FilmCatSource?
        if reimport {
            let instance = receipt.catField("instances")?.catArray?.first { $0.catField("installationId") == .string(id) } ?? receipt
            let imported = try parseFilmCat(instance)
            guard imported.installationId == id else { throw FilmSourceError(code: "INVALID_RESPONSE") }
            if imported.state == "ready" && imported.enabled { restored = imported }
        }
        try await synchronize(restored: restored, restoreReceipts: restoreReceipts)
        let result = try findCat(id)
        if reimport && restored == nil { throw FilmSourceError(code: "IMPORT_FAILED") }
        return result
    }

    public func deleteCat(_ installationId: String) async throws {
        try await run { try await self.managed { try await self.deleteCatCore(installationId) } }
    }

    private func deleteCatCore(_ id: String) async throws {
        _ = try findCat(id); invalidateCat(id); changingCats.insert(id)
        defer { changingCats.remove(id) }
        let receipt: JSONValue
        do { receipt = try await transport.call("uninstall", ["installationId": .string(id)]) }
        catch {
            if !(error is CancellationError), !Task.isCancelled { try? await synchronize() }
            throw error
        }
        try Task.checkCancellation()
        try commit(cats: snapshot.cats.filter { $0.installationId != id },
            videos: snapshot.videos.filter { $0.key.installationId != id },
            deleted: Set(document.deleted.filter { $0.installationId != id }))
        let failure = receipt.catField("failures")?.catArray?.first { $0.catField("installationId") == .string(id) }
        try await synchronize()
        if let failure { throw FilmSourceError(code: failure.catField("code")?.catText ?? "INVALID_RESPONSE") }
    }

    public func deleteVideo(_ key: FilmSourceKey) throws {
        try initialize(); _ = try findVideo(key); cancelDetection(key)
        var deleted = Set(document.deleted); deleted.insert(key)
        try commit(cats: snapshot.cats, videos: snapshot.videos.filter { $0.key != key }, deleted: deleted)
    }

    public func saveVideoOptions(_ key: FilmSourceKey, options: FilmVideoOptions, order: Int? = nil) throws {
        try initialize(); _ = try findVideo(key)
        var videos = snapshot.videos.map { original in
            var video = original
            if video.key == key { video.options = options; video.order = order ?? video.order }
            else if options.aggregateImages { video.options.aggregateImages = false }
            return video
        }
        videos.sort { a, b in
            if a.order != b.order { return a.order < b.order }
            return order != nil && a.key == key && b.key != key
        }
        try commit(cats: snapshot.cats, videos: videos, deleted: Set(document.deleted))
    }

    private func synchronize(restored: FilmCatSource? = nil, restoreReceipts: [FilmSourceKey: UUID] = [:]) async throws {
        let incoming: [FilmCatSource]
        do {
            let root = try await transport.call("status")
            guard let values = root.catField("instances")?.catArray else { throw FilmSourceError(code: "INVALID_RESPONSE") }
            incoming = try values.map(parseFilmCat)
            guard Set(incoming.map(\.installationId)).count == incoming.count else { throw FilmSourceError(code: "INVALID_RESPONSE") }
        } catch {
            if !(error is CancellationError), !Task.isCancelled { reportRuntime(error) }
            throw error
        }
        try Task.checkCancellation()
        let ids = Set(incoming.map(\.installationId))
        var deleted = Set(document.deleted.filter { ids.contains($0.installationId) })
        var videos = snapshot.videos.filter { ids.contains($0.key.installationId) }
        for previous in snapshot.cats where !ids.contains(previous.installationId) { invalidateCat(previous.installationId) }
        var cats: [FilmCatSource] = []
        for received in incoming {
            let previous = snapshot.cats.first { $0.installationId == received.installationId }
            var cat = received
            if !received.canDetect, let previous { cat.sites = previous.sites }
            if let previous, previous.generation != cat.generation || previous.state != cat.state ||
                previous.enabled != cat.enabled || previous.sourceHash != cat.sourceHash { invalidateCat(cat.installationId) }
            cats.append(cat)
            guard received.canDetect else { continue }
            let keys = Set(cat.sites.map { $0.identity(installationId: cat.installationId) })
            if let restored, restored.installationId == cat.installationId,
               restored.generation == cat.generation, restored.sourceHash == cat.sourceHash {
                let imported = Set(restored.sites.map { $0.identity(installationId: restored.installationId) })
                deleted = deleted.filter { key in
                    guard let receipt = restoreReceipts[key], receipt == deletionReceipts[key] else { return true }
                    return !keys.contains(key) || !imported.contains(key)
                }
            }
            for video in videos where video.key.installationId == cat.installationId && !keys.contains(video.key) {
                cancelDetection(video.key)
            }
            videos.removeAll { $0.key.installationId == cat.installationId && !keys.contains($0.key) }
            for site in cat.sites {
                let key = FilmSourceKey(installationId: cat.installationId, siteKey: site.key)
                if deleted.contains(key) { continue }
                if let index = videos.firstIndex(where: { $0.key == key }) {
                    if videos[index].api != site.api || videos[index].actions != site.actions || videos[index].methods != site.methods {
                        cancelDetection(key)
                    }
                    videos[index].name = site.name; videos[index].api = site.api
                    videos[index].actions = site.actions; videos[index].methods = site.methods
                } else {
                    let highest = videos.map(\.order).max()
                    let order = highest.map { $0 == Int.max ? $0 : $0 + 1 } ?? 0
                    videos.append(.init(key: key, name: site.name, api: site.api, actions: site.actions,
                        methods: site.methods, options: .init(), order: order))
                }
            }
        }
        try commit(cats: cats, videos: sorted(videos), deleted: deleted)
        publish(.init(cats: snapshot.cats, videos: snapshot.videos, runtimeError: nil))
    }

    private func reportRuntime(_ error: Error) {
        cancelDetections()
        let code = (error as? FilmSourceError)?.code ?? "RUNTIME_UNAVAILABLE"
        let cats = snapshot.cats.map { original in
            var cat = original; cat.generation = 0; cat.state = cat.enabled ? "failed" : "disabled"; cat.error = code
            return cat
        }
        publish(.init(cats: cats, videos: snapshot.videos, runtimeError: code))
    }

    public func detect(_ key: FilmSourceKey) async throws -> FilmSourceDetection {
        try await run { try await self.detectCore(key) }
    }

    private func detectCore(_ key: FilmSourceKey) async throws -> FilmSourceDetection {
        let cat = try findCat(key.installationId), video = try findVideo(key)
        guard cat.enabled else { throw FilmSourceError(code: "DISABLED") }
        guard !changingCats.contains(cat.installationId) else { throw FilmSourceError(code: "BUSY") }
        guard cat.canDetect else { throw FilmSourceError(code: "NOT_READY") }
        cancelDetection(key)
        let id = UUID(), transport = transport
        let task = Task { try await FilmSourceProbe(transport: transport, cat: cat, site: video).run() }
        probes[key] = (id, task)
        defer { if probes[key]?.0 == id { probes[key] = nil } }
        let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        return try await managed {
            try await self.finishDetection(result, key: key, id: id, generation: cat.generation)
        }
    }

    private func finishDetection(_ result: FilmSourceDetection, key: FilmSourceKey,
                                 id: UUID, generation: Int64) async throws -> FilmSourceDetection {
        try Task.checkCancellation()
        guard probes[key]?.0 == id else { throw CancellationError() }
        // A completed result blob outlives its script session. Confirm the host
        // after transfer cleanup, serialized with local management, not merely
        // against the cached generation captured before the probe.
        try await synchronize()
        try Task.checkCancellation()
        guard probes[key]?.0 == id, let current = snapshot.cats.first(where: { $0.installationId == key.installationId }),
              current.canDetect, current.generation == generation,
              snapshot.videos.contains(where: { $0.key == key }) else { throw CancellationError() }
        var videos = snapshot.videos
        if let index = videos.firstIndex(where: { $0.key == key }) { videos[index].detection = result }
        try commit(cats: snapshot.cats, videos: videos, deleted: Set(document.deleted))
        return result
    }

    public func cancelDetection(_ key: FilmSourceKey) { probes.removeValue(forKey: key)?.1.cancel() }
    public func cancelDetections() {
        let running = probes.values; probes.removeAll()
        for (_, task) in running { task.cancel() }
    }
    private func invalidateCat(_ id: String) {
        for key in Array(probes.keys) where key.installationId == id { cancelDetection(key) }
    }

    public func prepareShutdown() async {
        preparing = true
        for cancel in active.values { cancel() }
        cancelDetections()
        if !active.isEmpty { await withCheckedContinuation { drains.append($0) } }
    }

    public func resume() throws {
        guard !closed else { throw FilmSourceError(code: "CLOSED") }
        guard active.isEmpty else { throw FilmSourceError(code: "BUSY") }
        preparing = false
    }

    public func clear() async throws {
        await prepareShutdown()
        if store == nil { store = try storeFactory() }
        try store!.remove(key: Self.documentKey)
        document = FilmSourceDocument(); deletionReceipts.removeAll(); expected = nil; initialized = true
        publish(.init(cats: [], videos: [], runtimeError: nil))
    }

    public func close() async {
        closed = true
        await prepareShutdown()
        for observer in observers.values { observer.finish() }
        observers.removeAll()
    }

    private func run<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try available(); try Task.checkCancellation(); try load()
        let id = UUID()
        let task = Task { try Task.checkCancellation(); return try await operation() }
        active[id] = { task.cancel() }
        defer {
            active[id] = nil
            if active.isEmpty { let waiting = drains; drains.removeAll(); waiting.forEach { $0.resume() } }
        }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }

    private func managed<T: Sendable>(_ operation: @Sendable () async throws -> T) async throws -> T {
        if managementBusy { await withCheckedContinuation { managementWaiters.append($0) } }
        else { managementBusy = true }
        defer {
            if managementWaiters.isEmpty { managementBusy = false }
            else { managementWaiters.removeFirst().resume() }
        }
        try Task.checkCancellation()
        return try await operation()
    }

    private func available() throws {
        if closed { throw FilmSourceError(code: "CLOSED") }
        if preparing { throw FilmSourceError(code: "SHUTTING_DOWN") }
    }
    private func findCat(_ id: String) throws -> FilmCatSource {
        guard let cat = snapshot.cats.first(where: { $0.installationId == id }) else { throw FilmSourceError(code: "NOT_FOUND") }
        return cat
    }
    private func findVideo(_ key: FilmSourceKey) throws -> FilmVideoSource {
        guard let video = snapshot.videos.first(where: { $0.key == key }) else { throw FilmSourceError(code: "NOT_FOUND") }
        return video
    }
    private func sorted(_ videos: [FilmVideoSource]) -> [FilmVideoSource] { videos.sorted { $0.order < $1.order } }
    private func nonempty(_ value: String) throws {
        if value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw FilmSourceError(code: "INVALID_REQUEST") }
    }
    private static func readSource(_ url: URL) async throws -> String {
        guard url.pathExtension.lowercased() == "js" else { throw FilmSourceError(code: "INVALID_SOURCE") }
        var bytes = try await ImportedFileReader.read(url, maximumBytes: ImportedFileReader.maximumSupportedBytes)
        defer { bytes.resetBytes(in: 0..<bytes.count) }
        guard let source = String(data: bytes, encoding: .utf8) else { throw FilmSourceError(code: "INVALID_SOURCE") }
        return source
    }
}

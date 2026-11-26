import Foundation
import MeowDebug
import MeowRuntime
import MeowStorage

/// App-scoped owner. Local state works before Node is available; all durable
/// mutations commit before publishing a snapshot. The runtime is borrowed.
public actor FilmSourceService {
    private static let convergenceTimeoutNanoseconds: UInt64 = 30_000_000_000
    private static let convergenceIntervalNanoseconds: UInt64 = 250_000_000
    let transport: CatScriptTransport
    private let settingsRepository: FilmSettingsRepository
    private let settings: CatSourceSettings
    private let localInput: CatLocalSourceInput
    private let repository: FilmSourceRepository
    private var revision = 0
    private var document = FilmSourceState()
    private let observeConnection: @Sendable () async -> RuntimeHTTPClient?
    private var runtimeObservation: (UUID, Task<Void, Never>)?
    private var configurationEpoch = UUID()
    private var initialized = false
    private var closed = false
    private var preparing = false
    private var active: [UUID: @Sendable () -> Void] = [:]
    private var drains: [CheckedContinuation<Void, Never>] = []
    private var managementBusy = false
    private var managementWaiters: [CheckedContinuation<Void, Never>] = []
    private var changingCats: Set<String> = []
    private var probes: [FilmSourceKey: (UUID, Task<FilmSourceDetection, Error>)] = [:]
    private var observers: [UUID: AsyncStream<FilmSourceSnapshot>.Continuation] = [:]
    public private(set) var snapshot = FilmSourceSnapshot(cats: [], videos: [], runtimeError: nil)

    public init(transport: CatScriptTransport, database: SQLiteDatabase,
                settingsRepository: FilmSettingsRepository, runtimeSourceRoot: URL,
                observeConnection: @escaping @Sendable () async -> RuntimeHTTPClient? = { nil }) {
        self.transport = transport; self.repository = FilmSourceRepository(database: database)
        self.settingsRepository = settingsRepository
        self.settings = CatSourceSettings(repository: settingsRepository)
        self.localInput = CatLocalSourceInput(runtimeRoot: runtimeSourceRoot)
        self.observeConnection = observeConnection
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

    public func initialize() async throws {
        try available()
        try await load()
        observeStartedRuntime()
    }

    public func readPanInterception() async throws -> [PanProviderID: Bool] {
        try await run { try await self.settings.readPanInterception() }
    }

    public func savePanInterception(_ provider: PanProviderID, enabled: Bool) async throws -> [PanProviderID: Bool] {
        try await run {
            let saved = try await self.settings.savePanInterception(provider, enabled: enabled)
            await self.panInterceptionChanged()
            return saved
        }
    }

    private func panInterceptionChanged() {
        configurationEpoch = UUID()
        cancelDetections()
        observeStartedRuntime()
    }

    private func load() async throws {
        let configured = try await settings.read()
        if initialized { applyConfiguration(configured, received: snapshot.cats); return }
        let (loaded, revision) = try await repository.load()
        cancelDetections()
        document = loaded; self.revision = revision; initialized = true
        let cats = configured.map { input in
            (loaded.cats.first { $0.installationId == input.installationId } ?? StoredFilmCat(emptyCat(input))).snapshot(input)
        }
        let ids = Set(configured.map(\.installationId))
        publish(.init(cats: cats, videos: sorted(loaded.videos.filter { ids.contains($0.key.installationId) }),
            runtimeError: snapshot.runtimeError))
    }

    private func commit(cats: [FilmCatSource], videos: [FilmVideoSource], deleted: Set<FilmSourceKey>) async throws {
        try Task.checkCancellation()
        let candidate = FilmSourceState(cats: cats.map(StoredFilmCat.init), videos: videos,
            deleted: deleted.sorted(by: FilmSourceKey.ordered))
        let videosChanged = candidate.videos != document.videos
        if candidate != document {
            do { revision = try await repository.save(candidate, previous: document, expected: revision) }
            catch {
                initialized = false
                do { try await load() }
                catch {
                    DebugRuntime.logError(.filmCatalog, sub: "sources", event: "commit.reconcile.error", error: error)
                }
                if document == candidate {
                    publish(.init(cats: cats, videos: videos, runtimeError: snapshot.runtimeError))
                    return
                }
                throw error
            }
            if videosChanged { settingsRepository.notifyVideoSourcesChanged() }
        }
        document = candidate; initialized = true
        publish(.init(cats: cats, videos: videos, runtimeError: snapshot.runtimeError))
    }

    public func start() async throws {
        try await run {
            do {
                try await self.managed {
                    _ = try await self.transport.call("start")
                    try await self.synchronize()
                }
                let deadline = DispatchTime.now().uptimeNanoseconds + Self.convergenceTimeoutNanoseconds
                while await self.snapshot.cats.contains(where: { ["starting", "stopping"].contains($0.state) }) {
                    if DispatchTime.now().uptimeNanoseconds >= deadline {
                        DebugRuntime.log(.filmCatalog, sub: "sources", event: "start.converge.timeout", level: .warn)
                        break
                    }
                    try await Task.sleep(nanoseconds: Self.convergenceIntervalNanoseconds)
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
        observeStartedRuntime()
    }

    public func importLocal(name: String, fileURL: URL, installationId: String? = nil) async throws -> FilmCatSource {
        try CatSourceSettings.validateSource(name: name, type: "local", url: nil)
        let id = installationId ?? UUID().uuidString.lowercased()
        return try await run {
            let source = try await Self.readSource(fileURL)
            return try await self.importLocalCore(name: name, source: source, id: id)
        }
    }

    public func importLocal(name: String, source: String, installationId: String? = nil) async throws -> FilmCatSource {
        let id = installationId ?? UUID().uuidString.lowercased()
        return try await run {
            try await self.importLocalCore(name: name, source: source, id: id)
        }
    }

    private func importLocalCore(name: String, source: String, id: String) async throws -> FilmCatSource {
        try CatSourceSettings.validateSource(name: name, type: "local", url: nil)
        let hash = try localInput.prepare(source)
        return try await saveConfiguration(id) { inputs in
            guard !inputs.contains(where: { $0.installationId == id }) else { throw FilmSourceError(code: "STORAGE_CONFLICT") }
            return inputs + [.init(installationId: id, name: CatSourceSettings.trim(name), sourceType: "local",
                sourceUrl: nil, localPath: nil, localHash: hash, enabled: true, inputRevision: 1)]
        }
    }

    public func importNetwork(name: String, sourceURL: String, installationId: String? = nil) async throws -> FilmCatSource {
        try CatSourceSettings.validateSource(name: name, type: "network", url: sourceURL)
        let id = installationId ?? UUID().uuidString.lowercased()
        return try await run {
            try await self.saveConfiguration(id) { inputs in
                guard !inputs.contains(where: { $0.installationId == id }) else { throw FilmSourceError(code: "STORAGE_CONFLICT") }
                return inputs + [.init(installationId: id, name: CatSourceSettings.trim(name), sourceType: "network",
                    sourceUrl: sourceURL, localPath: nil, localHash: nil, enabled: true, inputRevision: 1)]
            }
        }
    }

    public func editCat(_ installationId: String, name: String, sourceURL: String? = nil) async throws -> FilmCatSource {
        try available(); try await load()
        let cat = try findCat(installationId)
        return try await editCat(installationId, name: name, sourceType: cat.sourceType,
            sourceURL: sourceURL ?? cat.sourceUrl)
    }

    public func editCat(_ installationId: String, name: String, sourceType: String,
                        sourceURL: String? = nil, fileURL: URL? = nil) async throws -> FilmCatSource {
        try CatSourceSettings.validateSource(name: name, type: sourceType, url: sourceURL)
        try available(); try await load()
        let previous = try await configuredCat(installationId)
        return try await run {
            let source: String?
            if let fileURL { source = try await Self.readSource(fileURL) } else { source = nil }
            return try await self.editCore(previous, name: name, type: sourceType, url: sourceURL, source: source)
        }
    }

    public func editCat(_ installationId: String, name: String, sourceType: String,
                        sourceURL: String? = nil, source: String) async throws -> FilmCatSource {
        try available(); try await load()
        let previous = try await configuredCat(installationId)
        return try await run {
            try await self.editCore(previous, name: name, type: sourceType, url: sourceURL, source: source)
        }
    }

    private func editCore(_ previous: CatSourceConfiguration, name: String, type: String,
                          url: String?, source: String?) async throws -> FilmCatSource {
        try CatSourceSettings.validateSource(name: name, type: type, url: url)
        guard source == nil || type == "local" else { throw FilmSourceError(code: "INVALID_SOURCE") }
        let hash = try source.map { try localInput.prepare($0) }
        return try await saveConfiguration(previous.installationId) { inputs in
            guard inputs.first(where: { $0.installationId == previous.installationId }) == previous else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }
            var next = previous
            next.name = CatSourceSettings.trim(name); next.sourceType = type; next.sourceUrl = url
            next.localPath = type == "local" && source == nil && previous.sourceType == "local" ? previous.localPath : nil
            next.localHash = type == "local" ? hash ?? (previous.sourceType == "local" ? previous.localHash : nil) : nil
            guard type != "local" || next.localHash != nil else { throw FilmSourceError(code: "INVALID_SOURCE") }
            if !next.hasSameInput(as: previous) { next.inputRevision = CatSourceSettings.nextRevision(previous.inputRevision) }
            return inputs.map { $0.installationId == previous.installationId ? next : $0 }
        }
    }

    public func updateCat(_ installationId: String, fileURL: URL? = nil) async throws -> FilmCatSource {
        try available(); try await load()
        let cat = try findCat(installationId)
        if cat.sourceType == "local" {
            guard let fileURL else { throw FilmSourceError(code: "INVALID_SOURCE") }
            return try await editCat(installationId, name: cat.name, sourceType: "local", fileURL: fileURL)
        }
        guard fileURL == nil else { throw FilmSourceError(code: "INVALID_SOURCE") }
        return try await updateCat(installationId, source: nil)
    }

    public func updateCat(_ installationId: String, source: String?) async throws -> FilmCatSource {
        try available(); try await load()
        let cat = try findCat(installationId)
        if cat.sourceType == "local" {
            guard let source else { throw FilmSourceError(code: "INVALID_SOURCE") }
            return try await editCat(installationId, name: cat.name, sourceType: "local", source: source)
        }
        guard source == nil else { throw FilmSourceError(code: "INVALID_SOURCE") }
        return try await run {
            try await self.manage(installationId) {
                try await self.transport.call("update", ["installationId": .string(installationId)])
            }
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
            try await self.saveConfiguration(installationId) { inputs in
                guard inputs.contains(where: { $0.installationId == installationId }) else { throw FilmSourceError(code: "NOT_FOUND") }
                return inputs.map { input in
                    var next = input
                    if input.installationId == installationId { next.enabled = enabled }
                    return next
                }
            }
        }
    }

    private func manage(_ id: String,
                        operation: @escaping @Sendable () async throws -> RuntimeValue) async throws -> FilmCatSource {
        try nonempty(id)
        return try await managed {
            try await self.manageCore(id, operation: operation)
        }
    }

    private func manageCore(_ id: String,
                            operation: @Sendable () async throws -> RuntimeValue) async throws -> FilmCatSource {
        let before = try await configuredCat(id)
        invalidateCat(id); changingCats.insert(id)
        defer { changingCats.remove(id) }
        do {
            _ = try await operation()
            try Task.checkCancellation()
        } catch {
            if !(error is CancellationError), !Task.isCancelled {
                do { try await synchronize() }
                catch { DebugRuntime.logError(.filmCatalog, sub: "sources", event: "manage.reconcile.error", error: error) }
            }
            throw error
        }
        try await synchronize()
        let current = try await configuredCat(id)
        guard current.hasSameInput(as: before), current.inputRevision == before.inputRevision,
              current.enabled == before.enabled else { throw CancellationError() }
        let result = try findCat(id)
        return result
    }

    public func deleteCat(_ installationId: String) async throws {
        try await run {
            try await self.removeConfiguration(installationId)
        }
    }

    private func configuredCat(_ id: String) async throws -> CatSourceConfiguration {
        guard let input = try await settings.read().first(where: { $0.installationId == id }) else {
            throw FilmSourceError(code: "NOT_FOUND")
        }
        return input
    }

    private func saveConfiguration(_ id: String,
                                   edit: @escaping @Sendable ([CatSourceConfiguration]) throws -> [CatSourceConfiguration]) async throws -> FilmCatSource {
        try available(); try Task.checkCancellation()
        let configured = try await settings.change(edit)
        configurationEpoch = UUID()
        // A failed cache write or runtime request cannot revoke this native commit.
        applyConfiguration(configured, received: snapshot.cats)
        let saved = try findCat(id)
        observeStartedRuntime()
        return saved
    }

    private func removeConfiguration(_ id: String) async throws {
        try available(); try Task.checkCancellation()
        let configured = try await settings.change { inputs in
            guard inputs.contains(where: { $0.installationId == id }) else { throw FilmSourceError(code: "NOT_FOUND") }
            return inputs.filter { $0.installationId != id }
        }
        configurationEpoch = UUID()
        applyConfiguration(configured, received: snapshot.cats)
        observeStartedRuntime()
    }

    private func emptyCat(_ input: CatSourceConfiguration) -> FilmCatSource {
        .init(installationId: input.installationId, name: input.name, sourceType: input.sourceType,
            sourceUrl: input.sourceUrl, enabled: input.enabled, state: input.enabled ? "stopped" : "disabled",
            inputRevision: input.inputRevision)
    }

    private func configuredCats(_ configured: [CatSourceConfiguration], received: [FilmCatSource]) -> [FilmCatSource] {
        configured.map { input in
            let cached = snapshot.cats.first { $0.installationId == input.installationId }
            var observed = received.first { $0.installationId == input.installationId }
            if let previous = cached, input.matches(previous), let candidate = observed,
               input.matches(candidate), candidate.generation < previous.generation {
                observed = previous
            }
            let current = observed.map(input.matches) == true
            let value = current ? observed! : cached ?? emptyCat(input)
            let sameInput = current || value.inputRevision == input.inputRevision
            return FilmCatSource(installationId: input.installationId, name: input.name, sourceType: input.sourceType,
                sourceUrl: input.sourceUrl, enabled: input.enabled, importedAt: value.importedAt, updatedAt: value.updatedAt,
                updateState: sameInput ? value.updateState : "idle", updateError: sameInput ? value.updateError : nil,
                generation: current ? value.generation : 0,
                state: current ? value.state : input.enabled ? "stopped" : "disabled",
                sourceHash: value.sourceHash, error: current ? value.error : nil,
                sites: current && value.canDetect ? value.sites : cached?.sites ?? value.sites,
                inputRevision: input.inputRevision)
        }
    }

    private func applyConfiguration(_ configured: [CatSourceConfiguration], received: [FilmCatSource]) {
        let cats = configuredCats(configured, received: received), ids = Set(configured.map(\.installationId))
        for previous in snapshot.cats {
            let current = cats.first { $0.installationId == previous.installationId }
            if current == nil || current?.inputRevision != previous.inputRevision || current?.enabled != previous.enabled {
                invalidateCat(previous.installationId)
            }
        }
        publish(.init(cats: cats, videos: snapshot.videos.filter { ids.contains($0.key.installationId) },
            runtimeError: snapshot.runtimeError))
    }

    public func deleteVideo(_ key: FilmSourceKey) async throws {
        try await run { try await self.managed { try await self.deleteVideoCore(key) } }
    }

    private func deleteVideoCore(_ key: FilmSourceKey) async throws {
        _ = try findVideo(key)
        cancelDetection(key)
        var deleted = Set(document.deleted); deleted.insert(key)
        try await commit(cats: snapshot.cats, videos: snapshot.videos.filter { $0.key != key }, deleted: deleted)
    }

    public func saveVideoOptions(_ key: FilmSourceKey, options: FilmVideoOptions, order: Int? = nil) async throws {
        try await run { try await self.managed { try await self.saveVideoOptionsCore(key, options: options, order: order) } }
    }

    private func saveVideoOptionsCore(_ key: FilmSourceKey, options: FilmVideoOptions, order: Int?) async throws {
        _ = try findVideo(key)
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
        try await commit(cats: snapshot.cats, videos: videos, deleted: Set(document.deleted))
    }

    private func synchronize(using observedTransport: CatScriptTransport? = nil) async throws {
        let epoch = configurationEpoch
        let incoming: [FilmCatSource]
        do {
            let root = try await (observedTransport ?? transport).call("status")
            guard let values = root.catField("instances")?.catArray else { throw FilmSourceError(code: "INVALID_RESPONSE") }
            incoming = try values.map(parseFilmCat)
            guard Set(incoming.map(\.installationId)).count == incoming.count else { throw FilmSourceError(code: "INVALID_RESPONSE") }
        } catch {
            if !Task.isCancelled, epoch != configurationEpoch { return }
            if !(error is CancellationError), !Task.isCancelled { reportRuntime(error) }
            throw error
        }
        try Task.checkCancellation()
        guard epoch == configurationEpoch else { return }
        // Native intent may have changed while status was in flight. Neither a
        // missing runtime instance nor an old revision may delete that intent.
        try await load()
        let configured = try await settings.read()
        let ids = Set(configured.map(\.installationId))
        let deleted = Set(document.deleted.filter { ids.contains($0.installationId) })
        var videos = snapshot.videos.filter { ids.contains($0.key.installationId) }
        let cats = configuredCats(configured, received: incoming)
        for cat in cats {
            let previous = snapshot.cats.first { $0.installationId == cat.installationId }
            if let previous, previous.generation != cat.generation || previous.state != cat.state ||
                previous.enabled != cat.enabled || previous.sourceHash != cat.sourceHash ||
                previous.inputRevision != cat.inputRevision { invalidateCat(cat.installationId) }
            guard cat.canDetect else { continue }
            let keys = Set(cat.sites.map { $0.identity(installationId: cat.installationId) })
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
        try await commit(cats: cats, videos: sorted(videos), deleted: deleted)
        publish(.init(cats: snapshot.cats, videos: snapshot.videos, runtimeError: nil))
    }

    /// Syncs once with an already-running runtime and converges transitional
    /// `starting`/`stopping` states within a bounded deadline (zero idle polling).
    private func observeStartedRuntime() {
        guard !closed, !preparing else { return }
        if let existing = runtimeObservation { existing.1.cancel() }
        let id = UUID()
        runtimeObservation = (id, Task { [weak self] in
            guard let self else { return }
            await self.convergeRuntime(id)
        })
    }

    private func convergeRuntime(_ id: UUID) async {
        defer { if runtimeObservation?.0 == id { runtimeObservation = nil } }
        guard let client = await observeConnection(), !Task.isCancelled, !client.isClosed else { return }
        // Never use the ordinary transport here: its provider may start Node or
        // return a different runtime epoch after a suspension.
        let captured = CatScriptTransport(connectionProvider: { client })
        let deadline = DispatchTime.now().uptimeNanoseconds + Self.convergenceTimeoutNanoseconds
        do {
            while !client.isClosed {
                try Task.checkCancellation()
                try available()
                try await managed { try await self.synchronize(using: captured) }
                let transitioning = snapshot.cats.contains { ["starting", "stopping"].contains($0.state) }
                guard transitioning else { return }
                if DispatchTime.now().uptimeNanoseconds >= deadline {
                    DebugRuntime.log(.filmCatalog, sub: "sources", event: "runtime.converge.timeout", level: .warn)
                    return
                }
                try await Task.sleep(nanoseconds: Self.convergenceIntervalNanoseconds)
            }
        } catch {
            if !(error is CancellationError), !Task.isCancelled {
                DebugRuntime.logError(.filmCatalog, sub: "sources", event: "runtime.converge.error", error: error)
            }
        }
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
        guard probes[key]?.0 == id, let current = snapshot.cats.first(where: { $0.installationId == key.installationId }),
              current.canDetect, current.generation == generation,
              snapshot.videos.contains(where: { $0.key == key }) else { throw CancellationError() }
        var videos = snapshot.videos
        if let index = videos.firstIndex(where: { $0.key == key }) { videos[index].detection = result }
        try await commit(cats: snapshot.cats, videos: videos, deleted: Set(document.deleted))
        return result
    }

    public func cancelDetection(_ key: FilmSourceKey) { probes.removeValue(forKey: key)?.1.cancel() }
    private func cancelDetections() {
        let running = probes.values; probes.removeAll()
        for (_, task) in running { task.cancel() }
    }
    private func invalidateCat(_ id: String) {
        for key in Array(probes.keys) where key.installationId == id { cancelDetection(key) }
    }

    public func prepareShutdown() async {
        preparing = true
        let observation = runtimeObservation?.1
        observation?.cancel()
        for cancel in active.values { cancel() }
        cancelDetections()
        await observation?.value
        if !active.isEmpty { await withCheckedContinuation { drains.append($0) } }
    }

    public func close() async {
        closed = true
        await prepareShutdown()
        for observer in observers.values { observer.finish() }
        observers.removeAll()
    }

    private func run<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try available(); try Task.checkCancellation(); try await load()
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
        let bytes = try await ImportedFileReader.read(url)
        guard let source = String(data: bytes, encoding: .utf8) else { throw FilmSourceError(code: "INVALID_SOURCE") }
        return source
    }
}

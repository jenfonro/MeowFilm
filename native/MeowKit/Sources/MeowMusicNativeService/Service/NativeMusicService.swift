import Foundation
import MeowMusicFeature
import MeowContracts
import MeowMusicPlatform

struct MusicDocument: Sendable {
    static let areas: Set<String> = ["library", "queue", "history", "playback", "catalogSource"]
    var revision: Int
    var library: MJ
    var queue: MJ
    var playback: MJ
    var history: MJ
    var catalogSource: MJ
    let songRows: any MusicNativeLibraryReading
    init(snapshot: MusicNativeLibrarySnapshot, runID: String, songs: any MusicNativeLibraryReading) throws {
        revision = snapshot.revision; songRows = songs
        func area(_ name: String, _ fallback: MJ) throws -> MJ { try snapshot.areas[name].map { try MJ.parse($0) } ?? fallback }
        library = try area("library", MusicModel.library)
        queue = try area("queue", MusicModel.queue)
        playback = try area("playback", MusicModel.playback)
        history = try area("history", MusicModel.history)
        catalogSource = try area("catalogSource", MusicModel.catalogSource)
        if queue["runId"].string != runID { queue["history"] = .array([]); queue["failed"] = .array([]) }
        queue["runId"] = .string(runID)
        if playback["position"]["occurrenceId"] != queue["currentId"] { resetPosition() }
    }
    mutating func resetPosition() { playback["position"] = .obj([("occurrenceId", queue["currentId"]), ("seconds", .n(0))]) }
    func song(_ id: String) throws -> MJ {
        guard let data = try songRows.songData(id: id) else { throw MusicServiceError.notFound }
        return try MJ.parse(data)
    }
    func projectSongs(_ ids: [String]) throws -> MJ {
        var result = MJ.obj()
        for id in Set(ids).filter({ !$0.isEmpty }) { result[id] = try song(id) }
        return result
    }
    func referenceSongs(_ ids: [String]) throws -> MJ {
        do { return try projectSongs(ids) }
        catch MusicServiceError.notFound { throw MusicServiceError.corruptDocument }
    }
    func queueSongs() throws -> MJ {
        try referenceSongs(queue["entries"].array.map { $0["songId"].string } +
            queue["similar"]["returnQueue"]["entries"].array.map { $0["songId"].string } +
            [queue["similar"]["seedSongId"].string] + queue["similar"]["batches"].array.map { $0["basedOnSongId"].string }.filter { !$0.isEmpty })
    }
    func referencedSongIDs(in areas: Set<String>) -> Set<String> {
        var ids = Set<String>()
        if areas.contains("library") {
            for collection in library["library"]["collections"].array {
                ids.formUnion(collection["order"].strings)
                ids.formUnion(collection["intents"].fields.map { $0.0 })
                for binding in collection["bindings"].array {
                    ids.formUnion(binding["map"].fields.map { $0.1.string })
                }
            }
        }
        if areas.contains("queue") {
            ids.formUnion(queue["entries"].array.map { $0["songId"].string })
            ids.formUnion(queue["similar"]["returnQueue"]["entries"].array.map { $0["songId"].string })
            ids.insert(queue["similar"]["seedSongId"].string)
            ids.formUnion(queue["similar"]["batches"].array.map { $0["basedOnSongId"].string })
        }
        ids.remove("")
        return ids
    }
    func requireRevision(_ area: String) throws {
        let value: MJ
        switch area {
        case "library": value = library
        case "queue": value = queue
        case "playback": value = playback
        case "catalogSource": value = catalogSource
        case "history": return
        default: throw MusicServiceError.invalidRequest
        }
        try musicRequire(value["revision"].isInteger, .corruptDocument)
        if area == "library" {
            try musicRequire(library["schema"] == .n(2) && library["scope"].string == "device" &&
                library["library"]["version"] == .n(2) && library["library"]["ownerId"].string == "device" &&
                library["library"]["revision"].isInteger, .corruptDocument)
        }
    }
    func requireQueue(knownSongIDs: Set<String> = []) throws {
        try musicRequire(queue["schema"] == .n(1) && queue["revision"].isInteger && queue["serial"].isInteger &&
                         queue["entries"].isArray, .corruptDocument)
        var seen = Set<String>(), serial = queue["serial"].int
        for entry in queue["entries"].array {
            let id = entry["id"].string, songID = entry["songId"].string
            try musicRequire(!id.isEmpty && seen.insert(id).inserted &&
                (knownSongIDs.contains(songID) || songRows.containsSong(id: songID)), .corruptDocument)
            if let number = Int(id.dropFirst(6)) { serial = max(serial, number) }
        }
        try musicRequire(serial <= 9_007_199_254_740_991 && queue["serial"].int >= serial &&
            (queue["currentId"] == .null || seen.contains(queue["currentId"].string)), .corruptDocument)
        for key in ["priority", "history", "failed"] {
            try musicRequire(queue[key].isArray && queue[key].array.allSatisfy {
                $0.isString && seen.contains($0.string) && (key != "priority" || $0 != queue["currentId"])
            }, .corruptDocument)
        }
    }
    func area(_ name: String) throws -> MJ {
        switch name {
        case "library": return library
        case "queue": return MusicModel.durable(queue)
        case "history": return MusicModel.durable(history)
        case "catalogSource": return catalogSource
        case "playback":
            var value = playback; value["position"]["seconds"] = .n(0)
            return value
        default: throw MusicServiceError.invalidRequest
        }
    }
}

/// Product-lifetime business owner. It is passive and locked at construction.
/// It borrows the application's platform client; it never owns a runtime session.
public actor NativeMusicService: MusicServing, MusicAccountBrowsing {
    nonisolated let access = MusicNativeAccess()
    /// Product-scoped request policy, not an identity or persistence schema.
    /// Omission preserves the existing five-platform behavior. Disabling a
    /// platform does not rewrite or hide previously stored songs and sources.
    public nonisolated let enabledPlatforms: Set<MusicPublicBrowsePlatform>
    let database: any MusicNativeLibraryStoring
    let repositoryReader: MusicNativeLibraryAccessReader
    var activeSongWriter: (any MusicNativeLibraryWriting)?
    let scripts: (any MusicScriptResolving)?
    let accounts: (any MusicNativeAccountProviding)?
    let publicPlatforms: MusicNativePlatforms
    let sourceDiscovery: any MusicSourceDiscoveryCaching
    var sourceDiscoveryPersistTask: Task<Void, Never>?
    var sourceDiscoveryPersistPending = false
    var sourceDiscoveryObservationOrder: UInt64 = 0
    var sourceDiscoveryLatest: [String: [String: MusicSearchObservation]] = [:]
    let homeSnapshot: (any MusicHomeSnapshotStoring)?
    let audioFileCache: (any MusicAudioFileCaching)?
    let presentationCache: (any MusicPresentationCaching)?
    var audioCacheAttempt: MusicNativeAudioCacheAttempt?
    var audioCacheTask: Task<Void, Never>?
    var audioCacheCaptures: [UUID: Task<Void, Never>] = [:]
    let scriptAudioCacheScope = MusicNativeCacheScope(scope: "script-history")
    var lastAudioCacheScope: String?
    var homeCache: (source: MusicPublicBrowsePlatform, scope: String, revision: UUID,
                    expires: Date, page: MusicNativePublicBrowseDiscovery)?
    var homeDisplay: (source: MusicPublicBrowsePlatform, page: MusicNativePublicBrowseDiscovery)?
    var homeDisplayPrepared = false
    var homeLoad: MusicNativeHomeLoad?
    var homeFirstRead: MusicNativeHomeFirstRead?
    /// Last adopted durable `catalogSource`; nil until first read.
    var homeSource: (platform: MusicPublicBrowsePlatform, revision: Int)?
    var homeSourceRevision = UUID()
    var homeObservers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var songObservers: [UUID: AsyncStream<Set<String>>.Continuation] = [:]
    var anonymousHomeBindings: [MusicPublicBrowsePlatform: MusicNativeHomeBinding] = [:]
    var lastHomeScope: String?
    var lastHomeObservation: MusicNativeCacheScope?
    var lastHomeRefreshObservation: MusicNativeCacheScope?
    let now: @Sendable () -> Date
    let random: @Sendable () -> Double
    let runID = UUID().uuidString.lowercased()
    private var sessionPosition = MusicModel.playback["position"]
    private var parsedDocument: (identity: MusicNativeLibraryRevision, state: MusicDocument)?
    private struct PendingTransaction {
        let before: MusicNativeLibraryRevision, after: MusicNativeLibraryRevision
        let draft: MusicDocument, areas: Set<String>, changed: Set<String>, songs: Set<String>
    }
    private var pendingTransaction: PendingTransaction?
    var available = false, generation: UInt64 = 0, closed = false, epoch = UUID()
    var lyricEpoch = UUID()
    struct Attempt: Sendable {
        let id: String, occurrence: String, sourceKey: String
        var selection: MusicPlaybackSelection
        var scriptSource: MusicScriptSource? = nil
        var ready = false, started = false, ended = false, endedConsumed = false
        var isPlaying = false
        var recovery: MusicNativePlaybackRecoveryPin? = nil
        var authorization: MusicQueueAuthorization? = nil
        var cacheRecoveryID: UUID? = nil
        var resolvedDuration: Double? = nil
        var latestFact: MusicPlaybackFact? = nil
    }
    var attempt: Attempt?
    var currentSourceSession: (occurrence: String, session: MusicNativeSourceInspection)?
    // Configuration invalidation also fences creation before a session exists.
    var sourceSessionRevision = UUID()
    var preparedSources: [String: MusicNativePreparedSource] = [:]
    var preparedCacheSources: [String: MusicNativePreparedCacheSource] = [:]
    var sourceResults: [String: [MusicNativeResolvedSource]] = [:]
    var sourceResultOrder: [String] = []
    var cachedPlaybackRecovery: MusicNativeCacheRecoveryPin?
    var publicCollectionRevision = UUID()
    var publicCollectionRefreshes = 0
    private var accessTransitions = 0
    var publicQueueSources: [UUID: (collectionID: String, authorization: MusicQueueAuthorization,
                                   reader: MusicNativePublicBrowseQueueRead)] = [:]
    var spent = Set<String>()
    var operations: [UUID: @Sendable () -> Void] = [:]
    var operationTasks: [UUID: Task<Void, Never>] = [:]
    var operationLabels: [UUID: String] = [:]
    var librarySyncRun: (id: UUID, cancel: @Sendable () -> Void, drain: @Sendable () async -> Void)?
    var automaticLibrarySyncRuntime = MusicNativeAutomaticSyncRuntime()
    let playbackFeedback: MusicNativeFeedbackController
    var playbackFeedbackAdmission: MusicNativeFeedbackAdmission?
    private let feedbackAccessCallbackID = UUID()
    private var feedbackCloseTask: Task<Void, Never>?
    private var closeTask: Task<Void, Never>?
    var feedbackClosing = false
    var queueFeedbackOrigins: [String: MusicNativeQueueFeedbackOrigin] = [:]
    var similarMode = MusicNativeSimilarRuntime()
    var fmPlayback: MusicNativeFMSession?
    var fmPreviews: [MusicHomeAction: MusicNativeFMPreviewEntry] = [:]
    var fmPreviewOrder: [MusicHomeAction] = []
    var fmQueues: [UUID: (MusicHomeAction, MusicQueueAuthorization, MusicNativeFMRead)] = [:]

    public init(database: any MusicNativeLibraryStoring, scripts: (any MusicScriptResolving)? = nil,
                accounts: (any MusicNativeAccountProviding)? = nil, platformClient: MusicPlatformClient,
                sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                enabledPlatforms: Set<MusicPublicBrowsePlatform> = Set(MusicPublicBrowsePlatform.allCases),
                homeSnapshot: (any MusicHomeSnapshotStoring)? = nil,
                audioFileCache: (any MusicAudioFileCaching)? = nil,
                presentationCache: (any MusicPresentationCaching)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() },
                monotonicClock: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
                random: @escaping @Sendable () -> Double = { Double.random(in: 0..<1) }) {
        self.database = database; repositoryReader = MusicNativeLibraryAccessReader(database: database, access: access); self.scripts = scripts; self.accounts = accounts; self.homeSnapshot = homeSnapshot
        self.audioFileCache = audioFileCache
        self.presentationCache = presentationCache
        self.sourceDiscovery = sourceDiscovery ?? MusicNativeMemorySourceDiscoveryCache()
        now = clock; self.random = random
        playbackFeedback = MusicNativeFeedbackController(clock: monotonicClock)
        self.enabledPlatforms = enabledPlatforms
        publicPlatforms = MusicNativePlatforms(client: platformClient)
        // The existing nonisolated protection fence must also revoke feedback
        // before the service actor can acknowledge an access transition.
        let feedback = playbackFeedback
        access.register(UUID()) { feedback.invalidate() }
    }
    public func setAccess(available value: Bool, generation next: UInt64) async throws {
        try musicRequire(!closed, .closed)
        try musicRequire(next > 0 && next <= 9_007_199_254_740_991 && next >= generation && (next != generation || value == available), .staleAccess)
        try access.grant(value, generation: next)
        if next == generation { return }
        accessTransitions += 1
        defer { accessTransitions -= 1 }
        if generation > 0 || !value {
            (accounts as? any MusicNativeFeedbackLifecycleProviding)?.cancelPendingPlaybackFeedback(platform: nil)
        }
        generation = next; available = value; epoch = UUID()
        clearTransient()
        if value, let owner = accounts as? any MusicNativeFeedbackLifecycleProviding {
            access.register(feedbackAccessCallbackID) { owner.cancelPendingPlaybackFeedback(platform: nil) }
        }
        for cancel in operations.values { cancel() }
        await drainOperations()
        if let run = librarySyncRun { await run.drain() }
        if let sync = automaticLibrarySyncRuntime.settingsSync { _ = await sync.task.value }
        await drainSimilarWork()
        await audioFileCache?.cancelPending()
    }
    /// Call after the playback consumer's local fact drain and before access
    /// revocation. Drains local registrations into the account outbox, not its
    /// remote receipts. A real access/settings revocation still clears it.
    public func prepareForClose() async {
        guard !closed else { return }
        if feedbackCloseTask == nil {
            feedbackClosing = true
            stopPlaybackFeedbackDispatchForClose()
            playbackFeedback.finishCurrent(reason: .interrupt)
            playbackFeedback.invalidate(preservingEnds: true)
            let feedback = playbackFeedback, owner = accounts as? any MusicNativeFeedbackLifecycleProviding
            feedbackCloseTask = Task {
                await feedback.flushRegistrations()
                await owner?.preparePlaybackFeedbackForClose()
            }
        }
        await feedbackCloseTask?.value
        access.remove(feedbackAccessCallbackID)
    }
    public func close() async {
        if closeTask == nil { closeTask = Task { await finishClose() } }
        await closeTask?.value
    }
    private func finishClose() async {
        await prepareForClose()
        // Library recordings reach disk before access is revoked.
        await flushSourceDiscoveryPersist()
        if closed { return }
        access.revoke(generation: generation, close: true)
        closed = true; available = false; epoch = UUID(); clearTransient()
        for observer in songObservers.values { observer.finish() }
        songObservers.removeAll()
        for cancel in operations.values { cancel() }
        await drainOperations()
        if let run = librarySyncRun { await run.drain() }
        await drainSimilarWork()
        await playbackFeedback.flush()
        await (accounts as? any MusicNativeFeedbackLifecycleProviding)?.flushPendingPlaybackFeedback()
        if let task = automaticLibrarySyncRuntime.task { _ = await task.result }
        if let sync = automaticLibrarySyncRuntime.settingsSync { _ = await sync.task.value }
        await audioFileCache?.close()
        try? database.close()
    }
    func clearTransient() {
        publicCollectionRevision = UUID()
        for (id, entry) in publicQueueSources {
            access.remove(id); entry.authorization.revoke()
            retainSimilarDrain(Task { await entry.reader.close() })
        }
        publicQueueSources.removeAll()
        clearFMPlayback()
        clearFMPreviews()
        for (id, value) in fmQueues {
            access.remove(id); value.1.revoke()
            retainSimilarDrain(Task { await value.2.close() })
        }
        fmQueues.removeAll()
        invalidateAutomaticLibrarySync()
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        for capture in audioCacheCaptures.values {
            capture.cancel()
            retainSimilarDrain(capture)
        }
        audioCacheCaptures.removeAll()
        clearSimilarRuntime()
        homeCache = nil
        homeLoad = nil
        if let task = homeFirstRead?.task { task.cancel(); retainSimilarDrain(Task { _ = await task.result }) }; homeFirstRead = nil
        homeDisplay = nil; homeDisplayPrepared = false
        for observer in homeObservers.values { observer.finish() }
        homeObservers.removeAll()
        for binding in anonymousHomeBindings.values { binding.authorization.revoke() }
        anonymousHomeBindings.removeAll()
        lastHomeScope = nil
        lastHomeObservation = nil
        lastHomeRefreshObservation = nil
        playbackFeedback.invalidate()
        playbackFeedbackAdmission = nil
        queueFeedbackOrigins.removeAll()
        librarySyncRun?.cancel()
        if let currentSourceSession { retainSimilarDrain(Task { await currentSourceSession.session.close() }) }
        currentSourceSession = nil; clearPreparedSources(); sourceResults = [:]; sourceResultOrder = []
        attempt = nil; spent.removeAll()
        sourceDiscoveryLatest.removeAll()
        parsedDocument = nil
        lyricEpoch = UUID()
    }
    func invalidatePlayback(reason: MusicPlaybackFeedbackReport.Reason) {
        if let admission = playbackFeedbackAdmission, admission.retiringReason == nil,
           admission.authorization?.isActive == false {
            playbackFeedback.invalidate(attemptID: admission.attemptID)
            playbackFeedbackAdmission = nil
        } else if let admission = playbackFeedbackAdmission, playbackFeedback.ownsAttempt(admission.attemptID) {
            // Queue commit retires media, not the consumer's accepted facts.
            // Its bounded drain finishes this attempt before admitting a new one.
            if admission.retiringReason == nil {
                playbackFeedbackAdmission?.retiringReason = reason
                playbackFeedbackAdmission?.targets = playbackFeedback.retireCurrent(reason: reason)
            }
        } else {
            playbackFeedback.finishCurrent(reason: reason)
            playbackFeedback.invalidate(preservingEnds: true)
            playbackFeedbackAdmission = nil
        }
        clearPreparedSources()
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        attempt = nil; lyricEpoch = UUID()
        cancelWork("playback"); cancelWork("lyrics")
    }
    func check(_ expected: UUID? = nil) throws {
        try musicRequire(!closed, .closed); try musicRequire(available, .accessUnavailable)
        // setAccess suspends while retiring the old scope's caches. Do not
        // admit new work against that partially retired scope during reentry.
        try musicRequire(accessTransitions == 0, .accessUnavailable)
        try access.check()
        if let expected { try musicRequire(epoch == expected, .staleAccess) }
        if Task.isCancelled { throw MusicServiceError.cancelled }
    }
    nonisolated func platformEnabled(_ id: String) -> Bool {
        guard let platform = MusicPublicBrowsePlatform(rawValue: id) else { return false }
        return enabledPlatforms.contains(platform)
    }
    nonisolated func requirePlatform(_ id: String) throws {
        try musicRequire(platformEnabled(id), .unsupported)
    }
    /// Official audio admission is checked by the owner, never by hiding UI rows.
    nonisolated static func officialEnabled(_ platform: String, _ playback: MJ) -> Bool {
        MusicModel.officialSources(playback).contains { $0.rawValue == platform }
    }
    func read() throws -> MusicDocument {
        try check()
        do {
            let identity = try repositoryReader.readRevision()
            if let pending = pendingTransaction {
                if identity == pending.after {
                    pendingTransaction = nil
                    publishTransaction(pending.draft, identity: identity, areas: pending.areas, changed: pending.changed, songs: pending.songs)
                } else if identity == pending.before {
                    pendingTransaction = nil; parsedDocument = nil
                } else { throw MusicServiceError.storageConflict }
            }
            var state: MusicDocument
            if let cached = parsedDocument, cached.identity == identity { state = cached.state }
            else {
                let snapshot = try repositoryReader.readSnapshot()
                state = try MusicDocument(snapshot: snapshot, runID: runID, songs: repositoryReader)
                parsedDocument = (snapshot.identity, state)
            }
            if sessionPosition["occurrenceId"] == state.queue["currentId"] { state.playback["position"] = sessionPosition }
            else { state.resetPosition() }
            return state
        } catch let error as MusicServiceError { throw error }
        catch { throw MusicServiceError.storageFailed }
    }
    func transaction<T>(_ areas: Set<String>, reportingCancellationAfterCommit: Bool = true,
                        _ change: (inout MusicDocument) throws -> T) throws -> T {
        try check()
        let original = try read()
        guard let originalIdentity = parsedDocument?.identity else { throw MusicServiceError.storageConflict }
        var draft = original
        for area in areas { try draft.requireRevision(area) }
        var changedSongs = Set<String>()
        var changed = Set<String>()
        var candidateReady = false
        var commitIdentity: MusicNativeLibraryRevision?
        let value: T
        do {
            value = try withUnsafeCurrentTask { task in
            try database.withTransaction(commit: { action in
                try access.perform {
                    if task?.isCancelled == true { throw MusicServiceError.cancelled }
                    commitIdentity = try database.readRevision()
                    try action()
                }
            }) { writer in
                try musicRequire(try writer.readRevision() == originalIdentity, .storageConflict)
                activeSongWriter = writer
                defer { activeSongWriter = nil }
                let result = try change(&draft)
                changed = areas.union(["library"]).filter { name in
                    switch name {
                    case "library": return draft.library != original.library
                    case "queue": return draft.queue != original.queue
                    case "playback": return draft.playback != original.playback
                    case "history": return draft.history != original.history
                    case "catalogSource": return draft.catalogSource != original.catalogSource
                    default: return true
                    }
                }
                for area in changed { try draft.requireRevision(area) }
                // Retained relationships are not re-normalized. New references
                // must point at a real row in this same transaction.
                let retained = original.referencedSongIDs(in: changed)
                for id in draft.referencedSongIDs(in: changed).subtracting(retained) {
                    try musicRequire(UUID(uuidString: id) != nil && writer.containsSong(id: id), .corruptDocument)
                }
                if changed.contains("queue") { try draft.requireQueue(knownSongIDs: retained) }
                if task?.isCancelled == true { throw MusicServiceError.cancelled }
                for area in changed { try writer.saveArea(area, value: draft.area(area).storageValue) }
                changedSongs = writer.changedSongIDs
                candidateReady = true
                return result
            }
            }
        } catch MusicNativeLibraryCommitError.receiptLost {
            // The store verifies this transaction's durable receipt before
            // reporting a lost callback. Settle its facts without replaying it.
            if candidateReady {
                draft.revision = original.revision + 1
                publishTransaction(draft, identity: commitIdentity, areas: areas, changed: changed, songs: changedSongs)
            }
            throw MusicServiceError.commitUnconfirmed
        } catch MusicNativeLibraryCommitError.outcomeUnknown(let before, let after) {
            try musicRequire(candidateReady && before == originalIdentity &&
                after.revision == original.revision + 1, .storageConflict)
            draft.revision = after.revision
            pendingTransaction = .init(before: before, after: after, draft: draft, areas: areas, changed: changed, songs: changedSongs)
            throw MusicServiceError.commitUnconfirmed
        }
        if changed.isEmpty && changedSongs.isEmpty { return value }
        draft.revision = original.revision + 1
        publishTransaction(draft, identity: commitIdentity, areas: areas, changed: changed, songs: changedSongs)
        if reportingCancellationAfterCommit && Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
        return value
    }
    private func publishTransaction(_ draft: MusicDocument, identity: MusicNativeLibraryRevision?, areas: Set<String>, changed: Set<String>, songs: Set<String>) {
        if changed.isEmpty && songs.isEmpty { return }
        parsedDocument = identity.map { ($0, draft) }
        sessionPosition = draft.playback["position"]
        if areas.contains("queue") {
            let entries = Set(draft.queue["entries"].array.map { $0["id"].string })
            queueFeedbackOrigins = queueFeedbackOrigins.filter { entries.contains($0.key) }
        }
        if changed.contains("library") || !songs.isEmpty { scheduleSourceDiscoveryPersist() }
        if !songs.isEmpty {
            emitSongChanges(songs)
            if similarMode.preview != nil || similarMode.active != nil || similarMode.queue != nil { emitSimilar() }
        }
    }
    func cancelWork(_ label: String) {
        for (id, value) in operationLabels where value == label { operations[id]?() }
    }
    func work<T: Sendable>(label: String? = nil, draining: Bool = false,
                          _ body: @escaping @Sendable () async throws -> T) async throws -> T {
        try check()
        let own = epoch, id = UUID(), gate = MusicNativeOperation<T>()
        let task: Task<Void, Never> = Task {
            let result: Result<T, Error>
            do { try check(own); result = .success(try await body()) }
            catch { result = .failure(error) }
            gate.finish(result)
            operations.removeValue(forKey: id)
            operationTasks.removeValue(forKey: id)
            operationLabels.removeValue(forKey: id)
            access.remove(id)
            if draining { similarMode.workers.removeValue(forKey: id) }
        }
        let cancel: @Sendable () -> Void = { task.cancel(); gate.finish(.failure(MusicServiceError.cancelled)) }
        operations[id] = cancel
        operationTasks[id] = task
        operationLabels[id] = label
        access.register(id, cancel: cancel)
        if draining { similarMode.workers[id] = task }
        let timer = Task {
            do { try await Task.sleep(nanoseconds: 30_000_000_000) } catch { return }
            gate.finish(.failure(MusicServiceError.timeout)); task.cancel()
        }
        defer { timer.cancel() }
        do {
            let value = try await withTaskCancellationHandler { try await gate.value() } onCancel: { cancel() }
            if draining { await task.value }
            try check(own); return value
        } catch {
            if draining { await task.value }
            if !Task.isCancelled, let declaration = error as? MusicSourceDeclarationFailure { throw declaration }
            throw MusicServiceError.sanitized(error)
        }
    }
    func song(_ ref: MusicReference, _ state: MusicDocument) throws -> MJ {
        switch ref.kind {
        case .library: return try repositorySong(ref.id, state)
        case .queue:
            try musicRequire(state.queue["entries"].isArray, .corruptDocument)
            let rows = state.queue["entries"].array.filter { $0["id"].string == ref.id }
            guard let row = rows.first else { throw MusicServiceError.notFound }
            try musicRequire(rows.count == 1, .corruptDocument)
            return try entrySong(row, state)
        }
    }
    private func drainOperations() async {
        for task in operationTasks.values { await task.value }
    }
    public func platforms() async throws -> [MusicPlatform] {
        try check()
        let inventory: [MusicPlatform] = [.init(id: "wy", title: "网易云音乐"), .init(id: "tx", title: "QQ 音乐"), .init(id: "kg", title: "酷狗音乐"),
            .init(id: "kw", title: "酷我音乐", lyrics: true, accountState: "unsupported"),
            .init(id: "mg", title: "咪咕音乐", lyrics: true, accountState: "unsupported")]
        return inventory.filter { platformEnabled($0.id) }
    }
    public func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        try check(); try requirePlatform(platform)
        let own = epoch
        let request = beginSourceDiscoveryObservation(platform: platform, query: query, page: page, limit: limit, scope: .page)
        let port = publicPlatforms
        do {
            let result = try await work {
                try await port.search(platform: platform, query: query, page: page, limit: limit)
            }
            let observation = finishSourceDiscoveryObservation(request)
            let observed = try await recordSearchPage(result, observation: observation)
            let projected = try await projectSongMetadata(observed)
            try check(own)
            let items = try displayTracks(result.items, candidates: result.items.map { try? MusicModel.normalize($0) },
                                          songs: projected)
            return .init(platform: platform, query: query, page: page, limit: limit, total: result.total,
                         nextPage: result.nextPage, items: items, observation: observation)
        } catch {
            if (try? check(own)) != nil {
                await recordSearchFailure(request, failure: MusicServiceError.sanitized(error))
            }
            throw error
        }
    }
    public func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeRecommendations(platform: platform, kind: kind, cursor: cursor) }
        return try await metadataPage(result)
    }
    public func accountCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeCollections(platform: platform, cursor: cursor) }
        return result
    }
    public func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeTracks(platform: platform, collectionID: collectionID, cursor: cursor) }
        return try await metadataPage(result)
    }
}

/// Logical cancellation is prompt. The owner tracks physical work until it
/// settles, including a non-cooperative injected adapter.
final class MusicNativeOperation<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<T, Error>?
    private var continuation: CheckedContinuation<T, Error>?
    func finish(_ value: Result<T, Error>) {
        lock.lock()
        guard result == nil else { lock.unlock(); return }
        result = value; let c = continuation; continuation = nil; lock.unlock()
        c?.resume(with: value)
    }
    func value() async throws -> T {
        try await withCheckedThrowingContinuation { c in
            lock.lock()
            if let result { lock.unlock(); c.resume(with: result) }
            else { continuation = c; lock.unlock() }
        }
    }
}

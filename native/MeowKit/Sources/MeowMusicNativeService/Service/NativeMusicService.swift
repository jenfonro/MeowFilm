import Foundation
import MeowMusicFeature
import MeowContracts
import MeowMusicPlatform

struct MusicDocument {
    var envelope: MJ
    var library: MJ
    var queue: MJ
    var playback: MJ
    var history: MJ
    var catalogSource: MJ
    init(_ data: Data?, runID: String) throws {
        envelope = try data.map { try MJ.parse($0) } ?? .obj([("version", .n(1)), ("scope", .string("device")), ("revision", .n(0)), ("records", .obj())])
        try envelope.requireFields(["version", "scope", "revision", "records"], required: ["version", "scope", "revision", "records"])
        try musicRequire(envelope["version"] == .n(1) && envelope["scope"].string == "device" &&
                         envelope["revision"].isInteger && envelope["records"].isObject, .corruptDocument)
        let records = envelope["records"]
        let allowed = ["library", "queue", "history", "playback", "catalogSource"]
        try musicRequire(records.fields.allSatisfy { allowed.map(MusicModel.key).contains($0.0) }, .corruptDocument)
        library = records.contains(MusicModel.key("library")) ? records[MusicModel.key("library")] : MusicModel.library
        queue = records.contains(MusicModel.key("queue")) ? records[MusicModel.key("queue")] : MusicModel.queue
        playback = records.contains(MusicModel.key("playback")) ? records[MusicModel.key("playback")] : MusicModel.playback
        history = records.contains(MusicModel.key("history")) ? records[MusicModel.key("history")] : MusicModel.history
        catalogSource = records.contains(MusicModel.key("catalogSource")) ? records[MusicModel.key("catalogSource")] : MusicModel.catalogSource
        try validate()
        if queue["runId"].string != runID { queue["history"] = .array([]); queue["failed"] = .array([]) }
        queue["runId"] = .string(runID)
        if playback["position"]["occurrenceId"] != queue["currentId"] { resetPosition() }
    }
    mutating func resetPosition() { playback["position"] = .obj([("occurrenceId", queue["currentId"]), ("seconds", .n(0))]) }
    func validate() throws {
        try musicRequire(library["schema"] == .n(2) && library["scope"].string == "device" && library["revision"].isInteger, .corruptDocument)
        try MusicLibraryValidation.validate(library["library"], owner: "device")
        _ = try MusicNativeSyncLedger.load(library)
        try musicRequire(queue["schema"] == .n(1) && queue["revision"].isInteger && queue["serial"].isInteger &&
                         queue["entries"].isArray, .corruptDocument)
        var seen = Set<String>(), serial = queue["serial"].int
        for entry in queue["entries"].array {
            let id = entry["id"].string, songID = entry["songId"].string
            try musicRequire(!id.isEmpty && seen.insert(id).inserted &&
                library["library"]["songs"][songID].isObject, .corruptDocument)
            if let number = Int(id.dropFirst(6)) { serial = max(serial, number) }
        }
        try musicRequire(serial <= 9_007_199_254_740_991 && queue["serial"].int >= serial &&
            (queue["currentId"] == .null || seen.contains(queue["currentId"].string)), .corruptDocument)
        for key in ["priority", "history", "failed"] {
            try musicRequire(queue[key].isArray && queue[key].array.allSatisfy {
                $0.isString && seen.contains($0.string) && (key != "priority" || $0 != queue["currentId"])
            }, .corruptDocument)
        }
        if queue["similar"] != .null {
            try MusicNativeSimilarData.validateSession(queue["similar"], serial: queue["serial"].int, songs: library["library"]["songs"])
        }
        try musicRequire(playback["schema"] == .n(1) && playback["revision"].isInteger &&
            playback["mode"].isInteger && MusicPlaybackMode(rawValue: playback["mode"].int) != nil &&
            playback["lyricsEnabled"].isBool && playback["showTranslation"].isBool &&
            playback["position"]["seconds"].isNumber && playback["position"]["seconds"].number.isFinite && playback["position"]["seconds"].number >= 0, .corruptDocument)
        for (key, allowed) in [("qualityEnabled", MusicModel.qualityOrder), ("qualityOrder", MusicModel.qualityOrder),
                              ("spatialEnabled", MusicModel.spatialOrder), ("spatialOrder", MusicModel.spatialOrder)] {
            let value = playback["qualitySettings"][key]
            if value != .null { try musicRequire(value.isArray && Set(value.strings).count == value.array.count && value.strings.allSatisfy(allowed.contains), .corruptDocument) }
        }
        if playback.contains("requestQuality") {
            try musicRequire(MusicSourceQualityPolicy.qualities.contains(playback["requestQuality"].string), .corruptDocument)
        }
        if playback.contains("fmSelections") {
            try musicRequire(playback["fmSelections"].isObject, .corruptDocument)
            for (scope, value) in playback["fmSelections"].fields {
                try musicRequire(!scope.isEmpty, .corruptDocument)
                do {
                    let action = try JSONDecoder().decode(MusicHomeAction.self, from: value.bytes)
                    try musicRequire(action.isPersonalFM, .corruptDocument)
                } catch { throw MusicServiceError.corruptDocument }
            }
        }
        if playback.contains("sourceExclusions") {
            try musicRequire(playback["sourceExclusions"].isArray, .corruptDocument)
            for row in playback["sourceExclusions"].array {
                try row.requireFields(["id", "songID", "title", "artist", "provider", "platform"],
                                      required: ["id", "songID", "title", "artist", "provider", "platform"])
                try musicRequire(UUID(uuidString: row["id"].string) != nil && !row["songID"].string.isEmpty &&
                    !row["provider"].string.isEmpty && MusicModel.sites.contains(row["platform"].string), .corruptDocument)
            }
        }
        if playback.contains("recordingSourceExclusions") {
            try musicRequire(playback["recordingSourceExclusions"].isArray, .corruptDocument)
            for row in playback["recordingSourceExclusions"].array {
                let fields = ["id", "songID", "title", "artist", "platform", "sourceKey"]
                try row.requireFields(fields, required: fields)
                try musicRequire(fields.allSatisfy { row[$0].isString } &&
                    UUID(uuidString: row["id"].string) != nil && !row["songID"].string.isEmpty &&
                    !row["sourceKey"].string.isEmpty &&
                    MusicModel.sites.contains(row["platform"].string), .corruptDocument)
            }
        }
        let official = playback["officialSources"]
        try musicRequire(official.isArray && official.array.allSatisfy(\.isString) &&
            Set(official.strings).count == official.array.count &&
            official.strings.allSatisfy { MusicAccountPlatform(rawValue: $0) != nil }, .corruptDocument)
        try musicRequire(history["schema"] == .n(1) && history["playlists"].isArray && history["playlists"].array.count <= 5 &&
                         history["days"].isArray && history["days"].array.count <= 7, .corruptDocument)
        try catalogSource.requireFields(["schema", "revision", "platform"], required: ["schema", "revision", "platform"])
        try musicRequire(catalogSource["schema"] == .n(1) && catalogSource["revision"].isInteger &&
            catalogSource["platform"].isString && ["wy", "kg"].contains(catalogSource["platform"].string), .corruptDocument)
    }
    mutating func encode(changed: Set<String>) throws -> Data {
        var records = envelope["records"]
        for area in changed {
            let value: MJ
            switch area {
            case "library": value = library
            case "queue": value = queue
            case "playback":
                // queue-state.js persists occurrence/priority, not elapsed
                // media time. Session seek/resume remains in the native owner.
                var durable = playback
                durable["position"]["seconds"] = .n(0)
                value = durable
            case "history": value = history
            case "catalogSource": value = catalogSource
            default: throw MusicServiceError.invalidRequest
            }
            records[MusicModel.key(area)] = value
        }
        try musicRequire(envelope["revision"].int < 9_007_199_254_740_991, .limit)
        envelope["records"] = records; envelope["revision"] = .n(envelope["revision"].int + 1)
        var data = envelope.bytes
        while data.count > 8 * 1024 * 1024, !history["playlists"].array.isEmpty {
            // Archives are newest-first. Evict only the oldest archive before
            // rejecting an oversized queue/library write.
            var playlists = history["playlists"].array
            playlists.removeLast()
            history["playlists"] = .array(playlists)
            records[MusicModel.key("history")] = history
            envelope["records"] = records
            data = envelope.bytes
        }
        try musicRequire(data.count <= 8 * 1024 * 1024, .limit); return data
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
    let document: any MusicNativeDocumentStoring
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
    var songObservers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var anonymousHomeBindings: [MusicPublicBrowsePlatform: MusicNativeHomeBinding] = [:]
    var lastHomeScope: String?
    var lastHomeObservation: MusicNativeCacheScope?
    var lastHomeRefreshObservation: MusicNativeCacheScope?
    let now: @Sendable () -> Date
    let random: @Sendable () -> Double
    let runID = UUID().uuidString.lowercased()
    private var sessionPosition = MusicModel.playback["position"]
    private var parsedDocument: (data: Data, runID: String, state: MusicDocument)?
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
                                   reader: MusicNativePublicBrowseQueueRead, bytes: Int)] = [:]
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

    public init(document: any MusicNativeDocumentStoring, scripts: (any MusicScriptResolving)? = nil,
                accounts: (any MusicNativeAccountProviding)? = nil, platformClient: MusicPlatformClient,
                sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                enabledPlatforms: Set<MusicPublicBrowsePlatform> = Set(MusicPublicBrowsePlatform.allCases),
                homeSnapshot: (any MusicHomeSnapshotStoring)? = nil,
                audioFileCache: (any MusicAudioFileCaching)? = nil,
                presentationCache: (any MusicPresentationCaching)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() },
                monotonicClock: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
                random: @escaping @Sendable () -> Double = { Double.random(in: 0..<1) }) {
        self.document = document; self.scripts = scripts; self.accounts = accounts; self.homeSnapshot = homeSnapshot
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
        do { return try documentState(access.perform { try document.read() }) }
        catch let e as MusicServiceError { throw e }
        catch { throw MusicServiceError.storageFailed }
    }
    private func documentState(_ data: Data?) throws -> MusicDocument {
        var state: MusicDocument
        if let data, let cached = parsedDocument, cached.runID == runID, cached.data == data {
            state = cached.state
        } else {
            state = try MusicDocument(data, runID: runID)
            parsedDocument = data.map { ($0, runID, state) }
        }
        if sessionPosition["occurrenceId"] == state.queue["currentId"] {
            state.playback["position"] = sessionPosition
        } else { state.resetPosition() }
        return state
    }
    func transaction<T>(_ areas: Set<String>, reportingCancellationAfterCommit: Bool = true,
                        _ change: (inout MusicDocument) throws -> T) throws -> T {
        try check()
        let old: Data?
        do { old = try access.perform { try document.read() } } catch { throw MusicServiceError.sanitized(error) }
        var draft = try documentState(old)
        let original = draft
        let originalLibrary = draft.library
        let value = try change(&draft)
        try draft.validate(); try check()
        var changed = areas
        if draft.library != originalLibrary { changed.insert("library") }
        changed = changed.filter { area in
            switch area {
            case "library": return draft.library != original.library
            case "queue": return draft.queue != original.queue
            case "playback": return draft.playback != original.playback
            case "history": return draft.history != original.history
            case "catalogSource": return draft.catalogSource != original.catalogSource
            default: return true
            }
        }
        if changed.isEmpty { return value }
        let bytes = try draft.encode(changed: changed)
        do { try access.perform { try document.compareAndSwap(expected: old, replacement: bytes) } }
        catch let e as MusicServiceError { throw e }
        catch { throw MusicServiceError.storageFailed }
        // Publish the session time only after the same queue/storage CAS
        // commits; a failed write must not silently advance or rewind it.
        sessionPosition = draft.playback["position"]
        if areas.contains("queue") {
            let entries = Set(draft.queue["entries"].array.map { $0["id"].string })
            queueFeedbackOrigins = queueFeedbackOrigins.filter { entries.contains($0.key) }
        }
        if changed.contains("library") { scheduleSourceDiscoveryPersist() }
        if draft.library["library"]["songs"] != original.library["library"]["songs"] {
            for observer in songObservers.values { observer.yield(()) }
            if similarMode.preview != nil || similarMode.active != nil || similarMode.queue != nil { emitSimilar() }
        }
        // Synchronous CAS cannot interleave setAccess on this actor. Task
        // cancellation can race the commit; do not claim that it rolled back.
        if reportingCancellationAfterCommit && Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
        return value
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
            guard let row = state.queue["entries"].array.first(where: { $0["id"].string == ref.id }) else { throw MusicServiceError.notFound }
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
            let items = try metadataTracks(projected).items
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

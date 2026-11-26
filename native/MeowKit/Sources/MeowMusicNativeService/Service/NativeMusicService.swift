import Foundation
import MeowContracts
import MeowDebug
import MeowMusicFeature
import MeowMusicPlatform

enum MusicDomainSection: Hashable, Sendable {
    case library, queue, history, playback, catalogSource
}

struct MusicDomainState: Sendable {
    var revision: Int
    var library: MusicLibraryState
    var queue: MusicQueueState
    var playback: MusicPlaybackSettingsState
    var history: MusicHistoryState
    var catalogSource: MusicCatalogSourceState
    var persistedSections: Set<MusicDomainSection>
    let songRows: any MusicNativeLibraryReading

    init(snapshot: MusicLibrarySnapshot, runID: String, songs: any MusicNativeLibraryReading) {
        revision = snapshot.revision
        songRows = songs
        var persisted = Set<MusicDomainSection>()
        if let lib = snapshot.library {
            library = lib
            persisted.insert(.library)
        } else {
            library = .default
        }
        if var q = snapshot.queue {
            if q.runID != runID {
                q.history = []
                q.failed = []
                q.runID = runID
            }
            queue = q
            persisted.insert(.queue)
        } else {
            var q = MusicQueueState.default
            q.runID = runID
            queue = q
        }
        if let pb = snapshot.playback {
            playback = pb
            persisted.insert(.playback)
        } else {
            playback = .default
        }
        if let hist = snapshot.history {
            history = hist
            persisted.insert(.history)
        } else {
            history = .default
        }
        if let cat = snapshot.catalogSource {
            catalogSource = cat
            persisted.insert(.catalogSource)
        } else {
            catalogSource = .default
        }
        persistedSections = persisted
        if playback.positionOccurrenceID != queue.currentID {
            resetPosition()
        }
    }

    mutating func resetPosition() {
        playback.positionOccurrenceID = queue.currentID
        playback.positionSeconds = 0
    }

    var persistedPlayback: MusicPlaybackSettingsState {
        var value = playback
        value.positionSeconds = 0
        return value
    }

    func song(_ id: String) throws -> MusicSongRecord {
        guard let record = try songRows.song(id: id) else { throw MusicServiceError.notFound }
        return record
    }

    func projectSongs(_ ids: [String]) throws -> [String: MusicSongRecord] {
        let unique = Set(ids.filter { !$0.isEmpty })
        if unique.isEmpty { return [:] }
        let rows = try songRows.songs(ids: unique)
        var result: [String: MusicSongRecord] = [:]
        result.reserveCapacity(unique.count)
        for id in unique {
            guard let record = rows[id] else { throw MusicServiceError.notFound }
            result[id] = record
        }
        return result
    }

    func referenceSongs(_ ids: [String]) throws -> [String: MusicSongRecord] {
        do { return try projectSongs(ids) }
        catch MusicServiceError.notFound { throw MusicServiceError.corruptStorage }
    }

    func queueSongs() throws -> [String: MusicSongRecord] {
        var ids = queue.entries.map(\.songID)
        if let similar = queue.similar {
            if let ret = similar.returnQueue { ids += ret.entries.map(\.songID) }
            if let seedID = similar.seedSongID { ids.append(seedID) }
            ids += similar.batches.compactMap(\.basedOnSongID)
        }
        return try referenceSongs(ids)
    }

    func referencedSongIDs(in sections: Set<MusicDomainSection>) -> Set<String> {
        var ids = Set<String>()
        if sections.contains(.library) {
            for collection in library.collections {
                ids.formUnion(collection.order)
                ids.formUnion(collection.intents.map(\.intentKey))
                for binding in collection.bindings {
                    ids.formUnion(binding.map.map(\.songID))
                }
            }
        }
        if sections.contains(.queue) {
            ids.formUnion(queue.entries.map(\.songID))
            if let similar = queue.similar {
                if let ret = similar.returnQueue { ids.formUnion(ret.entries.map(\.songID)) }
                if let seedID = similar.seedSongID { ids.insert(seedID) }
                ids.formUnion(similar.batches.compactMap(\.basedOnSongID))
            }
        }
        ids.remove("")
        return ids
    }

    func requireRevision(_ section: MusicDomainSection) throws {
        switch section {
        case .library:
            try musicRequire((0...musicMaxSafeInteger).contains(library.revision) &&
                library.ownerID == "device" &&
                (0...musicMaxSafeInteger).contains(library.libraryRevision), .corruptStorage)
        case .queue:
            try musicRequire((0...musicMaxSafeInteger).contains(queue.revision), .corruptStorage)
        case .playback:
            try musicRequire((0...musicMaxSafeInteger).contains(playback.revision), .corruptStorage)
        case .catalogSource:
            try musicRequire((0...musicMaxSafeInteger).contains(catalogSource.revision), .corruptStorage)
        case .history:
            return
        }
    }

    func requireQueue(knownSongIDs: Set<String> = []) throws {
        try musicRequire((0...musicMaxSafeInteger).contains(queue.revision) &&
                         (0...musicMaxSafeInteger).contains(queue.serial), .corruptStorage)
        let missingSongIDs = Set(queue.entries.map(\.songID)).subtracting(knownSongIDs)
        let existingSongIDs = missingSongIDs.isEmpty ? Set<String>() : try songRows.containsSongs(ids: missingSongIDs)
        var seen = Set<String>()
        var serial = queue.serial
        for entry in queue.entries {
            try musicRequire(!entry.id.isEmpty && seen.insert(entry.id).inserted &&
                (knownSongIDs.contains(entry.songID) || existingSongIDs.contains(entry.songID)), .corruptStorage)
            if let number = Int(entry.id.dropFirst(6)) { serial = max(serial, number) }
        }
        try musicRequire(serial <= musicMaxSafeInteger && queue.serial >= serial &&
            (queue.currentID == nil || seen.contains(queue.currentID!)), .corruptStorage)
        try musicRequire(queue.priority.allSatisfy { seen.contains($0) && $0 != queue.currentID }, .corruptStorage)
        try musicRequire(queue.history.allSatisfy { seen.contains($0) }, .corruptStorage)
        try musicRequire(queue.failed.allSatisfy { seen.contains($0) }, .corruptStorage)
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
    let database: MusicNativeLibraryDatabase
    var repositoryReader: any MusicNativeLibraryReading { database }
    var activeSongWriter: (any MusicNativeLibraryWriting)?
    let scripts: (any MusicScriptResolving)?
    let accounts: (any MusicNativeAccountProviding)?
    let publicPlatforms: MusicNativePlatforms
    let sourceSessionStore = MusicNativeSourceSessionStore()
    let sourceDiscovery: MusicNativePersistentSourceDiscoveryCache
    var sourceDiscoveryPersistTask: Task<Void, Never>?
    var sourceDiscoveryPersistPending = false
    var sourceDiscoveryObservationOrder: UInt64 = 0
    var sourceDiscoveryLatest: [String: [String: MusicSearchObservation]] = [:]
    let homeSnapshot: MusicNativeHomeSnapshotStorage?
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
    let historyDayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()
    let runID = UUID().uuidString.lowercased()
    private var sessionPosition: (occurrenceID: String?, seconds: Double) = (nil, 0)
    private var cachedState: (revision: Int, state: MusicDomainState)?
    var closed = false, epoch = UUID()
    var lyricEpoch = UUID()
    struct Attempt: Sendable {
        let id: String, occurrence: String, sourceKey: String
        var selection: MusicPlaybackSelection
        var scriptSource: MusicScriptSource? = nil
        var ready = false, started = false, ended = false, endedConsumed = false
        var isPlaying = false
        var recovery: MusicNativePlaybackRecoveryPin? = nil
        var authorization: MusicQueueAuthorization? = nil
        var latestFact: MusicPlaybackFact? = nil
    }
    var attempt: Attempt?
    var preparedSources: [String: MusicNativePreparedSource] = [:]
    var preparedCacheSources: [String: MusicNativePreparedCacheSource] = [:]
    var publicCollectionRevision = UUID()
    var publicCollectionRefreshes = 0
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
    private var feedbackCloseTask: Task<Void, Never>?
    private var closeTask: Task<Void, Never>?
    var feedbackClosing = false
    var queueFeedbackOrigins: [String: MusicNativeQueueFeedbackOrigin] = [:]
    var similarMode = MusicNativeSimilarRuntime()
    var fmPlayback: MusicNativeFMSession?
    var fmPreviews: [MusicHomeAction: MusicNativeFMPreviewEntry] = [:]
    var fmPreviewOrder: [MusicHomeAction] = []
    var fmQueues: [UUID: (MusicHomeAction, MusicQueueAuthorization, MusicNativeFMRead)] = [:]

    nonisolated func now() -> Date { Date() }

    public init(database: MusicNativeLibraryDatabase, scripts: (any MusicScriptResolving)? = nil,
                accounts: (any MusicNativeAccountProviding)? = nil, platformClient: MusicPlatformClient,
                sourceDiscovery: MusicNativePersistentSourceDiscoveryCache,
                enabledPlatforms: Set<MusicPublicBrowsePlatform> = Set(MusicPublicBrowsePlatform.allCases),
                homeSnapshot: MusicNativeHomeSnapshotStorage? = nil,
                audioFileCache: (any MusicAudioFileCaching)? = nil,
                presentationCache: (any MusicPresentationCaching)? = nil) {
        self.database = database
        self.scripts = scripts; self.accounts = accounts; self.homeSnapshot = homeSnapshot
        self.audioFileCache = audioFileCache
        self.presentationCache = presentationCache
        self.sourceDiscovery = sourceDiscovery
        playbackFeedback = MusicNativeFeedbackController()
        self.enabledPlatforms = enabledPlatforms
        publicPlatforms = MusicNativePlatforms(client: platformClient)
        let feedback = playbackFeedback
        access.register(UUID()) { feedback.invalidate() }
    }

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
    }

    public func close() async {
        if closeTask == nil { closeTask = Task { await finishClose() } }
        await closeTask?.value
    }

    private func finishClose() async {
        await prepareForClose()
        await flushSourceDiscoveryPersist()
        if closed { return }
        access.close()
        closed = true; epoch = UUID(); clearTransient()
        if let previousSession = await sourceSessionStore.resetAll() {
            await previousSession.close()
        }
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
        // audioFileCache is closed by MeowfilmAssembly after service/image owners drain.
        do {
            try database.close()
        } catch {
            DebugRuntime.logError(.storage, sub: "music.service", event: "databaseClose.error", error: error)
        }
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
        clearPreparedSources()
        attempt = nil; spent.removeAll()
        sourceDiscoveryLatest.removeAll()
        cachedState = nil
        lyricEpoch = UUID()
    }

    func invalidatePlayback(reason: MusicPlaybackFeedbackReport.Reason) {
        if let admission = playbackFeedbackAdmission, admission.retiringReason == nil,
           admission.authorization?.isActive == false {
            playbackFeedback.invalidate(attemptID: admission.attemptID)
            playbackFeedbackAdmission = nil
        } else if let admission = playbackFeedbackAdmission, playbackFeedback.ownsAttempt(admission.attemptID) {
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
        retireAudioCacheAttempt()
        attempt = nil; lyricEpoch = UUID()
        cancelWork("playback"); cancelWork("lyrics")
    }

    func check(_ expected: UUID? = nil) throws {
        try musicRequire(!closed, .closed)
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
    nonisolated static func officialEnabled(_ platform: String, _ playback: MusicPlaybackSettingsState) -> Bool {
        MusicModel.officialSources(playback).contains { $0.rawValue == platform }
    }

    func read() throws -> MusicDomainState {
        try check()
        do {
            let currentRevision = try repositoryReader.readRevision()
            var state: MusicDomainState
            if let cached = cachedState, cached.revision == currentRevision {
                state = cached.state
            } else {
                let snapshot = try repositoryReader.readSnapshot()
                state = MusicDomainState(snapshot: snapshot, runID: runID, songs: repositoryReader)
                cachedState = (snapshot.revision, state)
            }
            if sessionPosition.occurrenceID == state.queue.currentID {
                state.playback.positionOccurrenceID = sessionPosition.occurrenceID
                state.playback.positionSeconds = sessionPosition.seconds
            } else {
                state.resetPosition()
            }
            return state
        } catch let error as MusicServiceError { throw error }
        catch { throw MusicServiceError.storageFailed }
    }

    func transaction<T>(_ sections: Set<MusicDomainSection>, reportingCancellationAfterCommit: Bool = true,
                        _ change: (inout MusicDomainState) throws -> T) throws -> T {
        try check()
        let original = try read()
        guard let originalRevision = cachedState?.revision else { throw MusicServiceError.storageConflict }
        var draft = original
        for section in sections { try draft.requireRevision(section) }
        var changedSongs = Set<String>()
        var persistedChanged = Set<MusicDomainSection>()
        let value: T = try withUnsafeCurrentTask { task in
            try database.withTransaction(commit: { action in
                try access.perform {
                    if task?.isCancelled == true { throw MusicServiceError.cancelled }
                    try action()
                }
            }) { writer in
                try musicRequire(try writer.readRevision() == originalRevision, .storageConflict)
                activeSongWriter = writer
                defer { activeSongWriter = nil }
                let result = try change(&draft)
                persistedChanged = sections.union([.library]).filter { section in
                    switch section {
                    case .library: return draft.library != original.library
                    case .queue: return draft.queue != original.queue
                    case .playback: return draft.persistedPlayback != original.persistedPlayback
                    case .history: return draft.history != original.history
                    case .catalogSource: return draft.catalogSource != original.catalogSource
                    }
                }
                for section in persistedChanged { try draft.requireRevision(section) }
                let retained = original.referencedSongIDs(in: persistedChanged)
                let addedSongIDs = draft.referencedSongIDs(in: persistedChanged).subtracting(retained)
                if !addedSongIDs.isEmpty {
                    for id in addedSongIDs {
                        try musicRequire(UUID(uuidString: id) != nil, .corruptStorage)
                    }
                    let existing = try writer.containsSongs(ids: addedSongIDs)
                    try musicRequire(existing.count == addedSongIDs.count, .corruptStorage)
                }
                if persistedChanged.contains(.queue) { try draft.requireQueue(knownSongIDs: retained) }
                if task?.isCancelled == true { throw MusicServiceError.cancelled }
                if persistedChanged.contains(.library) {
                    try writer.applyLibraryChanges(
                        from: original.persistedSections.contains(.library) ? original.library : nil,
                        to: draft.library
                    )
                }
                if persistedChanged.contains(.queue) {
                    try writer.applyQueueChanges(
                        from: original.persistedSections.contains(.queue) ? original.queue : nil,
                        to: draft.queue
                    )
                }
                if persistedChanged.contains(.history) {
                    try writer.applyHistoryChanges(
                        from: original.persistedSections.contains(.history) ? original.history : nil,
                        to: draft.history
                    )
                }
                if persistedChanged.contains(.playback) {
                    try writer.applyPlaybackChanges(
                        from: original.persistedSections.contains(.playback) ? original.persistedPlayback : nil,
                        to: draft.persistedPlayback
                    )
                }
                if persistedChanged.contains(.catalogSource) {
                    try writer.saveCatalogSource(draft.catalogSource)
                }
                changedSongs = writer.changedSongIDs
                return result
            }
        }
        let positionChanged = draft.playback.positionOccurrenceID != original.playback.positionOccurrenceID ||
            draft.playback.positionSeconds != original.playback.positionSeconds
        if persistedChanged.isEmpty && changedSongs.isEmpty {
            if positionChanged {
                sessionPosition = (draft.playback.positionOccurrenceID, draft.playback.positionSeconds)
            }
            return value
        }
        let nextRevision = originalRevision + 1
        draft.revision = nextRevision
        draft.persistedSections.formUnion(persistedChanged)
        publishTransaction(draft, revision: nextRevision, sections: sections, changed: persistedChanged, songs: changedSongs)
        if reportingCancellationAfterCommit && Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
        return value
    }

    private func publishTransaction(_ draft: MusicDomainState, revision: Int,
                                    sections: Set<MusicDomainSection>,
                                    changed: Set<MusicDomainSection>,
                                    songs: Set<String>) {
        if changed.isEmpty && songs.isEmpty { return }
        cachedState = (revision, draft)
        sessionPosition = (draft.playback.positionOccurrenceID, draft.playback.positionSeconds)
        if sections.contains(.queue) {
            let entries = Set(draft.queue.entries.map(\.id))
            queueFeedbackOrigins = queueFeedbackOrigins.filter { entries.contains($0.key) }
        }
        if changed.contains(.library) || !songs.isEmpty { scheduleSourceDiscoveryPersist() }
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
        let own = epoch, id = UUID(), gate = SingleShotAsyncGate<T>()
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

    func song(_ ref: MusicReference, _ state: MusicDomainState) throws -> MusicSongRecord {
        switch ref.kind {
        case .library: return try repositorySong(ref.id, state)
        case .queue:
            let rows = state.queue.entries.filter { $0.id == ref.id }
            guard let row = rows.first else { throw MusicServiceError.notFound }
            try musicRequire(rows.count == 1, .corruptStorage)
            return try entrySong(row, state)
        }
    }

    private func drainOperations() async {
        for task in operationTasks.values { await task.value }
    }

    public func platforms() async throws -> [MusicPlatform] {
        try check()
        let inventory: [MusicPlatform] = [
            .init(id: "wy", title: "网易云音乐"),
            .init(id: "tx", title: "QQ 音乐"),
            .init(id: "kg", title: "酷狗音乐"),
            .init(id: "kw", title: "酷我音乐", lyrics: true, accountState: "unsupported"),
            .init(id: "mg", title: "咪咕音乐", lyrics: true, accountState: "unsupported")
        ]
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
        return try await work { try await accounts.nativeCollections(platform: platform, cursor: cursor) }
    }

    public func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeTracks(platform: platform, collectionID: collectionID, cursor: cursor) }
        return try await metadataPage(result)
    }
}

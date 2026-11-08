import Foundation
import MeowMusicFeature
import MeowContracts

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
        do {
            for value in [library, queue, playback, history, catalogSource] { try MusicModel.rejectSecrets(value) }
        } catch { throw MusicServiceError.corruptDocument }
        try musicRequire(library["schema"] == .n(1) && library["scope"].string == "device" && library["revision"].isInteger, .corruptDocument)
        try MusicLibraryValidation.validate(library["library"], owner: "device")
        _ = try MusicNativeSyncLedger.load(library)
        try musicRequire(queue["schema"] == .n(1) && queue["revision"].isInteger && queue["serial"].isInteger &&
                         queue["entries"].isArray, .corruptDocument)
        var seen = Set<String>(), serial = queue["serial"].int
        var validated: [Data: MJ] = [:]
        for entry in queue["entries"].array {
            let id = entry["id"].string
            let song = entry["song"]
            try musicRequire(id.range(of: "^entry-[1-9][0-9]*$", options: .regularExpression) != nil &&
                Int(id.dropFirst(6)) != nil && seen.insert(id).inserted &&
                song.isObject && song["sources"].isArray, .corruptDocument)
            serial = max(serial, Int(id.dropFirst(6))!)
            let key = Data(song["songId"].string.utf8)
            if key.isEmpty || validated[key] != song {
                _ = try MusicModel.track(song, .init(kind: .queue, id: id))
                if !key.isEmpty { validated[key] = song }
            }
        }
        try musicRequire(serial <= 9_007_199_254_740_991 && queue["serial"].int >= serial &&
            (queue["currentId"] == .null || seen.contains(queue["currentId"].string)), .corruptDocument)
        for key in ["priority", "history", "failed"] {
            try musicRequire(queue[key].isArray && queue[key].array.allSatisfy {
                $0.isString && seen.contains($0.string) && (key != "priority" || $0 != queue["currentId"])
            }, .corruptDocument)
        }
        if queue["similar"] != .null {
            try MusicNativeSimilarData.validateSession(queue["similar"], serial: queue["serial"].int)
        }
        try musicRequire(playback["schema"] == .n(1) && playback["revision"].isInteger &&
            playback["mode"].isInteger && (0...3).contains(playback["mode"].int) &&
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
        if playback.contains("sourceExclusions") {
            try musicRequire(playback["sourceExclusions"].isArray, .corruptDocument)
            for row in playback["sourceExclusions"].array {
                try row.requireFields(["id", "songID", "title", "artist", "provider", "platform"],
                                      required: ["id", "songID", "title", "artist", "provider", "platform"])
                try musicRequire(UUID(uuidString: row["id"].string) != nil && !row["songID"].string.isEmpty &&
                    !row["provider"].string.isEmpty && MusicModel.sites.contains(row["platform"].string), .corruptDocument)
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
/// There is no Node connection or music.* RPC in this implementation.
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
    /// Descriptor inputs -> recording for current library members only.
    var sourceDiscoveryRecordingMemo: [String: MusicSourceDiscoveryRecording] = [:]
    var sourceDiscoveryLibraryRevision: Int?
    var sourceDiscoveryObservationOrder: UInt64 = 0
    let homeSnapshot: (any MusicHomeSnapshotStoring)?
    let audioFileCache: (any MusicAudioFileCaching)?
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
    var anonymousHomeBindings: [MusicPublicBrowsePlatform: MusicNativeHomeBinding] = [:]
    var lastHomeScope: String?
    var lastHomeObservation: MusicNativeCacheScope?
    var lastHomeRefreshObservation: MusicNativeCacheScope?
    let ownedTransport: URLSessionMusicNativeHTTPTransport?
    let now: @Sendable () -> Date
    let random: @Sendable () -> Double
    let runID = UUID().uuidString.lowercased()
    private var sessionPosition = MusicModel.playback["position"]
    private var parsedDocument: (data: Data, runID: String, state: MusicDocument)?
    var available = false, generation: UInt64 = 0, closed = false, epoch = UUID()
    struct Handle { let song: MJ; let at: Date; let bytes: Int }
    var handles: [String: Handle] = [:]
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
                                   reader: MusicNativePublicBrowseQueueRead, bytes: Int, expires: Date)] = [:]
    var spent = Set<String>()
    var operations: [UUID: @Sendable () -> Void] = [:]
    var operationLabels: [UUID: String] = [:]
    var librarySyncRun: (id: UUID, cancel: @Sendable () -> Void)?
    var automaticLibrarySyncRuntime = MusicNativeAutomaticSyncRuntime()
    let playbackFeedback = MusicNativeFeedbackController()
    var playbackFeedbackAdmission: MusicNativeFeedbackAdmission?
    var similarMode = MusicNativeSimilarRuntime()
    var fmPreviews: [MusicHomeAction: MusicNativeFMPreviewEntry] = [:]
    var fmPreviewOrder: [MusicHomeAction] = []
    var fmQueues: [UUID: (MusicHomeAction, MusicQueueAuthorization)] = [:]

    public init(document: any MusicNativeDocumentStoring, scripts: (any MusicScriptResolving)? = nil,
                accounts: (any MusicNativeAccountProviding)? = nil, qq: any MusicPublicQQProviding,
                sourceDiscovery: (any MusicSourceDiscoveryCaching)? = nil,
                enabledPlatforms: Set<MusicPublicBrowsePlatform> = Set(MusicPublicBrowsePlatform.allCases),
                homeSnapshot: (any MusicHomeSnapshotStoring)? = nil,
                audioFileCache: (any MusicAudioFileCaching)? = nil,
                transport: (any MusicNativeHTTPTransport)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() },
                random: @escaping @Sendable () -> Double = { Double.random(in: 0..<1) },
                clientContexts: (any MusicNativeClientContextProviding)? = nil) {
        self.document = document; self.scripts = scripts; self.accounts = accounts; self.homeSnapshot = homeSnapshot
        self.audioFileCache = audioFileCache
        self.sourceDiscovery = sourceDiscovery ?? MusicNativeMemorySourceDiscoveryCache(clock: clock)
        now = clock; self.random = random
        self.enabledPlatforms = enabledPlatforms
        let owned = transport == nil ? URLSessionMusicNativeHTTPTransport() : nil
        ownedTransport = owned
        publicPlatforms = MusicNativePlatforms(transport: transport ?? owned!, qq: qq, clock: clock, clients: clientContexts)
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
        generation = next; available = value; epoch = UUID()
        clearTransient()
        for cancel in operations.values { cancel() }
        await publicPlatforms.revoke()
        await audioFileCache?.cancelPending()
    }
    public func close() async {
        if closed { return }
        // Library recordings reach disk before access is revoked.
        await flushSourceDiscoveryPersist()
        if closed { return }
        access.revoke(generation: generation, close: true)
        closed = true; available = false; epoch = UUID(); clearTransient()
        for cancel in operations.values { cancel() }
        await publicPlatforms.revoke()
        await audioFileCache?.close()
        ownedTransport?.close()
    }
    func clearTransient() {
        publicCollectionRevision = UUID()
        for (id, entry) in publicQueueSources {
            access.remove(id); entry.authorization.revoke()
            Task { await entry.reader.close() }
        }
        publicQueueSources.removeAll()
        clearFMPreviews()
        for (id, value) in fmQueues { access.remove(id); value.1.revoke() }
        fmQueues.removeAll()
        invalidateAutomaticLibrarySync()
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        for capture in audioCacheCaptures.values { capture.cancel() }
        audioCacheCaptures.removeAll()
        clearSimilarRuntime()
        homeCache = nil
        homeLoad = nil
        homeFirstRead?.task.cancel(); homeFirstRead = nil
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
        librarySyncRun?.cancel()
        if let currentSourceSession { Task { await currentSourceSession.session.close() } }
        currentSourceSession = nil; clearPreparedSources(); sourceResults = [:]; sourceResultOrder = []
        handles.removeAll(); attempt = nil; spent.removeAll()
        parsedDocument = nil
        lyricEpoch = UUID()
    }
    func invalidatePlayback() {
        clearPreparedSources()
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        playbackFeedback.finishCurrent(reason: .ui)
        playbackFeedback.invalidate(preservingEnds: true)
        playbackFeedbackAdmission = nil
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
        let value = try change(&draft)
        try draft.validate(); try check()
        let bytes = try draft.encode(changed: areas)
        do { try access.perform { try document.compareAndSwap(expected: old, replacement: bytes) } }
        catch let e as MusicServiceError { throw e }
        catch { throw MusicServiceError.storageFailed }
        // Publish the session time only after the same queue/storage CAS
        // commits; a failed write must not silently advance or rewind it.
        sessionPosition = draft.playback["position"]
        playbackFeedbackAdmission = nil
        if areas.contains("library") { scheduleSourceDiscoveryPersist() }
        // Synchronous CAS cannot interleave setAccess on this actor. Task
        // cancellation can race the commit; do not claim that it rolled back.
        if reportingCancellationAfterCommit && Task.isCancelled { throw MusicServiceError.commitUnconfirmed }
        return value
    }
    func cancelWork(_ label: String) {
        for (id, value) in operationLabels where value == label { operations[id]?() }
    }
    func work<T: Sendable>(label: String? = nil, _ body: @escaping @Sendable () async throws -> T) async throws -> T {
        try check()
        let own = epoch, id = UUID(), gate = MusicNativeOperation<T>()
        let task = Task {
            let result: Result<T, Error>
            do { try check(own); result = .success(try await body()) }
            catch { result = .failure(error) }
            gate.finish(result)
            operations.removeValue(forKey: id)
            operationLabels.removeValue(forKey: id)
            access.remove(id)
        }
        let cancel: @Sendable () -> Void = { task.cancel(); gate.finish(.failure(MusicServiceError.cancelled)) }
        operations[id] = cancel
        operationLabels[id] = label
        access.register(id, cancel: cancel)
        let timer = Task {
            do { try await Task.sleep(nanoseconds: 30_000_000_000) } catch { return }
            gate.finish(.failure(MusicServiceError.timeout)); task.cancel()
        }
        defer { timer.cancel() }
        do {
            let value = try await withTaskCancellationHandler { try await gate.value() } onCancel: { cancel() }
            try check(own); return value
        } catch {
            if !Task.isCancelled, let declaration = error as? MusicSourceDeclarationFailure { throw declaration }
            throw MusicServiceError.sanitized(error)
        }
    }
    func song(_ ref: MusicReference, _ state: MusicDocument) throws -> MJ {
        try musicRequire(!ref.id.isEmpty)
        switch ref.kind {
        case .library:
            let song = state.library["library"]["songs"][ref.id]
            try musicRequire(song.isObject, .notFound); return try MusicModel.cleanSong(song)
        case .queue:
            guard let row = state.queue["entries"].array.first(where: { $0["id"].string == ref.id }) else { throw MusicServiceError.notFound }
            return try MusicModel.cleanSong(row["song"])
        case .handle:
            guard let handle = handles[ref.id], now().timeIntervalSince(handle.at) < 600 else { throw MusicServiceError.handleExpired }
            return handle.song
        }
    }
    func remember(_ song: MJ, maximumHandles: Int = 600) throws -> MusicRemoteTrack {
        handles = handles.filter { now().timeIntervalSince($0.value.at) < 600 }
        let bytes = song.bytes.count
        try musicRequire(bytes <= 64 * 1024, .limit)
        while handles.count >= maximumHandles || handles.values.reduce(0, { $0 + $1.bytes }) + bytes > 4 * 1024 * 1024 {
            guard let oldest = handles.min(by: { $0.value.at < $1.value.at })?.key else { throw MusicServiceError.limit }
            handles.removeValue(forKey: oldest)
        }
        let id = UUID().uuidString.lowercased()
        handles[id] = Handle(song: song, at: now(), bytes: bytes)
        return try MusicModel.track(song, .init(kind: .handle, id: id))
    }
    func page(_ value: MusicNativeTrackPage, maximumItems: Int = 30) throws -> MusicBrowsePage {
        try musicRequire(maximumItems >= 0 && value.items.count <= maximumItems, .limit)
        if let cursor = value.cursor { _ = try musicText(cursor, maximum: 64) }
        let songs = try value.items.map(MusicModel.normalize)
        let sizes = songs.map { $0.bytes.count }, bytes = sizes.reduce(0, +)
        try musicRequire(sizes.allSatisfy { $0 <= 64 * 1024 } && bytes <= 4 * 1024 * 1024, .limit)
        return try metadataHandles(songs, cursor: value.cursor)
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
        _ = try musicText(query, maximum: 800)
        guard let kind = MusicPublicBrowsePlatform(rawValue: platform) else { throw MusicServiceError.unsupported }
        _ = try MusicSearchPagination.offset(platform: kind, page: page, limit: limit)
        try await retainLibrarySongMetadata()
        try check(own)
        let request = beginSourceDiscoveryObservation(platform: platform, query: query, page: page, limit: limit, scope: .page)
        let port = publicPlatforms
        do {
            let result = try await work {
                try await port.search(platform: platform, query: query, page: page, limit: limit)
            }
            try musicRequire(result.total >= 0 &&
                (kind == .migu || result.items.count <= limit) &&
                result.items.allSatisfy { $0.platform == platform } &&
                MusicSearchPagination.validNextPage(page: page, limit: limit,
                    next: result.nextPage, hasItems: !result.items.isEmpty), .invalidResponse)
            let observation = finishSourceDiscoveryObservation(request)
            let observed = try await recordSearchPage(result, observation: observation)
            let projected = try await projectSongMetadata(observed)
            try check(own)
            let items = try metadataHandles(projected).items
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
        try musicRequire(result.items.allSatisfy { $0.platform == platform.rawValue }, .invalidResponse)
        return try await metadataPage(result)
    }
    public func accountCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeCollections(platform: platform, cursor: cursor) }
        try musicRequire(result.items.count <= 30 && musicIDs(result.items.map(\.id)).count == result.items.count, .invalidResponse)
        for item in result.items {
            _ = try musicText(item.id, maximum: 64); _ = try musicText(item.title, maximum: 600)
            if let count = item.count { try musicRequire(count >= 0, .invalidResponse) }
        }
        if let cursor = result.cursor { _ = try musicText(cursor, maximum: 64) }
        return result
    }
    public func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(platform.rawValue)
        guard let accounts else { throw MusicServiceError.unavailable }
        let result = try await work { try await accounts.nativeTracks(platform: platform, collectionID: collectionID, cursor: cursor) }
        try musicRequire(result.items.allSatisfy { $0.platform == platform.rawValue }, .invalidResponse)
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

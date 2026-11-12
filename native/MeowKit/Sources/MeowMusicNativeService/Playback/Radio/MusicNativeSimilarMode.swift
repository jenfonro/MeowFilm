// SPDX-License-Identifier: Apache-2.0
// Native owner for fixed b900c259 static/similar-mode.js.
import Foundation
import Dispatch
import MeowMusicFeature

extension NativeMusicService: MusicSimilarModeServing {
    public func similarModeSnapshot() async throws -> MusicSimilarModeSnapshot? {
        try await restoreSimilarSessionIfNeeded()
        try check()
        if let active = similarMode.active {
            guard active.lease.authorization.isActive else { try cancelSimilarModeState(); return nil }
            let retry = active.continueEnabled && active.pending == nil &&
                (active.exhausted || active.status == .error)
            return .init(id: active.id, active: true, status: active.status,
                seed: MusicNativeSimilarData.display(active.seed), seedEntryID: active.seedEntryID,
                continueEnabled: active.continueEnabled, candidates: [],
                batches: try active.batches.map(MusicNativeSimilarData.batchDTO),
                sourceIDs: active.sourceIDs, fromHome: active.fromHome, provider: active.provider,
                error: active.error, canRetry: retry, errorID: active.errorID)
        }
        guard let item = similarMode.preview else { return nil }
        if let lease = item.lease, !lease.authorization.isActive { try cancelSimilarModeState(); return nil }
        let current = try read()
        guard similarEntranceIsCurrent(item, current) else { discardSimilarPreview(); return nil }
        return .init(id: item.id, active: false, status: item.status, seed: MusicNativeSimilarData.display(item.seed),
            seedEntryID: item.entryID, continueEnabled: false,
            candidates: item.songs.enumerated().map {
                .init(id: "\(item.id.uuidString):\($0.offset)", song: MusicNativeSimilarData.display($0.element))
            }, batches: [], sourceIDs: item.sourceIDs, fromHome: item.fromHome, provider: item.provider,
            error: item.error, canRetry: !item.fromHome && [.error, .empty].contains(item.status) &&
                (item.provider == .queueRecommendations || similarCanEnter(current)), errorID: item.errorID)
    }

    public func similarModeChanges() async -> AsyncStream<Void> {
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            if closed { continuation.finish(); return }
            similarMode.observers[id] = continuation
            continuation.yield(())
            continuation.onTermination = { [weak self] _ in Task { await self?.removeSimilarObserver(id) } }
        }
    }
    func removeSimilarObserver(_ id: UUID) { similarMode.observers.removeValue(forKey: id) }
    func emitSimilar() { for observer in similarMode.observers.values { observer.yield(()) } }

    public func canEnterSimilarMode() async throws -> Bool {
        let d = try read()
        guard heartbeatSource(d), let row = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }) else { return false }
        guard platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding else { return false }
        let song = try await sharedSongMetadata(row["song"])
        return MusicNativeSimilarData.nativeID(song) != nil
    }
    public func canEnterSongRecommendations() async throws -> Bool {
        let d = try read()
        guard similarMode.active == nil, similarMode.queue == nil, fmPlayback == nil,
              d.queue["source"]["sourceType"].string != "search-song",
              let row = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }) else { return false }
        guard platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding else { return false }
        let song = try await sharedSongMetadata(row["song"])
        return MusicNativeSimilarData.nativeID(song) != nil
    }
    func heartbeatSource(_ d: MusicDocument) -> Bool {
        guard let row = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }) else { return false }
        let source = row["context"]
        return source["collectionId"].string == "favorites" ||
            (source["platform"].string == "wy" && source["sourceId"].string == "favorites")
    }
    func similarCanEnter(_ d: MusicDocument) -> Bool {
        heartbeatSource(d) && platformEnabled("wy") && accounts is any MusicNativeSimilarModeProviding &&
            d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] })
                .flatMap { MusicNativeSimilarData.nativeID($0["song"]) } != nil
    }
    public func previewSimilarMode() async throws -> MusicSimilarModeSnapshot? {
        try await restoreSimilarSessionIfNeeded()
        if similarMode.active != nil { return try await similarModeSnapshot() }
        guard let item = try await prepareSimilarPreview() else { return nil }
        let pending = item.task
        await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
        try check()
        return try await similarModeSnapshot()
    }
    func prepareSimilarPreview(provider: MusicSimilarModeProvider = .heartbeat) async throws -> MusicNativeSimilarPreview? {
        let d = try read()
        guard platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding,
              provider != .heartbeat || heartbeatSource(d),
              let entry = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }) else { return nil }
        let song = try await sharedSongMetadata(entry["song"])
        guard let seedID = MusicNativeSimilarData.nativeID(song),
              try read().queue["revision"] == d.queue["revision"] else { return nil }
        if similarMode.preview?.provider != provider || similarMode.preview?.entryID != entry["id"].string ||
            similarMode.preview.map({ !similarEntranceIsCurrent($0, d) }) != false {
            discardSimilarPreview()
            let item = MusicNativeSimilarPreview(seed: song, entryID: entry["id"].string, fromHome: false,
                provider: provider, sourceIDs: [seedID], revision: d.queue["revision"].int,
                navigation: similarMode.navigation)
            similarMode.preview = item
            item.task = Task { await loadSimilarPreview(item.id) }
            emitSimilar()
        }
        return similarMode.preview
    }
    public func enterSimilarMode() async throws -> MusicSimilarModeResult {
        let selection = similarMode.modeSelection
        try await restoreSimilarSessionIfNeeded()
        // Set enter intent before the network completes, so explicit retry can
        // commit a failed first entry just as the original enterAfter flag does.
        guard let item = try await prepareSimilarPreview() else { throw MusicServiceError.sourceUnavailable }
        try musicRequire(similarMode.modeSelection == selection, .targetChanged)
        item.enterAfter = true
        let own = epoch
        let pending = item.task
        await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
        try check(own)
        try musicRequire(similarMode.modeSelection == selection, .targetChanged)
        return try commitSimilarPreview(id: item.id)
    }
    public func startSimilarMode(_ start: MusicSimilarModeStart) async throws -> MusicSimilarModeResult {
        let selection = similarMode.modeSelection
        try await restoreSimilarSessionIfNeeded()
        try musicRequire(similarMode.modeSelection == selection, .targetChanged)
        try check(); try requirePlatform("wy")
        let provider: MusicSimilarModeProvider, ids: [String], name: String
        switch start {
        case .heartbeat: provider = .heartbeat; ids = []; name = "你的红心歌曲"
        case .heartbeatSong(let songID):
            try musicRequire(MusicNativeSimilarData.id(songID))
            provider = .heartbeat; ids = [songID]; name = "心动"
        case .recommendations(let occurrenceID, let expectedRevision):
            let d = try read()
            try musicRequire(d.queue["revision"].int == expectedRevision && d.queue["currentId"].string == occurrenceID, .targetChanged)
            let allowed = try await canEnterSongRecommendations()
            try musicRequire(allowed, .sourceUnavailable)
            guard let item = try await prepareSimilarPreview(provider: .queueRecommendations) else { throw MusicServiceError.sourceUnavailable }
            try musicRequire(similarMode.modeSelection == selection, .targetChanged)
            item.enterAfter = true
            let pending = item.task
            await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
            try check()
            try musicRequire(similarMode.modeSelection == selection, .targetChanged)
            return try commitSimilarPreview(id: item.id)
        case .currentHeartbeat(let occurrenceID, let expectedRevision):
            let d = try read()
            try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
            try musicRequire(d.queue["currentId"].string == occurrenceID, .targetChanged)
            return try await enterSimilarMode()
        case .songRoaming(let sourceIDs, let sourceName), .artistRoaming(let sourceIDs, let sourceName):
            try musicRequire(!sourceIDs.isEmpty && sourceIDs.count <= 20 && sourceIDs.allSatisfy(MusicNativeSimilarData.id))
            if !sourceName.isEmpty { _ = try musicText(sourceName, maximum: 600) }
            if case .artistRoaming = start { provider = .artistRoaming } else { provider = .songRoaming }
            ids = sourceIDs; name = sourceName.isEmpty ? (provider == .artistRoaming ? "相似艺人" : "相似歌曲") : sourceName
        }
        if let active = similarMode.active, active.fromHome, active.provider == .heartbeat, provider == .heartbeat,
           ids.isEmpty || active.sourceIDs == ids {
            try active.lease.authorization.check()
            discardSimilarPreview()
            try await setSimilarContinue(true, snapshotID: active.id)
            return .init(state: stateDTO(try read()), action: .togglePlayback)
        }
        let d = try read()
        let item: MusicNativeSimilarPreview
        if let existing = similarMode.preview, existing.fromHome, existing.provider == provider,
           existing.sourceIDs == ids, [.loading, .ready].contains(existing.status),
           similarEntranceIsCurrent(existing, d) { item = existing }
        else {
            discardSimilarPreview()
            similarMode.navigation = UUID()
            item = .init(seed: .obj([("title", .string(name))]), entryID: nil, fromHome: true,
                         provider: provider, sourceIDs: ids, revision: d.queue["revision"].int,
                         navigation: similarMode.navigation)
            similarMode.preview = item; item.task = Task { await loadSimilarPreview(item.id) }; emitSimilar()
        }
        item.enterAfter = true
        let pending = item.task
        await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
        try check()
        try musicRequire(similarMode.modeSelection == selection, .targetChanged)
        return try commitSimilarPreview(id: item.id)
    }
    public func playSimilarCandidate(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.preview, item.id == snapshotID,
              let index = item.songs.indices.first(where: { "\(item.id.uuidString):\($0)" == candidateID }) else {
            throw MusicServiceError.targetChanged
        }
        return try commitSimilarPreview(id: item.id, candidateIndex: index)
    }
    public func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) async throws {
        try check()
        guard let session = similarMode.active, session.id == snapshotID else { throw MusicServiceError.targetChanged }
        try session.lease.authorization.check()
        if !enabled && !session.continueEnabled { return }
        var saved = session.saved; saved["continue"] = .bool(enabled)
        try session.lease.authorization.perform {
            try similarQueueTransaction(enabled ? ["queue", "playback"] : ["queue"]) { d in
                d.queue["similar"] = saved
                if enabled { selectContinuationMode(&d) }
            }
        }
        session.continueEnabled = enabled
        emitSimilar()
        if enabled { try similarBoundary() }
    }
    public func retrySimilarMode(snapshotID: UUID) async throws -> MusicSimilarModeResult {
        guard let snapshot = try await similarModeSnapshot(), snapshot.id == snapshotID, snapshot.canRetry else {
            throw MusicServiceError.invalidRequest
        }
        if let active = similarMode.active {
            active.exhausted = false
            try similarBoundary(retry: true)
            let pending = active.pending
            await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
            try check()
            return .init(state: stateDTO(try read()), action: .keepCurrent)
        }
        let enterAfter = similarMode.preview?.enterAfter == true
        let selection = similarMode.modeSelection
        let provider = similarMode.preview?.provider ?? .heartbeat
        discardSimilarPreview()
        let prepared = try await prepareSimilarPreview(provider: provider)
        await prepared?.task?.value
        guard let item = similarMode.preview else { throw MusicServiceError.targetChanged }
        item.enterAfter = enterAfter && similarMode.modeSelection == selection
        if item.enterAfter { return try commitSimilarPreview(id: item.id) }
        return .init(state: stateDTO(try read()), action: .keepCurrent)
    }
    public func cancelSimilarMode() async throws { try cancelSimilarModeState() }
    func cancelSimilarModeState() throws {
        try check()
        // Session metadata does not advance the queue's occurrence revision.
        if try read().queue["similar"] != .null {
            try similarQueueTransaction(["queue"]) { $0.queue["similar"] = .null }
        }
        clearSimilarRuntime(); similarMode.restored = true
    }
    public func pauseSimilarNavigation() async {
        similarMode.navigation = UUID()
        similarMode.playing = nil
    }
    public func recordSimilarPlaybackFact(_ fact: MusicPlaybackFact) async throws -> Bool {
        let accepted = try await record(fact)
        guard accepted else { return false }
        do { try await restoreSimilarSessionIfNeeded() } catch { return accepted }
        guard attempt?.id == fact.attemptId, attempt?.occurrence == fact.occurrenceId else { return accepted }
        guard attempt?.authorization?.isActive != false else { return accepted }
        switch fact.kind {
        case .playing:
            similarMode.playing = (fact.attemptId, fact.occurrenceId)
            _ = try? await prepareQueueRecommendations()
            try? similarBoundary(); try? queueRecommendationBoundary(); try? fmBoundary()
        case .ended:
            similarMode.playing = nil
        case .paused, .stopped, .failed: similarMode.playing = nil
        case .progress: break
        }
        return accepted
    }
}

extension NativeMusicService {
    func similarEntranceIsCurrent(_ item: MusicNativeSimilarPreview, _ d: MusicDocument) -> Bool {
        similarMode.preview?.id == item.id && item.revision == d.queue["revision"].int &&
            item.navigation == similarMode.navigation && (item.fromHome || item.entryID == d.queue["currentId"].string)
    }
    func similarOwner() throws -> any MusicNativeSimilarModeProviding {
        try check(); try requirePlatform("wy")
        guard let owner = accounts as? any MusicNativeSimilarModeProviding else { throw MusicServiceError.authRequired }
        return owner
    }
    func similarWork<T: Sendable>(_ id: UUID, _ body: @escaping @Sendable () async throws -> T) async throws -> T {
        let label = "similar:" + id.uuidString
        let start = DispatchTime.now().uptimeNanoseconds
        let deadline = similarMode.deadlines[id] ?? start + 12_000_000_000
        similarMode.deadlines[id] = deadline
        guard start < deadline else { throw MusicServiceError.timeout }
        let timeout = Task {
            do { try await Task.sleep(nanoseconds: deadline - start) } catch { return }
            similarMode.timedOut.insert(id); cancelWork(label)
        }
        defer { timeout.cancel(); similarMode.timedOut.remove(id) }
        do { return try await work(label: label, draining: true, body) }
        catch {
            if similarMode.timedOut.contains(id) { throw MusicServiceError.timeout }
            throw error
        }
    }
    func loadSimilarPreview(_ id: UUID) async {
        guard let item = similarMode.preview, item.id == id else { return }
        defer { similarMode.deadlines.removeValue(forKey: id) }
        let own = epoch
        do {
            let owner = try similarOwner()
            let opening = MusicAccountQueueOpening()
            let lease: MusicAccountQueueLease
            do {
                lease = try await similarWork(id) {
                    let value = try await owner.beginAccountQueue(platform: .netease)
                    guard opening.receive(value) else {
                        value.revoke(); await owner.endAccountQueue(value); throw MusicServiceError.cancelled
                    }
                    try Task.checkCancellation(); return value
                }
                _ = opening.finish()
            } catch {
                if let value = opening.finish() { value.revoke(); await owner.endAccountQueue(value) }
                throw error
            }
            guard similarMode.preview?.id == id else { lease.revoke(); await owner.endAccountQueue(lease); return }
            item.lease = lease; watchSimilarLease(lease)
            let scope = try await similarWork(id) { try await owner.similarModeScope(lease) }
            try check(own); try lease.authorization.check()
            let request: MusicNativeSimilarModeRequest
            switch item.provider {
            case .songRoaming: request = .songRoaming(sourceIDs: item.sourceIDs)
            case .artistRoaming: request = .artistRoaming(sourceIDs: item.sourceIDs)
            case .queueRecommendations: request = .songRecommendations(seedID: item.sourceIDs[0], offset: 0)
            case .heartbeat: request = .heartbeat(lastID: item.sourceIDs.first, fromHome: item.fromHome, context: nil)
            }
            let response = try await similarWork(id) { try await owner.similarModeBatch(lease, request: request) }
            try await owner.checkAccountQueue(lease)
            try check(own); try lease.authorization.check()
            guard similarEntranceIsCurrent(item, try read()) else { discardSimilarPreview(); emitSimilar(); return }
            try musicRequire(!scope.isEmpty && scope.utf8.count <= 512, .invalidResponse)
            let songs = try MusicNativeSimilarData.songs(response)
            item.recommendations = try MusicNativeSimilarData.recommendations(response.items)
            if item.provider == .queueRecommendations {
                guard let offset = response.offset, response.hasMore != nil else { throw MusicServiceError.invalidResponse }
                item.offset = offset
            }
            item.exhausted = response.hasMore == false
            if item.provider == .heartbeat {
                guard let context = response.heartbeatContext,
                      MusicNativeSimilarData.id(context.playlistID), MusicNativeSimilarData.id(context.seedID) else {
                    throw MusicServiceError.invalidResponse
                }
                item.context = context
                if item.fromHome, item.sourceIDs.isEmpty { item.sourceIDs = [context.seedID] }
            }
            try await retainSongMetadata(songs)
            try check(own); try lease.authorization.check()
            guard similarEntranceIsCurrent(item, try read()) else { return }
            item.scope = scope
            item.songs = MusicNativeSimilarData.unique(songs, excluding: [])
            item.status = item.songs.isEmpty ? .empty : .ready
            item.task = nil; emitSimilar()
        } catch {
            guard similarMode.preview?.id == id else { return }
            if epoch != own || item.lease?.authorization.isActive == false {
                discardSimilarPreview(); emitSimilar(); return
            }
            let failure = MusicServiceError.sanitized(error)
            if failure == .cancelled { discardSimilarPreview() }
            else { item.status = .error; item.error = failure; item.errorID = id; item.task = nil }
            emitSimilar()
        }
    }
    func releaseSimilarLease(_ lease: MusicAccountQueueLease, after task: Task<Void, Never>? = nil) {
        similarMode.watchers.removeValue(forKey: lease.id)?.cancel()
        lease.revoke()
        if let owner = accounts as? any MusicNativeSimilarModeProviding {
            retainSimilarDrain(Task {
                await task?.value
                await owner.endAccountQueue(lease)
            })
        }
    }
    func retainSimilarDrain(_ task: Task<Void, Never>) {
        let id = UUID()
        similarMode.retiring[id] = Task {
            await task.value
            similarMode.retiring.removeValue(forKey: id)
        }
    }
    func drainSimilarWork() async {
        while !similarMode.retiring.isEmpty || !similarMode.workers.isEmpty {
            let tasks = Array(similarMode.retiring.values) + Array(similarMode.workers.values)
            for task in tasks { await task.value }
        }
    }
    func watchSimilarLease(_ lease: MusicAccountQueueLease) {
        let id = lease.id
        similarMode.watchers[id] = Task { [weak self] in
            for await _ in lease.authorization.revocations() {
                if Task.isCancelled { return }
                await self?.similarLeaseRevoked(id)
                return
            }
        }
    }
    func similarLeaseRevoked(_ id: UUID) {
        guard similarMode.active?.lease.id == id || similarMode.preview?.lease?.id == id ||
            similarMode.queue?.lease?.id == id else { return }
        playbackFeedbackAdmission = nil; playbackFeedback.revoke(platform: .netease)
        // A revoked account must never resurrect from an old persisted session.
        if available, !closed, let d = try? read(), d.queue["similar"] != .null {
            try? transaction(["queue"]) { $0.queue["similar"] = .null }
        }
        clearSimilarRuntime(); similarMode.restored = true
    }
    func discardSimilarPreview() {
        guard let item = similarMode.preview else { return }
        similarMode.preview = nil; item.task?.cancel()
        if let lease = item.lease { releaseSimilarLease(lease, after: item.task) }
        else if let task = item.task { retainSimilarDrain(task) }
    }
    func cancelSimilarBatch(_ session: MusicNativeSimilarSession) {
        session.pending?.cancel()
        session.pendingTail = nil
    }
    func clearSimilarRuntime() {
        if let restoring = similarMode.restoring { cancelWork("similar:" + restoring.uuidString) }
        if let completion = similarMode.restorationCompletion {
            retainSimilarDrain(Task { _ = try? await completion.value() })
            similarMode.restorationCompletion = nil
        }
        discardSimilarPreview()
        if let active = similarMode.active {
            similarMode.active = nil; cancelSimilarBatch(active)
            releaseSimilarLease(active.lease, after: active.pending)
        }
        discardQueueRecommendations()
        similarMode.navigation = UUID(); similarMode.playing = nil
        similarMode.restored = false; similarMode.restoring = nil
        emitSimilar()
        if closed {
            for observer in similarMode.observers.values { observer.finish() }
            similarMode.observers.removeAll()
        }
    }
}

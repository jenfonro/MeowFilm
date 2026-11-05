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
            let tail = try read().queue["entries"].array.last
            let retry = active.continueEnabled && [.error, .empty].contains(active.status) &&
                active.attempted.contains(tail?["id"].string ?? "") && !active.sourceIDs.isEmpty &&
                (active.provider == .songRoaming || tail.flatMap { MusicNativeSimilarData.nativeID($0["song"]) } != nil)
            return .init(id: active.id, active: true, status: active.status,
                seed: MusicNativeSimilarData.display(active.seed), seedEntryID: active.seedEntryID,
                continueEnabled: active.continueEnabled, candidates: [],
                batches: try active.batches.map(MusicNativeSimilarData.batchDTO),
                sourceIDs: active.sourceIDs, fromHome: active.fromHome, provider: active.provider,
                error: active.error, canRetry: retry)
        }
        guard let item = similarMode.preview else { return nil }
        if let lease = item.lease, !lease.authorization.isActive { try cancelSimilarModeState(); return nil }
        let current = try read()
        return .init(id: item.id, active: false, status: item.status, seed: MusicNativeSimilarData.display(item.seed),
            seedEntryID: item.entryID, continueEnabled: true,
            candidates: item.songs.enumerated().map {
                .init(id: "\(item.id.uuidString):\($0.offset)", song: MusicNativeSimilarData.display($0.element))
            }, batches: [], sourceIDs: item.sourceIDs, fromHome: item.fromHome, provider: item.provider,
            error: item.error, canRetry: !item.fromHome && [.error, .empty].contains(item.status) &&
                similarCanEnter(current))
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

    public func canEnterSimilarMode() async throws -> Bool { similarCanEnter(try read()) }
    func similarCanEnter(_ d: MusicDocument) -> Bool {
        platformEnabled("wy") && accounts is any MusicNativeSimilarModeProviding &&
            d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] })
                .flatMap { MusicNativeSimilarData.nativeID($0["song"]) } != nil
    }
    public func previewSimilarMode() async throws -> MusicSimilarModeSnapshot? {
        try await restoreSimilarSessionIfNeeded()
        if similarMode.active != nil { return try await similarModeSnapshot() }
        guard let item = try prepareSimilarPreview() else { return nil }
        await item.task?.value
        try check()
        return try await similarModeSnapshot()
    }
    func prepareSimilarPreview() throws -> MusicNativeSimilarPreview? {
        let d = try read()
        guard similarCanEnter(d),
              let entry = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }),
              let seedID = MusicNativeSimilarData.nativeID(entry["song"]) else { return nil }
        if similarMode.preview?.entryID != entry["id"].string || similarMode.preview?.fromHome == true {
            discardSimilarPreview()
            let item = MusicNativeSimilarPreview(seed: entry["song"], entryID: entry["id"].string, fromHome: false,
                provider: .heartbeat, sourceIDs: [seedID], revision: d.queue["revision"].int,
                navigation: similarMode.navigation, auto: entry["context"]["sourceType"].string == "search-song")
            similarMode.preview = item
            item.task = Task { await loadSimilarPreview(item.id) }
            emitSimilar()
        }
        return similarMode.preview
    }
    public func enterSimilarMode() async throws -> MusicSimilarModeResult {
        try await restoreSimilarSessionIfNeeded()
        if similarMode.active != nil { return .init(state: stateDTO(try read()), action: .keepCurrent) }
        // Set enter intent before the network completes, so explicit retry can
        // commit a failed first entry just as the original enterAfter flag does.
        guard let item = try prepareSimilarPreview() else { throw MusicServiceError.sourceUnavailable }
        item.enterAfter = true
        let own = epoch
        await item.task?.value
        try check(own)
        return try commitSimilarPreview(id: item.id)
    }
    public func startSimilarMode(_ start: MusicSimilarModeStart) async throws -> MusicSimilarModeResult {
        try await restoreSimilarSessionIfNeeded()
        try check(); try requirePlatform("wy")
        let provider: MusicSimilarModeProvider, ids: [String], name: String
        switch start {
        case .heartbeat: provider = .heartbeat; ids = []; name = "你的红心歌曲"
        case .songRoaming(let sourceIDs, let sourceName):
            try musicRequire(!sourceIDs.isEmpty && sourceIDs.count <= 20 && sourceIDs.allSatisfy(MusicNativeSimilarData.id))
            if !sourceName.isEmpty { _ = try musicText(sourceName, maximum: 600) }
            provider = .songRoaming; ids = sourceIDs; name = sourceName.isEmpty ? "首页相似歌曲" : sourceName
        }
        if let active = similarMode.active, active.fromHome, active.provider == provider,
           provider == .heartbeat || active.sourceIDs == ids {
            try active.lease.authorization.check()
            discardSimilarPreview()
            return .init(state: stateDTO(try read()), action: .togglePlayback)
        }
        let d = try read()
        let item: MusicNativeSimilarPreview
        if let existing = similarMode.preview, existing.fromHome, existing.provider == provider,
           existing.sourceIDs == ids, similarEntranceIsCurrent(existing, d) { item = existing }
        else {
            discardSimilarPreview()
            similarMode.navigation = UUID()
            item = .init(seed: .obj([("title", .string(name))]), entryID: nil, fromHome: true,
                         provider: provider, sourceIDs: ids, revision: d.queue["revision"].int,
                         navigation: similarMode.navigation)
            similarMode.preview = item; item.task = Task { await loadSimilarPreview(item.id) }; emitSimilar()
        }
        await item.task?.value
        try check()
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
        if session.continueEnabled == enabled { return }
        var saved = session.saved; saved["continue"] = .bool(enabled)
        if !enabled, let tail = session.pendingTail {
            saved["attempted"] = .strings(session.attempted.filter { $0 != tail }.sorted())
            saved["status"] = .string("ready")
        }
        try session.lease.authorization.perform { try similarQueueTransaction(["queue"]) { $0.queue["similar"] = saved } }
        session.continueEnabled = enabled
        if !enabled {
            if let tail = session.pendingTail { session.attempted.remove(tail) }
            cancelSimilarBatch(session); session.status = .ready; session.error = nil
        }
        emitSimilar()
        if enabled { try similarBoundary() }
    }
    public func retrySimilarMode(snapshotID: UUID) async throws -> MusicSimilarModeResult {
        guard let snapshot = try await similarModeSnapshot(), snapshot.id == snapshotID, snapshot.canRetry else {
            throw MusicServiceError.invalidRequest
        }
        if let active = similarMode.active {
            let tail = try read().queue["entries"].array.last?["id"].string ?? ""
            active.attempted.remove(tail)
            try similarBoundary(retry: true)
            await active.pending?.value
            try check()
            return .init(state: stateDTO(try read()), action: .keepCurrent)
        }
        let enterAfter = similarMode.preview?.enterAfter == true
        discardSimilarPreview()
        _ = try await previewSimilarMode()
        guard let item = similarMode.preview else { throw MusicServiceError.targetChanged }
        item.enterAfter = enterAfter
        if enterAfter { return try commitSimilarPreview(id: item.id) }
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
            try? similarBoundary()
        case .progress:
            if similarMode.playing?.attempt == fact.attemptId { try? similarBoundary() }
        case .paused, .stopped, .failed:
            similarMode.playing = nil; similarMode.navigation = UUID()
        case .ended: similarMode.playing = nil
        }
        return accepted
    }
}

extension NativeMusicService {
    func similarEntranceIsCurrent(_ item: MusicNativeSimilarPreview, _ d: MusicDocument) -> Bool {
        similarMode.preview?.id == item.id && (item.fromHome ?
            item.revision == d.queue["revision"].int && item.navigation == similarMode.navigation :
            item.entryID == d.queue["currentId"].string)
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
        do { return try await work(label: label, body) }
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
            let request: MusicNativeSimilarModeRequest = item.provider == .songRoaming ?
                .songRoaming(sourceIDs: item.sourceIDs) :
                .heartbeat(lastID: item.sourceIDs.first, fromHome: item.fromHome, context: nil)
            let response = try await similarWork(id) { try await owner.similarModeBatch(lease, request: request) }
            try await owner.checkAccountQueue(lease)
            try check(own); try lease.authorization.check()
            guard similarEntranceIsCurrent(item, try read()) else { return }
            try musicRequire(!scope.isEmpty && scope.utf8.count <= 512, .invalidResponse)
            try musicRequire(response.items.allSatisfy { $0.platform == "wy" }, .invalidResponse)
            let songs = try response.items.map(MusicModel.normalize)
            item.recommendations = try MusicNativeSimilarData.recommendations(response.items)
            if item.provider == .heartbeat {
                guard let context = response.heartbeatContext,
                      MusicNativeSimilarData.id(context.playlistID), MusicNativeSimilarData.id(context.seedID) else {
                    throw MusicServiceError.invalidResponse
                }
                item.context = context
                if item.fromHome { item.sourceIDs = [context.seedID] }
            }
            item.scope = scope
            item.songs = MusicNativeSimilarData.unique(songs, excluding: item.fromHome ? [] : [item.seed])
            item.status = item.songs.isEmpty ? .empty : .ready
            item.task = nil; emitSimilar()
        } catch {
            guard similarMode.preview?.id == id else { return }
            if epoch != own || item.lease?.authorization.isActive == false {
                discardSimilarPreview(); emitSimilar(); return
            }
            let failure = MusicServiceError.sanitized(error)
            if failure == .cancelled { discardSimilarPreview() }
            else { item.status = .error; item.error = failure; item.task = nil }
            emitSimilar()
        }
    }
    func releaseSimilarLease(_ lease: MusicAccountQueueLease) {
        similarMode.watchers.removeValue(forKey: lease.id)?.cancel()
        lease.revoke()
        if let owner = accounts as? any MusicNativeSimilarModeProviding {
            Task { await owner.endAccountQueue(lease) }
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
        guard similarMode.active?.lease.id == id || similarMode.preview?.lease?.id == id else { return }
        playbackFeedbackAdmission = nil; playbackFeedback.revoke(platform: .netease)
        // A revoked account must never resurrect from an old persisted session.
        if available, !closed { try? transaction(["queue"]) { $0.queue["similar"] = .null } }
        clearSimilarRuntime(); similarMode.restored = true
    }
    func discardSimilarPreview() {
        guard let item = similarMode.preview else { return }
        similarMode.preview = nil; item.task?.cancel()
        if let lease = item.lease { releaseSimilarLease(lease) }
    }
    func cancelSimilarBatch(_ session: MusicNativeSimilarSession) {
        session.pendingID = nil; session.pendingTail = nil
        session.pending?.cancel(); session.pending = nil
    }
    func clearSimilarRuntime() {
        discardSimilarPreview()
        if let active = similarMode.active {
            similarMode.active = nil; cancelSimilarBatch(active); releaseSimilarLease(active.lease)
        }
        similarMode.navigation = UUID(); similarMode.playing = nil
        similarMode.restored = false; similarMode.restoring = nil
        emitSimilar()
        if closed {
            for observer in similarMode.observers.values { observer.finish() }
            similarMode.observers.removeAll()
        }
    }
}

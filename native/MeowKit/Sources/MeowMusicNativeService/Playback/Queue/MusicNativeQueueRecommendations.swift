import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicQueueRecommendationServing {
    public func queueRecommendationSnapshot() async throws -> MusicQueueRecommendationSnapshot? {
        try check()
        guard let item = try prepareQueueRecommendations() else { return nil }
        return queueRecommendationDTO(item)
    }

    public func previewQueueRecommendations() async throws -> MusicQueueRecommendationSnapshot? {
        guard let item = try prepareQueueRecommendations() else { return nil }
        let token = item.id
        if item.pending == nil, item.songs.isEmpty, !item.exhausted, item.status != .error {
            try beginQueueRecommendationBatch(item, append: false)
        }
        let pending = item.pending
        await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
        try check()
        try musicRequire(similarMode.queue === item && item.id == token, .targetChanged)
        return queueRecommendationDTO(item)
    }

    public func playQueueRecommendation(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID,
              let index = item.songs.indices.first(where: { "\(item.id.uuidString):\($0)" == candidateID }) else {
            throw MusicServiceError.targetChanged
        }
        let state = try appendQueueRecommendations(item, selectedIndex: index)
        invalidatePlayback()
        similarMode.navigation = UUID(); similarMode.playing = nil
        emitSimilar()
        return .init(state: state, action: .playCurrent)
    }

    public func setQueueRecommendationContinue(_ enabled: Bool, snapshotID: UUID) async throws {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID else { throw MusicServiceError.targetChanged }
        if let lease = item.lease { try lease.authorization.check() }
        try musicRequire(queueRecommendationsAreCurrent(item, try read()), .targetChanged)
        if item.continueEnabled == enabled { return }
        item.continueEnabled = enabled
        if !enabled {
            if let tail = item.pendingTail { item.attempted.remove(tail) }
            item.pendingTail = nil
            if item.pending != nil {
                item.pending?.cancel(); item.id = UUID()
                item.status = .ready; item.error = nil
            }
            similarMode.navigation = UUID()
        }
        emitSimilar()
        if enabled { try queueRecommendationBoundary() }
    }

    public func retryQueueRecommendations(snapshotID: UUID) async throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID, queueRecommendationDTO(item).canRetry else {
            throw MusicServiceError.invalidRequest
        }
        let d = try read()
        let append = item.continueEnabled && item.attempted.contains(item.currentEntryID) &&
            d.queue["entries"].array.last?["id"].string == item.currentEntryID
        item.exhausted = false; item.error = nil; item.id = UUID()
        item.attempted.remove(item.currentEntryID)
        try beginQueueRecommendationBatch(item, append: append)
        let pending = item.pending
        await withTaskCancellationHandler { await pending?.value } onCancel: { pending?.cancel() }
        try check()
        try musicRequire(similarMode.queue === item, .targetChanged)
        return .init(state: stateDTO(try read()), action: .keepCurrent)
    }

    public func cancelQueueRecommendationPreview() async {
        guard let item = similarMode.queue, item.pendingTail == nil else { return }
        item.pending?.cancel()
        item.id = UUID(); item.songs = []; item.recommendations = [:]
        item.status = .ready; item.error = nil
        if !item.continueEnabled && item.liveRecommendations.isEmpty { discardQueueRecommendations() }
        emitSimilar()
    }
}

extension NativeMusicService {
    func ordinaryRecommendationRequest(_ d: MusicDocument) -> MusicNativeSimilarModeRequest? {
        guard platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding,
              similarMode.active == nil, d.queue["similar"] == .null,
              !d.queue["entries"].array.contains(where: { $0["context"]["sourceType"].string == "fm" }) else { return nil }
        return MusicNativeSimilarData.queueRequest(d.queue)
    }

    func prepareQueueRecommendations() throws -> MusicNativeQueueRecommendations? {
        let d = try read()
        guard ordinaryRecommendationRequest(d) != nil else {
            discardQueueRecommendations(); return nil
        }
        if let item = similarMode.queue {
            if item.lease?.authorization.isActive == false {
                discardQueueRecommendations(); return nil
            }
            if !queueRecommendationsAreCurrent(item, d) { refreshQueueRecommendations(item, document: d) }
            return item
        }
        let item = MusicNativeQueueRecommendations(revision: d.queue["revision"].int,
                                                   currentEntryID: d.queue["currentId"].string)
        similarMode.queue = item
        return item
    }

    func queueRecommendationDTO(_ item: MusicNativeQueueRecommendations) -> MusicQueueRecommendationSnapshot {
        .init(id: item.id, status: item.status, currentEntryID: item.currentEntryID,
            continueEnabled: item.continueEnabled,
            candidates: item.songs.enumerated().map {
                .init(id: "\(item.id.uuidString):\($0.offset)", song: MusicNativeSimilarData.display($0.element))
            }, exhausted: item.exhausted, error: item.error,
            canRetry: item.pending == nil && (item.status == .error || item.exhausted && item.songs.isEmpty))
    }

    func queueRecommendationsAreCurrent(_ item: MusicNativeQueueRecommendations, _ d: MusicDocument) -> Bool {
        similarMode.queue === item && d.queue["revision"].int == item.revision &&
            d.queue["currentId"].string == item.currentEntryID && ordinaryRecommendationRequest(d) != nil
    }

    func refreshQueueRecommendations(_ item: MusicNativeQueueRecommendations, document d: MusicDocument,
                                     edited: Bool = true) {
        item.pending?.cancel()
        if let tail = item.pendingTail { item.attempted.remove(tail) }
        item.pendingTail = nil
        item.id = UUID(); item.revision = d.queue["revision"].int
        item.currentEntryID = d.queue["currentId"].string
        item.songs = []; item.recommendations = [:]
        if edited { item.exhausted = false; item.attempted.removeAll() }
        item.status = item.exhausted ? .empty : .ready; item.error = nil
        let entries = Set(d.queue["entries"].array.map { $0["id"].string })
        item.liveRecommendations = item.liveRecommendations.filter { entries.contains($0.key) }
    }

    func beginQueueRecommendationBatch(_ item: MusicNativeQueueRecommendations, append: Bool) throws {
        guard item.pending == nil else { return }
        let d = try read()
        try musicRequire(queueRecommendationsAreCurrent(item, d), .targetChanged)
        guard let request = ordinaryRecommendationRequest(d) else { throw MusicServiceError.sourceUnavailable }
        if append {
            try musicRequire(d.queue["entries"].array.last?["id"].string == item.currentEntryID, .targetChanged)
            item.attempted.insert(item.currentEntryID)
        }
        let requestID = UUID(), token = item.id
        item.pendingID = requestID; item.pendingTail = append ? item.currentEntryID : nil
        item.pendingToken = token
        item.status = .loading; item.error = nil; item.songs = []; item.recommendations = [:]
        item.pending = Task { await loadQueueRecommendations(item, requestID: requestID, token: token, request: request) }
        emitSimilar()
    }

    func loadQueueRecommendations(_ item: MusicNativeQueueRecommendations, requestID: UUID, token: UUID,
                                  request: MusicNativeSimilarModeRequest) async {
        defer {
            similarMode.deadlines.removeValue(forKey: requestID)
            if item.pendingID == requestID {
                item.pending = nil; item.pendingID = nil; item.pendingTail = nil
                item.pendingToken = nil
                if item.status == .loading { item.status = .ready }
                if similarMode.queue === item {
                    emitSimilar()
                    try? queueRecommendationBoundary()
                }
            }
        }
        let own = epoch
        do {
            let owner = try similarOwner()
            let lease: MusicAccountQueueLease
            if let existing = item.lease { lease = existing }
            else {
                let opening = MusicAccountQueueOpening()
                do {
                    lease = try await similarWork(requestID) {
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
                guard similarMode.queue === item, item.id == token, !Task.isCancelled else {
                    releaseSimilarLease(lease); return
                }
                item.lease = lease; watchSimilarLease(lease)
            }
            let response = try await similarWork(requestID) {
                try await owner.checkAccountQueue(lease)
                return try await owner.similarModeBatch(lease, request: request)
            }
            try await owner.checkAccountQueue(lease)
            try check(own); try lease.authorization.check()
            guard similarMode.queue === item, item.id == token, item.pendingID == requestID else { return }
            let d = try read()
            guard queueRecommendationsAreCurrent(item, d) else { return }
            let songs = try MusicNativeSimilarData.songs(response)
            let unique = MusicNativeSimilarData.unique(songs, excluding: d.queue["entries"].array.map { $0["song"] })
            let recommendations = try MusicNativeSimilarData.recommendations(response.items)
            item.songs = unique; item.recommendations = recommendations
            item.exhausted = response.hasMore == false || item.songs.isEmpty
            item.status = item.songs.isEmpty ? .empty : .ready
            if item.pendingTail != nil, item.continueEnabled, !item.songs.isEmpty {
                _ = try appendQueueRecommendations(item)
            }
        } catch {
            guard similarMode.queue === item, item.id == token, item.pendingID == requestID else { return }
            if epoch != own || item.lease?.authorization.isActive == false {
                discardQueueRecommendations(); return
            }
            let failure = MusicServiceError.sanitized(error)
            item.songs = []; item.recommendations = [:]
            item.status = failure == .cancelled ? .ready : .error
            item.error = failure == .cancelled ? nil : failure
        }
    }

    /// A cached preview can be admitted at the same real tail boundary. A
    /// pending preview becomes the one tail request, rather than racing it.
    func queueRecommendationBoundary() throws {
        guard let item = similarMode.queue, item.continueEnabled,
              !item.attempted.contains(item.currentEntryID), let playing = similarMode.playing,
              playing.occurrence == item.currentEntryID, attempt?.id == playing.attempt,
              attempt?.ready == true, attempt?.started == true, attempt?.ended == false else { return }
        let d = try read()
        guard let tail = d.queue["entries"].array.last, d.queue["currentId"] == tail["id"],
              queueRecommendationsAreCurrent(item, d) else { return }
        if item.pending != nil {
            guard item.pendingToken == item.id else { return }
            item.pendingTail = item.currentEntryID
            item.attempted.insert(item.currentEntryID)
            return
        }
        if !item.songs.isEmpty, item.status == .ready {
            item.attempted.insert(item.currentEntryID)
            _ = try appendQueueRecommendations(item)
            emitSimilar()
        } else if !item.exhausted && item.status != .error {
            try beginQueueRecommendationBatch(item, append: true)
        }
    }

    func appendQueueRecommendations(_ item: MusicNativeQueueRecommendations,
                                    selectedIndex: Int? = nil) throws -> MusicPlaybackState {
        guard let lease = item.lease else { throw MusicServiceError.targetChanged }
        try lease.authorization.check()
        try musicRequire(item.status == .ready, item.error ?? .targetChanged)
        try musicRequire(!item.songs.isEmpty, .notFound)
        if let selectedIndex { try musicRequire(item.songs.indices.contains(selectedIndex)) }
        var live: [String: MJ] = [:]
        let state = try lease.authorization.perform {
            try similarQueueTransaction(selectedIndex == nil ? ["queue"] : ["queue", "playback"]) { draft in
                try musicRequire(queueRecommendationsAreCurrent(item, draft), .targetChanged)
                var rows = try makeEntries(item.songs, queue: &draft.queue, context: .obj([
                    ("sourceType", .string("queue-recommendations")), ("sourceTitle", .string("更多推荐")),
                    ("provider", .string(MusicSimilarModeProvider.queueRecommendations.rawValue))
                ]))
                for i in rows.indices {
                    if let id = MusicNativeSimilarData.nativeID(rows[i]["song"]), let value = item.recommendations[id] {
                        rows[i]["context"]["recommendation"] = value
                        live[rows[i]["id"].string] = value
                    }
                }
                draft.queue["entries"] = .array(draft.queue["entries"].array + rows)
                if let selectedIndex {
                    select(rows[selectedIndex]["id"].string, queue: &draft.queue)
                    draft.resetPosition()
                }
                draft.queue["revision"] = .n(draft.queue["revision"].int + 1)
                return stateDTO(draft)
            }
        }
        item.liveRecommendations.merge(live) { _, new in new }
        item.revision = state.revision; item.currentEntryID = state.currentId ?? ""
        item.songs = []; item.recommendations = [:]; item.id = UUID()
        item.status = .ready; item.error = nil
        return state
    }

    func discardQueueRecommendations() {
        guard let item = similarMode.queue else { return }
        similarMode.queue = nil; item.pending?.cancel()
        if let lease = item.lease { releaseSimilarLease(lease, after: item.pending) }
        else if let pending = item.pending { retainSimilarDrain(pending) }
    }

    /// Durable row labels are display data. Only this live occurrence map and
    /// its still-active exact-account lease may authorize recommendation proof.
    func liveRecommendation(entryID: String) -> (lease: MusicAccountQueueLease, value: MJ)? {
        guard let d = try? read(),
              let entry = d.queue["entries"].array.first(where: { $0["id"].string == entryID }),
              let nativeID = MusicNativeSimilarData.nativeID(entry["song"]) else { return nil }
        if let session = similarMode.active, session.lease.authorization.isActive,
           let value = session.liveRecommendations[entryID], value["songId"].id == nativeID {
            return (session.lease, value)
        }
        if let item = similarMode.queue, let lease = item.lease, lease.authorization.isActive,
           let value = item.liveRecommendations[entryID], value["songId"].id == nativeID {
            return (lease, value)
        }
        return nil
    }
}

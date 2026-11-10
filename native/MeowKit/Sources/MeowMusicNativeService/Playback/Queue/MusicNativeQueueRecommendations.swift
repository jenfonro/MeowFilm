import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicQueueRecommendationServing {
    public func queueRecommendationSnapshot() async throws -> MusicQueueRecommendationSnapshot? {
        guard let item = try await prepareQueueRecommendations() else { return nil }
        item.currentEntryID = try read().queue["currentId"].string
        return queueRecommendationDTO(item)
    }

    public func previewQueueRecommendations() async throws -> MusicQueueRecommendationSnapshot? {
        guard let item = try await prepareQueueRecommendations() else { return nil }
        try queueRecommendationBoundary()
        let pending = item.pending
        await pending?.value
        try check()
        try musicRequire(similarMode.queue === item, .targetChanged)
        return queueRecommendationDTO(item)
    }

    public func playQueueRecommendation(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID,
              let index = item.songs.indices.first(where: { "\(item.candidateID.uuidString):\($0)" == candidateID }) else {
            throw MusicServiceError.targetChanged
        }
        let state = try appendQueueRecommendations(item, selectedIndex: index)
        invalidatePlayback(reason: .ui)
        similarMode.playing = nil; similarMode.playbackEvent = nil; similarMode.eventOccurrence = nil
        emitSimilar()
        return .init(state: state, action: .playCurrent)
    }

    public func setQueueRecommendationContinue(_ enabled: Bool, snapshotID: UUID) async throws {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID,
              queueRecommendationsAreCurrent(item, try read()) else { throw MusicServiceError.targetChanged }
        try item.lease?.authorization.check()
        item.continueEnabled = enabled
        emitSimilar()
        if enabled { try queueRecommendationBoundary() }
    }

    public func retryQueueRecommendations(snapshotID: UUID) async throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.queue, item.id == snapshotID, queueRecommendationDTO(item).canRetry else {
            throw MusicServiceError.invalidRequest
        }
        item.exhausted = false; item.error = nil
        try beginQueueRecommendationBatch(item, append: item.active && item.continueEnabled)
        let pending = item.pending
        await pending?.value
        try check()
        try musicRequire(similarMode.queue === item, .targetChanged)
        return .init(state: stateDTO(try read()), action: .keepCurrent)
    }

    // The queue sheet only observes a playback-owned candidate session.
    public func cancelQueueRecommendationPreview() async {}
}

extension NativeMusicService {
    func prepareQueueRecommendations() async throws -> MusicNativeQueueRecommendations? {
        let d = try read()
        if let item = similarMode.queue {
            guard item.lease?.authorization.isActive != false else { discardQueueRecommendations(); return nil }
            return item
        }
        guard platformEnabled("wy"), accounts is any MusicNativeSimilarModeProviding,
              similarMode.active == nil, fmPlayback == nil, d.queue["similar"] == .null,
              d.queue["source"]["sourceType"].string == "search-song",
              let row = d.queue["entries"].array.first(where: { $0["id"] == d.queue["source"]["seedEntryId"] }) else { return nil }
        let navigation = similarMode.navigation
        let seed = try await sharedSongMetadata(row["song"])
        let current = try read()
        if let existing = similarMode.queue { return existing }
        guard navigation == similarMode.navigation, current.queue["source"] == d.queue["source"],
              MusicNativeSimilarData.nativeID(seed) != nil else { return nil }
        let item = MusicNativeQueueRecommendations(revision: current.queue["revision"].int,
            currentEntryID: current.queue["currentId"].string, seed: seed)
        similarMode.queue = item
        return item
    }

    func queueRecommendationDTO(_ item: MusicNativeQueueRecommendations) -> MusicQueueRecommendationSnapshot {
        .init(id: item.id, status: item.status, currentEntryID: item.currentEntryID,
            continueEnabled: item.continueEnabled,
            candidates: item.songs.enumerated().map {
                .init(id: "\(item.candidateID.uuidString):\($0.offset)", song: MusicNativeSimilarData.display($0.element))
            }, exhausted: item.exhausted, error: item.error,
            canRetry: item.pending == nil && (item.status == .error || item.exhausted && item.songs.isEmpty),
            active: item.active, errorID: item.errorID)
    }

    func queueRecommendationsAreCurrent(_ item: MusicNativeQueueRecommendations, _ d: MusicDocument) -> Bool {
        similarMode.queue === item && item.lease?.authorization.isActive != false &&
            similarMode.active == nil && fmPlayback == nil && !d.queue["entries"].array.isEmpty
    }

    func beginQueueRecommendationBatch(_ item: MusicNativeQueueRecommendations, append: Bool) throws {
        guard item.pending == nil else { return }
        try musicRequire(queueRecommendationsAreCurrent(item, try read()), .targetChanged)
        guard let seed = MusicNativeSimilarData.nativeID(item.seed) else { throw MusicServiceError.sourceUnavailable }
        let requestID = UUID(), token = item.id
        item.pendingID = requestID; item.pendingTail = append ? String(item.offset) : nil
        item.pendingToken = token
        item.status = .loading; item.error = nil; item.errorID = nil
        item.pending = Task {
            await loadQueueRecommendations(item, requestID: requestID, token: token,
                request: .songRecommendations(seedID: seed, offset: item.offset))
        }
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
            guard queueRecommendationsAreCurrent(item, try read()) else { return }
            let songs = try MusicNativeSimilarData.songs(response)
            let recommendations = try MusicNativeSimilarData.recommendations(response.items)
            try await retainSongMetadata(songs)
            try check(own); try lease.authorization.check()
            guard similarMode.queue === item, item.id == token, item.pendingID == requestID,
                  queueRecommendationsAreCurrent(item, try read()) else { return }
            let unique = MusicNativeSimilarData.unique(songs, excluding: try read().queue["entries"].array.map { $0["song"] })
            guard let offset = response.offset, response.hasMore != nil else { throw MusicServiceError.invalidResponse }
            item.offset = offset; item.candidateID = UUID()
            item.songs = unique; item.recommendations = recommendations
            item.exhausted = response.hasMore == false || item.songs.isEmpty
            item.status = item.songs.isEmpty ? .empty : .ready
            if item.active, item.pendingTail != nil, item.continueEnabled, !item.songs.isEmpty {
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
            item.errorID = failure == .cancelled ? nil : requestID
        }
    }

    func queueRecommendationBoundary() throws {
        guard let item = similarMode.queue, item.continueEnabled, item.pending == nil,
              !item.exhausted || !item.songs.isEmpty else { return }
        let d = try read()
        if !item.active {
            guard item.songs.isEmpty, !item.attempted.contains("initial"), hasCurrentPlayback(d),
                  d.queue["currentId"] == d.queue["source"]["seedEntryId"] else { return }
            item.attempted.insert("initial")
            try beginQueueRecommendationBatch(item, append: false)
            return
        }
        let event = d.playback["mode"].int == MusicPlaybackMode.shuffle.rawValue ? similarMode.playbackEvent ?? "" :
            d.queue["currentId"].string + ":" + String(item.offset)
        guard continuationIsDue(d) else { return }
        if item.active, !item.songs.isEmpty {
            _ = try appendQueueRecommendations(item); emitSimilar(); return
        }
        guard item.songs.isEmpty, !item.attempted.contains(event) else { return }
        item.attempted.insert(event)
        try beginQueueRecommendationBatch(item, append: item.active)
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
                    draft.queue["source"]["sourceType"] = .string("queue-recommendations")
                    draft.queue["source"]["sourceTitle"] = .string("推荐")
                }
                draft.queue["revision"] = .n(draft.queue["revision"].int + 1)
                return stateDTO(draft)
            }
        }
        retainQueueFeedbackOrigins(live, authorization: lease.authorization,
                                   seedIDs: MusicNativeSimilarData.nativeID(item.seed).map { [$0] } ?? [])
        item.revision = state.revision; item.currentEntryID = state.currentId ?? ""
        if selectedIndex != nil { item.active = true }
        item.songs = []; item.recommendations = [:]; item.candidateID = UUID()
        item.status = .ready; item.error = nil; item.errorID = nil
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
    func liveRecommendation(entryID: String) -> MusicNativeQueueFeedbackOrigin? {
        guard let d = try? read(),
              let entry = d.queue["entries"].array.first(where: { $0["id"].string == entryID }),
              let nativeID = MusicNativeSimilarData.nativeID(entry["song"]) else { return nil }
        guard let value = queueFeedbackOrigins[entryID], value.authorization.isActive,
              value.value["songId"].id == nativeID else { return nil }
        return value
    }
}

import Foundation
import MeowMusicFeature

enum MusicNativeSimilarQueueChange: Equatable { case replace, clear, select, edit, insertNext, append }

extension NativeMusicService {
    /// Keep uncertain CAS outcomes out of the live recommendation session.
    /// Never roll back or overwrite a possibly committed queue with old state.
    /// The next read must acquire a fresh lease against the persisted session.
    func similarQueueTransaction<T>(_ areas: Set<String>, _ change: (inout MusicDocument) throws -> T) throws -> T {
        do { return try transaction(areas, change) }
        catch MusicServiceError.commitUnconfirmed {
            clearFMPlayback(); clearSimilarRuntime()
            invalidatePlayback(reason: .interrupt)
            throw MusicServiceError.commitUnconfirmed
        }
    }

    func commitSimilarPreview(id: UUID, candidateIndex: Int? = nil) throws -> MusicSimilarModeResult {
        try check()
        guard let item = similarMode.preview, item.id == id, let lease = item.lease else { throw MusicServiceError.targetChanged }
        try lease.authorization.check()
        guard item.status == .ready, !item.songs.isEmpty else { throw item.error ?? MusicServiceError.notFound }
        if let candidateIndex { try musicRequire(item.songs.indices.contains(candidateIndex)) }
        let before = try read()
        try musicRequire(similarEntranceIsCurrent(item, before), .targetChanged)
        var seed = item.fromHome ? nil : before.queue["entries"].array.first { $0["id"].string == item.entryID }
        try musicRequire(item.fromHome || seed != nil, .targetChanged)
        if seed != nil { seed?["song"] = item.seed }
        let seedNativeID = seed == nil ? nil : MusicNativeSimilarData.nativeID(item.seed)
        let seedIndex = seedNativeID.flatMap { id in item.songs.firstIndex { MusicNativeSimilarData.nativeID($0) == id } }
        let restores = item.provider == .heartbeat || item.provider == .queueRecommendations
        let returnQueue = restores ? (similarMode.active.flatMap { $0.returnQueue == .null ? nil : $0.returnQueue } ??
            MusicNativeSimilarData.queueSnapshot(before.queue)) : .null
        let session = MusicNativeSimilarSession(seed: item.seed, seedEntryID: seed?["id"].string,
            returnQueue: returnQueue, fromHome: item.fromHome, provider: item.provider,
            sourceIDs: item.sourceIDs, context: item.context, scope: item.scope, lease: lease)
        session.continueEnabled = item.provider != .heartbeat
        session.offset = item.offset
        session.exhausted = item.exhausted
        let batchID = UUID().uuidString.lowercased()
        var live: [String: MJ] = [:]
        let state = try lease.authorization.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { d in
                try musicRequire(similarEntranceIsCurrent(item, d), .targetChanged)
                let old = d.queue
                let title = item.fromHome && item.provider == .heartbeat ? "心动模式" :
                    "根据 \(MusicNativeSimilarData.display(item.seed).title) 推荐"
                let songs = item.songs.enumerated().filter { $0.offset != seedIndex }.map(\.element)
                var rows = try makeEntries(songs, queue: &d.queue, context: .obj([
                    ("sourceType", .string(item.provider.rawValue)), ("sourceTitle", .string(title)),
                    ("similar", .obj([("batchId", .string(batchID)), ("basedOn", MusicModel.durable(item.seed)),
                                      ("provider", .string(item.provider.rawValue))]))
                ]))
                if let seed { rows.insert(seed, at: 0) }
                for i in rows.indices {
                    if item.provider == .heartbeat {
                        rows[i]["context"]["platform"] = .string("wy")
                        rows[i]["context"]["sourceId"] = .string("favorites")
                    }
                    if let nativeID = MusicNativeSimilarData.nativeID(rows[i]["song"]),
                       let value = item.recommendations[nativeID] {
                        rows[i]["context"]["recommendation"] = value
                    }
                }
                archive(old, into: &d)
                d.queue["entries"] = .array(rows)
                let selected: MJ
                if let candidateIndex {
                    let nativeID = MusicNativeSimilarData.nativeID(item.songs[candidateIndex])
                    selected = rows.first { MusicNativeSimilarData.nativeID($0["song"]) == nativeID }!["id"]
                } else { selected = seed?["id"] ?? rows[0]["id"] }
                d.queue["currentId"] = selected
                for key in ["priority", "history", "failed"] { d.queue[key] = .array([]) }
                d.queue["source"] = .obj([("sourceType", .string(item.provider.rawValue)), ("sourceTitle", .string(title)),
                    ("platform", .string("wy")), ("sourceId", item.provider == .heartbeat ? .string("favorites") : .null)])
                session.batches = [MusicNativeSimilarData.batch(batchID, basedOn: item.seed, provider: item.provider, rows: rows)]
                for row in rows {
                    if let id = MusicNativeSimilarData.nativeID(row["song"]), let value = item.recommendations[id] {
                        live[row["id"].string] = value
                    }
                }
                d.queue["similar"] = session.saved
                d.queue["revision"] = .n(d.queue["revision"].int + 1)
                if selected != old["currentId"] { d.resetPosition() }
                return stateDTO(d)
            }
        }
        retainQueueFeedbackOrigins(live, authorization: lease.authorization,
            action: item.context.map { .heartbeatSong(songID: $0.seedID) },
            seedIDs: item.context.map { [$0.seedID] } ?? item.sourceIDs)
        // Transfer the original lease/observer, without revoking its fence.
        similarMode.preview = nil
        if let old = similarMode.active {
            cancelSimilarBatch(old); releaseSimilarLease(old.lease, after: old.pending)
        }
        discardQueueRecommendations()
        clearFMPlayback()
        similarMode.active = session; similarMode.restored = true
        similarMode.navigation = UUID()
        let play = item.fromHome || candidateIndex != nil
        if play {
            similarMode.playing = nil; similarMode.playbackEvent = nil; similarMode.eventOccurrence = nil
            invalidatePlayback(reason: .ui)
        }
        emitSimilar()
        if !play { try? similarBoundary() }
        return .init(state: state, action: play ? .playCurrent : .keepCurrent)
    }

    public func exitSimilarMode() async throws -> MusicSimilarModeResult {
        try await restoreSimilarSessionIfNeeded()
        guard let session = similarMode.active else {
            try cancelSimilarModeState()
            return .init(state: stateDTO(try read()), action: .keepCurrent)
        }
        try musicRequire(session.returnQueue != .null, .unsupported)
        try session.lease.authorization.check()
        let (state, keep) = try session.lease.authorization.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { d in
                let old = d.queue
                try restoreSimilarQueue(session.returnQueue, into: &d.queue)
                if let current = old["entries"].array.first(where: { $0["id"] == old["currentId"] }) {
                    var rows = d.queue["entries"].array
                    if !rows.contains(where: { $0["id"] == current["id"] }) {
                        let anchor = rows.firstIndex { $0["id"] == d.queue["currentId"] } ?? -1
                        rows.insert(current, at: anchor + 1)
                        d.queue["entries"] = .array(rows)
                    }
                    select(current["id"].string, queue: &d.queue, remember: false)
                }
                let keep = d.queue["currentId"] == old["currentId"]
                archive(old, into: &d)
                d.queue["similar"] = .null
                d.queue["revision"] = .n(d.queue["revision"].int + 1)
                if !keep { d.resetPosition() }
                return (stateDTO(d), keep)
            }
        }
        let playing = similarMode.playing
        clearSimilarRuntime(); similarMode.restored = true
        if keep { similarMode.playing = playing }
        else { invalidatePlayback(reason: .ui) }
        return .init(state: state, action: keep ? .keepCurrent : .restoreOnly)
    }

    func restoreSimilarQueue(_ snapshot: MJ, into queue: inout MJ) throws {
        try MusicNativeSimilarData.validateQueue(snapshot)
        let original = snapshot["entries"].array
        var rows = try makeEntries(original.map { $0["song"] }, queue: &queue)
        for i in rows.indices {
            rows[i]["id"] = original[i]["id"]
            rows[i]["context"] = MusicModel.durable(original[i]["context"])
        }
        let ids = Set(rows.map { $0["id"].string })
        let current = ids.contains(snapshot["currentId"].string) ? snapshot["currentId"] : rows.first?["id"] ?? .null
        var seen = Set<String>()
        queue["entries"] = .array(rows); queue["currentId"] = current
        queue["priority"] = .strings(snapshot["priority"].strings.filter {
            ids.contains($0) && $0 != current.string && seen.insert($0).inserted
        })
        queue["history"] = .strings(snapshot["history"].strings.filter { ids.contains($0) })
        queue["failed"] = .array([])
        queue["source"] = rows.isEmpty ? .null : MusicModel.durable(snapshot["source"])
    }

    func hasCurrentPlayback(_ d: MusicDocument) -> Bool {
        guard let playing = similarMode.playing, playing.attempt == attempt?.id,
              playing.occurrence == d.queue["currentId"].string,
              attempt?.ready == true, attempt?.started == true, attempt?.ended == false else { return false }
        return true
    }

    /// Fetching is independent of navigation. Only a real new play event draws
    /// the shuffle decision; pause/resume, progress and UI reads reuse it.
    func continuationIsDue(_ d: MusicDocument) -> Bool {
        guard hasCurrentPlayback(d) else { return false }
        if d.playback["mode"].int == MusicPlaybackMode.shuffle.rawValue { return similarMode.randomFetch }
        return d.queue["entries"].array.last?["id"] == d.queue["currentId"]
    }

    func similarBoundary(retry: Bool = false) throws {
        guard let session = similarMode.active, session.provider != .heartbeat,
              session.continueEnabled, session.pending == nil, !session.exhausted else { return }
        try check(); try session.lease.authorization.check()
        let d = try read()
        let event = d.playback["mode"].int == MusicPlaybackMode.shuffle.rawValue ? similarMode.playbackEvent ?? "" :
            d.queue["currentId"].string + ":" + String(session.offset)
        guard retry || (session.buffered != nil || !session.attempted.contains(event)) && continuationIsDue(d) else { return }
        session.attempted.insert(event)
        let request: MusicNativeSimilarModeRequest
        switch session.provider {
        case .heartbeat: return
        case .songRoaming: request = .songRoaming(sourceIDs: session.sourceIDs)
        case .artistRoaming: request = .artistRoaming(sourceIDs: session.sourceIDs)
        case .queueRecommendations:
            request = .songRecommendations(seedID: session.sourceIDs[0], offset: session.offset)
        }
        let requestID = UUID()
        session.pendingID = requestID; session.pendingTail = String(session.offset)
        session.status = .loading; session.error = nil; session.errorID = nil
        session.pending = Task { await loadSimilarTail(sessionID: session.id, requestID: requestID, request: request) }
        emitSimilar()
    }

    func loadSimilarTail(sessionID: UUID, requestID: UUID, request: MusicNativeSimilarModeRequest) async {
        guard let session = similarMode.active, session.id == sessionID else { return }
        defer {
            similarMode.deadlines.removeValue(forKey: requestID)
            if session.pendingID == requestID {
                session.pending = nil; session.pendingID = nil; session.pendingTail = nil
                if session.status == .loading { session.status = .ready }
                if similarMode.active === session { emitSimilar() }
            }
        }
        let own = epoch
        do {
            let owner = try similarOwner(), lease = session.lease
            let response: MusicNativeSimilarModeBatch
            if let buffered = session.buffered { response = buffered }
            else {
                response = try await similarWork(requestID) {
                    try await owner.checkAccountQueue(lease)
                    return try await owner.similarModeBatch(lease, request: request)
                }
            }
            try await owner.checkAccountQueue(lease)
            try check(own); try lease.authorization.check()
            guard similarMode.active === session, session.pendingID == requestID else { return }
            let incoming = try MusicNativeSimilarData.songs(response)
            let offset: Int
            if session.provider == .queueRecommendations {
                guard let next = response.offset, response.hasMore != nil else { throw MusicServiceError.invalidResponse }
                offset = next
            } else { offset = session.offset + 1 }
            let recommendations = try MusicNativeSimilarData.recommendations(response.items)
            try await retainSongMetadata(incoming)
            try check(own); try lease.authorization.check()
            guard similarMode.active === session, session.pendingID == requestID else { return }
            guard session.continueEnabled else { session.buffered = response; return }
            let songs = MusicNativeSimilarData.unique(incoming, excluding: try read().queue["entries"].array.map { $0["song"] })
            let exhausted = response.hasMore == false || songs.isEmpty
            var batch: MJ?, live: [String: MJ] = [:]
            try lease.authorization.perform {
                try similarQueueTransaction(["queue"]) { draft in
                    if !songs.isEmpty {
                        let batchID = UUID().uuidString.lowercased()
                        var rows = try makeEntries(songs, queue: &draft.queue, context: .obj([
                            ("sourceType", .string(session.provider.rawValue)),
                            ("sourceTitle", draft.queue["source"]["sourceTitle"]),
                            ("similar", .obj([("batchId", .string(batchID)), ("basedOn", MusicModel.durable(session.seed)),
                                              ("provider", .string(session.provider.rawValue))]))
                        ]))
                        for i in rows.indices {
                            if let id = MusicNativeSimilarData.nativeID(rows[i]["song"]), let value = recommendations[id] {
                                rows[i]["context"]["recommendation"] = value; live[rows[i]["id"].string] = value
                            }
                        }
                        draft.queue["entries"] = .array(draft.queue["entries"].array + rows)
                        draft.queue["revision"] = .n(draft.queue["revision"].int + 1)
                        batch = MusicNativeSimilarData.batch(batchID, basedOn: session.seed, provider: session.provider, rows: rows)
                    }
                    var saved = session.saved
                    saved["offset"] = .n(offset)
                    saved["exhausted"] = .bool(exhausted)
                    if let batch { saved["batches"] = .array(session.batches + [batch]) }
                    saved["status"] = .string("ready")
                    draft.queue["similar"] = saved
                }
            }
            if let batch { session.batches.append(batch) }
            session.buffered = nil
            session.offset = offset
            retainQueueFeedbackOrigins(live, authorization: session.lease.authorization,
                action: session.context.map { .heartbeatSong(songID: $0.seedID) },
                seedIDs: session.context.map { [$0.seedID] } ?? session.sourceIDs)
            session.status = .ready; session.exhausted = exhausted
        } catch {
            guard similarMode.active === session, session.pendingID == requestID else { return }
            if epoch != own || !session.lease.authorization.isActive { similarLeaseRevoked(session.lease.id); return }
            let failure = MusicServiceError.sanitized(error)
            session.status = failure == .cancelled ? .ready : .error
            session.error = failure == .cancelled ? nil : failure
            session.errorID = failure == .cancelled ? nil : requestID
            do { if failure != .cancelled { try persistSimilarSession(session) } }
            catch { session.error = MusicServiceError.sanitized(error); session.status = .error }
        }
    }
    func persistSimilarSession(_ session: MusicNativeSimilarSession) throws {
        try session.lease.authorization.perform {
            try similarQueueTransaction(["queue"]) { $0.queue["similar"] = session.saved }
        }
    }

    func similarWillChangeQueue(_ change: MusicNativeSimilarQueueChange, queue: inout MJ) {
        if change == .replace || change == .clear || queue["entries"].array.isEmpty { queue["similar"] = .null }
        if change == .select, similarMode.queue != nil,
           queue["entries"].array.first(where: { $0["id"] == queue["currentId"] })?["context"]["sourceType"].string == "queue-recommendations" {
            queue["source"]["sourceType"] = .string("queue-recommendations")
            queue["source"]["sourceTitle"] = .string("推荐")
        }
        if change == .append, similarMode.queue?.active != true, queue["source"]["sourceType"].string == "search-song" {
            queue["source"]["sourceType"] = .string("search")
        }
    }
    func similarDidChangeQueue(_ change: MusicNativeSimilarQueueChange) {
        if change == .replace || change == .clear {
            clearFMPlayback(); clearSimilarRuntime(); similarMode.restored = true
            similarMode.playbackEvent = nil; similarMode.eventOccurrence = nil
            return
        }
        guard let d = try? read() else { return }
        if d.queue["entries"].array.isEmpty {
            clearFMPlayback(); clearSimilarRuntime(); similarMode.restored = true; return
        }
        discardSimilarPreview()
        if change == .select {
            similarMode.playing = nil; similarMode.playbackEvent = nil; similarMode.eventOccurrence = nil
        }
        if let item = similarMode.queue {
            if change == .append && !item.active { discardQueueRecommendations() }
            else {
                item.revision = d.queue["revision"].int; item.currentEntryID = d.queue["currentId"].string
                if let row = d.queue["entries"].array.first(where: { $0["id"] == d.queue["currentId"] }),
                   row["context"]["sourceType"].string == "queue-recommendations" { item.active = true }
            }
        }
        emitSimilar()
        if change != .select { try? similarBoundary(); try? queueRecommendationBoundary(); try? fmBoundary() }
    }

    func similarBeforeAdvance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                              expectedRevision: Int) async throws -> (Int, MusicAdvanceResult?) {
        try await restoreSimilarSessionIfNeeded()
        let before = try read()
        try musicRequire(before.queue["revision"].int == expectedRevision, .staleRevision)
        if direction == .next, before.queue["priority"].array.isEmpty,
           let item = similarMode.queue, !item.active, !item.songs.isEmpty, item.status == .ready,
           before.queue["entries"].array.last?["id"] == before.queue["currentId"],
           reason == .manual || (item.continueEnabled && before.playback["mode"].int != MusicPlaybackMode.single.rawValue) {
            if reason == .ended, before.playback["mode"].int == MusicPlaybackMode.shuffle.rawValue {
                let state = try appendQueueRecommendations(item)
                return (state.revision, nil)
            }
            let state = try appendQueueRecommendations(item, selectedIndex: 0)
            if reason == .ended { attempt?.endedConsumed = true }
            invalidatePlayback(reason: reason == .ended ? .playend : .ui); similarDidChangeQueue(.select)
            return (state.revision, .init(state: state, selectedId: state.currentId))
        }
        return (expectedRevision, nil)
    }
}

import Foundation
import MeowMusicFeature

enum MusicNativeSimilarQueueChange: Equatable { case replace, clear, select, edit }

extension NativeMusicService {
    /// Keep uncertain CAS outcomes out of the live recommendation session.
    /// Never roll back or overwrite a possibly committed queue with old state.
    /// The next read must acquire a fresh lease against the persisted session.
    func similarQueueTransaction<T>(_ areas: Set<String>, _ change: (inout MusicDocument) throws -> T) throws -> T {
        do { return try transaction(areas, change) }
        catch MusicServiceError.commitUnconfirmed {
            clearSimilarRuntime()
            invalidatePlayback()
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
        let seed = item.fromHome ? nil : before.queue["entries"].array.first { $0["id"].string == item.entryID }
        try musicRequire(item.fromHome || seed != nil, .targetChanged)
        let seedNativeID = seed.flatMap { MusicNativeSimilarData.nativeID($0["song"]) }
        let seedIndex = seedNativeID.flatMap { id in item.songs.firstIndex { MusicNativeSimilarData.nativeID($0) == id } }
        let returnQueue = similarMode.active?.returnQueue ?? MusicNativeSimilarData.queueSnapshot(before.queue)
        let session = MusicNativeSimilarSession(seed: item.seed, seedEntryID: seed?["id"].string,
            returnQueue: returnQueue, fromHome: item.fromHome, provider: item.provider,
            sourceIDs: item.sourceIDs, context: item.context, scope: item.scope, lease: lease)
        let batchID = UUID().uuidString.lowercased()
        let state = try lease.authorization.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { d in
                try musicRequire(similarEntranceIsCurrent(item, d), .targetChanged)
                let old = d.queue
                let title = item.fromHome && item.provider == .heartbeat ? "心动模式" :
                    "根据 \(MusicNativeSimilarData.display(item.seed).title) 推荐"
                let songs = item.songs.enumerated().filter { $0.offset != seedIndex }.map(\.element)
                var rows = try makeEntries(songs, queue: &d.queue, context: .obj([
                    ("sourceType", .string("similar")), ("sourceTitle", .string(title)),
                    ("similar", .obj([("batchId", .string(batchID)), ("basedOn", MusicModel.durable(item.seed)),
                                      ("provider", .string(item.provider.rawValue))]))
                ]))
                if let seedIndex, let seed { rows.insert(seed, at: seedIndex) }
                for i in rows.indices {
                    if let nativeID = MusicNativeSimilarData.nativeID(rows[i]["song"]),
                       let value = item.recommendations[nativeID] {
                        rows[i]["context"]["recommendation"] = value
                    }
                }
                archive(old, into: &d)
                d.queue["entries"] = .array((seedIndex == nil ? seed.map { [$0] } ?? [] : []) + rows)
                let selected = candidateIndex.map { rows[$0]["id"] } ?? seed?["id"] ?? rows[0]["id"]
                d.queue["currentId"] = selected
                for key in ["priority", "history", "failed"] { d.queue[key] = .array([]) }
                d.queue["source"] = .null
                session.batches = [MusicNativeSimilarData.batch(batchID, basedOn: item.seed, provider: item.provider, rows: rows)]
                for row in rows {
                    if let id = MusicNativeSimilarData.nativeID(row["song"]), let value = item.recommendations[id] {
                        session.liveRecommendations[row["id"].string] = value
                    }
                }
                d.queue["similar"] = session.saved
                d.queue["revision"] = .n(d.queue["revision"].int + 1)
                if selected != old["currentId"] { d.resetPosition() }
                return stateDTO(d)
            }
        }
        // Transfer the original lease/observer, without revoking its fence.
        similarMode.preview = nil
        if let old = similarMode.active {
            cancelSimilarBatch(old); releaseSimilarLease(old.lease, after: old.pending)
        }
        discardQueueRecommendations()
        similarMode.active = session; similarMode.restored = true
        playbackFeedbackAdmission = nil
        similarMode.navigation = UUID()
        let play = item.fromHome || candidateIndex != nil
        if play { similarMode.playing = nil; invalidatePlayback() }
        emitSimilar()
        return .init(state: state, action: play ? .playCurrent : .keepCurrent)
    }

    public func exitSimilarMode() async throws -> MusicSimilarModeResult {
        try await restoreSimilarSessionIfNeeded()
        guard let session = similarMode.active else {
            try cancelSimilarModeState()
            return .init(state: stateDTO(try read()), action: .keepCurrent)
        }
        try session.lease.authorization.check()
        let (state, keep) = try session.lease.authorization.perform {
            try similarQueueTransaction(["queue", "playback", "history"]) { d in
                let old = d.queue
                try restoreSimilarQueue(session.returnQueue, into: &d.queue)
                if session.provider == .heartbeat,
                   let current = old["entries"].array.first(where: { $0["id"] == old["currentId"] }) {
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
        else { invalidatePlayback() }
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

    /// A real accepted playing/progress fact, or explicit retry of an already
    /// attempted tail, is required. Selection, resolution and timers aren't facts.
    func similarBoundary(retry: Bool = false) throws {
        guard let session = similarMode.active, session.continueEnabled, session.pending == nil,
              !session.exhausted else { return }
        try check(); try session.lease.authorization.check()
        let d = try read()
        guard let tail = d.queue["entries"].array.last else { return }
        let tailID = tail["id"].string
        guard !session.attempted.contains(tailID) else { return }
        if !retry {
            guard d.queue["currentId"] == tail["id"], let playing = similarMode.playing,
                  playing.occurrence == tailID, attempt?.id == playing.attempt,
                  attempt?.ready == true, attempt?.started == true, attempt?.ended == false else { return }
        }
        session.attempted.insert(tailID)
        let request: MusicNativeSimilarModeRequest
        if session.provider == .songRoaming { request = .songRoaming(sourceIDs: session.sourceIDs) }
        else if let value = MusicNativeSimilarData.queueRequest(d.queue) { request = value }
        else {
            session.status = .empty; session.exhausted = true
            try persistSimilarSession(session); emitSimilar(); return
        }
        let requestID = UUID()
        session.pendingID = requestID; session.pendingTail = tailID; session.status = .loading; session.error = nil
        let basedOn = session.provider == .songRoaming ? session.seed : tail["song"]
        let revision = d.queue["revision"].int, currentID = d.queue["currentId"].string
        session.pending = Task {
            await loadSimilarTail(sessionID: session.id, requestID: requestID, tailID: tailID,
                                  revision: revision, currentID: currentID, basedOn: basedOn, request: request)
        }
        emitSimilar()
    }

    func loadSimilarTail(sessionID: UUID, requestID: UUID, tailID: String, revision: Int, currentID: String,
                         basedOn: MJ, request: MusicNativeSimilarModeRequest) async {
        guard let session = similarMode.active, session.id == sessionID else { return }
        defer {
            similarMode.deadlines.removeValue(forKey: requestID)
            if session.pendingID == requestID {
                session.pending = nil; session.pendingID = nil; session.pendingTail = nil
                if session.status == .loading { session.status = .ready }
                if similarMode.active?.id == sessionID {
                    emitSimilar()
                    if Task.isCancelled { try? similarBoundary() }
                }
            }
        }
        let own = epoch
        do {
            let owner = try similarOwner(), lease = session.lease
            let response = try await similarWork(requestID) {
                try await owner.checkAccountQueue(lease)
                return try await owner.similarModeBatch(lease, request: request)
            }
            try await owner.checkAccountQueue(lease)
            try check(own); try lease.authorization.check()
            guard similarMode.active?.id == sessionID, session.pendingID == requestID, session.continueEnabled else { return }
            let d = try read()
            guard d.queue["entries"].array.last?["id"].string == tailID,
                  d.queue["revision"].int == revision, d.queue["currentId"].string == currentID else { return }
            let incoming = try MusicNativeSimilarData.songs(response)
            let recommendations = try MusicNativeSimilarData.recommendations(response.items)
            try await retainSongMetadata(incoming)
            try check(own); try lease.authorization.check()
            guard similarMode.active?.id == sessionID, session.pendingID == requestID, session.continueEnabled else { return }
            let songs = MusicNativeSimilarData.unique(incoming, excluding: d.queue["entries"].array.map { $0["song"] })
            let status: MusicSimilarModeSnapshot.Status = songs.isEmpty ? .empty : .ready
            let provider: MusicSimilarModeProvider = session.provider == .songRoaming ? .songRoaming : .queueRecommendations
            var batch: MJ?
            var live: [String: MJ] = [:]
            try lease.authorization.perform {
                try similarQueueTransaction(["queue"]) { draft in
                    try musicRequire(draft.queue["entries"].array.last?["id"].string == tailID &&
                        draft.queue["revision"].int == revision && draft.queue["currentId"].string == currentID, .targetChanged)
                    if !songs.isEmpty {
                        let batchID = UUID().uuidString.lowercased()
                        let title = provider == .queueRecommendations ? "更多推荐" : "根据 \(MusicNativeSimilarData.display(basedOn).title) 推荐"
                        var rows = try makeEntries(songs, queue: &draft.queue, context: .obj([
                            ("sourceType", .string("similar")), ("sourceTitle", .string(title)),
                            ("similar", .obj([("batchId", .string(batchID)), ("basedOn", MusicModel.durable(basedOn)),
                                              ("provider", .string(provider.rawValue))]))
                        ]))
                        for i in rows.indices {
                            if let id = MusicNativeSimilarData.nativeID(rows[i]["song"]), let value = recommendations[id] {
                                rows[i]["context"]["recommendation"] = value
                            }
                        }
                        draft.queue["entries"] = .array(draft.queue["entries"].array + rows)
                        draft.queue["revision"] = .n(draft.queue["revision"].int + 1)
                        batch = MusicNativeSimilarData.batch(batchID, basedOn: basedOn, provider: provider, rows: rows)
                        for row in rows {
                            if let id = MusicNativeSimilarData.nativeID(row["song"]), let value = recommendations[id] {
                                live[row["id"].string] = value
                            }
                        }
                    }
                    var saved = session.saved
                    if let batch { saved["batches"] = .array(session.batches + [batch]) }
                    saved["status"] = .string(status.rawValue)
                    draft.queue["similar"] = saved
                }
            }
            if let batch { session.batches.append(batch) }
            session.liveRecommendations.merge(live) { _, new in new }
            session.status = status; session.exhausted = response.hasMore == false || songs.isEmpty
        } catch {
            guard similarMode.active?.id == sessionID, session.pendingID == requestID else { return }
            if epoch != own || !session.lease.authorization.isActive {
                similarLeaseRevoked(session.lease.id); return
            }
            let failure = MusicServiceError.sanitized(error)
            session.status = failure == .cancelled ? .ready : .error
            session.error = failure == .cancelled ? nil : failure
            // A failed append never replaces the queue. Persist just its
            // retry state; storage failure is retained as an error, not success.
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
        if change == .replace || change == .clear || queue["entries"].array.isEmpty {
            queue["similar"] = .null; return
        }
        guard let session = similarMode.active else { return }
        if let tail = session.pendingTail {
            var saved = session.saved
            saved["attempted"] = .strings(session.attempted.filter { $0 != tail }.sorted())
            saved["status"] = .string("ready")
            queue["similar"] = saved
        }
    }
    func similarDidChangeQueue(_ change: MusicNativeSimilarQueueChange) {
        if change == .replace || change == .clear {
            clearSimilarRuntime(); similarMode.restored = true
            return
        }
        guard let d = try? read() else { return }
        if d.queue["entries"].array.isEmpty { clearSimilarRuntime(); similarMode.restored = true; return }
        discardSimilarPreview(); similarMode.navigation = UUID()
        if change == .select { similarMode.playing = nil }
        if let session = similarMode.active, let tail = session.pendingTail {
            session.attempted.remove(tail); cancelSimilarBatch(session); session.status = .ready; session.error = nil
        }
        if let item = similarMode.queue {
            if ordinaryRecommendationRequest(d) == nil { discardQueueRecommendations() }
            else { refreshQueueRecommendations(item, document: d, edited: change == .edit) }
        }
        emitSimilar()
        if change == .edit { try? similarBoundary(); try? queueRecommendationBoundary() }
    }

    func similarBeforeAdvance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                              expectedRevision: Int) async throws -> (Int, MusicAdvanceResult?) {
        try await restoreSimilarSessionIfNeeded()
        let before = try read()
        try musicRequire(before.queue["revision"].int == expectedRevision, .staleRevision)
        if reason == .manual, similarMode.active == nil, similarMode.preview?.enterAfter == true {
            discardSimilarPreview()
        }
        let session = similarMode.active, queue = similarMode.queue
        let pending = session?.pending ?? queue?.pending
        let pendingTail = session?.pendingTail ?? queue?.pendingTail
        if direction == .next, let pending,
           pendingTail == before.queue["currentId"].string,
           before.queue["entries"].array.last?["id"] == before.queue["currentId"],
           before.queue["priority"].array.isEmpty, [0, 1].contains(before.playback["mode"].int) {
            let navigation = UUID(); similarMode.navigation = navigation
            let attemptID = attempt?.id
            await pending.value
            try check()
            let alive = session.map { similarMode.active === $0 && $0.continueEnabled && $0.lease.authorization.isActive } ??
                queue.map { similarMode.queue === $0 && $0.continueEnabled && $0.lease?.authorization.isActive == true } ?? false
            try musicRequire(similarMode.navigation == navigation && alive && attempt?.id == attemptID, .cancelled)
            let after = try read()
            try musicRequire(after.queue["currentId"] == before.queue["currentId"], .cancelled)
            return (after.queue["revision"].int, nil)
        }
        return (expectedRevision, nil)
    }
}

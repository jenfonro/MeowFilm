import Foundation
import MeowDebug
import MeowMusicFeature

enum MusicNativeSimilarQueueChange: Equatable { case replace, clear, select, edit, insertNext, append }

extension NativeMusicService {
    /// Keep uncertain CAS outcomes out of the live recommendation session.
    /// Never roll back or overwrite a possibly committed queue with old state.
    /// The next read must acquire a fresh lease against the persisted session.
    func similarQueueTransaction<T>(_ sections: Set<MusicDomainSection>, _ change: (inout MusicDomainState) throws -> T) throws -> T {
        do { return try transaction(sections, change) }
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
        else { try musicRequire(item.enterAfter, .targetChanged) }
        let before = try read()
        try musicRequire(similarEntranceIsCurrent(item, before), .targetChanged)
        let seed = item.fromHome ? nil : before.queue.entries.first { $0.id == item.entryID }
        try musicRequire(item.fromHome || seed != nil, .targetChanged)
        let seedNativeID = seed == nil ? nil : MusicNativeSimilarData.nativeID(item.seed)
        let seedIndex = seedNativeID.flatMap { id in item.songs.firstIndex { MusicNativeSimilarData.nativeID($0) == id } }
        let restores = item.provider == .heartbeat || item.provider == .queueRecommendations
        let returnQueue: MusicQueueSimilarReturnRecord? = restores
            ? (similarMode.active?.returnQueue ?? before.queue.similar?.returnQueue ?? MusicNativeSimilarData.queueSnapshot(before.queue))
            : nil
        let session = MusicNativeSimilarSession(seed: item.seed, seedEntryID: seed?.id,
            returnQueue: returnQueue, fromHome: item.fromHome, provider: item.provider,
            sourceIDs: item.sourceIDs, context: item.context, scope: item.scope, lease: lease)
        session.continueEnabled = candidateIndex == nil
        session.offset = item.offset
        session.exhausted = item.exhausted
        let batchID = UUID().uuidString.lowercased()
        var live: [String: MusicRecommendationMetadata] = [:]
        let state = try lease.authorization.perform {
            try similarQueueTransaction([.queue, .playback, .history]) { d in
                try musicRequire(similarEntranceIsCurrent(item, d), .targetChanged)
                if session.continueEnabled { selectContinuationMode(&d) }
                let old = d.queue
                let title = item.fromHome && item.provider == .heartbeat ? "心动模式" :
                    "根据 \(try MusicNativeSimilarData.display(item.seed, songs: d.projectSongs([item.seed.songID].filter { !$0.isEmpty })).title) 推荐"
                let songs = item.songs.enumerated().filter { $0.offset != seedIndex }.map(\.element)
                var rows = try makeEntries(
                    songs,
                    state: &d,
                    context: MusicPlayContextRecord(
                        sourceTitle: title,
                        sourceType: item.provider.rawValue,
                        similarBatchId: batchID,
                        similarBasedOnSongId: item.seed.songID.isEmpty ? nil : item.seed.songID,
                        similarProvider: item.provider.rawValue
                    )
                )
                if let seed { rows.insert(seed, at: 0) }
                if item.provider == .heartbeat {
                    let playlistID = item.context?.playlistID.isEmpty == false ? item.context!.playlistID : "favorites"
                    for i in rows.indices {
                        rows[i].context.platform = "wy"
                        rows[i].context.sourceId = playlistID
                    }
                }
                try archive(old, into: &d)
                d.queue.entries = rows
                let rowSongs = try d.projectSongs(rows.map(\.songID))
                let selected: String
                if let candidateIndex {
                    let nativeID = MusicNativeSimilarData.nativeID(item.songs[candidateIndex])
                    selected = try rows.first { row in
                        guard let song = rowSongs[row.songID] else { throw MusicServiceError.notFound }
                        return MusicNativeSimilarData.nativeID(song) == nativeID
                    }!.id
                } else { selected = seed?.id ?? rows[0].id }
                d.queue.currentID = selected
                d.queue.priority = []; d.queue.history = []; d.queue.failed = []
                let heartbeatSourceID = item.provider == .heartbeat
                    ? (item.context?.playlistID.isEmpty == false ? item.context!.playlistID : "favorites")
                    : nil
                d.queue.source = MusicSourceRecord(
                    platform: "wy",
                    sourceType: item.provider.rawValue,
                    sourceTitle: title,
                    sourceId: heartbeatSourceID
                )
                session.batches = [MusicNativeSimilarData.batch(batchID, basedOn: item.seed, provider: item.provider, rows: rows)]
                for row in rows {
                    guard let song = rowSongs[row.songID] else { throw MusicServiceError.notFound }
                    if let id = MusicNativeSimilarData.nativeID(song), let value = item.recommendations[id] {
                        live[row.id] = value
                    }
                }
                d.queue.similar = session.saved
                d.queue.revision += 1
                if selected != old.currentID { d.resetPosition() }
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
        similarMode.active = session; similarMode.restorationState = .restored
        similarMode.offlineSnapshotID = UUID()
        similarMode.navigation = UUID()
        let play = item.fromHome || candidateIndex != nil
        if play {
            similarMode.playing = nil
            invalidatePlayback(reason: .ui)
        }
        emitSimilar()
        if !play {
            do { try similarBoundary() }
            catch {
                if MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "recommendations", event: "similarBoundary.error", error: error)
                }
            }
        }
        DebugRuntime.log(.musicPlayback, sub: "recommendations", event: "similarMode.entered", level: .info, data: .object([
            "provider": .string(item.provider.rawValue),
            "playlistId": .string(item.context?.playlistID ?? ""),
            "count": .int(state.total)
        ]))
        return .init(state: state, action: play ? .playCurrent : .keepCurrent)
    }

    public func exitSimilarMode() async throws -> MusicSimilarModeResult {
        try check()
        if similarMode.active == nil, try read().queue.similar != nil {
            do { try await restoreSimilarSessionIfNeeded() }
            catch {
                if MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "recommendations", event: "exitSimilar.restore.error", error: error)
                }
            }
            try check()
        }
        let returnQueue: MusicQueueSimilarReturnRecord
        let authorization: MusicQueueAuthorization?
        if let session = similarMode.active {
            guard let savedReturn = session.returnQueue else { throw MusicServiceError.unsupported }
            returnQueue = savedReturn
            if session.lease.authorization.isActive {
                do {
                    try await checkSimilarLease(session.lease)
                    try check()
                    authorization = session.lease.authorization.isActive ? session.lease.authorization : nil
                } catch {
                    try check()
                    DebugRuntime.logError(.musicPlayback, sub: "recommendations", event: "exitSimilar.leaseCheck.error", error: error)
                    authorization = nil
                }
            } else {
                authorization = nil
            }
        } else {
            let d = try read()
            guard let saved = d.queue.similar else {
                try cancelSimilarModeState()
                return .init(state: stateDTO(d), action: .keepCurrent)
            }
            guard let savedReturn = saved.returnQueue else {
                try cancelSimilarModeState()
                return .init(state: stateDTO(try read()), action: .keepCurrent)
            }
            returnQueue = savedReturn
            authorization = nil
        }
        let performExit: () throws -> (MusicPlaybackState, Bool) = {
            try self.similarQueueTransaction([.queue, .playback, .history]) { d in
                let old = d.queue
                try self.restoreSimilarQueue(returnQueue, into: &d)
                if let currentID = old.currentID,
                   let current = old.entries.first(where: { $0.id == currentID }) {
                    var rows = d.queue.entries
                    if !rows.contains(where: { $0.id == current.id }) {
                        let anchor = rows.firstIndex { $0.id == d.queue.currentID } ?? -1
                        rows.insert(current, at: anchor + 1)
                        d.queue.entries = rows
                    }
                    self.select(current.id, queue: &d.queue, remember: false)
                }
                let keep = d.queue.currentID == old.currentID
                try self.archive(old, into: &d)
                d.queue.similar = nil
                d.queue.revision += 1
                if !keep { d.resetPosition() }
                return (self.stateDTO(d), keep)
            }
        }
        let (state, keep): (MusicPlaybackState, Bool)
        if let authorization {
            (state, keep) = try authorization.perform(performExit)
        } else {
            (state, keep) = try performExit()
        }
        let playing = similarMode.playing
        clearSimilarRuntime(); similarMode.restorationState = .restored
        if keep { similarMode.playing = playing }
        else { invalidatePlayback(reason: .ui) }
        DebugRuntime.log(.musicPlayback, sub: "recommendations", event: "similarMode.exited", level: .info, data: .object([
            "offline": .bool(authorization == nil),
            "keepCurrent": .bool(keep)
        ]))
        return .init(state: state, action: keep ? .keepCurrent : .restoreOnly)
    }

    func restoreSimilarQueue(_ snapshot: MusicQueueSimilarReturnRecord, into state: inout MusicDomainState) throws {
        try MusicNativeSimilarData.validateQueue(snapshot, songs: state.referenceSongs(snapshot.entries.map(\.songID)))
        state.queue.entries = snapshot.entries
        state.queue.currentID = snapshot.currentID
        state.queue.priority = snapshot.priority
        state.queue.history = snapshot.history
        state.queue.source = snapshot.source
        state.queue.failed = []
    }

    func hasCurrentPlayback(_ d: MusicDomainState) -> Bool {
        guard let playing = similarMode.playing, playing.attempt == attempt?.id,
              playing.occurrence == d.queue.currentID,
              attempt?.ready == true, attempt?.started == true, attempt?.ended == false else { return false }
        return true
    }

    /// Only actual playback of the queue tail can trigger continuation.
    func continuationIsDue(_ d: MusicDomainState) -> Bool {
        hasCurrentPlayback(d) && d.playback.mode == MusicPlaybackMode.loop.rawValue &&
            d.queue.entries.last?.id == d.queue.currentID && d.queue.currentID != nil
    }

    func similarBoundary(retry: Bool = false) throws {
        guard let session = similarMode.active,
              session.continueEnabled, session.pending == nil, !session.exhausted else { return }
        try check(); try session.lease.authorization.check()
        let d = try read()
        let event = (d.queue.currentID ?? "") + ":" + String(session.offset)
        guard retry || (session.buffered != nil || !session.attempted.contains(event)) && continuationIsDue(d) else { return }
        session.attempted.insert(event)
        let request: MusicNativeSimilarModeRequest
        switch session.provider {
        case .heartbeat:
            guard let context = session.context,
                  let tail = d.queue.entries.last,
                  let lastID = MusicNativeSimilarData.nativeID(try d.song(tail.songID)) else {
                throw MusicServiceError.sourceUnavailable
            }
            request = .heartbeat(lastID: lastID, fromHome: session.fromHome, context: context)
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
            let lease = session.lease
            let references: MusicNativeSimilarBatchReferences
            if let buffered = session.buffered {
                try await checkSimilarLease(lease)
                references = buffered
            } else {
                let response = try await similarWork(requestID) {
                    try await self.similarBatch(lease, request: request)
                }
                try await checkSimilarLease(lease)
                try check(own); try lease.authorization.check()
                guard similarMode.active === session, session.pendingID == requestID else { return }
                if session.provider == .heartbeat {
                    guard response.heartbeatContext != nil else { throw MusicServiceError.invalidResponse }
                }
                let incoming = try adoptSongs(MusicNativeSimilarData.songs(response), authoritative: true)
                let offset: Int
                if session.provider == .queueRecommendations {
                    guard let next = response.offset, response.hasMore != nil else { throw MusicServiceError.invalidResponse }
                    offset = next
                } else { offset = session.offset + 1 }
                references = .init(songIDs: incoming.map(\.songID),
                    recommendations: try MusicNativeSimilarData.recommendations(response.items),
                    heartbeatContext: response.heartbeatContext, hasMore: response.hasMore, offset: offset)
            }
            try check(own); try lease.authorization.check()
            guard similarMode.active === session, session.pendingID == requestID else { return }
            guard session.continueEnabled else { session.buffered = references; return }
            let current = try read()
            let songsByID = try repositorySongMap(references.songIDs + current.queue.entries.map(\.songID), current)
            let incoming = try references.songIDs.map { id -> MusicSongRecord in
                guard let song = songsByID[id] else { throw MusicServiceError.notFound }
                return song
            }
            let existing = try current.queue.entries.map { entry -> MusicSongRecord in
                guard let song = songsByID[entry.songID] else { throw MusicServiceError.notFound }
                return song
            }
            let songs = MusicNativeSimilarData.unique(incoming, excluding: existing)
            let exhausted = references.hasMore == false || songs.isEmpty
            var batch: MusicQueueSimilarBatchRecord?
            var live: [String: MusicRecommendationMetadata] = [:]
            try lease.authorization.perform {
                try similarQueueTransaction([.queue]) { draft in
                    if !songs.isEmpty {
                        let batchID = UUID().uuidString.lowercased()
                        var rows = try makeEntries(
                            songs,
                            state: &draft,
                            context: MusicPlayContextRecord(
                                sourceTitle: draft.queue.source?.sourceTitle,
                                sourceType: session.provider.rawValue,
                                similarBatchId: batchID,
                                similarBasedOnSongId: session.seed.songID.isEmpty ? nil : session.seed.songID,
                                similarProvider: session.provider.rawValue
                            )
                        )
                        if session.provider == .heartbeat {
                            let playlistID = references.heartbeatContext?.playlistID.isEmpty == false
                                ? references.heartbeatContext!.playlistID
                                : (session.context?.playlistID.isEmpty == false ? session.context!.playlistID : "favorites")
                            for i in rows.indices {
                                rows[i].context.platform = "wy"
                                rows[i].context.sourceId = playlistID
                            }
                        }
                        let rowSongs = try draft.projectSongs(rows.map(\.songID))
                        for row in rows {
                            guard let song = rowSongs[row.songID] else { throw MusicServiceError.notFound }
                            if let id = MusicNativeSimilarData.nativeID(song),
                               let value = references.recommendations[id] {
                                live[row.id] = value
                            }
                        }
                        draft.queue.entries += rows
                        draft.queue.revision += 1
                        batch = MusicNativeSimilarData.batch(batchID, basedOn: session.seed, provider: session.provider, rows: rows)
                    }
                    var saved = session.saved
                    if let context = references.heartbeatContext {
                        saved.providerPID = context.playlistID
                        saved.providerSID = context.seedID
                        saved.providerCount = context.count
                    }
                    saved.offsetVal = references.offset
                    saved.exhausted = exhausted
                    if let batch { saved.batches = session.batches + [batch] }
                    saved.status = "ready"
                    draft.queue.similar = saved
                }
            }
            if let batch { session.batches.append(batch) }
            session.buffered = nil
            if session.provider == .heartbeat { session.context = references.heartbeatContext }
            session.offset = references.offset
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
            try similarQueueTransaction([.queue]) { $0.queue.similar = session.saved }
        }
    }

    func similarWillChangeQueue(_ change: MusicNativeSimilarQueueChange, queue: inout MusicQueueState) {
        if change == .replace || change == .clear || queue.entries.isEmpty {
            queue.similar = nil
        }
        if change == .select, similarMode.queue != nil,
           queue.entries.first(where: { $0.id == queue.currentID })?.context.sourceType == "queue-recommendations" {
            var src = queue.source ?? MusicSourceRecord()
            src.sourceType = "queue-recommendations"
            src.sourceTitle = "推荐"
            queue.source = src
        }
        if change == .append, similarMode.queue?.active != true, queue.source?.sourceType == "search-song" {
            queue.source?.sourceType = "search"
        }
    }

    func similarDidChangeQueue(_ change: MusicNativeSimilarQueueChange) {
        if change == .replace || change == .clear {
            clearFMPlayback(); clearSimilarRuntime(); similarMode.restorationState = .restored
            return
        }
        guard let d = try? read() else { return }
        if d.queue.entries.isEmpty {
            clearFMPlayback(); clearSimilarRuntime(); similarMode.restorationState = .restored; return
        }
        discardSimilarPreview()
        if change == .select {
            similarMode.playing = nil
        }
        if let item = similarMode.queue {
            if change == .append && !item.active { discardQueueRecommendations() }
            else {
                item.revision = d.queue.revision; item.currentEntryID = d.queue.currentID ?? ""
                if let row = d.queue.entries.first(where: { $0.id == d.queue.currentID }),
                   row.context.sourceType == "queue-recommendations" { item.active = true }
            }
        }
        emitSimilar()
        if change != .select { runContinuationBoundaries() }
    }

    func similarBeforeAdvance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                              expectedRevision: Int) async throws -> (Int, MusicAdvanceResult?) {
        try await restoreSimilarSessionIfNeeded()
        let before = try read()
        try musicRequire(before.queue.revision == expectedRevision, .staleRevision)
        if direction == .next, before.queue.priority.isEmpty,
           let item = similarMode.queue, !item.active, !item.songs.isEmpty, item.status == .ready,
           before.queue.entries.last?.id == before.queue.currentID && before.queue.currentID != nil,
           item.continueEnabled, before.playback.mode == MusicPlaybackMode.loop.rawValue {
            let state = try appendQueueRecommendations(item, selectedIndex: 0)
            if reason == .ended { attempt?.endedConsumed = true }
            invalidatePlayback(reason: reason == .ended ? .playend : .ui); similarDidChangeQueue(.select)
            return (state.revision, .init(state: state, selectedId: state.currentId))
        }
        return (expectedRevision, nil)
    }
}

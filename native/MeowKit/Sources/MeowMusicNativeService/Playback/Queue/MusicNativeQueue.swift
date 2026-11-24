// SPDX-License-Identifier: Apache-2.0
// Native port of packages/music-core/src/queue.js and history.js.
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func nextID(_ d: MusicDomainState, direction: MusicDirection, reason: MusicAdvanceReason = .manual, preview: Bool = false) -> String? {
        let q = d.queue, rows = q.entries, current = q.currentID ?? "", mode = d.playback.mode
        let failed = Set(q.failed)
        let valid = rows.map(\.id).filter { !failed.contains($0) }
        let validSet = Set(valid)
        if rows.isEmpty || valid.isEmpty { return nil }
        if direction == .previous && mode == 2 { return q.history.reversed().first { $0 != current && validSet.contains($0) } }
        if direction == .next, let priority = q.priority.first(where: validSet.contains) { return priority }
        if direction == .next && mode == 3 && reason == .ended && valid.contains(current) { return current }
        if mode == 2 {
            let options = valid.filter { $0 != current }
            guard !options.isEmpty else { return valid.first }
            if preview { return options.first }
            let n = Double.random(in: 0..<1)
            return options[Int(min(max(n, 0), 0.9999999999) * Double(options.count))]
        }
        let step = direction == .next ? 1 : -1
        let start = rows.firstIndex { $0.id == current } ?? (step == 1 ? -1 : rows.count)
        for offset in 1...rows.count {
            var i = start + offset * step
            i = (i % rows.count + rows.count) % rows.count
            let id = rows[i].id
            if validSet.contains(id) { return id }
        }
        return nil
    }

    func stateDTO(_ d: MusicDomainState) -> MusicPlaybackState {
        let policy = MusicModel.policy(d.playback)
        let sourceTitle = MusicModel.firstText(d.queue.source?.title, d.queue.source?.sourceTitle)
        let next = nextID(d, direction: .next, preview: true)
        let previous = nextID(d, direction: .previous, preview: true)
        let priority = Set(d.queue.priority)
        let hasPriority = next.map { priority.contains($0) } ?? false
        let randomNext = d.playback.mode == MusicPlaybackMode.shuffle.rawValue && !hasPriority
        return .init(
            revision: d.queue.revision,
            settingsRevision: d.playback.revision,
            mode: MusicPlaybackMode(rawValue: d.playback.mode) ?? .loop,
            qualityEnabled: policy.enabled,
            qualityOrder: policy.order,
            spatialEnabled: policy.spatial,
            spatialOrder: policy.spatialOrder,
            lyricsEnabled: d.playback.lyricsEnabled,
            showTranslation: d.playback.showTranslation,
            officialSources: MusicModel.officialSources(d.playback),
            currentId: d.queue.currentID.flatMap { $0.isEmpty ? nil : $0 },
            total: d.queue.entries.count,
            position: d.playback.positionSeconds,
            canNext: next != nil,
            canPrevious: previous != nil,
            nextPreviewID: randomNext ? nil : next,
            previousPreviewID: previous,
            sourceCollectionID: d.queue.source?.id.flatMap { $0.isEmpty ? nil : $0 },
            requestQuality: d.playback.qualityRequest ?? "flac",
            queueSourceTitle: sourceTitle.isEmpty ? nil : sourceTitle
        )
    }

    public func state() async throws -> MusicPlaybackState {
        let d = try read(); try d.requireQueue(); try d.requireRevision(.playback)
        return stateDTO(d)
    }

    public func queue() async throws -> MusicQueueSnapshot {
        let d = try read(); try d.requireQueue()
        let rows = d.queue.entries
        let songsByID = try d.referenceSongs(rows.map(\.songID))
        var tracks: [Data: (song: MusicSongRecord, track: MusicRemoteTrack)] = [:]
        return .init(
            revision: d.queue.revision,
            currentId: d.queue.currentID,
            entries: try rows.map { entry in
                guard let song = songsByID[entry.songID] else { throw MusicServiceError.corruptStorage }
                let id = entry.id
                let title = MusicModel.firstText(entry.context.sourceTitle, entry.context.playlist, d.queue.source?.title)
                let ref = MusicReference(kind: .queue, id: id)
                let key = Data(song.songID.utf8)
                let track: MusicRemoteTrack
                if !key.isEmpty, let cached = tracks[key], cached.song == song {
                    track = .init(ref: ref, songId: cached.track.songId, title: cached.track.title,
                        artist: cached.track.artist, album: cached.track.album, duration: cached.track.duration,
                        sources: cached.track.sources, artwork: cached.track.artwork, artistNames: cached.track.artistNames)
                } else {
                    track = try MusicModel.track(song, ref)
                    if !key.isEmpty { tracks[key] = (song, track) }
                }
                return .init(
                    id: id,
                    track: track,
                    sourceTitle: title.isEmpty ? nil : title,
                    remoteSessionID: entry.context.remoteSession.flatMap(UUID.init(uuidString:)),
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(
                        song: song, occurrence: id, context: entry.context
                    )
                )
            }
        )
    }

    func archive(_ queue: MusicQueueState, into d: inout MusicDomainState) throws {
        if queue.entries.count < 2 { return }
        let context = queue.entries.first?.context ?? .empty
        let title = MusicModel.firstText(queue.source?.title, context.sourceTitle, context.playlist)
        let item = MusicHistoryPlaylistRecord(
            id: UUID().uuidString.lowercased(),
            playedAtMs: Int64((now().timeIntervalSince1970 * 1000).rounded()),
            title: title.isEmpty ? "播放列表" : title,
            currentID: queue.currentID,
            source: queue.source,
            context: .empty,
            entries: queue.entries,
            priority: queue.priority,
            history: queue.history
        )
        d.history.playlists = [item] + d.history.playlists
    }

    func select(_ id: String, queue: inout MusicQueueState, remember: Bool = true) {
        if remember, let current = queue.currentID, !current.isEmpty, current != id {
            queue.history.append(current)
        }
        queue.currentID = id
        queue.priority.removeAll { $0 == id }
    }

    func makeEntries(_ songs: [MusicSongRecord], state: inout MusicDomainState,
                     context: MusicPlayContextRecord = .empty,
                     authoritative: Bool = false) throws -> [MusicQueueEntryRecord] {
        let ids = try registerSongs(songs, library: &state.library, authoritative: authoritative, playback: state.playback)
        try state.requireQueue()
        var serial = state.queue.serial
        try musicRequire(ids.count <= musicMaxSafeInteger - serial, .limit)
        let rows = ids.map { id -> MusicQueueEntryRecord in
            serial += 1
            return MusicQueueEntryRecord(id: "entry-\(serial)", songID: id, context: context)
        }
        state.queue.serial = serial
        return rows
    }

    public func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        var changesCurrent = false
        let similarChange: MusicNativeSimilarQueueChange
        switch change {
        case .replace, .collection: similarChange = .replace
        case .clear: similarChange = .clear
        case .select: similarChange = .select
        case .append(_, let next): similarChange = next ? .insertNext : .append
        case .remove, .reorder: similarChange = .edit
        }
        let result = try similarQueueTransaction([.queue, .playback, .history]) { d -> MusicPlaybackState in
            try d.requireQueue()
            try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
            let old = d.queue, previous = old.currentID
            var resolvedSongs: [MusicReference: MusicSongRecord] = [:]
            func resolved(_ ref: MusicReference) throws -> MusicSongRecord {
                if let value = resolvedSongs[ref] { return value }
                let value = try song(ref, d)
                resolvedSongs[ref] = value
                return value
            }
            switch change {
            case .replace(let refs, let index):
                try musicRequire(!refs.isEmpty && refs.indices.contains(index))
                let songs = try refs.map(resolved)
                let rows = try makeEntries(songs, state: &d, context: MusicPlayContextRecord(sourceTitle: "歌曲", sourceType: "song"))
                changesCurrent = true
                try archive(old, into: &d)
                d.queue.entries = rows
                d.queue.currentID = rows[index].id
                d.queue.history = []; d.queue.priority = []; d.queue.failed = []
                d.queue.source = MusicSourceRecord(sourceType: "song", sourceTitle: "歌曲")
            case .collection(let id, let revision, let index):
                changesCurrent = true
                let c = try collection(id, d)
                try musicRequire(c.revision == revision, .staleRevision)
                let ids = c.order
                try musicRequire(ids.indices.contains(index))
                let songsByID = try d.projectSongs(ids)
                let orderedSongs = try ids.map { id -> MusicSongRecord in
                    guard let song = songsByID[id] else { throw MusicServiceError.notFound }
                    return song
                }
                let rows = try makeEntries(orderedSongs, state: &d,
                    context: MusicPlayContextRecord(collectionId: id, sourceTitle: c.name))
                try archive(old, into: &d)
                d.queue.entries = rows
                d.queue.currentID = rows[index].id
                d.queue.history = []; d.queue.priority = []; d.queue.failed = []
                d.queue.source = MusicSourceRecord(id: id, title: c.name)
            case .append(let refs, let next):
                let songs = try refs.map(resolved)
                let added = try makeEntries(songs, state: &d)
                var rows = d.queue.entries
                let currentIndex = rows.firstIndex { $0.id == d.queue.currentID }
                let index = next ? currentIndex.map { $0 + 1 } ?? rows.count : rows.count
                rows.insert(contentsOf: added, at: index)
                d.queue.entries = rows
                if previous == nil { d.queue.currentID = added.first?.id }
                else if next { d.queue.priority = added.map(\.id) + d.queue.priority }
            case .select(let id):
                try musicRequire(d.queue.entries.contains { $0.id == id }, .notFound)
                changesCurrent = true
                select(id, queue: &d.queue)
            case .remove(let id):
                guard let index = d.queue.entries.firstIndex(where: { $0.id == id }) else { throw MusicServiceError.notFound }
                d.queue.entries.remove(at: index)
                d.queue.priority.removeAll { $0 == id }
                d.queue.history.removeAll { $0 == id }
                d.queue.failed.removeAll { $0 == id }
                if previous == id {
                    if d.queue.entries.isEmpty { d.queue.currentID = nil }
                    else { select(d.queue.entries[min(index, d.queue.entries.count - 1)].id, queue: &d.queue, remember: false) }
                }
                if d.queue.entries.isEmpty { d.queue.source = nil }
            case .reorder(let ids):
                let rows = d.queue.entries
                try musicRequire(ids.count == rows.count && Set(ids).count == ids.count && Set(ids) == Set(rows.map(\.id)))
                var byID: [Data: MusicQueueEntryRecord] = [:]
                for row in rows { byID[Data(row.id.utf8)] = row }
                d.queue.entries = ids.map { byID[Data($0.utf8)]! }
            case .clear:
                d.queue.entries = []; d.queue.priority = []; d.queue.history = []; d.queue.failed = []
                d.queue.currentID = nil; d.queue.source = nil
            }
            changesCurrent = changesCurrent || previous != d.queue.currentID
            if changesCurrent { d.resetPosition() }
            similarWillChangeQueue(similarChange, queue: &d.queue)
            d.queue.revision = expectedRevision + 1
            return stateDTO(d)
        }
        if changesCurrent { invalidatePlayback(reason: .ui) }
        similarDidChangeQueue(similarChange)
        return result
    }

    public func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        if reason == .ended {
            try musicRequire(direction == .next)
            try musicRequire(attempt?.ended == true && attempt?.endedConsumed == false, .staleResult)
        }
        let (expectedRevision, handled) = try await similarBeforeAdvance(direction, reason: reason, expectedRevision: expectedRevision)
        if let handled { return handled }
        if reason == .ended {
            try musicRequire(attempt?.ended == true && attempt?.endedConsumed == false, .staleResult)
        }
        let before = try read()
        try musicRequire(before.queue.revision == expectedRevision, .staleRevision)
        if nextID(before, direction: direction, reason: reason, preview: true) == nil {
            if reason == .ended { attempt?.endedConsumed = true }
            return .init(state: stateDTO(before), selectedId: nil)
        }
        let result = try similarQueueTransaction([.queue, .playback]) { d -> MusicAdvanceResult in
            try d.requireQueue()
            try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
            let id = nextID(d, direction: direction, reason: reason)
            if let id {
                if direction == .previous && d.playback.mode == 2 {
                    while let last = d.queue.history.popLast() { if last == id { break } }
                }
                select(id, queue: &d.queue, remember: !(direction == .previous && d.playback.mode == 2))
                similarWillChangeQueue(.select, queue: &d.queue)
            }
            d.resetPosition()
            d.queue.revision = expectedRevision + 1
            return .init(state: stateDTO(d), selectedId: id)
        }
        if reason == .ended { attempt?.endedConsumed = true }
        if result.selectedId != nil { invalidatePlayback(reason: reason == .ended ? .playend : .ui) }
        if result.selectedId != nil { similarDidChangeQueue(.select) }
        return result
    }

    public func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        let update = { (d: inout MusicDomainState) throws -> MusicPlaybackState in
            try musicRequire(d.playback.revision == change.ifRevision, .staleRevision)
            if let quality = change.requestQuality {
                try musicRequire(MusicSourceQualityPolicy.qualities.contains(quality))
                d.playback.qualityRequest = quality
            }
            if let mode = change.mode {
                d.playback.mode = mode.rawValue
                if d.queue.similar != nil { d.queue.similar?.continueEnabled = false }
                if d.queue.fm != nil { d.queue.fm?.continueEnabled = false }
            }
            if let values = change.qualityEnabled {
                try musicRequire(Set(values).count == values.count && values.allSatisfy(MusicModel.qualityOrder.contains))
                d.playback.qualityEnabled = values
            }
            if let values = change.qualityOrder {
                try musicRequire(Set(values).count == values.count && values.allSatisfy(MusicModel.qualityOrder.contains))
                d.playback.qualityOrder = values
            }
            if let values = change.spatialEnabled {
                try musicRequire(Set(values).count == values.count && values.allSatisfy(MusicModel.spatialOrder.contains))
                d.playback.spatialEnabled = values
            }
            if let values = change.spatialOrder {
                try musicRequire(Set(values).count == values.count && values.allSatisfy(MusicModel.spatialOrder.contains))
                d.playback.spatialOrder = values
            }
            let enabled = d.playback.qualityEnabled
            let spatial = d.playback.spatialEnabled
            try musicRequire(enabled.contains { $0 != "spatial" || !spatial.isEmpty }, .qualityDisabled)
            if let value = change.officialSources {
                try musicRequire(Set(value).count == value.count)
                d.playback.officialEnabled = MusicAccountPlatform.allCases.filter(value.contains).map(\.rawValue)
            }
            if let value = change.lyricsEnabled { d.playback.lyricsEnabled = value }
            if let value = change.showTranslation { d.playback.showTranslation = value }
            d.playback.revision = change.ifRevision + 1
            return self.stateDTO(d)
        }
        let result: MusicPlaybackState
        if change.mode == nil { result = try transaction([.playback], update) }
        else { result = try similarQueueTransaction([.playback, .queue], update) }
        if change.mode != nil {
            similarMode.modeSelection = UUID()
            similarMode.preview?.enterAfter = false
            similarMode.active?.continueEnabled = false
            similarMode.queue?.continueEnabled = false
            fmPlayback?.continueEnabled = false
            emitSimilar()
        }
        if change.lyricsEnabled == false { lyricEpoch = UUID(); cancelWork("lyrics") }
        return result
    }

    func selectContinuationMode(_ d: inout MusicDomainState) {
        if d.playback.mode != MusicPlaybackMode.loop.rawValue {
            d.playback.mode = MusicPlaybackMode.loop.rawValue
            d.playback.revision += 1
        }
    }

    public func playedSongs() async throws -> [MusicPlayedSong] {
        let d = try read()
        let allIDs = d.history.days.flatMap { $0.songs.map(\.songID) }
        let songsByID = try d.projectSongs(allIDs)
        return try d.history.days.flatMap { day in
            try day.songs.map { item in
                guard let song = songsByID[item.songID] else { throw MusicServiceError.notFound }
                return MusicPlayedSong(
                    date: day.date,
                    at: Double(item.playedAtMs),
                    track: try repositoryTrack(song),
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(
                        song: song, occurrence: "", context: item.context
                    )
                )
            }
        }
    }

    public func playedPlaylists() async throws -> [MusicPlayedPlaylist] {
        try read().history.playlists.map {
            .init(id: $0.id, title: $0.title, count: $0.entries.count, at: Double($0.playedAtMs))
        }
    }

    public func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState {
        let result = try similarQueueTransaction([.queue, .playback, .history]) { d in
            try d.requireQueue()
            try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
            let matches = d.history.playlists.filter { $0.id == id }
            guard let snapshot = matches.first else { throw MusicServiceError.notFound }
            try musicRequire(matches.count == 1, .corruptStorage)
            let old = d.queue; try archive(old, into: &d)
            d.queue.entries = snapshot.entries
            d.queue.currentID = snapshot.currentID
            d.queue.priority = snapshot.priority
            d.queue.history = snapshot.history
            d.queue.source = snapshot.source
            d.queue.failed = []
            similarWillChangeQueue(.replace, queue: &d.queue)
            d.queue.serial = max(old.serial, snapshot.entries.map { Int($0.id.dropFirst(6)) ?? 0 }.max() ?? 0)
            d.queue.revision = expectedRevision + 1
            d.resetPosition()
            return stateDTO(d)
        }
        invalidatePlayback(reason: .ui); similarDidChangeQueue(.replace); return result
    }
}

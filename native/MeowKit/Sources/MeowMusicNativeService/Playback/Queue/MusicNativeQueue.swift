// SPDX-License-Identifier: Apache-2.0
// Native port of packages/music-core/src/queue.js and history.js.
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func nextID(_ d: MusicDocument, direction: MusicDirection, reason: MusicAdvanceReason = .manual, preview: Bool = false) -> String? {
        let q = d.queue, rows = q["entries"].array, current = q["currentId"].string, mode = d.playback["mode"].int
        let failed = Set(q["failed"].strings)
        let valid = rows.map { $0["id"].string }.filter { !failed.contains($0) }
        let validSet = Set(valid)
        if rows.isEmpty || valid.isEmpty { return nil }
        if direction == .previous && mode == 2 { return q["history"].strings.reversed().first { $0 != current && validSet.contains($0) } }
        if direction == .next, let priority = q["priority"].strings.first(where: validSet.contains) { return priority }
        if direction == .next, mode == 1, similarMode.active?.pending != nil,
           similarMode.active?.pendingTail == current, rows.last?["id"].string == current { return nil }
        if direction == .next && mode == 3 && reason == .ended && valid.contains(current) { return current }
        if mode == 2 {
            let options = valid.filter { $0 != current }
            guard !options.isEmpty else { return valid.first }
            if preview { return options.first }
            let n = random(); let bounded = n.isFinite ? min(max(n, 0), 0.9999999999) : 0
            return options[Int(bounded * Double(options.count))]
        }
        let step = direction == .next ? 1 : -1
        let start = rows.firstIndex { $0["id"].string == current } ?? (step == 1 ? -1 : rows.count)
        for offset in 1...rows.count {
            var i = start + offset * step
            if mode == 0 && !(0..<rows.count).contains(i) { return nil }
            i = (i % rows.count + rows.count) % rows.count
            let id = rows[i]["id"].string
            if validSet.contains(id) { return id }
        }
        return nil
    }
    func stateDTO(_ d: MusicDocument) -> MusicPlaybackState {
        let policy = MusicModel.policy(d.playback), id = d.queue["currentId"].string
        let next = nextID(d, direction: .next, preview: true)
        let previous = nextID(d, direction: .previous, preview: true)
        // Availability may inspect a deterministic candidate, but a visual
        // preview must never pretend it is shuffle's eventual random result.
        let priority = Set(d.queue["priority"].strings)
        let hasPriority = next.map { priority.contains($0) } ?? false
        let randomNext = d.playback["mode"].int == MusicPlaybackMode.shuffle.rawValue && !hasPriority
        return .init(revision: d.queue["revision"].int, settingsRevision: d.playback["revision"].int,
            mode: MusicPlaybackMode(rawValue: d.playback["mode"].int) ?? .loop, qualityEnabled: policy.enabled,
            qualityOrder: policy.order, spatialEnabled: policy.spatial, spatialOrder: policy.spatialOrder,
            lyricsEnabled: d.playback["lyricsEnabled"].bool, showTranslation: d.playback["showTranslation"].bool,
            officialSources: MusicModel.officialSources(d.playback), currentId: id.isEmpty ? nil : id, total: d.queue["entries"].array.count,
            position: d.playback["position"]["seconds"].number,
            canNext: next != nil, canPrevious: previous != nil,
            nextPreviewID: randomNext ? nil : next, previousPreviewID: previous,
            fmNextPreviewID: fmNext(d, direction: .next), fmPreviousPreviewID: fmNext(d, direction: .previous),
            sourceCollectionID: d.queue["source"]["id"].string.isEmpty ? nil : d.queue["source"]["id"].string,
            requestQuality: d.playback["requestQuality"].isString ? d.playback["requestQuality"].string : "flac")
    }
    public func state() async throws -> MusicPlaybackState { stateDTO(try read()) }
    public func queue() async throws -> MusicQueueSnapshot {
        let d = try read()
        var tracks: [Data: (song: MJ, track: MusicRemoteTrack)] = [:]
        return .init(revision: d.queue["revision"].int, currentId: d.queue["currentId"] == .null ? nil : d.queue["currentId"].string,
            entries: try d.queue["entries"].array.map { entry in
                let id = entry["id"].string, song = entry["song"]
                let title = MusicModel.firstText(entry["context"]["sourceTitle"], entry["context"]["playlist"],
                                                d.queue["source"]["title"])
                let ref = MusicReference(kind: .queue, id: id)
                let key = Data(song["songId"].string.utf8)
                let track: MusicRemoteTrack
                if !key.isEmpty, let cached = tracks[key], cached.song == song {
                    track = .init(ref: ref, songId: cached.track.songId, title: cached.track.title,
                        artist: cached.track.artist, album: cached.track.album, duration: cached.track.duration,
                        sources: cached.track.sources, artwork: cached.track.artwork)
                } else {
                    track = try MusicModel.track(song, ref)
                    if !key.isEmpty { tracks[key] = (song, track) }
                }
                return .init(id: id,
                    track: track,
                    sourceTitle: title.isEmpty ? nil : title,
                    remoteSessionID: UUID(uuidString: entry["context"]["remoteSession"].string),
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(
                        song: song, occurrence: id, context: entry["context"]))
            })
    }
    func archive(_ queue: MJ, into d: inout MusicDocument) {
        if queue["entries"].array.count < 2 { return }
        var item = queue
        for key in ["schema", "serial", "revision", "runId", "failed"] { item.remove(key) }
        item["id"] = .string(UUID().uuidString.lowercased()); item["at"] = .number(now().timeIntervalSince1970 * 1000)
        let context = queue["entries"].array.first?["context"] ?? .obj()
        let title = MusicModel.firstText(queue["source"]["title"], context["sourceTitle"], context["playlist"])
        item["title"] = .string(title.isEmpty ? "播放列表" : title)
        d.history["playlists"] = .array(Array(([item] + d.history["playlists"].array).prefix(5)))
    }
    func select(_ id: String, queue: inout MJ, remember: Bool = true) {
        let current = queue["currentId"].string
        if remember && !current.isEmpty && current != id {
            queue["history"] = .strings(Array((queue["history"].strings + [current]).suffix(100)))
        }
        queue["currentId"] = .string(id); queue["priority"] = .strings(queue["priority"].strings.filter { $0 != id })
    }
    func makeEntries(_ songs: [MJ], queue: inout MJ, context: MJ = .obj()) throws -> [MJ] {
        var serial = queue["serial"].int
        let maximumSerial = 9_007_199_254_740_991
        try musicRequire((0...maximumSerial).contains(serial) && songs.count <= maximumSerial - serial, .limit)
        var cleaned: [Data: MJ] = [:]
        let rows = try songs.map { raw -> MJ in
            let songID = raw["songId"].string
            let song: MJ
            if !songID.isEmpty, let value = cleaned[Data(songID.utf8)],
               value["title"] == raw["title"], value["artist"] == raw["artist"],
               value["album"] == raw["album"], value["duration"] == raw["duration"],
               value["cover_url"] == raw["cover_url"], value["sources"] == raw["sources"] { song = value }
            else {
                song = try MusicModel.cleanSong(raw)
                if !songID.isEmpty { cleaned[Data(songID.utf8)] = song }
            }
            serial += 1
            return .obj([("id", .string("entry-\(serial)")), ("key", song["songId"]), ("song", song), ("context", context)])
        }
        queue["serial"] = .n(serial); return rows
    }
    public func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        var changesCurrent = false
        let similarChange: MusicNativeSimilarQueueChange
        switch change {
        case .replace, .collection: similarChange = .replace
        case .clear: similarChange = .clear
        case .select: similarChange = .select
        case .append, .remove, .reorder: similarChange = .edit
        }
        let result = try similarQueueTransaction(["queue", "playback", "history"]) { d -> MusicPlaybackState in
            try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
            let old = d.queue, previous = old["currentId"]
            var resolvedSongs: [MusicReference: MJ] = [:]
            func resolved(_ ref: MusicReference) throws -> MJ {
                if let value = resolvedSongs[ref] { return value }
                let value = try song(ref, d)
                resolvedSongs[ref] = value
                return value
            }
            switch change {
            case .replace(let refs, let index):
                try musicRequire(!refs.isEmpty && refs.indices.contains(index))
                let songs = try refs.map(resolved)
                let rows = try makeEntries(songs, queue: &d.queue, context: .obj([
                    ("sourceTitle", .string("搜索")), ("sourceType", .string("search"))
                ]))
                changesCurrent = true
                archive(old, into: &d); d.queue["entries"] = .array(rows); d.queue["currentId"] = rows[index]["id"]
                for key in ["history", "priority", "failed"] { d.queue[key] = .array([]) }
                d.queue["source"] = .null
            case .collection(let id, let revision, let index):
                changesCurrent = true
                let c = try collection(id, d)
                try musicRequire(c["revision"].int == revision, .staleRevision)
                let ids = c["order"].strings
                try musicRequire(ids.indices.contains(index))
                let rows = try makeEntries(ids.map { d.library["library"]["songs"][$0] }, queue: &d.queue,
                    context: .obj([("collectionId", .string(id)), ("sourceTitle", c["name"])]))
                archive(old, into: &d); d.queue["entries"] = .array(rows); d.queue["currentId"] = rows[index]["id"]
                for key in ["history", "priority", "failed"] { d.queue[key] = .array([]) }
                d.queue["source"] = .obj([("id", .string(id)), ("title", c["name"])])
            case .append(let refs, let next):
                let songs = try refs.map(resolved)
                let added = try makeEntries(songs, queue: &d.queue)
                var rows = d.queue["entries"].array
                let currentIndex = rows.firstIndex { $0["id"] == d.queue["currentId"] }
                let index = next ? currentIndex.map { $0 + 1 } ?? rows.count : rows.count
                rows.insert(contentsOf: added, at: index); d.queue["entries"] = .array(rows)
                if previous == .null { d.queue["currentId"] = added.first?["id"] ?? .null }
                else if next { d.queue["priority"] = .strings(added.map { $0["id"].string } + d.queue["priority"].strings) }
            case .select(let id):
                try musicRequire(d.queue["entries"].array.contains { $0["id"].string == id }, .notFound)
                changesCurrent = true
                select(id, queue: &d.queue)
            case .remove(let id):
                var rows = d.queue["entries"].array
                guard let index = rows.firstIndex(where: { $0["id"].string == id }) else { throw MusicServiceError.notFound }
                rows.remove(at: index); d.queue["entries"] = .array(rows)
                for key in ["priority", "history", "failed"] { d.queue[key] = .strings(d.queue[key].strings.filter { $0 != id }) }
                if previous.string == id {
                    if rows.isEmpty { d.queue["currentId"] = .null }
                    else { select(rows[min(index, rows.count - 1)]["id"].string, queue: &d.queue, remember: false) }
                }
                if rows.isEmpty { d.queue["source"] = .null }
            case .reorder(let ids):
                let rows = d.queue["entries"].array
                try musicRequire(ids.count == rows.count && Set(ids).count == ids.count && Set(ids) == Set(rows.map { $0["id"].string }))
                var byID: [Data: MJ] = [:]
                for row in rows { byID[Data(row["id"].string.utf8)] = row }
                d.queue["entries"] = .array(ids.map { byID[Data($0.utf8)]! })
            case .clear:
                for key in ["entries", "priority", "history", "failed"] { d.queue[key] = .array([]) }
                d.queue["currentId"] = .null; d.queue["source"] = .null
            }
            changesCurrent = changesCurrent || previous != d.queue["currentId"]
            if changesCurrent { d.resetPosition() }
            similarWillChangeQueue(similarChange, queue: &d.queue)
            d.queue["revision"] = .n(expectedRevision + 1)
            return stateDTO(d)
        }
        // Reselecting the same occurrence is a new user play intent. Publish
        // invalidation only after CAS, including cancellation of physical work.
        if changesCurrent { invalidatePlayback() }
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
        try musicRequire(before.queue["revision"].int == expectedRevision, .staleRevision)
        if nextID(before, direction: direction, reason: reason, preview: true) == nil {
            if reason == .ended { attempt?.endedConsumed = true }
            return .init(state: stateDTO(before), selectedId: nil)
        }
        let result = try similarQueueTransaction(["queue", "playback"]) { d -> MusicAdvanceResult in
            try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
            let id = nextID(d, direction: direction, reason: reason)
            if let id {
                if direction == .previous && d.playback["mode"].int == 2 {
                    var history = d.queue["history"].strings
                    while let last = history.popLast() { if last == id { break } }
                    d.queue["history"] = .strings(history)
                }
                select(id, queue: &d.queue, remember: !(direction == .previous && d.playback["mode"].int == 2))
            }
            d.resetPosition(); d.queue["revision"] = .n(expectedRevision + 1)
            return .init(state: stateDTO(d), selectedId: id)
        }
        if reason == .ended { attempt?.endedConsumed = true }
        if result.selectedId != nil { invalidatePlayback() }
        if result.selectedId != nil { similarDidChangeQueue(.select) }
        return result
    }
    public func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        let result = try transaction(["playback"]) { d in
            try musicRequire(d.playback["revision"].int == change.ifRevision, .staleRevision)
            if let quality = change.requestQuality {
                try musicRequire(MusicSourceQualityPolicy.qualities.contains(quality))
                d.playback["requestQuality"] = .string(quality)
            }
            if let mode = change.mode { d.playback["mode"] = .n(mode.rawValue) }
            var settings = d.playback["qualitySettings"]
            for (key, values, allowed) in [
                ("qualityEnabled", change.qualityEnabled, MusicModel.qualityOrder), ("qualityOrder", change.qualityOrder, MusicModel.qualityOrder),
                ("spatialEnabled", change.spatialEnabled, MusicModel.spatialOrder), ("spatialOrder", change.spatialOrder, MusicModel.spatialOrder)] {
                if let values {
                    try musicRequire(Set(values).count == values.count && values.allSatisfy(allowed.contains))
                    settings[key] = .strings(values)
                }
            }
            let enabled = settings.contains("qualityEnabled") ? settings["qualityEnabled"].strings : MusicModel.qualityOrder
            let spatial = settings.contains("spatialEnabled") ? settings["spatialEnabled"].strings : MusicModel.spatialOrder
            try musicRequire(enabled.contains { $0 != "spatial" || !spatial.isEmpty }, .qualityDisabled)
            d.playback["qualitySettings"] = settings
            if let value = change.officialSources {
                try musicRequire(Set(value).count == value.count)
                d.playback["officialSources"] = .strings(MusicAccountPlatform.allCases.filter(value.contains).map(\.rawValue))
            }
            if let value = change.lyricsEnabled { d.playback["lyricsEnabled"] = .bool(value) }
            if let value = change.showTranslation { d.playback["showTranslation"] = .bool(value) }
            d.playback["revision"] = .n(change.ifRevision + 1); return stateDTO(d)
        }
        if change.lyricsEnabled == false { lyricEpoch = UUID(); cancelWork("lyrics") }
        return result
    }
    public func playedSongs() async throws -> [MusicPlayedSong] {
        let d = try read()
        return try d.history["days"].array.flatMap { day in
            try day["songs"].array.map {
                let song = $0["song"]
                let context = $0["context"].isObject ? MusicModel.durable($0["context"]) : .obj()
                return .init(date: day["date"].string, at: $0["at"].number, track: try remember(song),
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(
                        song: song, occurrence: "", context: context))
            }
        }
    }
    public func playedPlaylists() async throws -> [MusicPlayedPlaylist] {
        try read().history["playlists"].array.map { .init(id: $0["id"].string, title: $0["title"].string, count: $0["entries"].array.count, at: $0["at"].number) }
    }
    public func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState {
        let result = try similarQueueTransaction(["queue", "playback", "history"]) { d in
            try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
            guard let snapshot = d.history["playlists"].array.first(where: { $0["id"].string == id }) else { throw MusicServiceError.notFound }
            let old = d.queue; archive(old, into: &d)
            for key in ["entries", "currentId", "priority", "history", "source"] { d.queue[key] = snapshot[key] }
            d.queue["failed"] = .array([])
            similarWillChangeQueue(.replace, queue: &d.queue)
            d.queue["serial"] = .n(max(old["serial"].int, snapshot["entries"].array.map { Int($0["id"].string.dropFirst(6)) ?? 0 }.max() ?? 0))
            d.queue["revision"] = .n(expectedRevision + 1); d.resetPosition(); return stateDTO(d)
        }
        invalidatePlayback(); similarDidChangeQueue(.replace); return result
    }
}

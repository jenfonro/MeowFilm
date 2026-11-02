// SPDX-License-Identifier: Apache-2.0
// Native history actions: iWebPlus b900c259 playback-history.js / queue.js.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicHistoryManaging {
    public func historyDetail(id: String) async throws -> MusicHistoryDetail {
        let own = epoch
        let raw = try historySnapshot(id: id, in: read())
        let snapshot = try MusicNativeHistorySnapshot(raw)
        let result = MusicHistoryDetail(
            summary: snapshot.summary, entries: snapshot.displayEntries,
            selectedIndex: snapshot.selectedIndex, sourceLabel: snapshot.sourceLabel,
            actionToken: historyActionToken(raw))
        // The nonisolated protection fence can close during JSON projection.
        try check(own)
        return result
    }

    public func removeHistory(detail: MusicHistoryDetail) async throws {
        try transaction(["history"]) { d in
            _ = try boundHistorySnapshot(detail, in: d)
            // playback-history.js removePlaylist only removes this snapshot.
            // Do not archive/clear the queue or remove any library/file data.
            d.history["playlists"] = .array(d.history["playlists"].array.filter {
                !musicSame($0["id"].string, detail.id)
            })
        }
    }

    public func clearHistory(_ category: MusicHistoryCategory) async throws {
        try transaction(["history"]) { d in
            switch category {
            case .songs: d.history["days"] = .array([])
            case .playlists: d.history["playlists"] = .array([])
            }
        }
    }

    public func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?,
                               expectedRevision: Int) async throws -> MusicPlaybackState {
        do {
            let result = try similarQueueTransaction(["queue", "playback", "history"]) { d in
                try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
                try musicRequire(expectedRevision < 9_007_199_254_740_991, .limit)
                let raw = try boundHistorySnapshot(detail, in: d)
                let snapshot = try MusicNativeHistorySnapshot(raw)
                guard let index = startIndex ?? snapshot.selectedIndex else { throw MusicServiceError.notFound }
                try musicRequire(snapshot.entries.indices.contains(index))

                let old = d.queue
                // Like queue.restore: normalize songs through makeEntry, but
                // preserve the archived occurrence IDs and each row's context.
                var rows = try makeEntries(snapshot.entries.map { $0["song"] }, queue: &d.queue)
                for i in rows.indices {
                    rows[i]["id"] = snapshot.entries[i]["id"]
                    rows[i]["context"] = snapshot.entries[i]["context"]
                }
                d.queue["serial"] = .n(max(d.queue["serial"].int, snapshot.maximumSerial))
                archive(old, into: &d)
                let selected = rows[index]["id"].string
                d.queue["entries"] = .array(rows)
                d.queue["currentId"] = .string(selected)
                d.queue["priority"] = .strings(snapshot.priority.filter { $0 != selected })
                d.queue["history"] = .strings(snapshot.history)
                d.queue["source"] = snapshot.source
                d.queue.remove("similar")
                d.queue["failed"] = .array([])
                d.queue["revision"] = .n(expectedRevision + 1)
                d.resetPosition()
                return stateDTO(d)
            }
            clearSimilarRuntime()
            invalidatePlayback()
            return result
        } catch MusicServiceError.commitUnconfirmed {
            // transaction has already committed before detecting cancellation.
            // Old resolve/lyrics attempts must not survive a committed replace.
            invalidatePlayback()
            throw MusicServiceError.commitUnconfirmed
        }
    }

    private func historySnapshot(id: String, in d: MusicDocument) throws -> MJ {
        _ = try musicText(id)
        let matches = d.history["playlists"].array.filter { musicSame($0["id"].string, id) }
        guard let snapshot = matches.first else { throw MusicServiceError.notFound }
        try musicRequire(matches.count == 1, .corruptDocument)
        return snapshot
    }

    private func historyActionToken(_ snapshot: MJ) -> MusicHistoryActionToken {
        // Hash the entire stored snapshot, not the title/count/visible rows.
        // The private run/epoch binding rejects other owners and old access
        // generations without retaining up to 2,000 temporary song handles.
        .init(rawValue: MJ.strings([
            "music.history.action.v1", runID, epoch.uuidString,
            snapshot.stable.digest
        ]).digest)
    }

    private func boundHistorySnapshot(_ detail: MusicHistoryDetail, in d: MusicDocument) throws -> MJ {
        let snapshot = try historySnapshot(id: detail.id, in: d)
        try musicRequire(musicHex(detail.actionToken.rawValue) &&
            musicSame(detail.actionToken.rawValue, historyActionToken(snapshot).rawValue), .targetChanged)
        return snapshot
    }
}

/// Private parsed data, never a second persistence model or a playback port.
private struct MusicNativeHistorySnapshot {
    let summary: MusicPlayedPlaylist
    let entries: [MJ]
    let displayEntries: [MusicHistoryDisplayEntry]
    let selectedIndex: Int?
    let sourceLabel: String?
    let maximumSerial: Int
    let priority: [String]
    let history: [String]
    let source: MJ

    init(_ raw: MJ) throws {
        do {
            try musicRequire(raw.isObject && raw["entries"].isArray &&
                raw["entries"].array.count <= 2000, .corruptDocument)
            let id = try raw["id"].text()
            try musicRequire(raw["at"].isNumber && raw["at"].number.isFinite &&
                (0...9_007_199_254_740_991).contains(raw["at"].number), .corruptDocument)
            try musicRequire(raw["currentId"] == .null || raw["currentId"].isString, .corruptDocument)
            for key in ["priority", "history"] {
                try musicRequire(raw[key] == .null || raw[key].isArray &&
                    raw[key].array.allSatisfy(\.isString), .corruptDocument)
            }
            try musicRequire(raw["source"] == .null || raw["source"].isObject, .corruptDocument)
            try musicRequire(raw["context"] == .null || raw["context"].isObject, .corruptDocument)

            var ids = Set<String>(), serial = 0
            var rows: [MJ] = [], display: [MusicHistoryDisplayEntry] = []
            rows.reserveCapacity(raw["entries"].array.count)
            display.reserveCapacity(raw["entries"].array.count)
            for (index, entry) in raw["entries"].array.enumerated() {
                let occurrence = entry["id"].string
                guard occurrence.range(of: "^entry-[1-9][0-9]*$", options: .regularExpression) != nil,
                      let number = Int(occurrence.dropFirst(6)), number <= 9_007_199_254_740_991,
                      ids.insert(occurrence).inserted, entry["song"].isObject,
                      entry["context"] == .null || entry["context"].isObject else {
                    throw MusicServiceError.corruptDocument
                }
                serial = max(serial, number)
                let song = try MusicModel.cleanSong(entry["song"])
                _ = try song["songId"].text()
                _ = try MusicModel.sources(song)
                let title = try song["title"].text(1024)
                try musicRequire(song["duration"].number.isFinite && song["duration"].number >= 0, .corruptDocument)
                var context = entry["context"] == .null ? MJ.obj() : MusicModel.durable(entry["context"])
                context.remove("queueSource")
                rows.append(.obj([("id", .string(occurrence)), ("song", song), ("context", context)]))
                display.append(.init(id: occurrence, index: index, title: title,
                    artist: song["artist"].string, album: song["album"].string,
                    artwork: MusicNativePublicBrowseSupport.artwork(song["cover_url"]),
                    duration: song["duration"].number,
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(song: song, occurrence: occurrence,
                        context: entry["context"].isObject ? context :
                            (raw["context"].isObject ? raw["context"] : raw["entries"].array.first?["context"] ?? .obj()))))
            }

            let context = raw["context"].isObject ? raw["context"] : rows.first?["context"] ?? .obj()
            let title = MusicModel.firstText(raw["title"], context["sourceTitle"], context["playlist"])
            let explicitLabel = context["sourceLabel"].string
            let labels = ["search": "搜索", "search-song": "搜索", "history-song": "已播歌曲",
                          "fm": "FM", "playlist": "歌单", "collection": "歌单", "similar": "推荐模式"]
            sourceLabel = explicitLabel.isEmpty ? labels[context["sourceType"].string] : explicitLabel
            summary = .init(id: id, title: title.isEmpty ? "播放列表" : title,
                            count: rows.count, at: raw["at"].number)
            entries = rows; displayEntries = display; maximumSerial = serial
            let selected = rows.firstIndex { $0["id"] == raw["currentId"] } ?? rows.indices.first
            selectedIndex = selected
            let selectedID = selected.map { rows[$0]["id"].string }
            var seenPriority = Set<String>()
            priority = raw["priority"].strings.filter {
                ids.contains($0) && $0 != selectedID && seenPriority.insert($0).inserted
            }
            history = raw["history"].strings.filter { ids.contains($0) }
            source = rows.isEmpty ? .null : MusicModel.durable(raw["source"])
        } catch {
            // Stored snapshots have no upstream/provider-error surface.
            throw MusicServiceError.corruptDocument
        }
    }
}

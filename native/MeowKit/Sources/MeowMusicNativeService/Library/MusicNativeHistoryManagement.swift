// SPDX-License-Identifier: Apache-2.0
// Native history actions: iWebPlus b900c259 playback-history.js / queue.js.
import CryptoKit
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicHistoryManaging {
    public func historyDetail(id: String) async throws -> MusicHistoryDetail {
        let own = epoch
        let state = try read()
        let raw = try historySnapshot(id: id, in: state)
        let snapshot = try MusicNativeHistorySnapshot(raw, songs: state.referenceSongs(raw.entries.map(\.songID)))
        let result = MusicHistoryDetail(
            summary: snapshot.summary, entries: snapshot.displayEntries,
            selectedIndex: snapshot.selectedIndex, sourceLabel: snapshot.sourceLabel,
            actionToken: historyActionToken(raw))
        try check(own)
        return result
    }

    public func removeHistory(detail: MusicHistoryDetail) async throws {
        try transaction([.history]) { d in
            _ = try boundHistorySnapshot(detail, in: d)
            d.history.playlists = d.history.playlists.filter { $0.id != detail.id }
        }
    }

    public func clearHistory(_ category: MusicHistoryCategory) async throws {
        try transaction([.history]) { d in
            switch category {
            case .songs: d.history.days = []
            case .playlists: d.history.playlists = []
            }
        }
    }

    public func restoreHistory(detail: MusicHistoryDetail, startIndex: Int?,
                               expectedRevision: Int) async throws -> MusicPlaybackState {
        do {
            let result = try similarQueueTransaction([.queue, .playback, .history]) { d in
                try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
                try musicRequire(expectedRevision < musicMaxSafeInteger, .limit)
                let raw = try boundHistorySnapshot(detail, in: d)
                let snapshot = try MusicNativeHistorySnapshot(raw, songs: d.referenceSongs(raw.entries.map(\.songID)))
                guard let index = startIndex ?? snapshot.selectedIndex else { throw MusicServiceError.notFound }
                try musicRequire(snapshot.entries.indices.contains(index))

                let old = d.queue
                let rows = snapshot.entries
                d.queue.serial = max(d.queue.serial, snapshot.maximumSerial)
                try archive(old, into: &d)
                let selected = rows[index].id
                d.queue.entries = rows
                d.queue.currentID = selected
                d.queue.priority = snapshot.priority.filter { $0 != selected }
                d.queue.history = snapshot.history
                d.queue.source = snapshot.source
                d.queue.similar = nil
                d.queue.failed = []
                d.queue.revision = expectedRevision + 1
                d.resetPosition()
                return stateDTO(d)
            }
            clearSimilarRuntime()
            invalidatePlayback(reason: .ui)
            return result
        } catch MusicServiceError.commitUnconfirmed {
            invalidatePlayback(reason: .ui)
            throw MusicServiceError.commitUnconfirmed
        }
    }

    private func historySnapshot(id: String, in d: MusicDomainState) throws -> MusicHistoryPlaylistRecord {
        _ = try musicText(id)
        let matches = d.history.playlists.filter { $0.id == id }
        guard let snapshot = matches.first else { throw MusicServiceError.notFound }
        try musicRequire(matches.count == 1, .corruptStorage)
        return snapshot
    }

    private func historyActionToken(_ snapshot: MusicHistoryPlaylistRecord) -> MusicHistoryActionToken {
        var hasher = SHA256()
        func feed(_ text: String?) {
            if let text {
                hasher.update(data: Data([1]))
                hasher.update(data: Data(text.utf8))
                hasher.update(data: Data([0]))
            } else {
                hasher.update(data: Data([0]))
            }
        }
        func feedInt(_ value: Int64) {
            var le = value.littleEndian
            withUnsafeBytes(of: &le) { hasher.update(bufferPointer: $0) }
        }
        feed("music.history.action.v1")
        feed(runID)
        feed(epoch.uuidString)
        feed(snapshot.id)
        feedInt(snapshot.playedAtMs)
        feed(snapshot.title)
        feed(snapshot.currentID)
        for field in (snapshot.source ?? .empty).sqliteValues {
            if case .text(let s) = field { feed(s) } else { feed(nil) }
        }
        for field in snapshot.context.orderedFields { feed(field) }
        feedInt(Int64(snapshot.entries.count))
        for entry in snapshot.entries {
            feed(entry.id)
            feed(entry.songID)
            for field in entry.context.orderedFields { feed(field) }
        }
        feedInt(Int64(snapshot.priority.count))
        for item in snapshot.priority { feed(item) }
        feedInt(Int64(snapshot.history.count))
        for item in snapshot.history { feed(item) }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        return .init(rawValue: digest)
    }

    private func boundHistorySnapshot(_ detail: MusicHistoryDetail, in d: MusicDomainState) throws -> MusicHistoryPlaylistRecord {
        let snapshot = try historySnapshot(id: detail.id, in: d)
        try musicRequire(musicHex(detail.actionToken.rawValue) &&
            detail.actionToken.rawValue == historyActionToken(snapshot).rawValue, .targetChanged)
        return snapshot
    }
}

/// Private parsed data, never a second persistence model or a playback port.
private struct MusicNativeHistorySnapshot {
    let summary: MusicPlayedPlaylist
    let entries: [MusicQueueEntryRecord]
    let displayEntries: [MusicHistoryDisplayEntry]
    let selectedIndex: Int?
    let sourceLabel: String?
    let maximumSerial: Int
    let priority: [String]
    let history: [String]
    let source: MusicSourceRecord?

    init(_ raw: MusicHistoryPlaylistRecord, songs: [String: MusicSongRecord]) throws {
        do {
            let id = try musicText(raw.id)
            try musicRequire((0...Int64(musicMaxSafeInteger)).contains(raw.playedAtMs), .corruptStorage)

            var ids = Set<String>(), serial = 0
            var rows: [MusicQueueEntryRecord] = [], display: [MusicHistoryDisplayEntry] = []
            rows.reserveCapacity(raw.entries.count)
            display.reserveCapacity(raw.entries.count)
            for (index, entry) in raw.entries.enumerated() {
                let occurrence = entry.id
                let suffix = occurrence.hasPrefix("entry-") ? occurrence.dropFirst(6) : Substring()
                let suffixBytes = suffix.utf8
                guard let firstByte = suffixBytes.first, (49...57).contains(firstByte),
                      suffixBytes.allSatisfy({ (48...57).contains($0) }),
                      let number = Int(suffix), number <= musicMaxSafeInteger,
                      ids.insert(occurrence).inserted,
                      let song = songs[entry.songID] else {
                    throw MusicServiceError.corruptStorage
                }
                serial = max(serial, number)
                rows.append(MusicQueueEntryRecord(id: occurrence, songID: entry.songID, context: entry.context))
                let displayContext = entry.context.isEmpty
                    ? (raw.context.isEmpty ? (raw.entries.first?.context ?? .empty) : raw.context)
                    : entry.context
                display.append(.init(
                    id: occurrence,
                    index: index,
                    title: song.title,
                    artist: song.artist,
                    album: song.album,
                    artwork: MusicModel.artworkURL(song.coverURL),
                    duration: song.effectiveDuration,
                    displayIdentity: MusicNativeQueueDisplayIdentity.make(
                        song: song,
                        occurrence: occurrence,
                        context: displayContext
                    )
                ))
            }

            let context = raw.context.isEmpty ? (rows.first?.context ?? .empty) : raw.context
            let title = MusicModel.firstText(raw.title, context.sourceTitle, context.playlist)
            let labels = ["search": "搜索", "search-song": "搜索", "history-song": "已播歌曲",
                          "fm": "FM", "playlist": "歌单", "collection": "歌单", "similar": "推荐模式"]
            sourceLabel = labels[context.sourceType ?? ""]
            summary = .init(id: id, title: title.isEmpty ? "播放列表" : title,
                            count: rows.count, at: Double(raw.playedAtMs))
            entries = rows; displayEntries = display; maximumSerial = serial
            let selected = rows.firstIndex { $0.id == raw.currentID } ?? rows.indices.first
            selectedIndex = selected
            let selectedID = selected.map { rows[$0].id }
            var seenPriority = Set<String>()
            priority = raw.priority.filter {
                ids.contains($0) && $0 != selectedID && seenPriority.insert($0).inserted
            }
            history = raw.history.filter { ids.contains($0) }
            source = rows.isEmpty ? nil : raw.source
        } catch {
            throw MusicServiceError.corruptStorage
        }
    }
}

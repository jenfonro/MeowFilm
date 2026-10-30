// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 queue-sheet.js: occurrence rows, three pages, confirmation.
import Foundation

struct MusicReferenceQueueIntent: Hashable, Sendable {
    let access: UUID
    let revision: Int
    let ids: [String]

    func admits(access: UUID, revision: Int, ids: [String], permitted: Bool) -> Bool {
        permitted && self.access == access && self.revision == revision && self.ids == ids
    }
    func moving(_ offsets: IndexSet, to destination: Int) -> [String]? {
        guard !offsets.isEmpty, Set(ids).count == ids.count, (0...ids.count).contains(destination),
              offsets.allSatisfy({ ids.indices.contains($0) }) else { return nil }
        let picked = offsets.map { ids[$0] }
        var remaining = ids.enumerated().filter { !offsets.contains($0.offset) }.map(\.element)
        let insertion = destination - offsets.filter { $0 < destination }.count
        remaining.insert(contentsOf: picked, at: insertion)
        return remaining
    }
}

/// A confirmation/read action may not survive a replaced detail, even when the
/// history ID is unchanged. The service still verifies the opaque token itself.
struct MusicReferenceHistoryIntent: Equatable, Sendable {
    let access: UUID
    let request: UUID
    let token: MusicHistoryActionToken
    func admits(access: UUID, request: UUID, token: MusicHistoryActionToken?, permitted: Bool) -> Bool {
        permitted && self.access == access && self.request == request && self.token == token
    }
}

/// Three category labels share one native strip: current, songs, history cards.
enum MusicReferenceQueuePage: Int, CaseIterable, Sendable {
    case current, songs, playlists
    var title: String {
        switch self { case .current: return "当前播放"; case .songs: return "已播歌曲"; case .playlists: return "已播歌单" }
    }
}

enum MusicReferenceQueueStrip {
    static func ids(history: [String]) -> [String] {
        ["current", "songs"] + (history.isEmpty ? ["playlists"] : history.map { "history:" + $0 })
    }
    static func category(_ id: String) -> MusicReferenceQueuePage {
        id == "current" ? .current : id == "songs" ? .songs : .playlists
    }
    static func tab(_ category: MusicReferenceQueuePage, selected: String, history: [String]) -> String {
        switch category {
        case .current: return "current"
        case .songs: return "songs"
        case .playlists:
            if Self.category(selected) == .playlists { return reconciled(selected, history: history) }
            return history.first.map { "history:" + $0 } ?? "playlists"
        }
    }
    static func reconciled(_ selected: String, history: [String]) -> String {
        if ids(history: history).contains(selected) { return selected }
        if category(selected) == .playlists { return history.first.map { "history:" + $0 } ?? "playlists" }
        return "current"
    }
}

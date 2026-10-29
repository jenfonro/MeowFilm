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

struct MusicReferenceSimilarPresentation {
    let snapshot: MusicSimilarModeSnapshot
    var showsSection: Bool { !snapshot.active || snapshot.status != .ready }
    var title: String { snapshot.active ? "推荐续播" : "根据 \(snapshot.seed.title) 推荐" }
    var candidates: [MusicSimilarModeCandidate] { snapshot.active ? [] : snapshot.candidates }
    var status: String? {
        switch snapshot.status {
        case .ready: return nil
        case .loading: return snapshot.active ? "正在获取下一批相似歌曲…" : "正在获取相似歌曲…"
        case .empty: return snapshot.active ? "暂无新的相似歌曲，已入队歌曲按原循环规则播放" : "暂无有效相似歌曲"
        case .error: return (snapshot.error?.localizedDescription ?? "相似推荐加载失败") + " · 当前队列未改变"
        }
    }
    var showsRetry: Bool { snapshot.status == .error || snapshot.status == .empty }
    var canRetry: Bool { showsRetry && snapshot.canRetry }
}

/// Three category labels share one native strip. Historical cards are ordered
/// between songs and current; tab entry and swipe entry have distinct rules.
enum MusicReferenceQueuePage: Int, CaseIterable, Sendable, Identifiable {
    case current, songs, playlists
    var id: Int { rawValue }
    var title: String {
        switch self { case .current: return "当前播放"; case .songs: return "已播歌曲"; case .playlists: return "已播歌单" }
    }
    static func historySelection(_ selected: String?, ids: [String]) -> String? {
        if let selected, ids.contains(selected) { return selected }
        return ids.first
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

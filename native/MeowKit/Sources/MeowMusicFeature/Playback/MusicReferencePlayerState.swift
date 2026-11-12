// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: index.html full-player, player-layout.css, lyrics.js.
import Foundation

/// A gesture/queued UI intent belongs to the displayed occurrence and attempt,
/// not just a song ID (the same recording may occur twice in the queue).
struct MusicReferencePlayerFence: Equatable, Sendable {
    let occurrence: String?
    let attempt: UUID?
    let access: UUID
    let queueRevision: Int
    let settingsRevision: Int

    func admits(_ current: Self, permitted: Bool) -> Bool { permitted && self == current }
    static func ownsAttempt(active: UUID?, observed: UUID?, owner: String?) -> Bool {
        active != nil && active == observed && owner == "music"
    }
}

enum MusicReferencePlayerLabels {
    static func fmSource(_ action: MusicHomeAction?) -> String {
        switch action {
        case .fm:
            let title = MusicFMModeChoice.title(for: action)
            return "FM·" + (title.hasSuffix("模式") ? String(title.dropLast(2)) : title)
        case .ruleFM(_, _, let name): return "FM·" + (name.isEmpty ? "频道" : name)
        default: return "FM·私人漫游"
        }
    }

    static func source(fm: String?, context: String?, collection: String?, track: String) -> String {
        if let fm { return fm }
        if let context, !context.isEmpty { return context }
        if let collection, !collection.isEmpty { return collection }
        return track
    }
    static func quality(requestedLabel: String?) -> String {
        requestedLabel.map { "请求：" + $0 } ?? "音源与音质"
    }
}

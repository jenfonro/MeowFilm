// SPDX-License-Identifier: GPL-3.0-only
// Presentation rules from iWebPlus b900c259, mini-player.js / mini-swipe.js.
import Foundation

struct MusicMiniPlayerLifecycle: Equatable, Sendable {
    private enum Display: Equatable, Sendable {
        case compact, preview(until: TimeInterval), held
    }
    private var display = Display.compact
    private(set) var occurrenceID: String?
    var expanded: Bool { display != .compact }
    var held: Bool { display == .held }
    var autoCollapseAt: TimeInterval? {
        if case .preview(let deadline) = display { return deadline }
        return nil
    }

    mutating func invalidate() { display = .compact }

    mutating func select(_ next: String?) {
        guard occurrenceID != next else { return }
        occurrenceID = next
        // A deliberately held card stays expanded across next/previous.
        if next == nil || !held { invalidate() }
    }

    mutating func activate(_ occurrenceID: String, at: TimeInterval) {
        guard !occurrenceID.isEmpty else { return }
        self.occurrenceID = occurrenceID
        display = .preview(until: at + 5)
    }

    mutating func hold() {
        if expanded { display = .held }
    }

    mutating func interactOutside() {
        if held { invalidate() }
    }

    mutating func expire(at: TimeInterval) {
        if let deadline = autoCollapseAt, at >= deadline { invalidate() }
    }
}

/// A seek/activation can never be retargeted to a newer song, account or native
/// attempt, even when a source refresh resolves the same queue occurrence.
public struct MusicMiniPlayerPlaybackIdentity: Equatable, Sendable {
    public let occurrenceID: String
    public let nativeAttemptID: UUID
    public let accessGeneration: UUID

    public init(occurrenceID: String, nativeAttemptID: UUID, accessGeneration: UUID) {
        self.occurrenceID = occurrenceID
        self.nativeAttemptID = nativeAttemptID
        self.accessGeneration = accessGeneration
    }
}

struct MusicMiniPlayerCommandIdentity: Equatable, Sendable {
    let occurrenceID: String
    let nativeAttemptID: UUID?
    let accessGeneration: UUID
    let queueRevision: Int
    let settingsRevision: Int
}

enum MusicMiniPlayerNavigationDirection: Equatable, Sendable {
    case previous, next
}

enum MusicMiniPlayerText {
    static func time(_ value: Double?) -> String {
        guard let value, value.isFinite, value >= 0 else { return "--:--" }
        // Avoid trapping on malformed durations; no media fact is changed.
        let seconds = Int(min(value.rounded(.down), Double(Int32.max)))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    static func compactTitle(_ track: MusicRemoteTrack?) -> String {
        guard let track else { return "暂无播放" }
        var title = track.title.trimmingCharacters(in: .whitespacesAndNewlines)
        let artist = track.artist.trimmingCharacters(in: .whitespacesAndNewlines)
        if !title.isEmpty, !artist.isEmpty, artist != "未知", artist != "未知歌手" {
            if title.hasPrefix(artist + " - ") { title.removeFirst(artist.count + 3) }
            else if title.hasPrefix(artist + "-") { title.removeFirst(artist.count + 1) }
            else if title.hasSuffix(" - " + artist) { title.removeLast(artist.count + 3) }
            else if title.hasSuffix("-" + artist) { title.removeLast(artist.count + 1) }
            return title.trimmingCharacters(in: .whitespacesAndNewlines) + " - " + artist
        }
        return !title.isEmpty ? title : !artist.isEmpty ? artist : "未知歌曲"
    }

    static func progress(position: Double, duration: Double?) -> Double {
        guard let duration, duration.isFinite, duration > 0, position.isFinite else { return 0 }
        return min(1, max(0, position / duration))
    }
}

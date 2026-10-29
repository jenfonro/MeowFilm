// SPDX-License-Identifier: GPL-3.0-only
// Presentation rules from iWebPlus b900c259, mini-player.js / mini-swipe.js.
import Foundation

/// A media observation, not a timer tick or a requested seek position.
struct MusicMiniPlayerSample: Equatable, Sendable {
    let occurrenceID: String?
    let attemptID: UUID?
    let at: TimeInterval
    let position: Double
    let rate: Double
    let playing: Bool
    let seeking: Bool
}

struct MusicMiniPlayerLifecycle: Equatable, Sendable {
    private(set) var expanded = false
    private(set) var held = false
    private(set) var occurrenceID: String?
    private(set) var elapsed: TimeInterval = 0
    private var previous: MusicMiniPlayerSample?

    mutating func invalidate() {
        expanded = false
        held = false
        elapsed = 0
        previous = nil
    }

    mutating func select(_ next: String?) {
        guard occurrenceID != next else { return }
        occurrenceID = next
        previous = nil
        // A deliberately held card stays expanded across next/previous.
        if next == nil || !held { invalidate() }
    }

    mutating func activate(_ occurrenceID: String) {
        guard !occurrenceID.isEmpty else { return }
        self.occurrenceID = occurrenceID
        expanded = true
        held = false
        elapsed = 0
        previous = nil
    }

    mutating func hold() {
        guard expanded, !held else { return }
        held = true
        previous = nil
    }

    /// Backgrounding, buffering and cancellation break the sampling interval,
    /// but do not spend the remaining five seconds or undo a manual hold.
    mutating func suspend() { previous = nil }

    mutating func sample(_ value: MusicMiniPlayerSample) {
        select(value.occurrenceID)
        guard expanded, !held, value.playing, !value.seeking,
              value.attemptID != nil, value.at.isFinite, value.position.isFinite,
              value.position >= 0, value.rate.isFinite, value.rate > 0 else {
            previous = nil
            return
        }
        guard previous?.at != value.at else { return }
        let last = previous
        previous = value
        guard let last, last.attemptID == value.attemptID, last.rate == value.rate else { return }
        let wall = value.at - last.at, media = value.position - last.position
        // Reject seeks, clock reversal and stalled media. At 2x, ten media
        // seconds are five actually played seconds, not ten.
        guard wall > 0, media >= 0, media <= wall * value.rate + 0.25 else { return }
        elapsed += min(wall, media / value.rate)
        if elapsed >= 5 - 0.0001 { invalidate() }
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

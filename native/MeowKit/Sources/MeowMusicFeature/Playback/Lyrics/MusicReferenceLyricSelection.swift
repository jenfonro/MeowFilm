// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: lyrics.js and player-layout.css.
import Foundation

/// Like lyrics.js, scrolling previews a line indefinitely. No timeout seeks,
/// resumes playback, or silently re-enables following; confirmation is explicit.
struct MusicReferenceLyricSelection: Equatable, Sendable {
    private(set) var fence: MusicReferencePlayerFence?
    private(set) var selected: Int?
    private(set) var playbackLine: Int?
    private(set) var isDragging = false
    var isManual: Bool { fence != nil }
    mutating func begin(_ fence: MusicReferencePlayerFence, selected: Int?, dragging: Bool = false,
                        playbackLine: Int? = nil) {
        self.fence = fence; self.selected = selected
        isDragging = dragging
        self.playbackLine = playbackLine
    }
    mutating func preview(_ id: Int) {
        guard isManual, isDragging else { return }
        selected = id
    }
    mutating func end(_ id: Int) {
        guard isManual, isDragging else { return }
        selected = id; isDragging = false
    }
    func target(lines: [MusicLyricLine], current: MusicReferencePlayerFence,
                permitted: Bool, duration: Double?) -> Double? {
        guard !isDragging else { return nil }
        guard let time = previewTime(lines: lines, current: current, permitted: permitted),
              let duration, duration.isFinite, duration > 0, time <= duration else { return nil }
        return time
    }
    func previewTime(lines: [MusicLyricLine], current: MusicReferencePlayerFence,
                     permitted: Bool) -> Double? {
        guard fence?.admits(current, permitted: permitted) == true, let selected,
              let line = lines.first(where: { $0.id == selected }),
              line.time.isFinite, line.time >= 0 else { return nil }
        return line.time
    }
    mutating func confirm(accepted: Bool, current: MusicReferencePlayerFence, permitted: Bool) -> Bool {
        guard accepted, selected != nil, !isDragging,
              fence?.admits(current, permitted: permitted) == true else { return false }
        cancel()
        return true
    }
    mutating func cancel() { fence = nil; selected = nil; playbackLine = nil; isDragging = false }
}

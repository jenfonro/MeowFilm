// SPDX-License-Identifier: GPL-3.0-only
import CoreGraphics

/// One measured line height controls both the content and its enclosing dock.
/// Dynamic Type must move the seek/control rows, not merely enlarge the clip.
struct MusicMiniPlayerLayout: Equatable, Sendable {
    let lineHeight: Double
    init(lineHeight: Double) {
        self.lineHeight = lineHeight.isFinite && lineHeight > 0 ? lineHeight : 22
    }
    var compactHeight: Double { max(58, lineHeight + 24) }
    var detailLineHeight: Double { lineHeight * 21 / 22 }
    var expandedInfoHeight: Double { max(100, detailLineHeight * 3 + 37) }
    var compactArtworkTop: Double { 8 + (compactHeight - 58) / 2 }
    var seekTop: Double { expandedInfoHeight }
    var controlsTop: Double { seekTop + 46 }
    var expandedHeight: Double { controlsTop + 50 }
    var compactControlsTop: Double { compactHeight - 50 }
    func compactControlFrame(width: Double) -> CGRect {
        let width = width.isFinite ? max(0, width) : 0
        let controls = min(100, width)
        return CGRect(x: CGFloat(width - controls), y: CGFloat(compactControlsTop),
                      width: CGFloat(controls), height: 44)
    }
}

/// Native interpolation of the fixed mini-player.css curve. These parameters
/// describe the reference; they are not a measured iPhone performance result.
struct MusicMiniPlayerMorph: Equatable, Sendable {
    let x1, y1, x2, y2, duration: Double
    init(expanded: Bool) {
        if expanded { x1 = 0.415; y1 = 1.005; x2 = 0.228; y2 = 1.126; duration = 0.74 }
        else { x1 = 0.35; y1 = 0.3; x2 = 0.5; y2 = 0.95; duration = 0.37 }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
import CoreGraphics

/// Measured text heights keep metadata, progress and controls in separate rows.
struct MusicMiniPlayerLayout: Equatable, Sendable {
    let titleLineHeight, artistLineHeight, lyricLineHeight: Double
    var compactHeight: Double { max(58, titleLineHeight + 24) }
    var expandedInfoHeight: Double {
        max(116, 16 + titleLineHeight + 6 + artistLineHeight + 8 + lyricLineHeight + 16)
    }
    var compactArtworkTop: Double { (compactHeight - 42) / 2 }
    var seekTop: Double { expandedInfoHeight }
    var controlsTop: Double { seekTop + 50 }
    var expandedHeight: Double { controlsTop + 64 }
    var compactControlsTop: Double { (compactHeight - 44) / 2 }
    func compactControlFrame(width: Double) -> CGRect {
        let controls = min(88, max(0, width - 4))
        return CGRect(x: CGFloat(max(0, width - controls - 4)), y: CGFloat(compactControlsTop),
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

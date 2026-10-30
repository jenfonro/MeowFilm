// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// queue-sheet.css b900c259. Media queries use the full viewport, not the
/// already padded list or the height remaining below the sheet's header.
struct MusicReferenceQueueMetrics: Equatable, Sendable {
    let width: Double
    let height: Double
    let horizontalPadding: Double
    let handleHeight: Double
    let headingBottomPadding: Double
    let tabFontSize: Double
    let selectionGap: Double
    let rowControlWidth: Double

    init(viewportWidth: Double, viewportHeight: Double) {
        let w = viewportWidth.isFinite ? max(0, viewportWidth) : 0
        let h = viewportHeight.isFinite ? max(0, viewportHeight) : 0
        let narrow = w <= 350, short = h <= 500
        width = min(480, w)
        height = min(h, short ? max(180, h * 0.92) : max(260, min(640, h * 0.78)))
        horizontalPadding = narrow ? 6 : 12
        handleHeight = short ? 16 : 24
        headingBottomPadding = short ? 0 : 8
        tabFontSize = narrow ? 12 : 14
        selectionGap = narrow ? 5 : 8
        rowControlWidth = narrow ? 40 : 44
    }

    static func bottomPadding(safeArea: Double) -> Double {
        max(12, safeArea.isFinite ? safeArea : 0)
    }

    static func historyCardWidth(pageWidth: Double) -> Double {
        pageWidth.isFinite ? max(0, pageWidth - 32) : 0
    }

}

/// Display identity is separate from the selected card and its action token.
/// Paging must not erase/reload an unchanged, already read snapshot.
struct MusicReferenceHistoryDisplayIdentity: Equatable, Sendable {
    let id: String
    let title: String
    let count: Int
    let at: Double
    let access: UUID

    init(_ summary: MusicPlayedPlaylist, access: UUID) {
        id = summary.id; title = summary.title; count = summary.count
        at = summary.at; self.access = access
    }
}

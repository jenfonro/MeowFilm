// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// iWebPlus discovery.css:51–55: intrinsic chips, 8pt gaps, wrapping rows.
/// Geometry is independent of the rendering host and never owns filter state.
public struct FilterTagRows {
    public let size: CGSize
    public let frames: [CGRect]

    public init(width: CGFloat?, items: [CGSize], rowSpacing: CGFloat = 8) {
        let gap: CGFloat = 8
        let intrinsic = items.reduce(CGFloat.zero) { $0 + $1.width } + CGFloat(max(0, items.count - 1)) * gap
        let width = max(1, width ?? intrinsic)
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0
        var frames: [CGRect] = []
        for item in items {
            let itemWidth = min(width, max(0, item.width))
            if x > 0 && x + itemWidth > width {
                x = 0; y += rowHeight + rowSpacing; rowHeight = 0
            }
            frames.append(CGRect(x: x, y: y, width: itemWidth, height: item.height))
            x += itemWidth + gap; rowHeight = max(rowHeight, item.height)
        }
        self.frames = frames
        self.size = CGSize(width: width, height: items.isEmpty ? 0 : y + rowHeight)
    }
}

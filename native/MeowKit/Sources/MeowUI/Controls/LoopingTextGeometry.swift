import Foundation

enum LoopingTextGeometry {
    static func period(textWidth: Double, viewport: Double, gap: Double = 28) -> Double? {
        guard textWidth.isFinite, viewport.isFinite, gap.isFinite,
              viewport > 0, textWidth > viewport, gap >= 0 else { return nil }
        return textWidth + gap
    }
}

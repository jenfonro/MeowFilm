// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowUI

/// player-layout.css:205–236, chorus-marker.css:2–4. Visible geometry is
/// independent of the 44pt native control's hit/accessibility area.
enum MusicReferenceProgressGeometry {
    static let trackHeight: CGFloat = 3
    static let thumbDiameter: CGFloat = 8
    static let draggingThumbDiameter: CGFloat = 12
    static let hitHeight: CGFloat = 44
    static let chorusDiameter: CGFloat = 5

    static func fraction(_ time: Double, duration: Double?) -> Double {
        guard time.isFinite, let duration, duration.isFinite, duration > 0 else { return 0 }
        return min(1, max(0, time / duration))
    }
    static func buffers(_ ranges: [Range<Double>], duration: Double?, complete: Bool) -> [Range<Double>] {
        guard let duration, duration.isFinite, duration > 0 else { return [] }
        if complete { return [0..<1] }
        return ranges.prefix(256).compactMap {
            guard $0.lowerBound.isFinite, $0.upperBound.isFinite else { return nil }
            let start = fraction($0.lowerBound, duration: duration), end = fraction($0.upperBound, duration: duration)
            return end > start ? start..<end : nil
        }
    }
}

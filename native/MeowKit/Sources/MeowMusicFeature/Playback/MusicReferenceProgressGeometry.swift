// SPDX-License-Identifier: GPL-3.0-only
import Foundation

enum MusicReferenceProgressGeometry {
    static func fraction(_ time: Double, duration: Double?) -> Double {
        guard time.isFinite, let duration, duration.isFinite, duration > 0 else { return 0 }
        return min(1, max(0, time / duration))
    }
}

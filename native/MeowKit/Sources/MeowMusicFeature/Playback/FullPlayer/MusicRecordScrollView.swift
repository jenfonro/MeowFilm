#if os(iOS)
import UIKit

/// Hit testing admits only a touch that starts on a visible disc. Once admitted,
/// UIScrollView keeps tracking it even when the finger leaves the circular surface.
@MainActor
final class MusicRecordScrollView: UIScrollView {
    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        guard super.point(inside: point, with: event) else { return false }
        for case let disc as MusicRecordDiscView in subviews
            where !disc.isHidden && disc.alpha > 0.01 && !disc.bounds.isEmpty {
            let local = disc.convert(point, from: self)
            let x = (local.x - disc.bounds.midX) / (disc.bounds.width / 2)
            let y = (local.y - disc.bounds.midY) / (disc.bounds.height / 2)
            if x * x + y * y <= 1 { return true }
        }
        return false
    }
}
#endif

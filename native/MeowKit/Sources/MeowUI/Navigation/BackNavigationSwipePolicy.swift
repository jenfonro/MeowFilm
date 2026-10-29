import Foundation

/// Only an intercepted editor needs a separate release policy. Ordinary pages
/// use UINavigationController's own interactive transition.
enum BackNavigationSwipePolicy {
    static func commits(distance: Double, velocity: Double, width: Double) -> Bool {
        guard distance.isFinite, velocity.isFinite, width.isFinite, width > 0 else { return false }
        return distance > width * 0.5 || distance >= 24 && velocity > 300
    }
}

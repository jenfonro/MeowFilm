import Foundation

/// Stack navigation and retained-page returns share one release decision.
public enum BackNavigationSwipePolicy {
    public static func commits(distance: Double, velocity: Double, width: Double) -> Bool {
        guard distance.isFinite, velocity.isFinite, width.isFinite, width > 0 else { return false }
        return distance > width * 0.5 || distance >= 24 && velocity > 300
    }
}

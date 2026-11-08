import Foundation

/// Media evidence is deliberately independent of metadata recording fusion.
public enum MusicMediaDuration {
    public static let tolerance: Double = 10
    public static func matches(expected: Double, actual: Double?) -> Bool {
        guard expected.isFinite, expected > 0, let actual, actual.isFinite, actual > 0 else { return true }
        return abs(expected - actual) <= tolerance
    }
}

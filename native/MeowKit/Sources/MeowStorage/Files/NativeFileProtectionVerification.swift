import Foundation

/// A Simulator filesystem is not evidence of device Data Protection. On the
/// reviewed iOS 26.1 runtime, setting complete protection succeeds but reading
/// the protection attribute returns nil. Only that absent Simulator attribute
/// is accepted for development; any other reported class is still rejected.
public enum NativeFileProtectionVerification: Equatable, Sendable {
    case completeUntilFirstUserAuthentication
    case unavailableInSimulator
    case rejected

    public static func inspect(_ attribute: Any?) -> Self {
        #if targetEnvironment(simulator)
        return inspect(attribute, simulator: true)
        #else
        return inspect(attribute, simulator: false)
        #endif
    }

    // Internal seam tests both policies on every Apple runner. Callers cannot
    // opt a real device into the Simulator exception.
    static func inspect(_ attribute: Any?, simulator: Bool) -> Self {
        if attribute == nil { return simulator ? .unavailableInSimulator : .rejected }
        #if os(iOS)
        if (attribute as? FileProtectionType) == .completeUntilFirstUserAuthentication {
            return .completeUntilFirstUserAuthentication
        }
        #endif
        if (attribute as? String) == "NSFileProtectionCompleteUntilFirstUserAuthentication" {
            return .completeUntilFirstUserAuthentication
        }
        return .rejected
    }
}

#if os(iOS)
import Foundation

/// Declarative chrome requirements owned by each page instead of type checks in the coordinator.
public struct PageChromePolicy: Equatable, Sendable {
    public var hidesDock: Bool
    public var hidesMiniPlayer: Bool
    public var isSettingsBranchRoot: Bool
    public var isImmersiveReading: Bool

    public static let standard = PageChromePolicy()

    public init(
        hidesDock: Bool = false,
        hidesMiniPlayer: Bool = false,
        isSettingsBranchRoot: Bool = false,
        isImmersiveReading: Bool = false
    ) {
        self.hidesDock = hidesDock
        self.hidesMiniPlayer = hidesMiniPlayer
        self.isSettingsBranchRoot = isSettingsBranchRoot
        self.isImmersiveReading = isImmersiveReading
    }
}
#endif

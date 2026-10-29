import Foundation
#if os(iOS) || os(macOS)
import Combine

/// A read-only UI projection. Song sampling and preference persistence belong
/// to the injected owner, never to the shared page or navigation container.
@MainActor
public protocol PageThemeProviding: AnyObject {
    var pageColorway: PageColorway { get }
    var animatesPageColorway: Bool { get }
    var pageThemeChanges: AnyPublisher<Void, Never> { get }
}
#endif

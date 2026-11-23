#if os(iOS)
import UIKit

/// Explicit contract for views that participate in in-place theme propagation
/// without recursive `subviews` crawling.
@MainActor
public protocol PageThemeable: AnyObject {
    func applyTheme(_ theme: PageTheme)
}

/// Compile-time enforced themeable view type for declarative containers such as `SettingsListRow`.
public typealias ThemeableView = UIView & PageThemeable
#endif

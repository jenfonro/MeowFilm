#if os(iOS)
import UIKit

/// Explicit theme contract so containers distribute theme updates directly to
/// owned controls instead of crawling arbitrary `subviews`.
@MainActor
public protocol PageThemeable: AnyObject {
    func applyTheme(_ theme: PageTheme)
}
extension PageHeaderTextButton: PageThemeable {}
#endif

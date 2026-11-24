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

/// Weak deduplicating registry for explicit `PageThemeable` dispatch inside `MeowUI` containers.
@MainActor
struct ThemeableRegistry {
    private struct Box { weak var value: (any PageThemeable)? }
    private var entries: [Box] = []

    mutating func register(_ item: any PageThemeable) {
        guard !entries.contains(where: { $0.value === item }) else { return }
        entries.append(Box(value: item))
    }

    mutating func apply(_ theme: PageTheme) {
        entries.removeAll { $0.value == nil }
        for entry in entries { entry.value?.applyTheme(theme) }
    }

    mutating func removeAll() { entries.removeAll() }
}

/// Weak deduplicating registry for `UILabel` color updates inside `MeowUI` containers.
@MainActor
struct WeakLabelRegistry {
    private struct Box { weak var value: UILabel? }
    private var entries: [Box] = []

    mutating func register(_ label: UILabel) {
        guard !entries.contains(where: { $0.value === label }) else { return }
        entries.append(Box(value: label))
    }

    mutating func applyColor(_ color: UIColor) {
        entries.removeAll { $0.value == nil }
        for entry in entries { entry.value?.textColor = color }
    }
}
#endif

#if os(iOS)
import UIKit

/// Native `UISwitch` aligned to the ordinary text line with a 44pt touch target.
@MainActor
public final class SettingsSwitch: UISwitch, PageThemeable {
    public var changed: ((Bool) -> Void)?

    public override init(frame: CGRect) {
        super.init(frame: frame)
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
        addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.changed?(self.isOn)
        }, for: .valueChanged)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override var alignmentRectInsets: UIEdgeInsets {
        UIEdgeInsets(top: 6, left: 0, bottom: 6, right: 0)
    }

    public func applyTheme(_ theme: PageTheme) {
        onTintColor = UIColor(theme.settings.accent)
    }

    public override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(
            dx: -max(0, (44 - bounds.width) / 2),
            dy: -max(0, (44 - bounds.height) / 2)
        ).contains(point)
    }
}
#endif

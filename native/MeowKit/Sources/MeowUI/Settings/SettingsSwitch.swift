#if os(iOS)
import UIKit

/// A native switch fits the ordinary text line without adding its touch height twice.
@MainActor
public final class SettingsSwitch: UIView, PageThemeable {
    public let control: UISwitch = SettingsSwitchControl()
    public var changed: ((Bool) -> Void)?

    public override init(frame: CGRect) {
        super.init(frame: frame)
        setContentHuggingPriority(.required, for: .horizontal)
        setContentCompressionResistancePriority(.required, for: .horizontal)
        addSubview(control)
        control.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            changed?(control.isOn)
        }, for: .valueChanged)
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (view: SettingsSwitch, _: UITraitCollection) in
            view.invalidateIntrinsicContentSize(); view.setNeedsLayout()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override var intrinsicContentSize: CGSize {
        CGSize(width: 44, height: UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection).lineHeight)
    }

    public func applyTheme(_ theme: PageTheme) {
        control.onTintColor = UIColor(theme.settings.accent)
    }

    public override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: 0, dy: -max(0, (44 - bounds.height) / 2)).contains(point)
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        control.bounds.size = control.intrinsicContentSize
        control.center = CGPoint(x: bounds.midX, y: bounds.midY)
        let scale = min(1, bounds.height / control.bounds.height)
        control.transform = CGAffineTransform(scaleX: scale, y: scale)
    }
}

@MainActor
private final class SettingsSwitchControl: UISwitch {
    override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        let scale = max(0.01, transform.d)
        return bounds.insetBy(dx: -max(0, (44 / scale - bounds.width) / 2),
            dy: -max(0, (44 / scale - bounds.height) / 2)).contains(point)
    }
}
#endif

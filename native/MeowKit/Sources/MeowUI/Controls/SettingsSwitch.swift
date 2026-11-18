#if os(iOS)
import UIKit
import SwiftUI

/// A native switch fits the ordinary text line without adding its touch height twice.
@MainActor
public final class SettingsSwitch: UIView {
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

@MainActor
public struct SettingsToggleStyle: ToggleStyle {
    @Environment(\.isEnabled) private var enabled
    @ScaledMetric(relativeTo: .body) private var controlHeight = UIFont.preferredFont(
        forTextStyle: .body, compatibleWith: UITraitCollection(preferredContentSizeCategory: .large)
    ).lineHeight

    public init() {}
    public func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 12) {
            configuration.label.frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle().inset(by: -SettingsVisualMetrics.rowVerticalPadding))
                .onTapGesture { if enabled { configuration.isOn.toggle() } }
            SwitchInput(value: configuration.$isOn)
                .frame(width: 44, height: controlHeight)
        }
    }

    private struct SwitchInput: UIViewRepresentable {
        @Binding var value: Bool
        @Environment(\.isEnabled) var enabled
        func makeUIView(context: Context) -> SettingsSwitch { SettingsSwitch() }
        func updateUIView(_ view: SettingsSwitch, context: Context) {
            view.control.isOn = value; view.control.isEnabled = enabled
            view.control.onTintColor = UIColor { UIColor(SettingsTheme(dark: $0.userInterfaceStyle == .dark).accent) }
            view.changed = { value = $0 }
        }
    }
}
#endif

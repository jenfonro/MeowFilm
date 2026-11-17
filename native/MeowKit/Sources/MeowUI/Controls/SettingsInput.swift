#if os(iOS)
import UIKit

/// Input geometry belongs to the field, so theme and size changes keep one shell.
@MainActor
open class SettingsTextField: UITextField {
    open override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        return CGSize(width: size.width, height: max(36, (font?.lineHeight ?? 20) + 16))
    }

    open override func point(inside point: CGPoint, with event: UIEvent?) -> Bool {
        bounds.insetBy(dx: -max(0, (44 - bounds.width) / 2),
            dy: -max(0, (44 - bounds.height) / 2)).contains(point)
    }

    open override func textRect(forBounds bounds: CGRect) -> CGRect {
        super.textRect(forBounds: bounds).insetBy(dx: 12, dy: 0)
    }

    open override func editingRect(forBounds bounds: CGRect) -> CGRect {
        super.editingRect(forBounds: bounds).insetBy(dx: 12, dy: 0)
    }

    open override func layoutSubviews() {
        super.layoutSubviews()
        layer.cornerRadius = min(bounds.width, bounds.height) / 2
    }
}

@MainActor
open class SettingsTextView: UITextView {
    open override func layoutSubviews() {
        super.layoutSubviews()
        let radius = min(bounds.width, bounds.height) / 2
        layer.cornerRadius = radius
        let vertical = max(10, (font?.lineHeight ?? 20) / 2)
        let horizontal = max(12, ceil(radius - sqrt(max(0, 2 * radius * vertical - vertical * vertical))) + 2)
        let inset = UIEdgeInsets(top: vertical, left: horizontal, bottom: vertical, right: horizontal)
        if textContainerInset != inset { textContainerInset = inset }
        textContainer.lineFragmentPadding = 0
    }
}
#endif

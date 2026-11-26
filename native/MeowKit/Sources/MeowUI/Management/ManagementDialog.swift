#if os(iOS)
import UIKit
import MeowDebug

/// A cancellable native modal shared by address input and import confirmation.
/// Built directly on `FloatingCardDialogController` with unified `DebugDialogControllable` support.
@MainActor
open class ManagementDialog: FloatingCardDialogController {
    public var submit: (() async throws -> Bool)?
    public var accepted: (() -> Void)?
    public var cancelled: (() -> Void)?
    public let acceptButton = UIButton(type: .system)
    public let cancelButton = UIButton(type: .system)
    private let actionTitle: String
    private let buttonSpacer = UIView()
    private var cancelling = false
    private var didResolve = false

    open override var dismissesOnBackdropTap: Bool { false }
    open override var showsCloseButton: Bool { false }
    open override var showsHeaderDivider: Bool { true }
    open override var showsFooter: Bool { true }
    open override var usesCompactHeader: Bool { true }
    open override var cardCornerRadius: CGFloat { 20 }

    public init(title: String, action: String = "确定", themeProvider: any PageThemeProviding) {
        self.actionTitle = action
        super.init(title: title, themeProvider: themeProvider)
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        backdropView.accessibilityIdentifier = "management.dialog.scrim"
        cardView.accessibilityIdentifier = "management.dialog.panel"
        errorLabel.accessibilityIdentifier = "management.dialog.error"
        bodyStack.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true

        cancelButton.setTitle("取消", for: .normal)
        cancelButton.accessibilityIdentifier = "management.dialog.cancel"
        cancelButton.addAction(UIAction { [weak self] _ in self?.requestDismiss() }, for: .touchUpInside)

        acceptButton.setTitle(actionTitle, for: .normal)
        acceptButton.accessibilityIdentifier = "management.dialog.confirm"
        acceptButton.addAction(UIAction { [weak self] _ in self?.commit() }, for: .touchUpInside)

        for button in [cancelButton, acceptButton] {
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            button.titleLabel?.numberOfLines = 0
            button.titleLabel?.textAlignment = .center
            button.setContentHuggingPriority(.required, for: .horizontal)
            button.setContentCompressionResistancePriority(.required, for: .vertical)
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        }
        footerStack.spacing = 18
        for child in [buttonSpacer, cancelButton, acceptButton] {
            footerStack.addArrangedSubview(child)
        }
        applyTypography()
        applyTheme()
    }

    open override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = footerStack.bounds.width
        guard width > 0 else { return }
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ||
            cancelButton.intrinsicContentSize.width + acceptButton.intrinsicContentSize.width + footerStack.spacing > width
        buttonSpacer.isHidden = vertical
        footerStack.axis = vertical ? .vertical : .horizontal
        footerStack.alignment = vertical ? .fill : .center
    }

    open override func applyTypography() {
        super.applyTypography()
        let buttonFont = UIFontMetrics(forTextStyle: .subheadline)
            .scaledFont(for: .systemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        cancelButton.titleLabel?.font = buttonFont
        acceptButton.titleLabel?.font = buttonFont
        view.setNeedsLayout()
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        cancelButton.setTitleColor(UIColor(settings.muted), for: .normal)
        acceptButton.setTitleColor(UIColor(settings.accent), for: .normal)
    }

    open override func dialogWillClose() {
        super.dialogWillClose()
        guard !didResolve else { return }
        didResolve = true
        let onCancel = cancelled
        accepted = nil
        cancelled = nil
        onCancel?()
    }

    public static func input(placeholder: String, multiline: Bool = false) -> SettingsTextField {
        let field = SettingsTextField()
        field.inputAppearance = .outlined
        field.placeholder = placeholder
        field.font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(for: .systemFont(ofSize: 14))
        field.adjustsFontForContentSizeCategory = true
        field.borderStyle = .none
        field.autocorrectionType = .no
        field.autocapitalizationType = .none
        field.smartQuotesType = .no
        field.smartDashesType = .no
        field.heightAnchor.constraint(greaterThanOrEqualToConstant: multiline ? 44 : 40).isActive = true
        field.accessibilityLabel = placeholder
        return field
    }

    public func addInput(_ input: UITextField, title: String) {
        (input as? SettingsTextField)?.inputAppearance = .underline
        if input.accessibilityLabel?.isEmpty != false {
            input.accessibilityLabel = title
        }
        if input.placeholder == title {
            input.placeholder = nil
        }
        let section = SettingsInputSection(title: title, input: input)
        addBodyView(section)
    }

    private func commit() {
        guard !didResolve, !hasActiveDialogTask(key: "submit") else { return }
        view.endEditing(true)
        setErrorMessage(nil)
        acceptButton.isEnabled = false
        guard let submit else {
            didResolve = true
            let onAccept = accepted
            accepted = nil
            cancelled = nil
            dismissSelf(animated: true) { onAccept?() }
            return
        }
        runDialogTask(
            key: "submit",
            onFinally: { [weak self] in
                guard let self else { return }
                self.acceptButton.isEnabled = true
                if self.cancelling {
                    self.cancelling = false
                    self.didResolve = true
                    let onCancel = self.cancelled
                    self.accepted = nil
                    self.cancelled = nil
                    self.dismissSelf(animated: true) { onCancel?() }
                }
            },
            onError: { [weak self] error in
                guard let self, !self.cancelling else { return }
                self.setErrorMessage(error.localizedDescription)
            }
        ) { [weak self] in
            if try await submit() {
                guard let self, !self.cancelling else { return }
                self.didResolve = true
                let onAccept = self.accepted
                self.accepted = nil
                self.cancelled = nil
                self.dismissSelf(animated: true) { onAccept?() }
            }
        }
    }

    open override func requestDismiss() {
        guard !didResolve else { return }
        if hasActiveDialogTask(key: "submit") {
            cancelling = true
            cancelButton.isEnabled = false
            cancelDialogTask(key: "submit")
            return
        }
        didResolve = true
        let onCancel = cancelled
        accepted = nil
        cancelled = nil
        dismissSelf(animated: true) { onCancel?() }
    }

    // MARK: - DebugDialogControllable

    open override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        [
            DebugDialogActionDescriptor(index: 0, label: "取消", kind: "cancel", enabled: cancelButton.isEnabled),
            DebugDialogActionDescriptor(index: 1, label: actionTitle, kind: "default", enabled: acceptButton.isEnabled),
        ]
    }

    open override func performDebugDialogAction(_ action: DebugDialogActionDescriptor) throws {
        if action.index == 0 { requestDismiss() }
        else { commit() }
    }
}
#endif

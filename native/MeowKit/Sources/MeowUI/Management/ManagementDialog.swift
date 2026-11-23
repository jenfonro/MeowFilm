#if os(iOS)
import UIKit
import MeowDebug

/// A cancellable native modal shared by address input and import confirmation.
/// Built directly on `FloatingCardDialogController` with unified `DebugDialogControllable` support.
@MainActor
public final class ManagementDialog: FloatingCardDialogController {
    public var body: UIStackView { bodyStack }
    public var submit: (() async throws -> Bool)?
    public var accepted: (() -> Void)?
    private let actionTitle: String
    private let acceptButton = UIButton(type: .system)
    private let cancelButton = UIButton(type: .system)
    private let buttonSpacer = UIView()
    private var cancelling = false

    public override var dismissesOnBackdropTap: Bool { false }
    public override var showsCloseButton: Bool { false }
    public override var showsHeaderDivider: Bool { true }
    public override var showsFooter: Bool { true }
    public override var usesCompactHeader: Bool { true }
    public override var cardCornerRadius: CGFloat { 20 }

    public init(title: String, action: String = "确定", themeProvider: any PageThemeProviding) {
        self.actionTitle = action
        super.init(title: title, themeProvider: themeProvider)
    }

    public override func viewDidLoad() {
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

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = footerStack.bounds.width
        guard width > 0 else { return }
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ||
            cancelButton.intrinsicContentSize.width + acceptButton.intrinsicContentSize.width + footerStack.spacing > width
        buttonSpacer.isHidden = vertical
        footerStack.axis = vertical ? .vertical : .horizontal
        footerStack.alignment = vertical ? .fill : .center
    }

    public override func applyTypography() {
        super.applyTypography()
        let buttonFont = UIFontMetrics(forTextStyle: .subheadline)
            .scaledFont(for: .systemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        cancelButton.titleLabel?.font = buttonFont
        acceptButton.titleLabel?.font = buttonFont
        view.setNeedsLayout()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        cancelButton.setTitleColor(UIColor(settings.muted), for: .normal)
        acceptButton.setTitleColor(UIColor(settings.accent), for: .normal)
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
        let section = SettingsInputSection(title: title, input: input)
        addBodyView(section)
    }

    public func showError(_ message: String) {
        setErrorMessage(message)
    }

    private func commit() {
        guard !hasActiveDialogTask(key: "submit") else { return }
        view.endEditing(true)
        setErrorMessage(nil)
        acceptButton.isEnabled = false
        guard let submit else { dismiss(animated: true) { self.accepted?() }; return }
        runDialogTask(
            key: "submit",
            onFinally: { [weak self] in
                guard let self else { return }
                self.acceptButton.isEnabled = true
                if self.cancelling {
                    self.cancelling = false
                    self.dismiss(animated: true)
                }
            },
            onError: { [weak self] error in
                guard let self, !self.cancelling else { return }
                self.showError(error.localizedDescription)
            }
        ) { [weak self] in
            if try await submit() {
                guard let self, !self.cancelling else { return }
                self.dismiss(animated: true) { self.accepted?() }
            }
        }
    }

    public override func requestDismiss() {
        if hasActiveDialogTask(key: "submit") {
            cancelling = true
            cancelButton.isEnabled = false
            cancelDialogTask(key: "submit")
            return
        }
        dismiss(animated: true)
    }

    // MARK: - DebugDialogControllable

    public override var debugDialogTypeName: String { "ManagementDialog" }

    public override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        [
            DebugDialogActionDescriptor(index: 0, label: "取消", kind: "cancel", enabled: cancelButton.isEnabled),
            DebugDialogActionDescriptor(index: 1, label: actionTitle, kind: "default", enabled: acceptButton.isEnabled),
        ]
    }

    public override func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String {
        if let kind {
            if kind == "confirm" || kind == "default" {
                commit()
                return actionTitle
            }
            if kind == "cancel" {
                requestDismiss()
                return "取消"
            }
        }
        if let index {
            switch index {
            case 0:
                requestDismiss()
                return "取消"
            case 1:
                commit()
                return actionTitle
            default:
                throw DebugAPIError.invalidArgument("ManagementDialog button index out of range: \(index)")
            }
        }
        if let label {
            if label == "取消" {
                requestDismiss()
                return "取消"
            }
            if label == actionTitle {
                commit()
                return actionTitle
            }
            throw DebugAPIError.notFound("ManagementDialog button not found: \(label)")
        }
        commit()
        return actionTitle
    }
}
#endif

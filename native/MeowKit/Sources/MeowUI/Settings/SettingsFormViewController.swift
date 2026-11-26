#if os(iOS)
import UIKit

/// Unified pure UIKit form controller backed by `ManagementFormView` with
/// optional sticky header, keyboard-aware focus reveal, and explicit
/// `PageThemeable` dispatch (zero recursive `subviews` theme crawling).
@MainActor
open class SettingsFormViewController: PageViewController {
    public let form = ManagementFormView()
    open var formHeader: UIView? { nil }
    open var formFooter: UIView? { nil }
    open var usesFormCardSurface: Bool { true }
    private let headerHost = UIView()
    private var formViewportSize = CGSize.zero
    private var needsFocusedFieldReveal = false
    private weak var activeField: UIView?
    private var formThemeables = ThemeableRegistry()
    private var mutedLabels = WeakLabelRegistry()
    private var focusObservers: [NSObjectProtocol] = []

    deinit {
        for observer in focusObservers { NotificationCenter.default.removeObserver(observer) }
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        form.translatesAutoresizingMaskIntoConstraints = false
        form.keyboardDismissMode = .interactive
        view.addSubview(form)
        var constraints = [
            form.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            form.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
        ]
        if let footer = formFooter {
            footer.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview(footer)
            constraints += [
                form.bottomAnchor.constraint(equalTo: footer.topAnchor),
                footer.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
                footer.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
                footer.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            ]
        } else {
            constraints.append(form.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor))
        }
        if let header = formHeader {
            headerHost.translatesAutoresizingMaskIntoConstraints = false
            header.translatesAutoresizingMaskIntoConstraints = false
            view.addSubview(headerHost)
            headerHost.addSubview(header)
            constraints += [
                headerHost.topAnchor.constraint(equalTo: contentTopAnchor),
                headerHost.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
                headerHost.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
                header.topAnchor.constraint(equalTo: headerHost.topAnchor, constant: 12),
                header.bottomAnchor.constraint(equalTo: headerHost.bottomAnchor, constant: -12),
                header.leadingAnchor.constraint(equalTo: headerHost.leadingAnchor, constant: 16),
                header.trailingAnchor.constraint(equalTo: headerHost.trailingAnchor, constant: -16),
                form.topAnchor.constraint(equalTo: headerHost.bottomAnchor),
            ]
        } else {
            constraints.append(form.topAnchor.constraint(equalTo: contentTopAnchor))
        }
        NSLayoutConstraint.activate(constraints)
        let center = NotificationCenter.default
        focusObservers = [
            center.addObserver(forName: UITextView.textDidBeginEditingNotification, object: nil, queue: .main) { [weak self] note in
                MainActor.assumeIsolated {
                    guard let self, let field = note.object as? UIView, field.isDescendant(of: self.form) else { return }
                    self.activeField = field
                    self.needsFocusedFieldReveal = true
                    self.view.setNeedsLayout()
                }
            },
            center.addObserver(forName: UITextView.textDidEndEditingNotification, object: nil, queue: .main) { [weak self] note in
                MainActor.assumeIsolated {
                    guard let self, let field = note.object as? UIView, self.activeField === field else { return }
                    self.activeField = nil
                }
            },
        ]
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: SettingsFormViewController, _: UITraitCollection) in
            page.setNeedsThemeUpdate()
        }
    }

    public func registerThemeable(_ item: any PageThemeable) {
        formThemeables.register(item)
        if isViewLoaded { item.applyTheme(theme) }
    }

    public func registerMutedLabel(_ label: UILabel) {
        mutedLabels.register(label)
        if isViewLoaded { label.textColor = UIColor(theme.settings.muted) }
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        form.backgroundColor = .clear
        form.applyTheme(theme)
        if !usesFormCardSurface {
            form.fields.backgroundColor = .clear
            form.fields.layer.cornerRadius = 0
            form.fields.clipsToBounds = false
        }
        formThemeables.apply(theme)
        mutedLabels.applyColor(UIColor(theme.settings.muted))
        needsFocusedFieldReveal = true
        view.setNeedsLayout()
    }

    public func revealFocusedField(_ field: UIView, inset: CGFloat = 8) {
        view.layoutIfNeeded()
        form.scrollRectToVisible(field.convert(field.bounds, to: form).insetBy(dx: 0, dy: -inset), animated: false)
    }

    open override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let changed = formViewportSize != form.bounds.size
        formViewportSize = form.bounds.size
        guard needsFocusedFieldReveal || changed else { return }
        needsFocusedFieldReveal = false
        if let field = activeField, field.isFirstResponder {
            form.scrollRectToVisible(field.convert(field.bounds, to: form).insetBy(dx: 0, dy: -8), animated: false)
        }
    }

    public func addTextField(
        _ title: String,
        id: String,
        value: String = "",
        in container: UIStackView? = nil,
        changed: @escaping (String) -> Void = { _ in }
    ) -> UITextField {
        let field = form.text(title, value: value, id: id, theme: theme, in: container, changed: changed) as! UITextField
        field.addAction(UIAction { [weak self, weak field] _ in
            self?.activeField = field
            self?.needsFocusedFieldReveal = true
            self?.view.setNeedsLayout()
        }, for: .editingDidBegin)
        field.addAction(UIAction { [weak self, weak field] _ in
            if self?.activeField === field { self?.activeField = nil }
        }, for: .editingDidEnd)
        return field
    }
}
#endif

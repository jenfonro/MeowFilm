#if os(iOS)
import UIKit

/// Unified pure UIKit form controller backed by `ManagementFormView` with
/// optional sticky header, keyboard-aware focus reveal, and explicit
/// `PageThemeable` dispatch (zero recursive `subviews` theme crawling).
@MainActor
open class SettingsFormViewController: PageViewController {
    private final class WeakThemeable {
        weak var value: (any PageThemeable)?
        init(_ value: any PageThemeable) { self.value = value }
    }

    private final class WeakLabel {
        weak var value: UILabel?
        init(_ value: UILabel) { self.value = value }
    }

    public let form = ManagementFormView()
    open var formHeader: UIView? { nil }
    private let headerHost = UIView()
    private var formViewportSize = CGSize.zero
    private var needsFocusedFieldReveal = false
    private weak var activeField: UIView?
    private var formThemeables: [WeakThemeable] = []
    private var mutedLabels: [WeakLabel] = []

    open override func viewDidLoad() {
        super.viewDidLoad()
        form.translatesAutoresizingMaskIntoConstraints = false
        form.keyboardDismissMode = .interactive
        view.addSubview(form)
        var constraints = [
            form.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            form.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            form.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
        ]
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
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: SettingsFormViewController, _: UITraitCollection) in
            page.setNeedsThemeUpdate()
        }
    }

    public func registerThemeable(_ item: any PageThemeable) {
        if !formThemeables.contains(where: { $0.value === item }) {
            formThemeables.append(WeakThemeable(item))
        }
        if isViewLoaded { item.applyTheme(theme) }
    }

    public func registerMutedLabel(_ label: UILabel) {
        mutedLabels.append(WeakLabel(label))
        if isViewLoaded { label.textColor = UIColor(theme.settings.muted) }
    }

    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        form.backgroundColor = .clear
        form.applyTheme(theme)
        formThemeables.removeAll { $0.value == nil }
        for entry in formThemeables { entry.value?.applyTheme(theme) }
        let muted = UIColor(theme.settings.muted)
        mutedLabels.removeAll { $0.value == nil }
        for entry in mutedLabels { entry.value?.textColor = muted }
        needsFocusedFieldReveal = true
        view.setNeedsLayout()
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

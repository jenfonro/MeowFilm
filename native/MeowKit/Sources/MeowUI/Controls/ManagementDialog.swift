#if os(iOS)
import UIKit
import Combine

/// A cancellable native modal shared by address input and import confirmation.
@MainActor
public final class ManagementDialog: UIViewController {
    public let body = UIStackView()
    public var submit: (() async throws -> Bool)?
    public var accepted: (() -> Void)?
    private let heading: String, action: String
    private let themeProvider: any PageThemeProviding
    private let panel = UIView(), headingLabel = UILabel(), line = UIView()
    private let error = UILabel(), accept = UIButton(type: .system), cancel = UIButton(type: .system)
    private let scroll = UIScrollView(), buttons = UIStackView(), buttonSpacer = UIView()
    private var themeObservation: AnyCancellable?
    private var task: Task<Void, Never>?
    private var cancelling = false
    public init(title: String, action: String = "确定", themeProvider: any PageThemeProviding) {
        heading = title; self.action = action; self.themeProvider = themeProvider
        super.init(nibName: nil, bundle: nil); modalPresentationStyle = .overFullScreen; modalTransitionStyle = .crossDissolve
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .clear
        let scrim = UIView(); scrim.backgroundColor = UIColor(PageTheme.scrim); scrim.accessibilityIdentifier = "management.dialog.scrim"
        view.addSubview(scrim); scrim.pinEdges(to: view)
        panel.accessibilityIdentifier = "management.dialog.panel"; panel.layer.cornerRadius = 20; panel.clipsToBounds = true; panel.layer.borderWidth = SettingsVisualMetrics.cardBorderWidth
        headingLabel.text = heading; headingLabel.font = UIFontMetrics(forTextStyle: .headline).scaledFont(for: .systemFont(ofSize: 17), compatibleWith: traitCollection)
        headingLabel.adjustsFontForContentSizeCategory = true; headingLabel.numberOfLines = 0; headingLabel.lineBreakMode = .byWordWrapping
        headingLabel.setContentCompressionResistancePriority(.required, for: .vertical)
        let header = UIView(); header.addSubview(headingLabel); headingLabel.translatesAutoresizingMaskIntoConstraints = false
        header.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([header.heightAnchor.constraint(greaterThanOrEqualToConstant: 52), headingLabel.leadingAnchor.constraint(equalTo: header.leadingAnchor, constant: 18), headingLabel.trailingAnchor.constraint(equalTo: header.trailingAnchor, constant: -18), headingLabel.topAnchor.constraint(equalTo: header.topAnchor, constant: 14), headingLabel.bottomAnchor.constraint(equalTo: header.bottomAnchor, constant: -14), line.leadingAnchor.constraint(equalTo: header.leadingAnchor), line.trailingAnchor.constraint(equalTo: header.trailingAnchor), line.bottomAnchor.constraint(equalTo: header.bottomAnchor), line.heightAnchor.constraint(equalToConstant: 0.5)])
        error.accessibilityIdentifier = "management.dialog.error"
        error.font = .preferredFont(forTextStyle: .footnote, compatibleWith: traitCollection)
        error.adjustsFontForContentSizeCategory = true; error.numberOfLines = 0; error.lineBreakMode = .byWordWrapping; error.isHidden = true
        error.setContentCompressionResistancePriority(.init(999), for: .vertical)
        body.axis = .vertical; body.spacing = 12
        body.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
        accept.setTitle(action, for: .normal); accept.accessibilityIdentifier = "management.dialog.confirm"
        accept.addAction(UIAction { [weak self] _ in self?.commit() }, for: .touchUpInside)
        cancel.setTitle("取消", for: .normal); cancel.accessibilityIdentifier = "management.dialog.cancel"
        cancel.addAction(UIAction { [weak self] _ in self?.close() }, for: .touchUpInside)
        for button in [cancel, accept] {
            button.titleLabel?.font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
            button.titleLabel?.adjustsFontForContentSizeCategory = true; button.titleLabel?.numberOfLines = 0
            button.titleLabel?.textAlignment = .center; button.titleLabel?.lineBreakMode = .byWordWrapping
            button.setContentCompressionResistancePriority(.required, for: .vertical)
            button.layer.cornerRadius = 12; button.widthAnchor.constraint(greaterThanOrEqualToConstant: 66).isActive = true
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
            button.contentEdgeInsets = .init(top: 8, left: 14, bottom: 8, right: 14)
        }
        for item in [buttonSpacer, cancel, accept] { buttons.addArrangedSubview(item) }
        buttons.spacing = 10; buttons.alignment = .center
        let contents = UIStackView(arrangedSubviews: [body, error, buttons]); contents.axis = .vertical; contents.spacing = 14; contents.isLayoutMarginsRelativeArrangement = true; contents.layoutMargins = .init(top: 20, left: 20, bottom: 20, right: 20)
        let stack = UIStackView(arrangedSubviews: [header, contents]); stack.axis = .vertical
        scroll.accessibilityIdentifier = "management.dialog.scroll"; scroll.contentInsetAdjustmentBehavior = .never
        view.addSubview(panel); panel.addSubview(scroll); scroll.pinEdges(to: panel); scroll.addSubview(stack)
        for item in [panel, stack] { item.translatesAutoresizingMaskIntoConstraints = false }
        let area = UILayoutGuide(); view.addLayoutGuide(area)
        let maxWidth = panel.widthAnchor.constraint(equalToConstant: 606); maxWidth.priority = .defaultHigh
        let contentHeight = scroll.heightAnchor.constraint(equalTo: stack.heightAnchor); contentHeight.priority = .init(998)
        NSLayoutConstraint.activate([area.leadingAnchor.constraint(equalTo: view.leadingAnchor), area.trailingAnchor.constraint(equalTo: view.trailingAnchor), area.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor), area.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            panel.centerXAnchor.constraint(equalTo: view.centerXAnchor), panel.centerYAnchor.constraint(equalTo: area.centerYAnchor), maxWidth,
            panel.widthAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.widthAnchor, constant: -20), panel.topAnchor.constraint(greaterThanOrEqualTo: view.safeAreaLayoutGuide.topAnchor, constant: 8), panel.bottomAnchor.constraint(lessThanOrEqualTo: view.keyboardLayoutGuide.topAnchor, constant: -8),
            stack.leadingAnchor.constraint(equalTo: scroll.contentLayoutGuide.leadingAnchor), stack.trailingAnchor.constraint(equalTo: scroll.contentLayoutGuide.trailingAnchor), stack.topAnchor.constraint(equalTo: scroll.contentLayoutGuide.topAnchor), stack.bottomAnchor.constraint(equalTo: scroll.contentLayoutGuide.bottomAnchor), stack.widthAnchor.constraint(equalTo: scroll.frameLayoutGuide.widthAnchor), contentHeight,
            scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 40)])
        themeObservation = themeProvider.pageThemeChanges.receive(on: RunLoop.main).sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) { (dialog: ManagementDialog, _: UITraitCollection) in dialog.applyTheme() }
        applyTheme()
    }
    public override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated); applyTheme()
    }
    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ||
            cancel.intrinsicContentSize.width + accept.intrinsicContentSize.width + buttons.spacing > buttons.bounds.width
        let axis: NSLayoutConstraint.Axis = vertical ? .vertical : .horizontal
        if buttons.axis != axis {
            buttonSpacer.isHidden = vertical; buttons.axis = axis; buttons.alignment = vertical ? .fill : .center
        }
    }
    private func applyTheme() {
        let theme = PageTheme(colors: themeProvider.pagePalette, dark: traitCollection.userInterfaceStyle == .dark), settings = theme.settings
        view.tintColor = UIColor(settings.accent)
        panel.backgroundColor = UIColor(settings.surface); panel.layer.borderColor = UIColor(settings.border).cgColor
        headingLabel.textColor = UIColor(settings.ink); line.backgroundColor = UIColor(settings.separator); error.textColor = UIColor(settings.danger)
        accept.backgroundColor = UIColor(settings.accent); accept.setTitleColor(UIColor(settings.onAccent), for: .normal)
        cancel.backgroundColor = UIColor(settings.input); cancel.setTitleColor(UIColor(settings.ink), for: .normal); cancel.setTitleColor(UIColor(settings.muted), for: .disabled)
        applyTheme(theme, to: body)
    }
    private func applyTheme(_ theme: PageTheme, to content: UIView) {
        if let preview = content as? ManagementImportView { preview.applyTheme(theme); return }
        if let form = content as? ManagementFormView { form.applyTheme(theme); return }
        if let label = content as? UILabel {
            if !label.adjustsFontForContentSizeCategory {
                label.font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(for: label.font, compatibleWith: traitCollection)
                label.adjustsFontForContentSizeCategory = true
            }
            label.numberOfLines = 0; label.lineBreakMode = .byWordWrapping
            label.setContentCompressionResistancePriority(.required, for: .vertical)
            return
        }
        if let input = content as? UITextField { theme.settings.apply(to: input); input.layer.cornerRadius = 12; return }
        if let input = content as? UITextView { theme.settings.apply(to: input); input.layer.cornerRadius = 12; return }
        for child in content.subviews { applyTheme(theme, to: child) }
    }
    public static func input(placeholder: String) -> UITextField {
        let input = UITextField(); input.placeholder = placeholder
        input.font = UIFontMetrics(forTextStyle: .body).scaledFont(for: .systemFont(ofSize: 14))
        input.adjustsFontForContentSizeCategory = true; input.setContentCompressionResistancePriority(.required, for: .vertical)
        input.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        input.backgroundColor = ManagementAppearance.input; input.layer.cornerRadius = 12
        input.textColor = ManagementAppearance.ink; input.tintColor = ManagementAppearance.accent
        input.attributedPlaceholder = NSAttributedString(string: placeholder, attributes: [.foregroundColor: ManagementAppearance.muted])
        input.leftView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); input.leftViewMode = .always
        input.rightView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); input.rightViewMode = .always
        input.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true; return input
    }
    public override func viewWillDisappear(_ animated: Bool) { super.viewWillDisappear(animated); task?.cancel() }
    private func commit() {
        guard task == nil else { return }; view.endEditing(true); accept.isEnabled = false; error.isHidden = true
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let success = try await submit?() ?? true
                try Task.checkCancellation()
                if success { let completion = accepted; dismiss(animated: true, completion: completion) }
            } catch is CancellationError { }
            catch {
                if !cancelling {
                    self.error.text = error.localizedDescription; self.error.isHidden = false
                    view.layoutIfNeeded(); scroll.scrollRectToVisible(self.error.convert(self.error.bounds, to: scroll), animated: false)
                }
            }
            task = nil; accept.isEnabled = true
            if cancelling { dismiss(animated: true) }
        }
    }
    private func close() {
        guard let task else { dismiss(animated: true); return }
        cancelling = true; cancel.isEnabled = false; task.cancel()
    }
}

@MainActor
public final class ManagementImportView: UIView, UITableViewDataSource, UITableViewDelegate {
    public struct Item {
        public let title: String, detail: String, status: String
        public init(title: String, detail: String, status: String) { self.title = title; self.detail = detail; self.status = status }
    }
    public private(set) var selected: Set<Int>
    private let items: [Item], table = UITableView(frame: .zero, style: .plain), all = UIButton(type: .custom), count = UILabel(), line = UIView()
    private let header = UIStackView()
    private lazy var tableHeight = table.heightAnchor.constraint(equalToConstant: CGFloat(min(12, max(1, items.count))) * 58)
    private var rowWidth: CGFloat = 0
    private var currentTheme = PageTheme()
    public init(items: [Item], selected: Set<Int>? = nil) {
        self.items = items; self.selected = selected ?? Set(items.indices); super.init(frame: .zero)
        table.dataSource = self; table.delegate = self; table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 58; table.backgroundColor = .clear; table.separatorInset = .zero
        tableHeight.priority = .defaultHigh; tableHeight.isActive = true
        table.heightAnchor.constraint(greaterThanOrEqualToConstant: 58).isActive = true
        all.contentHorizontalAlignment = .leading; all.setTitle("   全选", for: .normal); all.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        all.titleLabel?.adjustsFontForContentSizeCategory = true; all.titleLabel?.numberOfLines = 0
        all.titleLabel?.lineBreakMode = .byWordWrapping; all.setContentCompressionResistancePriority(.required, for: .vertical)
        all.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        all.widthAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        all.addAction(UIAction { [weak self] _ in guard let self else { return }; self.selected = self.selected.count == items.count ? [] : Set(items.indices); render() }, for: .touchUpInside)
        count.adjustsFontForContentSizeCategory = true; count.numberOfLines = 0; count.lineBreakMode = .byWordWrapping
        count.setContentHuggingPriority(.defaultHigh, for: .horizontal); count.setContentCompressionResistancePriority(.required, for: .vertical)
        header.addArrangedSubview(all); header.addArrangedSubview(count)
        header.alignment = .center; header.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        line.heightAnchor.constraint(equalToConstant: 0.5).isActive = true
        let stack = UIStackView(arrangedSubviews: [header, line, table]); stack.axis = .vertical; stack.spacing = 6; addSubview(stack); stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor), stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor)])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (preview: ManagementImportView, _: UITraitCollection) in preview.applyTypography() }
        applyTypography(); applyTheme(currentTheme)
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    private func applyTypography() {
        all.titleLabel?.font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        count.font = .preferredFont(forTextStyle: .footnote, compatibleWith: traitCollection)
        tableHeight.constant = CGFloat(min(12, max(1, items.count))) * UIFontMetrics(forTextStyle: .subheadline).scaledValue(for: 58, compatibleWith: traitCollection)
        table.reloadData(); setNeedsLayout()
    }
    public override func layoutSubviews() {
        super.layoutSubviews()
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ||
            all.intrinsicContentSize.width + count.intrinsicContentSize.width > header.bounds.width
        let axis: NSLayoutConstraint.Axis = vertical ? .vertical : .horizontal
        if header.axis != axis { header.axis = axis; header.alignment = vertical ? .fill : .center }
        if rowWidth != table.bounds.width { rowWidth = table.bounds.width; table.reloadData() }
    }
    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        let settings = theme.settings
        tintColor = UIColor(settings.accent); all.setTitleColor(UIColor(settings.ink), for: .normal)
        count.textColor = UIColor(settings.muted); line.backgroundColor = UIColor(settings.separator); table.separatorColor = UIColor(settings.separator)
        render()
    }
    private func render() {
        let checked = selected.count == items.count && !items.isEmpty
        let settings = currentTheme.settings
        all.setImage(UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?.withTintColor(UIColor(checked ? settings.accent : settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        count.text = "\(selected.count)/\(items.count)"; table.reloadData()
    }
    private func toggle(_ index: Int) { if selected.contains(index) { selected.remove(index) } else { selected.insert(index) }; render() }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) { toggle(indexPath.row) }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { items.count }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let item = items[indexPath.row], cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        let settings = currentTheme.settings
        cell.backgroundColor = .clear; cell.selectionStyle = .none
        let check = UIButton(type: .custom); check.setImage(UIImage(systemName: selected.contains(indexPath.row) ? "checkmark.circle.fill" : "circle", withConfiguration: UIImage.SymbolConfiguration(pointSize: 20))?.withTintColor(UIColor(selected.contains(indexPath.row) ? settings.accent : settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        check.accessibilityLabel = "选择 " + item.title; check.widthAnchor.constraint(equalToConstant: 44).isActive = true; check.heightAnchor.constraint(equalToConstant: 44).isActive = true
        check.addAction(UIAction { [weak self] _ in self?.toggle(indexPath.row) }, for: .touchUpInside)
        let title = UILabel(); title.text = item.title; title.font = UIFontMetrics(forTextStyle: .subheadline).scaledFont(for: .systemFont(ofSize: 14), compatibleWith: tableView.traitCollection); title.textColor = UIColor(settings.ink)
        let detail = UILabel(); detail.text = item.detail; detail.font = .preferredFont(forTextStyle: .caption1, compatibleWith: tableView.traitCollection); detail.textColor = UIColor(settings.muted); detail.isHidden = item.detail.isEmpty
        let labels = UIStackView(arrangedSubviews: [title, detail]); labels.axis = .vertical; labels.spacing = 4; labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let status = UILabel(); status.text = item.status; status.font = .preferredFont(forTextStyle: .caption1, compatibleWith: tableView.traitCollection); status.textColor = UIColor(item.status == "已存在" || item.status == "已有" ? settings.warning : settings.accent)
        for label in [title, detail, status] {
            label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0; label.lineBreakMode = .byWordWrapping
            label.setContentCompressionResistancePriority(.required, for: .vertical)
        }
        status.setContentHuggingPriority(.defaultHigh, for: .horizontal)
        let badge = UIView(); badge.backgroundColor = status.textColor.withAlphaComponent(0.1); badge.layer.cornerRadius = 10; badge.addSubview(status); status.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([status.leadingAnchor.constraint(equalTo: badge.leadingAnchor, constant: 8), status.trailingAnchor.constraint(equalTo: badge.trailingAnchor, constant: -8), status.topAnchor.constraint(equalTo: badge.topAnchor, constant: 3), status.bottomAnchor.constraint(equalTo: badge.bottomAnchor, constant: -3)])
        let text = UIStackView(arrangedSubviews: [labels, badge]); text.spacing = 10
        let labelsWidth = max(title.intrinsicContentSize.width, detail.isHidden ? 0 : detail.intrinsicContentSize.width)
        let inlineWidth = 44 + 20 + labelsWidth + status.intrinsicContentSize.width + 16
        let vertical = tableView.traitCollection.preferredContentSizeCategory.isAccessibilityCategory || inlineWidth > tableView.bounds.width
        text.axis = vertical ? .vertical : .horizontal; text.alignment = vertical ? .fill : .center
        let row = UIStackView(arrangedSubviews: [check, text]); row.alignment = .center; row.spacing = 10; cell.contentView.addSubview(row); row.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([row.leadingAnchor.constraint(equalTo: cell.contentView.leadingAnchor), row.trailingAnchor.constraint(equalTo: cell.contentView.trailingAnchor), row.topAnchor.constraint(equalTo: cell.contentView.topAnchor, constant: 7), row.bottomAnchor.constraint(equalTo: cell.contentView.bottomAnchor, constant: -7)])
        return cell
    }
}
#endif

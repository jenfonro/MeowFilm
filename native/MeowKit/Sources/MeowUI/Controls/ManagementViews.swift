#if os(iOS)
import UIKit

// Static control factories resolve the same settings resources as themed pages.
@MainActor
enum ManagementAppearance {
    static let surface = color { $0.surface }
    static let input = color { $0.input }
    static let inputBorder = color { $0.inputBorder }
    static let line = color { $0.separator }
    static let ink = color { $0.ink }
    static let muted = color { $0.muted }
    static let accent = color { $0.accent }
    private static func color(_ value: @escaping @Sendable (SettingsTheme) -> PageColor) -> UIColor {
        UIColor { UIColor(value(SettingsTheme(dark: $0.userInterfaceStyle == .dark))) }
    }
}

/// Reader's management glyphs, drawn natively rather than substituted with unrelated symbols.
@MainActor
public enum ManagementIcon {
    public static let save: UIImage = image { context in
        context.move(to: .init(x: 5, y: 4)); for point in [CGPoint(x: 17, y: 4), .init(x: 19, y: 6), .init(x: 19, y: 20), .init(x: 5, y: 20), .init(x: 5, y: 4)] { context.addLine(to: point) }
        context.move(to: .init(x: 8, y: 4)); for point in [CGPoint(x: 8, y: 10), .init(x: 16, y: 10), .init(x: 16, y: 4)] { context.addLine(to: point) }
        context.move(to: .init(x: 8, y: 20)); for point in [CGPoint(x: 8, y: 14), .init(x: 16, y: 14), .init(x: 16, y: 20)] { context.addLine(to: point) }; context.strokePath()
    }
    public static let more: UIImage = image { context in
        for y in [5, 12, 19] { context.fillEllipse(in: .init(x: 10.6, y: CGFloat(y) - 1.4, width: 2.8, height: 2.8)) }
    }
    public static let group: UIImage = image { context in
        context.move(to: .init(x: 4, y: 5.5)); for point in [CGPoint(x: 10, y: 5.5), .init(x: 11.8, y: 7.5), .init(x: 20, y: 7.5), .init(x: 20, y: 20), .init(x: 4, y: 20), .init(x: 4, y: 5.5)] { context.addLine(to: point) }
        for (y, end) in [(12, 16), (16, 13)] { context.move(to: .init(x: 8, y: CGFloat(y))); context.addLine(to: .init(x: CGFloat(end), y: CGFloat(y))) }; context.strokePath()
    }
    public static let sort: UIImage = image { _ in
        ("AŽ" as NSString).draw(at: .init(x: 1, y: 2), withAttributes: [.font: UIFont.systemFont(ofSize: 17, weight: .semibold), .foregroundColor: UIColor.black])
    }
    private static func image(_ draw: (CGContext) -> Void) -> UIImage {
        UIGraphicsImageRenderer(size: .init(width: 20, height: 20)).image { renderer in
            let context = renderer.cgContext; context.scaleBy(x: 20 / 24, y: 20 / 24)
            context.setStrokeColor(UIColor.black.cgColor); context.setFillColor(UIColor.black.cgColor)
            context.setLineWidth(1.8); context.setLineCap(.round); context.setLineJoin(.round); draw(context)
        }.withRenderingMode(.alwaysTemplate)
    }
}

/// Management actions use the application's public page header.
@MainActor
open class ManagementPageViewController: PageViewController {
    public func setManagementActions(_ buttons: [UIButton]) {
        pageHeader.setTrailing(buttons)
        pageHeader.applyTheme(theme)
    }
}

/// The list chrome shared by native settings managers. Item policies stay in features.
@MainActor
public final class ManagementListView: UIView, UITableViewDataSource, UITableViewDelegate, UISearchBarDelegate {
    public struct Row {
        public let id: String, title: String, detail: String
        public let enabled: Bool, failed: Bool, controlsEnabled: Bool, toggleEnabled: Bool
        public let showsSelection: Bool, showsToggle: Bool
        public let edit: (() -> Void)?, open: (() -> Void)?, toggle: (Bool) -> Void
        public let menu: UIMenu
        public init(id: String, title: String, detail: String = "", enabled: Bool, failed: Bool = false,
                    controlsEnabled: Bool = true, toggleEnabled: Bool = true,
                    showsSelection: Bool = true, showsToggle: Bool = true,
                    edit: (() -> Void)? = nil, open: (() -> Void)? = nil,
                    toggle: @escaping (Bool) -> Void, menu: UIMenu) {
            self.id = id; self.title = title; self.detail = detail; self.enabled = enabled
            self.failed = failed; self.controlsEnabled = controlsEnabled
            self.toggleEnabled = toggleEnabled; self.showsSelection = showsSelection
            self.showsToggle = showsToggle
            self.edit = edit; self.open = open; self.toggle = toggle; self.menu = menu
        }
    }
    public let search = UISearchBar()
    public let tools = UIStackView()
    public let footerMore = UIButton(type: .system)
    public let deleteButton = UIButton(type: .system)
    public var onSearch: ((String) -> Void)?
    public var onDelete: (() -> Void)?
    public private(set) var selected: Set<String> = []
    public var onSelection: (() -> Void)?
    public var footerMoreNeedsSelection = true {
        didSet { updateCount() }
    }
    let table = UITableView(frame: .zero, style: .plain)
    private let toolbar = UIStackView()
    private let toolbarSpacer = UIView()
    private lazy var toolbarHeight = toolbar.heightAnchor.constraint(equalToConstant: 50)
    private let footer = UIStackView(), count = UIButton(type: .custom), inverse = UIButton(type: .system)
    private let batchActions = UIStackView()
    private lazy var footerHeight = footer.heightAnchor.constraint(equalToConstant: 52)
    private let empty = UILabel(), emptyContainer = UIView()
    private var rows: [Row] = [], total = 0
    private var currentTheme = PageTheme()
    public override init(frame: CGRect) {
        super.init(frame: frame)
        for child in [search, toolbarSpacer, tools] { toolbar.addArrangedSubview(child) }
        toolbarSpacer.isHidden = true
        toolbar.alignment = .center; toolbar.spacing = 8
        search.searchBarStyle = .minimal; search.delegate = self; search.placeholder = "搜索"
        search.searchTextField.font = .systemFont(ofSize: 14); search.searchTextField.layer.cornerRadius = 19; search.searchTextField.clipsToBounds = true
        search.setContentHuggingPriority(.defaultLow, for: .horizontal)
        tools.axis = .horizontal; tools.spacing = 8
        table.register(ManagementRowCell.self, forCellReuseIdentifier: "management.row"); table.dataSource = self; table.delegate = self; table.separatorStyle = .singleLine; table.layer.cornerRadius = 22
        table.clipsToBounds = true; table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 64
        table.keyboardDismissMode = .onDrag; table.accessibilityIdentifier = "management.list"; table.contentInset.bottom = 76
        empty.textAlignment = .center; empty.font = .systemFont(ofSize: 14); empty.numberOfLines = 0
        emptyContainer.addSubview(empty); empty.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([empty.leadingAnchor.constraint(equalTo: emptyContainer.leadingAnchor, constant: 16), empty.trailingAnchor.constraint(equalTo: emptyContainer.trailingAnchor, constant: -16), empty.topAnchor.constraint(equalTo: emptyContainer.topAnchor, constant: 80)])
        footer.axis = .horizontal; footer.alignment = .center; footer.spacing = 10
        footer.isLayoutMarginsRelativeArrangement = true; footer.layoutMargins = .init(top: 0, left: 14, bottom: 0, right: 12)
        footer.layer.cornerRadius = 26; footer.layer.shadowColor = UIColor.black.cgColor; footer.layer.shadowOpacity = 0.1; footer.layer.shadowRadius = 18; footer.layer.shadowOffset = .init(width: 0, height: 8)
        count.contentHorizontalAlignment = .leading
        count.titleLabel?.numberOfLines = 0
        count.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        count.addAction(UIAction { [weak self] _ in self?.selectAll() }, for: .touchUpInside)
        count.accessibilityIdentifier = "management.selectAll"
        inverse.setTitle("反选", for: .normal); inverse.titleLabel?.font = .systemFont(ofSize: 13)
        inverse.addAction(UIAction { [weak self] _ in guard let self else { return }; selected.formSymmetricDifference(selectableIDs); selectionChanged() }, for: .touchUpInside)
        inverse.accessibilityIdentifier = "management.invert"
        deleteButton.setTitle("删除", for: .normal); deleteButton.titleLabel?.font = .systemFont(ofSize: 13)
        deleteButton.addAction(UIAction { [weak self] _ in self?.onDelete?() }, for: .touchUpInside)
        deleteButton.accessibilityIdentifier = "management.delete"
        footerMore.setImage(ManagementIcon.more, for: .normal); footerMore.accessibilityLabel = "批量更多操作"
        footerMore.showsMenuAsPrimaryAction = true
        batchActions.axis = .horizontal; batchActions.alignment = .center; batchActions.spacing = 10
        for button in [inverse, deleteButton, footerMore] { batchActions.addArrangedSubview(button) }
        for child in [count, batchActions] { footer.addArrangedSubview(child) }
        for button in [inverse, deleteButton, footerMore] {
            button.backgroundColor = ManagementAppearance.input; button.layer.cornerRadius = 12
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 32).isActive = true
        }
        inverse.widthAnchor.constraint(greaterThanOrEqualToConstant: 60).isActive = true
        deleteButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 60).isActive = true
        footerMore.widthAnchor.constraint(equalToConstant: 34).isActive = true
        count.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        count.setContentHuggingPriority(.defaultLow, for: .horizontal)
        batchActions.setContentHuggingPriority(.required, for: .horizontal)
        for child in [toolbar, table, footer] { addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false }
        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16), toolbar.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24), toolbar.topAnchor.constraint(equalTo: topAnchor, constant: 4), toolbarHeight,
            table.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 10), table.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 24), table.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24), table.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -8),
            footer.leadingAnchor.constraint(equalTo: table.leadingAnchor, constant: 12), footer.trailingAnchor.constraint(equalTo: table.trailingAnchor, constant: -12), footer.bottomAnchor.constraint(equalTo: table.bottomAnchor, constant: -12), footerHeight])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (list: ManagementListView, _: UITraitCollection) in list.applyFonts() }
        applyFonts()
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public override func didMoveToWindow() { super.didMoveToWindow(); applyFonts() }
    public override func layoutSubviews() {
        super.layoutSubviews()
        let width = footer.bounds.width - footer.layoutMargins.left - footer.layoutMargins.right
        guard width > 0 else { return }
        let actions = batchActions.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize)
        let vertical = count.intrinsicContentSize.width + actions.width + 10 > width
        footer.axis = vertical ? .vertical : .horizontal
        footer.alignment = vertical ? .fill : .center
        let countWidth = vertical ? width : max(1, width - actions.width - 10)
        let titleHeight = count.titleLabel?.sizeThatFits(.init(width: max(1, countWidth - 24),
                                                             height: .greatestFiniteMagnitude)).height ?? 0
        let countHeight = max(44, ceil(titleHeight))
        let height = vertical ? countHeight + actions.height + 10 : max(52, max(countHeight, actions.height))
        if footerHeight.constant != height {
            footerHeight.constant = height
            table.contentInset.bottom = height + 24
        }
    }
    private func applyFonts() {
        search.searchTextField.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        search.searchTextField.adjustsFontForContentSizeCategory = true
        empty.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        empty.adjustsFontForContentSizeCategory = true
        for button in [count, inverse, deleteButton] {
            button.titleLabel?.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 13), compatibleWith: traitCollection)
            button.titleLabel?.adjustsFontForContentSizeCategory = true
        }
        toolbarHeight.constant = toolbar.isHidden ? 0 : UIFontMetrics.default.scaledValue(for: 50, compatibleWith: traitCollection)
        setNeedsLayout(); table.reloadData()
    }
    public func configureToolbar(showsSearch: Bool, buttons: [UIButton], accessories: [UIView] = []) {
        search.isHidden = !showsSearch
        tools.arrangedSubviews.forEach { $0.removeFromSuperview() }
        buttons.forEach { tools.addArrangedSubview($0) }
        accessories.forEach { tools.addArrangedSubview($0) }
        let hasTools = !buttons.isEmpty || !accessories.isEmpty
        toolbarSpacer.isHidden = showsSearch || !hasTools
        toolbar.isHidden = !showsSearch && !hasTools
        toolbarHeight.constant = toolbar.isHidden ? 0 : UIFontMetrics.default.scaledValue(for: 50, compatibleWith: traitCollection)
    }
    public func setSelection(_ selected: Set<String>) {
        guard self.selected != selected else { return }
        self.selected = selected
        updateCount(); table.reloadData()
    }
    public func setRows(_ rows: [Row], total: Int, validIDs: Set<String>, emptyMessage: String) {
        self.rows = rows; self.total = total; selected.formIntersection(validIDs)
        empty.text = emptyMessage; table.backgroundView = rows.isEmpty ? emptyContainer : nil
        table.reloadData(); updateCount()
    }
    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        let settings = theme.settings
        table.backgroundColor = UIColor(settings.surface); footer.backgroundColor = UIColor(settings.surface)
        table.separatorColor = UIColor(settings.separator)
        empty.textColor = UIColor(settings.muted)
        search.searchTextField.backgroundColor = UIColor(settings.input); search.searchTextField.textColor = UIColor(settings.ink)
        search.searchTextField.tintColor = UIColor(settings.accent)
        if let placeholder = search.searchTextField.placeholder {
            search.searchTextField.attributedPlaceholder = NSAttributedString(string: placeholder, attributes: [.foregroundColor: UIColor(settings.muted)])
        }
        tintColor = UIColor(settings.ink)
        count.setTitleColor(UIColor(settings.ink), for: .normal)
        for button in [inverse, deleteButton, footerMore] {
            button.backgroundColor = UIColor(settings.input)
            button.setTitleColor(UIColor(button === deleteButton ? settings.danger : settings.ink), for: .normal)
            button.setTitleColor(UIColor(settings.muted), for: .disabled)
        }
        for button in tools.arrangedSubviews.compactMap({ $0 as? UIButton }) { button.backgroundColor = UIColor(settings.surface) }
        updateCount(); table.reloadData()
    }
    private var selectableIDs: Set<String> { Set(rows.filter(\.showsSelection).map(\.id)) }
    private func selectAll() { let ids = selectableIDs; selected = !ids.isEmpty && ids.isSubset(of: selected) ? [] : ids; selectionChanged() }
    private func selectionChanged() { updateCount(); table.reloadData(); onSelection?() }
    private func updateCount() {
        let checked = !selectableIDs.isEmpty && selectableIDs.isSubset(of: selected)
        let settings = currentTheme.settings
        count.setImage(UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?.withTintColor(UIColor(checked ? settings.accent : settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        count.setTitle("  全选 (\(selected.count)/\(total))", for: .normal)
        deleteButton.isEnabled = !selected.isEmpty; footerMore.isEnabled = !footerMoreNeedsSelection || !selected.isEmpty; inverse.isEnabled = !selectableIDs.isEmpty
        setNeedsLayout()
    }
    public func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) { onSearch?(searchText) }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { rows.count }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        let row = rows[indexPath.row]
        if row.controlsEnabled { row.open?() }
    }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let row = rows[indexPath.row]
        let cell = tableView.dequeueReusableCell(withIdentifier: "management.row", for: indexPath) as! ManagementRowCell
        cell.configure(row, selected: selected.contains(row.id), theme: currentTheme) { [weak self] in
            guard let self else { return }
            if selected.contains(row.id) { selected.remove(row.id) } else { selected.insert(row.id) }
            selectionChanged()
        }
        return cell
    }
    public static func icon(_ name: String, label: String, action: (() -> Void)? = nil) -> UIButton {
        let button = UIButton(type: .system)
        let image: UIImage? = switch name { case "management.sort": ManagementIcon.sort; case "management.group": ManagementIcon.group; case "management.more": ManagementIcon.more; default: UIImage(systemName: name) }
        button.setImage(image, for: .normal); button.accessibilityLabel = label
        button.widthAnchor.constraint(equalToConstant: 38).isActive = true; button.heightAnchor.constraint(equalToConstant: 38).isActive = true
        button.layer.cornerRadius = 19; button.backgroundColor = ManagementAppearance.surface
        if let action { button.addAction(UIAction { _ in action() }, for: .touchUpInside) }; return button
    }
}

@MainActor
private final class ManagementRowCell: UITableViewCell {
    private let select = UIButton(type: .system), title = UILabel(), detail = UILabel(), enabled = UISwitch()
    private let toggleContainer = UIView()
    private let edit = ManagementListView.icon("square.and.pencil", label: "编辑")
    private let more = ManagementListView.icon("management.more", label: "更多")
    private var row: ManagementListView.Row?, selection: (() -> Void)?
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        selectionStyle = .none; backgroundColor = .clear
        select.widthAnchor.constraint(equalToConstant: 32).isActive = true; select.heightAnchor.constraint(equalToConstant: 44).isActive = true
        select.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        title.font = .systemFont(ofSize: 14, weight: .medium); title.numberOfLines = 2
        detail.font = .systemFont(ofSize: 11); detail.numberOfLines = 2
        let labels = UIStackView(arrangedSubviews: [title, detail]); labels.axis = .vertical; labels.spacing = 3
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        select.addAction(UIAction { [weak self] _ in self?.selection?() }, for: .touchUpInside)
        enabled.addAction(UIAction { [weak self] _ in guard let self else { return }; row?.toggle(enabled.isOn) }, for: .valueChanged)
        toggleContainer.widthAnchor.constraint(equalToConstant: 40).isActive = true; toggleContainer.heightAnchor.constraint(equalToConstant: 44).isActive = true
        toggleContainer.addSubview(enabled); enabled.translatesAutoresizingMaskIntoConstraints = false; enabled.transform = CGAffineTransform(scaleX: 0.7, y: 0.7)
        NSLayoutConstraint.activate([
            enabled.centerXAnchor.constraint(equalTo: toggleContainer.centerXAnchor),
            enabled.centerYAnchor.constraint(equalTo: toggleContainer.centerYAnchor),
        ])
        edit.backgroundColor = .clear; more.backgroundColor = .clear
        edit.addAction(UIAction { [weak self] _ in self?.row?.edit?() }, for: .touchUpInside)
        more.showsMenuAsPrimaryAction = true
        let line = UIStackView(arrangedSubviews: [select, labels, toggleContainer, edit, more])
        line.alignment = .center; line.spacing = 4
        contentView.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            line.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 10),
            line.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -10),
            line.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 7),
            line.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -7),
        ])
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    func configure(_ row: ManagementListView.Row, selected: Bool, theme: PageTheme, selection: @escaping () -> Void) {
        self.row = row; self.selection = selection; accessibilityIdentifier = "management.row.\(row.id)"
        let settings = theme.settings
        selectionStyle = row.open == nil || !row.controlsEnabled ? .none : .default
        tintColor = UIColor(settings.ink); enabled.onTintColor = UIColor(settings.accent)
        select.isHidden = !row.showsSelection
        select.setImage(UIImage(systemName: selected ? "checkmark.circle.fill" : "circle")?.withTintColor(UIColor(selected ? settings.accent : settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        select.isEnabled = row.controlsEnabled
        select.accessibilityLabel = "选择 \(row.title)"; select.accessibilityValue = selected ? "已选择" : "未选择"
        select.accessibilityIdentifier = "management.select.\(row.id)"
        title.text = row.title; title.textColor = UIColor(settings.ink)
        detail.text = row.detail; detail.textColor = UIColor(row.failed ? settings.danger : settings.muted); detail.isHidden = row.detail.isEmpty
        title.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14, weight: .medium), compatibleWith: traitCollection)
        detail.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 11), compatibleWith: traitCollection)
        title.adjustsFontForContentSizeCategory = true; detail.adjustsFontForContentSizeCategory = true
        title.numberOfLines = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ? 0 : 2
        detail.numberOfLines = title.numberOfLines
        toggleContainer.isHidden = !row.showsToggle
        enabled.isOn = row.enabled; enabled.isEnabled = row.controlsEnabled && row.toggleEnabled
        enabled.accessibilityLabel = "启用 \(row.title)"
        enabled.accessibilityIdentifier = "management.enabled.\(row.id)"
        edit.accessibilityLabel = "编辑 \(row.title)"; edit.accessibilityIdentifier = "management.edit.\(row.id)"
        edit.tintColor = UIColor(settings.accent); edit.isEnabled = row.controlsEnabled
        edit.isHidden = row.edit == nil
        more.accessibilityLabel = "\(row.title)更多操作"; more.accessibilityIdentifier = "management.more.\(row.id)"
        more.menu = row.menu; more.tintColor = UIColor(settings.accent); more.isEnabled = row.controlsEnabled
        more.isHidden = row.menu.children.isEmpty
    }
}

@MainActor
public final class ManagementFormView: UIScrollView {
    public let fields = UIStackView()
    public var boxedInputs = false {
        didSet { fields.spacing = boxedInputs ? 14 : 0; fields.isLayoutMarginsRelativeArrangement = boxedInputs; fields.layoutMargins = boxedInputs ? .init(top: 16, left: 16, bottom: 16, right: 16) : .zero; fields.layer.cornerRadius = boxedInputs ? 22 : 18 }
    }
    public override init(frame: CGRect) {
        super.init(frame: frame); keyboardDismissMode = .interactive; alwaysBounceVertical = true
        fields.axis = .vertical; fields.layer.cornerRadius = 18; fields.clipsToBounds = true
        addSubview(fields); fields.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([fields.topAnchor.constraint(equalTo: contentLayoutGuide.topAnchor, constant: 12), fields.bottomAnchor.constraint(equalTo: contentLayoutGuide.bottomAnchor, constant: -12), fields.leadingAnchor.constraint(equalTo: contentLayoutGuide.leadingAnchor, constant: 12), fields.trailingAnchor.constraint(equalTo: contentLayoutGuide.trailingAnchor, constant: -12), fields.widthAnchor.constraint(equalTo: frameLayoutGuide.widthAnchor, constant: -24)])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (form: ManagementFormView, _: UITraitCollection) in form.updateCheckRows() }
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public override func didMoveToWindow() { super.didMoveToWindow(); updateCheckRows() }
    public func clear() { fields.arrangedSubviews.forEach { $0.removeFromSuperview() } }
    @discardableResult
    public func text(_ label: String, value: String, rows: Int = 1, id: String,
                     theme: PageTheme? = nil, in container: UIStackView? = nil,
                     changed: @escaping (String) -> Void) -> UIView {
        let section = UIStackView(); section.axis = .vertical; section.spacing = boxedInputs ? 8 : 5
        section.isLayoutMarginsRelativeArrangement = true; section.layoutMargins = boxedInputs ? .zero : .init(top: 11, left: 14, bottom: 10, right: 14)
        let title = UILabel(); title.text = label; title.numberOfLines = 0
        title.textColor = theme.map { UIColor($0.settings.muted) } ?? ManagementAppearance.muted
        title.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 12), compatibleWith: traitCollection); title.adjustsFontForContentSizeCategory = true
        title.setContentCompressionResistancePriority(.required, for: .vertical); section.addArrangedSubview(title)
        let input: UIView
        if rows > 1 {
            let text = ManagementTextView(minimumHeight: boxedInputs ? max(92, CGFloat(rows) * 19.5 + 18) : max(72, CGFloat(rows) * 23)); text.text = value; text.changed = changed; text.delegate = text
            text.autocorrectionType = .no; text.autocapitalizationType = .none; text.smartQuotesType = .no; text.smartDashesType = .no
            text.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 17), compatibleWith: traitCollection); text.adjustsFontForContentSizeCategory = true; text.backgroundColor = .clear
            text.textContainerInset = boxedInputs ? .init(top: 9, left: 12, bottom: 9, right: 12) : .zero; text.textContainer.lineFragmentPadding = 0
            input = text
        } else {
            let text = UITextField(); text.text = value; text.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: boxedInputs ? 13 : 17), compatibleWith: traitCollection); text.adjustsFontForContentSizeCategory = true
            text.autocorrectionType = .no; text.autocapitalizationType = .none; text.smartQuotesType = .no; text.smartDashesType = .no
            text.heightAnchor.constraint(greaterThanOrEqualToConstant: boxedInputs ? 40 : 25).isActive = true
            text.setContentCompressionResistancePriority(.required, for: .vertical); text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            if boxedInputs { text.leftView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); text.leftViewMode = .always; text.rightView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); text.rightViewMode = .always }
            text.addAction(UIAction { [weak text] _ in changed(text?.text ?? "") }, for: .editingChanged); input = text
        }
        if let theme {
            if let field = input as? UITextField { field.textColor = UIColor(theme.settings.ink) }
            if let field = input as? UITextView { field.textColor = UIColor(theme.settings.ink) }
        }
        if boxedInputs {
            input.layer.cornerRadius = 12; input.layer.borderWidth = 1
            input.layer.borderColor = (theme.map { UIColor($0.settings.inputBorder) } ??
                ManagementAppearance.inputBorder).resolvedColor(with: traitCollection).cgColor
            input.backgroundColor = theme.map { UIColor($0.settings.input) } ?? ManagementAppearance.input
        }
        input.accessibilityIdentifier = id; input.accessibilityLabel = label; section.addArrangedSubview(input)
        if let container { addSection(section, to: container, theme: theme) }
        else { addSection(section) }
        return input
    }
    public func addSection(_ section: UIView) {
        fields.addArrangedSubview(section)
        if !boxedInputs { let separator = UIView(); separator.backgroundColor = ManagementAppearance.line; separator.heightAnchor.constraint(equalToConstant: 0.5).isActive = true; fields.addArrangedSubview(separator) }
        updateCheckRows()
    }
    private func addSection(_ section: UIView, to container: UIStackView, theme: PageTheme?) {
        if !container.arrangedSubviews.isEmpty, !boxedInputs {
            let separator = UIView()
            separator.backgroundColor = theme.map { UIColor($0.settings.separator) } ?? ManagementAppearance.line
            separator.heightAnchor.constraint(equalToConstant: 0.5).isActive = true
            container.addArrangedSubview(separator)
        }
        container.addArrangedSubview(section)
    }
    private func updateCheckRows() {
        for case let row as UIStackView in fields.arrangedSubviews where row.arrangedSubviews.contains(where: { $0 is ManagementCheckButton }) {
            let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
            row.axis = vertical ? .vertical : .horizontal
            row.arrangedSubviews.filter { !($0 is ManagementCheckButton) }.forEach { $0.isHidden = vertical }
        }
    }
    public static func check(_ title: String, value: Bool, round: Bool = false, changed: @escaping (Bool) -> Void) -> UIButton {
        // A custom button keeps iOS 26 from turning selected controls into filled title capsules.
        let button = ManagementCheckButton(round: round); button.isSelected = value; button.contentHorizontalAlignment = .leading
        button.setTitle("  " + title, for: .normal)
        button.setContentHuggingPriority(.defaultHigh, for: .horizontal); button.setContentCompressionResistancePriority(.defaultHigh, for: .horizontal)
        button.setContentCompressionResistancePriority(.required, for: .vertical)
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 32).isActive = true; button.accessibilityLabel = title; button.accessibilityValue = value ? "已勾选" : "未勾选"
        button.addAction(UIAction { [weak button] _ in guard let button else { return }; button.isSelected.toggle(); button.accessibilityValue = button.isSelected ? "已勾选" : "未勾选"; changed(button.isSelected) }, for: .touchUpInside)
        return button
    }
    public func applyTheme(_ theme: PageTheme) {
        let settings = theme.settings
        fields.backgroundColor = UIColor(settings.surface); tintColor = UIColor(settings.accent)
        for section in fields.arrangedSubviews {
            for input in (section as? UIStackView)?.arrangedSubviews ?? [] {
                (input as? UILabel)?.textColor = UIColor(settings.muted)
                if let text = input as? UITextField {
                    text.textColor = UIColor(settings.ink)
                    if boxedInputs { settings.apply(to: text); text.layer.cornerRadius = 12 }
                } else if let text = input as? UITextView {
                    text.textColor = UIColor(settings.ink)
                    if boxedInputs { settings.apply(to: text); text.layer.cornerRadius = 12 }
                }
            }
        }
    }
}

@MainActor
private final class ManagementCheckButton: UIButton {
    private let shape: String, size: CGFloat, fontSize: CGFloat
    init(round: Bool) {
        shape = round ? "circle" : "square"; size = round ? 20 : 18; fontSize = round ? 14 : 13
        super.init(frame: .zero)
        titleLabel?.numberOfLines = 0; titleLabel?.lineBreakMode = .byWordWrapping; titleLabel?.adjustsFontForContentSizeCategory = true
        registerForTraitChanges([UITraitUserInterfaceStyle.self, UITraitPreferredContentSizeCategory.self]) { (button: ManagementCheckButton, _: UITraitCollection) in button.applyTheme() }
        applyTheme()
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    override func didMoveToWindow() { super.didMoveToWindow(); applyTheme() }
    override var intrinsicContentSize: CGSize {
        let size = super.intrinsicContentSize
        guard let titleLabel, titleLabel.preferredMaxLayoutWidth > 0 else { return size }
        let titleSize = titleLabel.sizeThatFits(.init(width: titleLabel.preferredMaxLayoutWidth, height: .greatestFiniteMagnitude))
        return .init(width: size.width, height: max(size.height, ceil(titleSize.height)))
    }
    override func layoutSubviews() {
        let width = max(0, contentRect(forBounds: bounds).width - (currentImage?.size.width ?? 0))
        if titleLabel?.preferredMaxLayoutWidth != width {
            titleLabel?.preferredMaxLayoutWidth = width; invalidateIntrinsicContentSize()
        }
        super.layoutSubviews()
    }
    private func applyTheme() {
        let settings = SettingsTheme(dark: traitCollection.userInterfaceStyle == .dark)
        titleLabel?.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: fontSize), compatibleWith: traitCollection)
        let config = UIImage.SymbolConfiguration(pointSize: size, weight: .regular)
        setImage(UIImage(systemName: shape, withConfiguration: config)?.withTintColor(UIColor(settings.muted), renderingMode: .alwaysOriginal), for: .normal)
        setImage(UIImage(systemName: "checkmark.\(shape).fill", withConfiguration: config)?.withTintColor(UIColor(settings.accent), renderingMode: .alwaysOriginal), for: .selected)
        setTitleColor(UIColor(settings.ink), for: .normal); setTitleColor(UIColor(settings.ink), for: .selected)
        invalidateIntrinsicContentSize()
    }
}

@MainActor
private final class ManagementTextView: UITextView, UITextViewDelegate {
    var changed: ((String) -> Void)?
    private let minimumHeight: CGFloat
    private lazy var minimumHeightConstraint = heightAnchor.constraint(equalToConstant: minimumHeight)
    init(minimumHeight: CGFloat) {
        self.minimumHeight = minimumHeight
        super.init(frame: .zero, textContainer: nil)
        minimumHeightConstraint.isActive = true; updateHeight()
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (text: ManagementTextView, _: UITraitCollection) in text.updateHeight() }
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    override func didMoveToWindow() { super.didMoveToWindow(); updateHeight() }
    private func updateHeight() { minimumHeightConstraint.constant = UIFontMetrics.default.scaledValue(for: minimumHeight, compatibleWith: traitCollection) }
    func textViewDidChange(_ textView: UITextView) { changed?(textView.text) }
}
#endif

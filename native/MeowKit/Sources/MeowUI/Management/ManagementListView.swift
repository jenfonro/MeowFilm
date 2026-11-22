#if os(iOS)
import UIKit

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
        ("AŽ" as NSString).draw(at: .init(x: 1, y: 2), withAttributes: [.font: UIFont.systemFont(ofSize: 17, weight: .regular), .foregroundColor: UIColor.black])
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

/// The list chrome shared by native settings managers, backed by `UITableViewDiffableDataSource`
/// and unified `SettingsSwitch` (zero `table.reloadData()`).
@MainActor
public final class ManagementListView: UIView, UITableViewDelegate, UISearchBarDelegate, PageThemeable {
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
    private lazy var searchContainer = ManagementSearchContainer(search: search)
    public let tools = UIStackView()
    private var toolButtons: [UIButton] = []
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
    private var dataSource: UITableViewDiffableDataSource<Int, String>!
    private let toolbar = UIStackView()
    private let toolbarSpacer = UIView()
    private lazy var toolbarHeight = toolbar.heightAnchor.constraint(equalToConstant: 50)
    private lazy var listTop = table.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 10)
    private let footer = UIStackView(), count = UIButton(type: .custom), inverse = UIButton(type: .system)
    private let batchActions = UIStackView()
    private lazy var footerHeight = footer.heightAnchor.constraint(equalToConstant: 52)
    private let empty = UILabel(), emptyContainer = UIView()
    private var rows: [Row] = []
    private var rowsByID: [String: Row] = [:]
    private var total = 0
    private var currentTheme = PageTheme()

    public override init(frame: CGRect) {
        super.init(frame: frame)
        for child in [searchContainer, toolbarSpacer, tools] { toolbar.addArrangedSubview(child) }
        toolbarSpacer.isHidden = true
        toolbar.alignment = .top; toolbar.spacing = 8
        search.searchBarStyle = .minimal; search.delegate = self; search.placeholder = "搜索"
        search.searchTextField.font = .systemFont(ofSize: 14); search.searchTextField.clipsToBounds = true
        searchContainer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        tools.axis = .horizontal; tools.spacing = 8; tools.isHidden = true

        table.register(ManagementRowCell.self, forCellReuseIdentifier: ManagementRowCell.reuseIdentifier)
        table.delegate = self
        table.separatorStyle = .singleLine
        table.layer.cornerRadius = 22
        table.clipsToBounds = true
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = 64
        table.keyboardDismissMode = .onDrag
        table.accessibilityIdentifier = "management.list"
        table.contentInsetAdjustmentBehavior = .never

        dataSource = UITableViewDiffableDataSource<Int, String>(tableView: table) { [weak self] tableView, indexPath, rowID in
            guard let self,
                  let row = self.rowsByID[rowID],
                  let cell = tableView.dequeueReusableCell(
                      withIdentifier: ManagementRowCell.reuseIdentifier, for: indexPath
                  ) as? ManagementRowCell else {
                return UITableViewCell()
            }
            self.configureCell(cell, row: row)
            return cell
        }
        dataSource.defaultRowAnimation = .none

        empty.textAlignment = .center; empty.font = .systemFont(ofSize: 14); empty.numberOfLines = 0
        emptyContainer.addSubview(empty); empty.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            empty.leadingAnchor.constraint(equalTo: emptyContainer.leadingAnchor, constant: 24),
            empty.trailingAnchor.constraint(equalTo: emptyContainer.trailingAnchor, constant: -24),
            empty.centerYAnchor.constraint(equalTo: emptyContainer.centerYAnchor),
            empty.topAnchor.constraint(greaterThanOrEqualTo: emptyContainer.topAnchor, constant: 24),
            empty.bottomAnchor.constraint(lessThanOrEqualTo: emptyContainer.bottomAnchor, constant: -24),
        ])

        footer.axis = .horizontal; footer.alignment = .center; footer.spacing = 10
        footer.isLayoutMarginsRelativeArrangement = true; footer.layoutMargins = .init(top: 0, left: 14, bottom: 0, right: 12)
        footer.layer.cornerRadius = 26; footer.layer.shadowColor = UIColor.black.cgColor; footer.layer.shadowOpacity = 0.1; footer.layer.shadowRadius = 18; footer.layer.shadowOffset = .init(width: 0, height: 8)
        count.contentHorizontalAlignment = .leading
        count.titleLabel?.numberOfLines = 0
        count.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        count.addAction(UIAction { [weak self] _ in self?.selectAll() }, for: .touchUpInside)
        count.accessibilityIdentifier = "management.selectAll"
        inverse.setTitle("反选", for: .normal); inverse.titleLabel?.font = .systemFont(ofSize: 13)
        inverse.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.selected.formSymmetricDifference(self.selectableIDs)
            self.selectionChanged()
        }, for: .touchUpInside)
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
        let listBottom = table.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -8)
        listBottom.priority = .defaultHigh
        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            toolbar.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24),
            toolbar.topAnchor.constraint(equalTo: topAnchor, constant: SettingsVisualMetrics.contentTopSpacing),
            toolbarHeight,
            listTop,
            table.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 24),
            table.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24),
            listBottom,
            table.bottomAnchor.constraint(lessThanOrEqualTo: keyboardLayoutGuide.topAnchor, constant: -4),
            footer.leadingAnchor.constraint(equalTo: table.leadingAnchor, constant: 12),
            footer.trailingAnchor.constraint(equalTo: table.trailingAnchor, constant: -12),
            footer.bottomAnchor.constraint(equalTo: table.bottomAnchor, constant: -12),
            footerHeight,
        ])
        footer.isHidden = true
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (list: ManagementListView, _: UITraitCollection) in
            list.applyFonts()
        }
        applyFonts()
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        applyFonts()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        toolbar.layoutIfNeeded(); searchContainer.layoutIfNeeded()
        let searchHeight = searchContainer.isHidden ? 0 : searchContainer.intrinsicContentSize.height
        let toolsHeight = tools.isHidden ? 0 : tools.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).height
        let requiredToolbarHeight = toolbar.isHidden ? 0 :
            max(UIFontMetrics.default.scaledValue(for: 50, compatibleWith: traitCollection), max(searchHeight, toolsHeight))
        var needsSecondPass = !searchContainer.isHidden && searchContainer.bounds.height + 0.01 < searchHeight
        if abs(toolbarHeight.constant - requiredToolbarHeight) > 0.01 {
            toolbarHeight.constant = requiredToolbarHeight
            toolbar.setNeedsLayout()
            needsSecondPass = true
        }
        if footer.isHidden {
            if abs(table.contentInset.bottom) > 0.5 {
                table.contentInset.bottom = 0
                table.verticalScrollIndicatorInsets.bottom = 0
            }
            if needsSecondPass {
                super.layoutSubviews()
                toolbar.layoutIfNeeded(); searchContainer.layoutIfNeeded()
            }
            return
        }
        let width = footer.bounds.width - footer.layoutMargins.left - footer.layoutMargins.right
        guard width > 0 else {
            if needsSecondPass {
                super.layoutSubviews()
                toolbar.layoutIfNeeded(); searchContainer.layoutIfNeeded()
            }
            return
        }
        let actions = batchActions.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize)
        let vertical = count.intrinsicContentSize.width + actions.width + 10 > width
        footer.axis = vertical ? .vertical : .horizontal
        footer.alignment = vertical ? .fill : .center
        let countWidth = vertical ? width : max(1, width - actions.width - 10)
        let titleHeight = count.titleLabel?.sizeThatFits(.init(width: max(1, countWidth - 24),
                                                             height: .greatestFiniteMagnitude)).height ?? 0
        let countHeight = max(44, ceil(titleHeight) + 12)
        let requiredFooterHeight = vertical
            ? countHeight + max(32, actions.height) + footer.spacing + 12
            : max(52, countHeight + 8)
        if abs(footerHeight.constant - requiredFooterHeight) > 0.01 {
            footerHeight.constant = requiredFooterHeight
            needsSecondPass = true
        }
        let bottomInset = requiredFooterHeight + 24
        if abs(table.contentInset.bottom - bottomInset) > 0.5 {
            table.contentInset.bottom = bottomInset
            table.verticalScrollIndicatorInsets.bottom = bottomInset
        }
        if needsSecondPass {
            super.layoutSubviews()
            toolbar.layoutIfNeeded(); searchContainer.layoutIfNeeded()
        }
    }

    private func applyFonts() {
        search.searchTextField.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        search.searchTextField.adjustsFontForContentSizeCategory = true
        search.searchTextField.invalidateIntrinsicContentSize()
        search.invalidateIntrinsicContentSize()
        search.setNeedsLayout()
        searchContainer.invalidateIntrinsicContentSize(); searchContainer.setNeedsLayout()
        empty.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        empty.adjustsFontForContentSizeCategory = true
        for button in [count, inverse, deleteButton] {
            button.titleLabel?.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 13), compatibleWith: traitCollection)
            button.titleLabel?.adjustsFontForContentSizeCategory = true
        }
        toolbarHeight.constant = toolbar.isHidden ? 0 : UIFontMetrics.default.scaledValue(for: 50, compatibleWith: traitCollection)
        reconfigureVisibleRows()
        setNeedsLayout()
    }

    public func configureToolbar(showsSearch: Bool, buttons: [UIButton], accessories: [UIView] = []) {
        search.isHidden = !showsSearch
        searchContainer.isHidden = !showsSearch
        tools.arrangedSubviews.forEach { $0.removeFromSuperview() }
        toolButtons = buttons
        buttons.forEach { tools.addArrangedSubview($0) }
        accessories.forEach { tools.addArrangedSubview($0) }
        let hasTools = !buttons.isEmpty || !accessories.isEmpty
        tools.isHidden = !hasTools
        toolbarSpacer.isHidden = showsSearch || !hasTools
        toolbar.isHidden = !showsSearch && !hasTools
        toolbarHeight.constant = toolbar.isHidden ? 0 : UIFontMetrics.default.scaledValue(for: 50, compatibleWith: traitCollection)
        listTop.constant = toolbar.isHidden ? 0 : 10
    }

    public func setSelection(_ selected: Set<String>) {
        guard self.selected != selected else { return }
        self.selected = selected
        updateCount()
        reconfigureVisibleRows()
    }

    public func setRows(_ rows: [Row], total: Int, validIDs: Set<String>, emptyMessage: String) {
        let oldIDs = self.rows.map(\.id)
        self.rows = rows
        self.rowsByID = Dictionary(rows.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest })
        self.total = total
        selected.formIntersection(validIDs)
        empty.text = emptyMessage
        table.backgroundView = rows.isEmpty ? emptyContainer : nil

        let newIDs = rows.map(\.id)
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0])
        snapshot.appendItems(newIDs, toSection: 0)
        let surviving = newIDs.filter { Set(oldIDs).contains($0) }
        if !surviving.isEmpty {
            snapshot.reconfigureItems(surviving)
        }
        dataSource.apply(snapshot, animatingDifferences: false)
        updateCount()
    }

    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        let settings = theme.settings
        table.backgroundColor = UIColor(settings.surface); footer.backgroundColor = UIColor(settings.surface)
        table.layer.borderWidth = SettingsVisualMetrics.cardBorderWidth
        table.layer.borderColor = UIColor(settings.border).cgColor
        table.separatorColor = UIColor(settings.separator)
        empty.textColor = UIColor(settings.muted)
        settings.apply(to: search.searchTextField)
        tintColor = UIColor(settings.ink)
        count.setTitleColor(UIColor(settings.ink), for: .normal)
        for button in [inverse, deleteButton, footerMore] {
            button.backgroundColor = UIColor(settings.input)
            button.setTitleColor(UIColor(button === deleteButton ? settings.danger : settings.ink), for: .normal)
            button.setTitleColor(UIColor(settings.muted), for: .disabled)
        }
        for button in toolButtons {
            button.backgroundColor = UIColor(settings.surface)
        }
        updateCount()
        reconfigureVisibleRows()
    }

    private func configureCell(_ cell: ManagementRowCell, row: Row) {
        cell.configure(row, selected: selected.contains(row.id), theme: currentTheme) { [weak self] in
            guard let self else { return }
            if self.selected.contains(row.id) { self.selected.remove(row.id) }
            else { self.selected.insert(row.id) }
            self.selectionChanged()
        }
    }

    private func reconfigureVisibleRows() {
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let rowID = dataSource.itemIdentifier(for: indexPath),
                  let row = rowsByID[rowID],
                  let cell = table.cellForRow(at: indexPath) as? ManagementRowCell else { continue }
            configureCell(cell, row: row)
        }
    }

    private var selectableIDs: Set<String> { Set(rows.filter(\.showsSelection).map(\.id)) }
    private func selectAll() {
        let ids = selectableIDs
        selected = !ids.isEmpty && ids.isSubset(of: selected) ? [] : ids
        selectionChanged()
    }
    private func selectionChanged() {
        updateCount()
        reconfigureVisibleRows()
        onSelection?()
    }
    private func updateCount() {
        let showsFooter = total > 0 || !selectableIDs.isEmpty
        footer.isHidden = !showsFooter
        let checked = !selectableIDs.isEmpty && selectableIDs.isSubset(of: selected)
        let settings = currentTheme.settings
        count.setImage(
            UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?
                .withTintColor(UIColor(checked ? settings.accent : settings.muted), renderingMode: .alwaysOriginal),
            for: .normal
        )
        count.setTitle("  全选 (\(selected.count)/\(total))", for: .normal)
        deleteButton.isEnabled = !selected.isEmpty
        footerMore.isEnabled = !footerMoreNeedsSelection || !selected.isEmpty
        inverse.isEnabled = !selectableIDs.isEmpty
        setNeedsLayout()
    }

    public func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) { onSearch?(searchText) }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let rowID = dataSource.itemIdentifier(for: indexPath),
              let row = rowsByID[rowID] else { return }
        if row.controlsEnabled { row.open?() }
    }

    public static func icon(_ name: String, label: String, action: (() -> Void)? = nil) -> UIButton {
        let button = UIButton(type: .system)
        let image: UIImage? = switch name {
        case "management.sort": ManagementIcon.sort
        case "management.group": ManagementIcon.group
        case "management.more": ManagementIcon.more
        default: UIImage(systemName: name)
        }
        button.setImage(image, for: .normal); button.accessibilityLabel = label
        button.widthAnchor.constraint(equalToConstant: 38).isActive = true
        button.heightAnchor.constraint(equalToConstant: 38).isActive = true
        button.layer.cornerRadius = 19
        button.backgroundColor = ManagementAppearance.surface
        if let action { button.addAction(UIAction { _ in action() }, for: .touchUpInside) }
        return button
    }
}

@MainActor
private final class ManagementSearchContainer: UIView {
    private let search: UISearchBar
    init(search: UISearchBar) {
        self.search = search
        super.init(frame: .zero)
        addSubview(search)
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    override var intrinsicContentSize: CGSize {
        let size = search.intrinsicContentSize
        return CGSize(width: size.width, height: ceil(max(36, search.searchTextField.bounds.height)))
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        let previousHeight = intrinsicContentSize.height
        search.frame = CGRect(
            x: bounds.minX, y: bounds.minY, width: bounds.width,
            height: max(bounds.height, search.sizeThatFits(CGSize(width: bounds.width, height: 0)).height)
        )
        search.layoutIfNeeded()
        let field = search.searchTextField
        search.frame.origin.y = bounds.minY - field.convert(field.bounds, to: search).minY
        field.layer.cornerRadius = min(field.bounds.width, field.bounds.height) / 2
        if intrinsicContentSize.height != previousHeight { invalidateIntrinsicContentSize() }
    }
}

@MainActor
private final class ManagementRowCell: UITableViewCell {
    static let reuseIdentifier = "management.row"
    private let select = UIButton(type: .system)
    private let title = UILabel()
    private let detail = UILabel()
    private let toggleSwitch = SettingsSwitch()
    private let edit = ManagementListView.icon("square.and.pencil", label: "编辑")
    private let more = ManagementListView.icon("management.more", label: "更多")
    private var row: ManagementListView.Row?
    private var selection: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        selectionStyle = .none
        backgroundColor = .clear
        select.widthAnchor.constraint(equalToConstant: 32).isActive = true
        select.heightAnchor.constraint(equalToConstant: 44).isActive = true
        title.font = .systemFont(ofSize: 14); title.numberOfLines = 2
        detail.font = .systemFont(ofSize: 11); detail.numberOfLines = 2
        let labels = UIStackView(arrangedSubviews: [title, detail])
        labels.axis = .vertical; labels.spacing = 3
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        select.addAction(UIAction { [weak self] _ in self?.selection?() }, for: .touchUpInside)
        toggleSwitch.changed = { [weak self] isOn in self?.row?.toggle(isOn) }
        edit.backgroundColor = .clear; more.backgroundColor = .clear
        edit.addAction(UIAction { [weak self] _ in self?.row?.edit?() }, for: .touchUpInside)
        more.showsMenuAsPrimaryAction = true
        let line = UIStackView(arrangedSubviews: [select, labels, toggleSwitch, edit, more])
        line.alignment = .center; line.spacing = 4
        contentView.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            line.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 10),
            line.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -10),
            line.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 7),
            line.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -7),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func prepareForReuse() {
        super.prepareForReuse()
        row = nil
        selection = nil
    }

    func configure(_ row: ManagementListView.Row, selected: Bool, theme: PageTheme, selection: @escaping () -> Void) {
        self.row = row; self.selection = selection
        accessibilityIdentifier = "management.row.\(row.id)"
        let settings = theme.settings
        selectionStyle = row.open == nil || !row.controlsEnabled ? .none : .default
        tintColor = UIColor(settings.ink)
        toggleSwitch.applyTheme(theme)
        select.isHidden = !row.showsSelection
        select.setImage(
            UIImage(systemName: selected ? "checkmark.circle.fill" : "circle")?
                .withTintColor(UIColor(selected ? settings.accent : settings.muted), renderingMode: .alwaysOriginal),
            for: .normal
        )
        select.isEnabled = row.controlsEnabled
        select.accessibilityLabel = "选择 \(row.title)"; select.accessibilityValue = selected ? "已选择" : "未选择"
        select.accessibilityIdentifier = "management.select.\(row.id)"
        title.text = row.title; title.textColor = UIColor(settings.ink)
        detail.text = row.detail; detail.textColor = UIColor(row.failed ? settings.danger : settings.muted)
        detail.isHidden = row.detail.isEmpty
        title.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        detail.font = UIFontMetrics.default.scaledFont(for: .systemFont(ofSize: 11), compatibleWith: traitCollection)
        title.adjustsFontForContentSizeCategory = true; detail.adjustsFontForContentSizeCategory = true
        title.numberOfLines = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ? 0 : 2
        detail.numberOfLines = title.numberOfLines
        toggleSwitch.isHidden = !row.showsToggle
        toggleSwitch.control.isOn = row.enabled
        toggleSwitch.control.isEnabled = row.controlsEnabled && row.toggleEnabled
        toggleSwitch.control.accessibilityLabel = "启用 \(row.title)"
        toggleSwitch.control.accessibilityIdentifier = "management.enabled.\(row.id)"
        edit.accessibilityLabel = "编辑 \(row.title)"; edit.accessibilityIdentifier = "management.edit.\(row.id)"
        edit.tintColor = UIColor(settings.accent); edit.isEnabled = row.controlsEnabled
        edit.isHidden = row.edit == nil
        more.accessibilityLabel = "\(row.title)更多操作"; more.accessibilityIdentifier = "management.more.\(row.id)"
        more.menu = row.menu; more.tintColor = UIColor(settings.accent); more.isEnabled = row.controlsEnabled
        more.isHidden = row.menu.children.isEmpty
    }
}
#endif

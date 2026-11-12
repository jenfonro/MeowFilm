#if os(iOS)
import UIKit

// Reader management surfaces are neutral; the application's other pages keep their palette.
@MainActor
enum ManagementAppearance {
    static let background = UIColor { $0.userInterfaceStyle == .dark ? UIColor(red: 0.11, green: 0.11, blue: 0.12, alpha: 1) : UIColor(red: 0.965, green: 0.969, blue: 0.984, alpha: 1) }
    static let surface = UIColor.secondarySystemGroupedBackground
    static let input = UIColor { $0.userInterfaceStyle == .dark ? UIColor.white.withAlphaComponent(0.08) : UIColor(red: 118 / 255, green: 118 / 255, blue: 128 / 255, alpha: 0.08) }
    static let line = UIColor { $0.userInterfaceStyle == .dark ? UIColor.white.withAlphaComponent(0.16) : UIColor.black.withAlphaComponent(0.12) }
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

/// Native Reader-style page chrome, scoped to management screens rather than the app shell.
@MainActor
open class ManagementPageViewController: PageViewController {
    open override var headerContentHeight: CGFloat { 52 }
    open override func loadView() { view = UIView() }
    open override func viewDidLoad() {
        super.viewDidLoad()
        pageHeader.horizontalInset = 21
        let back = PageHeaderButton(image: UIImage(systemName: "chevron.left")!, accessibilityLabel: "返回") { [weak self] in
            (self?.navigationController as? PageNavigationController)?.requestBack()
        }
        back.accessibilityIdentifier = "page.header.back"; decorate(back); pageHeader.setLeading(back)
    }
    open override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.backgroundColor = ManagementAppearance.background; view.tintColor = .systemBlue
        pageHeader.background.isHidden = true
    }
    public func setManagementActions(_ buttons: [UIButton]) {
        let actions = UIStackView(arrangedSubviews: buttons); actions.spacing = 8; actions.alignment = .center
        buttons.forEach(decorate); pageHeader.setTrailing([actions])
    }
    private func decorate(_ button: UIButton) {
        let circle = UIView(); circle.backgroundColor = ManagementAppearance.surface; circle.layer.cornerRadius = 19; circle.isUserInteractionEnabled = false
        button.insertSubview(circle, at: 0); circle.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([circle.widthAnchor.constraint(equalToConstant: 38), circle.heightAnchor.constraint(equalToConstant: 38), circle.centerXAnchor.constraint(equalTo: button.centerXAnchor), circle.centerYAnchor.constraint(equalTo: button.centerYAnchor)])
    }
}

/// The list chrome shared by native settings managers. Item policies stay in features.
@MainActor
public final class ManagementListView: UIView, UITableViewDataSource, UISearchBarDelegate {
    public struct Row {
        public let id: String, title: String, detail: String
        public let enabled: Bool, failed: Bool
        public let edit: () -> Void, toggle: (Bool) -> Void
        public let menu: UIMenu
        public init(id: String, title: String, detail: String = "", enabled: Bool, failed: Bool = false,
                    edit: @escaping () -> Void, toggle: @escaping (Bool) -> Void, menu: UIMenu) {
            self.id = id; self.title = title; self.detail = detail; self.enabled = enabled
            self.failed = failed; self.edit = edit; self.toggle = toggle; self.menu = menu
        }
    }
    public let search = UISearchBar()
    public let tools = UIStackView()
    public let footerMore = UIButton(type: .system)
    public var onSearch: ((String) -> Void)?
    public var onDelete: (() -> Void)?
    public private(set) var selected: Set<String> = []
    public var onSelection: (() -> Void)?
    private let table = UITableView(frame: .zero, style: .plain)
    private let footer = UIStackView(), count = UIButton(type: .custom), inverse = UIButton(type: .system), remove = UIButton(type: .system)
    private let empty = UILabel(), emptyContainer = UIView()
    private var rows: [Row] = [], total = 0
    private var currentTheme = PageTheme()
    public override init(frame: CGRect) {
        super.init(frame: frame)
        let toolbar = UIStackView(arrangedSubviews: [search, tools]); toolbar.alignment = .center; toolbar.spacing = 8
        search.searchBarStyle = .minimal; search.delegate = self; search.placeholder = "搜索"
        search.searchTextField.font = .systemFont(ofSize: 14); search.searchTextField.layer.cornerRadius = 19; search.searchTextField.clipsToBounds = true
        search.setContentHuggingPriority(.defaultLow, for: .horizontal)
        tools.axis = .horizontal; tools.spacing = 8
        table.register(ManagementRowCell.self, forCellReuseIdentifier: "management.row"); table.dataSource = self; table.separatorStyle = .singleLine; table.layer.cornerRadius = 22
        table.clipsToBounds = true; table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 64
        table.keyboardDismissMode = .onDrag; table.accessibilityIdentifier = "management.list"; table.contentInset.bottom = 76
        empty.textAlignment = .center; empty.font = .systemFont(ofSize: 14); empty.numberOfLines = 0
        emptyContainer.addSubview(empty); empty.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([empty.leadingAnchor.constraint(equalTo: emptyContainer.leadingAnchor, constant: 16), empty.trailingAnchor.constraint(equalTo: emptyContainer.trailingAnchor, constant: -16), empty.topAnchor.constraint(equalTo: emptyContainer.topAnchor, constant: 80)])
        footer.axis = .horizontal; footer.alignment = .center; footer.spacing = 10
        footer.isLayoutMarginsRelativeArrangement = true; footer.layoutMargins = .init(top: 0, left: 14, bottom: 0, right: 12)
        footer.layer.cornerRadius = 26; footer.layer.shadowColor = UIColor.black.cgColor; footer.layer.shadowOpacity = 0.1; footer.layer.shadowRadius = 18; footer.layer.shadowOffset = .init(width: 0, height: 8)
        count.contentHorizontalAlignment = .leading; count.titleLabel?.font = .systemFont(ofSize: 13)
        count.setTitleColor(.label, for: .normal)
        count.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        count.addAction(UIAction { [weak self] _ in self?.selectAll() }, for: .touchUpInside)
        count.accessibilityIdentifier = "management.selectAll"
        inverse.setTitle("反选", for: .normal); inverse.titleLabel?.font = .systemFont(ofSize: 13)
        inverse.addAction(UIAction { [weak self] _ in guard let self else { return }; selected.formSymmetricDifference(Set(rows.map(\.id))); selectionChanged() }, for: .touchUpInside)
        inverse.accessibilityIdentifier = "management.invert"
        remove.setTitle("删除", for: .normal); remove.titleLabel?.font = .systemFont(ofSize: 13)
        remove.addAction(UIAction { [weak self] _ in self?.onDelete?() }, for: .touchUpInside)
        remove.accessibilityIdentifier = "management.delete"
        footerMore.setImage(ManagementIcon.more, for: .normal); footerMore.accessibilityLabel = "批量更多操作"
        footerMore.showsMenuAsPrimaryAction = true
        for button in [count, inverse, remove, footerMore] { footer.addArrangedSubview(button) }
        for button in [inverse, remove, footerMore] { button.backgroundColor = ManagementAppearance.input; button.layer.cornerRadius = 12; button.heightAnchor.constraint(equalToConstant: 32).isActive = true }
        inverse.widthAnchor.constraint(equalToConstant: 60).isActive = true; remove.widthAnchor.constraint(equalToConstant: 60).isActive = true; footerMore.widthAnchor.constraint(equalToConstant: 34).isActive = true
        count.heightAnchor.constraint(equalToConstant: 44).isActive = true; count.setContentHuggingPriority(.defaultLow, for: .horizontal)
        for child in [toolbar, table, footer] { addSubview(child); child.translatesAutoresizingMaskIntoConstraints = false }
        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16), toolbar.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24), toolbar.topAnchor.constraint(equalTo: topAnchor, constant: 4), toolbar.heightAnchor.constraint(equalToConstant: 50),
            table.topAnchor.constraint(equalTo: toolbar.bottomAnchor, constant: 10), table.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 24), table.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -24), table.bottomAnchor.constraint(equalTo: safeAreaLayoutGuide.bottomAnchor, constant: -8),
            footer.leadingAnchor.constraint(equalTo: table.leadingAnchor, constant: 12), footer.trailingAnchor.constraint(equalTo: table.trailingAnchor, constant: -12), footer.bottomAnchor.constraint(equalTo: table.bottomAnchor, constant: -12), footer.heightAnchor.constraint(equalToConstant: 52)])
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public func setRows(_ rows: [Row], total: Int, validIDs: Set<String>, emptyMessage: String) {
        self.rows = rows; self.total = total; selected.formIntersection(validIDs)
        empty.text = emptyMessage; table.backgroundView = rows.isEmpty ? emptyContainer : nil
        table.reloadData(); updateCount()
    }
    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme; table.backgroundColor = ManagementAppearance.surface; footer.backgroundColor = ManagementAppearance.surface
        search.searchTextField.backgroundColor = ManagementAppearance.input; table.separatorColor = ManagementAppearance.line; empty.textColor = .secondaryLabel
        tintColor = .label; table.reloadData()
    }
    private func selectAll() { let ids = Set(rows.map(\.id)); selected = !ids.isEmpty && ids.isSubset(of: selected) ? [] : ids; selectionChanged() }
    private func selectionChanged() { updateCount(); table.reloadData(); onSelection?() }
    private func updateCount() {
        let checked = !rows.isEmpty && Set(rows.map(\.id)).isSubset(of: selected)
        count.setImage(UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?.withTintColor(checked ? .systemBlue : .systemGray3, renderingMode: .alwaysOriginal), for: .normal)
        count.setTitle("  全选 (\(selected.count)/\(total))", for: .normal)
        remove.isEnabled = !selected.isEmpty; footerMore.isEnabled = !selected.isEmpty; inverse.isEnabled = !rows.isEmpty
    }
    public func searchBar(_ searchBar: UISearchBar, textDidChange searchText: String) { onSearch?(searchText) }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { rows.count }
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
    private let edit = ManagementListView.icon("square.and.pencil", label: "编辑"), more = ManagementListView.icon("management.more", label: "更多")
    private var row: ManagementListView.Row?, selection: (() -> Void)?
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        selectionStyle = .none; backgroundColor = .clear
        select.widthAnchor.constraint(equalToConstant: 32).isActive = true; select.heightAnchor.constraint(equalToConstant: 44).isActive = true; select.setPreferredSymbolConfiguration(.init(pointSize: 20), forImageIn: .normal)
        title.font = .systemFont(ofSize: 14, weight: .medium); title.numberOfLines = 2
        detail.font = .systemFont(ofSize: 11); detail.numberOfLines = 2
        let labels = UIStackView(arrangedSubviews: [title, detail]); labels.axis = .vertical; labels.spacing = 3
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        select.addAction(UIAction { [weak self] _ in self?.selection?() }, for: .touchUpInside)
        enabled.addAction(UIAction { [weak self] _ in guard let self else { return }; row?.toggle(enabled.isOn) }, for: .valueChanged)
        let toggle = UIView(); toggle.widthAnchor.constraint(equalToConstant: 40).isActive = true; toggle.heightAnchor.constraint(equalToConstant: 44).isActive = true
        toggle.addSubview(enabled); enabled.translatesAutoresizingMaskIntoConstraints = false; enabled.transform = CGAffineTransform(scaleX: 0.7, y: 0.7); enabled.onTintColor = .systemGreen
        NSLayoutConstraint.activate([enabled.centerXAnchor.constraint(equalTo: toggle.centerXAnchor), enabled.centerYAnchor.constraint(equalTo: toggle.centerYAnchor)])
        edit.backgroundColor = .clear; more.backgroundColor = .clear
        edit.addAction(UIAction { [weak self] _ in self?.row?.edit() }, for: .touchUpInside); more.showsMenuAsPrimaryAction = true
        let line = UIStackView(arrangedSubviews: [select, labels, toggle, edit, more]); line.alignment = .center; line.spacing = 4
        contentView.addSubview(line); line.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([line.leadingAnchor.constraint(equalTo: contentView.leadingAnchor, constant: 10), line.trailingAnchor.constraint(equalTo: contentView.trailingAnchor, constant: -10), line.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 7), line.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -7)])
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    func configure(_ row: ManagementListView.Row, selected: Bool, theme: PageTheme, selection: @escaping () -> Void) {
        self.row = row; self.selection = selection; accessibilityIdentifier = "management.row.\(row.id)"
        select.setImage(UIImage(systemName: selected ? "checkmark.circle.fill" : "circle")?.withTintColor(selected ? .systemBlue : .systemGray3, renderingMode: .alwaysOriginal), for: .normal)
        select.accessibilityLabel = "选择 \(row.title)"; select.accessibilityValue = selected ? "已选择" : "未选择"
        title.text = row.title; title.textColor = .label
        detail.text = row.detail; detail.textColor = row.failed ? .systemRed : .secondaryLabel; detail.isHidden = row.detail.isEmpty
        enabled.isOn = row.enabled; enabled.accessibilityLabel = "启用 \(row.title)"
        edit.accessibilityLabel = "编辑 \(row.title)"; more.accessibilityLabel = "\(row.title)更多操作"; more.menu = row.menu
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
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }
    public func clear() { fields.arrangedSubviews.forEach { $0.removeFromSuperview() } }
    @discardableResult
    public func text(_ label: String, value: String, rows: Int = 1, id: String, changed: @escaping (String) -> Void) -> UIView {
        let section = UIStackView(); section.axis = .vertical; section.spacing = boxedInputs ? 8 : 5
        section.isLayoutMarginsRelativeArrangement = true; section.layoutMargins = boxedInputs ? .zero : .init(top: 11, left: 14, bottom: 10, right: 14)
        let title = UILabel(); title.text = label; title.numberOfLines = 0; title.textColor = boxedInputs ? .secondaryLabel : .systemRed; title.font = .systemFont(ofSize: boxedInputs ? 13 : 12, weight: .regular); section.addArrangedSubview(title)
        let input: UIView
        if rows > 1 {
            let text = ManagementTextView(); text.text = value; text.changed = changed; text.delegate = text
            text.autocorrectionType = .no; text.autocapitalizationType = .none; text.smartQuotesType = .no; text.smartDashesType = .no
            text.font = .systemFont(ofSize: boxedInputs ? 13 : 17); text.backgroundColor = .clear
            text.textContainerInset = boxedInputs ? .init(top: 9, left: 12, bottom: 9, right: 12) : .zero; text.textContainer.lineFragmentPadding = 0
            text.heightAnchor.constraint(equalToConstant: boxedInputs ? max(92, CGFloat(rows) * 19.5 + 18) : max(72, CGFloat(rows) * 23)).isActive = true; input = text
        } else {
            let text = UITextField(); text.text = value; text.font = .systemFont(ofSize: boxedInputs ? 13 : 17)
            text.autocorrectionType = .no; text.autocapitalizationType = .none; text.smartQuotesType = .no; text.smartDashesType = .no
            text.heightAnchor.constraint(equalToConstant: boxedInputs ? 40 : 25).isActive = true
            if boxedInputs { text.leftView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); text.leftViewMode = .always; text.rightView = UIView(frame: .init(x: 0, y: 0, width: 12, height: 1)); text.rightViewMode = .always }
            text.addAction(UIAction { [weak text] _ in changed(text?.text ?? "") }, for: .editingChanged); input = text
        }
        if boxedInputs { input.layer.cornerRadius = 12; input.layer.borderWidth = 0.7; input.layer.borderColor = ManagementAppearance.line.resolvedColor(with: traitCollection).cgColor; input.backgroundColor = ManagementAppearance.input }
        input.accessibilityIdentifier = id; input.accessibilityLabel = label; section.addArrangedSubview(input)
        addSection(section); return input
    }
    public func addSection(_ section: UIView) {
        fields.addArrangedSubview(section)
        if !boxedInputs { let separator = UIView(); separator.backgroundColor = ManagementAppearance.line; separator.heightAnchor.constraint(equalToConstant: 0.5).isActive = true; fields.addArrangedSubview(separator) }
    }
    public static func check(_ title: String, value: Bool, round: Bool = false, changed: @escaping (Bool) -> Void) -> UIButton {
        // A custom button keeps iOS 26 from turning selected controls into filled title capsules.
        let button = UIButton(type: .custom); button.isSelected = value; button.contentHorizontalAlignment = .leading
        let shape = round ? "circle" : "square", size: CGFloat = round ? 20 : 18
        let config = UIImage.SymbolConfiguration(pointSize: size, weight: .regular)
        button.setImage(UIImage(systemName: shape, withConfiguration: config)?.withTintColor(.systemGray3, renderingMode: .alwaysOriginal), for: .normal)
        button.setImage(UIImage(systemName: "checkmark.\(shape).fill", withConfiguration: config)?.withTintColor(.systemBlue, renderingMode: .alwaysOriginal), for: .selected)
        button.setTitle("  " + title, for: .normal); button.setTitleColor(.label, for: .normal); button.setTitleColor(.label, for: .selected); button.titleLabel?.font = .systemFont(ofSize: round ? 14 : 13)
        button.setContentHuggingPriority(.required, for: .horizontal); button.setContentCompressionResistancePriority(.required, for: .horizontal)
        button.heightAnchor.constraint(greaterThanOrEqualToConstant: 32).isActive = true; button.accessibilityLabel = title; button.accessibilityValue = value ? "已勾选" : "未勾选"
        button.addAction(UIAction { [weak button] _ in guard let button else { return }; button.isSelected.toggle(); button.accessibilityValue = button.isSelected ? "已勾选" : "未勾选"; changed(button.isSelected) }, for: .touchUpInside)
        return button
    }
    public func applyTheme(_ theme: PageTheme) {
        fields.backgroundColor = ManagementAppearance.surface; tintColor = .systemBlue
        for section in fields.arrangedSubviews {
            for input in (section as? UIStackView)?.arrangedSubviews ?? [] {
                (input as? UITextField)?.textColor = .label; (input as? UITextView)?.textColor = .label
                if boxedInputs { input.layer.borderColor = ManagementAppearance.line.resolvedColor(with: traitCollection).cgColor }
            }
        }
    }
}

@MainActor
private final class ManagementTextView: UITextView, UITextViewDelegate {
    var changed: ((String) -> Void)?
    func textViewDidChange(_ textView: UITextView) { changed?(textView.text) }
}
#endif

#if os(iOS)
import UIKit

/// Reusable import preview list backed by `UITableViewDiffableDataSource` and reusable cells.
@MainActor
public final class ManagementImportView: UIView, UITableViewDelegate, PageThemeable {
    public struct Item {
        public let title: String, detail: String, status: String
        public init(title: String, detail: String, status: String) {
            self.title = title
            self.detail = detail
            self.status = status
        }
    }

    public private(set) var selected: Set<Int>
    private let items: [Item]
    private let table = UITableView(frame: .zero, style: .plain)
    private var dataSource: UITableViewDiffableDataSource<Int, Int>!
    private let allButton = UIButton(type: .custom)
    private let countLabel = UILabel()
    private let divider = SettingsDividerView()
    private let header = UIStackView()
    private var currentTheme = PageTheme()

    public init(items: [Item], selected: Set<Int>? = nil) {
        self.items = items
        self.selected = selected ?? Set(items.indices)
        super.init(frame: .zero)

        allButton.setTitle("  全选", for: .normal)
        allButton.contentHorizontalAlignment = .leading
        allButton.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        allButton.titleLabel?.numberOfLines = 0
        allButton.titleLabel?.adjustsFontForContentSizeCategory = true
        allButton.setContentCompressionResistancePriority(.required, for: .vertical)
        allButton.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.selected = self.selected.count == self.items.count ? [] : Set(self.items.indices)
            self.updateHeaderAndVisibleRows()
        }, for: .touchUpInside)

        countLabel.adjustsFontForContentSizeCategory = true
        countLabel.setContentHuggingPriority(.required, for: .horizontal)
        header.alignment = .center
        header.spacing = 8
        header.addArrangedSubview(allButton)
        header.addArrangedSubview(countLabel)

        table.register(ManagementImportCell.self, forCellReuseIdentifier: ManagementImportCell.reuseIdentifier)
        table.delegate = self
        table.backgroundColor = .clear
        table.separatorInset = .zero
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = 56
        table.heightAnchor.constraint(equalToConstant: min(320, max(120, CGFloat(items.count) * 58))).isActive = true

        dataSource = UITableViewDiffableDataSource<Int, Int>(tableView: table) { [weak self] tableView, indexPath, index in
            guard let self,
                  let cell = tableView.dequeueReusableCell(
                      withIdentifier: ManagementImportCell.reuseIdentifier, for: indexPath
                  ) as? ManagementImportCell else {
                return UITableViewCell()
            }
            cell.configure(
                item: self.items[index],
                isSelected: self.selected.contains(index),
                theme: self.currentTheme
            ) { [weak self] in
                self?.toggle(index)
            }
            return cell
        }
        dataSource.defaultRowAnimation = .none

        let stack = UIStackView(arrangedSubviews: [header, divider, table])
        stack.axis = .vertical
        stack.spacing = 8
        addSubview(stack)
        stack.pinEdges(to: self)

        var builder = DiffableSnapshotBuilder<Int, Int>()
        builder.appendSection(0, items: Array(items.indices))
        dataSource.apply(builder.build(), animatingDifferences: false)

        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) { (preview: ManagementImportView, _: UITraitCollection) in
            preview.applyTypography()
        }
        applyTypography()
        applyTheme(PageTheme())
    }

    @available(*, unavailable)
    public required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func applyTypography() {
        allButton.titleLabel?.font = UIFontMetrics(forTextStyle: .subheadline)
            .scaledFont(for: .systemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        countLabel.font = .preferredFont(forTextStyle: .footnote, compatibleWith: traitCollection)
        reconfigureVisibleRows()
        setNeedsLayout()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        guard header.bounds.width > 0 else { return }
        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ||
            allButton.intrinsicContentSize.width + countLabel.intrinsicContentSize.width > header.bounds.width
        let axis: NSLayoutConstraint.Axis = vertical ? .vertical : .horizontal
        if header.axis != axis {
            header.axis = axis
            header.alignment = vertical ? .fill : .center
        }
    }

    public func applyTheme(_ theme: PageTheme) {
        currentTheme = theme
        let settings = theme.settings
        tintColor = UIColor(settings.accent)
        allButton.setTitleColor(UIColor(settings.ink), for: .normal)
        countLabel.textColor = UIColor(settings.muted)
        divider.applyTheme(theme)
        table.separatorColor = UIColor(settings.separator)
        updateHeaderAndVisibleRows()
    }

    private func updateHeaderAndVisibleRows() {
        let checked = selected.count == items.count && !items.isEmpty
        let settings = currentTheme.settings
        allButton.setImage(
            UIImage(systemName: checked ? "checkmark.circle.fill" : "circle")?
                .withTintColor(UIColor(checked ? settings.accent : settings.muted), renderingMode: .alwaysOriginal),
            for: .normal
        )
        countLabel.text = "\(selected.count)/\(items.count)"
        reconfigureVisibleRows()
    }

    private func reconfigureVisibleRows() {
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard indexPath.row < items.count,
                  let cell = table.cellForRow(at: indexPath) as? ManagementImportCell else { continue }
            let index = indexPath.row
            cell.configure(
                item: items[index],
                isSelected: selected.contains(index),
                theme: currentTheme
            ) { [weak self] in
                self?.toggle(index)
            }
        }
    }

    private func toggle(_ index: Int) {
        guard items.indices.contains(index) else { return }
        if selected.contains(index) { selected.remove(index) }
        else { selected.insert(index) }
        updateHeaderAndVisibleRows()
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: false)
        guard let index = dataSource.itemIdentifier(for: indexPath) else { return }
        toggle(index)
    }
}

@MainActor
private final class ManagementImportCell: UITableViewCell {
    static let reuseIdentifier = "meow.management.importCell"

    private let checkButton = UIButton(type: .custom)
    private let titleLabel = UILabel()
    private let detailLabel = UILabel()
    private let badge = SettingsStatusBadge(status: .success, text: "", theme: PageTheme())
    private let textStack = UIStackView()
    private var onToggle: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none

        checkButton.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            checkButton.widthAnchor.constraint(equalToConstant: 44),
            checkButton.heightAnchor.constraint(equalToConstant: 44),
        ])
        checkButton.addAction(UIAction { [weak self] _ in self?.onToggle?() }, for: .touchUpInside)

        for label in [titleLabel, detailLabel] {
            label.adjustsFontForContentSizeCategory = true
            label.numberOfLines = 0
            label.lineBreakMode = .byWordWrapping
            label.setContentCompressionResistancePriority(.required, for: .vertical)
        }
        let labels = UIStackView(arrangedSubviews: [titleLabel, detailLabel])
        labels.axis = .vertical
        labels.spacing = 4
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        badge.setContentHuggingPriority(.required, for: .horizontal)
        badge.setContentCompressionResistancePriority(.required, for: .horizontal)

        textStack.spacing = 10
        textStack.alignment = .center
        textStack.addArrangedSubview(labels)
        textStack.addArrangedSubview(badge)

        let row = UIStackView(arrangedSubviews: [checkButton, textStack])
        row.alignment = .center
        row.spacing = 10
        row.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            row.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            row.topAnchor.constraint(equalTo: contentView.topAnchor, constant: 7),
            row.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -7),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        onToggle = nil
    }

    func configure(item: ManagementImportView.Item, isSelected: Bool, theme: PageTheme,
                   onToggle: @escaping () -> Void) {
        self.onToggle = onToggle
        let settings = theme.settings
        let symbol = isSelected ? "checkmark.circle.fill" : "circle"
        let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        checkButton.setImage(
            UIImage(systemName: symbol, withConfiguration: config)?
                .withTintColor(UIColor(isSelected ? settings.accent : settings.muted), renderingMode: .alwaysOriginal),
            for: .normal
        )
        checkButton.accessibilityLabel = "选择 " + item.title
        checkButton.accessibilityValue = isSelected ? "已选择" : "未选择"

        titleLabel.text = item.title
        titleLabel.font = UIFontMetrics(forTextStyle: .subheadline)
            .scaledFont(for: .systemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        titleLabel.textColor = UIColor(settings.ink)

        detailLabel.text = item.detail
        detailLabel.font = .preferredFont(forTextStyle: .caption1, compatibleWith: traitCollection)
        detailLabel.textColor = UIColor(settings.muted)
        detailLabel.isHidden = item.detail.isEmpty

        let status: SettingsStatusBadge.Status = (item.status == "已存在" || item.status == "已有") ? .warning : .success
        badge.update(status: status, text: item.status, theme: theme)

        let vertical = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        textStack.axis = vertical ? .vertical : .horizontal
        textStack.alignment = vertical ? .fill : .center
    }
}
#endif

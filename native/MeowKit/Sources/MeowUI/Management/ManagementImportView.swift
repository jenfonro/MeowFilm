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

        table.register(UITableViewCell.self, forCellReuseIdentifier: Self.cellReuseIdentifier)
        table.delegate = self
        table.backgroundColor = .clear
        table.separatorInset = .zero
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = 56
        table.heightAnchor.constraint(equalToConstant: min(320, max(120, CGFloat(items.count) * 58))).isActive = true

        dataSource = UITableViewDiffableDataSource<Int, Int>(tableView: table) { [weak self] tableView, indexPath, index in
            let cell = tableView.dequeueReusableCell(withIdentifier: Self.cellReuseIdentifier, for: indexPath)
            self?.configureCell(cell, at: index)
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

    private static let cellReuseIdentifier = "meow.management.importCell"

    private func reconfigureVisibleRows() {
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard indexPath.row < items.count,
                  let cell = table.cellForRow(at: indexPath) else { continue }
            configureCell(cell, at: indexPath.row)
        }
    }

    private func configureCell(_ cell: UITableViewCell, at index: Int) {
        let item = items[index]
        let isSelected = selected.contains(index)
        let settings = currentTheme.settings
        cell.backgroundColor = .clear
        cell.selectionStyle = .none
        var content = UIListContentConfiguration.subtitleCell()
        content.text = item.title
        content.textProperties.font = UIFontMetrics(forTextStyle: .subheadline)
            .scaledFont(for: .systemFont(ofSize: 14, weight: .regular), compatibleWith: traitCollection)
        content.textProperties.color = UIColor(settings.ink)
        content.secondaryText = item.detail.isEmpty ? nil : item.detail
        content.secondaryTextProperties.font = .preferredFont(forTextStyle: .caption1, compatibleWith: traitCollection)
        content.secondaryTextProperties.color = UIColor(settings.muted)
        content.secondaryTextProperties.numberOfLines = 0
        content.image = UIImage(
            systemName: isSelected ? "checkmark.circle.fill" : "circle",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        )?.withTintColor(UIColor(isSelected ? settings.accent : settings.muted), renderingMode: .alwaysOriginal)
        content.imageToTextPadding = 10
        content.directionalLayoutMargins = .init(top: 7, leading: 0, bottom: 7, trailing: 8)
        cell.contentConfiguration = content
        cell.accessibilityValue = isSelected ? "已选择" : "未选择"
        let status: SettingsStatusBadge.Status = (item.status == "已存在" || item.status == "已有") ? .warning : .success
        let badge = (cell.accessoryView as? SettingsStatusBadge)
            ?? SettingsStatusBadge(status: status, text: item.status, theme: currentTheme)
        badge.update(status: status, text: item.status, theme: currentTheme)
        badge.sizeToFit()
        cell.accessoryView = badge
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
#endif

#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class PanAccountsViewController: PageViewController, UITableViewDelegate {
    private enum RowID: Hashable, Sendable {
        case header
        case account(PanProviderID)
    }

    let model: PanAccountsModel
    let table = SettingsTableView()
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!
    private weak var loginCard: PanLoginViewController?
    private var displayedNotice: UUID?

    public init(model: PanAccountsModel, themeProvider: any PageThemeProviding) {
        self.model = model
        super.init(themeProvider: themeProvider)
        pageTitle = "网盘账号"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "pan.accounts"
        table.accessibilityIdentifier = "pan.accounts.table"
        table.delegate = self
        table.rowHeight = UITableView.automaticDimension
        table.estimatedRowHeight = 110
        table.register(PanAccountTableCell.self, forCellReuseIdentifier: PanAccountTableCell.reuseIdentifier)
        table.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(table)
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])

        dataSource = UITableViewDiffableDataSource<Int, RowID>(tableView: table) { [weak self] tableView, indexPath, rowID in
            let cell = tableView.dequeueReusableCell(
                withIdentifier: PanAccountTableCell.reuseIdentifier, for: indexPath
            ) as! PanAccountTableCell
            self?.configure(cell: cell, rowID: rowID, rowIndex: indexPath.row)
            return cell
        }
        dataSource.defaultRowAnimation = .none

        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: PanAccountsViewController, _: UITraitCollection) in page.reconfigureVisibleCells()
        }
        observe(model, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() { model.start(); showNotice() }
    public override func pageDidBecomeHidden() {
        if loginCard == nil { model.close() }
    }
    public override func pageDidBecomeInactive() { closeLogin(); model.close() }
    public override func pageDidFinishAppearing() { showNotice() }
    public override func applyTheme() {
        super.applyTheme()
        reconfigureVisibleCells()
    }

    private func render() {
        guard isViewLoaded, dataSource != nil else { return }
        let items: [RowID] = [.header] + model.rows.map { .account($0.id) }
        let existing = Set(dataSource.snapshot().itemIdentifiers)
        var snapshot = NSDiffableDataSourceSnapshot<Int, RowID>()
        snapshot.appendSections([0])
        snapshot.appendItems(items, toSection: 0)
        let toReconfigure = items.filter { existing.contains($0) }
        if !toReconfigure.isEmpty { snapshot.reconfigureItems(toReconfigure) }
        dataSource.apply(snapshot, animatingDifferences: false)
        showNotice()
    }

    private func reconfigureVisibleCells() {
        guard isViewLoaded, dataSource != nil else { return }
        for indexPath in table.indexPathsForVisibleRows ?? [] {
            guard let rowID = dataSource.itemIdentifier(for: indexPath),
                  let cell = table.cellForRow(at: indexPath) as? PanAccountTableCell else { continue }
            configure(cell: cell, rowID: rowID, rowIndex: indexPath.row)
        }
    }

    private func configure(cell: PanAccountTableCell, rowID: RowID, rowIndex: Int) {
        let total = model.rows.count + 1
        let first = rowIndex == 0
        let last = rowIndex == total - 1
        let accessibilityLayout = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        switch rowID {
        case .header:
            cell.configureHeader(theme: theme, first: first, last: last, accessibilityLayout: accessibilityLayout)
        case .account(let providerID):
            guard let row = model.rows.first(where: { $0.id == providerID }) else { return }
            cell.configureAccount(
                row: row,
                theme: theme,
                first: first,
                last: last,
                accessibilityLayout: accessibilityLayout
            ) { [weak self] action in
                self?.perform(action, provider: providerID)
            }
        }
    }

    private func showNotice() {
        guard let notice = model.notice, displayedNotice != notice.id else { return }
        displayedNotice = notice.id
        enqueueError(notice.message)
    }

    public func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        table.heightForHeader(in: section)
    }

    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        table.viewForHeader(in: section, title: nil, theme: theme)
    }

    func perform(_ action: PanAccountAction, provider: PanProviderID) {
        switch action {
        case .refresh: model.refresh(provider)
        case .logout: model.logout(provider)
        case .login:
            guard presentedViewController == nil, let login = model.openLogin(provider) else { return }
            let card = PanLoginViewController(model: login, themeProvider: themeProvider) { [weak self] in self?.closeLogin() }
            loginCard = card
            present(card, animated: true)
        }
    }

    private func closeLogin() {
        model.closeLogin()
        guard let card = loginCard else { return }
        loginCard = nil
        if card.presentingViewController != nil { dismissPageModal(card) }
    }
}

@MainActor
private final class PanAccountTableCell: UITableViewCell {
    static let reuseIdentifier = "pan.account.cell"

    private let background = SettingsRowBackgroundView(selected: false)
    private var columns: PanAccountColumns?
    private var isAccessibilityLayout = false

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        selectionStyle = .none
        backgroundColor = .clear
        backgroundView = background
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        clearColumns()
        accessibilityIdentifier = nil
    }

    private func ensureColumns(accessibilityLayout: Bool) -> PanAccountColumns {
        if let existing = columns, isAccessibilityLayout == accessibilityLayout {
            clearColumns()
            return existing
        }
        columns?.removeFromSuperview()
        let created = PanAccountColumns(accessibilityLayout: accessibilityLayout)
        created.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(created)
        NSLayoutConstraint.activate([
            created.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
            created.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
            created.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
            created.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
        ])
        columns = created
        isAccessibilityLayout = accessibilityLayout
        return created
    }

    private func clearColumns() {
        guard let columns else { return }
        for column in columns.columns {
            for child in column.arrangedSubviews {
                column.removeArrangedSubview(child)
                child.removeFromSuperview()
            }
        }
    }

    func configureHeader(theme: PageTheme, first: Bool, last: Bool, accessibilityLayout: Bool) {
        background.update(theme: theme, first: first, last: last, selected: false, settings: true, showsSeparator: !last)
        accessibilityIdentifier = "pan.accounts.header"
        let columns = ensureColumns(accessibilityLayout: accessibilityLayout)
        for (column, text) in zip(columns.columns, ["平台", "状态", "操作"]) {
            let label = UILabel()
            label.text = text
            label.font = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
            label.numberOfLines = 0
            label.lineBreakMode = .byWordWrapping
            label.adjustsFontForContentSizeCategory = true
            if column === columns.columns[2] { label.textAlignment = .center }
            label.textColor = UIColor(theme.settings.muted)
            column.addArrangedSubview(label)
        }
    }

    func configureAccount(
        row: PanAccountRow,
        theme: PageTheme,
        first: Bool,
        last: Bool,
        accessibilityLayout: Bool,
        onAction: @escaping @MainActor (PanAccountAction) -> Void
    ) {
        background.update(theme: theme, first: first, last: last, selected: false, settings: true, showsSeparator: !last)
        accessibilityIdentifier = "pan.accounts.row.\(row.id.rawValue)"
        let columns = ensureColumns(accessibilityLayout: accessibilityLayout)
        let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection)

        let icon = UIImageView(image: UIImage(systemName: "externaldrive"))
        icon.preferredSymbolConfiguration = UIImage.SymbolConfiguration(pointSize: 16, weight: .regular)
        icon.tintColor = UIColor(theme.settings.accent)
        icon.setContentHuggingPriority(.required, for: .horizontal)
        icon.setContentCompressionResistancePriority(.required, for: .horizontal)
        let title = UILabel()
        title.text = row.id.title
        title.font = font
        title.numberOfLines = 0
        title.lineBreakMode = .byWordWrapping
        title.adjustsFontForContentSizeCategory = true
        title.textColor = UIColor(theme.settings.ink)
        let platform = UIStackView(arrangedSubviews: [icon, title])
        platform.alignment = .center
        platform.spacing = 8
        platform.accessibilityIdentifier = "pan.accounts.platform"
        columns.columns[0].addArrangedSubview(platform)

        if row.busy {
            let spinner = UIActivityIndicatorView(style: .medium)
            spinner.color = UIColor(theme.settings.muted)
            spinner.accessibilityLabel = "正在操作账号"
            spinner.startAnimating()
            columns.columns[1].addArrangedSubview(spinner)
        } else {
            let status: SettingsStatusBadge.Status
            switch row.account?.status {
            case .signedIn: status = .success
            case .expired: status = .danger
            case .unverified: status = .warning
            case .signedOut: status = .neutral
            case nil: status = row.account?.errorCode == .corruptStorage ? .danger : .neutral
            }
            let badge = SettingsStatusBadge(status: status, text: row.statusTitle, theme: theme)
            badge.accessibilityIdentifier = "pan.accounts.status"
            columns.columns[1].alignment = accessibilityLayout ? .fill : .leading
            columns.columns[1].addArrangedSubview(badge)
        }

        for action in row.actions {
            let labelText: String, suffix: String
            switch action {
            case .login: labelText = row.account?.status == .expired ? "重新登录" : "登录"; suffix = "login"
            case .refresh: labelText = "刷新"; suffix = "refresh"
            case .logout: labelText = "退出登录"; suffix = "logout"
            }
            let foreground = UIColor(row.busy ? theme.settings.muted : theme.settings.accent)
            var configuration = UIButton.Configuration.plain()
            configuration.title = labelText
            configuration.baseForegroundColor = foreground
            configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
                var attributes = $0
                attributes.foregroundColor = foreground
                attributes.font = font
                return attributes
            }
            configuration.titleAlignment = .center
            configuration.titleLineBreakMode = .byWordWrapping
            configuration.contentInsets = .init(top: 6, leading: 0, bottom: 6, trailing: 0)
            let button = UIButton(configuration: configuration, primaryAction: UIAction { _ in
                onAction(action)
            })
            button.contentHorizontalAlignment = .center
            button.titleLabel?.font = font
            button.titleLabel?.numberOfLines = 0
            button.titleLabel?.textAlignment = .center
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            button.accessibilityIdentifier = "pan.accounts.\(row.id.rawValue).\(suffix)"
            button.isEnabled = !row.busy
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
            columns.columns[2].addArrangedSubview(button)
        }
    }
}

/// Stable columns at ordinary sizes; accessibility sizes use full-width content
/// areas so platform names and operations do not wrap one character at a time.
@MainActor
final class PanAccountColumns: UIStackView {
    let columns = (0..<3).map { _ in UIStackView() }
    init(accessibilityLayout: Bool) {
        super.init(frame: .zero)
        axis = accessibilityLayout ? .vertical : .horizontal
        alignment = accessibilityLayout ? .fill : .center; spacing = 8
        accessibilityIdentifier = "pan.accounts.columns"
        for column in columns { column.axis = .vertical; column.spacing = 0; addArrangedSubview(column) }
        if !accessibilityLayout {
            NSLayoutConstraint.activate([
                columns[0].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.38),
                columns[1].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.30),
            ])
        }
    }
    @available(*, unavailable)
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif

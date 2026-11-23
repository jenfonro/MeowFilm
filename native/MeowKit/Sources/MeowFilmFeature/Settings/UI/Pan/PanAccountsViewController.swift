#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class PanAccountsViewController: PageViewController, UITableViewDelegate {
    private enum RowID: Hashable, Sendable {
        case header
        case account(PanProviderID)
    }

    private enum RowItem: Equatable {
        case header(last: Bool)
        case account(PanAccountRow, last: Bool)

        var id: RowID {
            switch self {
            case .header: return .header
            case .account(let row, _): return .account(row.id)
            }
        }
    }

    let model: PanAccountsModel
    let table = SettingsTableView()
    private var projection = IndexedDiffableProjection<Int, RowID, RowItem>()
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!
    private weak var loginCard: PanLoginViewController?

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
    public override func applyTheme() {
        super.applyTheme()
        reconfigureVisibleCells()
    }

    private func render() {
        guard isViewLoaded, dataSource != nil else { return }
        let rows = model.rows
        var items: [RowItem] = [.header(last: rows.isEmpty)]
        items.reserveCapacity(rows.count + 1)
        for (index, row) in rows.enumerated() {
            items.append(.account(row, last: index == rows.count - 1))
        }
        let update = projection.update(section: 0, items: items, id: { $0.id })
        dataSource.applyIfNeeded(update, animatingDifferences: false)
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
        guard let item = projection[rowID] else { return }
        let first = rowIndex == 0
        let accessibilityLayout = traitCollection.preferredContentSizeCategory.isAccessibilityCategory
        switch item {
        case .header(let last):
            cell.configureHeader(theme: theme, first: first, last: last, accessibilityLayout: accessibilityLayout)
        case .account(let row, let last):
            cell.configureAccount(
                row: row,
                theme: theme,
                first: first,
                last: last,
                accessibilityLayout: accessibilityLayout
            ) { [weak self] action in
                self?.perform(action, provider: row.id)
            }
        }
    }

    private func showNotice() {
        syncErrorSource(
            "pan.notice",
            message: model.notice?.message,
            token: model.notice.map { AnyHashable($0.id) }
        )
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
            guard canPresentPageAlert, let login = model.openLogin(provider) else { return }
            let card = PanLoginViewController(model: login, themeProvider: themeProvider) { [weak self] in self?.closeLogin() }
            loginCard = card
            if !presentPageModal(card) {
                loginCard = nil
                model.closeLogin()
            }
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
    private let columns = PanAccountColumns()
    private let headerLabels: [UILabel]
    private let platformIcon = UIImageView(image: UIImage(systemName: "externaldrive"))
    private let platformTitle = UILabel()
    private let platformStack: UIStackView
    private let statusSpinner = UIActivityIndicatorView(style: .medium)
    private let statusBadge = SettingsStatusBadge(status: .neutral, text: "", theme: PageTheme())
    private var actionButtons: [UIButton] = []
    private var boundActions: [PanAccountAction] = []
    private var onAction: (@MainActor (PanAccountAction) -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        headerLabels = ["平台", "状态", "操作"].enumerated().map { index, text in
            let label = UILabel()
            label.text = text
            label.numberOfLines = 0
            label.lineBreakMode = .byWordWrapping
            label.adjustsFontForContentSizeCategory = true
            if index == 2 { label.textAlignment = .center }
            label.isHidden = true
            return label
        }
        platformStack = UIStackView(arrangedSubviews: [platformIcon, platformTitle])
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        selectionStyle = .none
        backgroundColor = .clear
        backgroundView = background

        columns.translatesAutoresizingMaskIntoConstraints = false
        contentView.addSubview(columns)
        NSLayoutConstraint.activate([
            columns.leadingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.leadingAnchor),
            columns.trailingAnchor.constraint(equalTo: contentView.layoutMarginsGuide.trailingAnchor),
            columns.topAnchor.constraint(equalTo: contentView.layoutMarginsGuide.topAnchor),
            columns.bottomAnchor.constraint(equalTo: contentView.layoutMarginsGuide.bottomAnchor),
        ])

        for (column, label) in zip(columns.columns, headerLabels) {
            column.addArrangedSubview(label)
        }

        platformIcon.preferredSymbolConfiguration = UIImage.SymbolConfiguration(pointSize: 16, weight: .regular)
        platformIcon.setContentHuggingPriority(.required, for: .horizontal)
        platformIcon.setContentCompressionResistancePriority(.required, for: .horizontal)
        platformTitle.numberOfLines = 0
        platformTitle.lineBreakMode = .byWordWrapping
        platformTitle.adjustsFontForContentSizeCategory = true
        platformStack.alignment = .center
        platformStack.spacing = 8
        platformStack.accessibilityIdentifier = "pan.accounts.platform"
        platformStack.isHidden = true
        columns.columns[0].addArrangedSubview(platformStack)

        statusSpinner.hidesWhenStopped = true
        statusSpinner.accessibilityLabel = "正在操作账号"
        statusSpinner.isHidden = true
        statusBadge.accessibilityIdentifier = "pan.accounts.status"
        statusBadge.isHidden = true
        columns.columns[1].addArrangedSubview(statusSpinner)
        columns.columns[1].addArrangedSubview(statusBadge)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func prepareForReuse() {
        super.prepareForReuse()
        statusSpinner.stopAnimating()
        onAction = nil
        boundActions.removeAll()
        accessibilityIdentifier = nil
    }

    private func invokeAction(at index: Int) {
        guard boundActions.indices.contains(index) else { return }
        onAction?(boundActions[index])
    }

    private func ensureActionButtons(count: Int) {
        while actionButtons.count < count {
            let index = actionButtons.count
            let button = UIButton(configuration: .plain(), primaryAction: UIAction { [weak self] _ in
                self?.invokeAction(at: index)
            })
            button.contentHorizontalAlignment = .center
            button.titleLabel?.numberOfLines = 0
            button.titleLabel?.textAlignment = .center
            button.titleLabel?.adjustsFontForContentSizeCategory = true
            button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
            button.isHidden = true
            columns.columns[2].addArrangedSubview(button)
            actionButtons.append(button)
        }
    }

    func configureHeader(theme: PageTheme, first: Bool, last: Bool, accessibilityLayout: Bool) {
        background.update(theme: theme, first: first, last: last, selected: false, settings: true, showsSeparator: !last)
        accessibilityIdentifier = "pan.accounts.header"
        columns.setAccessibilityLayout(accessibilityLayout)
        columns.columns[1].alignment = .fill
        onAction = nil
        boundActions.removeAll()

        let headerFont = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
        let mutedColor = UIColor(theme.settings.muted)
        for label in headerLabels {
            label.font = headerFont
            label.textColor = mutedColor
            label.isHidden = false
        }
        platformStack.isHidden = true
        statusSpinner.stopAnimating()
        statusSpinner.isHidden = true
        statusBadge.isHidden = true
        for button in actionButtons {
            button.isHidden = true
            button.accessibilityIdentifier = nil
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
        columns.setAccessibilityLayout(accessibilityLayout)
        self.onAction = onAction
        boundActions = row.actions

        for label in headerLabels { label.isHidden = true }

        let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        platformIcon.tintColor = UIColor(theme.settings.accent)
        platformTitle.text = row.id.title
        platformTitle.font = font
        platformTitle.textColor = UIColor(theme.settings.ink)
        platformStack.isHidden = false

        if row.busy {
            columns.columns[1].alignment = .fill
            statusBadge.isHidden = true
            statusSpinner.color = UIColor(theme.settings.muted)
            statusSpinner.isHidden = false
            if !statusSpinner.isAnimating { statusSpinner.startAnimating() }
        } else {
            statusSpinner.stopAnimating()
            statusSpinner.isHidden = true
            let status: SettingsStatusBadge.Status
            switch row.account?.status {
            case .signedIn: status = .success
            case .expired: status = .danger
            case .unverified: status = .warning
            case .signedOut: status = .neutral
            case nil: status = row.account?.errorCode == .corruptStorage ? .danger : .neutral
            }
            columns.columns[1].alignment = accessibilityLayout ? .fill : .leading
            statusBadge.update(status: status, text: row.statusTitle, theme: theme)
            statusBadge.isHidden = false
        }

        ensureActionButtons(count: row.actions.count)
        let foreground = UIColor(row.busy ? theme.settings.muted : theme.settings.accent)
        for (index, button) in actionButtons.enumerated() {
            guard row.actions.indices.contains(index) else {
                button.isHidden = true
                button.accessibilityIdentifier = nil
                continue
            }
            let action = row.actions[index]
            let labelText: String, suffix: String
            switch action {
            case .login: labelText = row.account?.status == .expired ? "重新登录" : "登录"; suffix = "login"
            case .refresh: labelText = "刷新"; suffix = "refresh"
            case .logout: labelText = "退出登录"; suffix = "logout"
            }
            var configuration = button.configuration ?? UIButton.Configuration.plain()
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
            button.configuration = configuration
            button.titleLabel?.font = font
            button.accessibilityIdentifier = "pan.accounts.\(row.id.rawValue).\(suffix)"
            button.isEnabled = !row.busy
            button.isHidden = false
        }
    }
}

/// Stable columns at ordinary sizes; accessibility sizes use full-width content
/// areas so platform names and operations do not wrap one character at a time.
@MainActor
final class PanAccountColumns: UIStackView {
    let columns = (0..<3).map { _ in UIStackView() }
    private var widthConstraints: [NSLayoutConstraint] = []
    private var isAccessibilityLayout: Bool?

    init(accessibilityLayout: Bool = false) {
        super.init(frame: .zero)
        spacing = 8
        accessibilityIdentifier = "pan.accounts.columns"
        for column in columns {
            column.axis = .vertical
            column.spacing = 0
            addArrangedSubview(column)
        }
        widthConstraints = [
            columns[0].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.38),
            columns[1].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.30),
        ]
        setAccessibilityLayout(accessibilityLayout)
    }

    func setAccessibilityLayout(_ accessibilityLayout: Bool) {
        guard isAccessibilityLayout != accessibilityLayout else { return }
        isAccessibilityLayout = accessibilityLayout
        axis = accessibilityLayout ? .vertical : .horizontal
        alignment = accessibilityLayout ? .fill : .center
        if accessibilityLayout {
            NSLayoutConstraint.deactivate(widthConstraints)
        } else {
            NSLayoutConstraint.activate(widthConstraints)
        }
    }

    @available(*, unavailable)
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif

#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class PanAccountsViewController: PageViewController, UITableViewDataSource {
    let model: PanAccountsModel
    let table = SettingsTableView()
    private weak var loginCard: PanLoginViewController?
    private var displayedNotice: UUID?

    public init(model: PanAccountsModel, themeProvider: any PageThemeProviding) {
        self.model = model; super.init(themeProvider: themeProvider); pageTitle = "网盘账号"
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "pan.accounts"
        table.accessibilityIdentifier = "pan.accounts.table"
        table.dataSource = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 110
        table.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(table)
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: PanAccountsViewController, _: UITraitCollection) in page.table.reloadData()
        }
        observe(model) { [weak self] in self?.render() }
        render()
    }
    public override func pageDidBecomeVisible() { model.start(); showNotice() }
    public override func pageDidBecomeHidden() {
        // An over-full-screen login stays visible above this retained page.
        if loginCard == nil { model.close() }
    }
    public override func pageDidBecomeInactive() { closeLogin(); model.close() }
    public override func pageDidFinishAppearing() { showNotice() }
    public override func applyTheme() { super.applyTheme(); if isViewLoaded { table.reloadData() } }
    private func render() {
        table.reloadData(); showNotice()
    }
    private func showNotice() {
        guard let notice = model.notice, displayedNotice != notice.id else { return }
        if showError(notice.message) { displayedNotice = notice.id }
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { model.rows.count + 1 }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        let last = indexPath.row == model.rows.count
        cell.selectionStyle = .none; cell.backgroundColor = .clear
        cell.backgroundView = SettingsRowBackgroundView(theme: theme, first: indexPath.row == 0,
            last: last, showsSeparator: !last)
        let columns = PanAccountColumns(accessibilityLayout: traitCollection.preferredContentSizeCategory.isAccessibilityCategory)
        let font = UIFont.preferredFont(forTextStyle: .body, compatibleWith: traitCollection)
        columns.translatesAutoresizingMaskIntoConstraints = false; cell.contentView.addSubview(columns)
        NSLayoutConstraint.activate([
            columns.leadingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.leadingAnchor),
            columns.trailingAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.trailingAnchor),
            columns.topAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.topAnchor),
            columns.bottomAnchor.constraint(equalTo: cell.contentView.layoutMarginsGuide.bottomAnchor),
        ])
        if indexPath.row == 0 {
            cell.accessibilityIdentifier = "pan.accounts.header"
            for (column, text) in zip(columns.columns, ["平台", "状态", "操作"]) {
                let label = UILabel(); label.text = text
                label.font = UIFont.preferredFont(forTextStyle: .subheadline, compatibleWith: traitCollection)
                label.numberOfLines = 0; label.lineBreakMode = .byWordWrapping
                label.adjustsFontForContentSizeCategory = true
                if column === columns.columns[2] { label.textAlignment = .center }
                label.textColor = UIColor(theme.settings.muted); column.addArrangedSubview(label)
            }
            return cell
        }
        let row = model.rows[indexPath.row - 1]
        cell.accessibilityIdentifier = "pan.accounts." + row.id.rawValue
        let icon = UIImageView(image: UIImage(systemName: "externaldrive",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 24, weight: .regular)))
        icon.contentMode = .scaleAspectFit; icon.tintColor = UIColor(theme.settings.muted)
        icon.isAccessibilityElement = false
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 24),
            icon.heightAnchor.constraint(equalToConstant: 24),
        ])
        let title = UILabel(); title.text = row.id.title; title.font = font
        title.numberOfLines = 0; title.lineBreakMode = .byWordWrapping
        title.adjustsFontForContentSizeCategory = true; title.textColor = UIColor(theme.settings.ink)
        let platform = UIStackView(arrangedSubviews: [icon, title])
        platform.alignment = .center; platform.spacing = 8
        platform.accessibilityIdentifier = "pan.accounts.platform"
        columns.columns[0].addArrangedSubview(platform)
        if row.busy {
            let spinner = UIActivityIndicatorView(style: .medium)
            spinner.accessibilityLabel = "正在操作账号"; spinner.startAnimating()
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
            columns.columns[1].alignment = traitCollection.preferredContentSizeCategory.isAccessibilityCategory ? .fill : .leading
            columns.columns[1].addArrangedSubview(badge)
        }
        for action in row.actions {
            let title: String, suffix: String
            switch action {
            case .login: title = row.account?.status == .expired ? "重新登录" : "登录"; suffix = "login"
            case .refresh: title = "刷新"; suffix = "refresh"
            case .logout: title = "退出登录"; suffix = "logout"
            }
            let foreground = UIColor(row.busy ? theme.settings.muted : theme.settings.accent)
            var configuration = UIButton.Configuration.plain()
            configuration.title = title; configuration.baseForegroundColor = foreground
            configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
                var attributes = $0
                attributes.foregroundColor = foreground
                attributes.font = font
                return attributes
            }
            configuration.titleAlignment = .center
            configuration.titleLineBreakMode = .byWordWrapping
            configuration.contentInsets = .init(top: 6, leading: 0, bottom: 6, trailing: 0)
            let button = UIButton(configuration: configuration, primaryAction: UIAction { [weak self] _ in
                self?.perform(action, provider: row.id)
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
        return cell
    }
    private func perform(_ action: PanAccountAction, provider: PanProviderID) {
        switch action {
        case .refresh: model.refresh(provider)
        case .logout: model.logout(provider)
        case .login:
            guard presentedViewController == nil, let login = model.openLogin(provider) else { return }
            let card = PanLoginViewController(model: login, themeProvider: themeProvider) { [weak self] in self?.closeLogin() }
            loginCard = card; present(card, animated: true)
        }
    }
    private func closeLogin() {
        model.closeLogin()
        guard let card = loginCard else { return }
        loginCard = nil
        if card.presentingViewController != nil { dismissPageModal(card) }
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

#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

@MainActor
public final class PanAccountsViewController: PageViewController, UITableViewDataSource {
    let model: PanAccountsModel
    let table = UITableView(frame: .zero, style: .insetGrouped)
    private let loading = UIActivityIndicatorView(style: .medium)
    private weak var loginCard: PanLoginViewController?
    private var displayedNotice: UUID?

    public init(model: PanAccountsModel, themeProvider: any PageThemeProviding) {
        self.model = model; super.init(themeProvider: themeProvider); pageTitle = "网盘账号"
    }
    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "pan.accounts"
        table.accessibilityIdentifier = "pan.accounts.table"
        table.backgroundColor = .clear; table.dataSource = self
        table.rowHeight = UITableView.automaticDimension; table.estimatedRowHeight = 110
        table.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(table)
        loading.hidesWhenStopped = true; table.backgroundView = loading
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])
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
        if model.loading && model.rows.isEmpty { loading.startAnimating() } else { loading.stopAnimating() }
        table.reloadData(); showNotice()
    }
    private func showNotice() {
        guard let notice = model.notice, displayedNotice != notice.id else { return }
        if showError(notice.message) { displayedNotice = notice.id }
    }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { model.rows.count + 1 }
    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.selectionStyle = .none; cell.backgroundColor = UIColor(theme.surface)
        let columns = PanAccountColumns()
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
                let label = UILabel(); label.text = text; label.font = .preferredFont(forTextStyle: .subheadline)
                label.textColor = UIColor(theme.muted); column.addArrangedSubview(label)
            }
            return cell
        }
        let row = model.rows[indexPath.row - 1]
        cell.accessibilityIdentifier = "pan.accounts." + row.id.rawValue
        let platform = UIHostingConfiguration {
            AccountPlatformLabel(title: row.id.title, symbol: "externaldrive", size: 24, theme: theme)
                .frame(maxWidth: .infinity, alignment: .leading)
                .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        }.margins(.all, 0).makeContentView()
        platform.accessibilityIdentifier = "pan.accounts.platform"
        columns.columns[0].addArrangedSubview(platform)
        if row.busy || row.account == nil {
            let spinner = UIActivityIndicatorView(style: .medium)
            spinner.accessibilityLabel = "读取账号"; spinner.startAnimating()
            columns.columns[1].addArrangedSubview(spinner)
        } else {
            let label = UILabel(); label.text = row.account?.status.title
            label.accessibilityIdentifier = "pan.accounts.status"
            label.font = .preferredFont(forTextStyle: .subheadline); label.numberOfLines = 0
            label.textColor = row.account?.status == .expired ? UIColor(theme.settingsDanger) : UIColor(theme.muted)
            columns.columns[1].addArrangedSubview(label)
        }
        for action in row.actions {
            let title: String, suffix: String
            switch action {
            case .login: title = row.account?.status == .expired ? "重新登录" : "登录"; suffix = "login"
            case .refresh: title = "刷新"; suffix = "refresh"
            case .logout: title = "退出登录"; suffix = "logout"
            }
            var configuration = UIButton.Configuration.plain()
            configuration.title = title; configuration.baseForegroundColor = UIColor(theme.accent)
            configuration.titleLineBreakMode = .byWordWrapping
            configuration.contentInsets = .init(top: 6, leading: 0, bottom: 6, trailing: 0)
            let button = UIButton(configuration: configuration, primaryAction: UIAction { [weak self] _ in
                self?.perform(action, provider: row.id)
            })
            button.titleLabel?.font = .preferredFont(forTextStyle: .subheadline)
            button.titleLabel?.numberOfLines = 0
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

/// Three stable columns at narrow and wide phone sizes; paired operations stack
/// within the last column instead of turning account status into a subtitle.
@MainActor
final class PanAccountColumns: UIStackView {
    let columns = (0..<3).map { _ in UIStackView() }
    init() {
        super.init(frame: .zero)
        axis = .horizontal; alignment = .center; spacing = 8
        accessibilityIdentifier = "pan.accounts.columns"
        for column in columns { column.axis = .vertical; column.spacing = 0; addArrangedSubview(column) }
        NSLayoutConstraint.activate([
            columns[0].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.38),
            columns[1].widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.22),
        ])
    }
    @available(*, unavailable)
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif

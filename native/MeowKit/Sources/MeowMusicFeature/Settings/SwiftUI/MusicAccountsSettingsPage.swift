#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicAccountsViewController: MusicSettingsPageViewController<MusicAccountsSettingsContent> {
    private let owner: MusicViewModel
    private let state: MusicAccountsSettingsState
    private weak var router: (any MusicUIRouting)?
    private weak var accessAlert: UIAlertController?
    private weak var detail: MusicAccountDetailViewController?
    private var dialogAccess: UUID?
    private var dialogPlatform: MusicAccountPlatform?

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: (any MusicUIRouting)?) {
        owner = model
        state = MusicAccountsSettingsState(owner: model)
        self.router = router
        super.init(appearance: appearance, content: MusicAccountsSettingsContent(
            state: state, theme: PageTheme(palette: appearance.pageColorway)
        ))
        title = "平台账号"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            guard let self else { return }
            invalidateErrorPresentation()
            state.reconcileAccess()
            if !owner.isAvailable || owner.isClosed || dialogAccess != owner.accountAccessGeneration {
                accessAlert?.dismiss(animated: false)
                closeDetail(animated: false)
            }
        }
        observe(owner.accountChanges) { [weak self] platform in
            guard let self else { return }
            state.accountSessionChanged(platform)
            if dialogPlatform == platform { accessAlert?.dismiss(animated: false) }
        }
        observe(state.$rows.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.closeDetailIfSignedOut()
        }
        observe(state.errorChanges) { [weak self] in self?.showFailure() }
    }

    override func pageDidBecomeActive() { state.start() }

    override func pageDidBecomeVisible() { showFailure() }

    override func pageDidBecomeInactive() { state.close() }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicAccountsSettingsContent(
            state: state, theme: theme,
            openAccount: { [weak self] in self?.openAccount($0) },
            openCredentials: { [weak self] in self?.openCredentials() },
            openLibrary: { [weak self] in self?.openLibrary() }
        ))
    }

    private func openAccount(_ row: MusicAccountStatus) {
        guard let context = state.context(for: row), presentedViewController == nil else { return }
        switch row.phase {
        case .signedIn, .requiresValidation:
            openDetail(row.platform, context: context)
        default:
            if row.canLogin { openLogin(row, context: context) }
        }
    }

    private func openDetail(_ platform: MusicAccountPlatform, context: MusicAccountSettingsContext) {
        let controller = MusicAccountDetailViewController(
            state: state, platform: platform, appearance: appearance,
            actions: .init(
                perform: { [weak self] in self?.performCurrent($0, platform: platform) },
                logout: { [weak self] in self?.confirmLogout(platform) },
                recommend: { [weak self] kind, title in self?.recommend(platform, kind: kind, title: title) },
                close: { [weak self] in self?.closeDetail(animated: true) }
            )
        )
        detail = controller
        dialogAccess = context.accessGeneration
        present(controller, animated: true)
    }

    private func closeDetail(animated: Bool, completion: (() -> Void)? = nil) {
        guard let detail, detail.presentingViewController != nil, !detail.isBeingDismissed else {
            completion?()
            return
        }
        detail.dismiss(animated: animated, completion: completion)
    }

    private func closeDetailIfSignedOut() {
        guard let platform = detail?.platform, !state.pendingAccounts.contains(platform) else { return }
        let phase = state.rows.first { $0.id == platform }?.account.phase
        if phase != .signedIn, phase != .requiresValidation { closeDetail(animated: true) }
    }

    /// Detail actions always rebuild their context from the current row, never from the opening snapshot.
    private func performCurrent(_ action: MusicAccountSettingsAction, platform: MusicAccountPlatform) {
        guard let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              let context = state.context(for: row) else { return }
        perform(action, context: context)
    }

    private func confirmLogout(_ platform: MusicAccountPlatform) {
        guard let host = detail, host.presentedViewController == nil,
              let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              let context = state.context(for: row) else { return }
        let name = MusicAccountSettingsSummary(account: row).detail(at: Date()).name
        let alert = UIAlertController(
            title: "退出登录？", message: [platform.title, name].compactMap { $0 }.joined(separator: " · "),
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "退出登录", style: .destructive) { [weak self] _ in
            guard let self, state.isCurrent(context) else { return }
            perform(.logout, context: context)
        })
        dialogAccess = context.accessGeneration; dialogPlatform = platform; accessAlert = alert
        host.present(alert, animated: true)
    }

    private func recommend(_ platform: MusicAccountPlatform, kind: String, title: String) {
        guard let row = state.accounts?.rows.first(where: { $0.platform == platform }),
              row.phase == .signedIn, let context = state.context(for: row) else { return }
        closeDetail(animated: true) { [weak self] in
            guard let self, state.isCurrent(context) else { return }
            router?.showPublicBrowse(.accountRecommendation(platform, kind: kind, title: title), title: title)
        }
    }

    private func openLogin(_ row: MusicAccountStatus, context: MusicAccountSettingsContext) {
        guard row.canLogin, state.isCurrent(context), let service = owner.accounts,
              presentedViewController == nil else { return }
        present(
            MusicAccountLoginViewController(
                owner: owner, service: service, platform: row.platform, appearance: appearance
            ),
            animated: true
        )
    }

    private func perform(_ action: MusicAccountSettingsAction, context: MusicAccountSettingsContext) {
        state.perform(action, context: context)
        guard case .feedback(_) = action,
              let feedback = state.feedback, let ticket = feedback.confirmation else { return }
        let alert = UIAlertController(
            title: "允许通过HTTP发送酷狗听音记录？",
            message: "HTTP传输会暴露记录、设备标识及CSCC会话信息，可能被窃取或伪造。",
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in feedback.dismissConfirmation() })
        alert.addAction(UIAlertAction(title: "允许", style: .default) { _ in feedback.confirm(ticket) })
        let host: UIViewController = detail ?? self
        let show = { [weak self] in
            guard let self, isPageVisible, state.isCurrent(context), feedback.confirmation == ticket else { return }
            dialogAccess = context.accessGeneration; dialogPlatform = context.platform; accessAlert = alert
            host.present(alert, animated: true)
        }
        if let previous = host.presentedViewController as? UIAlertController {
            previous.dismiss(animated: true, completion: show)
        } else {
            show()
        }
    }

    private func showFailure() {
        // The floating detail reports the same state errors while it covers this page.
        guard detail == nil else { return }
        let access = owner.accountAccessGeneration
        reportError(state.error, retry: { [weak state] in state?.retry(generation: access) })
    }

    private func openCredentials() {
        guard state.canAct(generation: owner.accountAccessGeneration),
              let service = owner.accounts as? any MusicCredentialsImporting else { return }
        navigationController?.pushViewController(
            MusicCredentialImportViewController(owner: owner, service: service, appearance: appearance),
            animated: true
        )
    }

    private func openLibrary() {
        guard state.canAct(generation: owner.accountAccessGeneration) else { return }
        navigationController?.pushViewController(
            MusicLibraryImportViewController(model: owner, appearance: appearance), animated: true
        )
    }
}

@MainActor
struct MusicAccountsSettingsContent: View {
    @ObservedObject var state: MusicAccountsSettingsState
    let theme: PageTheme
    var openAccount: (MusicAccountStatus) -> Void = { _ in }
    var openCredentials: () -> Void = {}
    var openLibrary: () -> Void = {}

    var body: some View {
        List {
            Section("音乐平台") {
                if state.accounts == nil {
                    Text("账号服务不可用")
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
                ForEach(MusicAccountPlatform.allCases) { platform in
                    let summary = state.rows.first { $0.id == platform }
                    Button {
                        if let summary { openAccount(summary.account) }
                    } label: {
                        MusicPlatformAccountRow(
                            platform: platform, summary: summary, theme: theme,
                            loading: state.pendingAccounts.contains(platform) || summary == nil && state.busy
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("music.accounts." + platform.rawValue)
                    .disabled(summary == nil || !state.available || state.pendingAccounts.contains(platform))
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
            Section("导入") {
                if state.hasCredentialsImport {
                    Button(action: openCredentials) {
                        MusicSettingsLinkRow(theme: theme) { Text("导入账号凭据") }
                    }
                    .accessibilityIdentifier("music.credentials.import")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
                Button(action: openLibrary) {
                    MusicSettingsLinkRow(theme: theme) { Text("导入曲库") }
                }
                .accessibilityIdentifier("music.library.import")
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            .headerProminence(.increased)
            .disabled(!state.available)
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

private struct MusicPlatformAccountRow: View {
    let platform: MusicAccountPlatform
    let summary: MusicAccountSettingsSummary?
    let theme: PageTheme
    let loading: Bool

    var body: some View {
        let detail = summary?.detail(at: Date()) ?? .init(status: "不可用")
        HStack(spacing: 12) {
            MusicPlatformLogo(assetName: platform.settingsLogoName, size: 36, theme: theme)
            VStack(alignment: .leading, spacing: 6) {
                Text(platform.title)
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
                if loading {
                    ProgressView().accessibilityLabel("读取账号")
                } else {
                    accountDetail(detail)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if let account = summary?.account {
                if account.phase == .signedOut, account.canLogin {
                    Text("登录")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color(uiColor: UIColor(theme.accent)))
                        .frame(minWidth: 44, minHeight: 44)
                        .fixedSize(horizontal: true, vertical: false)
                } else if account.phase != .unavailable {
                    Image(systemName: "chevron.right")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                        .frame(width: 16)
                        .accessibilityHidden(true)
                }
            }
        }
        .frame(minHeight: 56)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func accountDetail(_ detail: MusicAccountSettingsDetail) -> some View {
        let status = Text(detail.status)
            .foregroundColor(detail.isNonVIP ? theme.settingsDanger : Color(uiColor: UIColor(theme.muted)))
        Group {
            if let name = detail.name {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 4) {
                        Text(name)
                        Text("·").accessibilityHidden(true)
                        status
                    }
                    .lineLimit(1)
                    Text("\(name) · \(status)")
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                status
            }
        }
        .font(.subheadline)
        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
    }
}
#endif

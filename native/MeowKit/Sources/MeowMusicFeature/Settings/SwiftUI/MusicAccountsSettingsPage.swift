#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

@MainActor
final class MusicAccountsViewController: MusicSettingsPageViewController<MusicAccountsSettingsContent> {
    private let owner: MusicViewModel
    private let state: MusicAccountsSettingsState
    private weak var router: (any MusicUIRouting)?
    private weak var accessAlert: UIAlertController?
    private var dialogAccess: UUID?
    private var dialogPlatform: MusicAccountPlatform?

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: (any MusicUIRouting)?) {
        owner = model
        state = MusicAccountsSettingsState(owner: model)
        self.router = router
        super.init(appearance: appearance, content: MusicAccountsSettingsContent(
            state: state, theme: PageTheme(palette: appearance.pageColorway)
        ))
        title = "平台聚合设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            guard let self else { return }
            invalidateErrorPresentation()
            state.reconcileAccess()
            if !owner.isAvailable || owner.isClosed || dialogAccess != owner.accountAccessGeneration {
                accessAlert?.dismiss(animated: false)
            }
        }
        observe(owner.accountChanges) { [weak self] platform in
            guard let self else { return }
            state.accountSessionChanged(platform)
            if dialogPlatform == platform { accessAlert?.dismiss(animated: false) }
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
        guard let context = state.context(for: row), let accounts = state.accounts else { return }
        let alert = UIAlertController(title: row.platform.title, message: row.phase.title, preferredStyle: .actionSheet)
        func add(_ title: String, _ action: MusicAccountSettingsAction, style: UIAlertAction.Style = .default) {
            alert.addAction(UIAlertAction(title: title, style: style) { [weak self] _ in
                self?.perform(action, context: context)
            })
        }
        if row.phase == .signedIn || row.phase == .requiresValidation {
            add("验证登录会话", .validate)
            if accounts.canRefreshSession(row.platform) { add("刷新登录会话", .refreshSession) }
            if accounts.supportsProfileRefresh { add("刷新资料", .refreshProfile) }
            add("退出登录", .logout, style: .destructive)
        } else if row.canLogin {
            alert.addAction(UIAlertAction(title: "登录", style: .default) { [weak self] _ in
                guard let self, state.isCurrent(context), let service = owner.accounts else { return }
                navigationController?.pushViewController(
                    MusicAccountLoginViewController(
                        owner: owner, service: service, platform: row.platform, appearance: appearance
                    ),
                    animated: true
                )
            })
        }
        if row.phase == .signedIn, let sync = state.sync {
            let value = sync.selection(row.platform)
            if sync.supportsAutomatic, sync.automaticRevision != nil {
                add((value.favorites ? "关闭" : "开启") + "收藏歌曲自动同步", .favorites(!value.favorites))
                add((value.playlists ? "关闭" : "开启") + "歌单自动同步", .playlists(!value.playlists))
            }
            if state.canSync { add("立即同步", .synchronize) }
        }
        if let setting = state.feedback?.value?.platforms[row.platform],
           row.platform == .netease || row.playbackFeedbackAvailable {
            add((setting.enabled ? "关闭" : "开启") + "播放上报", .feedback(!setting.enabled))
        }
        if row.phase == .signedIn, row.platform != .netease {
            let choices = row.platform == .qq
                ? [("guess", "猜你喜欢"), ("radar", "雷达推荐")]
                : [("daily", "每日推荐"), ("fm", "红心 Radio · 本次推荐")]
            for choice in choices {
                alert.addAction(UIAlertAction(title: choice.1, style: .default) { [weak self] _ in
                    guard let self, state.isCurrent(context) else { return }
                    router?.showPublicBrowse(
                        .accountRecommendation(row.platform, kind: choice.0, title: choice.1), title: choice.1
                    )
                })
            }
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        dialogAccess = context.accessGeneration; dialogPlatform = row.platform; accessAlert = alert
        present(alert, animated: true)
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
        let show = { [weak self] in
            guard let self, isPageVisible, state.isCurrent(context), feedback.confirmation == ticket else { return }
            dialogAccess = context.accessGeneration; dialogPlatform = context.platform; accessAlert = alert
            present(alert, animated: true)
        }
        if let previous = presentedViewController as? UIAlertController {
            previous.dismiss(animated: true, completion: show)
        } else {
            show()
        }
    }

    private func showFailure() {
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
            Section {
                if state.accounts == nil {
                    Text("账号服务不可用")
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                }
                ForEach(state.rows) { summary in
                    Button { openAccount(summary.account) } label: {
                        MusicSettingsLinkRow(theme: theme) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(summary.account.platform.title)
                                if state.pendingAccounts.contains(summary.id) {
                                    ProgressView().accessibilityLabel("读取账号")
                                } else {
                                    Text(summary.detail)
                                        .font(.subheadline)
                                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                                }
                            }
                        }
                    }
                    .disabled(!state.available || state.pendingAccounts.contains(summary.id))
                }
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            Section("导入") {
                if state.hasCredentialsImport {
                    Button(action: openCredentials) {
                        MusicSettingsLinkRow(theme: theme) { Text("导入账号凭据") }
                    }
                    .accessibilityIdentifier("music.credentials.import")
                }
                Button(action: openLibrary) {
                    MusicSettingsLinkRow(theme: theme) { Text("导入曲库") }
                }
                .accessibilityIdentifier("music.library.import")
            }
            .disabled(!state.available)
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}
#endif

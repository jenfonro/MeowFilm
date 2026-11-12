#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

struct MusicAccountDetailActions {
    var perform: (MusicAccountSettingsAction) -> Void = { _ in }
    var logout: () -> Void = {}
    var recommend: (_ kind: String, _ title: String) -> Void = { _, _ in }
    var close: () -> Void = {}
}

/// Signed-in account details float over the account list and read its live state.
@MainActor
final class MusicAccountDetailViewController: MusicSettingsPageViewController<MusicAccountDetailContent> {
    let platform: MusicAccountPlatform
    private let state: MusicAccountsSettingsState
    private let actions: MusicAccountDetailActions

    init(state: MusicAccountsSettingsState, platform: MusicAccountPlatform,
         appearance: MusicAppearancePreferencesStore, actions: MusicAccountDetailActions) {
        self.state = state
        self.platform = platform
        self.actions = actions
        super.init(appearance: appearance, content: MusicAccountDetailContent(
            state: state, platform: platform, theme: PageTheme(colors: appearance.pagePalette), actions: actions
        ))
        pageTitle = platform.title
        modalPresentationStyle = .overFullScreen
        modalTransitionStyle = .crossDissolve
    }

    override func loadView() { view = AccountFloatingBackdrop() }
    override var usesPageHeader: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(state.errorChanges) { [weak self] in self?.reportError(self?.state.error) }
    }

    override func pageDidBecomeVisible() { reportError(state.error) }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicAccountDetailContent(state: state, platform: platform, theme: theme, actions: actions))
    }
}

@MainActor
struct MusicAccountDetailContent: View {
    @ObservedObject var state: MusicAccountsSettingsState
    let platform: MusicAccountPlatform
    let theme: PageTheme
    var actions = MusicAccountDetailActions()

    var body: some View {
        let summary = state.rows.first { $0.id == platform }
        AccountFloatingCard(theme: theme) {
            VStack(alignment: .leading, spacing: 0) {
                MusicAccountCardHeader(platform: platform, title: platform.title, theme: theme, close: actions.close) {
                    if let summary, let accounts = state.accounts {
                        MusicAccountMoreMenu(state: state, accounts: accounts, account: summary.account,
                                             theme: theme, actions: actions)
                    }
                }
                if let summary {
                    details(summary)
                }
            }
        }
    }

    @ViewBuilder
    private func details(_ summary: MusicAccountSettingsSummary) -> some View {
        let account = summary.account
        let now = Date()
        let membership = summary.membership(at: now)
        let signedIn = account.phase == .signedIn
        let pending = state.pendingAccounts.contains(platform) || !state.available
        HStack(spacing: 14) {
            MusicAccountAvatar(url: account.profile?.avatarURL, theme: theme)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(summary.detail(at: now).name ?? platform.title)
                        .font(.title3.weight(.semibold))
                        .lineLimit(2)
                    if account.phase == .requiresValidation {
                        Text(account.phase.title)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(theme.settingsDanger)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(Capsule().fill(theme.settingsDanger.opacity(0.1)))
                    }
                }
                Text(membership.title)
                    .font(.subheadline)
                    .foregroundStyle(color(membership.tone))
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
        .padding(.top, 16)
        .padding(.bottom, 18)
        Rectangle().fill(Color(uiColor: UIColor(theme.settings.border))).frame(height: 1)
        HStack {
            Text("会员有效期").foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
            Spacer(minLength: 8)
            Text(membership.expiry).monospacedDigit()
        }
        .font(.subheadline)
        .frame(minHeight: 48)
        .accessibilityElement(children: .combine)
        if let sync = state.sync, sync.supportsAutomatic {
            MusicAccountSyncSection(sync: sync, platform: platform, theme: theme,
                                    enabled: signedIn && !pending, perform: actions.perform)
        }
        HStack(spacing: 12) {
            Button("刷新资料") { actions.perform(.refreshProfile) }
                .buttonStyle(AccountPrimaryButtonStyle(theme: theme))
                .disabled(!signedIn || pending || state.accounts?.supportsProfileRefresh != true)
            Button("退出登录", action: actions.logout)
                .buttonStyle(AccountDestructiveButtonStyle(theme: theme))
                .disabled(pending)
        }
        .padding(.top, 20)
    }

    private func color(_ tone: MusicAccountSettingsMembership.Tone) -> Color {
        switch tone {
        case .accent: return Color(uiColor: UIColor(theme.settings.accent))
        case .muted: return Color(uiColor: UIColor(theme.settings.muted))
        case .danger: return theme.settingsDanger
        }
    }
}

private struct MusicAccountAvatar: View {
    let url: URL?
    let theme: PageTheme
    @State private var image: UIImage?

    var body: some View {
        ZStack {
            if let image {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Color(uiColor: UIColor(theme.settings.input))
                Image(systemName: "person.fill")
                    .font(.title2)
                    .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
            }
        }
        .frame(width: 60, height: 60)
        .clipShape(Circle())
        .accessibilityHidden(true)
        // Same isolated, redirect-checked transport and cache as catalogue covers.
        .task(id: url) {
            image = nil
            guard let url, let value = try? await MusicArtworkImages.shared.read(url).0, !Task.isCancelled else { return }
            image = value
        }
    }
}

@MainActor
private struct MusicAccountSyncSection: View {
    @ObservedObject var sync: MusicLibrarySyncViewModel
    let platform: MusicAccountPlatform
    let theme: PageTheme
    let enabled: Bool
    let perform: (MusicAccountSettingsAction) -> Void

    var body: some View {
        let selection = sync.selection(platform)
        let ready = enabled && sync.automaticRevision != nil && !sync.busy
        VStack(alignment: .leading, spacing: 0) {
            Text("自动同步")
                .font(.subheadline.weight(.semibold))
                .padding(.top, 12)
                .padding(.bottom, 4)
            Toggle("收藏歌曲", isOn: Binding(get: { selection.favorites }, set: { perform(.favorites($0)) }))
                .frame(minHeight: 48)
            Rectangle().fill(Color(uiColor: UIColor(theme.settings.border))).frame(height: 1)
            Toggle("歌单", isOn: Binding(get: { selection.playlists }, set: { perform(.playlists($0)) }))
                .frame(minHeight: 48)
        }
        .disabled(!ready)
    }
}

/// Existing per-account operations outside the two primary buttons stay reachable here.
@MainActor
private struct MusicAccountMoreMenu: View {
    @ObservedObject var state: MusicAccountsSettingsState
    @ObservedObject var accounts: MusicAccountsViewModel
    let account: MusicAccountStatus
    let theme: PageTheme
    let actions: MusicAccountDetailActions

    var body: some View {
        let signedIn = account.phase == .signedIn
        let pending = state.pendingAccounts.contains(account.platform) || !state.available
        Menu {
            Button("验证登录会话") { actions.perform(.validate) }
            if accounts.canRefreshSession(account.platform) {
                Button("刷新登录会话") { actions.perform(.refreshSession) }
            }
            if signedIn, state.canSync {
                Button("立即同步") { actions.perform(.synchronize) }
            }
            if let feedback = state.feedback,
               account.platform == .netease || account.playbackFeedbackAvailable {
                MusicAccountFeedbackToggle(feedback: feedback, platform: account.platform, perform: actions.perform)
            }
            if signedIn, account.platform != .netease {
                let choices = account.platform == .qq
                    ? [("guess", "猜你喜欢"), ("radar", "雷达推荐")]
                    : [("daily", "每日推荐"), ("fm", "红心 Radio · 本次推荐")]
                Section {
                    ForEach(choices.indices, id: \.self) { index in
                        let choice = choices[index]
                        Button(choice.1) { actions.recommend(choice.0, choice.1) }
                    }
                }
            }
        } label: {
            Image(systemName: "ellipsis.circle")
                .font(.body.weight(.semibold))
                .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
                .frame(width: 44, height: 44)
                .contentShape(Rectangle())
        }
        .disabled(pending)
        .accessibilityLabel("更多")
    }
}

@MainActor
private struct MusicAccountFeedbackToggle: View {
    @ObservedObject var feedback: MusicPlaybackFeedbackSettingsModel
    let platform: MusicAccountPlatform
    let perform: (MusicAccountSettingsAction) -> Void

    var body: some View {
        if let setting = feedback.value?.platforms[platform] {
            Toggle("播放上报", isOn: Binding(get: { setting.enabled }, set: { perform(.feedback($0)) }))
        }
    }
}
#endif

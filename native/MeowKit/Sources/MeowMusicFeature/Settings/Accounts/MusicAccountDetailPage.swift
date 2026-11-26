#if os(iOS)
import UIKit
import Combine
import MeowDebug
import MeowUI

struct MusicAccountDetailActions {
    var perform: (MusicAccountSettingsAction) -> Void = { _ in }
    var logout: () -> Void = {}
    var recommend: (String, String) -> Void = { _, _ in }
    var confirmDestructiveSync: () -> Void = {}
    var resetSync: () -> Void = {}
    var close: () -> Void = {}
}

/// Pure UIKit music platform account detail floating card backed by `FloatingCardDialogController`.
@MainActor
final class MusicAccountDetailViewController: FloatingCardDialogController {
    let platform: MusicAccountPlatform
    private let state: MusicAccountsSettingsState
    private let artworkImages: MusicArtworkImages
    private let actions: MusicAccountDetailActions
    private struct MoreMenuSignature: Equatable {
        let platform: MusicAccountPlatform
        let canRefreshSession: Bool
        let canSync: Bool
        let canResetSync: Bool
        let feedbackEnabled: Bool?
        let showsRecommendations: Bool
    }

    private var subscriptions: Set<AnyCancellable> = []
    private var currentAvatarURL: URL?
    private var displayedMoreMenu: MoreMenuSignature?

    private let platformMark: AccountPlatformMarkView
    private let moreButton = UIButton(type: .system)

    private let profileRow = UIStackView()
    private let avatarContainer = UIView()
    private let avatarPlaceholder = UIImageView()
    private let avatarImageView = UIImageView()
    private let nameLabel = UILabel()
    private let validationBadge = UILabel()
    private let membershipLabel = UILabel()

    private let profileDivider = SettingsDividerView()
    private let expiryRow = UIStackView()
    private let expiryTitleLabel = UILabel()
    private let expiryValueLabel = UILabel()

    private let syncContainer = UIStackView()
    private let syncTitleLabel = UILabel()
    private let syncFavoritesSwitch = SettingsSwitch()
    private let syncFavoritesLabel = UILabel()
    private let syncDivider = SettingsDividerView()
    private let syncPlaylistsSwitch = SettingsSwitch()
    private let syncPlaylistsLabel = UILabel()
    private var syncConfirmButton: AccountCardButton!
    private var syncResetButton: AccountCardButton!

    private let buttonsRow = UIStackView()
    private var refreshProfileButton: AccountCardButton!
    private var logoutButton: AccountCardButton!

    init(
        state: MusicAccountsSettingsState,
        platform: MusicAccountPlatform,
        appearance: MusicAppearancePreferencesStore,
        artworkImages: MusicArtworkImages,
        actions: MusicAccountDetailActions
    ) {
        self.state = state
        self.platform = platform
        self.artworkImages = artworkImages
        self.actions = actions
        platformMark = AccountPlatformMarkView(assetName: platform.settingsLogoName, symbol: "music.note", size: 34)
        super.init(title: platform.title, themeProvider: appearance)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.accounts.detail"
        setHeaderLeading(platformMark)
        configureMoreButton()
        setHeaderTrailing(moreButton)

        buildProfileSection()
        buildExpirySection()
        buildSyncSection()
        buildActionButtons()
        bindState()
        applyTheme()
        render()
    }

    override func dialogWillClose() {
        super.dialogWillClose()
        subscriptions.removeAll()
    }

    override func requestDismiss() {
        actions.close()
    }

    private func configureMoreButton() {
        var config = UIButton.Configuration.plain()
        config.image = UIImage(
            systemName: "ellipsis.circle",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 18, weight: .regular)
        )
        config.contentInsets = .zero
        moreButton.configuration = config
        moreButton.showsMenuAsPrimaryAction = true
        moreButton.accessibilityLabel = "更多"
        moreButton.accessibilityIdentifier = "music.accounts.detail.more"
        moreButton.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            moreButton.widthAnchor.constraint(equalToConstant: 44),
            moreButton.heightAnchor.constraint(equalToConstant: 44),
        ])
    }

    private func buildProfileSection() {
        avatarContainer.layer.cornerRadius = 30
        avatarContainer.clipsToBounds = true
        avatarContainer.isAccessibilityElement = false
        avatarContainer.translatesAutoresizingMaskIntoConstraints = false

        avatarPlaceholder.image = UIImage(
            systemName: "person.fill",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 22, weight: .regular)
        )
        avatarPlaceholder.contentMode = .center
        avatarPlaceholder.translatesAutoresizingMaskIntoConstraints = false

        avatarImageView.contentMode = .scaleAspectFill
        avatarImageView.clipsToBounds = true
        avatarImageView.translatesAutoresizingMaskIntoConstraints = false

        avatarContainer.addSubview(avatarPlaceholder)
        avatarContainer.addSubview(avatarImageView)
        NSLayoutConstraint.activate([
            avatarContainer.widthAnchor.constraint(equalToConstant: 60),
            avatarContainer.heightAnchor.constraint(equalToConstant: 60),
            avatarPlaceholder.centerXAnchor.constraint(equalTo: avatarContainer.centerXAnchor),
            avatarPlaceholder.centerYAnchor.constraint(equalTo: avatarContainer.centerYAnchor),
            avatarImageView.topAnchor.constraint(equalTo: avatarContainer.topAnchor),
            avatarImageView.bottomAnchor.constraint(equalTo: avatarContainer.bottomAnchor),
            avatarImageView.leadingAnchor.constraint(equalTo: avatarContainer.leadingAnchor),
            avatarImageView.trailingAnchor.constraint(equalTo: avatarContainer.trailingAnchor),
        ])

        nameLabel.font = .preferredFont(forTextStyle: .title3)
        nameLabel.adjustsFontForContentSizeCategory = true
        nameLabel.numberOfLines = 2
        registerPrimaryLabel(nameLabel)

        validationBadge.font = .preferredFont(forTextStyle: .caption1)
        validationBadge.adjustsFontForContentSizeCategory = true
        validationBadge.textAlignment = .center
        validationBadge.layer.cornerRadius = 10
        validationBadge.clipsToBounds = true
        validationBadge.setContentHuggingPriority(.required, for: .horizontal)
        validationBadge.setContentCompressionResistancePriority(.required, for: .horizontal)

        let nameRow = UIStackView(arrangedSubviews: [nameLabel, validationBadge])
        nameRow.axis = .horizontal
        nameRow.alignment = .center
        nameRow.spacing = 8

        membershipLabel.font = .preferredFont(forTextStyle: .subheadline)
        membershipLabel.adjustsFontForContentSizeCategory = true
        membershipLabel.numberOfLines = 0

        let infoStack = UIStackView(arrangedSubviews: [nameRow, membershipLabel])
        infoStack.axis = .vertical
        infoStack.alignment = .leading
        infoStack.spacing = 4

        profileRow.axis = .horizontal
        profileRow.alignment = .center
        profileRow.spacing = 14
        profileRow.addArrangedSubview(avatarContainer)
        profileRow.addArrangedSubview(infoStack)
        addBodyView(profileRow)
        addBodyView(profileDivider)
    }

    private func buildExpirySection() {
        expiryTitleLabel.text = "会员有效期"
        expiryTitleLabel.font = .preferredFont(forTextStyle: .subheadline)
        expiryTitleLabel.adjustsFontForContentSizeCategory = true
        registerMutedLabel(expiryTitleLabel)

        expiryValueLabel.font = .monospacedDigitSystemFont(ofSize: UIFont.preferredFont(forTextStyle: .subheadline).pointSize, weight: .regular)
        expiryValueLabel.adjustsFontForContentSizeCategory = true
        expiryValueLabel.textAlignment = .right
        registerPrimaryLabel(expiryValueLabel)

        expiryRow.axis = .horizontal
        expiryRow.alignment = .center
        expiryRow.spacing = 8
        expiryRow.addArrangedSubview(expiryTitleLabel)
        expiryRow.addArrangedSubview(expiryValueLabel)
        expiryRow.heightAnchor.constraint(greaterThanOrEqualToConstant: 48).isActive = true
        addBodyView(expiryRow)
    }

    private func buildSyncSection() {
        syncContainer.axis = .vertical
        syncContainer.spacing = 0

        syncTitleLabel.text = "自动同步"
        syncTitleLabel.font = .preferredFont(forTextStyle: .subheadline)
        syncTitleLabel.adjustsFontForContentSizeCategory = true
        registerMutedLabel(syncTitleLabel)

        syncFavoritesLabel.text = "收藏歌曲"
        syncFavoritesLabel.font = .preferredFont(forTextStyle: .body)
        syncFavoritesLabel.adjustsFontForContentSizeCategory = true
        registerPrimaryLabel(syncFavoritesLabel)
        syncFavoritesSwitch.accessibilityLabel = "收藏歌曲"
        syncFavoritesSwitch.accessibilityIdentifier = "music.accounts.detail.syncFavorites"
        syncFavoritesSwitch.changed = { [weak self] enabled in
            self?.actions.perform(.favorites(enabled))
        }
        registerThemeable(syncFavoritesSwitch)

        let favoritesRow = UIStackView(arrangedSubviews: [syncFavoritesLabel, syncFavoritesSwitch])
        favoritesRow.axis = .horizontal
        favoritesRow.alignment = .center
        favoritesRow.spacing = 12
        favoritesRow.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight).isActive = true

        syncPlaylistsLabel.text = "歌单"
        syncPlaylistsLabel.font = .preferredFont(forTextStyle: .body)
        syncPlaylistsLabel.adjustsFontForContentSizeCategory = true
        registerPrimaryLabel(syncPlaylistsLabel)
        syncPlaylistsSwitch.accessibilityLabel = "歌单"
        syncPlaylistsSwitch.accessibilityIdentifier = "music.accounts.detail.syncPlaylists"
        syncPlaylistsSwitch.changed = { [weak self] enabled in
            self?.actions.perform(.playlists(enabled))
        }
        registerThemeable(syncPlaylistsSwitch)

        let playlistsRow = UIStackView(arrangedSubviews: [syncPlaylistsLabel, syncPlaylistsSwitch])
        playlistsRow.axis = .horizontal
        playlistsRow.alignment = .center
        playlistsRow.spacing = 12
        playlistsRow.heightAnchor.constraint(greaterThanOrEqualToConstant: SettingsVisualMetrics.rowHeight).isActive = true

        syncConfirmButton = AccountCardButton(title: "继续同步", style: .primary, theme: theme) { [weak self] in
            self?.actions.confirmDestructiveSync()
        }
        syncConfirmButton.accessibilityIdentifier = "music.accounts.detail.confirmDestructiveSync"
        syncConfirmButton.isHidden = true
        registerThemeable(syncConfirmButton)

        syncResetButton = AccountCardButton(title: "重置同步状态", style: .destructive, theme: theme) { [weak self] in
            self?.actions.resetSync()
        }
        syncResetButton.accessibilityIdentifier = "music.accounts.detail.resetSync"
        syncResetButton.isHidden = true
        registerThemeable(syncResetButton)

        syncContainer.addArrangedSubview(syncTitleLabel)
        syncContainer.setCustomSpacing(4, after: syncTitleLabel)
        syncContainer.addArrangedSubview(favoritesRow)
        syncContainer.addArrangedSubview(syncDivider)
        syncContainer.addArrangedSubview(playlistsRow)
        syncContainer.setCustomSpacing(8, after: playlistsRow)
        syncContainer.addArrangedSubview(syncConfirmButton)
        syncContainer.setCustomSpacing(8, after: syncConfirmButton)
        syncContainer.addArrangedSubview(syncResetButton)
        registerThemeable(syncDivider)
        addBodyView(syncContainer)
    }

    private func buildActionButtons() {
        refreshProfileButton = AccountCardButton(title: "刷新资料", style: .primary, theme: theme) { [weak self] in
            self?.actions.perform(.refreshProfile)
        }
        refreshProfileButton.accessibilityIdentifier = "music.accounts.detail.refreshProfile"
        registerThemeable(refreshProfileButton)

        logoutButton = AccountCardButton(title: "退出登录", style: .destructive, theme: theme) { [weak self] in
            self?.actions.logout()
        }
        logoutButton.accessibilityIdentifier = "music.accounts.detail.logout"
        registerThemeable(logoutButton)

        buttonsRow.axis = .horizontal
        buttonsRow.distribution = .fillEqually
        buttonsRow.spacing = 12
        buttonsRow.addArrangedSubview(refreshProfileButton)
        buttonsRow.addArrangedSubview(logoutButton)
        addBodyView(buttonsRow)
    }

    private func bindState() {
        var publishers: [AnyPublisher<Void, Never>] = [
            state.$rows.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$syncActionBusy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$pendingAccounts.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.errorChanges,
        ]
        if let sync = state.sync {
            publishers.append(contentsOf: [
                sync.$state.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                sync.$selections.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                sync.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                sync.$isActive.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                sync.$automaticRevision.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
        }
        if let feedback = state.feedback {
            publishers.append(feedback.$value.dropFirst().map { _ -> Void in }.eraseToAnyPublisher())
        }
        Publishers.MergeMany(publishers)
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.render() }
            .store(in: &subscriptions)
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        let settings = theme.settings
        moreButton.configuration?.baseForegroundColor = UIColor(settings.muted)
        avatarContainer.backgroundColor = UIColor(settings.input)
        avatarPlaceholder.tintColor = UIColor(settings.muted)
        validationBadge.textColor = UIColor(settings.danger)
        validationBadge.backgroundColor = UIColor(settings.danger).withAlphaComponent(0.1)
        renderMembershipTone()
    }

    private func renderMembershipTone() {
        guard let summary = state.rows.first(where: { $0.id == platform }) else { return }
        let membership = summary.membership(at: Date())
        switch membership.tone {
        case .accent: membershipLabel.textColor = UIColor(theme.settings.accent)
        case .muted: membershipLabel.textColor = UIColor(theme.settings.muted)
        case .danger: membershipLabel.textColor = UIColor(theme.settings.danger)
        }
    }

    private func render() {
        guard isViewLoaded else { return }
        setErrorMessage(state.error)

        guard let summary = state.rows.first(where: { $0.id == platform }) else {
            moreButton.isHidden = true
            profileRow.isHidden = true
            profileDivider.isHidden = true
            expiryRow.isHidden = true
            syncContainer.isHidden = true
            buttonsRow.isHidden = true
            return
        }

        profileRow.isHidden = false
        profileDivider.isHidden = false
        expiryRow.isHidden = false
        buttonsRow.isHidden = false

        let account = summary.account
        let now = Date()
        let membership = summary.membership(at: now)
        let signedIn = account.phase == .signedIn
        let pending = state.pendingAccounts.contains(platform) || !state.isActive

        loadAvatar(account.profile?.avatarURL)
        nameLabel.text = summary.detail(at: now).name ?? platform.title
        if account.phase == .requiresValidation {
            validationBadge.text = "  \(account.phase.title)  "
            validationBadge.isHidden = false
        } else {
            validationBadge.isHidden = true
        }
        membershipLabel.text = membership.title
        renderMembershipTone()
        expiryValueLabel.text = membership.expiry

        if let sync = state.sync, sync.supportsAutomatic {
            syncContainer.isHidden = false
            let selection = sync.selection(platform)
            let ready = signedIn && !pending && sync.automaticRevision != nil && !sync.busy
            syncFavoritesSwitch.isOn = selection.favorites
            syncFavoritesSwitch.isEnabled = ready
            syncPlaylistsSwitch.isOn = selection.playlists
            syncPlaylistsSwitch.isEnabled = ready
            let showConfirm = state.needsDestructiveConfirmation(for: platform)
            syncConfirmButton.isHidden = !showConfirm
            syncConfirmButton.isEnabled = showConfirm && state.canSync && !pending
            let showReset = state.canResetSync(for: platform)
            syncResetButton.isHidden = !showReset
            syncResetButton.isEnabled = state.canExecuteSyncReset(for: platform)
        } else {
            syncContainer.isHidden = true
            syncConfirmButton.isHidden = true
            syncResetButton.isHidden = true
        }

        refreshProfileButton.isEnabled = signedIn && !pending && (state.accounts?.supportsProfileRefresh == true)
        logoutButton.isEnabled = !pending

        if let accounts = state.accounts {
            moreButton.isHidden = false
            moreButton.isEnabled = !pending
            let feedbackEnabled: Bool?
            if let feedback = state.feedback,
               account.platform == .netease || account.playbackFeedbackAvailable,
               let setting = feedback.value?.platforms[account.platform] {
                feedbackEnabled = setting.enabled
            } else {
                feedbackEnabled = nil
            }
            let signature = MoreMenuSignature(
                platform: account.platform,
                canRefreshSession: accounts.canRefreshSession(account.platform),
                canSync: signedIn && state.canSync,
                canResetSync: state.canExecuteSyncReset(for: account.platform),
                feedbackEnabled: feedbackEnabled,
                showsRecommendations: signedIn && account.platform != .netease
            )
            if displayedMoreMenu != signature {
                displayedMoreMenu = signature
                moreButton.menu = buildMoreMenu(accounts: accounts, account: account, signedIn: signedIn)
            }
        } else {
            displayedMoreMenu = nil
            moreButton.menu = nil
            moreButton.isHidden = true
        }
    }

    private func buildMoreMenu(
        accounts: MusicAccountsViewModel,
        account: MusicAccountStatus,
        signedIn: Bool
    ) -> UIMenu {
        var primary: [UIMenuElement] = [
            UIAction(title: "验证登录会话") { [weak self] _ in
                self?.actions.perform(.validate)
            }
        ]
        if accounts.canRefreshSession(account.platform) {
            primary.append(UIAction(title: "刷新登录会话") { [weak self] _ in
                self?.actions.perform(.refreshSession)
            })
        }
        if signedIn, state.canSync {
            primary.append(UIAction(title: "立即同步") { [weak self] _ in
                self?.actions.perform(.synchronize)
            })
        }
        if state.canExecuteSyncReset(for: account.platform) {
            primary.append(UIAction(title: "重置同步状态", attributes: .destructive) { [weak self] _ in
                self?.actions.resetSync()
            })
        }
        if let feedback = state.feedback,
           account.platform == .netease || account.playbackFeedbackAvailable,
           let setting = feedback.value?.platforms[account.platform] {
            primary.append(UIAction(
                title: "播放上报",
                state: setting.enabled ? .on : .off
            ) { [weak self] _ in
                self?.actions.perform(.feedback(!setting.enabled))
            })
        }
        if signedIn, account.platform != .netease {
            let choices: [(String, String)] = account.platform == .qq
                ? [("guess", "猜你喜欢"), ("radar", "雷达推荐")]
                : [("daily", "每日推荐"), ("fm", "红心 Radio · 本次推荐")]
            let recMenu = UIMenu(options: .displayInline, children: choices.map { kind, title in
                UIAction(title: title) { [weak self] _ in
                    self?.actions.recommend(kind, title)
                }
            })
            primary.append(recMenu)
        }
        return UIMenu(children: primary)
    }

    private func loadAvatar(_ url: URL?) {
        guard url != currentAvatarURL else { return }
        currentAvatarURL = url
        cancelDialogTask(key: "avatar")
        avatarImageView.image = nil
        guard let url else { return }
        let images = artworkImages
        runDialogTask(key: "avatar") { [weak self] in
            guard let result = try? await images.read(url), !Task.isCancelled else { return }
            guard let self, self.currentAvatarURL == url else { return }
            self.avatarImageView.image = result.0
        }
    }

    // MARK: - DebugDialogControllable

    override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        var items = [
            DebugDialogActionDescriptor(index: 0, label: "关闭", kind: "cancel", enabled: true),
            DebugDialogActionDescriptor(index: 1, label: "刷新资料", kind: "default", enabled: refreshProfileButton?.isEnabled ?? false),
            DebugDialogActionDescriptor(index: 2, label: "退出登录", kind: "destructive", enabled: logoutButton?.isEnabled ?? false),
        ]
        if let button = syncConfirmButton, !button.isHidden {
            items.append(DebugDialogActionDescriptor(index: items.count, label: "继续同步", kind: "default", enabled: button.isEnabled))
        }
        if let button = syncResetButton, !button.isHidden {
            items.append(DebugDialogActionDescriptor(index: items.count, label: "重置同步状态", kind: "destructive", enabled: button.isEnabled))
        }
        return items
    }

    override func performDebugDialogAction(_ action: DebugDialogActionDescriptor) throws {
        switch action.label {
        case "关闭": actions.close()
        case "刷新资料": actions.perform(.refreshProfile)
        case "退出登录": actions.logout()
        case "继续同步": actions.confirmDestructiveSync()
        case "重置同步状态": actions.resetSync()
        default: break
        }
    }
}
#endif

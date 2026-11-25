#if os(iOS) || os(macOS)
import Foundation
import Combine

struct MusicAccountSettingsSummary: Equatable, Identifiable {
    let account: MusicAccountStatus
    var id: MusicAccountPlatform { account.platform }

    func detail(at date: Date) -> MusicAccountSettingsDetail {
        guard account.phase == .signedIn else {
            return .init(status: account.phase.title)
        }
        let nickname = account.nickname.trimmingCharacters(in: .whitespacesAndNewlines)
        let profileName = account.profile?.nickname.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let name = nickname.isEmpty ? (profileName.isEmpty ? account.phase.title : profileName) : nickname
        guard let membership = account.profile?.membership, let vip = membership.vip else {
            return .init(name: name, status: "会员状态未知")
        }
        if !vip || membership.expiresAt.map({ $0 <= date }) == true {
            return .init(name: name, status: "无VIP", isNonVIP: true)
        }
        guard let expiresAt = membership.expiresAt else {
            return .init(name: name, status: "VIP · 到期时间未知")
        }
        return .init(name: name, status: Self.day(expiresAt) + " 到期")
    }

    /// Detail-card membership; unknown fields stay unknown and never read as non-VIP.
    func membership(at date: Date) -> MusicAccountSettingsMembership {
        guard let membership = account.profile?.membership, let vip = membership.vip else {
            return .init(title: "会员状态未知", tone: .muted, expiry: "—")
        }
        if !vip || membership.expiresAt.map({ $0 <= date }) == true {
            return .init(title: "无VIP", tone: .danger, expiry: "—")
        }
        let tier = membership.tier.trimmingCharacters(in: .whitespacesAndNewlines)
        return .init(title: tier.isEmpty ? "VIP" : tier, tone: .accent,
                     expiry: membership.expiresAt.map(Self.day) ?? "—")
    }

    private static func day(_ date: Date) -> String {
        date.formatted(Date.ISO8601FormatStyle(timeZone: .current).year().month().day())
    }
}

struct MusicAccountSettingsDetail: Equatable {
    var name: String?
    let status: String
    var isNonVIP = false
}

struct MusicAccountSettingsMembership: Equatable {
    enum Tone { case accent, muted, danger }
    let title: String
    let tone: Tone
    let expiry: String
}

enum MusicAccountSettingsAction {
    case validate, refreshSession, refreshProfile, logout
    case favorites(Bool), playlists(Bool), synchronize, feedback(Bool)
}

struct MusicAccountSettingsContext {
    let platform: MusicAccountPlatform
    let sessionRevision: UUID?
    let phase: MusicAccountStatus.Phase
}

@MainActor
final class MusicAccountsSettingsState: ObservableObject {
    @Published private(set) var rows: [MusicAccountSettingsSummary] = []
    @Published private(set) var busy = false
    @Published private(set) var syncActionBusy = false
    @Published private(set) var pendingAccounts = Set<MusicAccountPlatform>()
    let accounts: MusicAccountsViewModel?
    let sync: MusicLibrarySyncViewModel?
    let feedback: MusicPlaybackFeedbackSettingsModel?
    private let owner: MusicViewModel
    private var started = false
    private var activatedOnce = false
    private var subscriptions: Set<AnyCancellable> = []
    private var loadTask: Task<Void, Never>?
    private var syncTask: Task<Void, Never>? {
        didSet { syncActionBusy = syncTask != nil }
    }
    private var sessionRevisions: [MusicAccountPlatform: UUID] = [:]
    private var reloadRequested = false
    private var loadingInitialSettings = false
    private var lastNeedsDestructiveConfirmation = false
    var onNeedsDestructiveConfirmation: (() -> Void)?

    var isActive: Bool { started && !owner.isClosed }
    var error: String? { accounts?.error ?? sync?.error ?? feedback?.error }
    var canSync: Bool { !syncActionBusy && sync?.canSync == true }
    var needsDestructiveConfirmation: Bool { sync?.needsDestructiveConfirmation == true }
    func needsDestructiveConfirmation(for platform: MusicAccountPlatform) -> Bool {
        guard let sync, sync.needsDestructiveConfirmation else { return false }
        let items = sync.state.issues.filter { $0.code == "emptyRemote" || $0.code == "bulkRemoteDelete" }
        let specific = items.compactMap(\.platform)
        guard !specific.isEmpty else { return true }
        return specific.contains(platform)
    }
    var destructiveConfirmationSummary: String {
        guard let sync else { return "" }
        let items = sync.state.issues.filter { $0.code == "emptyRemote" || $0.code == "bulkRemoteDelete" }
        var seen = Set<String>()
        let parts = items.compactMap { issue -> String? in
            let text = [issue.platform?.title, issue.title].compactMap { $0 }.joined(separator: " · ")
            return seen.insert(text).inserted ? text : nil
        }
        return parts.isEmpty ? "远端歌单大幅变化，需确认后同步" : parts.joined(separator: "\n")
    }
    func syncBlockedIssue(for platform: MusicAccountPlatform) -> MusicLibrarySyncIssue? {
        guard let sync, sync.state.phase == .blocked else { return nil }
        if let direct = sync.state.issues.first(where: {
            $0.platform == platform &&
                ($0.code == "accountChanged" || $0.code == "automaticBindingRequired" || $0.code == "unconfirmed")
        }) {
            return direct
        }
        return sync.state.issues.first(where: {
            $0.platform == nil && ($0.code == "accountChanged" || $0.code == "automaticBindingRequired")
        })
    }
    func canResetSync(for platform: MusicAccountPlatform) -> Bool {
        guard let sync, sync.state.phase == .blocked else { return false }
        if syncBlockedIssue(for: platform) != nil { return true }
        return sync.state.hasUnresolvedFences && !sync.selection(platform).kinds.isEmpty
    }
    func canExecuteSyncReset(for platform: MusicAccountPlatform) -> Bool {
        isActive && syncTask == nil && !pendingAccounts.contains(platform) &&
            sync?.busy == false && canResetSync(for: platform)
    }
    var errorChanges: AnyPublisher<Void, Never> {
        let accountsError = accounts?.$error.dropFirst().eraseToAnyPublisher() ?? Empty<String?, Never>().eraseToAnyPublisher()
        let syncError = sync?.$error.dropFirst().eraseToAnyPublisher() ?? Empty<String?, Never>().eraseToAnyPublisher()
        let feedbackError = feedback?.$error.dropFirst().eraseToAnyPublisher() ?? Empty<String?, Never>().eraseToAnyPublisher()
        return Publishers.Merge3(accountsError, syncError, feedbackError).map { _ -> Void in }.eraseToAnyPublisher()
    }

    init(owner: MusicViewModel) {
        self.owner = owner
        accounts = owner.accounts.map { service in
            MusicAccountsViewModel(service: service, onSessionChanged: { [weak owner] platform in
                owner?.accountSessionsChanged(platform)
            })
        }
        sync = owner.librarySync.map {
            MusicLibrarySyncViewModel(service: $0, accounts: owner.accounts,
                                      settingsReceipts: owner.settingsReceipts) { [weak owner] in
                await owner?.load()
            }
        }
        feedback = owner.playbackFeedbackSettings.map {
            MusicPlaybackFeedbackSettingsModel(service: $0, receipts: owner.settingsReceipts) { [weak owner] in
                try await owner?.reconcilePlaybackFeedbackSettings()
            }
        }
        let statuses = accounts?.$rows.dropFirst().eraseToAnyPublisher() ?? Empty<[MusicAccountStatus], Never>().eraseToAnyPublisher()
        statuses.map { $0.map { MusicAccountSettingsSummary(account: $0) } }
            .removeDuplicates()
            .sink { [weak self] in self?.rows = $0 }
            .store(in: &subscriptions)
        if let accounts {
            accounts.$isBusy.dropFirst().removeDuplicates().sink { [weak self] in self?.busy = $0 }
                .store(in: &subscriptions)
            accounts.$statusesRevision.dropFirst().sink { [weak self] _ in
                guard let self, !pendingAccounts.isEmpty else { return }
                pendingAccounts = []
            }
            .store(in: &subscriptions)
        }
        if let sync {
            sync.$state.dropFirst().sink { [weak self] _ in
                guard let self else { return }
                let current = sync.needsDestructiveConfirmation
                let previous = self.lastNeedsDestructiveConfirmation
                self.lastNeedsDestructiveConfirmation = current
                if !self.loadingInitialSettings, self.isActive, current, !previous {
                    self.onNeedsDestructiveConfirmation?()
                }
            }
            .store(in: &subscriptions)
        }
        owner.settingsChanges.receive(on: RunLoop.main).sink { [weak self] in
            guard let self, isActive else { return }
            feedback?.settingsChanged(revision: owner.state.settingsRevision)
        }
        .store(in: &subscriptions)
    }

    func start() {
        started = true
        reconcileAccess()
    }

    func reconcileAccess() {
        guard started else { return }
        let open = !owner.isClosed
        if !open {
            loadTask?.cancel(); loadTask = nil
            syncTask?.cancel(); syncTask = nil
            reloadRequested = false
            lastNeedsDestructiveConfirmation = false
            sessionRevisions = [:]; pendingAccounts = []
            accounts?.deactivate()
            sync?.deactivate()
            feedback?.deactivate()
            return
        }
        guard !activatedOnce else { return }
        activatedOnce = true
        accounts?.activate()
        sync?.activate()
        feedback?.activate()
        loadSettings()
    }

    func retry() {
        guard isActive else { return }
        if !pendingAccounts.isEmpty {
            accounts?.error = nil
            accounts?.reloadStatuses()
        } else if accounts?.error != nil {
            accounts?.reload()
        }
        if sync?.error != nil || feedback?.error != nil { loadSettings() }
    }

    func accountSessionChanged(_ platform: MusicAccountPlatform) {
        guard isActive else { return }
        sessionRevisions[platform] = UUID()
        pendingAccounts.insert(platform)
        if platform == .kugou { feedback?.dismissConfirmation() }
        accounts?.reloadStatuses()
    }

    func context(for row: MusicAccountStatus) -> MusicAccountSettingsContext? {
        guard isActive, !pendingAccounts.contains(row.platform),
              accounts?.rows.first(where: { $0.platform == row.platform })?.phase == row.phase else { return nil }
        return .init(platform: row.platform, sessionRevision: sessionRevisions[row.platform], phase: row.phase)
    }

    func isCurrent(_ context: MusicAccountSettingsContext) -> Bool {
        isActive && !pendingAccounts.contains(context.platform) &&
            sessionRevisions[context.platform] == context.sessionRevision &&
            accounts?.rows.first(where: { $0.platform == context.platform })?.phase == context.phase
    }

    func perform(_ action: MusicAccountSettingsAction, context: MusicAccountSettingsContext) {
        guard isCurrent(context) else { return }
        let platform = context.platform
        switch action {
        case .validate: accounts?.validate(platform)
        case .refreshSession: accounts?.refreshSession(platform)
        case .refreshProfile: accounts?.refreshProfile(platform)
        case .logout: accounts?.logout(platform)
        case .favorites(let enabled): sync?.select(platform, favorites: enabled)
        case .playlists(let enabled): sync?.select(platform, playlists: enabled)
        case .feedback(let enabled): feedback?.update(platform, enabled)
        case .synchronize:
            guard syncTask == nil, let sync else { return }
            if sync.needsDestructiveConfirmation {
                onNeedsDestructiveConfirmation?()
                return
            }
            syncTask = Task { [weak self] in
                defer { self?.syncTask = nil }
                guard self?.isCurrent(context) == true, !Task.isCancelled else { return }
                await sync.synchronize()
            }
        }
    }

    func confirmDestructiveSynchronize() {
        guard isActive, syncTask == nil, let sync, sync.canSync else { return }
        syncTask = Task { [weak self] in
            defer { self?.syncTask = nil }
            guard let self, self.isActive, !Task.isCancelled else { return }
            await sync.synchronize(confirmDestructive: true)
        }
    }

    func resetSyncAccount(_ platform: MusicAccountPlatform) {
        guard isActive, syncTask == nil, let sync, !sync.busy else { return }
        syncTask = Task { [weak self] in
            defer { self?.syncTask = nil }
            guard let self, self.isActive, !Task.isCancelled else { return }
            await sync.resetAccount(platform)
        }
    }

    func close() {
        started = false; activatedOnce = false; reloadRequested = false; loadingInitialSettings = false
        lastNeedsDestructiveConfirmation = false
        loadTask?.cancel(); loadTask = nil
        syncTask?.cancel(); syncTask = nil
        sessionRevisions = [:]; pendingAccounts = []
        accounts?.deactivate(); sync?.deactivate()
        feedback?.deactivate()
    }

    private func loadSettings() {
        guard isActive else { return }
        guard loadTask == nil else {
            reloadRequested = true
            return
        }
        loadingInitialSettings = true
        loadTask = Task { [weak self] in
            guard let self else { return }
            defer {
                loadingInitialSettings = false
                loadTask = nil
                if reloadRequested {
                    reloadRequested = false
                    loadSettings()
                }
            }
            guard isActive, !Task.isCancelled else { return }
            await sync?.load()
            self.lastNeedsDestructiveConfirmation = self.sync?.needsDestructiveConfirmation ?? false
            guard isActive, !Task.isCancelled else { return }
            await feedback?.load()
        }
    }

    deinit { loadTask?.cancel(); syncTask?.cancel() }
}
#endif

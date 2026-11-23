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
    @Published private(set) var pendingAccounts = Set<MusicAccountPlatform>()
    let accounts: MusicAccountsViewModel?
    let sync: MusicLibrarySyncViewModel?
    let feedback: MusicPlaybackFeedbackSettingsModel?
    private let owner: MusicViewModel
    private var started = false
    private var activatedOnce = false
    private var subscriptions: Set<AnyCancellable> = []
    private var loadTask: Task<Void, Never>?
    private var syncTask: Task<Void, Never>?
    private var sessionRevisions: [MusicAccountPlatform: UUID] = [:]
    private var reloadRequested = false

    var isActive: Bool { started && !owner.isClosed }
    var error: String? { accounts?.error ?? sync?.error ?? feedback?.error }
    var canSync: Bool { syncTask == nil && sync?.canSync == true }
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
            MusicPlaybackFeedbackSettingsModel(service: $0, receipts: owner.settingsReceipts) { [weak owner] generation in
                try await owner?.reconcilePlaybackFeedbackSettings(accessGeneration: generation)
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
            loadTask?.cancel(); syncTask?.cancel(); reloadRequested = false
            sessionRevisions = [:]; pendingAccounts = []
            accounts?.setAccess(isAvailable: false, accessGeneration: owner.accountAccessGeneration)
            sync?.activate(available: false, generation: owner.accountAccessGeneration)
            feedback?.setAccess(available: false, generation: owner.accountAccessGeneration)
            return
        }
        guard !activatedOnce else { return }
        activatedOnce = true
        let access = owner.accountAccessGeneration
        accounts?.activate(isAvailable: true, accessGeneration: access)
        sync?.activate(available: true, generation: access)
        feedback?.setAccess(available: true, generation: access)
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
            syncTask = Task { [weak self] in
                defer { self?.syncTask = nil }
                guard self?.isCurrent(context) == true, !Task.isCancelled else { return }
                await sync.synchronize()
            }
        }
    }

    func close() {
        started = false; activatedOnce = false; reloadRequested = false
        loadTask?.cancel(); syncTask?.cancel()
        sessionRevisions = [:]; pendingAccounts = []
        accounts?.deactivate(); sync?.deactivate()
        feedback?.setAccess(available: false, generation: owner.accountAccessGeneration)
    }

    private func loadSettings() {
        guard isActive else { return }
        guard loadTask == nil else {
            reloadRequested = true
            return
        }
        loadTask = Task { [weak self] in
            guard let self else { return }
            defer {
                loadTask = nil
                if reloadRequested {
                    reloadRequested = false
                    loadSettings()
                }
            }
            guard isActive, !Task.isCancelled else { return }
            await sync?.load()
            guard isActive, !Task.isCancelled else { return }
            await feedback?.load()
        }
    }

    deinit { loadTask?.cancel(); syncTask?.cancel() }
}
#endif

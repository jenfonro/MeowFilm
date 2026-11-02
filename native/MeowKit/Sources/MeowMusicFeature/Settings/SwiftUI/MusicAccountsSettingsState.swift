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
    let accessGeneration: UUID
    let sessionRevision: UUID?
    let phase: MusicAccountStatus.Phase
}

@MainActor
final class MusicAccountsSettingsState: ObservableObject {
    @Published private(set) var rows: [MusicAccountSettingsSummary] = []
    @Published private(set) var available = false
    @Published private(set) var busy = false
    @Published private(set) var pendingAccounts = Set<MusicAccountPlatform>()
    let accounts: MusicAccountsViewModel?
    let sync: MusicLibrarySyncViewModel?
    let feedback: MusicPlaybackFeedbackSettingsModel?
    let hasCredentialsImport: Bool
    private let owner: MusicViewModel
    private var started = false
    private var generation: UUID?
    private var subscriptions: Set<AnyCancellable> = []
    private var loadTask: Task<Void, Never>?
    private var syncTask: Task<Void, Never>?
    private var sessionRevisions: [MusicAccountPlatform: UUID] = [:]
    private var reloadRequested = false

    var error: String? { accounts?.error ?? sync?.error ?? feedback?.error }
    var canSync: Bool { syncTask == nil && sync?.canSync == true }
    var errorChanges: AnyPublisher<Void, Never> {
        let accountsError = accounts?.$error.eraseToAnyPublisher() ?? Just<String?>(nil).eraseToAnyPublisher()
        let syncError = sync?.$error.eraseToAnyPublisher() ?? Just<String?>(nil).eraseToAnyPublisher()
        let feedbackError = feedback?.$error.eraseToAnyPublisher() ?? Just<String?>(nil).eraseToAnyPublisher()
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
            MusicLibrarySyncViewModel(service: $0, accounts: owner.accounts) { [weak owner] in
                await owner?.load()
            }
        }
        feedback = owner.playbackFeedbackSettings.map {
            MusicPlaybackFeedbackSettingsModel(service: $0) { [weak owner] generation in
                try await owner?.reconcilePlaybackFeedbackSettings(accessGeneration: generation)
            }
        }
        hasCredentialsImport = owner.accounts is any MusicCredentialsImporting
        let statuses = accounts?.$rows.eraseToAnyPublisher() ?? Just<[MusicAccountStatus]>([]).eraseToAnyPublisher()
        statuses.map { $0.map { MusicAccountSettingsSummary(account: $0) } }
            .removeDuplicates()
            .sink { [weak self] in self?.rows = $0 }
            .store(in: &subscriptions)
        if let accounts {
            accounts.$isBusy.removeDuplicates().sink { [weak self] in self?.busy = $0 }
                .store(in: &subscriptions)
            accounts.$statusesRevision.dropFirst().sink { [weak self] _ in
                guard let self, !pendingAccounts.isEmpty else { return }
                pendingAccounts = []
            }
            .store(in: &subscriptions)
        }
        owner.settingsChanges.receive(on: RunLoop.main).sink { [weak self] in
            guard let self, canAct(generation: owner.accountAccessGeneration) else { return }
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
        let next = owner.isAvailable && !owner.isClosed
        let access = owner.accountAccessGeneration
        guard available != next || generation != access else { return }
        let first = generation == nil
        generation = access; available = next
        loadTask?.cancel(); syncTask?.cancel(); reloadRequested = false
        sessionRevisions = [:]; pendingAccounts = []
        if first { accounts?.activate(isAvailable: next, accessGeneration: access) }
        else { accounts?.setAccess(isAvailable: next, accessGeneration: access) }
        sync?.activate(available: next, generation: access)
        feedback?.setAccess(available: next, generation: access)
        if next { loadSettings() }
    }

    func canAct(generation: UUID) -> Bool {
        started && available && owner.isAvailable && !owner.isClosed &&
            self.generation == generation && owner.accountAccessGeneration == generation
    }

    func retry(generation: UUID) {
        guard canAct(generation: generation) else { return }
        if !pendingAccounts.isEmpty {
            accounts?.error = nil
            accounts?.reloadStatuses()
        } else if accounts?.error != nil {
            accounts?.reload()
        }
        if sync?.error != nil || feedback?.error != nil { loadSettings() }
    }

    func accountSessionChanged(_ platform: MusicAccountPlatform) {
        guard canAct(generation: owner.accountAccessGeneration) else { return }
        sessionRevisions[platform] = UUID()
        pendingAccounts.insert(platform)
        if platform == .kugou { feedback?.dismissConfirmation() }
        accounts?.reloadStatuses()
    }

    func context(for row: MusicAccountStatus) -> MusicAccountSettingsContext? {
        let access = owner.accountAccessGeneration
        guard canAct(generation: access), !pendingAccounts.contains(row.platform),
              accounts?.rows.first(where: { $0.platform == row.platform })?.phase == row.phase else { return nil }
        return .init(platform: row.platform, accessGeneration: access,
                     sessionRevision: sessionRevisions[row.platform], phase: row.phase)
    }

    func isCurrent(_ context: MusicAccountSettingsContext) -> Bool {
        canAct(generation: context.accessGeneration) && !pendingAccounts.contains(context.platform) &&
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
        started = false; available = false; reloadRequested = false
        loadTask?.cancel(); syncTask?.cancel()
        sessionRevisions = [:]; pendingAccounts = []
        accounts?.deactivate(); sync?.deactivate()
        feedback?.setAccess(available: false, generation: generation)
    }

    private func loadSettings() {
        guard started, available else { return }
        guard loadTask == nil else {
            reloadRequested = true
            return
        }
        let access = generation
        loadTask = Task { [weak self] in
            guard let self else { return }
            defer {
                loadTask = nil
                if reloadRequested {
                    reloadRequested = false
                    loadSettings()
                }
            }
            guard let access, canAct(generation: access), !Task.isCancelled else { return }
            await sync?.load()
            guard canAct(generation: access), !Task.isCancelled else { return }
            await feedback?.load()
        }
    }

    deinit { loadTask?.cancel(); syncTask?.cancel() }
}
#endif

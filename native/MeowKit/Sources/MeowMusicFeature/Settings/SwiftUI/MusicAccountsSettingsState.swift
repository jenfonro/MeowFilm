#if os(iOS) || os(macOS)
import Foundation
import Combine

struct MusicAccountSettingsSummary: Equatable, Identifiable {
    let account: MusicAccountStatus
    let automaticSync: Bool
    let playbackFeedback: Bool
    var id: MusicAccountPlatform { account.platform }

    var detail: String {
        var values = [account.nickname, account.phase.title].filter { !$0.isEmpty }
        if account.phase == .signedIn, let membership = account.profile?.membership {
            values.append(membership.title(at: Date()))
        }
        if automaticSync { values.append("自动同步") }
        if playbackFeedback { values.append("播放上报") }
        return values.joined(separator: " · ")
    }
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
        let selections = sync?.$selections.eraseToAnyPublisher()
            ?? Just<[MusicLibrarySyncSelection]>([]).eraseToAnyPublisher()
        let settings = feedback?.$value.eraseToAnyPublisher()
            ?? Just<MusicPlaybackFeedbackSettings?>(nil).eraseToAnyPublisher()
        Publishers.CombineLatest3(statuses, selections, settings)
            .map { statuses, selections, settings in
                statuses.map { account in
                    let selection = selections.first { $0.platform == account.platform }
                    return MusicAccountSettingsSummary(
                        account: account,
                        automaticSync: selection?.favorites == true || selection?.playlists == true,
                        playbackFeedback: settings?.platforms[account.platform]?.enabled == true
                    )
                }
            }
            .removeDuplicates()
            .sink { [weak self] in self?.rows = $0 }
            .store(in: &subscriptions)
        if let accounts {
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

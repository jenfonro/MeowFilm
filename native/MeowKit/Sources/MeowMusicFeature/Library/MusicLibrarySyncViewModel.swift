#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor public final class MusicLibrarySyncViewModel: ObservableObject {
    @Published public private(set) var selections: [MusicLibrarySyncSelection] = []
    @Published public private(set) var state = MusicLibrarySyncState()
    @Published public private(set) var accounts: [MusicAccountStatus] = []
    @Published public private(set) var busy = false
    @Published public private(set) var isAvailable = false
    @Published public private(set) var error: String?
    private let service: any MusicLibrarySyncing
    private let accountService: (any MusicAccountsServing)?
    private let settingsReceipts: MusicSettingsReceiptOwner
    private let onLibraryChanged: @MainActor () async -> Void
    private var automatic: (any MusicAutomaticLibrarySyncing)? { service as? any MusicAutomaticLibrarySyncing }
    public var supportsAutomatic: Bool { automatic != nil }
    @Published public private(set) var automaticRevision: Int?
    private var writeAuthorization: MusicQueueAuthorization?
    private var completionTask: Task<Void, Never>?
    private var epoch = UUID()
    private var accessGeneration = UUID()
    private var active = false
    private var syncID: UUID?
    private var syncTask: Task<MusicLibrarySyncState, Error>?
    private var settingsTask: Task<MusicAutomaticLibrarySyncSaveResult, Error>?
    private var loadID = UUID()

    public init(service: any MusicLibrarySyncing, accounts: (any MusicAccountsServing)? = nil,
                settingsReceipts: MusicSettingsReceiptOwner,
                onLibraryChanged: @escaping @MainActor () async -> Void = {}) {
        self.service = service; accountService = accounts; self.settingsReceipts = settingsReceipts
        self.onLibraryChanged = onLibraryChanged
    }
    public var canSync: Bool {
        active && isAvailable && !busy && settingsTask == nil && completionTask == nil &&
            selections.contains { !$0.kinds.isEmpty }
    }
    public func selection(_ platform: MusicAccountPlatform) -> MusicLibrarySyncSelection {
        selections.first { $0.platform == platform } ?? .init(platform: platform)
    }
    public func select(_ platform: MusicAccountPlatform, favorites: Bool? = nil, playlists: Bool? = nil) {
        guard active, isAvailable, !busy, settingsTask == nil,
              !supportsAutomatic || automaticRevision != nil else { return }
        loadID = UUID() // A late status load must not undo this explicit choice.
        var value = selection(platform)
        if let favorites { value.favorites = favorites }
        if let playlists { value.playlists = playlists }
        selections.removeAll { $0.platform == platform }; selections.append(value)
        if let automatic, let revision = automaticRevision {
            let own = epoch, access = accessGeneration, id = UUID(), permission = MusicQueueAuthorization()
            let preferences = MusicAutomaticLibrarySyncPreferences(revision: revision, selections: selections)
            completionTask?.cancel(); completionTask = nil
            busy = true; error = nil
            let keys = [
                favorites == nil ? nil : "library/automatic/" + platform.rawValue + "/favorites",
                playlists == nil ? nil : "library/automatic/" + platform.rawValue + "/playlists"
            ].compactMap { $0 }
            let task = settingsReceipts.submit(settings: keys, operation: "保存自动同步设置") {
                do { return try await automatic.saveAutomaticLibrarySync(preferences, requestID: id, authorization: permission) }
                catch { permission.revoke(); throw error }
            }
            settingsTask = task
            Task { [weak self] in
                let result = await task.result
                guard let self else { return }
                settingsTask = nil
                guard current(own) else {
                    if active, isAvailable {
                        await load()
                        if accessGeneration == access, case .success(let receipt) = result,
                           automaticRevision == receipt.preferences.revision {
                            observeSettingsSynchronization(receipt, own: epoch)
                        }
                    }
                    return
                }
                busy = false
                switch result {
                case .success(let receipt):
                    selections = receipt.preferences.selections
                    automaticRevision = receipt.preferences.revision
                    error = nil
                    observeSettingsSynchronization(receipt, own: own)
                case .failure(let failure):
                    let problem = failure as? MusicServiceError ?? MusicServiceError.sanitized(failure)
                    error = problem == .commitUnconfirmed ? nil : problem.localizedDescription
                    // Read once on the live page, never replay an uncertain save.
                    await load()
                    if current(own), problem != .commitUnconfirmed { error = problem.localizedDescription }
                }
            }
        }
    }
    private func observeSettingsSynchronization(_ receipt: MusicAutomaticLibrarySyncSaveResult, own: UUID) {
        completionTask?.cancel()
        completionTask = Task { [weak self] in
            let result = await receipt.synchronization.value
            guard let self, current(own), automaticRevision == receipt.preferences.revision else { return }
            completionTask = nil
            switch result {
            case .success(let value): state = value; error = nil
            case .failure(let failure): error = failure.localizedDescription
            }
            await onLibraryChanged()
        }
    }
    public func activate(available: Bool, generation: UUID) {
        if !active || accessGeneration != generation || isAvailable != available {
            cancel(); epoch = UUID(); loadID = UUID()
            accounts = []; state = .init(); selections = []; error = nil; automaticRevision = nil
        }
        active = true; accessGeneration = generation; isAvailable = available
    }
    public func deactivate() {
        active = false; isAvailable = false; epoch = UUID(); loadID = UUID()
        cancel(); accounts = []; state = .init(); selections = []; error = nil; automaticRevision = nil
    }
    public func cancel() {
        epoch = UUID(); loadID = UUID()
        writeAuthorization?.revoke(); writeAuthorization = nil
        completionTask?.cancel(); completionTask = nil
        syncTask?.cancel(); syncTask = nil; busy = false
        if let id = syncID {
            let service = service
            Task { await service.cancelLibrarySync(id: id) }
        }
        syncID = nil
    }
    private func current(_ expected: UUID) -> Bool { active && isAvailable && epoch == expected && !Task.isCancelled }
    public func load() async {
        guard active, isAvailable, !busy else { return }
        let own = epoch, request = UUID(); loadID = request
        do {
            let status = try await service.librarySyncState()
            guard current(own), loadID == request else { return }
            let preferences = try await automatic?.automaticLibrarySyncPreferences()
            guard current(own), loadID == request else { return }
            let rows = try await accountService?.statuses() ?? []
            guard current(own), loadID == request else { return }
            state = status; selections = preferences?.selections ?? status.selections
            automaticRevision = preferences?.revision; accounts = rows; error = nil
        } catch {
            guard current(own), loadID == request else { return }
            self.error = MusicServiceError.sanitized(error).errorDescription
        }
    }
    public var needsDestructiveConfirmation: Bool {
        state.phase == .blocked && state.issues.contains { $0.code == "emptyRemote" || $0.code == "bulkRemoteDelete" }
    }
    public func resetAccount(_ platform: MusicAccountPlatform) async {
        guard active, isAvailable, !busy, settingsTask == nil else { return }
        let own = epoch
        loadID = UUID(); busy = true; error = nil
        defer { if current(own) { busy = false } }
        do {
            let next = try await service.resetSyncAccount(for: platform)
            guard current(own) else { return }
            state = next
            if let preferences = try await automatic?.automaticLibrarySyncPreferences() {
                guard current(own) else { return }
                selections = preferences.selections
                automaticRevision = preferences.revision
            }
            error = nil
            await onLibraryChanged()
        } catch {
            guard current(own) else { return }
            self.error = MusicServiceError.sanitized(error).errorDescription
        }
    }
    public func synchronize(confirmDestructive: Bool = false) async {
        guard canSync else { return }
        let own = epoch, id = UUID(), service = service
        let request = MusicLibrarySyncRequest(
            id: id,
            selections: selections.filter { !$0.kinds.isEmpty },
            confirmDestructive: confirmDestructive
        )
        loadID = UUID(); syncID = id; busy = true; error = nil
        if let automatic, !confirmDestructive {
            let permission = MusicQueueAuthorization()
            writeAuthorization = permission
            let automaticRequest = MusicAutomaticLibrarySyncRequest(id: id, reason: .manual,
                preferenceRevision: automaticRevision, authorization: permission)
            let task = Task { try await automatic.synchronizeAutomaticLibrary(automaticRequest) }
            syncTask = task
            await finishAutomatic(task, own: own, id: id, permission: permission)
            return
        }
        let task = Task { try await service.synchronizeLibrary(request) }; syncTask = task
        defer { if syncID == id { syncID = nil; syncTask = nil; busy = false } }
        do {
            let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            guard current(own), syncID == id else { return }
            state = result
            await onLibraryChanged()
        } catch {
            guard current(own), syncID == id else { return }
            self.error = MusicServiceError.sanitized(error).errorDescription
        }
    }
    private func finishAutomatic(_ task: Task<MusicLibrarySyncState, Error>, own: UUID, id: UUID,
                                 permission: MusicQueueAuthorization) async {
        defer {
            permission.revoke()
            if syncID == id {
                syncID = nil; syncTask = nil; completionTask = nil; writeAuthorization = nil; busy = false
            }
        }
        var failure: String?
        do {
            let result = try await withTaskCancellationHandler { try await task.value } onCancel: {
                permission.revoke(); task.cancel()
            }
            guard current(own), syncID == id else { return }
            state = result
        } catch {
            guard current(own), syncID == id else { return }
            failure = MusicServiceError.sanitized(error).errorDescription
        }
        guard current(own), syncID == id else { return }
        // Settings can be durable even when the following network round fails.
        // Read actual preferences; never visually roll back a saved OFF switch.
        do {
            let preferences = try await automatic?.automaticLibrarySyncPreferences()
            let status = try await service.librarySyncState()
            guard current(own), syncID == id else { return }
            if let preferences {
                selections = preferences.selections; automaticRevision = preferences.revision
            }
            state = status; error = failure
            await onLibraryChanged()
        } catch {
            guard current(own), syncID == id else { return }
            self.error = failure ?? MusicServiceError.sanitized(error).errorDescription
        }
    }
    deinit { writeAuthorization?.revoke(); syncTask?.cancel(); completionTask?.cancel() }
}
#endif

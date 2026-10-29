#if os(iOS) || os(macOS)
import Foundation
import Combine

public struct MusicLibrarySyncConfirmation: Identifiable, Sendable {
    public let id: UUID
    fileprivate let epoch: UUID
    fileprivate let generation: UUID
    fileprivate let selections: [MusicLibrarySyncSelection]
}

@MainActor public final class MusicLibrarySyncViewModel: ObservableObject {
    @Published public private(set) var selections: [MusicLibrarySyncSelection] = []
    @Published public private(set) var state = MusicLibrarySyncState()
    @Published public private(set) var accounts: [MusicAccountStatus] = []
    @Published public private(set) var busy = false
    @Published public private(set) var isAvailable = false
    @Published public private(set) var error: String?
    @Published public private(set) var confirmation: MusicLibrarySyncConfirmation?
    private let service: any MusicLibrarySyncing
    private let accountService: (any MusicAccountsServing)?
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
    private var loadID = UUID()
    private var presentationActive = true
    private var pendingConfirmation: UUID?

    public init(service: any MusicLibrarySyncing, accounts: (any MusicAccountsServing)? = nil,
                onLibraryChanged: @escaping @MainActor () async -> Void = {}) {
        self.service = service; accountService = accounts; self.onLibraryChanged = onLibraryChanged
    }
    public var canSync: Bool {
        active && isAvailable && presentationActive && !busy && pendingConfirmation == nil &&
            selections.contains { !$0.kinds.isEmpty }
    }
    public func prepareConfirmation() {
        guard canSync else { return }
        confirmation = .init(id: UUID(), epoch: epoch, generation: accessGeneration, selections: selections)
    }
    public func dismissConfirmation(id: UUID) {
        if confirmation?.id == id { confirmation = nil }
    }
    public func setPresentationActive(_ value: Bool) {
        presentationActive = value
        if !value { cancel() }
    }
    /// Consume the exact foreground decision synchronously. A queued callback
    /// cannot authorize a different selection, page, or unlocked account epoch.
    @discardableResult public func confirm(_ ticket: MusicLibrarySyncConfirmation) -> Task<Void, Never>? {
        guard confirmation?.id == ticket.id, canSync, matches(ticket) else { return nil }
        confirmation = nil; pendingConfirmation = ticket.id
        return Task { [weak self] in
            guard let self, self.pendingConfirmation == ticket.id else { return }
            self.pendingConfirmation = nil
            guard self.canSync, self.matches(ticket) else { return }
            await self.synchronize()
        }
    }
    private func matches(_ ticket: MusicLibrarySyncConfirmation) -> Bool {
        ticket.epoch == epoch && ticket.generation == accessGeneration && ticket.selections == selections
    }
    public func selection(_ platform: MusicAccountPlatform) -> MusicLibrarySyncSelection {
        selections.first { $0.platform == platform } ?? .init(platform: platform)
    }
    public func select(_ platform: MusicAccountPlatform, favorites: Bool? = nil, playlists: Bool? = nil) {
        guard active, isAvailable, presentationActive, !busy, pendingConfirmation == nil,
              !supportsAutomatic || automaticRevision != nil else { return }
        confirmation = nil
        loadID = UUID() // A late status load must not undo this explicit choice.
        var value = selection(platform)
        if let favorites { value.favorites = favorites }
        if let playlists { value.playlists = playlists }
        selections.removeAll { $0.platform == platform }; selections.append(value)
        if let automatic, let revision = automaticRevision {
            let own = epoch, id = UUID(), permission = MusicQueueAuthorization()
            let preferences = MusicAutomaticLibrarySyncPreferences(revision: revision, selections: selections)
            syncID = id; busy = true; error = nil; writeAuthorization = permission
            let task = Task {
                try permission.check(); try Task.checkCancellation()
                return try await automatic.saveAutomaticLibrarySync(preferences, requestID: id, authorization: permission)
            }
            syncTask = task
            completionTask = Task { [weak self] in
                await self?.finishAutomatic(task, own: own, id: id, permission: permission)
            }
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
        confirmation = nil; pendingConfirmation = nil
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
        confirmation = nil; pendingConfirmation = nil
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
    public func synchronize() async {
        guard canSync else { return }
        confirmation = nil
        let own = epoch, id = UUID(), service = service
        let request = MusicLibrarySyncRequest(id: id, selections: selections.filter { !$0.kinds.isEmpty })
        loadID = UUID(); syncID = id; busy = true; error = nil
        if let automatic {
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

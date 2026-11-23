// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus b900c259 account-settings.js / library.ts / collection.ts.
import Foundation
import MeowMusicFeature

struct MusicNativeAutomaticSyncRuntime {
    var observers: [UUID: AsyncStream<MusicAutomaticLibrarySyncEvent>.Continuation] = [:]
    var task: Task<MusicLibrarySyncState, Error>?
    var authorization: MusicQueueAuthorization?
    var settingsSync: (id: UUID, authorization: MusicQueueAuthorization,
                       task: Task<Result<MusicLibrarySyncState, MusicServiceError>, Never>)?
    var saving: UUID?
    var binding: UUID?
    var attemptedIntent: (preferences: Int, mutation: Int)?
}

private struct MusicNativeAutomaticSyncPort: MusicNativeLibrarySyncProviding {
    let base: any MusicNativeLibrarySyncProviding
    let bindings: [String: MusicNativeAutomaticSyncBinding]
    let authorization: MusicQueueAuthorization
    func check() throws { try authorization.check(); try Task.checkCancellation() }
    func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        try check()
        guard let binding = bindings[platform.rawValue] else { throw MusicServiceError.verificationRequired }
        let session = try await base.beginLibrarySync(platform: platform)
        do {
            try check()
            guard session.platform == platform, binding.accountID == session.accountID,
                  binding.scope == session.scope else { throw MusicServiceError.targetChanged }
            try await base.checkLibrarySync(session); try check()
            return session
        } catch { await base.endLibrarySync(session); throw error }
    }
    func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws {
        try check(); try await base.checkLibrarySync(session); try check()
    }
    func readLibrarySync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        try await checkLibrarySync(session)
        let value = try await base.readLibrarySync(session, kinds: kinds)
        try await checkLibrarySync(session)
        return value
    }
    func readLibrarySubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        try await checkLibrarySync(session)
        let value = try await base.readLibrarySubscriptions(session)
        try await checkLibrarySync(session)
        return value
    }
    func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        do { try await checkLibrarySync(session) }
        catch { return .notDispatched(MusicServiceError.sanitized(error)) }
        return try await base.writeLibrarySync(session, mutation: mutation)
    }
    func endLibrarySync(_ session: MusicLibrarySyncSession) async { await base.endLibrarySync(session) }
}

extension NativeMusicService: MusicAutomaticLibrarySyncing {
    public func automaticLibrarySyncPreferences() async throws -> MusicAutomaticLibrarySyncPreferences {
        let saved = try MusicNativeSyncLedger.load(read().library).automatic
        return .init(revision: saved.revision, selections: saved.selections)
    }

    public func automaticLibrarySyncEvents() -> AsyncStream<MusicAutomaticLibrarySyncEvent> {
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(32)) { continuation in
            guard !closed, available else { continuation.finish(); return }
            automaticLibrarySyncRuntime.observers[id] = continuation
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeAutomaticSyncObserver(id) }
            }
        }
    }

    private func removeAutomaticSyncObserver(_ id: UUID) {
        automaticLibrarySyncRuntime.observers.removeValue(forKey: id)
    }

    func publishAutomaticLibrarySync(_ event: MusicAutomaticLibrarySyncEvent) {
        for observer in automaticLibrarySyncRuntime.observers.values { observer.yield(event) }
    }

    func invalidateAutomaticLibrarySync() {
        automaticLibrarySyncRuntime.settingsSync?.authorization.revoke()
        automaticLibrarySyncRuntime.settingsSync?.task.cancel()
        automaticLibrarySyncRuntime.authorization?.revoke()
        automaticLibrarySyncRuntime.task?.cancel()
        automaticLibrarySyncRuntime.saving = nil
        automaticLibrarySyncRuntime.binding = nil
        automaticLibrarySyncRuntime.attemptedIntent = nil
        for observer in automaticLibrarySyncRuntime.observers.values { observer.finish() }
        automaticLibrarySyncRuntime.observers.removeAll()
    }

    func recordAutomaticLibraryMutation(_ d: inout MusicDomainState, collectionID: String,
                                        orderOnly: Bool = false, relationshipPlatform: MusicAccountPlatform? = nil) throws {
        var sync = d.library.sync ?? .empty
        let revision = sync.autoIntentRevision
        let settledRevision = sync.autoSettledIntentRevision
        try musicRequire(revision >= 0 && revision < musicMaxSafeInteger && settledRevision >= 0 && settledRevision <= revision, .limit)
        let settled = revision == settledRevision
        if let relationshipPlatform {
            var platforms = sync.relationshipPlatforms
            if settled { platforms = [relationshipPlatform.rawValue] }
            else if !platforms.isEmpty, !platforms.contains(relationshipPlatform.rawValue) {
                platforms.append(relationshipPlatform.rawValue)
            }
            sync.relationshipPlatforms = platforms
            sync.autoOrderOnly = nil
        } else {
            sync.relationshipPlatforms = []
            sync.autoOrderOnly = (orderOnly && settled) ? collectionID : nil
        }
        sync.autoIntentRevision = revision + 1
        d.library.sync = sync
    }

    private func automaticSelections(_ values: [MusicLibrarySyncSelection]) throws -> [MusicLibrarySyncSelection] {
        try musicRequire(values.count <= 3 && Set(values.map(\.platform)).count == values.count)
        for value in values { try requirePlatform(value.platform.rawValue) }
        return values.filter { !$0.kinds.isEmpty }.sorted { $0.platform.rawValue < $1.platform.rawValue }
    }

    private func changeAutomaticPreferences(expected: Int, authorization: MusicQueueAuthorization,
        _ change: (inout MusicNativeAutomaticSyncPreferences, inout MusicNativeSyncLedger) throws -> Void) throws {
        try authorization.perform {
            try transaction([.library]) { d in
                var ledger = try MusicNativeSyncLedger.load(d.library)
                var preferences = ledger.automatic
                try musicRequire(preferences.revision == expected, .revisionConflict)
                try change(&preferences, &ledger)
                ledger.automatic = preferences
                d.library.sync = ledger.toRecord()
                d.library.revision += 1
            }
        }
    }

    public func saveAutomaticLibrarySync(_ preferences: MusicAutomaticLibrarySyncPreferences, requestID: UUID,
                                         authorization: MusicQueueAuthorization) async throws -> MusicAutomaticLibrarySyncSaveResult {
        try check(); try authorization.check()
        let selected = try automaticSelections(preferences.selections), own = epoch
        try musicRequire((0..<(musicMaxSafeInteger - 1)).contains(preferences.revision), .limit)
        let savedRevision = preferences.revision + 1
        let previous = automaticLibrarySyncRuntime.settingsSync?.task, drain = librarySyncRun?.drain
        automaticLibrarySyncRuntime.settingsSync?.authorization.revoke()
        previous?.cancel()
        automaticLibrarySyncRuntime.authorization?.revoke()
        automaticLibrarySyncRuntime.task?.cancel()
        librarySyncRun?.cancel()
        try changeAutomaticPreferences(expected: preferences.revision, authorization: authorization) { value, ledger in
            value.revision = savedRevision; value.selections = selected
            value.bindings = value.bindings.filter { pair in selected.contains { $0.platform.rawValue == pair.key } }
            value.relationshipPlatforms = value.relationshipPlatforms.filter { platform in selected.contains { $0.platform == platform && $0.playlists } }
            ledger.phase = .idle; ledger.issues = []
        }
        let committed = MusicAutomaticLibrarySyncPreferences(revision: savedRevision, selections: selected)
        let permission = authorization, accessID = UUID()
        automaticLibrarySyncRuntime.saving = requestID
        let task = Task {
            await withTaskCancellationHandler {
                await self.completeAutomaticSettingsSync(committed, requestID: requestID, accessID: accessID,
                    epoch: own, authorization: permission, previous: previous, drain: drain)
            } onCancel: { permission.revoke() }
        }
        automaticLibrarySyncRuntime.settingsSync = (requestID, permission, task)
        access.register(accessID) { permission.revoke(); task.cancel() }
        do { try permission.register(accessID) { task.cancel() } }
        catch { task.cancel() }
        publishAutomaticLibrarySync(.refreshed)
        return .init(preferences: committed, synchronization: task)
    }

    private func completeAutomaticSettingsSync(_ committed: MusicAutomaticLibrarySyncPreferences,
        requestID: UUID, accessID: UUID, epoch own: UUID, authorization: MusicQueueAuthorization,
        previous: Task<Result<MusicLibrarySyncState, MusicServiceError>, Never>?,
        drain: (@Sendable () async -> Void)?) async -> Result<MusicLibrarySyncState, MusicServiceError> {
        defer {
            authorization.remove(accessID); authorization.revoke(); access.remove(accessID)
            if automaticLibrarySyncRuntime.settingsSync?.id == requestID { automaticLibrarySyncRuntime.settingsSync = nil }
            if automaticLibrarySyncRuntime.saving == requestID { automaticLibrarySyncRuntime.saving = nil }
            publishAutomaticLibrarySync(.refreshed)
        }
        _ = await previous?.value
        await drain?()
        do {
            try check(own); try authorization.check()
            guard automaticLibrarySyncRuntime.saving == requestID else { throw MusicServiceError.staleAccess }
            if committed.selections.isEmpty { return .success(try await librarySyncState()) }
            try await bindAutomaticLibraryAccounts(revision: committed.revision, authorization: authorization)
            let state = try await runAutomaticLibrarySync(.init(id: requestID, reason: .manual,
                preferenceRevision: committed.revision, authorization: authorization), bind: false)
            return .success(state)
        } catch {
            let failure = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
            do { try recordAutomaticSyncFailure(error, expected: committed.revision, authorization: authorization) }
            catch { return .failure(error as? MusicServiceError ?? MusicServiceError.sanitized(error)) }
            return .failure(failure)
        }
    }

    private func bindAutomaticLibraryAccounts(revision: Int, authorization: MusicQueueAuthorization) async throws {
        try check(); try authorization.check(); try musicRequire(librarySyncRun == nil, .busy)
        guard let port = accounts as? any MusicNativeLibrarySyncProviding else { throw MusicServiceError.unavailable }
        let own = epoch, library = try read().library, ledger = try MusicNativeSyncLedger.load(library)
        let preferences = ledger.automatic
        try musicRequire(preferences.revision == revision, .revisionConflict)
        var bindings: [String: MusicNativeAutomaticSyncBinding] = [:]
        for selection in preferences.selections {
            try check(own); try authorization.check()
            let session = try await port.beginLibrarySync(platform: selection.platform)
            do {
                try check(own); try authorization.check()
                try await port.checkLibrarySync(session)
                try check(own); try authorization.check()
                try musicRequire(!session.accountID.isEmpty, .invalidResponse)
                _ = try musicText(session.scope)
                let previous = library.accountID(for: selection.platform.rawValue)
                try musicRequire(session.platform == selection.platform &&
                    (previous == nil || previous == session.accountID), .targetChanged)
                if let old = ledger.scopes[selection.platform.rawValue] { try musicRequire(old == session.scope, .targetChanged) }
                bindings[selection.platform.rawValue] = .init(accountID: session.accountID, scope: session.scope)
            } catch { await port.endLibrarySync(session); throw error }
            await port.endLibrarySync(session)
        }
        try check(own); try authorization.check()
        try changeAutomaticPreferences(expected: revision, authorization: authorization) { value, _ in
            value.bindings = bindings
        }
    }

    public func synchronizeAutomaticLibrary(_ request: MusicAutomaticLibrarySyncRequest) async throws -> MusicLibrarySyncState {
        try check(); try request.authorization.check()
        try musicRequire(automaticLibrarySyncRuntime.saving == nil && automaticLibrarySyncRuntime.binding == nil, .busy)
        if request.reason == .manual { automaticLibrarySyncRuntime.binding = request.id }
        defer { if automaticLibrarySyncRuntime.binding == request.id { automaticLibrarySyncRuntime.binding = nil } }
        return try await runAutomaticLibrarySync(request, bind: request.reason == .manual)
    }

    private func runAutomaticLibrarySync(_ request: MusicAutomaticLibrarySyncRequest, bind: Bool) async throws -> MusicLibrarySyncState {
        try check(); try request.authorization.check()
        let preferences = try MusicNativeSyncLedger.load(read().library).automatic
        if let expected = request.preferenceRevision { try musicRequire(preferences.revision == expected, .revisionConflict) }
        guard !preferences.selections.isEmpty else { return try await librarySyncState() }
        if request.reason == .localMutation {
            let attempted = automaticLibrarySyncRuntime.attemptedIntent
            if preferences.intentRevision == preferences.settledIntentRevision ||
                attempted?.preferences == preferences.revision && attempted?.mutation == preferences.intentRevision {
                return try await librarySyncState()
            }
        }
        try musicRequire(librarySyncRun == nil, .busy)
        if bind { try await bindAutomaticLibraryAccounts(revision: preferences.revision, authorization: request.authorization) }
        let current = try MusicNativeSyncLedger.load(read().library).automatic
        try musicRequire(current.revision == preferences.revision, .revisionConflict)
        var selected = try automaticSelections(current.selections)
        let relationshipsOnly = request.reason == .localMutation && !current.relationshipPlatforms.isEmpty
        if relationshipsOnly {
            selected = selected.filter { current.relationshipPlatforms.contains($0.platform) && $0.playlists }
                .map { .init(platform: $0.platform, playlists: true) }
        }
        guard selected.allSatisfy({ current.bindings[$0.platform.rawValue] != nil }) else {
            try recordAutomaticSyncFailure(MusicServiceError.verificationRequired, expected: current.revision,
                                           authorization: request.authorization)
            publishAutomaticLibrarySync(.refreshed)
            throw MusicServiceError.verificationRequired
        }
        guard let base = accounts as? any MusicNativeLibrarySyncProviding else { throw MusicServiceError.unavailable }
        let permission = MusicQueueAuthorization(parent: request.authorization)
        let port = MusicNativeAutomaticSyncPort(base: base, bindings: current.bindings, authorization: permission)
        let nativeRequest = MusicLibrarySyncRequest(id: request.id, selections: selected)
        let orderOnly = request.reason == .localMutation ? current.orderOnly : nil
        let task = Task {
            try permission.check()
            if let orderOnly { return try await self.performAutomaticOrderSync(nativeRequest, port: port, collectionID: orderOnly) }
            return try await self.performLibrarySync(nativeRequest, port: port, relationshipsOnly: relationshipsOnly)
        }
        let cancel: @Sendable () -> Void = { permission.revoke(); task.cancel() }
        automaticLibrarySyncRuntime.task = task; automaticLibrarySyncRuntime.authorization = permission
        automaticLibrarySyncRuntime.attemptedIntent = (current.revision, current.intentRevision)
        librarySyncRun = (request.id, cancel, { _ = await task.result })
        access.register(request.id, cancel: cancel)
        defer {
            permission.revoke(); access.remove(request.id)
            if librarySyncRun?.id == request.id {
                librarySyncRun = nil; automaticLibrarySyncRuntime.task = nil
                automaticLibrarySyncRuntime.authorization = nil
            }
            publishAutomaticLibrarySync(.refreshed)
            if request.authorization.isActive, !Task.isCancelled,
               let latest = try? MusicNativeSyncLedger.load(read().library).automatic,
               latest.revision == current.revision, latest.intentRevision > current.intentRevision {
                publishAutomaticLibrarySync(.localMutation)
            }
        }
        do {
            let result = try await withTaskCancellationHandler { try await task.value } onCancel: { cancel() }
            try check(); try request.authorization.check()
            try changeAutomaticPreferences(expected: current.revision, authorization: request.authorization) { value, _ in
                value.settledIntentRevision = max(value.settledIntentRevision, current.intentRevision)
                if value.intentRevision == current.intentRevision { value.orderOnly = nil; value.relationshipPlatforms = [] }
            }
            return result
        } catch {
            try recordAutomaticSyncFailure(error, expected: current.revision, authorization: request.authorization)
            throw error
        }
    }

    private func recordAutomaticSyncFailure(_ error: Error, expected: Int, authorization: MusicQueueAuthorization) throws {
        guard !Task.isCancelled, authorization.isActive, (try? check()) != nil,
              let current = try? MusicNativeSyncLedger.load(read().library),
              current.automatic.revision == expected else { return }
        let code = MusicServiceError.sanitized(error) == .verificationRequired
            ? "automaticBindingRequired" : MusicNativeSyncLogic.code(error)
        try changeAutomaticPreferences(expected: expected, authorization: authorization) { _, ledger in
            ledger.phase = .blocked; ledger.issues = [.init(code: code)]
        }
    }

    private func performAutomaticOrderSync(_ request: MusicLibrarySyncRequest, port: any MusicNativeLibrarySyncProviding,
                                           collectionID: String) async throws -> MusicLibrarySyncState {
        let initial = try read()
        try initial.requireRevision(.library)
        let local = try collection(collectionID, initial)
        guard let kind = MusicLibrarySyncKind(rawValue: local.kind) else { throw MusicServiceError.corruptStorage }
        var round = MusicNativeSyncRound(
            request: request,
            epoch: epoch,
            port: port,
            library: initial.library,
            ledger: try MusicNativeSyncLedger.load(initial.library),
            revision: initial.library.revision
        )
        round.ledger.selections = request.selections; round.ledger.phase = .pulling; round.ledger.issues = []
        try syncCheckpoint(&round)
        do {
            try musicRequire(!local.deleted, .notFound)
            try musicRequire(round.ledger.fences.isEmpty, .commitUnconfirmed)
            for selection in request.selections {
                let session = try await port.beginLibrarySync(platform: selection.platform)
                round.sessions[selection.platform.rawValue] = session
                try await syncCurrent(round)
                try musicRequire(round.library.accountID(for: selection.platform.rawValue) == session.accountID &&
                    round.ledger.scopes[selection.platform.rawValue] == session.scope, .targetChanged)
                round.snapshots[selection.platform.rawValue] = try await syncSnapshot(session, selection: selection, round: round)
            }
            var targets = Set<String>()
            for selection in request.selections where selection.kinds.contains(kind) {
                for binding in local.bindings where binding.platform == selection.platform.rawValue {
                    try musicRequire(binding.accountID == round.sessions[selection.platform.rawValue]?.accountID &&
                        binding.writable &&
                        targets.insert(MusicNativeSyncLogic.bindingKey(binding)).inserted, .corruptStorage)
                    guard let remote = round.snapshots[selection.platform.rawValue]?.collections.first(where: { $0.id == binding.remoteID }) else {
                        throw MusicServiceError.targetChanged
                    }
                    try musicRequire(binding.baseline.sorted() == remote.members.map(\.occurrenceID).sorted(), .revisionConflict)
                    for member in remote.members {
                        guard let songID = binding.songID(for: member.occurrenceID),
                              try repositoryReader.containsSong(id: songID) else {
                            throw MusicServiceError.corruptStorage
                        }
                    }
                }
            }
            round.ledger.phase = .pushing; try syncCheckpoint(&round)
            for selection in request.selections where selection.kinds.contains(kind) {
                let session = round.sessions[selection.platform.rawValue]!
                for binding in local.bindings where binding.platform == selection.platform.rawValue {
                    guard let remote = round.snapshots[selection.platform.rawValue]?.collections.first(where: { $0.id == binding.remoteID }) else { continue }
                    var desired = local.order.flatMap { id in remote.members.filter { binding.songID(for: $0.occurrenceID) == id } }
                    let used = Set(desired.map(\.occurrenceID))
                    desired += remote.members.filter { !used.contains($0.occurrenceID) }
                    guard desired.map(\.occurrenceID) != remote.members.map(\.occurrenceID) else { continue }
                    guard remote.canOrder else { round.issue("orderUnsupported", platform: selection.platform, collection: collectionID); continue }
                    _ = try await syncDispatch(.init(action: .order, collectionID: remote.id, before: remote, members: desired),
                        localID: local.id, localRevision: local.revision, session: session, selection: selection, round: &round)
                }
            }
            round.ledger.phase = round.ledger.issues.isEmpty && round.ledger.fences.isEmpty ? .synced : .partial
            try await syncCurrent(round); try syncCheckpoint(&round)
            for session in round.sessions.values { await port.endLibrarySync(session) }
            return try await librarySyncState()
        } catch {
            for session in round.sessions.values { await port.endLibrarySync(session) }
            throw error
        }
    }
}

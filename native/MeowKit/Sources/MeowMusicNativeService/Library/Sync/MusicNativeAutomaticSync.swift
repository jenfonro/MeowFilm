// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus b900c259 account-settings.js / library.ts / collection.ts.
import Foundation
import MeowMusicFeature

/// Stored by the ONE NativeMusicService actor. No timer, singleton or extra
/// library writer. meowfilm owner hooks: field + clearTransient invalidation only.
struct MusicNativeAutomaticSyncRuntime {
    var observers: [UUID: AsyncStream<MusicAutomaticLibrarySyncEvent>.Continuation] = [:]
    var task: Task<MusicLibrarySyncState, Error>?
    var authorization: MusicQueueAuthorization?
    var saving: UUID?
    var binding: UUID?
    var attemptedIntent: (preferences: Int, mutation: Int)?
}

/// The existing engine still owns merge, dispatch fences and readback. This
/// narrow port additionally prevents a scheduled run adopting an unknown/new
/// account, and checks the foreground decision before every boundary.
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
    func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        try await checkLibrarySync(session)
        // Cancellation propagates into the existing account owner's per-write
        // authorization. An uncertain dispatched write keeps the engine fence.
        let result = try await base.writeLibrarySync(session, mutation: mutation)
        try await checkLibrarySync(session)
        return result
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

    /// Call at the start of NativeMusicService.clearTransient(). The existing
    /// access.register cancellation also fences a nonisolated protection event.
    func invalidateAutomaticLibrarySync() {
        automaticLibrarySyncRuntime.authorization?.revoke()
        automaticLibrarySyncRuntime.task?.cancel()
        automaticLibrarySyncRuntime.saving = nil
        automaticLibrarySyncRuntime.binding = nil
        automaticLibrarySyncRuntime.attemptedIntent = nil
        for observer in automaticLibrarySyncRuntime.observers.values { observer.finish() }
        automaticLibrarySyncRuntime.observers.removeAll()
        // Keep the draining task until its defer releases librarySyncRun.
    }

    /// Called INSIDE the user's successful library transaction. It is durable
    /// intent, not a serialized HTTP command. Import/rollback never call this.
    func recordAutomaticLibraryMutation(_ d: inout MusicDocument, collectionID: String,
                                       orderOnly: Bool = false) throws {
        var ledger = try MusicNativeSyncLedger.load(d.library)
        var preferences = ledger.automatic
        try musicRequire(preferences.intentRevision < 9_007_199_254_740_991, .limit)
        preferences.orderOnly = orderOnly && preferences.intentRevision == preferences.settledIntentRevision
            ? collectionID : nil
        preferences.intentRevision += 1
        ledger.automatic = preferences; d.library["sync"] = try ledger.json()
    }

    private func automaticSelections(_ values: [MusicLibrarySyncSelection]) throws -> [MusicLibrarySyncSelection] {
        try musicRequire(values.count <= 3 && Set(values.map(\.platform)).count == values.count)
        for value in values { try requirePlatform(value.platform.rawValue) }
        return values.filter { !$0.kinds.isEmpty }.sorted { $0.platform.rawValue < $1.platform.rawValue }
    }
    private func changeAutomaticPreferences(expected: Int, authorization: MusicQueueAuthorization,
        _ change: (inout MusicNativeAutomaticSyncPreferences, inout MusicNativeSyncLedger) throws -> Void) throws {
        try authorization.perform {
            try transaction(["library"]) { d in
                var ledger = try MusicNativeSyncLedger.load(d.library)
                var preferences = ledger.automatic
                try musicRequire(preferences.revision == expected, .revisionConflict)
                try change(&preferences, &ledger)
                ledger.automatic = preferences
                d.library["sync"] = try ledger.json()
                d.library["revision"] = .n(d.library["revision"].int + 1)
            }
        }
    }

    public func saveAutomaticLibrarySync(_ preferences: MusicAutomaticLibrarySyncPreferences, requestID: UUID,
                                        authorization: MusicQueueAuthorization) async throws -> MusicLibrarySyncState {
        try check(); try authorization.check()
        try musicRequire(automaticLibrarySyncRuntime.saving == nil, .busy)
        let selected = try automaticSelections(preferences.selections), own = epoch
        try musicRequire((0..<9_007_199_254_740_990).contains(preferences.revision), .limit)
        let savedRevision = preferences.revision + 1
        automaticLibrarySyncRuntime.saving = requestID
        defer {
            if automaticLibrarySyncRuntime.saving == requestID { automaticLibrarySyncRuntime.saving = nil }
            publishAutomaticLibrarySync(.refreshed)
        }
        // Persist OFF immediately, even if an old network operation won't
        // cooperate. Its next checkpoint fails the library revision fence.
        automaticLibrarySyncRuntime.authorization?.revoke()
        automaticLibrarySyncRuntime.task?.cancel()
        librarySyncRun?.cancel()
        try changeAutomaticPreferences(expected: preferences.revision, authorization: authorization) { value, ledger in
            value.revision = savedRevision; value.selections = selected
            value.bindings = value.bindings.filter { pair in selected.contains { $0.platform.rawValue == pair.key } }
            ledger.phase = .idle; ledger.issues = []
        }
        if selected.isEmpty { return try await librarySyncState() }
        if let draining = automaticLibrarySyncRuntime.task { _ = try? await draining.value }
        try check(own); try authorization.check()
        guard automaticLibrarySyncRuntime.saving == requestID else { throw MusicServiceError.staleAccess }
        do {
            try await bindAutomaticLibraryAccounts(revision: savedRevision, authorization: authorization)
            return try await runAutomaticLibrarySync(.init(id: requestID, reason: .manual,
                preferenceRevision: savedRevision, authorization: authorization), bind: false)
        } catch {
            try recordAutomaticSyncFailure(error, expected: savedRevision, authorization: authorization)
            throw error
        }
    }

    /// Only an explicit settings/manual decision enters here. A timer never
    /// establishes permission by looking at the currently signed-in platform.
    private func bindAutomaticLibraryAccounts(revision: Int, authorization: MusicQueueAuthorization) async throws {
        try check(); try authorization.check(); try musicRequire(librarySyncRun == nil, .busy)
        guard let port = accounts as? any MusicNativeLibrarySyncProviding else { throw MusicServiceError.unavailable }
        let own = epoch, wrapper = try read().library, ledger = try MusicNativeSyncLedger.load(wrapper)
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
                _ = try musicText(session.accountID, maximum: 1024)
                _ = try musicText(session.scope, maximum: 1024)
                let previous = wrapper["library"]["accounts"][selection.platform.rawValue]
                try musicRequire(session.platform == selection.platform &&
                    (previous == .null || previous.string == session.accountID), .targetChanged)
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
        // Reserve the explicit bind phase as well as the engine run; actor
        // reentrancy must not let a timer start another account lease.
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
        let selected = try automaticSelections(current.selections)
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
            return try await self.performLibrarySync(nativeRequest, port: port)
        }
        let cancel: @Sendable () -> Void = { permission.revoke(); task.cancel() }
        automaticLibrarySyncRuntime.task = task; automaticLibrarySyncRuntime.authorization = permission
        automaticLibrarySyncRuntime.attemptedIntent = (current.revision, current.intentRevision)
        librarySyncRun = (request.id, cancel)
        access.register(request.id, cancel: cancel)
        defer {
            permission.revoke(); access.remove(request.id)
            if librarySyncRun?.id == request.id {
                librarySyncRun = nil; automaticLibrarySyncRuntime.task = nil
                automaticLibrarySyncRuntime.authorization = nil
            }
            publishAutomaticLibrarySync(.refreshed)
            // A user edit which invalidated this round is a NEW intent. The
            // same failed/offline intent is deliberately not an immediate retry.
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
                // A newer mutation is still pending and must keep its trigger.
                value.settledIntentRevision = max(value.settledIntentRevision, current.intentRevision)
                if value.intentRevision == current.intentRevision { value.orderOnly = nil }
            }
            return result
        } catch {
            try recordAutomaticSyncFailure(error, expected: current.revision, authorization: request.authorization)
            throw error
        }
    }

    private func recordAutomaticSyncFailure(_ error: Error, expected: Int, authorization: MusicQueueAuthorization) throws {
        // Do not reopen protected storage or replace a newer settings revision.
        guard !Task.isCancelled, authorization.isActive, (try? check()) != nil,
              let current = try? MusicNativeSyncLedger.load(read().library),
              current.automatic.revision == expected else { return }
        let code = MusicServiceError.sanitized(error) == .verificationRequired
            ? "automaticBindingRequired" : MusicNativeSyncLogic.code(error)
        try changeAutomaticPreferences(expected: expected, authorization: authorization) { _, ledger in
            ledger.phase = .blocked; ledger.issues = [.init(code: code)]
        }
    }

    /// A pure-order save must NOT run the full merge/create/member projection.
    /// Reuses the engine's snapshot validator, CAS, dispatch fence and readback.
    private func performAutomaticOrderSync(_ request: MusicLibrarySyncRequest, port: any MusicNativeLibrarySyncProviding,
                                          collectionID: String) async throws -> MusicLibrarySyncState {
        let initial = try read()
        var round = MusicNativeSyncRound(request: request, epoch: epoch, port: port,
            library: initial.library["library"], ledger: try MusicNativeSyncLedger.load(initial.library),
            revision: initial.library["revision"].int)
        round.ledger.selections = request.selections; round.ledger.phase = .pulling; round.ledger.issues = []
        try syncCheckpoint(&round)
        do {
            let local = try collection(collectionID, initial)
            try musicRequire(!local["deleted"].bool, .notFound)
            // Unknown writes are recovered by a later FULL read round, never
            // replayed as a side effect of a reorder.
            try musicRequire(round.ledger.fences.isEmpty, .commitUnconfirmed)
            for selection in request.selections {
                let session = try await port.beginLibrarySync(platform: selection.platform)
                round.sessions[selection.platform.rawValue] = session
                try await syncCurrent(round)
                try musicRequire(round.library["accounts"][selection.platform.rawValue].string == session.accountID &&
                    round.ledger.scopes[selection.platform.rawValue] == session.scope, .targetChanged)
                round.snapshots[selection.platform.rawValue] = try await syncSnapshot(session, selection: selection, round: round)
            }
            // Preflight ALL targets before the first write.
            for selection in request.selections where selection.kinds.contains(MusicLibrarySyncKind(rawValue: local["kind"].string)!) {
                for binding in local["bindings"].array where binding["platform"].string == selection.platform.rawValue {
                    guard let remote = round.snapshots[selection.platform.rawValue]?.collections.first(where: { $0.id == binding["id"].string }) else {
                        throw MusicServiceError.targetChanged
                    }
                    try musicRequire(binding["baseline"].strings.sorted() == remote.members.map(\.occurrenceID).sorted(), .revisionConflict)
                }
            }
            round.ledger.phase = .pushing; try syncCheckpoint(&round)
            for selection in request.selections where selection.kinds.contains(MusicLibrarySyncKind(rawValue: local["kind"].string)!) {
                let session = round.sessions[selection.platform.rawValue]!
                for binding in local["bindings"].array where binding["platform"].string == selection.platform.rawValue {
                    guard let remote = round.snapshots[selection.platform.rawValue]?.collections.first(where: { $0.id == binding["id"].string }) else { continue }
                    guard remote.writable else { round.issue("readOnly", platform: selection.platform, collection: collectionID); continue }
                    let map = binding["map"]
                    var desired = local["order"].strings.flatMap { id in remote.members.filter { map[$0.occurrenceID].string == id } }
                    let used = Set(desired.map(\.occurrenceID))
                    desired += remote.members.filter { !used.contains($0.occurrenceID) }
                    guard desired.map(\.occurrenceID) != remote.members.map(\.occurrenceID) else { continue }
                    guard remote.canOrder else { round.issue("orderUnsupported", platform: selection.platform, collection: collectionID); continue }
                    _ = try await syncDispatch(.init(action: .order, collectionID: remote.id, before: remote, members: desired),
                        local: local, session: session, selection: selection, round: &round)
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

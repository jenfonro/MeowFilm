// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowDebug
import MeowMusicFeature

extension NativeMusicService: MusicLibrarySyncing {
    public func librarySyncState() async throws -> MusicLibrarySyncState {
        let library = try read().library, ledger = try MusicNativeSyncLedger.load(library)
        let pending = library.collections.reduce(0) { count, c in
            count + c.intents.filter(\.pending).count
        } + library.networkReferences.filter { reference in
            reference.pending && ledger.selections.contains {
                $0.platform.rawValue == reference.platform && $0.playlists
            }
        }.count
        let phase: MusicLibrarySyncState.Phase = librarySyncRun == nil && [.pulling, .pushing].contains(ledger.phase) ? .blocked : ledger.phase
        return .init(
            selections: ledger.selections,
            phase: phase,
            pending: pending + ledger.fences.count,
            unresolvedFences: ledger.fences.count,
            issues: ledger.issues
        )
    }

    public func resetSyncAccount(for platform: MusicAccountPlatform) async throws -> MusicLibrarySyncState {
        try check()
        try requirePlatform(platform.rawValue)
        try musicRequire(librarySyncRun == nil, .busy)
        let key = platform.rawValue
        try transaction([.library]) { d in
            var ledger = try MusicNativeSyncLedger.load(d.library)
            d.library.accounts.removeAll { $0.platform == key }
            ledger.scopes.removeValue(forKey: key)
            ledger.automatic.bindings.removeValue(forKey: key)
            ledger.fences.removeAll { $0.platform == platform }
            ledger.issues.removeAll {
                $0.platform == platform || ($0.platform == nil && ($0.code == "accountChanged" || $0.code == "automaticBindingRequired"))
            }
            for i in d.library.collections.indices {
                let removedKeys = Set(
                    d.library.collections[i].bindings
                        .filter { $0.platform == key }
                        .map(MusicNativeSyncLogic.bindingKey)
                )
                if !removedKeys.isEmpty {
                    d.library.collections[i].bindings.removeAll { $0.platform == key }
                    for j in d.library.collections[i].intents.indices {
                        d.library.collections[i].intents[j].acknowledged.removeAll { removedKeys.contains($0) }
                    }
                }
            }
            for i in d.library.networkReferences.indices where d.library.networkReferences[i].platform == key {
                d.library.networkReferences[i].relation = nil
            }
            ledger.phase = ledger.fences.isEmpty && ledger.issues.isEmpty ? .idle : .blocked
            d.library.sync = ledger.toRecord()
            d.library.statusPhase = ledger.phase.rawValue
            d.library.statusAtMs = Int64((now().timeIntervalSince1970 * 1000).rounded())
            d.library.statusCode = nil
            d.library.statusIssues = ledger.issues.map {
                MusicLibraryIssueRecord(platform: $0.platform?.rawValue, collectionID: $0.collectionID, code: $0.code)
            }
            d.library.libraryRevision += 1
            d.library.revision += 1
        }
        publishAutomaticLibrarySync(.refreshed)
        DebugRuntime.log(.musicLibrary, sub: "sync", event: "resetAccount", level: .info, data: .object([
            "platform": .string(key)
        ]))
        return try await librarySyncState()
    }

    public func cancelLibrarySync(id: UUID) {
        if librarySyncRun?.id == id { librarySyncRun?.cancel() }
    }

    public func synchronizeLibrary(_ request: MusicLibrarySyncRequest) async throws -> MusicLibrarySyncState {
        try check()
        try musicRequire(librarySyncRun == nil, .busy)
        try musicRequire((1...3).contains(request.selections.count) &&
            Set(request.selections.map(\.platform)).count == request.selections.count &&
            request.selections.allSatisfy { !$0.kinds.isEmpty })
        for selection in request.selections { try requirePlatform(selection.platform.rawValue) }
        guard let port = accounts as? any MusicNativeLibrarySyncProviding else { throw MusicServiceError.unavailable }
        let task = Task { try await self.performLibrarySync(request, port: port) }
        let cancel: @Sendable () -> Void = { task.cancel() }
        librarySyncRun = (request.id, cancel, { _ = await task.result }); access.register(request.id, cancel: cancel)
        defer { access.remove(request.id); if librarySyncRun?.id == request.id { librarySyncRun = nil } }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { cancel() }
    }

    func syncCurrent(_ round: MusicNativeSyncRound) async throws {
        try check(round.epoch)
        for session in round.sessions.values {
            try requirePlatform(session.platform.rawValue)
            try await round.port.checkLibrarySync(session); try check(round.epoch)
        }
        try musicRequire(try read().library.revision == round.revision, .revisionConflict)
    }

    func syncCheckpoint(_ round: inout MusicNativeSyncRound) throws {
        try check(round.epoch)
        let expected = round.revision
        let syncRecord = round.ledger.toRecord()
        var library = round.library
        library.libraryRevision += 1
        library.statusPhase = round.ledger.phase.rawValue
        library.statusAtMs = Int64((now().timeIntervalSince1970 * 1000).rounded())
        library.statusCode = nil
        library.statusIssues = round.ledger.issues.map {
            MusicLibraryIssueRecord(platform: $0.platform?.rawValue, collectionID: $0.collectionID, code: $0.code)
        }
        library.sync = syncRecord
        library.revision = expected + 1
        try transaction([.library]) { d in
            try musicRequire(d.library.revision == expected, .revisionConflict)
            d.library = library
        }
        round.library = library
        round.revision = expected + 1
    }

    func syncSnapshot(_ session: MusicLibrarySyncSession, selection: MusicLibrarySyncSelection,
                      round: MusicNativeSyncRound) async throws -> MusicLibrarySyncSnapshot {
        try await syncCurrent(round)
        let snapshot: MusicLibrarySyncSnapshot
        if round.relationshipsOnly { snapshot = try await round.port.readLibrarySubscriptions(session) }
        else { snapshot = try await round.port.readLibrarySync(session, kinds: selection.kinds) }
        try await syncCurrent(round)
        try musicRequire(snapshot.accountID == session.accountID, .targetChanged)
        let songs = try snapshot.collections.flatMap(\.members).map {
            try MusicModel.normalize(.init(platform: session.platform.rawValue, data: $0.track))
        }
        _ = try adoptSongs(songs, authoritative: true)
        return snapshot
    }

    private func requireSyncBindings(_ library: MusicLibraryState, selections: [MusicLibrarySyncSelection]) throws {
        var targets = Set<String>()
        let identities = Dictionary(grouping: library.collections, by: { Data($0.id.utf8) })
        if selections.contains(where: { $0.kinds.contains(.favorites) }) {
            let favorites = library.collections.filter { $0.id == "favorites" }
            try musicRequire(favorites.count == 1 && favorites[0].kind == "favorites", .favoritesProtected)
        }
        var referencedSongIDs = Set<String>()
        for row in library.collections {
            let selected = selections.filter { $0.kinds.map(\.rawValue).contains(row.kind) }
            guard !selected.isEmpty else { continue }
            referencedSongIDs.formUnion(row.order)
            referencedSongIDs.formUnion(row.intents.map(\.intentKey))
            for binding in row.bindings where selected.contains(where: { $0.platform.rawValue == binding.platform }) {
                for occurrence in binding.baseline {
                    if let songID = binding.songID(for: occurrence), !songID.isEmpty {
                        referencedSongIDs.insert(songID)
                    }
                }
                for entry in binding.map where !entry.songID.isEmpty {
                    referencedSongIDs.insert(entry.songID)
                }
            }
        }
        referencedSongIDs.remove("")
        let existingSongs = referencedSongIDs.isEmpty ? Set<String>() : try repositoryReader.containsSongs(ids: referencedSongIDs)
        for row in library.collections {
            let selected = selections.filter { $0.kinds.map(\.rawValue).contains(row.kind) }
            guard !selected.isEmpty else { continue }
            try musicRequire(!row.id.isEmpty && identities[Data(row.id.utf8)]?.count == 1 &&
                (0...musicMaxSafeInteger).contains(row.revision) &&
                musicIDs(row.order).count == row.order.count, .corruptStorage)
            try musicRequire(row.kind != "favorites" || (row.id == "favorites" && !row.deleted), .favoritesProtected)
            for songID in row.order {
                try musicRequire(existingSongs.contains(songID), .corruptStorage)
            }
            for intent in row.intents {
                try musicRequire(existingSongs.contains(intent.intentKey) &&
                    ["local", "remote"].contains(intent.origin ?? ""), .corruptStorage)
            }
            for binding in row.bindings where selected.contains(where: { $0.platform.rawValue == binding.platform }) {
                try musicRequire(!binding.remoteID.isEmpty && !binding.accountID.isEmpty &&
                    binding.accountID == library.accountID(for: binding.platform) &&
                    targets.insert(MusicNativeSyncLogic.bindingKey(binding)).inserted &&
                    binding.writable &&
                    musicIDs(binding.baseline).count == binding.baseline.count, .corruptStorage)
                for occurrence in binding.baseline {
                    try musicRequire(!occurrence.isEmpty &&
                        existingSongs.contains(binding.songID(for: occurrence) ?? ""), .corruptStorage)
                }
                for entry in binding.map {
                    try musicRequire(!entry.songID.isEmpty && existingSongs.contains(entry.songID), .corruptStorage)
                }
            }
        }
    }

    func performLibrarySync(_ request: MusicLibrarySyncRequest, port: any MusicNativeLibrarySyncProviding,
                            relationshipsOnly: Bool = false) async throws -> MusicLibrarySyncState {
        let initial = try read()
        try initial.requireRevision(.library)
        if !relationshipsOnly { try requireSyncBindings(initial.library, selections: request.selections) }
        var round = MusicNativeSyncRound(
            request: request,
            epoch: epoch,
            port: port,
            library: initial.library,
            ledger: try MusicNativeSyncLedger.load(initial.library),
            revision: initial.library.revision
        )
        round.relationshipsOnly = relationshipsOnly
        round.ledger.selections = request.selections
        round.ledger.phase = .pulling; round.ledger.issues = []
        try syncCheckpoint(&round)
        do {
            for selection in request.selections {
                try check(round.epoch)
                let session = try await port.beginLibrarySync(platform: selection.platform)
                round.sessions[selection.platform.rawValue] = session
                try musicRequire(session.platform == selection.platform, .targetChanged)
                try await syncCurrent(round)
                let old = round.library.accountID(for: selection.platform.rawValue)
                try musicRequire(old == nil || old == session.accountID, .targetChanged)
                if let scope = round.ledger.scopes[selection.platform.rawValue] { try musicRequire(scope == session.scope, .targetChanged) }
                round.snapshots[selection.platform.rawValue] = try await syncSnapshot(session, selection: selection, round: round)
            }
            for fence in round.ledger.fences {
                guard let session = round.sessions[fence.platform.rawValue],
                      let snapshot = round.snapshots[fence.platform.rawValue] else {
                    round.issue("unconfirmed", platform: fence.platform, collection: fence.localID)
                    continue
                }
                try musicRequire(fence.scope == session.scope && fence.accountID == session.accountID, .targetChanged)
                if relationshipsOnly, !fence.mutation.action.isSubscription {
                    round.issue("unconfirmed", platform: fence.platform, collection: fence.localID)
                    continue
                }
                guard request.selections.first(where: { $0.platform == fence.platform })?.kinds
                    .contains(fence.mutation.before?.kind ?? .playlist) == true else {
                    round.issue("unconfirmed", platform: fence.platform, collection: fence.localID); continue
                }
                if fence.confirmed(in: snapshot) {
                    try syncConfirm(fence, snapshot: snapshot, round: &round)
                    round.ledger.fences.removeAll { $0.mutation.id == fence.mutation.id }
                    try await syncCurrent(round); try syncCheckpoint(&round)
                } else { round.issue("unconfirmed", platform: fence.platform, collection: fence.localID) }
            }
            if round.ledger.issues.contains(where: { $0.code == "unconfirmed" }) {
                round.ledger.phase = .blocked; try syncCheckpoint(&round)
                DebugRuntime.log(.musicLibrary, sub: "sync", event: "blocked.unconfirmedFences", level: .warn, data: .object([
                    "fences": .int(round.ledger.fences.count)
                ]))
                for session in round.sessions.values { await port.endLibrarySync(session) }
                return try await librarySyncState()
            }
            for selection in request.selections {
                let platform = selection.platform.rawValue, session = round.sessions[platform]!
                round.library.setAccountID(session.accountID, for: platform)
                round.ledger.scopes[platform] = session.scope
            }
            if relationshipsOnly { try syncMergeSubscriptions(&round) }
            else {
                guard try syncProtectPull(&round) else {
                    round.ledger.phase = .blocked; try syncCheckpoint(&round)
                    for session in round.sessions.values { await port.endLibrarySync(session) }
                    return try await librarySyncState()
                }
                try syncMerge(&round)
                try await syncMapMissing(&round)
            }
            round.ledger.phase = .pushing
            try await syncCurrent(round); try syncCheckpoint(&round)
            if relationshipsOnly { try await syncPushSubscriptions(&round) }
            else { try await syncPush(&round); syncAcknowledge(&round) }
            round.ledger.phase = round.ledger.issues.isEmpty && round.ledger.fences.isEmpty ? .synced : .partial
            try await syncCurrent(round); try syncCheckpoint(&round)
            for session in round.sessions.values { await port.endLibrarySync(session) }
            return try await librarySyncState()
        } catch {
            var failure = MusicServiceError.sanitized(error)
            if !Task.isCancelled, (try? await syncCurrent(round)) != nil {
                round.issue(MusicNativeSyncLogic.code(failure)); round.ledger.phase = .blocked
                do { try syncCheckpoint(&round) } catch { failure = MusicServiceError.sanitized(error) }
            }
            for session in round.sessions.values { await port.endLibrarySync(session) }
            throw failure
        }
    }
}

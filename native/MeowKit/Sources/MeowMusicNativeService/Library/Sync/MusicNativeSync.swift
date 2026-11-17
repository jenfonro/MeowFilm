// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicLibrarySyncing {
    public func librarySyncState() async throws -> MusicLibrarySyncState {
        let wrapper = try read().library, ledger = try MusicNativeSyncLedger.load(wrapper)
        let pending = wrapper["library"]["collections"].array.reduce(0) { count, c in
            count + c["intents"].fields.filter { $0.1["pending"].bool }.count
        } + wrapper["library"]["networkReferences"].array.filter { reference in
            reference["pending"].bool && ledger.selections.contains {
                $0.platform.rawValue == reference["platform"].string && $0.playlists
            }
        }.count
        let phase: MusicLibrarySyncState.Phase = librarySyncRun == nil && [.pulling, .pushing].contains(ledger.phase) ? .blocked : ledger.phase
        return .init(selections: ledger.selections, phase: phase, pending: pending + ledger.fences.count, issues: ledger.issues)
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
        // Validate the whole explicit selection before any checkpoint or
        // account lease. Do not silently turn a mixed request into a partial sync.
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
        try musicRequire(try read().library["revision"].int == round.revision, .revisionConflict)
    }
    func syncCheckpoint(_ round: inout MusicNativeSyncRound) throws {
        try check(round.epoch)
        let expected = round.revision, ledger = try round.ledger.json()
        var library = round.library
        library["revision"] = .n(library["revision"].int + 1)
        library["status"] = .obj([("phase", .string(round.ledger.phase.rawValue)),
            ("at", .number(now().timeIntervalSince1970 * 1000)),
            ("issues", .array(round.ledger.issues.map { .obj([("code", .string($0.code))]) }))])
        try transaction(["library"]) { d in
            try musicRequire(d.library["revision"].int == expected, .revisionConflict)
            // Metadata adoption does not advance the user's library revision.
            // Preserve its current facts rather than writing this round's older
            // display projection over concurrent metadata observations.
            library["songs"] = d.library["library"]["songs"]
            d.library["library"] = library; d.library["sync"] = ledger; d.library["revision"] = .n(expected + 1)
        }
        round.library = library; round.revision = expected + 1
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
    private func requireSyncBindings(_ library: MJ, selections: [MusicLibrarySyncSelection]) throws {
        try musicRequire(library["collections"].isArray, .corruptDocument)
        var targets = Set<String>()
        let identities = Dictionary(grouping: library["collections"].array, by: { Data($0["id"].string.utf8) })
        if selections.contains(where: { $0.kinds.contains(.favorites) }) {
            let favorites = library["collections"].array.filter { $0["id"].string == "favorites" }
            try musicRequire(favorites.count == 1 && favorites[0]["kind"].string == "favorites", .favoritesProtected)
        }
        for row in library["collections"].array {
            let selected = selections.filter { $0.kinds.map(\.rawValue).contains(row["kind"].string) }
            guard !selected.isEmpty else { continue }
            try musicRequire(!row["id"].string.isEmpty && identities[Data(row["id"].string.utf8)]?.count == 1 &&
                row["revision"].isInteger && row["bindings"].isArray && row["order"].isArray &&
                row["intents"].isObject && row["manualOrder"].isBool &&
                musicIDs(row["order"].strings).count == row["order"].array.count, .corruptDocument)
            try musicRequire(row["kind"].string != "favorites" || row["id"].string == "favorites" && !row["deleted"].bool, .favoritesProtected)
            for songID in row["order"].array {
                try musicRequire(songID.isString && library["songs"][songID.string].isObject, .corruptDocument)
            }
            for (songID, intent) in row["intents"].fields {
                try musicRequire(library["songs"][songID].isObject && intent["present"].isBool &&
                    intent["pending"].isBool && ["local", "remote"].contains(intent["origin"].string), .corruptDocument)
            }
            for binding in row["bindings"].array where selected.contains(where: { $0.platform.rawValue == binding["platform"].string }) {
                try musicRequire(!binding["id"].string.isEmpty && !binding["accountId"].string.isEmpty &&
                    binding["accountId"] == library["accounts"][binding["platform"].string] &&
                    targets.insert(MusicNativeSyncLogic.bindingKey(binding)).inserted &&
                    binding["baseline"].isArray && binding["map"].isObject && binding["writable"].bool &&
                    musicIDs(binding["baseline"].strings).count == binding["baseline"].array.count, .corruptDocument)
                for occurrence in binding["baseline"].array {
                    try musicRequire(occurrence.isString && !occurrence.string.isEmpty &&
                        library["songs"][binding["map"][occurrence.string].string].isObject, .corruptDocument)
                }
                for (_, songID) in binding["map"].fields {
                    try musicRequire(songID.isString && library["songs"][songID.string].isObject, .corruptDocument)
                }
            }
        }
    }
    func performLibrarySync(_ request: MusicLibrarySyncRequest, port: any MusicNativeLibrarySyncProviding,
                            relationshipsOnly: Bool = false) async throws -> MusicLibrarySyncState {
        let initial = try read()
        try initial.requireRevision("library")
        if !relationshipsOnly { try requireSyncBindings(initial.library["library"], selections: request.selections) }
        var round = MusicNativeSyncRound(request: request, epoch: epoch, port: port,
            library: initial.library["library"], ledger: try MusicNativeSyncLedger.load(initial.library), revision: initial.library["revision"].int)
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
                let old = round.library["accounts"][selection.platform.rawValue]
                try musicRequire(old == .null || old.string == session.accountID, .targetChanged)
                if let scope = round.ledger.scopes[selection.platform.rawValue] { try musicRequire(scope == session.scope, .targetChanged) }
                round.snapshots[selection.platform.rawValue] = try await syncSnapshot(session, selection: selection, round: round)
            }
            // Recover each dispatched operation by reads only. A previous
            // process's late/unknown write can never be retried by this round.
            for fence in round.ledger.fences {
                guard let session = round.sessions[fence.platform.rawValue],
                      let snapshot = round.snapshots[fence.platform.rawValue] else { continue }
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
                // No merge can consume ambiguous deltas. This invocation is
                // recovery-only until every selected dispatched fence settles.
                round.ledger.phase = .blocked; try syncCheckpoint(&round)
                for session in round.sessions.values { await port.endLibrarySync(session) }
                return try await librarySyncState()
            }
            for selection in request.selections {
                let platform = selection.platform.rawValue, session = round.sessions[platform]!
                round.library["accounts"][platform] = .string(session.accountID); round.ledger.scopes[platform] = session.scope
            }
            if relationshipsOnly { try syncMergeSubscriptions(&round) }
            else {
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
            // A revoked document is never reopened merely to paint a failure.
            // Durable fences already exist before dispatch and remain intact.
            if !Task.isCancelled, (try? await syncCurrent(round)) != nil {
                round.issue(MusicNativeSyncLogic.code(failure)); round.ledger.phase = .blocked
                do { try syncCheckpoint(&round) } catch { failure = MusicServiceError.sanitized(error) }
            }
            for session in round.sessions.values { await port.endLibrarySync(session) }
            throw failure
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    func syncConfirm(_ fence: MusicNativeSyncFence, snapshot: MusicLibrarySyncSnapshot,
                     round: inout MusicNativeSyncRound) throws {
        if fence.mutation.action.isSubscription {
            try syncConfirmSubscription(fence, snapshot: snapshot, round: &round)
            return
        }
        guard let index = round.library.collections.firstIndex(where: { $0.id == fence.localID }) else {
            throw MusicServiceError.revisionConflict
        }
        var c = round.library.collections[index]
        guard let target = fence.resolvedTargetID(in: snapshot) else {
            throw MusicServiceError.upstreamFailed
        }
        let bindingIndex = c.bindings.firstIndex {
            $0.platform == fence.platform.rawValue && $0.accountID == fence.accountID && $0.remoteID == target
        }
        if fence.mutation.action == .delete {
            if let bindingIndex {
                let key = MusicNativeSyncLogic.bindingKey(c.bindings[bindingIndex])
                c.bindings.remove(at: bindingIndex)
                for i in c.intents.indices where !c.intents[i].acknowledged.isEmpty {
                    c.intents[i].acknowledged.removeAll { $0 == key }
                }
            }
        } else {
            guard let remote = snapshot.collections.first(where: { $0.id == target }) else { throw MusicServiceError.upstreamFailed }
            var b = bindingIndex.map { c.bindings[$0] } ?? MusicCollectionBindingRecord(
                platform: fence.platform.rawValue,
                accountID: fence.accountID,
                remoteID: target,
                writable: remote.writable,
                name: remote.name,
                baseline: [],
                map: []
            )
            var mapping = Dictionary(uniqueKeysWithValues: b.map.map { ($0.remoteEntryID, $0.songID) })
            for member in remote.members {
                let id = try registeredSongID(.init(platform: fence.platform.rawValue, data: member.track))
                mapping[member.occurrenceID] = id
            }
            b.map = mapping.keys.sorted().map { MusicCollectionBindingMapEntry(remoteEntryID: $0, songID: mapping[$0]!) }
            b.baseline = remote.members.map(\.occurrenceID)
            b.name = remote.name
            b.writable = remote.writable
            if let bindingIndex { c.bindings[bindingIndex] = b } else { c.bindings.append(b) }
            if fence.mutation.action == .create, c.revision == fence.localRevision {
                c.name = fence.acknowledgedName ?? remote.name
            }
            if c.revision == fence.localRevision {
                let members = Set(remote.members.compactMap { mapping[$0.occurrenceID] })
                let key = MusicNativeSyncLogic.bindingKey(b)
                for i in c.intents.indices where members.contains(c.intents[i].intentKey) == c.intents[i].present {
                    c.intents[i].acknowledged = MusicNativeSyncLogic.unique(c.intents[i].acknowledged + [key])
                }
            }
        }
        round.library.collections[index] = c
    }

    func syncDispatch(_ mutation: MusicLibrarySyncMutation, localID: String, localRevision: Int,
                      session: MusicLibrarySyncSession, selection: MusicLibrarySyncSelection,
                      round: inout MusicNativeSyncRound) async throws -> Bool {
        try await syncCurrent(round)
        let directoryIDs: [String]
        if mutation.action == .create, let preSnapshot = round.snapshots[session.platform.rawValue] {
            directoryIDs = MusicNativeSyncLogic.unique(preSnapshot.ownedCollectionIDs + preSnapshot.collections.map(\.id))
        } else {
            directoryIDs = []
        }
        var fence = MusicNativeSyncFence(platform: session.platform, accountID: session.accountID, scope: session.scope,
            localID: localID, localRevision: localRevision, mutation: .init(mutation), directoryIDs: directoryIDs)
        round.ledger.fences.append(fence)
        do {
            try syncCheckpoint(&round)
            try await syncCurrent(round)
        } catch {
            round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
            try await syncDiscardNotDispatched(fence, epoch: round.epoch)
            throw error
        }
        do {
            let result = try await round.writeSync(session, mutation: mutation)
            switch result {
            case .notDispatched(let reason), .rejected(let reason):
                round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
                do {
                    try await syncCurrent(round)
                    round.issue(MusicNativeSyncLogic.code(reason), platform: session.platform, collection: localID)
                    try syncCheckpoint(&round)
                } catch {
                    try await syncDiscardNotDispatched(fence, epoch: round.epoch)
                    throw error
                }
                return false
            case .acknowledged(let id, let name):
                try await syncCurrent(round)
                if mutation.action == .create {
                    if let id, !id.isEmpty {
                        guard round.snapshots[session.platform.rawValue]?.collections.contains(where: { $0.id == id }) == false else {
                            throw MusicServiceError.upstreamFailed
                        }
                        fence.acknowledgedID = id; fence.acknowledgedName = name
                        let index = round.ledger.fences.firstIndex { $0.mutation.id == mutation.id }!
                        round.ledger.fences[index] = fence
                        try syncCheckpoint(&round)
                    } else if let name, !name.isEmpty {
                        fence.acknowledgedName = name
                        let index = round.ledger.fences.firstIndex { $0.mutation.id == mutation.id }!
                        round.ledger.fences[index] = fence
                        try syncCheckpoint(&round)
                    }
                }
            }
        } catch {
            try await syncCurrent(round)
        }
        let snapshot = try await syncSnapshot(session, selection: selection, round: round)
        round.snapshots[session.platform.rawValue] = snapshot
        if fence.confirmed(in: snapshot) {
            try syncConfirm(fence, snapshot: snapshot, round: &round)
            round.ledger.fences.removeAll { $0.mutation.id == mutation.id }
            try await syncCurrent(round); try syncCheckpoint(&round)
            return true
        }
        round.issue("unconfirmed", platform: session.platform, collection: localID)
        try syncCheckpoint(&round); return false
    }

    private func syncDiscardNotDispatched(_ fence: MusicNativeSyncFence, epoch own: UUID) async throws {
        try await Task {
            try check(own)
            let ledger = try MusicNativeSyncLedger.load(read().library)
            guard ledger.fences.contains(where: { $0.mutation.id == fence.mutation.id }) else { return }
            try transaction([.library]) { state in
                var ledger = try MusicNativeSyncLedger.load(state.library)
                guard let index = ledger.fences.firstIndex(where: { $0.mutation.id == fence.mutation.id }) else { return }
                let stored = ledger.fences[index]
                try musicRequire(stored.platform == fence.platform && stored.accountID == fence.accountID &&
                    stored.scope == fence.scope && stored.localID == fence.localID &&
                    stored.localRevision == fence.localRevision && stored.mutation == fence.mutation, .revisionConflict)
                ledger.fences.remove(at: index)
                state.library.sync = ledger.toRecord()
                state.library.revision += 1
            }
        }.value
    }

    func syncPush(_ round: inout MusicNativeSyncRound) async throws {
        let ids = round.library.collections.map(\.id)
        for id in ids {
            for selection in round.request.selections {
                let platform = selection.platform.rawValue, session = round.sessions[platform]!
                if round.ledger.fences.contains(where: { $0.platform == selection.platform && $0.localID == id }) { continue }
                var dispatched = Set<String>()
                while true {
                    try await syncCurrent(round)
                    let c = round.library.collections.first { $0.id == id }!
                    guard let kind = MusicLibrarySyncKind(rawValue: c.kind), selection.kinds.contains(kind) else { break }
                    let bindings = c.bindings.filter { $0.platform == platform && $0.accountID == session.accountID }
                    if bindings.isEmpty {
                        if c.deleted || c.kind == "favorites" { break }
                        let mutation = MusicLibrarySyncMutation(action: .create, name: c.name,
                            directoryRevision: round.snapshots[platform]!.revision)
                        let mutationKey = MusicNativeSyncLogic.mutationKey(mutation)
                        guard dispatched.insert(mutationKey).inserted else {
                            round.issue("stalled", platform: selection.platform, collection: id)
                            break
                        }
                        if !(try await syncDispatch(mutation, localID: c.id, localRevision: c.revision,
                                                    session: session, selection: selection, round: &round)) { break }
                        continue
                    }
                    var candidate: MusicLibrarySyncMutation?
                    for binding in bindings {
                        guard let remote = round.snapshots[platform]?.collections.first(where: { $0.id == binding.remoteID }) else { continue }
                        candidate = try syncPlan(local: c, binding: binding, remote: remote, selection: selection, round: &round)
                        if candidate != nil { break }
                    }
                    guard let candidate else { break }
                    guard dispatched.insert(MusicNativeSyncLogic.mutationKey(candidate)).inserted else {
                        round.issue("stalled", platform: selection.platform, collection: id)
                        break
                    }
                    if !(try await syncDispatch(candidate, localID: c.id, localRevision: c.revision,
                                                session: session, selection: selection, round: &round)) { break }
                }
            }
        }
        try await syncPushSubscriptions(&round)
    }

    func syncPlan(local c: MusicCollectionRecord, binding: MusicCollectionBindingRecord,
                  remote: MusicLibrarySyncCollection,
                  selection: MusicLibrarySyncSelection,
                  round: inout MusicNativeSyncRound) throws -> MusicLibrarySyncMutation? {
        func mutation(_ action: MusicLibrarySyncMutation.Action, name: String? = nil,
                      members: [MusicLibrarySyncMember] = []) -> MusicLibrarySyncMutation {
            .init(action: action, collectionID: remote.id, name: name, before: remote, members: members)
        }
        if c.deleted { return remote.canDelete ? mutation(.delete) : nil }
        if c.name != remote.name, remote.canRename { return mutation(.rename, name: c.name) }
        let missingIDs = c.order.filter { id in
            !remote.members.contains { binding.songID(for: $0.occurrenceID) == id || binding.songID(for: $0.nativeID) == id }
        }
        let missingSongs: [String: MusicSongRecord] = missingIDs.isEmpty ? [:] : try repositorySongMap(missingIDs, read())
        var desired: [MusicLibrarySyncMember] = []
        for id in c.order {
            let existing = remote.members.filter { binding.songID(for: $0.occurrenceID) == id || binding.songID(for: $0.nativeID) == id }
            if !existing.isEmpty { desired += existing; continue }
            guard let song = missingSongs[id] else { throw MusicServiceError.notFound }
            let platform = selection.platform.rawValue
            let platformExcluded = try sourceExcluded(song, provider: "official:" + platform, platform: platform)
            let excluded = try recordingSourceExclusions(song)
            if !platformExcluded,
               let source = song.sources.first(where: {
                   $0.platform == platform && !excluded.contains($0.sourceKey)
               }),
               let locator = try MusicModel.sources(song).first(where: { $0.sourceKey == source.sourceKey }) {
                let data = try MusicWireCodec.buildMusicInfoData(source)
                desired.append(.init(nativeID: locator.nativeId, occurrenceID: locator.nativeId, track: data))
            } else { round.issue("unmapped", platform: selection.platform, collection: c.id) }
        }
        let projected = Set(desired.map(\.occurrenceID))
        for member in remote.members where !projected.contains(member.occurrenceID) {
            let id = binding.songID(for: member.occurrenceID) ?? ""
            if id.isEmpty || c.intent(for: id)?.present != false { desired.append(member) }
        }
        let present = Set(remote.members.map(\.nativeID)), wanted = Set(desired.map(\.nativeID))
        var seen = Set<String>()
        let removals = remote.members.filter { !wanted.contains($0.nativeID) && seen.insert($0.nativeID).inserted }
        if !removals.isEmpty {
            let limit = selection.platform == .netease && remote.kind == .favorites ? 1 : 100
            return mutation(.remove, members: Array(removals.prefix(limit)))
        }
        seen = []
        let additions = desired.filter { !present.contains($0.nativeID) && seen.insert($0.nativeID).inserted }
        if !additions.isEmpty {
            let limit = selection.platform == .netease && remote.kind == .favorites ? 1 : 100
            return mutation(.add, members: Array(additions.prefix(limit)))
        }
        try musicRequire(desired.map(\.occurrenceID).sorted() == remote.members.map(\.occurrenceID).sorted(), .revisionConflict)
        func logical(_ members: [MusicLibrarySyncMember]) -> [String] {
            MusicNativeSyncLogic.unique(members.map { binding.songID(for: $0.occurrenceID) ?? $0.nativeID })
        }
        if logical(desired) == logical(remote.members) { return nil }
        if desired.map(\.occurrenceID) != remote.members.map(\.occurrenceID) {
            if !remote.canOrder { round.issue("orderUnsupported", platform: selection.platform, collection: c.id); return nil }
            return mutation(.order, members: desired)
        }
        return nil
    }

    func syncAcknowledge(_ round: inout MusicNativeSyncRound) {
        for index in round.library.collections.indices {
            var c = round.library.collections[index]
            guard let kind = MusicLibrarySyncKind(rawValue: c.kind) else { continue }
            let targets = round.request.selections.filter { $0.kinds.contains(kind) }
            guard !targets.isEmpty else { continue }
            let bindings = c.bindings.filter { b in targets.contains { $0.platform.rawValue == b.platform } }
            let unresolved = round.ledger.fences.contains { $0.localID == c.id }
            for i in c.intents.indices {
                var intent = c.intents[i]
                var confirmed: [String] = []
                for b in bindings {
                    let ids = b.baseline.compactMap { b.songID(for: $0) }
                    if ids.contains(intent.intentKey) == intent.present {
                        confirmed.append(MusicNativeSyncLogic.bindingKey(b))
                    }
                }
                intent.acknowledged = MusicNativeSyncLogic.unique(intent.acknowledged + confirmed)
                let allTargets = targets.allSatisfy { selection in bindings.contains { $0.platform == selection.platform.rawValue } }
                if !unresolved, !targets.isEmpty,
                   (c.deleted && bindings.isEmpty) || (!bindings.isEmpty && allTargets && confirmed.count == bindings.count) {
                    intent.pending = false
                }
                c.intents[i] = intent
            }
            round.library.collections[index] = c
        }
    }
}
